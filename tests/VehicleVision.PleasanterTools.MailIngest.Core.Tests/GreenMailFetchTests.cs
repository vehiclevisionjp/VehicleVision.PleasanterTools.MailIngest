using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Net.Smtp;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Mail;
using VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>環境変数があるときだけ動く、テスト用メールサーバ（GreenMail）に対する統合試験。</summary>
/// <remarks>
/// <para>CI には外部のサーバも資格情報も与えない。ローカルでは GreenMail（Apache-2.0）の Docker を、自分専用の名前・ポートで動かす。
/// 実機のメールアカウントは使わない。</para>
/// <para>環境変数: <c>MAILINGEST_TEST_GREENMAIL_HOST</c>、<c>_USER</c>、<c>_PASSWORD</c>、
/// <c>_PORTS</c>（<c>smtp,pop3,imap,pop3s,imaps</c> の順にカンマ区切り）。</para>
/// </remarks>
[Collection("GreenMail")]
public sealed class GreenMailFetchTests
{
    private const string Prefix = "MAILINGEST_TEST_GREENMAIL_";

    private static string? Env(string name) => Environment.GetEnvironmentVariable(Prefix + name);

    private static string Host => Env("HOST")!;

    private static string User => Env("USER")!;

    private static string Password => Env("PASSWORD")!;

    private static int Port(int index) => int.Parse(Env("PORTS")!.Split(',')[index], System.Globalization.CultureInfo.InvariantCulture);

    private static int SmtpPort => Port(0);

    private static int Pop3Port => Port(1);

    private static int ImapPort => Port(2);

    private static int Pop3sPort => Port(3);

    private static int ImapsPort => Port(4);

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

    private sealed class Warnings : IMailDiagnostics
    {
        public List<(string SourceId, string Code, string Message)> Items { get; } = [];

        public void Warning(string sourceId, string code, string message) => Items.Add((sourceId, code, message));
    }

    private static MailSourceConfig Source(
        MailProtocol protocol,
        TlsMode tls,
        int port,
        string[] allowed,
        string sourceId = "test-source",
        LimitsConfig? limits = null) => new(
        sourceId,
        protocol,
        Host,
        port,
        tls,
        new CertificateValidationConfig(CertificateValidationMode.Strict, null),
        new AuthenticationConfig(allowed, true, "U", "P"),
        protocol == MailProtocol.Imap ? new ImapConfig("INBOX", ImapSearchState.All, null, AfterSuccessAction.Leave, null) : null,
        protocol == MailProtocol.Pop3 ? new Pop3Config(AfterSuccessAction.Leave) : null,
        limits ?? LimitsConfig.Default,
        new BodyConfig("preferPlain", "toText"),
        new AttachmentsConfig("metadataOnly", false),
        new TargetConfig(1, "results", "ClassZ", "ClassY", "skip", 1, [], new Dictionary<string, object>()));

    private static MailCredentials Credentials(string? password = null) => new(User, password ?? Password);

    /// <summary>受信箱を空にし、合成メールを n 通送る（古い順に subject-0, subject-1, ...）。</summary>
    private static async Task SeedAsync(int count, bool withBigMessage = false)
    {
        using (var imap = new ImapClient())
        {
            await imap.ConnectAsync(Host, ImapPort, SecureSocketOptions.None);
            await imap.AuthenticateAsync(User, Password);
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
        await smtp.ConnectAsync(Host, SmtpPort, SecureSocketOptions.None);
        for (var i = 0; i < count; i++)
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("送信者", "sender@example.invalid"));
            message.To.Add(new MailboxAddress("受信者", User + "@example.invalid"));
            message.Subject = $"件名-{i}";
            message.Body = new TextPart("plain") { Text = $"本文 {i}\n二行目" };
            await smtp.SendAsync(message);
            await Task.Delay(30);
        }

        if (withBigMessage)
        {
            var big = new MimeMessage();
            big.From.Add(new MailboxAddress("送信者", "sender@example.invalid"));
            big.To.Add(new MailboxAddress("受信者", User + "@example.invalid"));
            big.Subject = "大きいメール";
            big.Body = new TextPart("plain") { Text = new string('x', 50_000) };
            await smtp.SendAsync(big);
        }

        await smtp.DisconnectAsync(true);
        await Task.Delay(300);
    }

    private static async Task<int> UnseenCountAsync()
    {
        using var imap = new ImapClient();
        await imap.ConnectAsync(Host, ImapPort, SecureSocketOptions.None);
        await imap.AuthenticateAsync(User, Password);
        await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
        var unseen = await imap.Inbox.SearchAsync(SearchQuery.NotSeen);
        var total = imap.Inbox.Count;
        await imap.DisconnectAsync(true);
        return total == 0 ? -1 : unseen.Count;
    }

    private static async Task<int> Pop3CountAsync()
    {
        using var pop = new Pop3Client();
        await pop.ConnectAsync(Host, Pop3Port, SecureSocketOptions.None);
        await pop.AuthenticateAsync(User, Password);
        var count = await pop.GetMessageCountAsync();
        await pop.DisconnectAsync(true);
        return count;
    }

    private static readonly string[] ImapAllowed = ["PLAIN", "LOGIN", "IMAP-LOGIN"];
    private static readonly string[] Pop3Allowed = ["USER/PASS"];

    [GreenMailFact]
    public async Task IMAP_平文_非破壊で古い順に取得し_既読にせず_MIME_まで正規化できる()
    {
        await Gate.WaitAsync();
        try
        {
            await SeedAsync(3);
            var warnings = new Warnings();
            var factory = new MailKitSessionFactory(diagnostics: warnings);

            await using var session = await factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.None, ImapPort, ImapAllowed), Credentials());
            var listing = await session.ListAsync(10, _ => false);

            Assert.Equal(3, listing.Candidates.Count);
            Assert.False(listing.HasMore);
            Assert.All(listing.Candidates, c => Assert.StartsWith("imap:", c.Location.LocationKey, StringComparison.Ordinal));
            Assert.Equal(3, listing.Candidates.Select(c => c.Location.LocationKey).Distinct().Count());
            Assert.True(listing.Candidates.Zip(listing.Candidates.Skip(1)).All(p => p.First.Location.Uid < p.Second.Location.Uid));

            var subjects = new List<string?>();
            foreach (var candidate in listing.Candidates)
            {
                var raw = await session.OpenRawAsync(candidate, 25 * 1024 * 1024);
                Assert.False(raw.TooLarge);
                await using var stream = raw.Stream!;
                var result = await MimeMessageNormalizer.NormalizeAsync(stream, LimitsConfig.Default, DateTimeOffset.UtcNow);
                Assert.True(result.IsSuccess, result.Failure.ToString());
                subjects.Add(result.Mail!.Subject);
            }

            Assert.Equal(["件名-0", "件名-1", "件名-2"], subjects);
            var warning = Assert.Single(warnings.Items);
            Assert.Equal(("test-source", "PlaintextConnection"), (warning.SourceId, warning.Code));
            Assert.DoesNotContain(Password, warning.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(User, warning.Message, StringComparison.Ordinal);

            await session.DisposeAsync();
            Assert.Equal(3, await UnseenCountAsync()); // 取得しても既読にならない（非破壊）
        }
        finally
        {
            Gate.Release();
        }
    }

    [GreenMailFact]
    public async Task IMAP_既処理の位置は除き_件数の上限を超えたら_HasMore()
    {
        await Gate.WaitAsync();
        try
        {
            await SeedAsync(4);
            var factory = new MailKitSessionFactory();
            await using var session = await factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.None, ImapPort, ImapAllowed), Credentials());

            var first = await session.ListAsync(2, _ => false);
            var known = first.Candidates.Select(c => c.Location.LocationKey).ToHashSet();
            var second = await session.ListAsync(10, known.Contains);

            Assert.Equal(2, first.Candidates.Count);
            Assert.True(first.HasMore);
            Assert.Equal(2, second.Candidates.Count);
            Assert.False(second.HasMore);
            Assert.Empty(second.Candidates.Select(c => c.Location.LocationKey).Intersect(known));
        }
        finally
        {
            Gate.Release();
        }
    }

    [GreenMailFact]
    public async Task IMAP_RAW_の上限を超えるメールは取り込まない()
    {
        await Gate.WaitAsync();
        try
        {
            await SeedAsync(1, withBigMessage: true);
            var factory = new MailKitSessionFactory();
            await using var session = await factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.None, ImapPort, ImapAllowed), Credentials());
            var listing = await session.ListAsync(10, _ => false);
            var big = listing.Candidates.Single(c => c.DeclaredSize > 40_000);
            var small = listing.Candidates.Single(c => c.DeclaredSize < 40_000);

            var rejected = await session.OpenRawAsync(big, 10_000);
            var accepted = await session.OpenRawAsync(small, 10_000);

            Assert.True(rejected.TooLarge);
            Assert.Null(rejected.Stream);
            Assert.False(accepted.TooLarge);
            await accepted.Stream!.DisposeAsync();

            // 申告サイズを信用しない経路: 部分取得で上限 + 1 バイトまでしか読まない
            var lying = new VehicleVision.PleasanterTools.MailIngest.Mail.ImapCandidate(big.Location, 1, new UniqueId((uint)big.Location.Uid!.Value));
            var partial = await session.OpenRawAsync(lying, 10_000);
            Assert.True(partial.TooLarge);
        }
        finally
        {
            Gate.Release();
        }
    }

    [GreenMailFact]
    public async Task POP3_平文_UIDL_で識別して取得し_メールを残す()
    {
        await Gate.WaitAsync();
        try
        {
            await SeedAsync(3);
            var warnings = new Warnings();
            var factory = new MailKitSessionFactory(diagnostics: warnings);

            await using (var session = await factory.ConnectAsync(Source(MailProtocol.Pop3, TlsMode.None, Pop3Port, Pop3Allowed), Credentials()))
            {
                var listing = await session.ListAsync(10, _ => false);
                Assert.Equal(3, listing.Candidates.Count);
                Assert.All(listing.Candidates, c =>
                {
                    Assert.StartsWith("pop3:", c.Location.LocationKey, StringComparison.Ordinal);
                    Assert.False(string.IsNullOrEmpty(c.Location.Uidl));
                });

                var subjects = new List<string?>();
                foreach (var candidate in listing.Candidates)
                {
                    var raw = await session.OpenRawAsync(candidate, 25 * 1024 * 1024);
                    await using var stream = raw.Stream!;
                    var result = await MimeMessageNormalizer.NormalizeAsync(stream, LimitsConfig.Default, DateTimeOffset.UtcNow);
                    subjects.Add(result.Mail!.Subject);
                }

                Assert.Equal(["件名-0", "件名-1", "件名-2"], subjects.Order().ToList());

                var capped = await session.ListAsync(2, _ => false);
                Assert.True(capped.HasMore);
                var known = listing.Candidates.Take(1).Select(c => c.Location.LocationKey).ToHashSet();
                Assert.Equal(2, (await session.ListAsync(10, known.Contains)).Candidates.Count);
            }

            Assert.Equal(("test-source", "PlaintextConnection"), (warnings.Items[0].SourceId, warnings.Items[0].Code));
            Assert.Equal(3, await Pop3CountAsync()); // 削除しない（leave）
        }
        finally
        {
            Gate.Release();
        }
    }

    [GreenMailFact]
    public async Task POP3_申告サイズが上限を超えるメールは取得しない()
    {
        await Gate.WaitAsync();
        try
        {
            await SeedAsync(0, withBigMessage: true);
            var factory = new MailKitSessionFactory();
            await using var session = await factory.ConnectAsync(Source(MailProtocol.Pop3, TlsMode.None, Pop3Port, Pop3Allowed), Credentials());
            var candidate = Assert.Single((await session.ListAsync(10, _ => false)).Candidates);

            var raw = await session.OpenRawAsync(candidate, 10_000);

            Assert.True(raw.TooLarge);
            Assert.Null(raw.Stream);
        }
        finally
        {
            Gate.Release();
        }
    }

    [GreenMailFact]
    public async Task パスワードが違えば_Authentication_で止まり_メッセージに資格情報を含まない()
    {
        var factory = new MailKitSessionFactory();

        var ex = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.None, ImapPort, ImapAllowed), Credentials("wrong-password")));

        Assert.Equal(MailFailureKind.Authentication, ex.Kind);
        Assert.DoesNotContain("wrong-password", ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Host, ex.Message, StringComparison.Ordinal);
    }

    [GreenMailFact]
    public async Task 許可リストとサーバが一致しなければ認証を送らずに止まる()
    {
        var factory = new MailKitSessionFactory();

        var imap = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.None, ImapPort, ["CRAM-MD5"]), Credentials()));
        var pop3 = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Pop3, TlsMode.None, Pop3Port, ["APOP"]), Credentials()));

        Assert.Equal(MailFailureKind.NoAllowedAuthentication, imap.Kind);
        Assert.Equal(MailFailureKind.NoAllowedAuthentication, pop3.Kind);
    }

    [GreenMailFact]
    public async Task 暗黙_TLS_は自己署名証明書を_Strict_で拒否し_平文へ降格しない()
    {
        var factory = new MailKitSessionFactory();

        var imap = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.Tls, ImapsPort, ImapAllowed), Credentials()));
        var pop3 = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Pop3, TlsMode.Tls, Pop3sPort, ["PLAIN", "USER/PASS"]), Credentials()));

        Assert.Equal(MailFailureKind.Tls, imap.Kind);
        Assert.Equal(MailFailureKind.Tls, pop3.Kind);
    }

    [GreenMailFact]
    public async Task STARTTLS_非対応のサーバでは昇格に失敗して止まり_認証も平文もしない()
    {
        var warnings = new Warnings();
        var factory = new MailKitSessionFactory(diagnostics: warnings);

        var imap = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.StartTls, ImapPort, ImapAllowed), Credentials()));
        var pop3 = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Pop3, TlsMode.StartTls, Pop3Port, ["PLAIN", "USER/PASS"]), Credentials()));

        Assert.Equal(MailFailureKind.Tls, imap.Kind);
        Assert.Equal(MailFailureKind.Tls, pop3.Kind);
        Assert.Empty(warnings.Items); // 平文の警告（認証の直前）に到達していない = 認証を試みていない
    }

    [GreenMailFact]
    public async Task 接続できないポートは_Unavailable()
    {
        var factory = new MailKitSessionFactory(operationTimeout: TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<MailConnectionException>(
            () => factory.ConnectAsync(Source(MailProtocol.Imap, TlsMode.None, 1, ImapAllowed), Credentials()));

        Assert.Equal(MailFailureKind.Unavailable, ex.Kind);
    }
}
