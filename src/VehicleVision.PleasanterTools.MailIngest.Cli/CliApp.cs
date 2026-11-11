using System.Reflection;
using VehicleVision.PleasanterTools.Core.Cli;
using VehicleVision.PleasanterTools.Core.Contracts;
using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.MailIngest.Core;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Data;

namespace VehicleVision.PleasanterTools.MailIngest.Cli;

/// <summary>
/// コマンドラインの入口。引数の解析・ヘルプ・エラー・<c>--version</c>・補完・Ctrl+C は Core.Cli（CLI の共通仕様）に任せる。
/// 終了コードは 0＝成功、1＝失敗（<c>validate</c> で問題あり・<c>run</c> の処理エラー・照合待ちを含む）、2＝使い方・設定の誤り（何も実行していない）、
/// 3＝<c>run</c> が件数上限で残りあり、130＝中断。
/// </summary>
public static class CliApp
{
    /// <summary>ツール名（<c>--version</c> の表示・ヘルプ・環境変数の接頭辞 <c>MAILINGEST_</c> の元）。</summary>
    public const string ToolName = "mailingest";

    public const string CheckCommand = "check";
    public const string ValidateCommand = "validate";
    public const string RunCommandName = "run";
    public const string MigrateCommand = "migrate";
    public const string MigrateStatusCommand = "migrate-status";

    // 固有の ErrorCodes（--json の errors[].code）。設定の問題（IngestConfigParser など）のコードは、そのまま errors[].code に入る
    internal const string ConnectionFailedCode = "ConnectionFailed";
    internal const string ConfigFileCode = "ConfigFile";
    internal const string SecretUnresolvedCode = "SecretUnresolved";
    internal const string DatabaseSettingInvalidCode = "DatabaseSettingInvalid";
    internal const string DatabaseNotMigratedCode = "DatabaseNotMigrated";
    internal const string DatabaseFailedCode = "DatabaseFailed";
    internal const string MigrationsPendingCode = "MigrationsPending";
    internal const string ConfigurationInvalidCode = "ConfigurationInvalid";

    internal static readonly OptionSpec ConfigOption = new()
    {
        Name = "config",
        Description = "Path of the configuration file.",
        Kind = OptionKind.Value,
        ValueName = "FILE",
        Required = true,
        ValueCompletion = OptionValueCompletion.File,
    };

    public static ToolSpec Tool { get; } = new(
        ToolName,
        "Ingest mail from IMAP / POP3 servers into Pleasanter records.",
        ToolVersion(),
        [
            new CommandSpec(CheckCommand, "Check the connection to Pleasanter.", [StandardOptions.Json]),
            new CommandSpec(ValidateCommand, "Validate a configuration file. No connection is made.", [ConfigOption, StandardOptions.Json]),
            new CommandSpec(RunCommandName, "Ingest the mail described in a configuration file once.", [ConfigOption, StandardOptions.Json]),
            new CommandSpec(MigrateCommand, "Apply the pending migrations to the state database.", [StandardOptions.Json]),
            new CommandSpec(MigrateStatusCommand, "Show the pending migrations (exit code 1 if any).", [StandardOptions.Json]),
            CompletionCommand.Spec,
        ]);

    public static IReadOnlyDictionary<string, CommandHandler> Handlers { get; } = new Dictionary<string, CommandHandler>
    {
        [CheckCommand] = CheckAsync,
        [ValidateCommand] = ValidateAsync,
        [RunCommandName] = RunCommand.ExecuteAsync,
        [MigrateCommand] = (context, token) => MigrateAsync(context, MigrateCommand, MigrationCommand.ApplyArgument, token),
        [MigrateStatusCommand] = (context, token) => MigrateAsync(context, MigrateStatusCommand, MigrationCommand.StatusArgument, token),
        [CompletionCommand.Name] = CompletionCommand.CreateHandler(Tool),
    };

    /// <summary>コンソールと環境変数で実行する（<c>Main</c> から）。</summary>
    public static Task<int> RunWithConsoleAsync(string[] args) =>
        CliApplication.RunWithConsoleAsync(Tool, Handlers, args);

    /// <summary>出力・入力・環境変数を差し替えて実行する（試験用）。</summary>
    public static Task<int> RunAsync(
        string[] args, TextWriter output, TextWriter error, Func<string, string?>? environment, CancellationToken cancellationToken) =>
        CliApplication.RunAsync(
            Tool, Handlers, args, output, error, TextReader.Null, environment ?? Environment.GetEnvironmentVariable, cancellationToken);

    private static string ToolVersion()
    {
        var informational = typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrEmpty(version) ? "0.0.0" : version;
    }

    /// <summary>設定の問題を、<c>--json</c> の <c>errors</c>・<c>warnings</c> の要素にする（場所が空なら <c>path</c> は付けない）。</summary>
    internal static CliIssue ToIssue(ConfigIssue issue) =>
        new(issue.Code, issue.Message, string.IsNullOrEmpty(issue.Path) ? null : issue.Path);

    /// <summary>失敗を <c>[ERROR]</c>（stderr）で出し、<c>--json</c> なら枠も stdout へ出して、終了コードを返す。</summary>
    internal static int Fail(CommandContext context, string command, bool json, int exitCode, string code, string message)
    {
        context.Output.Error(message);
        if (json)
        {
            context.Output.WriteJson(command, false, exitCode, [new CliIssue(code, message)], []);
        }

        return exitCode;
    }

    /// <summary>設定の問題の一覧を、1 件ずつ <c>[ERROR]</c>（stderr）で出し、<c>--json</c> なら枠も stdout へ出して、終了コードを返す。</summary>
    internal static int FailIssues(
        CommandContext context, string command, bool json, int exitCode, IReadOnlyList<ConfigIssue> errors, IReadOnlyList<ConfigIssue> warnings)
    {
        foreach (var error in errors)
        {
            context.Output.Error(FormatIssue(error));
        }

        if (json)
        {
            context.Output.WriteJson(command, false, exitCode, [.. errors.Select(ToIssue)], [.. warnings.Select(ToIssue)]);
        }

        return exitCode;
    }

    /// <summary>設定の問題の 1 行の文章（<c>[Code] path: message</c>。場所が空なら <c>[Code] message</c>）。</summary>
    internal static string FormatIssue(ConfigIssue issue) =>
        string.IsNullOrEmpty(issue.Path)
            ? $"[{issue.Code}] {issue.Message}"
            : $"[{issue.Code}] {issue.Path}: {issue.Message}";

    /// <summary>設定ファイルを読む。無い・大きすぎる・読めないときは <paramref name="failure"/> に理由を入れる（パスやシステムの文言は含めない）。</summary>
    internal static async Task<(string? Text, string? Failure)> ReadConfigAsync(string path, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > IngestConfigParser.MaxConfigBytes)
            {
                return (null, "The configuration file is missing or too large.");
            }

            return (await File.ReadAllTextAsync(path, token).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, "The configuration file cannot be read.");
        }
    }

    private static async Task<int> CheckAsync(CommandContext context, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        try
        {
            // 環境変数の欠落・不正は UsageException（Core.Cli が [ERROR] と 2 にする）
            using var client = PleasanterGateway.Create(PleasanterEnvironment.FromEnvironment(context.GetEnvironmentVariable));
            var connected = await client.CheckConnectionAsync(token).ConfigureAwait(false);
            if (!connected)
            {
                context.Output.Error("Connection failed.");
            }
            else if (!json)
            {
                context.Output.Ok("Connected.");
            }

            if (json)
            {
                context.Output.WriteJson(
                    CheckCommand,
                    connected,
                    connected ? ExitCodeValues.Success : ExitCodeValues.Failure,
                    connected ? [] : [new CliIssue(ConnectionFailedCode, "Connection failed.")],
                    [],
                    writer => writer.WriteBoolean("connected", connected));
            }

            return connected ? ExitCodeValues.Success : ExitCodeValues.Failure;
        }
        catch (ArgumentException ex)
        {
            // BaseUrl が不正（DotNetClient の検査。https のみ・ループバックだけ http 可）。文言に URL・API キーは含まれない
            return Fail(context, CheckCommand, json, ExitCodeValues.UsageError, ErrorCodes.Usage, ex.Message);
        }
    }

    /// <summary>
    /// 設定 JSON を検査する（接続しない。秘密の値は読まない）。見つかった問題は結果なので stdout へ出す
    /// （<c>[ERROR]</c>・<c>[WARN]</c> は stderr 用の接頭辞なので付けず、<c>error [Code] path: message</c> の形で出す）。
    /// 検査を始められない（設定ファイルが無い・読めない）は 2、始められて問題ありは 1。
    /// </summary>
    private static async Task<int> ValidateAsync(CommandContext context, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        var path = context.Options.Value(ConfigOption.Name)!;
        var (text, failure) = await ReadConfigAsync(path, token).ConfigureAwait(false);
        if (text is null)
        {
            return Fail(context, ValidateCommand, json, ExitCodeValues.UsageError, ConfigFileCode, failure!);
        }

        var result = IngestConfigParser.Parse(text);
        var errors = result.Errors.ToList();
        var warnings = result.Warnings.ToList();
        if (result.Config is { } config)
        {
            var (secretErrors, secretWarnings) = SecretEnvironmentChecker.Check(config, context.GetEnvironmentVariable);
            errors.AddRange(secretErrors);
            warnings.AddRange(secretWarnings);
        }

        var sources = result.Config?.Sources.Count;
        var exitCode = errors.Count > 0 ? ExitCodeValues.Failure : ExitCodeValues.Success;
        if (json)
        {
            context.Output.WriteJson(
                ValidateCommand,
                exitCode == ExitCodeValues.Success,
                exitCode,
                [.. errors.Select(ToIssue)],
                [.. warnings.Select(ToIssue)],
                writer =>
                {
                    writer.WriteStartObject("checked");
                    if (sources is { } count)
                    {
                        writer.WriteNumber("sources", count);
                    }
                    else
                    {
                        writer.WriteNull("sources");
                    }

                    writer.WriteEndObject();
                });
            return exitCode;
        }

        foreach (var warning in warnings)
        {
            context.Output.Result($"warning {FormatIssue(warning)}");
        }

        foreach (var error in errors)
        {
            context.Output.Result($"error {FormatIssue(error)}");
        }

        if (errors.Count > 0)
        {
            context.Output.Result($"Configuration is invalid ({errors.Count} error(s), {warnings.Count} warning(s)).");
        }
        else
        {
            context.Output.Ok($"Configuration is valid ({sources} source(s)). No connection was made.");
        }

        return exitCode;
    }

    /// <summary>
    /// 状態 DB のマイグレーションを当てる（<c>migrate</c>）／未適用を見る（<c>migrate-status</c>。未適用があれば 1）。
    /// テキストは Core.Data の <see cref="MigrationCommand"/> の出力（<c>[OK]</c>・stderr の進行表示）を <see cref="CliOutput"/> へ流す。
    /// </summary>
    private static async Task<int> MigrateAsync(CommandContext context, string command, string argument, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        try
        {
            var info = DatabaseSetup.Resolve(context.GetEnvironmentVariable, Directory.GetCurrentDirectory());
            using var error = new CliOutputWriter(context.Output, toStderr: true);
            if (!json)
            {
                using var output = new CliOutputWriter(context.Output, toStderr: false);
                return await DatabaseSetup.MigrateAsync(argument, info, output, error, token).ConfigureAwait(false);
            }

            var migrator = MailIngestDatabase.Instance.Migrator;
            var pending = migrator.PendingMigrations(info.Provider, info.ConnectionString);
            var apply = argument == MigrationCommand.ApplyArgument;
            var applied = 0;
            if (apply && pending.Count > 0)
            {
                // 進行表示・失敗は stderr のまま。結果は枠にまとめる（MigrationCommand の stdout は捨てる）
                await DatabaseSetup.MigrateAsync(argument, info, TextWriter.Null, error, token).ConfigureAwait(false);
                applied = pending.Count;
                pending = [];
            }

            var ok = pending.Count == 0;
            if (!ok)
            {
                context.Output.Error($"{pending.Count} migration(s) are pending.");
            }

            context.Output.WriteJson(
                command,
                ok,
                ok ? ExitCodeValues.Success : ExitCodeValues.Failure,
                ok ? [] : [new CliIssue(MigrationsPendingCode, $"{pending.Count} migration(s) are pending.")],
                [],
                writer =>
                {
                    writer.WriteString("provider", info.Provider.ToString());
                    if (apply)
                    {
                        writer.WriteNumber("applied", applied);
                    }

                    writer.WriteStartArray("pending");
                    foreach (var name in pending)
                    {
                        writer.WriteStringValue(name);
                    }

                    writer.WriteEndArray();
                });
            return ok ? ExitCodeValues.Success : ExitCodeValues.Failure;
        }
        catch (InvalidOperationException ex)
        {
            // 設定の誤り。接続文字列などの値は含まれない
            return Fail(context, command, json, ExitCodeValues.UsageError, DatabaseSettingInvalidCode, ex.Message);
        }
        catch (System.Data.Common.DbException)
        {
            return Fail(context, command, json, ExitCodeValues.Failure, DatabaseFailedCode, "The state database failed.");
        }
    }
}
