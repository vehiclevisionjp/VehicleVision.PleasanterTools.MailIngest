using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.MailIngest.Core;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Ingest;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Data;
using VehicleVision.PleasanterTools.MailIngest.Mail;
using VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

namespace VehicleVision.PleasanterTools.MailIngest.Cli;

/// <summary>
/// <c>run --config &lt;path&gt; [--json]</c>: 取り込みを 1 回実行する。業務ロジックは <c>.Core</c> の <see cref="IngestRunner"/> にあり、
/// ここは設定・秘密・DB・接続の組み立てと、終了コード・サマリーの出力だけを行う。
/// </summary>
/// <remarks>
/// 進行メッセージ・サマリーは英語（CLI の規約）。標準出力はサマリー、標準エラーは警告・エラー・構造化ログ。
/// 本文・件名・差出人・Message-ID・パスワード・API キー・接続先は出さない。
/// </remarks>
internal static class RunCommand
{
    public static async Task<int> ExecuteAsync(string path, bool json)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // 作成の途中で強制終了しない。終了後は Sending が残るので、次回に結果不明として照合する
            cancellation.Cancel();
        };

        try
        {
            return await RunAsync(path, json, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled. Items in flight are reconciled on the next run.");
            return IngestExitCode.Errors;
        }
        catch (Exception ex)
        {
            // 状態 DB の障害など。例外の文言（接続文字列・パスを含み得る）は出さない
            Console.Error.WriteLine($"error [Unexpected] The run failed unexpectedly ({ex.GetType().Name}).");
            return IngestExitCode.Errors;
        }
    }

    private static async Task<int> RunAsync(string path, bool json, CancellationToken cancellationToken)
    {
        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > IngestConfigParser.MaxConfigBytes)
            {
                Console.Error.WriteLine("error [ConfigFile] The configuration file is missing or too large.");
                return IngestExitCode.InvalidConfiguration;
            }

            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("error [ConfigFile] The configuration file cannot be read.");
            return IngestExitCode.InvalidConfiguration;
        }

        var parsed = IngestConfigParser.Parse(text);
        foreach (var warning in parsed.Warnings)
        {
            Console.Error.WriteLine($"warning [{warning.Code}] {warning.Path}: {warning.Message}");
        }

        if (parsed.Config is not { } config)
        {
            PrintErrors(parsed.Errors);
            return IngestExitCode.InvalidConfiguration;
        }

        // 接続の前に、設定の対応づけ・対応範囲・秘密（メールの資格情報）をすべて確かめる
        Func<string, string?> environment = Environment.GetEnvironmentVariable;
        var startup = IngestRunner.Check(config, environment);
        string apiKey;
        try
        {
            apiKey = SecretResolver.Resolve(config.Pleasanter.ApiKeyEnvironmentVariable, environment);
        }
        catch (SecretResolutionException ex)
        {
            Console.Error.WriteLine($"error [SecretUnresolved] pleasanter.apiKeyEnvironmentVariable: {ex.Message}");
            return IngestExitCode.InvalidConfiguration;
        }

        if (!startup.IsValid)
        {
            PrintErrors(startup.Errors);
            return IngestExitCode.InvalidConfiguration;
        }

        DbConnectionFactory connections;
        try
        {
            var database = MailIngestDatabase.Instance;
            var provider = database.ParseProvider(
                Environment.GetEnvironmentVariable(database.ProviderSetting),
                DatabaseProvider.Sqlite);
            var connectionString = database.ResolveConnectionString(
                provider,
                Environment.GetEnvironmentVariable(database.ConnectionStringSetting),
                Directory.GetCurrentDirectory());
            database.EnsureSecure(
                provider,
                connectionString,
                string.Equals(Environment.GetEnvironmentVariable(database.AllowInsecureSetting), "true", StringComparison.OrdinalIgnoreCase));
            if (database.Migrator.HasPendingMigrations(provider, connectionString))
            {
                Console.Error.WriteLine("error [DatabaseNotMigrated] The state database has pending migrations. Run 'migrate' first.");
                return IngestExitCode.InvalidConfiguration;
            }

            connections = new DbConnectionFactory(provider, connectionString);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return IngestExitCode.InvalidConfiguration;
        }

        PleasanterGateway gateway;
        try
        {
            gateway = PleasanterGateway.Create(new PleasanterOptions
            {
                BaseUrl = config.Pleasanter.BaseUrl.ToString(),
                ApiKey = apiKey,
                ApiVersion = config.Pleasanter.ApiVersion,
                DetectServerVersion = false,
            });
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message); // DotNetClient の検査（https のみ・ループバックだけ http）。URL・API キーは含まれない
            return IngestExitCode.InvalidConfiguration;
        }

        using (gateway)
        {
            var log = new ConsoleLog();
            var api = new PleasanterOperations(
                gateway,
                new PleasanterOperationsOptions { RequestsPerSecond = config.Pleasanter.RequestsPerSecond });
            var store = new IngestStateStore(connections);
            var sourceRunner = new SourceIngestRunner(store, new MimeMailNormalizer(), api, log);
            var runner = new IngestRunner(store, new MailKitSessionFactory(null, new ConsoleDiagnostics()), sourceRunner, api, log);

            var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
            var summary = await runner.RunAsync(
                startup,
                owner.Length > 64 ? owner[..64] : owner,
                environment,
                openFile: null,
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine(json ? summary.ToJson() : summary.ToText());
            return summary.ExitCode;
        }
    }

    private static void PrintErrors(IEnumerable<ConfigIssue> errors)
    {
        foreach (var error in errors)
        {
            Console.Error.WriteLine($"error [{error.Code}] {error.Path}: {error.Message}");
        }
    }

    /// <summary>構造化ログの標準エラーへの出力。<see cref="IngestLogEntry"/> に収まる項目だけを出す。</summary>
    private sealed class ConsoleLog : IIngestLog
    {
        public void Write(IngestLogEntry entry)
        {
            var parts = new List<string> { $"level={entry.Level}", $"event={entry.Event}" };
            if (entry.SourceId is not null)
            {
                parts.Add($"source={entry.SourceId}");
            }

            if (entry.ItemId is { } item)
            {
                parts.Add($"item={item:D}");
            }

            if (entry.Code is not null)
            {
                parts.Add($"code={entry.Code}");
            }

            if (entry.HttpStatus is { } status)
            {
                parts.Add($"http={status}");
            }

            if (entry.Count is { } count)
            {
                parts.Add($"count={count}");
            }

            if (entry.DurationMs is { } ms)
            {
                parts.Add($"ms={ms}");
            }

            Console.Error.WriteLine("log " + string.Join(' ', parts));
        }
    }

    /// <summary>接続中の警告（平文など）。パスワード・ユーザー名・本文は渡されない。</summary>
    private sealed class ConsoleDiagnostics : IMailDiagnostics
    {
        public void Warning(string sourceId, string code, string message) =>
            Console.Error.WriteLine($"warning [{code}] source={sourceId}: {message}");
    }
}
