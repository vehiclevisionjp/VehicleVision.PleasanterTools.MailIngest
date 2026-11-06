using System.Net;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Ingest;

/// <summary>予約キーでの検索の結果。</summary>
public enum KeyLookupOutcome
{
    /// <summary>同じ外部キーのレコードが無い（全ページを調べた）。</summary>
    None,

    /// <summary>ちょうど 1 件（外部キー列も完全一致）。</summary>
    One,

    /// <summary>複数。競合として保留する。最初の 1 件で成功にしない。</summary>
    Multiple,

    /// <summary>ページ上限までに結論が出ない・応答が整合しない。未存在とは扱わない。</summary>
    Inconclusive,

    /// <summary>検索そのものが失敗した（通信・権限・恒久エラーなど）。<see cref="KeyLookup.Failure"/> に結果。</summary>
    Failed,
}

/// <param name="RecordId">1 件のときのレコード ID。</param>
/// <param name="ProjectionHash">1 件のときの、サーバ上の投射ハッシュ列の値（無ければ <c>null</c>）。</param>
public sealed record KeyLookup(
    KeyLookupOutcome Outcome,
    long? RecordId = null,
    string? ProjectionHash = null,
    PleasanterResult? Failure = null);

/// <summary>Pleasanter 操作の再試行・送信間隔の設定。既定は設計の初期案（実測による確定値ではない）。</summary>
public sealed class PleasanterOperationsOptions
{
    /// <summary>1 秒あたりの送信回数の上限（接続先設定の <c>requestsPerSecond</c>）。0 以下なら間隔を空けない。</summary>
    public decimal RequestsPerSecond { get; init; } = 1;

    /// <summary>検索（非更新）の合計試行回数。作成・更新は再送しない。</summary>
    public int MaxSearchAttempts { get; init; } = 3;

    /// <summary>検索の待機の上限（バックオフの上限・Retry-After を待つ最大）。これを超える Retry-After は待たず、次回へ回す。</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>結果不明の照合で、レコードが 0 件のときの検索の合計回数（設計の初期案は最大 3 回）。解消しなければ保留する。</summary>
    public int ReconcileSearchAttempts { get; init; } = 3;

    /// <summary>照合の検索の間隔（索引・レプリカの遅延を待つ）。</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>待機の実装。試験で差し替える。既定は <see cref="TimeProvider"/> による遅延。</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>
/// 取り込みが使う Pleasanter の操作（予約キーの検索・作成）。<see cref="PleasanterGateway"/> を包み、呼び出し回数と、
/// 接続先全体を止めるべき状況（認証拒否・429・日次枠の残量 0）を数える。
/// </summary>
/// <remarks>
/// <para>**作成・更新は再送しない。** 失敗の扱いは呼び出し側（<see cref="SourceIngestRunner"/>）が決める。</para>
/// <para>検索は全ページを辿る。外部キー列が完全一致するレコードだけを数え、1 ページ目の 0 件だけで未存在とはしない。
/// 取得の形（<c>Response.Data</c> の <c>ClassHash</c>・<c>ResultId</c>）は参照ソースで確認したが、**実機の Pleasanter では未検証**。</para>
/// </remarks>
public sealed class PleasanterOperations(
    PleasanterGateway gateway,
    PleasanterOperationsOptions? options = null,
    TimeProvider? time = null)
{
    private readonly PleasanterOperationsOptions _options = options ?? new PleasanterOperationsOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset? _lastCallAt;

    /// <summary>検索の 1 ページの件数（設計の初期案）。</summary>
    public const int PageSize = 100;

    /// <summary>検索で辿るページの上限。超えたら <see cref="KeyLookupOutcome.Inconclusive"/>。</summary>
    public const int MaxPages = 20;

    public PleasanterOperationsOptions Options => _options;

    public int SearchCalls { get; private set; }

    public int CreateCalls { get; private set; }

    /// <summary>接続先全体への送信を止める理由。<c>null</c> なら止めていない。ソースをまたいで保つ。</summary>
    public string? HaltReason { get; private set; }

    /// <summary>残りの日次枠（返ったときだけ）。</summary>
    public int? LimitRemaining { get; private set; }

    /// <summary>待たずに次回へ回した再試行（Retry-After が待機の上限を超えた・試行を使い切った）の、これより前には再開しない時刻。</summary>
    public DateTimeOffset? RetryNotBefore { get; private set; }

    public async Task<KeyLookup> FindByKeyAsync(
        long siteId,
        string keyColumn,
        string projectionColumn,
        string key,
        CancellationToken cancellationToken = default)
    {
        var matches = new List<(long Id, string? Projection)>();
        var offset = 0;
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await SearchPageAsync(siteId, keyColumn, key, offset, cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                return new KeyLookup(KeyLookupOutcome.Failed, Failure: result);
            }

            if (result.Data is not { } data || result.TotalCount is not { } total)
            {
                return new KeyLookup(KeyLookupOutcome.Inconclusive);
            }

            foreach (var node in data)
            {
                if (node is JsonObject record
                    && ReadColumn(record, keyColumn) == key
                    && ReadId(record) is { } id)
                {
                    matches.Add((id, ReadColumn(record, projectionColumn)));
                }
            }

            if (matches.Count > 1)
            {
                return new KeyLookup(KeyLookupOutcome.Multiple);
            }

            offset += data.Count;
            if (offset >= total)
            {
                return matches.Count == 1
                    ? new KeyLookup(KeyLookupOutcome.One, matches[0].Id, matches[0].Projection)
                    : new KeyLookup(KeyLookupOutcome.None);
            }

            if (data.Count == 0)
            {
                return new KeyLookup(KeyLookupOutcome.Inconclusive); // 件数が残っているのに、進まない
            }
        }

        return new KeyLookup(KeyLookupOutcome.Inconclusive);
    }

    /// <summary>レコードを作る。**再送しない。**</summary>
    public async Task<PleasanterResult> CreateAsync(
        long siteId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
    {
        await PaceAsync(cancellationToken).ConfigureAwait(false);
        CreateCalls++;
        var result = await gateway.CreateAsync(siteId, record, cancellationToken).ConfigureAwait(false);
        Observe(result);
        return result;
    }

    /// <summary>
    /// 検索の 1 ページ。検索は非更新なので、408 / 429 / 5xx / 通信失敗は上限つきで再試行する
    /// （Retry-After を優先、無ければ 1 秒開始・2 倍・ジッター付き・上限あり、合計 <see cref="PleasanterOperationsOptions.MaxSearchAttempts"/> 試行）。
    /// 待機の上限を超える Retry-After は、値を短縮して早く送らず、待たずに次回へ回す（<see cref="RetryNotBefore"/>）。
    /// </summary>
    private async Task<PleasanterResult> SearchPageAsync(
        long siteId,
        string keyColumn,
        string key,
        int offset,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await PaceAsync(cancellationToken).ConfigureAwait(false);
            SearchCalls++;
            var result = await gateway.FindByExternalKeyAsync(siteId, keyColumn, key, offset, PageSize, cancellationToken)
                .ConfigureAwait(false);
            ObserveQuota(result);
            if (result.IsSuccess)
            {
                return result;
            }

            if (result.ErrorKind == PleasanterErrorKind.Unauthorized)
            {
                HaltReason ??= "Unauthorized";
                return result;
            }

            var retryable = result.ErrorKind is PleasanterErrorKind.Transient or PleasanterErrorKind.Unknown;
            var wait = result.RetryAfter ?? Backoff(attempt);
            if (!retryable || attempt >= _options.MaxSearchAttempts || wait > _options.MaxWait)
            {
                if (retryable && result.RetryAfter is { } after)
                {
                    RetryNotBefore = _time.GetUtcNow() + after;
                }

                if (result.StatusCode == (int)HttpStatusCode.TooManyRequests)
                {
                    HaltReason ??= "RateLimited";
                }

                return result;
            }

            await DelayAsync(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var seconds = Math.Min(_options.MaxWait.TotalSeconds, Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
    }

    /// <summary>待機する（照合の検索の間隔など）。</summary>
    public Task DelayAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        _options.Delay is { } delay ? delay(wait, cancellationToken) : Task.Delay(wait, _time, cancellationToken);

    /// <summary>1 秒あたりの送信回数の上限を守るため、前回の呼び出しからの間隔を空ける。</summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (_options.RequestsPerSecond > 0 && _lastCallAt is { } last)
        {
            var due = last + TimeSpan.FromSeconds((double)(1m / _options.RequestsPerSecond));
            var now = _time.GetUtcNow();
            if (due > now)
            {
                await DelayAsync(due - now, cancellationToken).ConfigureAwait(false);
            }
        }

        _lastCallAt = _time.GetUtcNow();
    }

    private void ObserveQuota(PleasanterResult result)
    {
        if (result.LimitRemaining is { } remaining)
        {
            LimitRemaining = remaining;
            if (remaining <= 0)
            {
                HaltReason ??= "ApiQuotaExhausted";
            }
        }
    }

    private void Observe(PleasanterResult result)
    {
        ObserveQuota(result);

        if (result.ErrorKind == PleasanterErrorKind.Unauthorized)
        {
            HaltReason ??= "Unauthorized";
        }
        else if (result.StatusCode == (int)HttpStatusCode.TooManyRequests)
        {
            HaltReason ??= "RateLimited";
        }
    }

    /// <summary>列の値を読む。一覧の既定の形（<c>ClassHash</c> などの辞書）と、平らな形の両方を見る。</summary>
    private static string? ReadColumn(JsonObject record, string column)
    {
        var hashName = MappingPlan.ColumnInfo.Parse(column)?.HashName;
        if (hashName is not null
            && record[hashName] is JsonObject hash
            && hash[column] is JsonValue inner
            && inner.TryGetValue<string>(out var value))
        {
            return value;
        }

        return record[column] is JsonValue flat && flat.TryGetValue<string>(out var text) ? text : null;
    }

    private static long? ReadId(JsonObject record) =>
        record["ResultId"] is JsonValue id && id.TryGetValue<long>(out var value) && value > 0 ? value : null;
}
