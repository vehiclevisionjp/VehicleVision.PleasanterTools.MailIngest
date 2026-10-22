using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace VehicleVision.PleasanterTools.Template.Core.Tests;

public sealed class PleasanterApiClientTests
{
    private static readonly PleasanterOptions Options = new()
    {
        BaseUrl = "https://example.invalid/",
        ApiKey = "secret-key",
    };

    [Theory]
    [InlineData(HttpStatusCode.OK, PleasanterErrorKind.None)]
    [InlineData(HttpStatusCode.Unauthorized, PleasanterErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest, PleasanterErrorKind.Permanent)]
    [InlineData(HttpStatusCode.TooManyRequests, PleasanterErrorKind.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, PleasanterErrorKind.Transient)]
    public void Classify_はステータスを扱い方へ分類する(HttpStatusCode status, PleasanterErrorKind expected)
        => Assert.Equal(expected, PleasanterApiClient.Classify(status));

    [Fact]
    public async Task CreateAsync_は_ApiKey_と_ApiVersion_をボディへ載せる()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"Id":42,"Message":"ok"}""");
        var client = new PleasanterApiClient(new HttpClient(handler), Options);

        var response = await client.CreateAsync(
            100,
            new Dictionary<string, object?> { ["Title"] = "t" },
            CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal(42, response.Id);
        Assert.Equal("https://example.invalid/api/items/100/Create", handler.RequestUri);
        var body = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal("secret-key", (string?)body["ApiKey"]);
        Assert.Equal(1.1m, (decimal?)body["ApiVersion"]);
        Assert.Equal("t", (string?)body["Title"]);
    }

    [Fact]
    public async Task 通信に失敗したら_Unknown_を返す()
    {
        var client = new PleasanterApiClient(new HttpClient(new StubHandler(throwOnSend: true)), Options);

        var response = await client.GetAsync(1, cancellationToken: CancellationToken.None);

        Assert.Equal(PleasanterErrorKind.Unknown, response.ErrorKind);
    }

    private sealed class StubHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}",
        bool throwOnSend = false) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (throwOnSend)
            {
                throw new HttpRequestException("stub failure");
            }

            RequestUri = request.RequestUri?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
