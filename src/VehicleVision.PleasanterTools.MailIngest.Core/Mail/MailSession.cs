using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.State;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Mail;

/// <summary>接続・認証・取得の失敗の分類。メッセージには接続先・ユーザー名・秘密を含めない。</summary>
public enum MailFailureKind
{
    /// <summary>TLS の確立・昇格に失敗、または証明書の検証に失敗。認証していない。平文へ降格しない。</summary>
    Tls,

    /// <summary>認証に失敗（資格情報の拒否）。対象ソースを止め、人が直す。自動の再認証ループはしない。</summary>
    Authentication,

    /// <summary>設定が許可する認証方式が、サーバと一致しない。認証を送っていない。</summary>
    NoAllowedAuthentication,

    /// <summary>接続できない・タイムアウト。一時的。</summary>
    Unavailable,

    /// <summary>プロトコルの異常、または対応していない機能（例: UIDL が無い POP3）。</summary>
    Protocol,
}

/// <summary>メール接続・取得の失敗。</summary>
public sealed class MailConnectionException(MailFailureKind kind, string message) : Exception(message)
{
    public MailFailureKind Kind { get; } = kind;
}

/// <summary>メールサーバの資格情報。値はログ・例外・ToString に出さない。</summary>
public sealed class MailCredentials(string user, string password)
{
    public string User { get; } = user;

    public string Password { get; } = password;

    public override string ToString() => nameof(MailCredentials);
}

/// <summary>取り込み候補（1 通のメール）。サーバ申告のサイズは信用しない。</summary>
public abstract record MailCandidate(MailLocation Location, long? DeclaredSize);

/// <summary>RAW の取得結果。</summary>
/// <param name="Stream">RAW。上限以内のときだけ。呼び出し側が破棄する。</param>
/// <param name="TooLarge">上限を超えた（申告サイズまたは実際の取得量）。</param>
public sealed record MailRaw(Stream? Stream, bool TooLarge);

/// <summary>一覧の結果。</summary>
/// <param name="Candidates">未処理の候補（上限件数まで）。古い順。</param>
/// <param name="HasMore">上限を超えて、さらに候補が残っている。</param>
public sealed record MailListing(IReadOnlyList<MailCandidate> Candidates, bool HasMore);

/// <summary>メールサーバとの 1 回の接続。<b>非破壊</b>（フラグ・移動・削除をしない）。</summary>
public interface IMailSession : IAsyncDisposable
{
    /// <summary>
    /// 候補を古い順に最大 <paramref name="maxCandidates"/> 件まで挙げる。<paramref name="isKnown"/> が <c>true</c> の位置は除く
    /// （既処理の判定）。
    /// </summary>
    Task<MailListing> ListAsync(int maxCandidates, Func<string, bool> isKnown, CancellationToken cancellationToken = default);

    /// <summary>RAW を取得する。<paramref name="maxBytes"/> を超えるものは取り込まない（上限 + 1 バイトまでしか読まない）。</summary>
    Task<MailRaw> OpenRawAsync(MailCandidate candidate, long maxBytes, CancellationToken cancellationToken = default);
}

/// <summary>接続中の警告（平文など）の受け口。パスワード・ユーザー名・本文は渡さない。</summary>
public interface IMailDiagnostics
{
    void Warning(string sourceId, string code, string message);
}

/// <summary>ソース設定から、接続・認証済みのセッションを作る。</summary>
public interface IMailSessionFactory
{
    Task<IMailSession> ConnectAsync(
        MailSourceConfig source,
        MailCredentials credentials,
        CancellationToken cancellationToken = default);
}
