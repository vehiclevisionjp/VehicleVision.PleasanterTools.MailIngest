using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Ingest;
using VehicleVision.PleasanterTools.MailIngest.Core.State;
using VehicleVision.PleasanterTools.MailIngest.Mail;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>
/// 環境変数があるときだけ動く、実際のメールサーバ（GreenMail の Docker）と偽の Pleasanter をつないだ取り込みの統合試験。
/// CI には外部のサーバも資格情報も与えない。環境変数は <see cref="GreenMailFetchTests"/> と同じ。
/// </summary>
[Collection("GreenMail")]
public sealed class IngestGreenMailTests
{
    private const string Prefix = "MAILINGEST_TEST_GREENMAIL_";

    private static string? Env(string name) => Environment.GetEnvironmentVariable(Prefix + name);

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private sealed class GreenMailFactAttribute : FactAttribute
    {
        public GreenMailFactAttribute()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Prefix + "HOST")))
            {
                Skip = "GreenMail が無い（MAILINGEST_TEST_GREENMAIL_* が未設定）。CI では実行しない。";
            }
        }
    }

    private static int Port(int index) => int.Parse(Env("PORTS")!.Split(',')[index], System.Globalization.CultureInfo.InvariantCulture);

    private static async Task SeedAsync(int count)
    {
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(Env("HOST")!, Port(2), SecureSocketOptions.None);
            await imap.AuthenticateAsync(Env("USER")!, Env("PASSWORD")!);
            await imap.Inbox.OpenAsync(FolderAccess.ReadWrite);
            var all = await imap.Inbox.SearchAsync(SearchQuery.All);
            if (all.Count > 0)
            {
                await imap.Inbox.AddFlagsAsync(all, MessageFlags.Deleted, silent: true);
                await imap.Inbox.ExpungeAsync();
            }

            await imap.DisconnectAsync(true);
        }

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(Env("HOST")!, Port(0), SecureSocketOptions.None);
        for (var i = 0; i < count; i++)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("送信者", "sender@example.invalid"));
            message.To.Add(new MailboxAddress("受信者", Env("USER")! + "@example.invalid"));
            message.Subject = $"統合-{i}";
            message.MessageId = $"it-{Guid.NewGuid():N}@example.invalid";
            message.Body = new TextPart("plain") { Text = $"本文 {i}\n二行目" };
            await smtp.SendAsync(message);
            await Task.Delay(30);
        }
    }

    private static MailSourceConfig Source(MailProtocol protocol, int port, string sourceId)
    {
        var template = IngestHarness.CreatePlan(sourceId: sourceId).Source;
        return template with
        {
            Protocol = protocol,
            Host = Env("HOST")!,
            Port = port,
            Tls = TlsMode.None,
            Authentication = new AuthenticationConfig(
                protocol == MailProtocol.Imap ? ["IMAP-LOGIN"] : ["USER/PASS"], true, "MAILINGEST_IT_USER", "MAILINGEST_IT_PASSWORD"),
            Imap = protocol == MailProtocol.Imap ? new ImapConfig("INBOX", ImapSearchState.All, null, AfterSuccessAction.Leave, null) : null,
            Pop3 = protocol == MailProtocol.Pop3 ? new Pop3Config(AfterSuccessAction.Leave) : null,
        };
    }

    [GreenMailFact]
    public async Task IMAP_から取り込み_再実行で重複せず_メールは未読のまま残る()
    {
        await Gate.WaitAsync();
        try
        {
            await SeedAsync(3);
            using var h = new IngestHarness();
            var source = Source(MailProtocol.Imap, Port(2), "it-imap");
            var env = new Dictionary<string, string?> { ["MAILINGEST_IT_USER"] = Env("USER"), ["MAILINGEST_IT_PASSWORD"] = Env("PASSWORD") };
            var runner = new IngestRunner(h.Store, new MailKitSessionFactory(), h.Runner, h.Api, h.Log, h.Time);
            var check = IngestRunner.Check(new IngestConfig(1, h.Plan.Destination, [source]), env.GetValueOrDefault);
            Assert.True(check.IsValid, string.Join(",", check.Errors.Select(e => e.Code)));

            var first = await runner.RunAsync(check, "it", env.GetValueOrDefault);
            var second = await runner.RunAsync(check, "it", env.GetValueOrDefault);

            Assert.Equal(IngestExitCode.Success, first.ExitCode);
            Assert.Equal(3, first.Sources[0].Created);
            Assert.Equal(0, second.Sources[0].Listed);
            Assert.Equal(3, h.Server.Records.Count);
            Assert.Equal(3, h.Server.CreateCount);

            using var imap = new ImapClient();
            await imap.ConnectAsync(Env("HOST")!, Port(2), SecureSocketOptions.None);
            await imap.AuthenticateAsync(Env("USER")!, Env("PASSWORD")!);
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
            Assert.Equal(3, imap.Inbox.Count);
            Assert.Equal(3, (await imap.Inbox.SearchAsync(SearchQuery.NotSeen)).Count); // leave: 既読にしていない
            await imap.DisconnectAsync(true);
        }
        finally
        {
            Gate.Release();
        }
    }

    [GreenMailFact]
    public async Task POP3_から取り込み_再実行で重複せず_メールはサーバに残る()
    {
        await Gate.WaitAsync();
        try
        {
            await SeedAsync(2);
            using var h = new IngestHarness();
            var source = Source(MailProtocol.Pop3, Port(1), "it-pop3");
            var env = new Dictionary<string, string?> { ["MAILINGEST_IT_USER"] = Env("USER"), ["MAILINGEST_IT_PASSWORD"] = Env("PASSWORD") };
            var runner = new IngestRunner(h.Store, new MailKitSessionFactory(), h.Runner, h.Api, h.Log, h.Time);
            var check = IngestRunner.Check(new IngestConfig(1, h.Plan.Destination, [source]), env.GetValueOrDefault);
            Assert.True(check.IsValid, string.Join(",", check.Errors.Select(e => e.Code)));

            var first = await runner.RunAsync(check, "it", env.GetValueOrDefault);
            var second = await runner.RunAsync(check, "it", env.GetValueOrDefault);

            Assert.Equal(2, first.Sources[0].Created);
            Assert.Equal(0, second.Sources[0].Listed);
            Assert.Equal(2, h.Server.Records.Count);
            Assert.Equal(2, (await h.Store.CountByStateAsync("it-pop3")).GetValueOrDefault(IngestState.Completed));

            using var imap = new ImapClient();
            await imap.ConnectAsync(Env("HOST")!, Port(2), SecureSocketOptions.None);
            await imap.AuthenticateAsync(Env("USER")!, Env("PASSWORD")!);
            await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
            Assert.Equal(2, imap.Inbox.Count);
            await imap.DisconnectAsync(true);
        }
        finally
        {
            Gate.Release();
        }
    }
}
