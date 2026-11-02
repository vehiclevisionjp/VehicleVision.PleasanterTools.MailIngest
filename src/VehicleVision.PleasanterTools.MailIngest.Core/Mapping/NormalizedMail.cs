namespace VehicleVision.PleasanterTools.MailIngest.Core.Mapping;

/// <summary>差出人・宛先などのアドレス群。アドレスと表示名は別の配列で持つ。</summary>
/// <param name="Addresses">解析済みのアドレス。</param>
/// <param name="Names">表示名。名前が無い要素は空文字（アドレスと同じ順序・長さ）。</param>
public sealed record MailAddressList(IReadOnlyList<string> Addresses, IReadOnlyList<string> Names)
{
    public static MailAddressList Empty { get; } = new([], []);
}

/// <summary>
/// MIME の解析後に得られる、取り込みの入力となる正規化メール。
/// </summary>
/// <remarks>
/// <para>ここは MIME 解析や文字コード変換をしない（それらは受信実装の責務）。
/// 値は解析済みで、件名・アドレス・Message-ID・添付名は単一行であること。</para>
/// <para><c>SentAt</c> が欠落・不正なら <c>null</c>。処理時刻へ黙って置換しない。
/// <c>ReceivedAt</c> は IMAP の INTERNALDATE で、POP3 は <c>null</c>。</para>
/// </remarks>
public sealed record NormalizedMail
{
    public string? Subject { get; init; }

    public string? BodyText { get; init; }

    public MailAddressList From { get; init; } = MailAddressList.Empty;

    public MailAddressList To { get; init; } = MailAddressList.Empty;

    public MailAddressList Cc { get; init; } = MailAddressList.Empty;

    public string? MessageId { get; init; }

    public DateTimeOffset? SentAt { get; init; }

    public DateTimeOffset? ReceivedAt { get; init; }

    public DateTimeOffset FetchedAt { get; init; }

    public int AttachmentCount { get; init; }

    public IReadOnlyList<string> AttachmentNames { get; init; } = [];
}
