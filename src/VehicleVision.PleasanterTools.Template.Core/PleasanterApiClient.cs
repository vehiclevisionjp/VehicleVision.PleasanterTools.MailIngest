using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.Template.Core;

/// <summary>Pleasanter の応答をどう扱うか。</summary>
/// <remarks>分類を誤ると、直らないものを再送し続けるか、直るものを捨てる。</remarks>
public enum PleasanterErrorKind
{
    /// <summary>成功。</summary>
    None,

    /// <summary>一時的。再送してよい。</summary>
    Transient,

    /// <summary>恒久的。再送しても通らない。</summary>
    Permanent,

    /// <summary>認証に失敗。キーの失効・設定ミスのため人が直す。</summary>
    Unauthorized,

    /// <summary>応答が返らなかった。できたかどうか分からないので、照合してから判断する。</summary>
    Unknown,
}

/// <summary>Pleasanter 呼び出しの結果。</summary>
/// <param name="ErrorKind">扱い方。</param>
/// <param name="StatusCode">HTTP のステータス。応答が無ければ <c>null</c>。</param>
/// <param name="Id">作成・更新されたレコードの ID。</param>
/// <param name="Message">Pleasanter が返した文言。</param>
/// <param name="Body">応答の中身。</param>
public sealed record PleasanterResponse(
    PleasanterErrorKind ErrorKind,
    int? StatusCode = null,
    long? Id = null,
    string? Message = null,
    JsonNode? Body = null)
{
    public bool IsSuccess => ErrorKind is PleasanterErrorKind.None;
}

/// <summary>Pleasanter の標準 API を叩く最小のクライアント。</summary>
/// <remarks>
/// <para>**API キーはリクエストボディに載せる。ヘッダ方式は無い**
/// （Pleasanter 本体の <c>Libraries/Requests/Context.cs</c>）。</para>
/// <para>**このクラスは再試行しない。** 失敗の扱いは呼び出し側が決める。</para>
/// </remarks>
public sealed class PleasanterApiClient(
    HttpClient httpClient,
    IPleasanterOptionsProvider optionsProvider)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public PleasanterApiClient(HttpClient httpClient, PleasanterOptions options)
        : this(httpClient, new StaticPleasanterOptionsProvider(options))
    {
    }

    /// <summary>接続先へ到達し、API キーが拒否されないことを確かめる。</summary>
    public async Task<bool> CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        var response = await PostAsync(
            "api/users/Get",
            new Dictionary<string, object?>(),
            cancellationToken).ConfigureAwait(false);
        return response.ErrorKind is not (PleasanterErrorKind.Unauthorized or PleasanterErrorKind.Unknown)
            && response.StatusCode is not (int)HttpStatusCode.NotFound;
    }

    /// <summary>レコードを作る。</summary>
    public Task<PleasanterResponse> CreateAsync(
        long siteId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/Create", record, cancellationToken);

    /// <summary>レコードを更新する。</summary>
    public Task<PleasanterResponse> UpdateAsync(
        long referenceId,
        IReadOnlyDictionary<string, object?> record,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{referenceId}/Update", record, cancellationToken);

    /// <summary>サイトのレコードを取り出す。絞り込みは <c>View</c> に渡す。</summary>
    public Task<PleasanterResponse> GetAsync(
        long siteId,
        IReadOnlyDictionary<string, object?>? view = null,
        CancellationToken cancellationToken = default)
        => PostAsync(
            $"api/items/{siteId}/Get",
            view is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["View"] = view },
            cancellationToken);

    /// <summary>サイト設定を取り出す。</summary>
    public Task<PleasanterResponse> GetSiteAsync(
        long siteId,
        CancellationToken cancellationToken = default)
        => PostAsync($"api/items/{siteId}/GetSite", new Dictionary<string, object?>(), cancellationToken);

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

    private async Task<PleasanterResponse> PostAsync(
        string path,
        IReadOnlyDictionary<string, object?> body,
        CancellationToken cancellationToken)
    {
        var options = await optionsProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        var payload = new Dictionary<string, object?>(body, StringComparer.Ordinal)
        {
            ["ApiKey"] = options.ApiKey,
            ["ApiVersion"] = options.ApiVersion,
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{options.BaseUrl.TrimEnd('/')}/{path}")
        {
            Content = JsonContent.Create(payload, options: SerializerOptions),
        };

        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // 送れたのか届いたのか分からない。照合してから判断する
            return new PleasanterResponse(PleasanterErrorKind.Unknown);
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            JsonNode? node = null;
            try
            {
                node = JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                // Pleasanter は JSON を返す。返らないなら経路の問題とみなす
            }

            return new PleasanterResponse(
                Classify(response.StatusCode),
                (int)response.StatusCode,
                node?["Id"]?.GetValue<long?>(),
                node?["Message"]?.GetValue<string?>(),
                node);
        }
    }
}
