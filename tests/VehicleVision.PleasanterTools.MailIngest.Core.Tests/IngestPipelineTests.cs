using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Identity;
using VehicleVision.PleasanterTools.MailIngest.Core.Ingest;
using VehicleVision.PleasanterTools.MailIngest.Core.State;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>取り込みパイプライン（取得 → 正規化 → 重複排除 → 対応づけ → 既存キー照合 → 作成 → DB 確定）の試験。</summary>
public sealed class IngestPipelineTests
{
    private static string Loc(int uid) => $"imap:1:{uid}:INBOX";

    private static async Task<IngestItem?> ItemAsync(IngestHarness h, int uid) =>
        await h.Store.FindByLocationAsync("src", Loc(uid));

    [Fact]
    public async Task 新規メールを作成し_予約列つきで_Completed_にする()
    {
        using var h = new IngestHarness();
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>", "件名A", "本文A"));

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.Created);
        Assert.Null(summary.StopReason);
        var item = (await ItemAsync(h, 1))!;
        Assert.Equal(IngestState.Completed, item.State);
        Assert.NotNull(item.RecordId);
        var record = Assert.Single(h.Server.Records);
        var expectedKey = ExternalKey.Create("dest", 100, "src", "a@example.invalid");
        Assert.Equal(expectedKey, (string?)record["ClassHash"]!["ClassZ"]);
        Assert.Equal(item.ProjectionHash, (string?)record["ClassHash"]!["ClassY"]);
        Assert.Equal("件名A", (string?)record["Title"]);
        Assert.True(h.Server.ApiKeySeen);
    }

    [Fact]
    public async Task 再実行では既処理を取り直さず_再作成しない()
    {
        using var h = new IngestHarness();
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>"));

        await h.RunAsync(session);
        var second = await h.RunAsync(session);

        Assert.Equal(0, second.Listed);
        Assert.Equal(1, h.Server.CreateCount);
        Assert.Equal(1, session.OpenCount);
    }

    [Fact]
    public async Task 同じメールが別の位置に現れても作成は一度だけ()
    {
        using var h = new IngestHarness();
        var raw = TestMail.Raw("<a@example.invalid>");
        var session = new FakeMailSession().Add(raw, uid: 1).Add(raw, uid: 2);

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.Created);
        Assert.Equal(1, summary.Duplicates);
        Assert.Single(h.Server.Records);
        Assert.Equal(IngestState.Completed, (await ItemAsync(h, 2))!.State);
        Assert.Equal((await ItemAsync(h, 1))!.Id, (await ItemAsync(h, 2))!.Id);
    }

    [Fact]
    public async Task 同じ_Message_ID_で別内容なら競合として保留し_既存を更新しない()
    {
        using var h = new IngestHarness();
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>", "先着", "本文"), uid: 1)
            .Add(TestMail.Raw("<a@example.invalid>", "後着", "別の本文"), uid: 2);

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.Created);
        Assert.Equal(1, summary.Held);
        Assert.Equal(1, summary.HeldByCode["ContentConflict"]);
        Assert.Single(h.Server.Records);
        Assert.Equal("先着", (string?)h.Server.Records[0]["Title"]);
        var held = (await ItemAsync(h, 2))!;
        Assert.Equal(IngestState.Held, held.State);
        Assert.Equal("ContentConflict", held.LastErrorCode);

        // 次回は保留済みとして取り直さない
        var third = await h.RunAsync(session);
        Assert.Equal(0, third.Listed);
    }

    [Fact]
    public async Task API_の恒久エラーはそのメールだけ保留にし_後続は続ける()
    {
        using var h = new IngestHarness();
        h.Server.ScriptCreate(CreateBehavior.BadRequest400, CreateBehavior.Ok);
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>"))
            .Add(TestMail.Raw("<b@example.invalid>"));

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.Held);
        Assert.Equal(1, summary.Created);
        Assert.Equal("Api400", (await ItemAsync(h, 1))!.LastErrorCode);
        Assert.Equal(IngestState.Held, (await ItemAsync(h, 1))!.State);
        Assert.Equal(IngestState.Completed, (await ItemAsync(h, 2))!.State);
        Assert.Null(summary.StopReason);
    }

    [Theory]
    [InlineData(CreateBehavior.ApplyThenLoseResponse, true)]
    [InlineData(CreateBehavior.LoseRequest, false)]
    [InlineData(CreateBehavior.ApplyThen503, true)]
    public async Task 結果不明の作成は_Unknown_にして再送せず_次回も再作成しない(CreateBehavior behavior, bool applied)
    {
        using var h = new IngestHarness();
        h.Server.ScriptCreate(behavior);
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>"));

        var first = await h.RunAsync(session);
        var second = await h.RunAsync(session);

        Assert.Equal(1, first.Unknown);
        Assert.Equal(0, first.Created);
        var item = (await ItemAsync(h, 1))!;
        Assert.Equal(IngestState.Unknown, item.State);
        Assert.NotNull(item.ProjectionHash); // 照合に使うので、送信前に残してある
        Assert.Equal(applied ? 1 : 0, h.Server.Records.Count);
        Assert.Equal(1, h.Server.CreateCount); // 再送していない
        Assert.Equal(0, second.Listed); // 照合は #82。ここでは取り直して作り直さない
    }

    [Fact]
    public async Task 認証の拒否は_RetryPending_にして残りを止め_次回に作成できる()
    {
        using var h = new IngestHarness();
        h.Server.ScriptCreate(CreateBehavior.Unauthorized401);
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>"))
            .Add(TestMail.Raw("<b@example.invalid>"));

        var first = await h.RunAsync(session);

        Assert.Equal("Unauthorized", first.StopReason);
        Assert.Equal(1, first.RetryLater);
        Assert.Equal(1, first.Deferred);
        Assert.Equal(IngestState.RetryPending, (await ItemAsync(h, 1))!.State);
        Assert.Equal(1, h.Server.CreateCount);

        // 次回（接続先の停止は実行ごと。新しい操作の状態で再開する）
        var retry = new SourceIngestRunner(h.Store, h.Normalizer, new PleasanterOperations(h.Gateway), h.Log, h.Time);
        var second = await retry.RunAsync(h.Plan, session);

        Assert.Equal(2, second.Created);
        Assert.Equal(IngestState.Completed, (await ItemAsync(h, 1))!.State);
    }

    [Fact]
    public async Task HTTP429_は接続先全体の送信を止める()
    {
        using var h = new IngestHarness();
        h.Server.ScriptCreate(CreateBehavior.TooManyRequests429);
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>"))
            .Add(TestMail.Raw("<b@example.invalid>"));

        var summary = await h.RunAsync(session);

        Assert.Equal("RateLimited", summary.StopReason);
        Assert.Equal("RateLimited", h.Api.HaltReason);
        Assert.Equal(1, summary.Unknown); // 429 の作成は未実行を保証できない
        Assert.Equal(1, summary.Deferred);
        Assert.Equal(1, h.Server.CreateCount);
    }

    [Fact]
    public async Task サーバに同じキーで内容も一致するレコードがあれば_再作成せず回復する()
    {
        using var h = new IngestHarness();
        var key = ExternalKey.Create("dest", 100, "src", "a@example.invalid");
        var mail = TestMail.Raw("<a@example.invalid>", "件名", "本文");
        // 一度作ってから DB だけ失う状況を、サーバに直接足して再現する（投射ハッシュは同じ内容から作る）
        using var probe = new IngestHarness();
        await probe.RunAsync(new FakeMailSession().Add(mail));
        var projection = ((string?)probe.Server.Records[0]["ClassHash"]!["ClassY"]!)!;
        var id = h.Server.AddRecord(key, projection);

        var summary = await h.RunAsync(new FakeMailSession().Add(mail));

        Assert.Equal(1, summary.Recovered);
        Assert.Equal(0, h.Server.CreateCount);
        var item = (await ItemAsync(h, 1))!;
        Assert.Equal(IngestState.Completed, item.State);
        Assert.Equal(id, item.RecordId);
    }

    [Fact]
    public async Task サーバに同じキーで内容が違うレコードがあれば_保留にして上書きも再作成もしない()
    {
        using var h = new IngestHarness();
        h.Server.AddRecord(ExternalKey.Create("dest", 100, "src", "a@example.invalid"), "other-hash");

        var summary = await h.RunAsync(new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>")));

        Assert.Equal(1, summary.HeldByCode["ServerRecordDiffers"]);
        Assert.Equal(0, h.Server.CreateCount);
        Assert.Equal(IngestState.Held, (await ItemAsync(h, 1))!.State);
    }

    [Fact]
    public async Task サーバに同じキーが複数あれば保留にする()
    {
        using var h = new IngestHarness();
        var key = ExternalKey.Create("dest", 100, "src", "a@example.invalid");
        h.Server.AddRecord(key, "h1");
        h.Server.AddRecord(key, "h2");

        var summary = await h.RunAsync(new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>")));

        Assert.Equal(1, summary.HeldByCode["DuplicateKeyOnServer"]);
        Assert.Equal(0, h.Server.CreateCount);
    }

    [Fact]
    public async Task 検索が失敗したら状態を進めず_作成もせず_次回へ残す()
    {
        using var h = new IngestHarness();
        h.Server.ScriptSearchFailure(System.Net.HttpStatusCode.BadGateway);
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>"));

        var first = await h.RunAsync(session);

        Assert.Equal(1, first.RetryLater);
        Assert.Equal(0, h.Server.CreateCount);
        Assert.Equal(IngestState.Pending, (await ItemAsync(h, 1))!.State);

        var second = await h.RunAsync(session);
        Assert.Equal(1, second.Created);
    }

    [Fact]
    public async Task RAW_が上限を超えたら保留にして残し_後続は続け_次回は取り直さない()
    {
        var small = LimitsConfig.Default with { RawBytes = 4000 };
        using var h = new IngestHarness();
        var plan = IngestHarness.CreatePlan(limits: small);
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<big@example.invalid>", body: new string('x', 6000)))
            .Add(TestMail.Raw("<a@example.invalid>"));

        var summary = await h.Runner.RunAsync(plan, session);

        Assert.Equal(1, summary.HeldByCode["RawTooLarge"]);
        Assert.Equal(1, summary.Created);
        var second = await h.Runner.RunAsync(plan, session);
        Assert.Equal(0, second.Listed);
    }

    [Fact]
    public async Task 不正な_MIME_は保留にして後続を続ける()
    {
        using var h = new IngestHarness();
        var session = new FakeMailSession().Add([]).Add(TestMail.Raw("<a@example.invalid>"));

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.HeldByCode["InvalidMime"]);
        Assert.Equal(1, summary.Created);
    }

    [Fact]
    public async Task 対応づけに失敗したメールは保留にし_API_を呼ばない()
    {
        using var h = new IngestHarness(mappingTarget: "NumA");
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>", subject: "数でない"));

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.HeldByCode["MappingFailed"]);
        Assert.Equal(0, h.Server.CreateCount);
        Assert.Equal(0, h.Server.SearchCount);
    }

    [Fact]
    public async Task Message_ID_が無いメールは_RAW_のハッシュで重複排除する()
    {
        using var h = new IngestHarness();
        var raw = TestMail.Raw(null, "ID なし");
        var session = new FakeMailSession().Add(raw, uid: 1).Add(raw, uid: 2);

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.Created);
        Assert.Equal(1, summary.Duplicates);
        Assert.Equal(ExternalKey.RawVersion, (await ItemAsync(h, 1))!.KeyVersion);
    }

    [Fact]
    public async Task 分類できない例外は失敗として数え_他のメールへ波及しない()
    {
        using var h = new IngestHarness();
        h.Normalizer = new ThrowingNormalizer(h.Normalizer, failOnSubject: "壊れる");
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>", "壊れる"))
            .Add(TestMail.Raw("<b@example.invalid>", "正常"));

        var summary = await h.RunAsync(session);

        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Created);
        Assert.DoesNotContain(h.Log.Entries, e => (e.Code ?? string.Empty).Contains("壊れる", StringComparison.Ordinal));
    }

    [Fact]
    public async Task API_成功後に_DB_確定へ失敗したら_受信後操作へ進まず止め_次回は結果不明として扱える()
    {
        using var h = new IngestHarness(store => new FailingCommitStore(store));
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>"))
            .Add(TestMail.Raw("<b@example.invalid>"));

        var summary = await h.RunAsync(session);

        Assert.Equal("StateCommitFailed", summary.StopReason);
        Assert.Equal(1, summary.Failed);
        Assert.Equal(1, summary.Deferred);
        Assert.Equal(1, h.Server.CreateCount);
        var item = (await ItemAsync(h, 1))!;
        Assert.Equal(IngestState.Sending, item.State); // 再起動後は MarkInterruptedAsUnknown で結果不明になる
        Assert.Equal(1, await h.RawStore.MarkInterruptedAsUnknownAsync("src"));
    }

    [Fact]
    public async Task 取得の接続失敗はソースを止めて_状態を変えない()
    {
        using var h = new IngestHarness();
        var session = new FakeMailSession { ListFailure = new VehicleVision.PleasanterTools.MailIngest.Core.Mail.MailConnectionException(VehicleVision.PleasanterTools.MailIngest.Core.Mail.MailFailureKind.Unavailable, "x") };

        var summary = await h.RunAsync(session);

        Assert.Equal("MailUnavailable", summary.StopReason);
        Assert.Equal(0, h.Server.CreateCount);
    }

    [Fact]
    public async Task 件数上限を超えた分は_HasMore_で次回へ残る()
    {
        using var h = new IngestHarness();
        var plan = IngestHarness.CreatePlan(limits: LimitsConfig.Default with { MessagesPerRun = 2 });
        var session = new FakeMailSession();
        for (var i = 0; i < 5; i++)
        {
            session.Add(TestMail.Raw($"<m{i}@example.invalid>"));
        }

        var first = await h.Runner.RunAsync(plan, session);
        var second = await h.Runner.RunAsync(plan, session);

        Assert.Equal(2, first.Created);
        Assert.True(first.HasMore);
        Assert.Equal(2, second.Created);
        Assert.True(second.HasMore);
    }

    [Fact]
    public void 受信後操作が_leave_以外の設定は計画を作れない()
    {
        var plan = IngestHarness.CreatePlan();
        var source = plan.Source with { Imap = plan.Source.Imap! with { AfterSuccess = AfterSuccessAction.MarkSeen } };

        var (created, errors) = SourcePlan.Create(plan.Destination, source, "sources[0]");

        Assert.Null(created);
        Assert.Contains(errors, e => e.Code == "AfterSuccessNotSupported");
    }

    [Fact]
    public void Pending_から保留へ遷移できる()
    {
        Assert.True(IngestStateTransitions.IsAllowed(IngestState.Pending, IngestState.Held));
    }

    private sealed class ThrowingNormalizer(IMailNormalizer inner, string failOnSubject) : IMailNormalizer
    {
        public async Task<NormalizeOutcome> NormalizeAsync(
            Stream raw,
            LimitsConfig limits,
            DateTimeOffset fetchedAt,
            DateTimeOffset? receivedAt,
            CancellationToken cancellationToken = default)
        {
            var outcome = await inner.NormalizeAsync(raw, limits, fetchedAt, receivedAt, cancellationToken);
            return outcome.Mail?.Subject == failOnSubject
                ? throw new InvalidOperationException("機密 " + failOnSubject)
                : outcome;
        }
    }

    /// <summary>AckPending への確定だけ失敗させる。</summary>
    private sealed class FailingCommitStore(IIngestStateStore inner) : IIngestStateStore
    {
        public Task<IngestItem?> FindAsync(string d, long s, string src, string k, CancellationToken c = default) => inner.FindAsync(d, s, src, k, c);

        public Task<EnsureResult> EnsureAsync(NewIngestItem item, CancellationToken c = default) => inner.EnsureAsync(item, c);

        public Task<bool> TransitionAsync(Guid id, IngestState from, IngestState to, TransitionData? data = null, CancellationToken c = default) =>
            to == IngestState.AckPending ? throw new IOException("db down") : inner.TransitionAsync(id, from, to, data, c);

        public Task<int> MarkInterruptedAsUnknownAsync(string sourceId, CancellationToken c = default) => inner.MarkInterruptedAsUnknownAsync(sourceId, c);

        public Task AddLocationAsync(Guid id, string sourceId, MailLocation l, CancellationToken c = default) => inner.AddLocationAsync(id, sourceId, l, c);

        public Task<IngestItem?> FindByLocationAsync(string sourceId, string key, CancellationToken c = default) => inner.FindByLocationAsync(sourceId, key, c);

        public Task<IReadOnlySet<string>> ListSettledLocationKeysAsync(string sourceId, CancellationToken c = default) => inner.ListSettledLocationKeysAsync(sourceId, c);

        public Task SetItemAckStateAsync(Guid id, AckState state, CancellationToken c = default) => inner.SetItemAckStateAsync(id, state, c);

        public Task<bool> SetAckStateAsync(string sourceId, string key, AckState state, CancellationToken c = default) => inner.SetAckStateAsync(sourceId, key, state, c);

        public Task<SourceRun?> TryBeginRunAsync(string sourceId, string ownerId, CancellationToken c = default) => inner.TryBeginRunAsync(sourceId, ownerId, c);

        public Task EndRunAsync(Guid runId, string status, CancellationToken c = default) => inner.EndRunAsync(runId, status, c);

        public Task<SourceRun?> FindActiveRunAsync(string sourceId, CancellationToken c = default) => inner.FindActiveRunAsync(sourceId, c);
    }
}
