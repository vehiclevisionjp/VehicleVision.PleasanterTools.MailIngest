using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

public sealed class PleasanterApiClientTests
{
    private static readonly PleasanterOptions Options = new()
    {
        BaseUrl = "https://example.invalid/",
        ApiKey = "secret-key",
    };

    private static PleasanterApiClient ClientFor(StubHandler handler) => new(new HttpClient(handler), Options);

    private static readonly IReadOnlyDictionary<string, object?> Record = new Dictionary<string, object?>
    {
        ["Title"] = "t",
    };

    [Theory]
    [InlineData(HttpStatusCode.OK, PleasanterErrorKind.None)]
    [InlineData(HttpStatusCode.Unauthorized, PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.TooManyRequests, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.MovedPermanently, PleasanterErrorKind.Permanent)]
    public void Classify_はステータスを扱い方へ分類する(HttpStatusCode status, PleasanterErrorKind expected)
        => Assert.Equal(expected, PleasanterApiClient.Classify(status));

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

        Assert.Equal(PleasanterErrorKind.InvalidResponse, response.ErrorKind);
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

        Assert.Equal(PleasanterErrorKind.InvalidResponse, response.ErrorKind);
    }

    [Fact]
    public async Task 更新は_Id_が無くても_StatusCode_が_200_なら成功()
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, """{"StatusCode":200}"""))
            .UpdateAsync(5, Record);

        Assert.True(response.IsSuccess);
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

    [Fact]
    public void Retry_After_は_HTTP_date_も相対時間にし_過去は_0_にする()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            TimeSpan.FromSeconds(30),
            PleasanterApiClient.ParseRetryAfter(new RetryConditionHeaderValue(now.AddSeconds(30)), now));
        Assert.Equal(
            TimeSpan.Zero,
            PleasanterApiClient.ParseRetryAfter(new RetryConditionHeaderValue(now.AddSeconds(-30)), now));
        Assert.Null(PleasanterApiClient.ParseRetryAfter(null, now));
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

        Assert.Equal(PleasanterErrorKind.InvalidResponse, response.ErrorKind);
        Assert.False(response.MayHaveBeenApplied);
    }

    [Fact]
    public async Task 一覧のオフセットが要求と違えば異常とする()
    {
        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, ListBody(0, 100, 500, 100)))
            .GetAsync(1, offset: 100);

        Assert.Equal(PleasanterErrorKind.InvalidResponse, response.ErrorKind);
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
    public async Task 応答が上限を超えたら異常とする()
    {
        var big = new string('a', PleasanterApiClient.MaxResponseBytes + 1);

        var response = await ClientFor(new StubHandler(HttpStatusCode.OK, big)).GetAsync(1, offset: 0);

        Assert.Equal(PleasanterErrorKind.InvalidResponse, response.ErrorKind);
        Assert.Null(response.Body);
    }

    [Fact]
    public async Task 別ホストへ転送された応答は成功にしない()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Id":1,"StatusCode":200}""")
        {
            FinalUri = new Uri("https://other.invalid/api/items/1/Create"),
        };

        var response = await ClientFor(handler).CreateAsync(1, Record);

        Assert.Equal(PleasanterErrorKind.InvalidResponse, response.ErrorKind);
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
    public void 標準のハンドラーはリダイレクトを追わない()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(PleasanterApiClient.CreateHttpMessageHandler());

        Assert.False(handler.AllowAutoRedirect);
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

        public Uri? FinalUri { get; init; }

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
                RequestMessage = FinalUri is null ? request : new HttpRequestMessage(HttpMethod.Post, FinalUri),
            };
            response.Headers.RetryAfter = RetryAfter;
            return response;
        }
    }
}
