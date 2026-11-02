using System.Globalization;
using VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Identity;

/// <summary>Message-ID の検証と正規化。</summary>
/// <remarks>
/// 外側の山括弧と前後の空白だけを正規化する。ローカル部の小文字化・Unicode 正規化などの過剰な同一視はしない。
/// 複数の Message-ID・制御文字・内部の空白や山括弧を含むものは拒否する（隔離対象）。
/// </remarks>
public static class MessageIdNormalizer
{
    /// <summary>Message-ID の長さの上限（RFC 5322 の行長上限に合わせる）。</summary>
    public const int MaxLength = 998;

    public static bool TryNormalize(string? raw, out string messageId)
    {
        messageId = string.Empty;
        if (raw is null)
        {
            return false;
        }

        var text = raw.Trim(' ', '\t');
        if (text.Length >= 2 && text[0] == '<' && text[^1] == '>')
        {
            text = text[1..^1];
        }

        if (text.Length == 0
            || text.Length > MaxLength
            || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>'))
        {
            return false;
        }

        messageId = text;
        return true;
    }
}

/// <summary>重複排除の外部キー（Pleasanter の専用列へ保存する 64 桁の識別値）。</summary>
public static class ExternalKey
{
    /// <summary>キー方式の版。方式を変えるときは版を上げる。</summary>
    public const string Version = "mailingest-external-key-v1";

    /// <summary>
    /// 接続先識別子・siteId・sourceId・Message-ID から作る。
    /// 可変な URL・パスワードは含めない。別ソース・別サイト・別接続先の同じ Message-ID は別のキーになる。
    /// </summary>
    /// <param name="messageId"><see cref="MessageIdNormalizer"/> で正規化済みの値。</param>
    public static string Create(string destinationId, long siteId, string sourceId, string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationId);
        ArgumentException.ThrowIfNullOrEmpty(sourceId);
        ArgumentException.ThrowIfNullOrEmpty(messageId);

        using var hasher = new CanonicalHasher(Version);
        hasher.Add(destinationId);
        hasher.Add(siteId.ToString(CultureInfo.InvariantCulture));
        hasher.Add(sourceId);
        hasher.Add(messageId);
        return hasher.Finish();
    }
}

/// <summary>同じメールかどうかを見る内容ハッシュ。DB にだけ保持し、Pleasanter へは送らない。</summary>
public static class ContentHash
{
    public const string Version = "mailingest-content-v1";

    /// <summary>
    /// Message-ID・件名・差出人・宛先・本文・送信日・添付情報から計算する。
    /// <c>FetchedAt</c>・<c>ReceivedAt</c>・状態・試行時刻・IMAP の既読フラグなど、
    /// 処理の都合で変わる値は含めない。
    /// </summary>
    /// <param name="mail">正規化メール。</param>
    /// <param name="attachmentDigests">
    /// 添付パートの内容ダイジェスト（受信上限内のストリームから取った SHA-256 など）。
    /// 同じ Message-ID で添付だけが異なるメールを見逃さないために含める。無ければ空。
    /// </param>
    public static string Compute(NormalizedMail mail, IReadOnlyList<string>? attachmentDigests = null)
    {
        ArgumentNullException.ThrowIfNull(mail);

        using var hasher = new CanonicalHasher(Version);
        hasher.Add(mail.MessageId);
        hasher.Add(mail.Subject);
        AddAddresses(hasher, mail.From);
        AddAddresses(hasher, mail.To);
        AddAddresses(hasher, mail.Cc);
        hasher.Add(mail.BodyText);
        hasher.Add(mail.SentAt?.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture));
        hasher.AddCount(mail.AttachmentCount);
        hasher.AddList(mail.AttachmentNames);
        hasher.AddList(attachmentDigests ?? []);
        return hasher.Finish();
    }

    private static void AddAddresses(CanonicalHasher hasher, MailAddressList list)
    {
        hasher.AddList(list.Addresses);
        hasher.AddList(list.Names);
    }
}

/// <summary>
/// 実際に管理する列の型付き正規値から作る投射ハッシュ。手動編集の検出や不要な更新の回避に使う。
/// </summary>
/// <remarks>メール内容そのものの <see cref="ContentHash"/> とは別物で、混同しない。</remarks>
public static class ProjectionHash
{
    public const string Version = "mailingest-projection-v1";

    /// <summary>
    /// 列名の昇順（序数比較）で列と値を符号化する。予約列（外部キー・投射ハッシュ自身の保存列）は入力から除く。
    /// </summary>
    /// <param name="values"><see cref="MappedRecord.Values"/>。</param>
    /// <param name="mappingRevision">対応づけの版。上げるとハッシュが変わる。</param>
    /// <param name="excludedColumns">除外する列（外部キー列・投射ハッシュ列）。</param>
    public static string Compute(
        IReadOnlyDictionary<string, object> values,
        int mappingRevision,
        IReadOnlyCollection<string> excludedColumns)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(excludedColumns);

        using var hasher = new CanonicalHasher(Version);
        hasher.Add(mappingRevision.ToString(CultureInfo.InvariantCulture));

        var included = values
            .Where(p => !excludedColumns.Contains(p.Key, StringComparer.Ordinal))
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToList();
        hasher.AddCount(included.Count);
        foreach (var (column, value) in included)
        {
            hasher.Add(column);
            hasher.Add(Canonical(value));
        }

        return hasher.Finish();
    }

    private static string Canonical(object value) => value switch
    {
        string text => "s:" + text,
        long number => "i:" + number.ToString(CultureInfo.InvariantCulture),
        decimal number => "n:" + Normalize(number).ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "b:true" : "b:false",
        DateTimeOffset date => "d:" + date.ToUniversalTime().UtcTicks.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("対応外の値の型。", nameof(value)),
    };

    /// <summary>末尾のゼロを除き、1.0 と 1 を同じ表現にする。</summary>
    private static decimal Normalize(decimal value) => value / 1.000000000000000000000000000000000m;
}

/// <summary>既存レコードとの突き合わせ結果。</summary>
public enum DuplicateOutcome
{
    /// <summary>同じ外部キーが無い。新規として扱う。</summary>
    New,

    /// <summary>同一キー・同一内容。再作成しない。</summary>
    Skip,

    /// <summary>同一キーだが内容が違う。自動更新せず保留し、人が判断する。</summary>
    Conflict,
}

public static class DuplicateJudge
{
    /// <summary>
    /// 同一キー・同一内容は skip、同一キー・別内容は競合。先着の内容を後着で上書きしない。
    /// </summary>
    /// <param name="existingContentHash">既に記録された内容ハッシュ。無ければ <c>null</c>。</param>
    /// <param name="incomingContentHash">今回のメールの内容ハッシュ。</param>
    public static DuplicateOutcome Judge(string? existingContentHash, string incomingContentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(incomingContentHash);

        if (existingContentHash is null)
        {
            return DuplicateOutcome.New;
        }

        return string.Equals(existingContentHash, incomingContentHash, StringComparison.Ordinal)
            ? DuplicateOutcome.Skip
            : DuplicateOutcome.Conflict;
    }
}
