using System.Net.Sockets;
using System.Security.Authentication;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Pop3;
using MailKit.Search;
using MailKit.Security;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Core.State;
using VehicleVision.PleasanterTools.MailIngest.Mail.Auth;

namespace VehicleVision.PleasanterTools.MailIngest.Mail;

/// <summary>
/// MailKit による IMAP / POP3 の接続・取得。<b>非破壊</b>（<c>leave</c> のみ。既読化・移動・削除はしない）。
/// </summary>
/// <remarks>
/// <para>接続保護は設定の 3 モードを MailKit の <see cref="SecureSocketOptions"/> へ 1 対 1 に写す。
/// <c>Auto</c> / <c>StartTlsWhenAvailable</c> は使わず、TLS の確立・昇格に失敗したら認証せず停止し、平文へ降格しない。
/// 証明書の検証は OS の既定（<c>Strict</c>）で、検証を緩めるコールバックは設定しない。TLS は 1.2 以上。</para>
/// <para>プロトコルのログ（ProtocolLogger）は有効にしない。例外メッセージには接続先・ユーザー名・秘密を含めない。</para>
/// </remarks>
public sealed class MailKitSessionFactory(
    Func<MailSourceConfig, MailCredentials, IMailAuthenticator>? authenticatorFactory = null,
    IMailDiagnostics? diagnostics = null,
    TimeSpan? operationTimeout = null) : IMailSessionFactory
{
    /// <summary>接続・認証・各操作のタイムアウトの既定（30 秒）。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _timeout = operationTimeout ?? DefaultTimeout;

    /// <summary>設定の接続保護を MailKit の指定へ写す。</summary>
    public static SecureSocketOptions ToSecureSocketOptions(TlsMode mode) => mode switch
    {
        TlsMode.None => SecureSocketOptions.None,
        TlsMode.StartTls => SecureSocketOptions.StartTls,
        TlsMode.Tls => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public async Task<IMailSession> ConnectAsync(
        MailSourceConfig source,
        MailCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(credentials);

        if (source.CertificateValidation.Mode != CertificateValidationMode.Strict)
        {
            // 証明書の例外（AllowUntrusted / Pinned）は未決事項のため未実装。緩めず、停止する
            throw new MailConnectionException(MailFailureKind.Protocol, "証明書検証 Strict 以外は未対応。");
        }

        var authenticator = authenticatorFactory?.Invoke(source, credentials)
            ?? new PasswordAuthenticator(credentials, source.Authentication.AllowedMechanisms);
        var options = ToSecureSocketOptions(source.Tls);

        return source.Protocol == MailProtocol.Imap
            ? await ConnectImapAsync(source, options, authenticator, cancellationToken).ConfigureAwait(false)
            : await ConnectPop3Async(source, options, authenticator, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IMailSession> ConnectImapAsync(
        MailSourceConfig source,
        SecureSocketOptions options,
        IMailAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var client = new ImapClient { Timeout = (int)_timeout.TotalMilliseconds, SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
        try
        {
            await ConnectAndAuthenticateAsync(client, source, options, authenticator, cancellationToken).ConfigureAwait(false);
            var imap = source.Imap ?? throw new MailConnectionException(MailFailureKind.Protocol, "IMAP の設定が無い。");
            return await ImapMailSession.OpenAsync(client, imap, source.Limits, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task<IMailSession> ConnectPop3Async(
        MailSourceConfig source,
        SecureSocketOptions options,
        IMailAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        var client = new Pop3Client { Timeout = (int)_timeout.TotalMilliseconds, SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 };
        try
        {
            await ConnectAndAuthenticateAsync(client, source, options, authenticator, cancellationToken).ConfigureAwait(false);
            if (!client.Capabilities.HasFlag(Pop3Capabilities.UIDL))
            {
                // UIDL が無いと、leave でも毎回すべてを再取得することになる。対応可否は未決のため、停止する
                throw new MailConnectionException(MailFailureKind.Protocol, "POP3 サーバが UIDL に対応していない。");
            }

            return new Pop3MailSession(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task ConnectAndAuthenticateAsync(
        MailService client,
        MailSourceConfig source,
        SecureSocketOptions options,
        IMailAuthenticator authenticator,
        CancellationToken cancellationToken)
    {
        try
        {
            await client.ConnectAsync(source.Host, source.Port, options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is SslHandshakeException or System.Security.Authentication.AuthenticationException or NotSupportedException)
        {
            // TLS の確立・昇格の失敗（STARTTLS 非対応を含む）。認証していない。平文へ降格しない
            throw new MailConnectionException(MailFailureKind.Tls, "TLS の確立・昇格に失敗した。認証は送っていない。");
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ServiceNotConnectedException)
        {
            throw new MailConnectionException(MailFailureKind.Unavailable, "メールサーバへ接続できない。");
        }
        catch (Exception ex) when (ex is ProtocolException or CommandException)
        {
            throw new MailConnectionException(MailFailureKind.Protocol, "メールサーバのプロトコルの異常。");
        }

        if (source.Tls == TlsMode.None)
        {
            // 認証の直前（各接続試行ごと）に警告する。ユーザー名・パスワード・本文は含めない
            diagnostics?.Warning(
                source.SourceId,
                "PlaintextConnection",
                $"ソース {source.SourceId}: 平文接続。認証情報と本文が暗号化されない。信頼できるネットワーク内だけで使う。");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeout);
            await authenticator.AuthenticateAsync(client, source.Protocol, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MailConnectionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or OperationCanceledException
            or ServiceNotConnectedException)
        {
            throw new MailConnectionException(MailFailureKind.Unavailable, "認証中にメールサーバとの通信が途切れた。");
        }
        catch (Exception ex) when (ex is ProtocolException or CommandException)
        {
            throw new MailConnectionException(MailFailureKind.Protocol, "メールサーバのプロトコルの異常。");
        }
    }
}

/// <summary>IMAP の取得。フォルダを読み取り専用（EXAMINE）で開き、RAW は部分取得で上限 + 1 バイトまでしか読まない。</summary>
internal sealed class ImapMailSession : IMailSession
{
    private const int ChunkSize = 200;

    private readonly ImapClient _client;
    private readonly IMailFolder _folder;
    private readonly ImapConfig _config;
    private readonly LimitsConfig _limits;

    private ImapMailSession(ImapClient client, IMailFolder folder, ImapConfig config, LimitsConfig limits)
    {
        _client = client;
        _folder = folder;
        _config = config;
        _limits = limits;
    }

    public static async Task<ImapMailSession> OpenAsync(
        ImapClient client,
        ImapConfig config,
        LimitsConfig limits,
        CancellationToken cancellationToken)
    {
        try
        {
            var folder = string.Equals(config.Folder, "INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox
                : await client.GetFolderAsync(config.Folder, cancellationToken).ConfigureAwait(false);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken).ConfigureAwait(false);
            return new ImapMailSession(client, folder, config, limits);
        }
        catch (Exception ex) when (ex is FolderNotFoundException or ImapCommandException or ImapProtocolException)
        {
            throw new MailConnectionException(MailFailureKind.Protocol, "IMAP のフォルダを開けない。");
        }
    }

    public async Task<MailListing> ListAsync(
        int maxCandidates,
        Func<string, bool> isKnown,
        CancellationToken cancellationToken = default)
    {
        var found = new List<MailCandidate>();
        var hasMore = false;
        var validity = _folder.UidValidity;

        bool Add(UniqueId uid, long? size)
        {
            var key = $"imap:{validity}:{uid.Id}:{_config.Folder}";
            if (isKnown(key))
            {
                return true;
            }

            if (found.Count >= maxCandidates)
            {
                return false;
            }

            found.Add(new ImapCandidate(
                new MailLocation("imap", key, _config.Folder, validity, uid.Id, null),
                size,
                uid));
            return true;
        }

        if (_config.SearchState == ImapSearchState.All && _config.ReceivedSinceUtc is null)
        {
            // 全 UID を一括で展開せず、メッセージ番号の範囲を区切って UID・サイズだけを取る
            for (var start = 0; start < _folder.Count && !hasMore; start += ChunkSize)
            {
                var end = Math.Min(start + ChunkSize - 1, _folder.Count - 1);
                var summaries = await _folder
                    .FetchAsync(start, end, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var summary in summaries.OrderBy(s => s.UniqueId.Id))
                {
                    if (!Add(summary.UniqueId, summary.Size))
                    {
                        hasMore = true;
                        break;
                    }
                }
            }
        }
        else
        {
            // 状態・日付の条件は SEARCH で絞る（構造化したクエリだけ。生の検索文字列は実行しない）。UID の一括展開は避けられない
            var query = _config.SearchState == ImapSearchState.Unseen ? SearchQuery.NotSeen : SearchQuery.All;
            if (_config.ReceivedSinceUtc is { } since)
            {
                query = query.And(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)));
            }

            var uids = await _folder.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            foreach (var uid in uids.OrderBy(u => u.Id))
            {
                if (!Add(uid, null))
                {
                    hasMore = true;
                    break;
                }
            }
        }

        return new MailListing(found, hasMore);
    }

    public async Task<MailRaw> OpenRawAsync(
        MailCandidate candidate,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var imap = (ImapCandidate)candidate;
        if (candidate.DeclaredSize is { } declared && declared > maxBytes)
        {
            return new MailRaw(null, TooLarge: true);
        }

        // サーバ申告のサイズは信用しない。上限 + 1 バイトまでだけを取る（BODY.PEEK の部分取得）
        var count = (int)Math.Min(maxBytes + 1, int.MaxValue - 1);
        var stream = await _folder.GetStreamAsync(imap.Uid, 0, count, cancellationToken).ConfigureAwait(false);
        if (stream.Length > maxBytes)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            return new MailRaw(null, TooLarge: true);
        }

        stream.Position = 0;
        return new MailRaw(stream, TooLarge: false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(quit: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ServiceNotConnectedException or ImapProtocolException)
        {
            // 切断の失敗は、取得した結果に影響しない
        }
        finally
        {
            _client.Dispose();
        }
    }
}

public sealed record ImapCandidate(MailLocation Location, long? Size, UniqueId Uid) : MailCandidate(Location, Size);

/// <summary>POP3 の取得。UIDL で位置を識別し、LIST で事前にサイズを見る。通番は再接続後の識別子にしない。</summary>
internal sealed class Pop3MailSession(Pop3Client client) : IMailSession
{
    public async Task<MailListing> ListAsync(
        int maxCandidates,
        Func<string, bool> isKnown,
        CancellationToken cancellationToken = default)
    {
        var uids = await client.GetMessageUidsAsync(cancellationToken).ConfigureAwait(false);
        var sizes = await client.GetMessageSizesAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<MailCandidate>();
        var hasMore = false;

        for (var index = 0; index < uids.Count; index++)
        {
            var uidl = uids[index];
            if (uidl is null || uidl.Length == 0 || uidl.Length > 70)
            {
                continue;
            }

            var key = $"pop3:{uidl}";
            if (isKnown(key))
            {
                continue;
            }

            if (found.Count >= maxCandidates)
            {
                hasMore = true;
                break;
            }

            found.Add(new Pop3Candidate(
                new MailLocation("pop3", key, null, null, null, uidl),
                index < sizes.Count ? sizes[index] : null,
                index));
        }

        return new MailListing(found, hasMore);
    }

    public async Task<MailRaw> OpenRawAsync(
        MailCandidate candidate,
        long maxBytes,
        CancellationToken cancellationToken = default)
    {
        var pop = (Pop3Candidate)candidate;
        if (candidate.DeclaredSize is { } declared && declared > maxBytes)
        {
            return new MailRaw(null, TooLarge: true);
        }

        // 申告サイズは信用しない。転送量が上限を超えたら、進捗の通知で打ち切る（接続は不定になるので呼び出し側が破棄する）
        var guard = new LimitProgress(maxBytes);
        try
        {
            var stream = await client.GetStreamAsync(pop.Index, headersOnly: false, cancellationToken, guard).ConfigureAwait(false);
            if (stream.Length > maxBytes)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                return new MailRaw(null, TooLarge: true);
            }

            stream.Position = 0;
            return new MailRaw(stream, TooLarge: false);
        }
        catch (RawLimitExceededException)
        {
            return new MailRaw(null, TooLarge: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(quit: true).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ServiceNotConnectedException or Pop3ProtocolException)
        {
            // 切断の失敗は、取得した結果に影響しない
        }
        finally
        {
            client.Dispose();
        }
    }

    private sealed class RawLimitExceededException : Exception;

    private sealed class LimitProgress(long limit) : ITransferProgress
    {
        public void Report(long bytesTransferred, long totalSize)
        {
            if (bytesTransferred > limit)
            {
                throw new RawLimitExceededException();
            }
        }

        public void Report(long bytesTransferred)
        {
            if (bytesTransferred > limit)
            {
                throw new RawLimitExceededException();
            }
        }
    }
}

internal sealed record Pop3Candidate(MailLocation Location, long? Size, int Index) : MailCandidate(Location, Size);
