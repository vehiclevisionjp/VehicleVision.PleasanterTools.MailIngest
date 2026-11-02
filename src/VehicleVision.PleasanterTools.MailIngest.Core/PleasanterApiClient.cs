using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.MailIngest.Core;

/// <summary>Pleasanter の応答をどう扱うか。</summary>
/// <remarks>分類を誤ると、直らないものを再送し続けるか、直るものを捨てる。</remarks>
public enum PleasanterErrorKind
{
    /// <summary>成功。</summary>
    None,

    /// <summary>一時的。再送してよい（作成・更新は <see cref="PleasanterResponse.MayHaveBeenApplied"/> も見る）。</summary>
    Transient,

    /// <summary>恒久的。再送しても通らない。</summary>
    Permanent,

    /// <summary>認証に失敗。キーの失効・設定ミスのため人が直す。</summary>
    Unauthorized,

    /// <summary>応答が返らなかった。できたかどうか分からないので、照合してから判断する。</summary>
    Unknown,

    /// <summary>
    /// HTTP は届いたが、応答が期待する形ではない（HTML・空 JSON・型不一致・上限超過・Id 欠落など）。
    /// 経路・設定の問題とみなす。作成・更新では反映済みかもしれないので照合する。
    /// </summary>
    InvalidResponse,
}

/// <summary>Pleasanter 呼び出しの結果。</summary>
/// <param name="ErrorKind">扱い方。</param>
/// <param name="StatusCode">HTTP のステータス。応答が無ければ <c>null</c>。</param>
/// <param name="Id">作成・更新されたレコードの ID。</param>
/// <param name="Message">Pleasanter が返した文言。**ログへ出さない。**</param>
/// <param name="Body">応答の中身。**ログへ出さない。**</param>
public sealed record PleasanterResponse(
    PleasanterErrorKind ErrorKind,
    int? StatusCode = null,
    long? Id = null,
    string? Message = null,
    JsonNode? Body = null)
{
    public bool IsSuccess => ErrorKind is PleasanterErrorKind.None;

    /// <summary>429 の <c>Retry-After</c>（秒または HTTP-date を相対時間にしたもの）。無ければ <c>null</c>。</summary>
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

/// <summary>Pleasanter の標準 API を叩く最小のクライアント。</summary>
/// <remarks>
/// <para>**API キーはリクエストボディに載せる。ヘッダ方式は無い**
/// （Pleasanter 本体の <c>Libraries/Requests/Context.cs</c>）。</para>
/// <para>**このクラスは再試行しない。** 失敗の扱いは呼び出し側が決める。</para>
/// <para>HTTP 200 だけでは成功にしない。応答 JSON の <c>StatusCode</c>、作成時の <c>Id</c>、
/// 一覧の構造を検査する。応答の大きさは <see cref="MaxResponseBytes"/> までで、超えたら異常とする。</para>
/// <para>**リダイレクトは追わせない。** 秘密を含む POST を別ホストへ転送しないため、
/// <see cref="CreateHttpMessageHandler"/> で作ったハンドラーの <see cref="HttpClient"/> を渡すこと。</para>
/// <para>ここに書いた応答の形は設計文書の推奨と参照ソースの確認に基づく。**実機の Pleasanter では未検証。**</para>
/// </remarks>
public sealed class PleasanterApiClient(
    HttpClient httpClient,
    IPleasanterOptionsProvider optionsProvider)
{
    /// <summary>応答の大きさの上限（2 MiB）。</summary>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public PleasanterApiClient(HttpClient httpClient, PleasanterOptions options)
        : this(httpClient, new StaticPleasanterOptionsProvider(options))
    {
    }

    /// <summary>リダイレクトを追わない <see cref="HttpClient"/> 用のハンドラーを作る。</summary>
    public static HttpMessageHandler CreateHttpMessageHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
    };

    /// <summary>接続先へ到達し、API キーが拒否されないことを確かめる。</summary>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var response = await PostAsync(
            "api/users/Get",
            new Dictionary<string, object?>(),
            CallKind.Other,
            cancellationToken).ConfigureAwait(false);
        return response.ErrorKind is not (
                PleasanterErrorKind.Unauthorized or PleasanterErrorKind.Unknown or PleasanterErrorKind.InvalidResponse)
            && response.StatusCode is not (int)HttpStatusCode.NotFound;
    }

    /// <summary>レコードを作る。成功には有効な <c>Id</c> が要る。</summary>
    public Task<PleasanterResponse> CreateAsync(
        long siteId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/Create", record, CallKind.Create, cancellationToken);

    /// <summary>レコードを更新する。</summary>
    public Task<PleasanterResponse> UpdateAsync(
        long referenceId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{referenceId}/Update", record, CallKind.Update, cancellationToken);

    /// <summary>
    /// サイトのレコードを取り出す。絞り込みは <c>View</c>、ページ送りはトップレベルの
    /// <c>Offset</c> / <c>PageSize</c> に渡す。
    /// </summary>
    public Task<PleasanterResponse> GetAsync(
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
    public Task<PleasanterResponse> FindByExternalKeyAsync(
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
    public Task<PleasanterResponse> GetSiteAsync(
        long siteId,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/GetSite", new Dictionary<string, object?>(), CallKind.Other, cancellationToken);

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
    internal static TimeSpan? ParseRetryAfter(
        System.Net.Http.Headers.RetryConditionHeaderValue? header,
        DateTimeOffset now)
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

    private async Task<PleasanterResponse> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> body,
        CallKind kind,
        CancellationToken cancellationToken,
        int? requestedOffset = null)
    {
        var options = await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var isWrite = kind is CallKind.Create or CallKind.Update;
        var payload = new Dictionary<string, object?>(body, StringComparer.Ordinal)
        {
            ["ApiKey"] = options.ApiKey,
            ["ApiVersion"] = options.ApiVersion,
        };

        var baseUri = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, path))
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 送れたのか届いたのか分からない。照合してから判断する。例外の内容（URL を含み得る）は返さない
            return new PleasanterResponse(PleasanterErrorKind.Unknown) { MayHaveBeenApplied = isWrite };
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;
            var retryAfter = ParseRetryAfter(response.Headers.RetryAfter, DateTimeOffset.UtcNow);

            string? raw;
            try
            {
                raw = await ReadLimitedAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new PleasanterResponse(PleasanterErrorKind.Unknown, statusCode) { MayHaveBeenApplied = isWrite };
            }

            var kindByHttp = Classify(response.StatusCode);
            var applied = isWrite
                && (kindByHttp == PleasanterErrorKind.Transient || kindByHttp == PleasanterErrorKind.None);

            // 別ホストへのリダイレクトを許す HttpClient でも、最終的な宛先が違えば成功にしない
            if (response.RequestMessage?.RequestUri is { } finalUri
                && !string.Equals(finalUri.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
            {
                return new PleasanterResponse(PleasanterErrorKind.InvalidResponse, statusCode)
                {
                    MayHaveBeenApplied = isWrite,
                };
            }

            if (raw is null)
            {
                return new PleasanterResponse(PleasanterErrorKind.InvalidResponse, statusCode)
                {
                    MayHaveBeenApplied = isWrite,
                };
            }

            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                // Pleasanter は JSON を返す。返らないなら経路の問題とみなす
            }

            if (kindByHttp != PleasanterErrorKind.None)
            {
                return Build(node, kindByHttp, statusCode, retryAfter, applied, requestedOffset: null, CallKind.Other);
            }

            return Build(node, PleasanterErrorKind.None, statusCode, retryAfter, applied, requestedOffset, kind);
        }
    }

    private static PleasanterResponse Build(
        JsonNode? node,
        PleasanterErrorKind httpKind,
        int httpStatus,
        TimeSpan? retryAfter,
        bool mayHaveBeenApplied,
        int? requestedOffset,
        CallKind kind)
    {
        var invalid = new PleasanterResponse(PleasanterErrorKind.InvalidResponse, httpStatus)
        {
            MayHaveBeenApplied = kind is CallKind.Create or CallKind.Update,
        };

        if (node is not JsonObject obj)
        {
            // 失敗ステータスで本文が JSON でなくても、分類は HTTP に従う
            return httpKind == PleasanterErrorKind.None
                ? invalid
                : new PleasanterResponse(httpKind, httpStatus) { RetryAfter = retryAfter, MayHaveBeenApplied = mayHaveBeenApplied };
        }

        var errorKind = httpKind;
        var statusCode = httpStatus;
        if (httpKind == PleasanterErrorKind.None)
        {
            // HTTP 200 でも、JSON の StatusCode が無い・200 でないなら成功にしない
            if (!TryGetInt(obj, "StatusCode", out var bodyStatus))
            {
                return invalid;
            }

            if (bodyStatus != (int)HttpStatusCode.OK)
            {
                errorKind = Classify((HttpStatusCode)bodyStatus);
                statusCode = bodyStatus;
                mayHaveBeenApplied = kind is CallKind.Create or CallKind.Update
                    && errorKind == PleasanterErrorKind.Transient;
            }
        }

        long? id = null;
        if (obj.TryGetPropertyValue("Id", out var idNode) && idNode is not null)
        {
            if (idNode is JsonValue idValue && idValue.TryGetValue<long>(out var parsedId))
            {
                id = parsedId;
            }
            else if (errorKind == PleasanterErrorKind.None)
            {
                return invalid;
            }
        }

        if (errorKind == PleasanterErrorKind.None && kind == CallKind.Create && id is not > 0)
        {
            return invalid;
        }

        string? message = null;
        if (obj.TryGetPropertyValue("Message", out var messageNode) && messageNode is JsonValue messageValue
            && messageValue.TryGetValue<string>(out var text))
        {
            message = text;
        }

        var result = new PleasanterResponse(errorKind, statusCode, id, message, obj)
        {
            RetryAfter = retryAfter,
            MayHaveBeenApplied = mayHaveBeenApplied && errorKind != PleasanterErrorKind.None,
            LimitPerDate = TryGetInt(obj, "LimitPerDate", out var perDate) ? perDate : null,
            LimitRemaining = TryGetInt(obj, "LimitRemaining", out var remaining) ? remaining : null,
        };

        if (errorKind == PleasanterErrorKind.None && kind == CallKind.List)
        {
            return ValidateList(obj, result, requestedOffset) ?? invalid with { MayHaveBeenApplied = false };
        }

        return result;
    }

    private static PleasanterResponse? ValidateList(JsonObject obj, PleasanterResponse result, int? requestedOffset)
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

    /// <summary>応答を <see cref="MaxResponseBytes"/> までの文字列として読む。超えたら <c>null</c>。</summary>
    private static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
