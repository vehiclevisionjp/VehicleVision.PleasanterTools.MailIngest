using System.Text.Json.Nodes;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

public sealed class IngestConfigParserTests
{
    private const string ValidJson = """
        {
          "schemaVersion": 1,
          "pleasanter": {
            "destinationId": "primary-pleasanter",
            "baseUrl": "https://pleasanter.example.invalid",
            "apiVersion": 1.1,
            "apiKeyEnvironmentVariable": "MAILINGEST_PLEASANTER_API_KEY",
            "timeZone": "Asia/Tokyo",
            "requestsPerSecond": 1
          },
          "sources": [
            {
              "sourceId": "support-imap",
              "protocol": "imap",
              "host": "imap.example.invalid",
              "tls": "Tls",
              "authentication": {
                "mode": "password",
                "userEnvironmentVariable": "MAILINGEST_SUPPORT_USER",
                "passwordEnvironmentVariable": "MAILINGEST_SUPPORT_PASSWORD"
              },
              "imap": {
                "folder": "INBOX",
                "search": { "state": "all", "receivedSinceUtc": null },
                "afterSuccess": { "action": "leave", "destinationFolder": null }
              },
              "target": {
                "siteId": 12345,
                "tableKind": "results",
                "externalKeyColumn": "ClassZ",
                "projectionHashColumn": "ClassY",
                "onExisting": "skip",
                "mappingRevision": 1,
                "mappings": [
                  { "source": "subject", "target": "Title", "default": "件名なし" },
                  { "source": "from.addresses", "target": "ClassA", "join": "; " }
                ],
                "fixedValues": { "ClassB": "メール", "Status": 100 }
              }
            },
            {
              "sourceId": "support-pop3",
              "protocol": "pop3",
              "host": "pop.example.invalid",
              "tls": "StartTls",
              "pop3": { "afterSuccess": "leave" },
              "authentication": {
                "mode": "password",
                "userEnvironmentVariable": "MAILINGEST_LEGACY_USER",
                "passwordEnvironmentVariable": "MAILINGEST_LEGACY_PASSWORD"
              },
              "target": {
                "siteId": 12346,
                "externalKeyColumn": "ClassZ",
                "projectionHashColumn": "ClassY",
                "mappings": [ { "source": "subject", "target": "Title" } ]
              }
            }
          ]
        }
        """;

    /// <summary>既定の JSON を JsonNode で加工し、1 か所だけ変える。</summary>
    private static ConfigParseResult ParseWith(Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(ValidJson)!;
        edit(node);
        return IngestConfigParser.Parse(node.ToJsonString());
    }

    private static JsonNode Source(JsonNode root, int index = 0) => root["sources"]![index]!;

    private static void AssertError(ConfigParseResult result, string code, string? pathPart = null)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Config);
        Assert.Contains(
            result.Errors,
            e => e.Code == code && (pathPart is null || e.Path.Contains(pathPart, StringComparison.Ordinal)));
    }

    [Fact]
    public void 設計文書の例は検査を通り既定値が補われる()
    {
        var result = IngestConfigParser.Parse(ValidJson);

        Assert.True(result.IsValid, string.Join(";", result.Errors.Select(e => e.Code + e.Path)));
        var config = result.Config!;
        Assert.Equal(2, config.Sources.Count);
        var imap = config.Sources[0];
        Assert.Equal(TlsMode.Tls, imap.Tls);
        Assert.Equal(993, imap.Port);
        Assert.Equal(LimitsConfig.Default, imap.Limits);
        Assert.Equal(["PLAIN", "LOGIN", "IMAP-LOGIN"], imap.Authentication.AllowedMechanisms);
        Assert.Equal(CertificateValidationMode.Strict, imap.CertificateValidation.Mode);
        Assert.Equal(2, imap.Target.Mappings.Count);
        var pop3 = config.Sources[1];
        Assert.Equal(110, pop3.Port);
        Assert.Equal(["PLAIN", "LOGIN", "USER/PASS"], pop3.Authentication.AllowedMechanisms);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("None", "pop3", 110)]
    [InlineData("StartTls", "pop3", 110)]
    [InlineData("Tls", "pop3", 995)]
    [InlineData("None", "imap", 143)]
    [InlineData("StartTls", "imap", 143)]
    [InlineData("Tls", "imap", 993)]
    public void 接続保護ごとの既定ポートを補う(string tls, string protocol, int expectedPort)
    {
        var result = ParseWith(root =>
        {
            var source = Source(root);
            source["protocol"] = protocol;
            source["tls"] = tls;
            source["authentication"]!["allowedMechanisms"] = new JsonArray(protocol == "pop3" ? "USER/PASS" : "IMAP-LOGIN");
            source["authentication"]!["allowInsecurePasswordAuthentication"] = tls == "None";
            source.AsObject().Remove("imap");
            source.AsObject().Remove("pop3");
        });

        Assert.True(result.IsValid, string.Join(";", result.Errors.Select(e => e.Code + e.Path)));
        Assert.Equal(expectedPort, result.Config!.Sources[0].Port);
    }

    [Fact]
    public void tls_を省略すると_Tls_になる()
    {
        var result = ParseWith(root => Source(root).AsObject().Remove("tls"));

        Assert.True(result.IsValid);
        Assert.Equal(TlsMode.Tls, result.Config!.Sources[0].Tls);
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("Auto")]
    [InlineData("StartTlsWhenAvailable")]
    [InlineData("none")]
    public void 未知の_tls_は拒否する(string value)
    {
        AssertError(ParseWith(root => Source(root)["tls"] = value), "UnknownValue", "tls");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void ポート範囲外は拒否する(int port)
    {
        AssertError(ParseWith(root => Source(root)["port"] = port), "OutOfRange", "port");
    }

    [Fact]
    public void 平文は警告を出し_認証方式の明示とフラグを求める()
    {
        var noMechanisms = ParseWith(root => Source(root)["tls"] = "None");
        AssertError(noMechanisms, "MechanismsRequiredForPlaintext");

        var noFlag = ParseWith(root =>
        {
            Source(root)["tls"] = "None";
            Source(root)["authentication"]!["allowedMechanisms"] = new JsonArray("IMAP-LOGIN");
        });
        AssertError(noFlag, "InsecurePasswordAuthenticationNotAllowed");

        var ok = ParseWith(root =>
        {
            Source(root)["tls"] = "None";
            Source(root)["authentication"]!["allowedMechanisms"] = new JsonArray("IMAP-LOGIN");
            Source(root)["authentication"]!["allowInsecurePasswordAuthentication"] = true;
        });
        Assert.True(ok.IsValid, string.Join(";", ok.Errors.Select(e => e.Code)));
        var warning = Assert.Single(ok.Warnings);
        Assert.Equal("PlaintextConnection", warning.Code);
        Assert.Contains("support-imap", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 保護付きでの_allowInsecure_は拒否する()
    {
        AssertError(
            ParseWith(root => Source(root)["authentication"]!["allowInsecurePasswordAuthentication"] = true),
            "InsecureFlagWithProtection");
    }

    [Theory]
    [InlineData("imap", "APOP")]
    [InlineData("imap", "USER/PASS")]
    [InlineData("imap", "XOAUTH2")]
    [InlineData("pop3", "IMAP-LOGIN")]
    [InlineData("pop3", "plain")]
    public void プロトコルに合わない_未評価の認証方式は拒否する(string protocol, string mechanism)
    {
        var result = ParseWith(root =>
        {
            var source = Source(root);
            source["protocol"] = protocol;
            source.AsObject().Remove("imap");
            source["authentication"]!["allowedMechanisms"] = new JsonArray(mechanism);
        });

        AssertError(result, "UnknownMechanism");
    }

    [Fact]
    public void APOP_は_POP3_で明示すれば許可し_フラグは不要()
    {
        var result = ParseWith(root =>
        {
            var source = Source(root, 1);
            source["tls"] = "None";
            source["authentication"]!["allowedMechanisms"] = new JsonArray("APOP");
        });

        Assert.True(result.IsValid, string.Join(";", result.Errors.Select(e => e.Code)));
    }

    [Fact]
    public void 証明書検証_Pinned_はピンが必須で_Strict_ではピンを禁止する()
    {
        var fingerprint = new string('a', 64);

        AssertError(
            ParseWith(root => Source(root)["certificateValidation"] = new JsonObject { ["mode"] = "Pinned" }),
            "InvalidFingerprint");
        AssertError(
            ParseWith(root => Source(root)["certificateValidation"] = new JsonObject
            {
                ["mode"] = "Pinned",
                ["sha256Fingerprint"] = "abc",
            }),
            "InvalidFingerprint");
        AssertError(
            ParseWith(root => Source(root)["certificateValidation"] = new JsonObject
            {
                ["mode"] = "Strict",
                ["sha256Fingerprint"] = fingerprint,
            }),
            "FingerprintNotAllowed");
        AssertError(
            ParseWith(root => Source(root)["certificateValidation"] = new JsonObject
            {
                ["mode"] = "AllowUntrusted",
                ["sha256Fingerprint"] = fingerprint,
            }),
            "FingerprintNotAllowed");

        var ok = ParseWith(root => Source(root)["certificateValidation"] = new JsonObject
        {
            ["mode"] = "Pinned",
            ["sha256Fingerprint"] = fingerprint,
        });
        Assert.True(ok.IsValid);
        Assert.Equal(new string('A', 64), ok.Config!.Sources[0].CertificateValidation.Sha256Fingerprint);
        Assert.Contains(ok.Warnings, w => w.Code == "CertificateValidationRelaxed");
    }

    [Fact]
    public void 平文で証明書の例外設定は拒否する()
    {
        var result = ParseWith(root =>
        {
            Source(root)["tls"] = "None";
            Source(root)["authentication"]!["allowedMechanisms"] = new JsonArray("IMAP-LOGIN");
            Source(root)["authentication"]!["allowInsecurePasswordAuthentication"] = true;
            Source(root)["certificateValidation"] = new JsonObject { ["mode"] = "AllowUntrusted" };
        });

        AssertError(result, "CertificateSettingWithPlaintext");
    }

    [Fact]
    public void 未知のプロパティ_未知の版_重複ソースは拒否する()
    {
        AssertError(ParseWith(root => root["extra"] = 1), "UnknownProperty", "extra");
        AssertError(ParseWith(root => Source(root)["unknown"] = 1), "UnknownProperty", "sources[0].unknown");
        AssertError(ParseWith(root => root["schemaVersion"] = 2), "UnsupportedSchemaVersion");
        AssertError(
            ParseWith(root => Source(root, 1)["sourceId"] = "support-imap"),
            "DuplicateSourceId");
    }

    [Fact]
    public void JSON_として不正な入力は_InvalidJson_になる()
    {
        var result = IngestConfigParser.Parse("{ \"schemaVersion\": 1, ");

        Assert.Contains(result.Errors, e => e.Code == "InvalidJson");
    }

    [Fact]
    public void 重複したプロパティは拒否する()
    {
        var json = ValidJson.Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1, \"schemaVersion\": 1,", StringComparison.Ordinal);

        Assert.Contains(IngestConfigParser.Parse(json).Errors, e => e.Code == "DuplicateProperty");
    }

    [Fact]
    public void プロトコルと食い違うセクションは拒否する()
    {
        AssertError(
            ParseWith(root => Source(root)["pop3"] = new JsonObject { ["afterSuccess"] = "leave" }),
            "ProtocolMismatch");
        AssertError(
            ParseWith(root => Source(root, 1)["imap"] = new JsonObject()),
            "ProtocolMismatch");
    }

    [Fact]
    public void 秘密そのものや展開式は環境変数名として拒否し_エラーに値を載せない()
    {
        var secret = "${MY_SECRET}-hunter2";
        var result = ParseWith(root => Source(root)["authentication"]!["passwordEnvironmentVariable"] = secret);

        AssertError(result, "InvalidEnvironmentVariableName");
        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void ユーザーとパスワードに同じ環境変数は使えない()
    {
        AssertError(
            ParseWith(root => Source(root)["authentication"]!["userEnvironmentVariable"] = "MAILINGEST_SUPPORT_PASSWORD"),
            "SameEnvironmentVariable");
    }

    [Theory]
    [InlineData("move", null, "DestinationRequired")]
    [InlineData("move", "INBOX", "SameFolder")]
    [InlineData("leave", "Archive", "DestinationNotAllowed")]
    [InlineData("expunge", null, "UnknownValue")]
    public void IMAP_の受信後操作の整合を検査する(string action, string? destination, string code)
    {
        var result = ParseWith(root =>
        {
            Source(root)["attachments"] = new JsonObject { ["allowDestructiveAfterSuccess"] = true };
            Source(root)["imap"]!["afterSuccess"] = new JsonObject
            {
                ["action"] = action,
                ["destinationFolder"] = destination,
            };
        });

        AssertError(result, code);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    public void 破壊的な受信後操作は添付未転送を承知した明示設定が要る(string action)
    {
        var result = ParseWith(root => Source(root)["imap"]!["afterSuccess"] = new JsonObject
        {
            ["action"] = action,
            ["destinationFolder"] = "Done",
        });

        AssertError(result, "DestructiveNotAllowed");
    }

    [Fact]
    public void 破壊的操作は明示許可があれば通る()
    {
        var result = ParseWith(root =>
        {
            Source(root, 1)["attachments"] = new JsonObject { ["allowDestructiveAfterSuccess"] = true };
            Source(root, 1)["pop3"] = new JsonObject { ["afterSuccess"] = "delete" };
        });

        Assert.True(result.IsValid, string.Join(";", result.Errors.Select(e => e.Code)));
        Assert.Equal(AfterSuccessAction.Delete, result.Config!.Sources[1].Pop3!.AfterSuccess);
    }

    [Fact]
    public void 未対応の値は未決事項として拒否する()
    {
        AssertError(ParseWith(root => Source(root)["target"]!["onExisting"] = "updateMapped"), "UnsupportedValue");
        AssertError(ParseWith(root => Source(root)["target"]!["tableKind"] = "issues"), "UnsupportedValue");
        AssertError(
            ParseWith(root => Source(root)["attachments"] = new JsonObject { ["mode"] = "upload" }),
            "UnsupportedValue");
        AssertError(
            ParseWith(root => Source(root)["authentication"]!["mode"] = "oauth2"),
            "UnsupportedAuthenticationMode");
    }

    [Fact]
    public void 予約列の不整合を拒否する()
    {
        AssertError(
            ParseWith(root => Source(root)["target"]!["projectionHashColumn"] = "ClassZ"),
            "ReservedColumnConflict");
        AssertError(
            ParseWith(root => Source(root)["target"]!["externalKeyColumn"] = "Title"),
            "InvalidColumn");
        AssertError(
            ParseWith(root =>
            {
                Source(root, 1)["target"]!["siteId"] = 12345;
                Source(root, 1)["target"]!["externalKeyColumn"] = "ClassX";
            }),
            "InconsistentReservedColumns");
    }

    [Theory]
    [InlineData("http://pleasanter.example.invalid")]
    [InlineData("https://user:pw@pleasanter.example.invalid")]
    [InlineData("https://pleasanter.example.invalid/?a=b")]
    [InlineData("not a url")]
    public void Pleasanter_URL_は安全な形だけ許可する(string url)
    {
        AssertError(ParseWith(root => root["pleasanter"]!["baseUrl"] = url), url.StartsWith("http:", StringComparison.Ordinal) ? "InsecureUrl" : "InvalidUrl");
    }

    [Fact]
    public void ループバックの_http_は許可する()
    {
        Assert.True(ParseWith(root => root["pleasanter"]!["baseUrl"] = "http://localhost:8080").IsValid);
    }

    [Fact]
    public void 未知のタイムゾーンは拒否する()
    {
        AssertError(ParseWith(root => root["pleasanter"]!["timeZone"] = "Nowhere/Land"), "UnknownTimeZone");
    }

    [Fact]
    public void 上限は正の整数だけ受け付ける()
    {
        AssertError(
            ParseWith(root => Source(root)["limits"] = new JsonObject { ["rawBytes"] = 0 }),
            "OutOfRange");
        var ok = ParseWith(root => Source(root)["limits"] = new JsonObject { ["messagesPerRun"] = 5 });
        Assert.Equal(5, ok.Config!.Sources[0].Limits.MessagesPerRun);
        Assert.Equal(LimitsConfig.Default.RawBytes, ok.Config.Sources[0].Limits.RawBytes);
    }

    [Fact]
    public void ホスト名にスキームやパスを含められない()
    {
        AssertError(ParseWith(root => Source(root)["host"] = "imaps://imap.example.invalid/"), "InvalidHost");
    }

    [Fact]
    public void 秘密の環境変数は_NAME_と_NAME_FILE_の同時指定をエラーにする()
    {
        var config = IngestConfigParser.Parse(ValidJson).Config!;
        var environment = new Dictionary<string, string>
        {
            ["MAILINGEST_PLEASANTER_API_KEY"] = "x",
            ["MAILINGEST_PLEASANTER_API_KEY_FILE"] = "/run/secrets/key",
            ["MAILINGEST_SUPPORT_USER"] = "u",
        };

        var (errors, warnings) = SecretEnvironmentChecker.Check(config, name => environment.GetValueOrDefault(name));

        var error = Assert.Single(errors);
        Assert.Equal("SecretConflict", error.Code);
        Assert.Equal("pleasanter.apiKeyEnvironmentVariable", error.Path);
        Assert.Contains(warnings, w => w.Code == "SecretNotSet");
        Assert.DoesNotContain(errors.Concat(warnings), i => i.Message.Contains("/run/secrets", StringComparison.Ordinal));
    }
}
