using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Ingest;

/// <summary>MIME の正規化の結果。<c>.Mail</c>（MimeKit）が <see cref="IMailNormalizer"/> で返す。</summary>
/// <param name="FailureCode">失敗の理由（例: <c>RawTooLarge</c>・<c>InvalidMime</c>）。成功なら <c>null</c>。</param>
/// <param name="Mail">成功時の正規化メール。</param>
/// <param name="RawSha256">受信した RAW 全体の SHA-256（小文字 16 進 64 桁）。上限超過では <c>null</c>。</param>
/// <param name="AttachmentDigests">添付パートの内容の SHA-256。内容ハッシュへ含める。</param>
/// <param name="MessageIdIsValid"><c>Mail.MessageId</c> が妥当なとき <c>true</c>。</param>
public sealed record NormalizeOutcome(
    string? FailureCode,
    NormalizedMail? Mail,
    string? RawSha256,
    IReadOnlyList<string> AttachmentDigests,
    bool MessageIdIsValid)
{
    public bool IsSuccess => FailureCode is null;
}

/// <summary>RAW のメールを正規化メールへ変換する境界。<c>.Core</c> は MimeKit の型を持たない。</summary>
public interface IMailNormalizer
{
    Task<NormalizeOutcome> NormalizeAsync(
        Stream raw,
        LimitsConfig limits,
        DateTimeOffset fetchedAt,
        DateTimeOffset? receivedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>ログの水準。</summary>
public enum IngestLogLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// 構造化ログの 1 件。<b>この形に収まるものだけを出す</b>（設計: runId・sourceId・状態 ID・エラーコード・件数・所要時間・HTTP コード）。
/// 本文・件名・差出人・宛先・生の Message-ID・添付名・API 応答文・秘密は載せられない。
/// </summary>
/// <param name="Event">固定の事象名（例: <c>ItemHeld</c>）。</param>
/// <param name="ItemId">状態 DB のランダムな ID。</param>
public sealed record IngestLogEntry(
    IngestLogLevel Level,
    string Event,
    string? SourceId = null,
    Guid? ItemId = null,
    string? Code = null,
    int? HttpStatus = null,
    int? Count = null,
    long? DurationMs = null);

/// <summary>構造化ログの出力先。</summary>
public interface IIngestLog
{
    void Write(IngestLogEntry entry);
}

/// <summary>何も出さないログ。</summary>
public sealed class NullIngestLog : IIngestLog
{
    public static NullIngestLog Instance { get; } = new();

    public void Write(IngestLogEntry entry)
    {
    }
}
