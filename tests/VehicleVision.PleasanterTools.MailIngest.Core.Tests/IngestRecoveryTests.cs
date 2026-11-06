using System.Net;
using VehicleVision.PleasanterTools.MailIngest.Core.Identity;
using VehicleVision.PleasanterTools.MailIngest.Core.Ingest;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Core.State;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>結果不明の照合・保留・再開、検索の再試行、送信間隔の試験。</summary>
public sealed class IngestRecoveryTests
{
    private static string Loc(int uid) => $"imap:1:{uid}:INBOX";

    private static string KeyOf(string id) => ExternalKey.Create("dest", 100, "src", id);

    private static async Task<IngestItem> ItemAsync(IngestHarness h, int uid) =>
        (await h.Store.FindByLocationAsync("src", Loc(uid)))!;

    /// <summary>1 通目の作成の結果を不明にして、Unknown の項目を作る。</summary>
    private static async Task<(FakeMailSession Session, IngestItem Item)> UnknownAsync(
        IngestHarness h,
        CreateBehavior behavior)
    {
        h.Server.ScriptCreate(behavior);
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>"));
        await h.RunAsync(session);
        var item = await ItemAsync(h, 1);
        Assert.Equal(IngestState.Unknown, item.State);
        return (session, item);
    }

    [Fact]
    public async Task 結果不明の作成が実はサーバに反映済みなら_次回に照合して確定し_再作成しない()
    {
        using var h = new IngestHarness();
        var (session, _) = await UnknownAsync(h, CreateBehavior.ApplyThenLoseResponse);

        var second = await h.RunAsync(session);

        Assert.Equal(1, second.Reconciled);
        var item = await ItemAsync(h, 1);
        Assert.Equal(IngestState.Completed, item.State);
        Assert.Equal(h.Server.Records[0]["ResultId"]!.GetValue<long>(), item.RecordId);
        Assert.Equal(1, h.Server.CreateCount);
        Assert.Single(h.Server.Records);
    }

    [Fact]
    public async Task 反映済みでも索引の遅延で最初は見えなければ_時間を空けて検索してから確定する()
    {
        using var h = new IngestHarness();
        var (session, _) = await UnknownAsync(h, CreateBehavior.ApplyThen503);
        h.Server.HiddenSearches = 1;

        var second = await h.RunAsync(session);

        Assert.Equal(1, second.Reconciled);
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(h.Delays));
        Assert.Equal(1, h.Server.CreateCount);
    }

    [Fact]
    public async Task 時間を空けても_0_件なら保留にし_即_Create_しない()
    {
        using var h = new IngestHarness();
        var (session, _) = await UnknownAsync(h, CreateBehavior.LoseRequest);

        var second = await h.RunAsync(session);

        Assert.Equal(1, second.HeldByCode["ReconcileNotFound"]);
        Assert.Equal(1, h.Server.CreateCount); // 再作成していない
        Assert.Equal(2, h.Delays.Count); // 検索 3 回の間
        Assert.Equal(IngestState.Held, (await ItemAsync(h, 1)).State);

        // 保留は次回以降も自動では動かさない
        var third = await h.RunAsync(session);
        Assert.Equal(0, third.Created);
        Assert.Equal(1, h.Server.CreateCount);
    }

    [Fact]
    public async Task 照合で複数見つかれば保留にし_最初の1件で成功にしない()
    {
        using var h = new IngestHarness();
        var (session, _) = await UnknownAsync(h, CreateBehavior.ApplyThenLoseResponse);
        h.Server.AddRecord(KeyOf("a@example.invalid"), "x");

        var second = await h.RunAsync(session);

        Assert.Equal(1, second.HeldByCode["ReconcileMultiple"]);
    }

    [Fact]
    public async Task 照合で内容が一致しなければ保留にする()
    {
        using var h = new IngestHarness();
        var (session, _) = await UnknownAsync(h, CreateBehavior.LoseRequest);
        h.Server.AddRecord(KeyOf("a@example.invalid"), "手動で編集された");

        var second = await h.RunAsync(session);

        Assert.Equal(1, second.HeldByCode["ReconcileMismatch"]);
        Assert.Equal(1, h.Server.CreateCount);
    }

    [Fact]
    public async Task 照合の検索が失敗したら_Unknown_のまま次回へ残し_次回に確定できる()
    {
        using var h = new IngestHarness();
        var (session, _) = await UnknownAsync(h, CreateBehavior.ApplyThenLoseResponse);
        h.Server.ScriptSearchFailure(HttpStatusCode.BadGateway, HttpStatusCode.BadGateway, HttpStatusCode.BadGateway);

        var second = await h.RunAsync(session);

        Assert.Equal(1, second.ReconcileDeferred);
        Assert.Equal(IngestState.Unknown, (await ItemAsync(h, 1)).State);
        Assert.Equal(2, h.Delays.Count); // 1 秒・2 秒（ジッター付き）のバックオフ
        Assert.InRange(h.Delays[0], TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1.3));
        Assert.InRange(h.Delays[1], TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.3));

        var third = await h.RunAsync(session);
        Assert.Equal(1, third.Reconciled);
        Assert.Equal(IngestState.Completed, (await ItemAsync(h, 1)).State);
    }

    [Fact]
    public async Task 再起動で残った_Sending_は_Unknown_にして照合し_再送しない()
    {
        using var h = new IngestHarness();
        var key = KeyOf("crash@example.invalid");
        var ensure = await h.RawStore.EnsureAsync(new NewIngestItem("dest", 100, "src", key, ExternalKey.Version, "hash", 1));
        await h.RawStore.AddLocationAsync(ensure.Item.Id, "src", new MailLocation("imap", Loc(9), "INBOX", 1, 9, null));
        await h.RawStore.TransitionAsync(
            ensure.Item.Id,
            IngestState.Pending,
            IngestState.Sending,
            new TransitionData { ProjectionHash = "proj" });
        var id = h.Server.AddRecord(key, "proj");

        var summary = await h.RunAsync(new FakeMailSession());

        Assert.Equal(1, summary.Interrupted);
        Assert.Equal(1, summary.Reconciled);
        var item = await ItemAsync(h, 9);
        Assert.Equal(IngestState.Completed, item.State);
        Assert.Equal(id, item.RecordId);
        Assert.Equal(0, h.Server.CreateCount);
    }

    [Fact]
    public async Task AckPending_は_API_を呼ばずに受信後操作を再開して完了する()
    {
        using var h = new IngestHarness();
        var ensure = await h.RawStore.EnsureAsync(new NewIngestItem("dest", 100, "src", KeyOf("done@example.invalid"), ExternalKey.Version, "hash", 1));
        await h.RawStore.AddLocationAsync(ensure.Item.Id, "src", new MailLocation("imap", Loc(5), "INBOX", 1, 5, null));
        await h.RawStore.TransitionAsync(ensure.Item.Id, IngestState.Pending, IngestState.Sending, new TransitionData { ProjectionHash = "p" });
        await h.RawStore.TransitionAsync(ensure.Item.Id, IngestState.Sending, IngestState.AckPending, new TransitionData { RecordId = 7 });

        var summary = await h.RunAsync(new FakeMailSession());

        Assert.Equal(1, summary.ResumedAck);
        Assert.Equal(IngestState.Completed, (await ItemAsync(h, 5)).State);
        Assert.Equal(0, h.Server.CreateCount);
        Assert.Equal(0, h.Server.SearchCount);
    }

    [Fact]
    public async Task 検索の_429_は_Retry_After_だけ待って再試行し_作成へ進む()
    {
        using var h = new IngestHarness();
        h.Server.ScriptSearchFailure(HttpStatusCode.TooManyRequests, retryAfterSeconds: 5);

        var summary = await h.RunAsync(new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>")));

        Assert.Equal(1, summary.Created);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(h.Delays));
        Assert.Equal(2, h.Server.SearchCount);
    }

    [Fact]
    public async Task Retry_After_が待機の上限を超えたら_待たず短縮もせず_次回へ回す()
    {
        using var h = new IngestHarness();
        h.Server.ScriptSearchFailure(HttpStatusCode.TooManyRequests, retryAfterSeconds: 120);
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>"))
            .Add(TestMail.Raw("<b@example.invalid>"));

        var summary = await h.RunAsync(session);

        Assert.Equal("RateLimited", summary.StopReason);
        Assert.Equal(h.Time.GetUtcNow() + TimeSpan.FromSeconds(120), summary.RetryNotBefore);
        Assert.Empty(h.Delays);
        Assert.Equal(0, h.Server.CreateCount);
        Assert.Equal(1, summary.RetryLater);
        Assert.Equal(1, summary.Deferred);
        Assert.Equal(IngestState.Pending, (await ItemAsync(h, 1)).State);
    }

    [Fact]
    public async Task 日次枠の残量が_0_になったら新規送信を止める()
    {
        using var h = new IngestHarness();
        h.Server.LimitRemaining = 0;
        var session = new FakeMailSession()
            .Add(TestMail.Raw("<a@example.invalid>"))
            .Add(TestMail.Raw("<b@example.invalid>"));

        var summary = await h.RunAsync(session);

        Assert.Equal("ApiQuotaExhausted", summary.StopReason);
        Assert.Equal(0, h.Server.CreateCount); // 検索の応答で残量 0 を知り、作成へ進まない
        Assert.Equal(1, summary.RetryLater);
        Assert.Equal(1, summary.Deferred);
    }

    [Fact]
    public async Task 毎秒の送信上限に合わせて呼び出しの間隔を空ける()
    {
        using var h = new IngestHarness();
        var api = new PleasanterOperations(
            h.Gateway,
            new PleasanterOperationsOptions
            {
                RequestsPerSecond = 2,
                Delay = (wait, _) =>
                {
                    h.Delays.Add(wait);
                    h.Time.Advance(wait);
                    return Task.CompletedTask;
                },
            },
            h.Time);

        await api.FindByKeyAsync(100, "ClassZ", "ClassY", "k1");
        await api.FindByKeyAsync(100, "ClassZ", "ClassY", "k2");

        Assert.Equal(TimeSpan.FromSeconds(0.5), Assert.Single(h.Delays));
    }
}
