using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

public sealed class PleasanterGatewayTests
{
    private static readonly PleasanterOptions Options = new()
    {
        BaseUrl = "https://example.invalid/",
        ApiKey = "secret-key",
        DetectServerVersion = false,
    };

    private static PleasanterGateway ClientFor(StubHandler handler) => new(handler, Options);

    private static readonly IReadOnlyDictionary<string, object?> Record = new Dictionary<string, object?>
    {
        ["Title"] = "t",
    };

    [Fact]
    public async Task CreateAsync_は_ApiKey_と_ApiVersion_をボディへ載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Id":42,"StatusCode":200,"Message":"ok"}""");

        var response = await ClientFor(handler).CreateAsync(100, Record, CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal(42, response.Id);
        Assert.Equal("https://example.invalid/api/items/100/Create", handler.RequestUri);
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal("secret-key", (string?)body["ApiKey"]);
        Assert.Equal(1.1m, (decimal?)body["ApiVersion"]);
        Assert.Equal("t", (string?)body["Title"]);
    }

    [Fact]
    public async Task 通信に失敗したら_Unknown_を返し_作成は反映済みの可能性を示す()
    {
        var client = ClientFor(new StubHandler(throwOnSend: true));

        var read = await client.GetAsync(1, cancellationToken: CancellationToken.None);
        var write = await client.CreateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.Unknown, read.ErrorKind);
        Assert.False(read.MayHaveBeenApplied);
        Assert.Equal(PleasanterErrorKind.Unknown, write.ErrorKind);
        Assert.True(write.MayHaveBeenApplied);
    }

    [Fact]
    public async Task 例外の内容や_ApiKey_を結果に載せない()
    {
        var response = await ClientFor(new StubHandler(throwOnSend: true)).CreateAsync(1, Record);

        Assert.DoesNotContain("secret-key", response.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("stub failure", response.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("{}")]
    [InlineData("""{"Id":1}""")]
    [InlineData("""{"StatusCode":"200","Id":1}""")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task HTTP_200_でも成功の形でない応答は成功にしない(string body)
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, body)).CreateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
        Assert.True(response.IsInvalidResponse);
        Assert.True(response.MayHaveBeenApplied);
        Assert.False(response.IsSuccess);
    }

    [Theory]
    [InlineData("""{"StatusCode":200}""")]
    [InlineData("""{"StatusCode":200,"Id":0}""")]
    [InlineData("""{"StatusCode":200,"Id":"abc"}""")]
    public async Task 作成は有効な_Id_が無ければ成功にしない(string body)
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, body)).CreateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
        Assert.True(response.IsInvalidResponse);
    }

    [Fact]
    public async Task 更新は_Id_が無くても_StatusCode_が_200_なら成功()
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, """{"StatusCode":200}"""))
            .UpdateAsync(5, Record);

        Assert.True(response.IsSuccess);
    }

    [Theory]
    [InlineData(401, PleasanterErrorKind.Unauthorized)]
    [InlineData(400, PleasanterErrorKind.Permanent)]
    [InlineData(403, PleasanterErrorKind.Permanent)]
    [InlineData(404, PleasanterErrorKind.Permanent)]
    [InlineData(301, PleasanterErrorKind.Permanent)]
    [InlineData(408, PleasanterErrorKind.Transient)]
    [InlineData(429, PleasanterErrorKind.Transient)]
    [InlineData(500, PleasanterErrorKind.Transient)]
    [InlineData(503, PleasanterErrorKind.Transient)]
    public async Task HTTP_200_でも_JSON_の_StatusCode_が200以外なら_その値で分類し_本文の値を報告する(int bodyStatus, PleasanterErrorKind expected)
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, "{\"StatusCode\":" + bodyStatus + "}")).UpdateAsync(1, Record);

        Assert.Equal(expected, response.ErrorKind);
        Assert.Equal(bodyStatus, response.StatusCode);
        Assert.Equal(bodyStatus, response.ApiStatusCode);
        Assert.False(response.IsInvalidResponse);
        Assert.Equal(expected == PleasanterErrorKind.Transient, response.MayHaveBeenApplied);
    }

    [Fact]
    public async Task HTTP_が200以外なら_本文の_StatusCode_ではなく_HTTP_の状態で分類する()
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.InternalServerError, """{"StatusCode":404}""")).UpdateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.Transient, response.ErrorKind);
        Assert.Equal(500, response.StatusCode);
        Assert.Null(response.ApiStatusCode);
    }

    [Fact]
    public async Task HTTP_200_でも_JSON_の_StatusCode_が失敗なら失敗として分類する()
    {
        var unauthorized = await ClientFor(new StubHandler(HttpStatusCode.OK, """{"StatusCode":401}""")).CreateAsync(1, Record);
        var server = await ClientFor(new StubHandler(HttpStatusCode.OK, """{"StatusCode":500}""")).UpdateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.Unauthorized, unauthorized.ErrorKind);
        Assert.Equal(PleasanterErrorKind.Transient, server.ErrorKind);
        Assert.True(server.MayHaveBeenApplied);
        Assert.False(unauthorized.MayHaveBeenApplied);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public async Task 作成の失敗は_一時的なものだけ反映済みの可能性を示す(HttpStatusCode status, bool mayHaveBeenApplied)
    {
        var response = await ClientFor(new StubHandler(status, "{}")).CreateAsync(1, Record);

        Assert.False(response.IsSuccess);
        Assert.Equal(mayHaveBeenApplied, response.MayHaveBeenApplied);
    }

    [Fact]
    public async Task 検索の失敗は反映済みの可能性を示さない()
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.InternalServerError, "{}"))
            .GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.Equal(PleasanterErrorKind.Transient, response.ErrorKind);
        Assert.False(response.MayHaveBeenApplied);
    }

    [Fact]
    public async Task TooManyRequests_の_Retry_After_を秒で読む()
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}")
        {
            RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)),
        };

        var response = await ClientFor(handler).GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.Equal(PleasanterErrorKind.Transient, response.ErrorKind);
        Assert.Equal(TimeSpan.FromSeconds(7), response.RetryAfter);
    }

    [Theory]
    [InlineData("7", 7)]
    [InlineData("0", 0)]
    [InlineData("3600", 3600)] // 上限の丸めは DotNetClient では行わない（待つかどうかは呼び出し側が決める）
    public async Task Retry_After_の秒指定を相対時間で返す(string header, int seconds)
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}") { RetryAfterRaw = header };

        var response = await ClientFor(handler).GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(seconds), response.RetryAfter);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Retry_After_が不正なら_null_にする(string header)
    {
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}") { RetryAfterRaw = header };

        var response = await ClientFor(handler).GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.Null(response.RetryAfter);
    }

    [Fact]
    public async Task Retry_After_が無ければ_null_にする()
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.TooManyRequests, "{}"))
            .GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.Null(response.RetryAfter);
    }

    [Fact]
    public async Task Retry_After_の_HTTP_date_は相対時間にする()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(120);
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}") { RetryAfterRaw = date.ToString("R", System.Globalization.CultureInfo.InvariantCulture) };

        var response = await ClientFor(handler).GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.NotNull(response.RetryAfter);
        Assert.InRange(response.RetryAfter!.Value, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(121));
    }

    [Fact]
    public async Task Retry_After_の過去の_HTTP_date_は_0_にする()
    {
        var date = DateTimeOffset.UtcNow.AddSeconds(-120);
        var handler = new StubHandler(HttpStatusCode.TooManyRequests, "{}") { RetryAfterRaw = date.ToString("R", System.Globalization.CultureInfo.InvariantCulture) };

        var response = await ClientFor(handler).GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.Equal(TimeSpan.Zero, response.RetryAfter);
    }

    [Fact]
    public async Task Retry_After_は_429_以外_503_でも_本文の失敗でも読む()
    {
        var unavailable = await ClientFor(new StubHandler(HttpStatusCode.ServiceUnavailable, "{}") { RetryAfterRaw = "11" })
            .CreateAsync(1, Record);
        var bodyFailure = await ClientFor(new StubHandler(HttpStatusCode.OK, """{"StatusCode":429}""") { RetryAfterRaw = "12" })
            .UpdateAsync(1, Record);
        var invalid = await ClientFor(new StubHandler(HttpStatusCode.OK, "<html/>") { RetryAfterRaw = "13" })
            .UpdateAsync(1, Record);

        Assert.Equal(TimeSpan.FromSeconds(11), unavailable.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(12), bodyFailure.RetryAfter);
        Assert.Equal(TimeSpan.FromSeconds(13), invalid.RetryAfter);
        Assert.True(invalid.IsInvalidResponse);
    }

    [Fact]
    public async Task 検索はトップレベルの_Offset_と_PageSize_を送る()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ListBody(offset: 200, pageSize: 100, total: 250, count: 2));

        var response = await ClientFor(handler).GetAsync(
            7,
            new Dictionary<string, object?> { ["ColumnFilterHash"] = new Dictionary<string, object?>() },
            offset: 200,
            pageSize: 100);

        Assert.Equal("https://example.invalid/api/items/7/Get", handler.RequestUri);
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal(200, (int?)body["Offset"]);
        Assert.Equal(100, (int?)body["PageSize"]);
        Assert.NotNull(body["View"]);
        Assert.Null(body["View"]!["Offset"]);
        Assert.True(response.IsSuccess);
        Assert.Equal(2, response.Data!.Count);
        Assert.Equal(200, response.Offset);
        Assert.Equal(100, response.PageSize);
        Assert.Equal(250, response.TotalCount);
    }

    [Fact]
    public async Task 外部キーでの検索は完全一致のフィルターを組み立てる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, ListBody(0, 100, 0, 0));

        var response = await ClientFor(handler).FindByExternalKeyAsync(7, "ClassZ", "abc123");

        Assert.True(response.IsSuccess);
        Assert.Empty(response.Data!);
        var view = JsonNode.Parse(handler.RequestBody!)!["View"]!;
        Assert.Equal("""["abc123"]""", (string?)view["ColumnFilterHash"]!["ClassZ"]);
        Assert.Equal("ExactMatch", (string?)view["ColumnFilterSearchTypes"]!["ClassZ"]);
    }

    [Theory]
    [InlineData("""{"StatusCode":200}""")]
    [InlineData("""{"StatusCode":200,"Response":{"Data":[],"Offset":0,"PageSize":10}}""")]
    [InlineData("""{"StatusCode":200,"Response":{"Data":"x","Offset":0,"PageSize":10,"TotalCount":0}}""")]
    [InlineData("""{"StatusCode":200,"Response":{"Data":[{},{}],"Offset":0,"PageSize":1,"TotalCount":2}}""")]
    [InlineData("""{"StatusCode":200,"Response":{"Data":[{}],"Offset":0,"PageSize":10,"TotalCount":0}}""")]
    [InlineData("""{"StatusCode":200,"Response":{"Data":[],"Offset":10,"PageSize":10,"TotalCount":0}}""")]
    [InlineData("""{"StatusCode":200,"Response":{"Data":[],"Offset":-1,"PageSize":10,"TotalCount":0}}""")]
    public async Task 一覧の構造が不正なら成功にしない(string body)
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, body)).GetAsync(1, offset: 0);

        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
        Assert.True(response.IsInvalidResponse);
        Assert.False(response.MayHaveBeenApplied);
    }

    [Fact]
    public async Task 一覧のオフセットが要求と違えば異常とする()
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, ListBody(0, 100, 500, 100)))
            .GetAsync(1, offset: 100);

        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
        Assert.True(response.IsInvalidResponse);
    }

    [Fact]
    public async Task 回数制限の残量は返ったときだけ読み_無ければ_null()
    {
        var with = await ClientFor(new StubHandler(
            HttpStatusCode.OK,
            """{"Id":1,"StatusCode":200,"LimitPerDate":1000,"LimitRemaining":0}""")).CreateAsync(1, Record);
        var without = await ClientFor(new StubHandler(HttpStatusCode.OK, """{"Id":1,"StatusCode":200}""")).CreateAsync(1, Record);

        Assert.Equal(1000, with.LimitPerDate);
        Assert.Equal(0, with.LimitRemaining);
        Assert.Null(without.LimitPerDate);
        Assert.Null(without.LimitRemaining);
    }

    [Fact]
    public async Task 応答が上限を超えたら読まず_Unknown_とし_作成は反映済みの可能性を示す()
    {
        var big = new string('a', PleasanterGateway.MaxResponseBytes + 1);

        var read = await ClientFor(new StubHandler(HttpStatusCode.OK, big)).GetAsync(1, offset: 0);
        var write = await ClientFor(new StubHandler(HttpStatusCode.OK, big)).CreateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.Unknown, read.ErrorKind);
        Assert.Null(read.StatusCode);
        Assert.Null(read.Body);
        Assert.False(read.MayHaveBeenApplied);
        Assert.Equal(PleasanterErrorKind.Unknown, write.ErrorKind);
        Assert.True(write.MayHaveBeenApplied);
    }

    [Fact]
    public async Task リダイレクト応答は追わず恒久エラーにする()
    {
        var handler = new StubHandler(HttpStatusCode.TemporaryRedirect, "{}");

        var response = await ClientFor(handler).CreateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.Permanent, response.ErrorKind);
        Assert.False(response.MayHaveBeenApplied);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public void 標準のハンドラーはリダイレクトを追わず_Cookie_を持たない()
    {
        using var handler = PleasanterHttp.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task 本番用の組み方は_307_を別ホストへ再送しない()
    {
        using var other = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        other.Start();
        using var first = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        first.Start();
        var otherPort = ((IPEndPoint)other.LocalEndpoint).Port;
        var firstPort = ((IPEndPoint)first.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            using var tcp = await first.AcceptTcpClientAsync();
            var stream = tcp.GetStream();
            var buffer = new byte[16 * 1024];
            _ = await stream.ReadAsync(buffer);
            var reply = $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{otherPort}/x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
        });

        using var gateway = PleasanterGateway.Create(new PleasanterOptions
        {
            BaseUrl = $"http://127.0.0.1:{firstPort}",
            ApiKey = "secret-key",
            DetectServerVersion = false,
        });
        var response = await gateway.CreateAsync(1, Record);
        await server;

        Assert.Equal(PleasanterErrorKind.Permanent, response.ErrorKind);
        Assert.Equal(307, response.StatusCode);
        Assert.False(other.Pending());
    }

    [Theory]
    [InlineData("http://example.invalid")]
    [InlineData("https://user:pass@example.invalid")]
    [InlineData("https://example.invalid/?a=b")]
    [InlineData("https://example.invalid/#x")]
    [InlineData("ftp://example.invalid")]
    public async Task BaseUrl_が不正なら送らず_ArgumentException_にし_API_キーも_URL_も出さない(string baseUrl)
    {
        var handler = new StubHandler();
        var gateway = new PleasanterGateway(
            handler,
            new PleasanterOptions { BaseUrl = baseUrl, ApiKey = "secret-key", DetectServerVersion = false });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => gateway.CreateAsync(1, Record));

        Assert.Equal(0, handler.CallCount);
        Assert.DoesNotContain("secret-key", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("example.invalid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ループバックの_http_は使える()
    {
        var gateway = new PleasanterGateway(
            new StubHandler(HttpStatusCode.OK, """{"StatusCode":200}"""),
            new PleasanterOptions { BaseUrl = "http://localhost:8080", ApiKey = "k", DetectServerVersion = false });

        Assert.True(await gateway.CheckConnectionAsync());
    }

    [Fact]
    public async Task 呼び出しは版の問い合わせをせず_パスの大文字小文字も従来のまま()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Id":1,"StatusCode":200}""");

        await new PleasanterGateway(handler, new PleasanterOptions { BaseUrl = "https://example.invalid", ApiKey = "k" })
            .CreateAsync(9, Record);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal("https://example.invalid/api/items/9/Create", handler.RequestUri);
    }

    [Fact]
    public void 環境変数から接続設定を作り_足りなければ_InvalidOperationException()
    {
        var options = PleasanterEnvironment.FromEnvironment(name => name switch
        {
            "PLEASANTER_BASE_URL" => "https://example.invalid",
            "PLEASANTER_API_KEY" => "k",
            "PLEASANTER_SERVER_VERSION" => "not-a-version",
            _ => null,
        });

        Assert.Equal("https://example.invalid", options.BaseUrl);
        Assert.False(options.DetectServerVersion);
        Assert.Null(options.ServerVersion);
        Assert.Throws<InvalidOperationException>(() => PleasanterEnvironment.FromEnvironment(_ => null));
    }

    [Fact]
    public async Task キャンセルは再試行せず伝える()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ClientFor(new StubHandler()).CreateAsync(1, Record, cts.Token));
    }

    [Fact]
    public async Task 接続確認は不正な応答を接続済みとしない()
    {
        Assert.True(await ClientFor(new StubHandler(HttpStatusCode.OK, """{"StatusCode":200}""")).CheckConnectionAsync());
        Assert.False(await ClientFor(new StubHandler(HttpStatusCode.OK, "<html/>")).CheckConnectionAsync());
        Assert.False(await ClientFor(new StubHandler(HttpStatusCode.Unauthorized, "{}")).CheckConnectionAsync());
    }

    private static string ListBody(int offset, int pageSize, int total, int count)
    {
        var data = string.Join(",", Enumerable.Repeat("{\"ResultId\":1}", count));
        return "{\"StatusCode\":200,\"Response\":{\"Offset\":" + offset + ",\"PageSize\":" + pageSize
            + ",\"TotalCount\":" + total + ",\"Data\":[" + data + "]}}";
    }

    private sealed class StubHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        bool throwOnSend = false) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        public int CallCount { get; private set; }

        public RetryConditionHeaderValue? RetryAfter { get; init; }

        /// <summary>ヘッダの生の文字列（HttpClient の構文検査を通して読まれる）。</summary>
        public string? RetryAfterRaw { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (throwOnSend)
            {
                throw new HttpRequestException("stub failure");
            }

            RequestUri = request.RequestUri?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            response.Headers.RetryAfter = RetryAfter;
            if (RetryAfterRaw is not null)
            {
                response.Headers.TryAddWithoutValidation("Retry-After", RetryAfterRaw);
            }

            return response;
        }
    }
}
