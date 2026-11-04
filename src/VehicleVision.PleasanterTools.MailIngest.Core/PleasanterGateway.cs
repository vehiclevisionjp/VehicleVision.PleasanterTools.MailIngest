using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.MailIngest.Core;

/// <summary>
/// <see cref="PleasanterResponse"/> に、本ツールが必要とする検査の結果を足した結果。
/// </summary>
/// <remarks>
/// <see cref="PleasanterResponse.ErrorKind"/> は DotNetClient の分類（<c>None</c>・<c>Transient</c>・<c>Permanent</c>・<c>Unauthorized</c>・<c>Unknown</c>）。
/// 「HTTP は届いたが、応答が期待する形ではない」ものは <see cref="PleasanterErrorKind.Unknown"/> にし、
/// <see cref="IsInvalidResponse"/> を立てる（成功にせず、作成・更新では照合してから判断するため。経路・設定の問題として見分けたいときに使う）。
/// </remarks>
/// <param name="ErrorKind">扱い方。HTTP が 200 でも <paramref name="ApiStatusCode"/> が 200 以外なら、その値で分類し直した結果。</param>
/// <param name="StatusCode">HTTP のステータス。応答が無ければ <c>null</c>。</param>
/// <param name="Id">作成・更新されたレコードの ID。</param>
/// <param name="Message">Pleasanter が返した文言。**ログへ出さない。**</param>
/// <param name="Body">応答の中身。**ログへ出さない。**</param>
public sealed record PleasanterResult(
    PleasanterErrorKind ErrorKind,
    int? StatusCode = null,
    long? Id = null,
    string? Message = null,
    JsonNode? Body = null)
    : PleasanterResponse(ErrorKind, StatusCode, Id, Message, Body)
{
    /// <summary>応答 JSON の <c>StatusCode</c>。</summary>
    public int? ApiStatusCode { get; init; }

    /// <summary>
    /// HTTP は届いたが、応答が期待する形ではない（HTML・空 JSON・型不一致・Id 欠落・一覧の構造の不正など）。
    /// このとき <see cref="PleasanterResponse.ErrorKind"/> は <see cref="PleasanterErrorKind.Unknown"/>。経路・設定の問題とみなす。
    /// </summary>
    public bool IsInvalidResponse { get; init; }

    /// <summary>429 などの <c>Retry-After</c>（秒または HTTP-date を相対時間にしたもの）。無ければ <c>null</c>。</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// 作成・更新が、失敗応答や応答の喪失でもサーバで反映済みの可能性がある。
    /// <c>true</c> なら単純に再送せず、外部キーで照合してから判断する。
    /// </summary>
    public bool MayHaveBeenApplied { get; init; }

    /// <summary>一覧取得の結果（<c>Response.Data</c>）。一覧以外は <c>null</c>。</summary>
    public JsonArray? Data { get; init; }

    /// <summary>一覧のオフセット（サーバが返した値）。</summary>
    public int? Offset { get; init; }

    /// <summary>一覧のページサイズ（サーバが返した値。要求より小さいことがある）。</summary>
    public int? PageSize { get; init; }

    /// <summary>条件に合う全件数。</summary>
    public int? TotalCount { get; init; }

    /// <summary>1 日あたりの API 回数の上限。返らなければ <c>null</c>（無制限とは解釈しない）。</summary>
    public int? LimitPerDate { get; init; }

    /// <summary>1 日あたりの残り回数。返らなければ <c>null</c>（無制限とは解釈しない）。</summary>
    public int? LimitRemaining { get; init; }
}

/// <summary>
/// DotNetClient の <see cref="PleasanterApiClient"/> を、本ツールの呼び方で包む。
/// </summary>
/// <remarks>
/// <para>通信は <see cref="PleasanterHttp.CreateHandler"/>（リダイレクトを辿らない・Cookie 無し）の上で行う。
/// API キーは本文に載るため、<c>BaseUrl</c> の検査と秘密を出さない扱いは DotNetClient に任せる。</para>
/// <para>**このクラスは再試行しない。** 失敗の扱いは呼び出し側が決める。</para>
/// <para>HTTP 200 だけでは成功にしない。応答 JSON の <c>StatusCode</c>、作成時の <c>Id</c>、一覧の構造を検査する
/// （DotNetClient 0.2.0 は HTTP のステータスだけで分類し、本文が JSON でなくても成功にするため、ここで補う）。
/// 応答の大きさは <see cref="MaxResponseBytes"/> まで。超えると <see cref="HttpClient"/> が読み込みを打ち切り、
/// 応答が無かったもの（<see cref="PleasanterErrorKind.Unknown"/>）として扱う。</para>
/// <para>**DotNetClient 0.2.0 に無いものを、ここで補っている。** 該当の変更を含む版へ上げたら、不要になった分を消す。</para>
/// <list type="bullet">
/// <item><description>HTTP が 200 でも、本文の <c>StatusCode</c> が 200 以外なら失敗として分類する
/// （DotNetClient #95・PR #96 は develop にあるが 0.2.0 には無い。**DotNetClient #95 を含む版へ上げたら、この分類の補いは消せる**。
/// 本文の形の検査・Id・一覧・反映済みの可能性は、それとは別に本ツールが持つ）</description></item>
/// <item><description>Retry-After ヘッダを読む（DotNetClient は返さない）。待機指定が長いときに次回へ回すために使う</description></item>
/// </list>
/// <para>型付きのメソッド（<c>client.Items.*</c>）は使わず、<see cref="PleasanterApiClient.PostJsonAsync"/> だけを使う。
/// 型付きは最初の呼び出しの前に版の問い合わせを 1 回行い、本ツールが数える API 呼び出しの回数（日次枠・毎秒の上限）の外で通信が増えるため。</para>
/// <para>ここに書いた応答の形は設計文書の推奨と参照ソースの確認に基づく。**実機の Pleasanter では未検証。**</para>
/// </remarks>
public sealed class PleasanterGateway : IDisposable
{
    /// <summary>応答の大きさの上限（2 MiB）。</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly PleasanterApiClient _client;

    /// <summary>通信の経路（ハンドラー）を渡して作る。ハンドラーは、このクラスを破棄するときに一緒に破棄する。</summary>
    public PleasanterGateway(HttpMessageHandler handler, IPleasanterOptionsProvider optionsProvider)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(optionsProvider);
        _httpClient = new HttpClient(new RetryAfterRecorder(handler), disposeHandler: true)
        {
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        _client = new PleasanterApiClient(_httpClient, optionsProvider);
    }

    /// <summary>通信の経路（ハンドラー）と固定の接続設定で作る。</summary>
    public PleasanterGateway(HttpMessageHandler handler, PleasanterOptions options)
        : this(handler, new StaticPleasanterOptionsProvider(options))
    {
    }

    /// <summary>本番用。リダイレクトを辿らず、Cookie を持たない。</summary>
    public static PleasanterGateway Create(IPleasanterOptionsProvider optionsProvider)
        => new(PleasanterHttp.CreateHandler(), optionsProvider);

    /// <summary>本番用。リダイレクトを辿らず、Cookie を持たない。</summary>
    public static PleasanterGateway Create(PleasanterOptions options)
        => new(PleasanterHttp.CreateHandler(), options);

    /// <summary>接続先へ到達し、API キーが拒否されないことを確かめる。</summary>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var response = await PostAsync(
            "api/users/Get",
            new Dictionary<string, object?>(),
            CallKind.Other,
            cancellationToken).ConfigureAwait(false);
        return response.ErrorKind is not (PleasanterErrorKind.Unauthorized or PleasanterErrorKind.Unknown)
            && response.StatusCode is not (int)HttpStatusCode.NotFound;
    }

    /// <summary>レコードを作る。成功には有効な <c>Id</c> が要る。</summary>
    public Task<PleasanterResult> CreateAsync(
        long siteId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/Create", record, CallKind.Create, cancellationToken);

    /// <summary>レコードを更新する。</summary>
    public Task<PleasanterResult> UpdateAsync(
        long referenceId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{referenceId}/Update", record, CallKind.Update, cancellationToken);

    /// <summary>
    /// サイトのレコードを取り出す。絞り込みは <c>View</c>、ページ送りはトップレベルの
    /// <c>Offset</c> / <c>PageSize</c> に渡す。
    /// </summary>
    public Task<PleasanterResult> GetAsync(
        long siteId,
        IReadOnlyDictionary<string, object?>? view = null,
        int? offset = null,
        int? pageSize = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (view is not null)
        {
            body["View"] = view;
        }

        if (offset is not null)
        {
            body["Offset"] = offset;
        }

        if (pageSize is not null)
        {
            body["PageSize"] = pageSize;
        }

        return PostAsync($"api/items/{siteId}/Get", body, CallKind.List, cancellationToken, offset);
    }

    /// <summary>
    /// 予約キー列の完全一致で検索する。<b>フィルターの形（配列を JSON 文字列にする）と
    /// <c>ExactMatch</c> の動作は参照ソースで確認したが、実機では未確認。</b>
    /// 返ったレコードの外部キー列も、呼び出し側で完全一致を検査すること。
    /// </summary>
    public Task<PleasanterResult> FindByExternalKeyAsync(
        long siteId,
        string externalKeyColumn,
        string externalKey,
        int offset = 0,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(externalKeyColumn);
        ArgumentException.ThrowIfNullOrEmpty(externalKey);

        var view = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ColumnFilterHash"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [externalKeyColumn] = JsonSerializer.Serialize(new[] { externalKey }),
            },
            ["ColumnFilterSearchTypes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [externalKeyColumn] = "ExactMatch",
            },
        };
        return GetAsync(siteId, view, offset, pageSize, cancellationToken);
    }

    /// <summary>サイト設定を取り出す。</summary>
    public Task<PleasanterResult> GetSiteAsync(
        long siteId,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/GetSite", new Dictionary<string, object?>(), CallKind.Other, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _httpClient.Dispose();

    internal static PleasanterErrorKind Classify(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.OK => PleasanterErrorKind.None,
        HttpStatusCode.Unauthorized => PleasanterErrorKind.Unauthorized,
        HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            => PleasanterErrorKind.Permanent,
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            => PleasanterErrorKind.Transient,
        _ => (int)statusCode >= 500 ? PleasanterErrorKind.Transient : PleasanterErrorKind.Permanent,
    };

    /// <summary>Retry-After を相対時間へ。秒・HTTP-date のどちらも解釈する。負は 0 にする。</summary>
    internal static TimeSpan? ParseRetryAfter(RetryConditionHeaderValue? header, DateTimeOffset now)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private enum CallKind
    {
        Other,
        List,
        Create,
        Update,
    }

    private async Task<PleasanterResult> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> body,
        CallKind kind,
        CancellationToken cancellationToken,
        int? requestedOffset = null)
    {
        var isWrite = kind is CallKind.Create or CallKind.Update;
        var node = JsonSerializer.SerializeToNode(body, SerializerOptions) as JsonObject ?? [];
        var slot = new RetryAfterSlot();
        RetryAfterRecorder.Current.Value = slot;
        PleasanterResponse response;
        try
        {
            // 取り消しは伝わる。BaseUrl の不正（ArgumentException）・接続設定の解決の失敗も伝わる。
            // それ以外の通信の失敗は、DotNetClient が Unknown で返す
            response = await _client.PostJsonAsync(path, node, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RetryAfterRecorder.Current.Value = null;
        }

        if (response.StatusCode is not { } httpStatus)
        {
            // 送れたのか届いたのか分からない。照合してから判断する。例外の内容（URL を含み得る）は返さない
            return new PleasanterResult(PleasanterErrorKind.Unknown) { MayHaveBeenApplied = isWrite };
        }

        var retryAfter = slot.Value;
        var httpKind = response.ErrorKind;
        var applied = isWrite && httpKind is PleasanterErrorKind.Transient or PleasanterErrorKind.None;

        JsonObject? obj = response.Body as JsonObject;
        if (httpKind is not PleasanterErrorKind.None)
        {
            // 失敗ステータスは、本文が JSON でなくても HTTP の分類に従う
            return Build(response, obj, httpKind, httpStatus, apiStatus: null, retryAfter, applied);
        }

        // HTTP 200 でも、JSON の StatusCode が無い・200 でないなら成功にしない
        if (obj is null || !TryGetInt(obj, "StatusCode", out var bodyStatus))
        {
            return Invalid(httpStatus, isWrite, retryAfter);
        }

        var errorKind = PleasanterErrorKind.None;
        var statusCode = httpStatus;
        if (bodyStatus != (int)HttpStatusCode.OK)
        {
            errorKind = Classify((HttpStatusCode)bodyStatus);
            statusCode = bodyStatus;
            applied = isWrite && errorKind == PleasanterErrorKind.Transient;
        }

        // Id が在るのに数値でない（DotNetClient は読めず null にする）
        if (errorKind == PleasanterErrorKind.None
            && obj.TryGetPropertyValue("Id", out var idNode) && idNode is not null && response.Id is null)
        {
            return Invalid(httpStatus, isWrite, retryAfter);
        }

        if (errorKind == PleasanterErrorKind.None && kind == CallKind.Create && response.Id is not > 0)
        {
            return Invalid(httpStatus, isWrite, retryAfter);
        }

        var result = Build(response, obj, errorKind, statusCode, bodyStatus, retryAfter, applied);
        if (errorKind == PleasanterErrorKind.None && kind == CallKind.List)
        {
            return ValidateList(obj, result, requestedOffset) ?? Invalid(httpStatus, isWrite: false, retryAfter);
        }

        return result;
    }

    private static PleasanterResult Build(
        PleasanterResponse response,
        JsonObject? obj,
        PleasanterErrorKind errorKind,
        int statusCode,
        int? apiStatus,
        TimeSpan? retryAfter,
        bool mayHaveBeenApplied)
        => new(errorKind, statusCode, response.Id, response.Message, response.Body)
        {
            ApiStatusCode = apiStatus,
            RetryAfter = retryAfter,
            MayHaveBeenApplied = mayHaveBeenApplied && errorKind != PleasanterErrorKind.None,
            LimitPerDate = obj is not null && TryGetInt(obj, "LimitPerDate", out var perDate) ? perDate : null,
            LimitRemaining = obj is not null && TryGetInt(obj, "LimitRemaining", out var remaining) ? remaining : null,
        };

    private static PleasanterResult Invalid(int httpStatus, bool isWrite, TimeSpan? retryAfter)
        => new(PleasanterErrorKind.Unknown, httpStatus)
        {
            IsInvalidResponse = true,
            MayHaveBeenApplied = isWrite,
            RetryAfter = retryAfter,
        };

    private static PleasanterResult? ValidateList(JsonObject obj, PleasanterResult result, int? requestedOffset)
    {
        if (obj["Response"] is not JsonObject inner
            || inner["Data"] is not JsonArray data
            || !TryGetInt(inner, "Offset", out var offset)
            || !TryGetInt(inner, "PageSize", out var pageSize)
            || !TryGetInt(inner, "TotalCount", out var totalCount)
            || offset < 0
            || pageSize < 0
            || totalCount < 0
            || data.Count > pageSize
            || offset + data.Count > totalCount
            || offset != (requestedOffset ?? 0))
        {
            return null;
        }

        return result with { Data = data, Offset = offset, PageSize = pageSize, TotalCount = totalCount };
    }

    private static bool TryGetInt(JsonObject obj, string name, out int value)
    {
        value = 0;
        return obj.TryGetPropertyValue(name, out var node)
            && node is JsonValue jsonValue
            && jsonValue.TryGetValue<int>(out value);
    }

    private sealed class RetryAfterSlot
    {
        public TimeSpan? Value { get; set; }
    }

    /// <summary>応答の Retry-After を、呼び出しごとの入れ物へ書き留める（DotNetClient が返さないため）。</summary>
    private sealed class RetryAfterRecorder(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        // 呼び出しの非同期の流れにだけ付く。同時の呼び出しは互いの記録を見ない
        public static readonly AsyncLocal<RetryAfterSlot?> Current = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (Current.Value is { } slot)
            {
                slot.Value = ParseRetryAfter(response.Headers.RetryAfter, DateTimeOffset.UtcNow);
            }

            return response;
        }
    }
}
