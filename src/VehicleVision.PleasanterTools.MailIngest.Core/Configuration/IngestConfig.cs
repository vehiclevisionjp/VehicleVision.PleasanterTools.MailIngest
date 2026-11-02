namespace VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

/// <summary>受信プロトコル。</summary>
public enum MailProtocol
{
    Pop3,
    Imap,
}

/// <summary>接続保護。MailKit の列挙値名は外部設定に使わない。</summary>
public enum TlsMode
{
    /// <summary>平文。明示設定だけ。</summary>
    None,

    /// <summary>STLS / STARTTLS で同じ接続を昇格。</summary>
    StartTls,

    /// <summary>暗黙 TLS。省略時の既定。</summary>
    Tls,
}

/// <summary>証明書検証。</summary>
public enum CertificateValidationMode
{
    Strict,
    AllowUntrusted,
    Pinned,
}

/// <summary>IMAP の検索対象。</summary>
public enum ImapSearchState
{
    All,
    Unseen,
}

/// <summary>取り込み成功後の操作。</summary>
public enum AfterSuccessAction
{
    Leave,
    MarkSeen,
    Move,
    Delete,
}

/// <summary>設定全体（<c>schemaVersion: 1</c>）。</summary>
public sealed record IngestConfig(
    int SchemaVersion,
    PleasanterDestinationConfig Pleasanter,
    IReadOnlyList<MailSourceConfig> Sources);

/// <summary>Pleasanter 接続先。API キーは環境変数名だけを持つ。</summary>
public sealed record PleasanterDestinationConfig(
    string DestinationId,
    Uri BaseUrl,
    decimal ApiVersion,
    string ApiKeyEnvironmentVariable,
    string TimeZone,
    decimal RequestsPerSecond);

/// <summary>取り込み元（アカウント + 対象 + 送り先の 1 定義）。</summary>
public sealed record MailSourceConfig(
    string SourceId,
    MailProtocol Protocol,
    string Host,
    int Port,
    TlsMode Tls,
    CertificateValidationConfig CertificateValidation,
    AuthenticationConfig Authentication,
    ImapConfig? Imap,
    Pop3Config? Pop3,
    LimitsConfig Limits,
    BodyConfig Body,
    AttachmentsConfig Attachments,
    TargetConfig Target);

public sealed record CertificateValidationConfig(CertificateValidationMode Mode, string? Sha256Fingerprint);

public sealed record AuthenticationConfig(
    IReadOnlyList<string> AllowedMechanisms,
    bool AllowInsecurePasswordAuthentication,
    string UserEnvironmentVariable,
    string PasswordEnvironmentVariable);

public sealed record ImapConfig(
    string Folder,
    ImapSearchState SearchState,
    DateTimeOffset? ReceivedSinceUtc,
    AfterSuccessAction AfterSuccess,
    string? DestinationFolder);

public sealed record Pop3Config(AfterSuccessAction AfterSuccess);

/// <summary>受信の上限。既定は設計文書の「初期候補」であり、確定値ではない。</summary>
public sealed record LimitsConfig(
    int MessagesPerRun,
    long RawBytes,
    int DecodedBodyBytes,
    int HeaderBytes,
    int MimeDepth,
    int MimeParts)
{
    public static LimitsConfig Default { get; } = new(100, 26214400, 1048576, 262144, 32, 100);
}

public sealed record BodyConfig(string Selection, string HtmlFallback);

public sealed record AttachmentsConfig(string Mode, bool AllowDestructiveAfterSuccess);

/// <summary>Pleasanter への送り先と対応づけ。</summary>
public sealed record TargetConfig(
    long SiteId,
    string TableKind,
    string ExternalKeyColumn,
    string ProjectionHashColumn,
    string OnExisting,
    int MappingRevision,
    IReadOnlyList<MappingConfig> Mappings,
    IReadOnlyDictionary<string, object> FixedValues);

/// <summary>1 件の対応づけ。評価規則は別途検証する。</summary>
public sealed record MappingConfig(string Source, string Target, string? Default, string? Join);
