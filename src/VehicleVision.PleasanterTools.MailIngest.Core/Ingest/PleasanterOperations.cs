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

/// <summary>
/// 取り込みが使う Pleasanter の操作（予約キーの検索・作成）。<see cref="PleasanterGateway"/> を包み、呼び出し回数と、
/// 接続先全体を止めるべき状況（認証拒否・429・日次枠の残量 0）を数える。
/// </summary>
/// <remarks>
/// <para>**作成・更新は再送しない。** 失敗の扱いは呼び出し側（<see cref="SourceIngestRunner"/>）が決める。</para>
/// <para>検索は全ページを辿る。外部キー列が完全一致するレコードだけを数え、1 ページ目の 0 件だけで未存在とはしない。
/// 取得の形（<c>Response.Data</c> の <c>ClassHash</c>・<c>ResultId</c>）は参照ソースで確認したが、**実機の Pleasanter では未検証**。</para>
/// </remarks>
public sealed class PleasanterOperations(PleasanterGateway gateway)
{
    /// <summary>検索の 1 ページの件数（設計の初期案）。</summary>
    public const int PageSize = 100;

    /// <summary>検索で辿るページの上限。超えたら <see cref="KeyLookupOutcome.Inconclusive"/>。</summary>
    public const int MaxPages = 20;

    public int SearchCalls { get; private set; }

    public int CreateCalls { get; private set; }

    /// <summary>接続先全体への送信を止める理由。<c>null</c> なら止めていない。ソースをまたいで保つ。</summary>
    public string? HaltReason { get; private set; }

    /// <summary>残りの日次枠（返ったときだけ）。</summary>
    public int? LimitRemaining { get; private set; }

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
            SearchCalls++;
            var result = await gateway.FindByExternalKeyAsync(siteId, keyColumn, key, offset, PageSize, cancellationToken)
                .ConfigureAwait(false);
            Observe(result);
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
        CreateCalls++;
        var result = await gateway.CreateAsync(siteId, record, cancellationToken).ConfigureAwait(false);
        Observe(result);
        return result;
    }

    private void Observe(PleasanterResult result)
    {
        if (result.LimitRemaining is { } remaining)
        {
            LimitRemaining = remaining;
            if (remaining <= 0)
            {
                HaltReason ??= "ApiQuotaExhausted";
            }
        }

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
