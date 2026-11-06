using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MimeKit;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Ingest;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Core.State;
using VehicleVision.PleasanterTools.MailIngest.Data;
using VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>メール（合成データ）の RAW を作る。</summary>
internal static class TestMail
{
    public static byte[] Raw(string? messageId, string subject = "件名", string body = "本文", string? attachmentText = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("送信者", "sender@example.invalid"));
        message.To.Add(new MailboxAddress("受信者", "to@example.invalid"));
        message.Subject = subject;
        message.Date = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.FromHours(9));
        if (messageId is not null)
        {
            message.MessageId = messageId;
        }

        var text = new TextPart("plain") { Text = body };
        if (attachmentText is null)
        {
            message.Body = text;
        }
        else
        {
            var multipart = new Multipart("mixed") { text };
            multipart.Add(new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(attachmentText))),
                ContentTransferEncoding = ContentEncoding.Base64,
                FileName = "a.bin",
            });
            message.Body = multipart;
        }

        using var stream = new MemoryStream();
        message.WriteTo(stream);
        if (messageId is not null)
        {
            return stream.ToArray();
        }

        // MimeKit は書き出し時に Message-Id を補うので、欠落したメールは行を取り除いて作る
        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split("\r\n")
            .Where(l => !l.StartsWith("Message-Id:", StringComparison.OrdinalIgnoreCase));
        return Encoding.UTF8.GetBytes(string.Join("\r\n", lines));
    }
}

/// <summary>メモリ上の偽のメールサーバ（1 セッション）。サーバ上のメールは変えない。</summary>
internal sealed class FakeMailSession : IMailSession
{
    private readonly List<(MailLocation Location, byte[] Raw)> _messages = [];

    public bool IsPop3 { get; init; }

    public int OpenCount { get; private set; }

    public Exception? ListFailure { get; set; }

    public FakeMailSession Add(byte[] raw, int uid = -1)
    {
        var number = uid < 0 ? _messages.Count + 1 : uid;
        var location = IsPop3
            ? new MailLocation("pop3", $"pop3:u{number}", null, null, null, $"u{number}")
            : new MailLocation("imap", $"imap:1:{number}:INBOX", "INBOX", 1, number, null);
        _messages.Add((location, raw));
        return this;
    }

    public Task<MailListing> ListAsync(int maxCandidates, Func<string, bool> isKnown, CancellationToken cancellationToken = default)
    {
        if (ListFailure is not null)
        {
            throw ListFailure;
        }

        var found = _messages
            .Where(m => !isKnown(m.Location.LocationKey))
            .Take(maxCandidates + 1)
            .Select(m => (MailCandidate)new FakeCandidate(m.Location, m.Raw.Length))
            .ToList();
        var more = found.Count > maxCandidates;
        return Task.FromResult(new MailListing(more ? found.Take(maxCandidates).ToList() : found, more));
    }

    public Task<MailRaw> OpenRawAsync(MailCandidate candidate, long maxBytes, CancellationToken cancellationToken = default)
    {
        OpenCount++;
        var raw = _messages.Single(m => m.Location.LocationKey == candidate.Location.LocationKey).Raw;
        return Task.FromResult(raw.Length > maxBytes
            ? new MailRaw(null, TooLarge: true)
            : new MailRaw(new MemoryStream(raw, writable: false), TooLarge: false));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed record FakeCandidate(MailLocation Location, long? Size) : MailCandidate(Location, Size);

/// <summary>作成の挙動の台本。</summary>
public enum CreateBehavior
{
    Ok,

    /// <summary>サーバには作られたが、応答が届かない（通信失敗）。</summary>
    ApplyThenLoseResponse,

    /// <summary>サーバに届かず、通信失敗。</summary>
    LoseRequest,

    /// <summary>サーバには作られたが、503 を返す。</summary>
    ApplyThen503,

    BadRequest400,

    Unauthorized401,

    TooManyRequests429,
}

/// <summary>メモリ上の偽の Pleasanter（Create / Get の標準 API の形）。</summary>
internal sealed class FakePleasanterHandler : HttpMessageHandler
{
    private readonly Queue<CreateBehavior> _creates = new();
    private readonly Queue<HttpStatusCode> _searchFailures = new();

    public List<JsonObject> Records { get; } = [];

    public int CreateCount { get; private set; }

    public int SearchCount { get; private set; }

    public bool ApiKeySeen { get; private set; }

    public void ScriptCreate(params CreateBehavior[] behaviors)
    {
        foreach (var behavior in behaviors)
        {
            _creates.Enqueue(behavior);
        }
    }

    public void ScriptSearchFailure(params HttpStatusCode[] statuses)
    {
        foreach (var status in statuses)
        {
            _searchFailures.Enqueue(status);
        }
    }

    /// <summary>サーバ側にレコードを直接足す（外部の作成・DB 消失の再現）。</summary>
    public long AddRecord(string externalKey, string projectionHash)
    {
        var id = Records.Count + 1000L;
        Records.Add(new JsonObject
        {
            ["ResultId"] = id,
            ["ClassHash"] = new JsonObject { ["ClassZ"] = externalKey, ["ClassY"] = projectionHash },
        });
        return id;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        ApiKeySeen |= body["ApiKey"] is not null;

        if (path.EndsWith("/Create", StringComparison.Ordinal))
        {
            CreateCount++;
            var behavior = _creates.Count > 0 ? _creates.Dequeue() : CreateBehavior.Ok;
            switch (behavior)
            {
                case CreateBehavior.LoseRequest:
                    throw new HttpRequestException("lost");
                case CreateBehavior.BadRequest400:
                    return Json(HttpStatusCode.BadRequest, "{\"StatusCode\":400,\"Message\":\"bad\"}");
                case CreateBehavior.Unauthorized401:
                    return Json(HttpStatusCode.Unauthorized, "{\"StatusCode\":401}");
                case CreateBehavior.TooManyRequests429:
                    return Json(HttpStatusCode.TooManyRequests, "{\"StatusCode\":429}");
            }

            var id = Apply(body);
            if (behavior == CreateBehavior.ApplyThenLoseResponse)
            {
                throw new HttpRequestException("lost response");
            }

            return behavior == CreateBehavior.ApplyThen503
                ? Json(HttpStatusCode.ServiceUnavailable, "{}")
                : Json(HttpStatusCode.OK, "{\"Id\":" + id + ",\"StatusCode\":200,\"Message\":\"ok\"}");
        }

        if (path.EndsWith("/Get", StringComparison.Ordinal))
        {
            SearchCount++;
            if (_searchFailures.Count > 0)
            {
                return Json(_searchFailures.Dequeue(), "{}");
            }

            return Search(body);
        }

        return Json(HttpStatusCode.NotFound, "{}");
    }

    private long Apply(JsonObject body)
    {
        var id = Records.Count + 1000L;
        var record = new JsonObject { ["ResultId"] = id };
        foreach (var (name, value) in body)
        {
            if (name is not ("ApiKey" or "ApiVersion"))
            {
                record[name] = value?.DeepClone();
            }
        }

        Records.Add(record);
        return id;
    }

    private HttpResponseMessage Search(JsonObject body)
    {
        var filter = body["View"]?["ColumnFilterHash"]?.AsObject().First();
        var column = filter?.Key;
        var wanted = filter is { Value: { } v }
            ? JsonSerializer.Deserialize<string[]>(v.GetValue<string>())![0]
            : null;
        var hits = Records
            .Where(r => column is null || (string?)r["ClassHash"]?[column] == wanted)
            .ToList();
        var offset = (int?)body["Offset"] ?? 0;
        var size = Math.Min((int?)body["PageSize"] ?? 200, 200);
        var data = new JsonArray(hits.Skip(offset).Take(size).Select(r => r.DeepClone()).ToArray());
        var response = new JsonObject
        {
            ["StatusCode"] = 200,
            ["Response"] = new JsonObject
            {
                ["Offset"] = offset,
                ["PageSize"] = size,
                ["TotalCount"] = hits.Count,
                ["Data"] = data,
            },
        };
        return Json(HttpStatusCode.OK, response.ToJsonString());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };
}

/// <summary>SQLite の一時 DB・偽のサーバ・実際の MIME 正規化で組み立てた、取り込みの試験環境。</summary>
internal sealed class IngestHarness : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mailingest-ingest-" + Guid.NewGuid().ToString("N"));

    public IngestHarness(Func<IIngestStateStore, IIngestStateStore>? wrapStore = null, string mappingTarget = "Title")
    {
        Directory.CreateDirectory(_directory);
        var connectionString = $"Data Source={Path.Combine(_directory, "mailingest.db")}";
        MailIngestDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.Sqlite, connectionString);
        RawStore = new IngestStateStore(new DbConnectionFactory(DatabaseProvider.Sqlite, connectionString), Time);
        Store = wrapStore?.Invoke(RawStore) ?? RawStore;
        Server = new FakePleasanterHandler();
        Gateway = new PleasanterGateway(
            Server,
            new PleasanterOptions { BaseUrl = "https://example.invalid/", ApiKey = "secret-key", DetectServerVersion = false });
        Api = new PleasanterOperations(Gateway);
        Log = new RecordingLog();
        Normalizer = new MimeMailNormalizer();
        Plan = CreatePlan(mappingTarget);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 6, 3, 0, 0, TimeSpan.Zero));

    public IngestStateStore RawStore { get; }

    public IIngestStateStore Store { get; }

    public FakePleasanterHandler Server { get; }

    public PleasanterGateway Gateway { get; }

    public PleasanterOperations Api { get; }

    public RecordingLog Log { get; }

    public IMailNormalizer Normalizer { get; set; }

    public SourcePlan Plan { get; }

    public SourceIngestRunner Runner => new(Store, Normalizer, Api, Log, Time);

    public Task<SourceRunSummary> RunAsync(FakeMailSession session) => Runner.RunAsync(Plan, session);

    public static SourcePlan CreatePlan(string mappingTarget = "Title", LimitsConfig? limits = null, string sourceId = "src")
    {
        var destination = new PleasanterDestinationConfig(
            "dest", new Uri("https://example.invalid/"), 1.1m, "K", "Asia/Tokyo", 1);
        var source = new MailSourceConfig(
            sourceId,
            MailProtocol.Imap,
            "mail.example.invalid",
            993,
            TlsMode.Tls,
            new CertificateValidationConfig(CertificateValidationMode.Strict, null),
            new AuthenticationConfig(["PLAIN"], false, "U", "P"),
            new ImapConfig("INBOX", ImapSearchState.All, null, AfterSuccessAction.Leave, null),
            null,
            limits ?? LimitsConfig.Default,
            new BodyConfig("preferPlain", "toText"),
            new AttachmentsConfig("metadataOnly", false),
            new TargetConfig(
                100,
                "results",
                "ClassZ",
                "ClassY",
                "skip",
                1,
                [new MappingConfig("subject", mappingTarget, null, null), new MappingConfig("bodyText", "Body", null, null)],
                new Dictionary<string, object>()));
        var (plan, errors) = SourcePlan.Create(destination, source, "sources[0]");
        return plan ?? throw new InvalidOperationException(string.Join(",", errors.Select(e => e.Code)));
    }

    public void Dispose()
    {
        Gateway.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 一時ファイル
        }
    }
}

internal sealed class RecordingLog : IIngestLog
{
    public List<IngestLogEntry> Entries { get; } = [];

    public void Write(IngestLogEntry entry) => Entries.Add(entry);
}

/// <summary>時刻を進められる時計。</summary>
internal sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}
