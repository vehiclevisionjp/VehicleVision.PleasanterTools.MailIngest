using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Ingest;

namespace VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

/// <summary><see cref="MimeMessageNormalizer"/>（MimeKit）を、<c>.Core</c> の境界 <see cref="IMailNormalizer"/> へ合わせる。</summary>
public sealed class MimeMailNormalizer : IMailNormalizer
{
    public async Task<NormalizeOutcome> NormalizeAsync(
        Stream raw,
        LimitsConfig limits,
        DateTimeOffset fetchedAt,
        DateTimeOffset? receivedAt,
        CancellationToken cancellationToken = default)
    {
        var result = await MimeMessageNormalizer.NormalizeAsync(raw, limits, fetchedAt, receivedAt, cancellationToken)
            .ConfigureAwait(false);
        return new NormalizeOutcome(
            result.Failure == MimeFailure.None ? null : result.Failure.ToString(),
            result.Mail,
            result.RawSha256,
            result.AttachmentDigests,
            result.MessageIdIsValid);
    }
}
