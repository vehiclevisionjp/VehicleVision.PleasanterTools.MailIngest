using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

/// <summary>MIME の正規化に失敗した理由。失敗したメールは保留して残し、後続のメールは続ける。</summary>
public enum MimeFailure
{
    /// <summary>成功。</summary>
    None,

    /// <summary>空・解釈できない入力。</summary>
    InvalidMime,

    /// <summary>受信 RAW の上限を超えた（実ストリームで強制する）。</summary>
    RawTooLarge,

    /// <summary>ヘッダの合計が上限を超えた（解析後に検査する）。</summary>
    HeadersTooLarge,

    /// <summary>MIME の入れ子が上限を超えた（解析後に検査する）。</summary>
    MimeTooDeep,

    /// <summary>パート数が上限を超えた（解析後に検査する）。</summary>
    TooManyParts,

    /// <summary>本文のデコード後サイズが上限を超えた（デコードのストリームで強制する）。</summary>
    BodyTooLarge,

    /// <summary>宣言された文字コードが不明、または復号できない。内容を推測して成功にしない。</summary>
    InvalidEncoding,

    /// <summary>単一行の項目に改行・NUL が含まれる。</summary>
    ControlCharacter,

    /// <summary>暗号化された本文しかない（S/MIME・PGP の復号は対象外）。</summary>
    UnsupportedEncrypted,
}

/// <summary>MIME の正規化結果。</summary>
/// <param name="Failure">失敗の理由。成功なら <see cref="MimeFailure.None"/>。</param>
/// <param name="Mail">成功時の正規化メール。</param>
/// <param name="RawSha256">受信した RAW 全体の SHA-256（小文字 16 進 64 桁）。Message-ID 欠落時の代替キーの元。</param>
/// <param name="AttachmentDigests">添付パートのデコード後の内容の SHA-256。内容ハッシュへ含める。</param>
/// <param name="MessageIdIsValid"><c>Mail.MessageId</c> が妥当なとき <c>true</c>。欠落・複数・不正なら <c>false</c>。</param>
public sealed record MimeNormalizeResult(
    MimeFailure Failure,
    NormalizedMail? Mail,
    string? RawSha256,
    IReadOnlyList<string> AttachmentDigests,
    bool MessageIdIsValid)
{
    public bool IsSuccess => Failure == MimeFailure.None;

    internal static MimeNormalizeResult Fail(MimeFailure failure, string? rawSha256 = null) =>
        new(failure, null, rawSha256, [], false);
}
