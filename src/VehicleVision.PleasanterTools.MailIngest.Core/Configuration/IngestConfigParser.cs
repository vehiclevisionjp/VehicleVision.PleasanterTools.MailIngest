using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

/// <summary>
/// 設定 JSON（<c>schemaVersion: 1</c>）を読み、起動時に検査する。
/// </summary>
/// <remarks>
/// <para>設計文書 <c>メール対応づけ設定.md</c> の推奨案に沿う。確定仕様ではない。</para>
/// <para>ここは形と整合の検査だけ。接続・証明書の実検証・秘密の値の解決は行わない。
/// エラーと警告には設定値（秘密を含み得る）を載せない。</para>
/// </remarks>
public static partial class IngestConfigParser
{
    /// <summary>設定ファイルの大きさの上限。</summary>
    public const int MaxConfigBytes = 1024 * 1024;

    private static readonly string[] SecureDefaultPop3 = ["PLAIN", "LOGIN", "USER/PASS"];
    private static readonly string[] SecureDefaultImap = ["PLAIN", "LOGIN", "IMAP-LOGIN"];
    private static readonly string[] Pop3Mechanisms = ["PLAIN", "LOGIN", "USER/PASS", "APOP", "CRAM-MD5"];
    private static readonly string[] ImapMechanisms = ["PLAIN", "LOGIN", "IMAP-LOGIN", "CRAM-MD5"];
    private static readonly string[] PasswordLikeMechanisms = ["PLAIN", "LOGIN", "USER/PASS", "IMAP-LOGIN"];

    public static ConfigParseResult Parse(string json)
    {
        var issues = new ConfigIssues();
        IngestConfig? config = null;

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
        {
            issues.Error(string.Empty, "TooLarge", "設定が大きすぎる。");
            return new ConfigParseResult(null, issues.Errors, issues.Warnings);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            config = ParseRoot(document.RootElement, issues);
        }
        catch (JsonException)
        {
            issues.Error(string.Empty, "InvalidJson", "JSON として読めない。");
        }

        return new ConfigParseResult(config, issues.Errors, issues.Warnings);
    }

    private static IngestConfig? ParseRoot(JsonElement element, ConfigIssues issues)
    {
        var root = ObjectReader.Create(element, string.Empty, issues);
        if (root is null)
        {
            return null;
        }

        var version = root.Int("schemaVersion", required: true);
        if (version is not null && version != 1)
        {
            issues.Error("schemaVersion", "UnsupportedSchemaVersion", "未対応の schemaVersion。1 だけ対応する。");
        }

        var pleasanter = ParsePleasanter(root.Object("pleasanter", required: true), issues);
        var sources = new List<MailSourceConfig>();
        var sourcesRaw = root.Raw("sources");
        if (sourcesRaw is null)
        {
            issues.Error("sources", "Missing", "必須。");
        }
        else if (sourcesRaw.Value.ValueKind != JsonValueKind.Array)
        {
            issues.Error("sources", "InvalidType", "配列が必要。");
        }
        else
        {
            var index = 0;
            foreach (var item in sourcesRaw.Value.EnumerateArray())
            {
                var source = ParseSource(ObjectReader.Create(item, $"sources[{index}]", issues), issues);
                if (source is not null)
                {
                    sources.Add(source);
                }

                index++;
            }

            if (index == 0)
            {
                issues.Error("sources", "Empty", "1 件以上必要。");
            }
        }

        root.Finish();
        CheckCrossSource(sources, issues);

        return pleasanter is null ? null : new IngestConfig(version ?? 0, pleasanter, sources);
    }

    private static PleasanterDestinationConfig? ParsePleasanter(ObjectReader? reader, ConfigIssues issues)
    {
        if (reader is null)
        {
            return null;
        }

        var destinationId = reader.String("destinationId", required: true);
        CheckId(destinationId, reader.Join("destinationId"), issues);

        Uri? baseUrl = null;
        var baseUrlText = reader.String("baseUrl", required: true);
        if (baseUrlText is not null)
        {
            baseUrl = ValidateBaseUrl(baseUrlText, reader.Join("baseUrl"), issues);
        }

        var apiVersion = reader.Decimal("apiVersion", required: false, PleasanterOptions.DefaultApiVersion)!.Value;
        if (apiVersion <= 0)
        {
            issues.Error(reader.Join("apiVersion"), "OutOfRange", "正の数値が必要。");
        }

        var apiKeyVariable = reader.String("apiKeyEnvironmentVariable", required: true);
        CheckEnvironmentVariableName(apiKeyVariable, reader.Join("apiKeyEnvironmentVariable"), issues);

        var timeZone = reader.String("timeZone", required: true);
        if (timeZone is not null && !IsKnownTimeZone(timeZone))
        {
            issues.Error(reader.Join("timeZone"), "UnknownTimeZone", "このホストで解決できないタイムゾーン ID。");
        }

        var rps = reader.Decimal("requestsPerSecond", required: false, 1m)!.Value;
        if (rps <= 0)
        {
            issues.Error(reader.Join("requestsPerSecond"), "OutOfRange", "正の数値が必要。");
        }

        reader.Finish();
        if (destinationId is null || baseUrl is null || apiKeyVariable is null || timeZone is null)
        {
            return null;
        }

        return new PleasanterDestinationConfig(destinationId, baseUrl, apiVersion, apiKeyVariable, timeZone, rps);
    }

    private static Uri? ValidateBaseUrl(string text, string path, ConfigIssues issues)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            issues.Error(path, "InvalidUrl", "認証情報・クエリ・フラグメントを含まない http(s) の絶対 URL が必要。");
            return null;
        }

        if (uri.Scheme == "http" && !uri.IsLoopback)
        {
            issues.Error(path, "InsecureUrl", "http はループバックだけ許可する。API キーを送るため https が必要。");
            return null;
        }

        return uri;
    }

    private static bool IsKnownTimeZone(string id)
    {
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static MailSourceConfig? ParseSource(ObjectReader? reader, ConfigIssues issues)
    {
        if (reader is null)
        {
            return null;
        }

        var errorsBefore = issues.Errors.Count;
        var sourceId = reader.String("sourceId", required: true);
        CheckId(sourceId, reader.Join("sourceId"), issues);

        var protocol = ParseEnum(
            reader.String("protocol", required: true),
            reader.Join("protocol"),
            issues,
            ("pop3", MailProtocol.Pop3),
            ("imap", MailProtocol.Imap));

        var host = reader.String("host", required: true);
        if (host is not null && !HostRegex().IsMatch(host))
        {
            issues.Error(reader.Join("host"), "InvalidHost", "ホスト名は英数字・ハイフン・ドットだけ（スキームやパスを含めない）。");
        }

        var tls = ParseEnum(
            reader.String("tls", required: false, "Tls"),
            reader.Join("tls"),
            issues,
            ("None", TlsMode.None),
            ("StartTls", TlsMode.StartTls),
            ("Tls", TlsMode.Tls));

        var port = reader.Int("port", required: false);
        if (port is not null && port is < 1 or > 65535)
        {
            issues.Error(reader.Join("port"), "OutOfRange", "1〜65535 の整数が必要。");
        }

        port ??= DefaultPort(protocol, tls);

        var certificate = ParseCertificate(reader.Object("certificateValidation", required: false), tls, issues);
        var authentication = ParseAuthentication(reader.Object("authentication", required: true), protocol, tls, issues);

        var attachments = ParseAttachments(reader.Object("attachments", required: false), issues);
        var imap = ParseImap(reader, protocol, attachments, issues);
        var pop3 = ParsePop3(reader, protocol, attachments, issues);
        var limits = ParseLimits(reader.Object("limits", required: false), issues);
        var body = ParseBody(reader.Object("body", required: false), issues);
        var target = ParseTarget(reader.Object("target", required: true), issues);

        reader.Finish();

        if (tls == TlsMode.None && sourceId is not null)
        {
            issues.Warn(
                reader.Join("tls"),
                "PlaintextConnection",
                $"ソース {sourceId}: 平文接続。認証情報と本文が暗号化されない。信頼できるネットワーク内だけで使う。");
        }

        if (certificate is { Mode: not CertificateValidationMode.Strict } && sourceId is not null)
        {
            issues.Warn(
                reader.Join("certificateValidation"),
                "CertificateValidationRelaxed",
                $"ソース {sourceId}: 証明書検証を緩めている。中間者攻撃の危険がある。");
        }

        if (issues.Errors.Count > errorsBefore
            || sourceId is null || protocol is null || host is null || tls is null || port is null
            || certificate is null || authentication is null || attachments is null
            || limits is null || body is null || target is null)
        {
            return null;
        }

        return new MailSourceConfig(
            sourceId, protocol.Value, host, port.Value, tls.Value, certificate, authentication,
            imap, pop3, limits, body, attachments, target);
    }

    private static int? DefaultPort(MailProtocol? protocol, TlsMode? tls)
    {
        if (protocol is null || tls is null)
        {
            return null;
        }

        return (protocol.Value, tls.Value) switch
        {
            (MailProtocol.Pop3, TlsMode.Tls) => 995,
            (MailProtocol.Imap, TlsMode.Tls) => 993,
            (MailProtocol.Pop3, _) => 110,
            _ => 143,
        };
    }

    private static CertificateValidationConfig? ParseCertificate(ObjectReader? reader, TlsMode? tls, ConfigIssues issues)
    {
        if (reader is null)
        {
            return new CertificateValidationConfig(CertificateValidationMode.Strict, null);
        }

        var mode = ParseEnum(
            reader.String("mode", required: false, "Strict"),
            reader.Join("mode"),
            issues,
            ("Strict", CertificateValidationMode.Strict),
            ("AllowUntrusted", CertificateValidationMode.AllowUntrusted),
            ("Pinned", CertificateValidationMode.Pinned));
        var fingerprint = reader.String("sha256Fingerprint", required: false, defaultValue: null, allowNull: true);
        reader.Finish();

        if (mode is null)
        {
            return null;
        }

        if (tls == TlsMode.None && (mode != CertificateValidationMode.Strict || fingerprint is not null))
        {
            issues.Error(reader.Path, "CertificateSettingWithPlaintext", "tls が None のとき、証明書の例外設定・ピン指定はできない。");
        }
        else if (mode == CertificateValidationMode.Pinned)
        {
            if (fingerprint is null || !FingerprintRegex().IsMatch(fingerprint))
            {
                issues.Error(reader.Join("sha256Fingerprint"), "InvalidFingerprint", "Pinned には区切りなしの 64 桁の 16 進数が必要。");
            }
        }
        else if (fingerprint is not null)
        {
            issues.Error(reader.Join("sha256Fingerprint"), "FingerprintNotAllowed", "ピン指定は Pinned のときだけ。");
        }

        return new CertificateValidationConfig(mode.Value, fingerprint?.ToUpperInvariant());
    }

    private static AuthenticationConfig? ParseAuthentication(
        ObjectReader? reader,
        MailProtocol? protocol,
        TlsMode? tls,
        ConfigIssues issues)
    {
        if (reader is null)
        {
            return null;
        }

        var mode = reader.String("mode", required: true);
        if (mode is not null && mode != "password")
        {
            issues.Error(reader.Join("mode"), "UnsupportedAuthenticationMode", "password だけ対応する（OAuth2 は未決事項）。");
        }

        IReadOnlyList<string>? mechanisms = ReadStringArray(reader, "allowedMechanisms", issues);
        var allowInsecure = reader.Bool("allowInsecurePasswordAuthentication", false);
        var user = reader.String("userEnvironmentVariable", required: true);
        var password = reader.String("passwordEnvironmentVariable", required: true);
        CheckEnvironmentVariableName(user, reader.Join("userEnvironmentVariable"), issues);
        CheckEnvironmentVariableName(password, reader.Join("passwordEnvironmentVariable"), issues);
        reader.Finish();

        if (user is not null && password is not null && string.Equals(user, password, StringComparison.Ordinal))
        {
            issues.Error(reader.Path, "SameEnvironmentVariable", "ユーザー名とパスワードに同じ環境変数は使えない。");
        }

        if (protocol is null || tls is null)
        {
            return null;
        }

        var known = protocol == MailProtocol.Pop3 ? Pop3Mechanisms : ImapMechanisms;
        if (mechanisms is null)
        {
            if (tls == TlsMode.None)
            {
                issues.Error(
                    reader.Join("allowedMechanisms"),
                    "MechanismsRequiredForPlaintext",
                    "tls が None のとき、許可する認証方式の明示（非空）が必要。");
                return null;
            }

            mechanisms = protocol == MailProtocol.Pop3 ? SecureDefaultPop3 : SecureDefaultImap;
        }
        else
        {
            if (mechanisms.Count == 0)
            {
                issues.Error(reader.Join("allowedMechanisms"), "Empty", "空にはできない。");
            }

            foreach (var mechanism in mechanisms)
            {
                if (!known.Contains(mechanism, StringComparer.Ordinal))
                {
                    issues.Error(
                        reader.Join("allowedMechanisms"),
                        "UnknownMechanism",
                        "このプロトコルで許可していない・未評価の認証方式がある。");
                }
            }

            if (mechanisms.Distinct(StringComparer.Ordinal).Count() != mechanisms.Count)
            {
                issues.Error(reader.Join("allowedMechanisms"), "Duplicate", "認証方式が重複している。");
            }
        }

        if (tls == TlsMode.None
            && !allowInsecure
            && mechanisms.Any(m => PasswordLikeMechanisms.Contains(m, StringComparer.Ordinal)))
        {
            issues.Error(
                reader.Join("allowInsecurePasswordAuthentication"),
                "InsecurePasswordAuthenticationNotAllowed",
                "平文でパスワードを送る方式には allowInsecurePasswordAuthentication: true が必要。");
        }

        if (tls != TlsMode.None && allowInsecure)
        {
            issues.Error(
                reader.Join("allowInsecurePasswordAuthentication"),
                "InsecureFlagWithProtection",
                "allowInsecurePasswordAuthentication は tls が None のときだけ指定できる。");
        }

        if (user is null || password is null)
        {
            return null;
        }

        return new AuthenticationConfig(mechanisms, allowInsecure, user, password);
    }

    private static AttachmentsConfig? ParseAttachments(ObjectReader? reader, ConfigIssues issues)
    {
        if (reader is null)
        {
            return new AttachmentsConfig("metadataOnly", false);
        }

        var mode = reader.String("mode", required: false, "metadataOnly");
        if (mode != "metadataOnly")
        {
            issues.Error(reader.Join("mode"), "UnsupportedValue", "metadataOnly だけ対応する（添付の転送は未決事項）。");
        }

        var destructive = reader.Bool("allowDestructiveAfterSuccess", false);
        reader.Finish();
        return new AttachmentsConfig("metadataOnly", destructive);
    }

    private static ImapConfig? ParseImap(
        ObjectReader source,
        MailProtocol? protocol,
        AttachmentsConfig? attachments,
        ConfigIssues issues)
    {
        var reader = source.Object("imap", required: false);
        if (protocol == MailProtocol.Pop3)
        {
            if (reader is not null)
            {
                issues.Error(source.Join("imap"), "ProtocolMismatch", "pop3 のソースに imap 設定は置けない。");
            }

            return null;
        }

        var folder = "INBOX";
        var state = ImapSearchState.All;
        DateTimeOffset? since = null;
        var action = AfterSuccessAction.Leave;
        string? destination = null;

        if (reader is not null)
        {
            folder = reader.String("folder", required: false, "INBOX") ?? "INBOX";
            if (folder.Length == 0 || HasControlCharacter(folder))
            {
                issues.Error(reader.Join("folder"), "InvalidFolder", "空・制御文字を含むフォルダ名は使えない。");
            }

            var search = reader.Object("search", required: false);
            if (search is not null)
            {
                state = ParseEnum(
                    search.String("state", required: false, "all"),
                    search.Join("state"),
                    issues,
                    ("all", ImapSearchState.All),
                    ("unseen", ImapSearchState.Unseen)) ?? ImapSearchState.All;
                var sinceText = search.String("receivedSinceUtc", required: false, defaultValue: null, allowNull: true);
                if (sinceText is not null)
                {
                    if (!sinceText.EndsWith('Z')
                        || !DateTimeOffset.TryParse(
                            sinceText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        issues.Error(search.Join("receivedSinceUtc"), "InvalidDateTime", "末尾が Z の UTC 日時（ISO 8601）が必要。");
                    }
                    else
                    {
                        since = parsed;
                    }
                }

                search.Finish();
            }

            var after = reader.Object("afterSuccess", required: false);
            if (after is not null)
            {
                action = ParseEnum(
                    after.String("action", required: false, "leave"),
                    after.Join("action"),
                    issues,
                    ("leave", AfterSuccessAction.Leave),
                    ("markSeen", AfterSuccessAction.MarkSeen),
                    ("move", AfterSuccessAction.Move),
                    ("delete", AfterSuccessAction.Delete)) ?? AfterSuccessAction.Leave;
                destination = after.String("destinationFolder", required: false, defaultValue: null, allowNull: true);
                if (action == AfterSuccessAction.Move)
                {
                    if (string.IsNullOrEmpty(destination) || HasControlCharacter(destination))
                    {
                        issues.Error(after.Join("destinationFolder"), "DestinationRequired", "move には移動先フォルダが必要。");
                    }
                    else if (string.Equals(destination, folder, StringComparison.Ordinal))
                    {
                        issues.Error(after.Join("destinationFolder"), "SameFolder", "同じフォルダへは移動できない。");
                    }
                }
                else if (destination is not null)
                {
                    issues.Error(after.Join("destinationFolder"), "DestinationNotAllowed", "移動先は move のときだけ指定できる。");
                }

                CheckDestructive(action, attachments, after.Join("action"), issues);
                after.Finish();
            }

            reader.Finish();
        }

        return new ImapConfig(folder, state, since, action, destination);
    }

    private static Pop3Config? ParsePop3(
        ObjectReader source,
        MailProtocol? protocol,
        AttachmentsConfig? attachments,
        ConfigIssues issues)
    {
        var reader = source.Object("pop3", required: false);
        if (protocol == MailProtocol.Imap)
        {
            if (reader is not null)
            {
                issues.Error(source.Join("pop3"), "ProtocolMismatch", "imap のソースに pop3 設定は置けない。");
            }

            return null;
        }

        var action = AfterSuccessAction.Leave;
        if (reader is not null)
        {
            action = ParseEnum(
                reader.String("afterSuccess", required: false, "leave"),
                reader.Join("afterSuccess"),
                issues,
                ("leave", AfterSuccessAction.Leave),
                ("delete", AfterSuccessAction.Delete)) ?? AfterSuccessAction.Leave;
            CheckDestructive(action, attachments, reader.Join("afterSuccess"), issues);
            reader.Finish();
        }

        return new Pop3Config(action);
    }

    private static void CheckDestructive(
        AfterSuccessAction action,
        AttachmentsConfig? attachments,
        string path,
        ConfigIssues issues)
    {
        if (action is AfterSuccessAction.Move or AfterSuccessAction.Delete
            && attachments is { AllowDestructiveAfterSuccess: false })
        {
            issues.Error(
                path,
                "DestructiveNotAllowed",
                "添付は転送されないため、移動・削除には attachments.allowDestructiveAfterSuccess: true が必要。");
        }
    }

    private static LimitsConfig ParseLimits(ObjectReader? reader, ConfigIssues issues)
    {
        var d = LimitsConfig.Default;
        if (reader is null)
        {
            return d;
        }

        var messages = PositiveInt(reader, "messagesPerRun", d.MessagesPerRun, issues);
        var raw = PositiveLong(reader, "rawBytes", d.RawBytes, issues);
        var decoded = PositiveInt(reader, "decodedBodyBytes", d.DecodedBodyBytes, issues);
        var header = PositiveInt(reader, "headerBytes", d.HeaderBytes, issues);
        var depth = PositiveInt(reader, "mimeDepth", d.MimeDepth, issues);
        var parts = PositiveInt(reader, "mimeParts", d.MimeParts, issues);
        reader.Finish();
        return new LimitsConfig(messages, raw, decoded, header, depth, parts);
    }

    private static int PositiveInt(ObjectReader reader, string name, int defaultValue, ConfigIssues issues)
    {
        var value = reader.Int(name, required: false, defaultValue)!.Value;
        if (value <= 0)
        {
            issues.Error(reader.Join(name), "OutOfRange", "正の整数が必要。");
            return defaultValue;
        }

        return value;
    }

    private static long PositiveLong(ObjectReader reader, string name, long defaultValue, ConfigIssues issues)
    {
        var value = reader.Long(name, required: false, defaultValue)!.Value;
        if (value <= 0)
        {
            issues.Error(reader.Join(name), "OutOfRange", "正の整数が必要。");
            return defaultValue;
        }

        return value;
    }

    private static BodyConfig ParseBody(ObjectReader? reader, ConfigIssues issues)
    {
        if (reader is not null)
        {
            if (reader.String("selection", required: false, "preferPlain") != "preferPlain")
            {
                issues.Error(reader.Join("selection"), "UnsupportedValue", "preferPlain だけ対応する。");
            }

            if (reader.String("htmlFallback", required: false, "toText") != "toText")
            {
                issues.Error(reader.Join("htmlFallback"), "UnsupportedValue", "toText だけ対応する（HTML の生保存は採らない）。");
            }

            reader.Finish();
        }

        return new BodyConfig("preferPlain", "toText");
    }

    private static TargetConfig? ParseTarget(ObjectReader? reader, ConfigIssues issues)
    {
        if (reader is null)
        {
            return null;
        }

        var siteId = reader.Long("siteId", required: true);
        if (siteId is not null && siteId <= 0)
        {
            issues.Error(reader.Join("siteId"), "OutOfRange", "正の整数が必要。");
        }

        var tableKind = reader.String("tableKind", required: false, "results");
        if (tableKind != "results")
        {
            issues.Error(reader.Join("tableKind"), "UnsupportedValue", "results（記録テーブル）だけ対応する。");
        }

        var keyColumn = reader.String("externalKeyColumn", required: true);
        var projectionColumn = reader.String("projectionHashColumn", required: true);
        CheckClassColumn(keyColumn, reader.Join("externalKeyColumn"), issues);
        CheckClassColumn(projectionColumn, reader.Join("projectionHashColumn"), issues);
        if (keyColumn is not null && string.Equals(keyColumn, projectionColumn, StringComparison.Ordinal))
        {
            issues.Error(reader.Path, "ReservedColumnConflict", "外部キー列と投射ハッシュ列は別の列にする。");
        }

        var onExisting = reader.String("onExisting", required: false, "skip");
        if (onExisting != "skip")
        {
            issues.Error(reader.Join("onExisting"), "UnsupportedValue", "skip だけ対応する（updateMapped は未決事項）。");
        }

        var revision = reader.Int("mappingRevision", required: false, 1)!.Value;
        if (revision < 1)
        {
            issues.Error(reader.Join("mappingRevision"), "OutOfRange", "1 以上の整数が必要。");
        }

        var mappings = ParseMappings(reader, issues);
        var fixedValues = ParseFixedValues(reader, issues);
        reader.Finish();

        if (siteId is null || keyColumn is null || projectionColumn is null)
        {
            return null;
        }

        return new TargetConfig(
            siteId.Value, "results", keyColumn, projectionColumn, "skip", revision, mappings, fixedValues);
    }

    private static List<MappingConfig> ParseMappings(ObjectReader target, ConfigIssues issues)
    {
        var result = new List<MappingConfig>();
        var raw = target.Raw("mappings");
        if (raw is null)
        {
            issues.Error(target.Join("mappings"), "Missing", "必須。");
            return result;
        }

        if (raw.Value.ValueKind != JsonValueKind.Array)
        {
            issues.Error(target.Join("mappings"), "InvalidType", "配列が必要。");
            return result;
        }

        var index = 0;
        foreach (var item in raw.Value.EnumerateArray())
        {
            var reader = ObjectReader.Create(item, $"{target.Join("mappings")}[{index}]", issues);
            index++;
            if (reader is null)
            {
                continue;
            }

            var source = reader.String("source", required: true);
            var column = reader.String("target", required: true);
            var defaultValue = reader.String("default", required: false);
            var join = reader.String("join", required: false);
            reader.Finish();
            if (source is not null && column is not null)
            {
                result.Add(new MappingConfig(source, column, defaultValue, join));
            }
        }

        return result;
    }

    private static Dictionary<string, object> ParseFixedValues(ObjectReader target, ConfigIssues issues)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        var raw = target.Raw("fixedValues");
        if (raw is null)
        {
            return result;
        }

        if (raw.Value.ValueKind != JsonValueKind.Object)
        {
            issues.Error(target.Join("fixedValues"), "InvalidType", "オブジェクトが必要。");
            return result;
        }

        foreach (var property in raw.Value.EnumerateObject())
        {
            var path = $"{target.Join("fixedValues")}.{property.Name}";
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    result[property.Name] = property.Value.GetString()!;
                    break;
                case JsonValueKind.Number when property.Value.TryGetDecimal(out var number):
                    result[property.Name] = number;
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    result[property.Name] = property.Value.GetBoolean();
                    break;
                default:
                    issues.Error(path, "InvalidType", "文字列・数値・真偽値だけ指定できる。");
                    break;
            }
        }

        return result;
    }

    private static void CheckCrossSource(List<MailSourceConfig> sources, ConfigIssues issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!seen.Add(source.SourceId))
            {
                issues.Error("sources", "DuplicateSourceId", "sourceId が重複している。");
            }
        }

        foreach (var group in sources.GroupBy(s => s.Target.SiteId))
        {
            var columns = group
                .Select(s => (s.Target.ExternalKeyColumn, s.Target.ProjectionHashColumn))
                .Distinct()
                .Count();
            if (columns > 1)
            {
                issues.Error("sources", "InconsistentReservedColumns", "同じサイトへ異なる予約列を指定できない。");
            }
        }
    }

    private static T? ParseEnum<T>(string? text, string path, ConfigIssues issues, params (string Name, T Value)[] values)
        where T : struct
    {
        if (text is null)
        {
            return null;
        }

        foreach (var (name, value) in values)
        {
            if (string.Equals(name, text, StringComparison.Ordinal))
            {
                return value;
            }
        }

        issues.Error(path, "UnknownValue", $"未知の値。次のいずれか: {string.Join(", ", values.Select(v => v.Name))}。");
        return null;
    }

    private static List<string>? ReadStringArray(ObjectReader reader, string name, ConfigIssues issues)
    {
        var raw = reader.Raw(name);
        if (raw is null)
        {
            return null;
        }

        if (raw.Value.ValueKind != JsonValueKind.Array)
        {
            issues.Error(reader.Join(name), "InvalidType", "配列が必要。");
            return null;
        }

        var result = new List<string>();
        foreach (var item in raw.Value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                issues.Error(reader.Join(name), "InvalidType", "文字列の配列が必要。");
                return null;
            }

            result.Add(item.GetString()!);
        }

        return result;
    }

    private static void CheckId(string? value, string path, ConfigIssues issues)
    {
        if (value is not null && !IdRegex().IsMatch(value))
        {
            issues.Error(path, "InvalidId", "英数字で始まり、英数字・ドット・アンダースコア・ハイフンだけの 64 文字以内。");
        }
    }

    private static void CheckEnvironmentVariableName(string? value, string path, ConfigIssues issues)
    {
        if (value is not null && !EnvironmentVariableRegex().IsMatch(value))
        {
            issues.Error(
                path,
                "InvalidEnvironmentVariableName",
                "環境変数の名前だけを書く（秘密そのものや ${ENV} 展開式は置けない）。");
        }
    }

    private static void CheckClassColumn(string? value, string path, ConfigIssues issues)
    {
        if (value is not null && !ClassColumnRegex().IsMatch(value))
        {
            issues.Error(path, "InvalidColumn", "Class 列（ClassA〜ClassZ など）の名前が必要。");
        }
    }

    private static bool HasControlCharacter(string value) => value.Any(char.IsControl);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EnvironmentVariableRegex();

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostRegex();

    [GeneratedRegex("^[0-9A-Fa-f]{64}$")]
    private static partial Regex FingerprintRegex();

    [GeneratedRegex("^Class([A-Z]|[0-9]{3})$")]
    private static partial Regex ClassColumnRegex();
}
