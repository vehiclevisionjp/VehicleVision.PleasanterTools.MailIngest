using Microsoft.Data.Sqlite;
using VehicleVision.PleasanterTools.Core.Cli.Testing;

namespace VehicleVision.PleasanterTools.MailIngest.Cli.Tests;

/// <summary>
/// CLI の共通仕様の契約テスト（Core.Cli.Testing）。出力が ASCII・結果は stdout で診断は stderr・接頭辞・終了コード・
/// <c>--json</c> の枠・秘密を出さない、を確かめる。ネットワークへは出ない（<c>run</c> は、接続の前に止まる場面だけ試す）。
/// </summary>
public sealed class CliContractTest : CliContractTests, IDisposable
{
    private const string ValidConfig = """
        {
          "schemaVersion": 1,
          "pleasanter": {
            "destinationId": "primary",
            "baseUrl": "https://pleasanter.example.test",
            "apiVersion": 1.1,
            "apiKeyEnvironmentVariable": "MAILINGEST_TEST_API_KEY",
            "timeZone": "Asia/Tokyo",
            "requestsPerSecond": 1
          },
          "sources": [{
            "sourceId": "support",
            "protocol": "imap",
            "host": "imap.example.test",
            "tls": "Tls",
            "authentication": {
              "mode": "password",
              "userEnvironmentVariable": "MAILINGEST_TEST_USER",
              "passwordEnvironmentVariable": "MAILINGEST_TEST_PASSWORD"
            },
            "imap": {
              "folder": "INBOX",
              "search": { "state": "all", "receivedSinceUtc": null },
              "afterSuccess": { "action": "leave", "destinationFolder": null }
            },
            "target": {
              "siteId": 1,
              "tableKind": "results",
              "externalKeyColumn": "ClassZ",
              "projectionHashColumn": "ClassY",
              "onExisting": "skip",
              "mappingRevision": 1,
              "mappings": [{ "source": "subject", "target": "Title" }],
              "fixedValues": {}
            }
          }]
        }
        """;

    // 設定の中身に問題がある（schemaVersion が未対応）
    private const string BadSchemaConfig = """
        { "schemaVersion": 2, "sources": [] }
        """;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mailingest-contract-" + Guid.NewGuid().ToString("N"));

    public CliContractTest()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗は、試験の結果にしない
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private Dictionary<string, string> DatabaseEnvironment(string name) => new()
    {
        ["MAILINGEST_DB_PROVIDER"] = "sqlite",
        ["MAILINGEST_DB_CONNECTIONSTRING"] = $"Data Source={Path.Combine(_directory, name)}",
    };

    private Dictionary<string, string> RunEnvironment(string databaseName)
    {
        var env = DatabaseEnvironment(databaseName);
        env["MAILINGEST_TEST_API_KEY"] = "secret-value";
        env["MAILINGEST_TEST_USER"] = "secret-user";
        env["MAILINGEST_TEST_PASSWORD"] = "secret-password";
        return env;
    }

    private async Task MigrateAsync(string name)
    {
        var env = DatabaseEnvironment(name);
        var code = await CliApp.RunAsync(["migrate"], TextWriter.Null, TextWriter.Null, key => env.GetValueOrDefault(key), CancellationToken.None);
        Assert.Equal(0, code);
    }

    protected override CliContract CreateContract()
    {
        var valid = WriteFile("valid.json", ValidConfig);
        var badSchema = WriteFile("bad-schema.json", BadSchemaConfig);
        var missing = Path.Combine(_directory, "missing.json");
        var secretEnvironment = new Dictionary<string, string>
        {
            ["VVPT_PLEASANTER_BASE_URL"] = "http://not-https.example.test/?token=secret-value",
            ["VVPT_PLEASANTER_API_KEY"] = "secret-value",
        };

        return new CliContract
        {
            Name = "MailIngest",
            Run = CliContract.RunnerFor(CliApp.Tool, CliApp.Handlers),
            Tool = CliApp.Tool,
            Scenarios =
            [
                new CliScenario { Name = "check without environment", Args = ["check"], Outcome = CliOutcome.UsageError },
                new CliScenario
                {
                    Name = "check without environment json", Args = ["check", "--json"], Outcome = CliOutcome.UsageError,
                    Json = CliJsonKind.Result, Command = "check",
                },
                new CliScenario
                {
                    Name = "check with a bad base url keeps secrets", Args = ["check"], Outcome = CliOutcome.UsageError,
                    Environment = secretEnvironment, Secrets = ["secret-value"],
                },
                new CliScenario
                {
                    Name = "check with only legacy names", Args = ["check"], Outcome = CliOutcome.UsageError,
                    Environment = new Dictionary<string, string>
                    {
                        ["PLEASANTER_BASE_URL"] = "https://pleasanter.example.test",
                        ["PLEASANTER_API_KEY"] = "secret-value",
                    },
                    Secrets = ["secret-value"],
                },
                new CliScenario { Name = "validate without config", Args = ["validate"], Outcome = CliOutcome.UsageError },
                new CliScenario
                {
                    Name = "validate with a missing file", Args = ["validate", "--config", missing], Outcome = CliOutcome.UsageError,
                },
                new CliScenario
                {
                    Name = "validate with a missing file json", Args = ["validate", "--config", missing, "--json"],
                    Outcome = CliOutcome.UsageError, Json = CliJsonKind.Result, Command = "validate",
                },
                new CliScenario
                {
                    Name = "validate with problems", Args = ["validate", "--config", badSchema], Outcome = CliOutcome.CheckFailed,
                },
                new CliScenario
                {
                    Name = "validate with problems json", Args = ["validate", "--config", badSchema, "--json"],
                    Outcome = CliOutcome.CheckFailed, Json = CliJsonKind.Result, Command = "validate",
                },
                new CliScenario
                {
                    Name = "validate ok", Args = ["validate", "--config", valid], Outcome = CliOutcome.Success, RequiresOkLine = true,
                    Environment = RunEnvironment("validate.db"), Secrets = ["secret-value", "secret-password"],
                },
                new CliScenario
                {
                    Name = "validate ok json", Args = ["validate", "--config", valid, "--json"], Outcome = CliOutcome.Success,
                    Json = CliJsonKind.Result, Command = "validate", Environment = RunEnvironment("validate-json.db"),
                },
                new CliScenario { Name = "run without config", Args = ["run"], Outcome = CliOutcome.UsageError },
                new CliScenario
                {
                    Name = "run with a missing file", Args = ["run", "--config", missing], Outcome = CliOutcome.UsageError,
                },
                new CliScenario
                {
                    Name = "run with a bad configuration json", Args = ["run", "--config", badSchema, "--json"],
                    Outcome = CliOutcome.UsageError, Json = CliJsonKind.Result, Command = "run",
                },
                new CliScenario
                {
                    Name = "run without secrets", Args = ["run", "--config", valid], Outcome = CliOutcome.UsageError,
                    Environment = DatabaseEnvironment("run-nosecret.db"),
                },
                new CliScenario
                {
                    Name = "run before migrate", Args = ["run", "--config", valid, "--json"], Outcome = CliOutcome.UsageError,
                    Environment = RunEnvironment("run-pending.db"), Json = CliJsonKind.Result, Command = "run",
                    Secrets = ["secret-value", "secret-user", "secret-password"],
                },
                new CliScenario
                {
                    Name = "run interrupted", Args = ["run", "--config", valid], Outcome = CliOutcome.Interrupted,
                    Environment = RunEnvironment("run-interrupted.db"),
                },
                new CliScenario
                {
                    Name = "migrate", Args = ["migrate"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("migrate.db"), RequiresOkLine = true,
                },
                new CliScenario
                {
                    Name = "migrate json", Args = ["migrate", "--json"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("migrate-json.db"), Json = CliJsonKind.Result, Command = "migrate",
                },
                new CliScenario
                {
                    Name = "migrate with a bad provider", Args = ["migrate"], Outcome = CliOutcome.UsageError,
                    Environment = new Dictionary<string, string> { ["MAILINGEST_DB_PROVIDER"] = "no-such-db" },
                },
                new CliScenario
                {
                    Name = "migrate status pending", Args = ["migrate-status"], Outcome = CliOutcome.CheckFailed,
                    Environment = DatabaseEnvironment("pending.db"),
                },
                new CliScenario
                {
                    Name = "migrate status pending json", Args = ["migrate-status", "--json"], Outcome = CliOutcome.Failed,
                    Environment = DatabaseEnvironment("pending-json.db"), Json = CliJsonKind.Result, Command = "migrate-status",
                },
                new CliScenario
                {
                    Name = "migrate status applied", Args = ["migrate-status"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("applied.db"), RequiresOkLine = true,
                    Arrange = () => MigrateAsync("applied.db"),
                },
                new CliScenario
                {
                    Name = "migrate status applied json", Args = ["migrate-status", "--json"], Outcome = CliOutcome.Success,
                    Environment = DatabaseEnvironment("applied-json.db"), Json = CliJsonKind.Result, Command = "migrate-status",
                    Arrange = () => MigrateAsync("applied-json.db"),
                },
            ],
            ArgumentForms =
            [
                new CliArgumentForms
                {
                    Name = "validate options",
                    Forms =
                    [
                        ["validate", "--config", badSchema, "--json"],
                        ["validate", "--config=" + badSchema, "--json"],
                        ["validate", "-config", badSchema, "-json"],
                        ["validate", "/config:" + badSchema, "/json"],
                        ["VALIDATE", "--CONFIG=" + badSchema, "--Json"],
                    ],
                },
            ],
        };
    }
}
