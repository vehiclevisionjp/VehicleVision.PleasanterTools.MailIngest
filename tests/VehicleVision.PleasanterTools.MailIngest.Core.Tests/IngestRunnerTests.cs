using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Ingest;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Core.State;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>全ソースの実行・実行排他・終了コード・サマリーの試験。</summary>
public sealed class IngestRunnerTests
{
    private static readonly Dictionary<string, string> Env = new() { ["U"] = "user-secret", ["P"] = "password-secret" };

    private static string? Lookup(string name) => Env.GetValueOrDefault(name);

    private sealed class FakeFactory(Dictionary<string, Func<IMailSession>> sessions) : IMailSessionFactory
    {
        public List<string> Connected { get; } = [];

        public Task<IMailSession> ConnectAsync(MailSourceConfig source, MailCredentials credentials, CancellationToken cancellationToken = default)
        {
            Connected.Add(source.SourceId);
            Assert.Equal("user-secret", credentials.User);
            return Task.FromResult(sessions[source.SourceId]());
        }
    }

    private static IngestConfig ConfigOf(IngestHarness h, params MailSourceConfig[] sources) =>
        new(1, h.Plan.Destination, sources);

    private static MailSourceConfig Src(string id, LimitsConfig? limits = null) => IngestHarness.CreatePlan(sourceId: id, limits: limits).Source;

    private static async Task<IngestRunSummary> RunAsync(IngestHarness h, IMailSessionFactory factory, params MailSourceConfig[] sources)
    {
        var runner = new IngestRunner(h.Store, factory, h.Runner, h.Api, h.Log, h.Time);
        var check = IngestRunner.Check(ConfigOf(h, sources), Lookup);
        Assert.True(check.IsValid, string.Join(",", check.Errors.Select(e => e.Code)));
        return await runner.RunAsync(check, "owner", Lookup);
    }

    [Fact]
    public async Task 全ソースを順に実行し_成功なら終了コード_0_で_サマリーに内容を載せない()
    {
        using var h = new IngestHarness();
        var factory = new FakeFactory(new()
        {
            ["a"] = () => new FakeMailSession().Add(TestMail.Raw("<1@example.invalid>", "秘密の件名")),
            ["b"] = () => new FakeMailSession().Add(TestMail.Raw("<2@example.invalid>")),
        });

        var summary = await RunAsync(h, factory, Src("a"), Src("b"));

        Assert.Equal(IngestExitCode.Success, summary.ExitCode);
        Assert.Equal(["a", "b"], factory.Connected);
        Assert.All(summary.Sources, s => Assert.Equal(1, s.Created));
        Assert.Equal(2, h.Server.Records.Count);
        var text = summary.ToText();
        foreach (var output in new[] { text })
        {
            Assert.DoesNotContain("秘密の件名", output, StringComparison.Ordinal);
            Assert.DoesNotContain("password-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("user-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("example.invalid", output, StringComparison.Ordinal);
        }

        Assert.Contains("Attachments are not transferred", text, StringComparison.Ordinal);
        Assert.Equal(summary.ToLines().Count, text.Split(Environment.NewLine).Length);
        foreach (var source in new[] { "a", "b" })
        {
            Assert.Null(await h.Store.FindActiveRunAsync(source)); // 排他は解放されている
        }
    }

    [Fact]
    public async Task メールが無くても_終了コード_0_で_疎通済みとは言わない()
    {
        using var h = new IngestHarness();
        var factory = new FakeFactory(new() { ["a"] = () => new FakeMailSession() });

        var summary = await RunAsync(h, factory, Src("a"));

        Assert.Equal(IngestExitCode.Success, summary.ExitCode);
        Assert.Contains("No new messages were processed", summary.ToText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task 単一ソースの接続失敗は他のソースへ波及せず_終了コード_1()
    {
        using var h = new IngestHarness();
        var factory = new FakeFactory(new()
        {
            ["a"] = () => throw new MailConnectionException(MailFailureKind.Authentication, "x"),
            ["b"] = () => new FakeMailSession().Add(TestMail.Raw("<2@example.invalid>")),
        });

        var summary = await RunAsync(h, factory, Src("a"), Src("b"));

        Assert.Equal(IngestExitCode.Errors, summary.ExitCode);
        Assert.Equal("MailAuthentication", summary.Sources[0].StopReason);
        Assert.Equal(1, summary.Sources[1].Created);
    }

    [Fact]
    public async Task 接続できなくても_結果不明の照合はメールサーバへ触れずに行う()
    {
        using var h = new IngestHarness();
        h.Server.ScriptCreate(CreateBehavior.ApplyThenLoseResponse);
        await h.RunAsync(new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>")));
        var factory = new FakeFactory(new() { ["src"] = () => throw new MailConnectionException(MailFailureKind.Unavailable, "x") });

        var summary = await RunAsync(h, factory, Src("src"));

        Assert.Equal(1, summary.Sources[0].Reconciled);
        Assert.Equal("MailUnavailable", summary.Sources[0].StopReason);
        Assert.Equal(IngestExitCode.Errors, summary.ExitCode);
        Assert.Equal(1, h.Server.CreateCount);
    }

    [Fact]
    public async Task 同じソースの実行が終わっていなければ_引き継がず接続もしない()
    {
        using var h = new IngestHarness();
        Assert.NotNull(await h.Store.TryBeginRunAsync("a", "other-process"));
        var factory = new FakeFactory(new() { ["a"] = () => new FakeMailSession(), ["b"] = () => new FakeMailSession() });

        var summary = await RunAsync(h, factory, Src("a"), Src("b"));

        Assert.Equal("RunInProgress", summary.Sources[0].StopReason);
        Assert.Equal(["b"], factory.Connected);
        Assert.Equal(IngestExitCode.Errors, summary.ExitCode);
        Assert.NotNull(await h.Store.FindActiveRunAsync("a")); // 解除しない
    }

    [Fact]
    public async Task 予期しない例外でもソースを失敗として数え_排他を解放する()
    {
        using var h = new IngestHarness();
        var factory = new FakeFactory(new()
        {
            ["a"] = () => throw new InvalidOperationException("機密の文言"),
            ["b"] = () => new FakeMailSession().Add(TestMail.Raw("<2@example.invalid>")),
        });

        var summary = await RunAsync(h, factory, Src("a"), Src("b"));

        Assert.Equal("SourceFailed", summary.Sources[0].StopReason);
        Assert.Equal(1, summary.Sources[1].Created);
        Assert.Null(await h.Store.FindActiveRunAsync("a"));
        Assert.DoesNotContain(h.Log.Entries, e => (e.Code ?? string.Empty).Contains("機密", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 件数上限で残りがあるだけなら_終了コード_3()
    {
        using var h = new IngestHarness();
        var session = new FakeMailSession();
        for (var i = 0; i < 5; i++)
        {
            session.Add(TestMail.Raw($"<m{i}@example.invalid>"));
        }

        var factory = new FakeFactory(new() { ["a"] = () => session });
        var summary = await RunAsync(h, factory, Src("a", LimitsConfig.Default with { MessagesPerRun = 2 }));

        Assert.Equal(IngestExitCode.Remaining, summary.ExitCode);
        Assert.True(summary.Sources[0].HasMore);
    }

    [Fact]
    public async Task 今回新たに保留にしたら_終了コード_1_で_古い保留だけなら_0()
    {
        using var h = new IngestHarness();
        h.Server.ScriptCreate(CreateBehavior.BadRequest400);
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>"));
        var factory = new FakeFactory(new() { ["src"] = () => session });

        var first = await RunAsync(h, factory, Src("src"));
        var second = await RunAsync(h, factory, Src("src"));

        Assert.Equal(IngestExitCode.Errors, first.ExitCode);
        Assert.Equal(1, second.Sources[0].Outstanding(IngestState.Held));
        Assert.Equal(IngestExitCode.Success, second.ExitCode);
    }

    [Fact]
    public async Task 結果不明が残っている間は_終了コード_1()
    {
        using var h = new IngestHarness();
        h.Server.ScriptCreate(CreateBehavior.LoseRequest);
        h.Server.ScriptSearchFailure(
            System.Net.HttpStatusCode.BadGateway, System.Net.HttpStatusCode.BadGateway, System.Net.HttpStatusCode.BadGateway);
        var session = new FakeMailSession().Add(TestMail.Raw("<a@example.invalid>"));
        var factory = new FakeFactory(new() { ["src"] = () => session });

        // 1 回目: 作成の結果が不明。2 回目: 照合の検索が失敗して Unknown のまま
        await RunAsync(h, factory, Src("src"));
        var second = await RunAsync(h, factory, Src("src"));

        Assert.Equal(1, second.Sources[0].Outstanding(IngestState.Unknown));
        Assert.Equal(IngestExitCode.Errors, second.ExitCode);
    }

    [Fact]
    public void 起動時の検査は_秘密の未解決と未対応の受信後操作を値を出さずに報告する()
    {
        using var h = new IngestHarness();
        var source = Src("a") with
        {
            Imap = new ImapConfig("INBOX", ImapSearchState.All, null, AfterSuccessAction.MarkSeen, null),
            Authentication = new AuthenticationConfig(["PLAIN"], false, "U", "MISSING_PASSWORD"),
        };

        var check = IngestRunner.Check(ConfigOf(h, source), Lookup);

        Assert.False(check.IsValid);
        Assert.Contains(check.Errors, e => e.Code == "AfterSuccessNotSupported");
        var secret = Assert.Single(check.Errors, e => e.Code == "SecretUnresolved");
        Assert.Contains("MISSING_PASSWORD", secret.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("password-secret", string.Join(" ", check.Errors.Select(e => e.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void 終了コードの優先順位は_エラー_残り_成功()
    {
        var ok = new SourceRunSummary("a");
        var more = new SourceRunSummary("b") { HasMore = true };
        var failed = new SourceRunSummary("c") { Failed = 1 };

        Assert.Equal(0, IngestExitCode.From([ok]));
        Assert.Equal(3, IngestExitCode.From([ok, more]));
        Assert.Equal(1, IngestExitCode.From([ok, more, failed]));
    }
}
