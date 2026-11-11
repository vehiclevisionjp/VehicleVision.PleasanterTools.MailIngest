using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Security;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;

namespace VehicleVision.PleasanterTools.MailIngest.Mail.Auth;

/// <summary>
/// 認証方式の機構（差し替え可能な抽象）。接続・TLS 確立の後に呼ばれる。パスワード認証は
/// <see cref="PasswordAuthenticator"/>、OAuth2（XOAUTH2）は別 Issue（#65）で同じ抽象に足す。
/// </summary>
public interface IMailAuthenticator
{
    /// <summary>認証する。設定が許可する方式にサーバが一致しなければ、認証を送らずに <see cref="MailConnectionException"/>。</summary>
    Task AuthenticateAsync(MailService client, MailProtocol protocol, CancellationToken cancellationToken);
}

/// <summary>パスワード認証で使う方式。</summary>
public enum PasswordMechanism
{
    Plain,
    Login,
    CramMd5,
    Apop,
    Pop3UserPass,
    ImapLogin,
}

/// <summary>
/// 許可リスト・サーバの広告・MailKit の対応方式の共通部分だけから、使う方式を 1 つ選ぶ（純粋な計画）。
/// </summary>
/// <remarks>
/// 一致しなければ <c>null</c>（認証を送らない）。複数の方式を順に試して失敗を繰り返さない（ロックアウト回避）。
/// 優先順位は SASL の PLAIN / LOGIN / CRAM-MD5、POP3 の APOP、非 SASL（USER/PASS・IMAP LOGIN）。
/// </remarks>
public static class PasswordAuthenticationPlanner
{
    public static PasswordMechanism? Plan(
        MailProtocol protocol,
        IReadOnlyCollection<string> allowed,
        IReadOnlyCollection<string> advertisedSasl,
        bool imapLoginDisabled,
        bool pop3ApopAdvertised)
    {
        ArgumentNullException.ThrowIfNull(allowed);
        ArgumentNullException.ThrowIfNull(advertisedSasl);

        bool Sasl(string name) =>
            allowed.Contains(name, StringComparer.Ordinal) && advertisedSasl.Contains(name, StringComparer.OrdinalIgnoreCase);

        if (Sasl("PLAIN"))
        {
            return PasswordMechanism.Plain;
        }

        if (Sasl("LOGIN"))
        {
            return PasswordMechanism.Login;
        }

        if (Sasl("CRAM-MD5"))
        {
            return PasswordMechanism.CramMd5;
        }

        if (protocol == MailProtocol.Pop3)
        {
            if (allowed.Contains("APOP", StringComparer.Ordinal) && pop3ApopAdvertised)
            {
                return PasswordMechanism.Apop;
            }

            return allowed.Contains("USER/PASS", StringComparer.Ordinal) ? PasswordMechanism.Pop3UserPass : null;
        }

        return allowed.Contains("IMAP-LOGIN", StringComparer.Ordinal) && !imapLoginDisabled
            ? PasswordMechanism.ImapLogin
            : null;
    }
}

/// <summary>パスワード認証。設定の許可リストにある方式だけを、明示して使う（暗黙のフォールバックをしない）。</summary>
public sealed class PasswordAuthenticator(MailCredentials credentials, IReadOnlyList<string> allowedMechanisms) : IMailAuthenticator
{
    public async Task AuthenticateAsync(MailService client, MailProtocol protocol, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        var advertised = client.AuthenticationMechanisms.ToList();
        var imapLoginDisabled = client is ImapClient imap && imap.Capabilities.HasFlag(ImapCapabilities.LoginDisabled);
        var apop = client is Pop3Client pop3 && pop3.Capabilities.HasFlag(Pop3Capabilities.Apop);

        var plan = PasswordAuthenticationPlanner.Plan(protocol, allowedMechanisms, advertised, imapLoginDisabled, apop)
            ?? throw new MailConnectionException(
                MailFailureKind.NoAllowedAuthentication,
                "The server supports none of the authentication mechanisms allowed by the configuration. No authentication was sent.");

        try
        {
            switch (plan)
            {
                case PasswordMechanism.Plain:
                    await client.AuthenticateAsync(new SaslMechanismPlain(credentials.User, credentials.Password), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PasswordMechanism.Login:
                    await client.AuthenticateAsync(new SaslMechanismLogin(credentials.User, credentials.Password), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PasswordMechanism.CramMd5:
                    await client.AuthenticateAsync(new SaslMechanismCramMd5(credentials.User, credentials.Password), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    // 非 SASL: MailKit の自動選択（SASL → APOP → USER/PASS の順）を、広告を隠して 1 つに固定する
                    client.AuthenticationMechanisms.Clear();
                    if (client is Pop3Client pop)
                    {
                        pop.Capabilities = plan == PasswordMechanism.Apop
                            ? pop.Capabilities | Pop3Capabilities.Apop
                            : pop.Capabilities & ~Pop3Capabilities.Apop;
                    }

                    await client.AuthenticateAsync(credentials.User, credentials.Password, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (AuthenticationException)
        {
            // 資格情報の拒否。ユーザー名・サーバの応答文は載せない
            throw new MailConnectionException(MailFailureKind.Authentication, "Authentication was rejected.");
        }
    }
}
