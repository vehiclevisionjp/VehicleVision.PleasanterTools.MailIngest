using System.Security.Cryptography;
using System.Text;
using MimeKit;
using MimeKit.Cryptography;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Identity;
using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

/// <summary>
/// 受信した RAW のメールを、MimeKit の構造化パーサーで <see cref="NormalizedMail"/> へ変換する。
/// </summary>
/// <remarks>
/// <para>上限の強制方法（先行試験の結論。<c>MimeMessageNormalizerTests</c> で確認）:</para>
/// <list type="bullet">
/// <item><b>RAW</b>: 読み込み時に、上限 + 1 バイトで打ち切る（解析前に強制）。サーバ申告のサイズは信用しない。</item>
/// <item><b>本文のデコード後サイズ</b>: デコード済みストリームを上限 + 1 バイトまでしか読まない（強制）。</item>
/// <item><b>入れ子・パート数・ヘッダ合計</b>: MimeKit の解析後に検査する。<c>ParserOptions.MaxMimeDepth</c> は超過を拒否せず
///   <i>葉として扱う</i>ため、設定するだけでは拒否にならない。ここでは再帰の深さを有界にするために使い、
///   葉扱いになった multipart を「深すぎる」として拒否する。解析前の強制ではなく、メモリは RAW の上限で抑える。</item>
/// </list>
/// <para>S/MIME・PGP の復号・署名検証、外部リソースの取得、CID の解決、添付の保存・転送はしない。
/// 添付は数と安全化した名前と内容の SHA-256 だけを取る。</para>
/// </remarks>
public static class MimeMessageNormalizer
{
    /// <summary>添付名の長さの上限。</summary>
    public const int MaxAttachmentNameLength = 255;

    static MimeMessageNormalizer()
    {
        // ISO-2022-JP / Shift_JIS などの日本語コードページを使えるようにする
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static async Task<MimeNormalizeResult> NormalizeAsync(
        Stream raw,
        LimitsConfig limits,
        DateTimeOffset fetchedAt,
        DateTimeOffset? receivedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(limits);

        var (bytes, sha256) = await ReadLimitedAsync(raw, limits.RawBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null)
        {
            return MimeNormalizeResult.Fail(MimeFailure.RawTooLarge);
        }

        if (bytes.Length == 0)
        {
            return MimeNormalizeResult.Fail(MimeFailure.InvalidMime, sha256);
        }

        MimeMessage message;
        try
        {
            var options = new ParserOptions { MaxMimeDepth = limits.MimeDepth + 2 };
            using var stream = new MemoryStream(bytes, writable: false);
            message = await MimeMessage.LoadAsync(options, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return MimeNormalizeResult.Fail(MimeFailure.InvalidMime, sha256);
        }

        return Normalize(message, limits, fetchedAt, receivedAt, sha256, cancellationToken);
    }

    private static MimeNormalizeResult Normalize(
        MimeMessage message,
        LimitsConfig limits,
        DateTimeOffset fetchedAt,
        DateTimeOffset? receivedAt,
        string sha256,
        CancellationToken cancellationToken)
    {
        // 構造の検査（入れ子・パート数・ヘッダ合計）
        var (structure, leaves, encrypted) = Walk(message, limits);
        if (structure != MimeFailure.None)
        {
            return MimeNormalizeResult.Fail(structure, sha256);
        }

        // 単一行の項目
        var subject = message.Subject;
        if (subject is not null)
        {
            if (subject.Contains('�'))
            {
                return MimeNormalizeResult.Fail(MimeFailure.InvalidEncoding, sha256);
            }

            if (HasLineBreakOrNul(subject))
            {
                return MimeNormalizeResult.Fail(MimeFailure.ControlCharacter, sha256);
            }
        }

        var from = ToAddressList(message.From);
        var to = ToAddressList(message.To);
        var cc = ToAddressList(message.Cc);
        if (from is null || to is null || cc is null)
        {
            return MimeNormalizeResult.Fail(MimeFailure.ControlCharacter, sha256);
        }

        // Message-ID: 欠落・複数・不正は MessageIdIsValid = false（代替キーは RAW の SHA-256）
        var messageIds = message.Headers.Where(h => h.Id == HeaderId.MessageId).ToList();
        string? messageId = null;
        var messageIdIsValid = messageIds.Count == 1 && MessageIdNormalizer.TryNormalize(messageIds[0].Value, out messageId);
        if (!messageIdIsValid)
        {
            messageId = null;
        }

        // 本文: text/plain 優先、無ければ HTML をテキスト化
        var body = SelectBody(leaves);
        string? bodyText = null;
        if (body is not null)
        {
            var decoded = DecodeBody(body, limits.DecodedBodyBytes, cancellationToken);
            if (decoded.Failure != MimeFailure.None)
            {
                return MimeNormalizeResult.Fail(decoded.Failure, sha256);
            }

            bodyText = body.IsHtml && !body.IsPlain ? HtmlTextExtractor.ToText(decoded.Text!) : decoded.Text!;
            bodyText = bodyText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimStart('﻿');
            if (bodyText.Contains('\0'))
            {
                return MimeNormalizeResult.Fail(MimeFailure.ControlCharacter, sha256);
            }
        }
        else if (encrypted)
        {
            return MimeNormalizeResult.Fail(MimeFailure.UnsupportedEncrypted, sha256);
        }

        // 添付: 数・安全化した名前・内容の SHA-256 だけ。保存も転送もしない
        var attachmentNames = new List<string>();
        var digests = new List<string>();
        foreach (var part in leaves.Where(l => !ReferenceEquals(l, body) && IsAttachment(l)))
        {
            var name = SanitizeAttachmentName(AttachmentFileName(part), attachmentNames.Count + 1);
            attachmentNames.Add(name);
            digests.Add(Digest(part, cancellationToken));
        }

        var sentAt = message.Date == DateTimeOffset.MinValue ? (DateTimeOffset?)null : message.Date;
        var mail = new NormalizedMail
        {
            Subject = string.IsNullOrEmpty(subject) ? null : subject,
            BodyText = bodyText,
            From = from,
            To = to,
            Cc = cc,
            MessageId = messageId,
            SentAt = sentAt,
            ReceivedAt = receivedAt,
            FetchedAt = fetchedAt,
            AttachmentCount = attachmentNames.Count,
            AttachmentNames = attachmentNames,
        };
        return new MimeNormalizeResult(MimeFailure.None, mail, sha256, digests, messageIdIsValid);
    }

    /// <summary>RAW を上限 + 1 バイトまでしか読まない。超えたら <c>null</c>。</summary>
    private static async Task<(byte[]? Bytes, string Sha256)> ReadLimitedAsync(
        Stream raw,
        long limit,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await raw.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                return (null, string.Empty);
            }

            buffer.Write(chunk, 0, read);
            hash.AppendData(chunk, 0, read);
        }

        return (buffer.ToArray(), Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>
    /// 入れ子の深さ・パート数・ヘッダ合計を検査しながら葉のパートを集める。明示的なスタックで走査する（再帰しない）。
    /// </summary>
    private static (MimeFailure Failure, List<MimePart> Leaves, bool Encrypted) Walk(MimeMessage message, LimitsConfig limits)
    {
        var leaves = new List<MimePart>();
        var encrypted = false;
        long headerBytes = message.Headers.Sum(HeaderSize);
        var parts = 0;
        var stack = new Stack<(MimeEntity Entity, int Depth)>();
        if (message.Body is not null)
        {
            stack.Push((message.Body, 1));
        }

        while (stack.Count > 0)
        {
            var (entity, depth) = stack.Pop();
            parts++;
            if (depth > limits.MimeDepth)
            {
                return (MimeFailure.MimeTooDeep, leaves, encrypted);
            }

            if (parts > limits.MimeParts)
            {
                return (MimeFailure.TooManyParts, leaves, encrypted);
            }

            headerBytes += entity.Headers.Sum(HeaderSize);
            if (headerBytes > limits.HeaderBytes)
            {
                return (MimeFailure.HeadersTooLarge, leaves, encrypted);
            }

            switch (entity)
            {
                case Multipart multipart:
                    encrypted |= multipart is MultipartEncrypted;
                    foreach (var child in multipart.Reverse())
                    {
                        stack.Push((child, depth + 1));
                    }

                    break;
                case MimePart part when part.ContentType.IsMimeType("multipart", "*"):
                    // 深さの上限で MimeKit が葉として扱った multipart
                    return (MimeFailure.MimeTooDeep, leaves, encrypted);
                case MessagePart:
                    // message/rfc822 は中へ入らず、添付として数える
                    leaves.Add(new MimePart(entity.ContentType) { ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) });
                    break;
                case MimePart part:
                    encrypted |= part is ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData };
                    leaves.Add(part);
                    break;
                default:
                    break;
            }
        }

        leaves.Reverse();
        return (MimeFailure.None, leaves, encrypted);
    }

    private static long HeaderSize(Header header) => (long)header.RawField.Length + header.RawValue.Length;

    private static TextPart? SelectBody(List<MimePart> leaves)
    {
        var texts = leaves.OfType<TextPart>().Where(t => !IsAttachment(t)).ToList();
        return texts.FirstOrDefault(t => t.IsPlain) ?? texts.FirstOrDefault(t => t.IsHtml);
    }

    private static bool IsAttachment(MimePart part) =>
        part.IsAttachment
        || part.ContentDisposition?.Disposition == ContentDisposition.Attachment
        || !string.IsNullOrEmpty(part.FileName)
        || part is not TextPart;

    private static string? AttachmentFileName(MimePart part) =>
        part.FileName ?? part.ContentType.Name;

    /// <summary>
    /// 添付名を安全にする。パス区切りより後ろだけを残し、制御文字を除き、長さを制限する。空なら連番の名前にする。
    /// </summary>
    internal static string SanitizeAttachmentName(string? name, int index)
    {
        var text = name ?? string.Empty;
        var lastSeparator = text.LastIndexOfAny(['/', '\\']);
        if (lastSeparator >= 0)
        {
            text = text[(lastSeparator + 1)..];
        }

        text = new string(text.Where(c => !char.IsControl(c) && c != '�').ToArray()).Trim().TrimStart('.');
        if (text.Length > MaxAttachmentNameLength)
        {
            text = text[..MaxAttachmentNameLength];
        }

        return text.Length == 0 ? $"attachment-{index}" : text;
    }

    private static string Digest(MimePart part, CancellationToken cancellationToken)
    {
        if (part.Content is null)
        {
            return Convert.ToHexStringLower(SHA256.HashData([]));
        }

        using var stream = part.Content.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(chunk, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// 本文をデコードする。転送エンコーディングをデコードしたストリームを、上限 + 1 バイトまでしか読まない。
    /// 文字コードは宣言どおりに厳密に復号し、不明・復号不能なら <see cref="MimeFailure.InvalidEncoding"/>（推測しない）。
    /// </summary>
    private static (MimeFailure Failure, string? Text) DecodeBody(TextPart part, int limit, CancellationToken cancellationToken)
    {
        if (part.Content is null)
        {
            return (MimeFailure.None, string.Empty);
        }

        byte[] data;
        try
        {
            using var stream = part.Content.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length + read > limit)
                {
                    return (MimeFailure.BodyTooLarge, null);
                }

                buffer.Write(chunk, 0, read);
            }

            data = buffer.ToArray();
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or NotSupportedException)
        {
            return (MimeFailure.InvalidEncoding, null);
        }

        var charset = string.IsNullOrWhiteSpace(part.ContentType.Charset) ? "us-ascii" : part.ContentType.Charset;
        try
        {
            var encoding = Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return (MimeFailure.None, encoding.GetString(data));
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException or NotSupportedException)
        {
            return (MimeFailure.InvalidEncoding, null);
        }
    }

    private static MailAddressList? ToAddressList(InternetAddressList list)
    {
        var addresses = new List<string>();
        var names = new List<string>();
        foreach (var mailbox in list.Mailboxes)
        {
            var name = mailbox.Name ?? string.Empty;
            if (HasLineBreakOrNul(mailbox.Address) || HasLineBreakOrNul(name))
            {
                return null;
            }

            addresses.Add(mailbox.Address);
            names.Add(name);
        }

        return new MailAddressList(addresses, names);
    }

    private static bool HasLineBreakOrNul(string value) => value.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0;
}
