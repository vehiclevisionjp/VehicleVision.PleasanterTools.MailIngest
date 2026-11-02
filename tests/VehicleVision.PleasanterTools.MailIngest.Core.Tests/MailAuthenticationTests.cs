using System.Text;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Mail;
using VehicleVision.PleasanterTools.MailIngest.Mail.Auth;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

public sealed class MailAuthenticationTests
{
    private static readonly string[] None = [];

    [Theory]
    // TLS 時の既定の許可リスト
    [InlineData(MailProtocol.Imap, "PLAIN,LOGIN,IMAP-LOGIN", "PLAIN,LOGIN", false, false, PasswordMechanism.Plain)]
    [InlineData(MailProtocol.Imap, "PLAIN,LOGIN,IMAP-LOGIN", "LOGIN", false, false, PasswordMechanism.Login)]
    [InlineData(MailProtocol.Imap, "PLAIN,LOGIN,IMAP-LOGIN", "", false, false, PasswordMechanism.ImapLogin)]
    [InlineData(MailProtocol.Pop3, "PLAIN,LOGIN,USER/PASS", "", false, false, PasswordMechanism.Pop3UserPass)]
    [InlineData(MailProtocol.Pop3, "PLAIN,LOGIN,USER/PASS", "PLAIN", false, false, PasswordMechanism.Plain)]
    // 許可リストにある方式だけを使う
    [InlineData(MailProtocol.Imap, "IMAP-LOGIN", "PLAIN,LOGIN", false, false, PasswordMechanism.ImapLogin)]
    [InlineData(MailProtocol.Pop3, "USER/PASS", "PLAIN", false, false, PasswordMechanism.Pop3UserPass)]
    [InlineData(MailProtocol.Imap, "CRAM-MD5", "PLAIN,CRAM-MD5", false, false, PasswordMechanism.CramMd5)]
    // APOP は POP3 で明示かつ広告があるときだけ
    [InlineData(MailProtocol.Pop3, "APOP,USER/PASS", "", false, true, PasswordMechanism.Apop)]
    [InlineData(MailProtocol.Pop3, "APOP,USER/PASS", "", false, false, PasswordMechanism.Pop3UserPass)]
    [InlineData(MailProtocol.Pop3, "APOP", "", false, false, null)]
    // 共通部分が無ければ認証しない
    [InlineData(MailProtocol.Imap, "PLAIN", "LOGIN", false, false, null)]
    [InlineData(MailProtocol.Imap, "IMAP-LOGIN", "PLAIN", true, false, null)]
    [InlineData(MailProtocol.Imap, "PLAIN,LOGIN", "", false, false, null)]
    [InlineData(MailProtocol.Pop3, "PLAIN,LOGIN", "", false, false, null)]
    // 別プロトコルの方式は選ばない
    [InlineData(MailProtocol.Imap, "USER/PASS,APOP", "", false, true, null)]
    [InlineData(MailProtocol.Pop3, "IMAP-LOGIN", "", false, false, null)]
    public void 許可リスト_サーバの広告_プロトコルの共通部分から方式を選ぶ(
        MailProtocol protocol,
        string allowed,
        string advertised,
        bool imapLoginDisabled,
        bool apop,
        PasswordMechanism? expected)
    {
        var plan = PasswordAuthenticationPlanner.Plan(protocol, Split(allowed), Split(advertised), imapLoginDisabled, apop);

        Assert.Equal(expected, plan);
    }

    private static string[] Split(string text) => text.Length == 0 ? None : text.Split(',');

    [Theory]
    [InlineData(TlsMode.None, "None")]
    [InlineData(TlsMode.StartTls, "StartTls")]
    [InlineData(TlsMode.Tls, "SslOnConnect")]
    public void 接続保護は_1_対_1_で写し_自動選択を使わない(TlsMode mode, string expected)
    {
        Assert.Equal(expected, MailKitSessionFactory.ToSecureSocketOptions(mode).ToString());
    }

    [Fact]
    public void 資格情報は文字列化してもパスワードを出さない()
    {
        var credentials = new MailCredentials("user-name", "pass-word");

        Assert.DoesNotContain("pass-word", credentials.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("user-name", credentials.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void 環境変数から秘密を解決する()
    {
        var env = new Dictionary<string, string?> { ["X"] = "value" };

        Assert.Equal("value", SecretResolver.Resolve("X", n => env.GetValueOrDefault(n)));
    }

    [Fact]
    public void 環境変数と_FILE_の同時指定は拒否し_どちらも無ければ拒否する()
    {
        var both = new Dictionary<string, string?> { ["X"] = "a", ["X_FILE"] = "/run/x" };
        var none = new Dictionary<string, string?>();

        Assert.Contains("同時", Assert.Throws<SecretResolutionException>(() => SecretResolver.Resolve("X", n => both.GetValueOrDefault(n))).Message, StringComparison.Ordinal);
        Assert.Throws<SecretResolutionException>(() => SecretResolver.Resolve("X", n => none.GetValueOrDefault(n)));
    }

    [Fact]
    public void FILE_は絶対パスで_UTF8_末尾の改行_1_個だけ除く()
    {
        var path = OperatingSystem.IsWindows() ? "C:\\run\\secret" : "/run/secret";
        var env = new Dictionary<string, string?> { ["X_FILE"] = path };

        string Resolve(string content) => SecretResolver.Resolve(
            "X",
            n => env.GetValueOrDefault(n),
            _ => new MemoryStream(Encoding.UTF8.GetBytes(content)));

        Assert.Equal("pass", Resolve("pass\n"));
        Assert.Equal("pass", Resolve("pass\r\n"));
        Assert.Equal("pass\n", Resolve("pass\n\n"));
        Assert.Equal("日本語", Resolve("日本語"));
        Assert.Throws<SecretResolutionException>(() => Resolve(string.Empty));
        Assert.Throws<SecretResolutionException>(() => Resolve("\n"));
    }

    [Fact]
    public void FILE_は相対パス_大きすぎる_UTF8_でない_読めないものを拒否し_パスや内容を出さない()
    {
        var path = OperatingSystem.IsWindows() ? "C:\\run\\secret" : "/run/secret";
        var env = new Dictionary<string, string?> { ["X_FILE"] = path };
        var relative = new Dictionary<string, string?> { ["X_FILE"] = "secret.txt" };

        var tooLarge = Assert.Throws<SecretResolutionException>(() => SecretResolver.Resolve(
            "X", n => env.GetValueOrDefault(n), _ => new MemoryStream(new byte[SecretResolver.MaxFileBytes + 1])));
        var invalid = Assert.Throws<SecretResolutionException>(() => SecretResolver.Resolve(
            "X", n => env.GetValueOrDefault(n), _ => new MemoryStream([0xFF, 0xFE, 0xFD])));
        var unreadable = Assert.Throws<SecretResolutionException>(() => SecretResolver.Resolve(
            "X", n => env.GetValueOrDefault(n), _ => throw new IOException("cannot open " + path)));
        var notAbsolute = Assert.Throws<SecretResolutionException>(() => SecretResolver.Resolve("X", n => relative.GetValueOrDefault(n)));

        Assert.True(SecretResolver.Resolve("X", n => env.GetValueOrDefault(n), _ => new MemoryStream(new byte[SecretResolver.MaxFileBytes - 1])
            .Fill((byte)'a')).Length == SecretResolver.MaxFileBytes - 1);
        foreach (var ex in new[] { tooLarge, invalid, unreadable, notAbsolute })
        {
            Assert.DoesNotContain("secret", ex.Message.Replace("X_FILE", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("run", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 実ファイルから秘密を読める()
    {
        var path = Path.Combine(Path.GetTempPath(), "mailingest-secret-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "file-secret\n");
        try
        {
            var env = new Dictionary<string, string?> { ["X_FILE"] = path };

            Assert.Equal("file-secret", SecretResolver.Resolve("X", n => env.GetValueOrDefault(n)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

internal static class MemoryStreamExtensions
{
    public static MemoryStream Fill(this MemoryStream stream, byte value)
    {
        stream.Position = 0;
        var buffer = new byte[stream.Length];
        Array.Fill(buffer, value);
        stream.Write(buffer);
        stream.Position = 0;
        return stream;
    }
}
