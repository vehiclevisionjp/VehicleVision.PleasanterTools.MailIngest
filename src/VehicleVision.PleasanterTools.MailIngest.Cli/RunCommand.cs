using System.Globalization;
using System.Text.Json;
using VehicleVision.PleasanterTools.Core.Cli;
using VehicleVision.PleasanterTools.Core.Contracts;
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
/// 結果行（サマリー）は <c>[INFO]</c> を付けて stdout、警告・エラー・構造化ログは stderr。<c>--json</c> のときの stdout は枠つきの JSON 1 行だけ。
/// 本文・件名・差出人・Message-ID・パスワード・API キー・接続先は出さない。
/// API キーは、設定ファイルが指す環境変数名（<c>NAME</c> / <c>NAME_FILE</c>）から読む（<c>VVPT_PLEASANTER_*</c> は読まない）。
/// </remarks>
internal static class RunCommand
{
    public static async Task<int> ExecuteAsync(CommandContext context, CancellationToken token)
    {
        var json = context.Options.Flag(StandardOptions.JsonName);
        try
        {
            return await RunAsync(context, json, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 作成の途中で強制終了しない。終了後は Sending が残るので、次回に結果不明として照合する
            const string message = "Interrupted. Items in flight are reconciled on the next run.";
            context.Output.Warn(message);
            if (json)
            {
                context.Output.WriteJson(
                    CliApp.RunCommandName, false, ExitCodeValues.Interrupted, [new CliIssue("Interrupted", message)], []);
            }

            return ExitCodeValues.Interrupted;
        }
        catch (Exception ex) when (ex is not UsageException)
        {
            // 状態 DB の障害など。例外の文言（接続文字列・パスを含み得る）は出さず、型名だけ
            return CliApp.Fail(
                context, CliApp.RunCommandName, json, ExitCodeValues.Failure, ErrorCodes.UnexpectedError,
                $"The run failed unexpectedly ({ex.GetType().Name}).");
        }
    }

    private static async Task<int> RunAsync(CommandContext context, bool json, CancellationToken token)
    {
        const string command = CliApp.RunCommandName;
        var output = context.Output;
        var (text, failure) = await CliApp.ReadConfigAsync(context.Options.Value(CliApp.ConfigOption.Name)!, token).ConfigureAwait(false);
        if (text is null)
        {
            return CliApp.Fail(context, command, json, IngestExitCode.InvalidConfiguration, CliApp.ConfigFileCode, failure!);
        }

        var parsed = IngestConfigParser.Parse(text);
        foreach (var warning in parsed.Warnings)
        {
            output.Warn(CliApp.FormatIssue(warning));
        }

        if (parsed.Config is not { } config)
        {
            return CliApp.FailIssues(context, command, json, IngestExitCode.InvalidConfiguration, parsed.Errors, parsed.Warnings);
        }

        // 接続の前に、設定の対応づけ・対応範囲・秘密（メールの資格情報）をすべて確かめる
        var environment = context.GetEnvironmentVariable;
        var startup = IngestRunner.Check(config, environment);
        string apiKey;
        try
        {
            apiKey = SecretResolver.Resolve(config.Pleasanter.ApiKeyEnvironmentVariable, environment);
        }
        catch (SecretResolutionException ex)
        {
            var issue = new ConfigIssue("pleasanter.apiKeyEnvironmentVariable", CliApp.SecretUnresolvedCode, ex.Message);
            return CliApp.FailIssues(context, command, json, IngestExitCode.InvalidConfiguration, [issue], parsed.Warnings);
        }

        if (!startup.IsValid)
        {
            return CliApp.FailIssues(context, command, json, IngestExitCode.InvalidConfiguration, startup.Errors, parsed.Warnings);
        }

        DbConnectionFactory connections;
        try
        {
            var database = DatabaseSetup.Resolve(environment, Directory.GetCurrentDirectory());
            if (MailIngestDatabase.Instance.Migrator.HasPendingMigrations(database.Provider, database.ConnectionString))
            {
                return CliApp.Fail(
                    context, command, json, IngestExitCode.InvalidConfiguration, CliApp.DatabaseNotMigratedCode,
                    "The state database has pending migrations. Run 'migrate' first.");
            }

            connections = new DbConnectionFactory(database.Provider, database.ConnectionString);
        }
        catch (InvalidOperationException ex)
        {
            return CliApp.Fail(
                context, command, json, IngestExitCode.InvalidConfiguration, CliApp.DatabaseSettingInvalidCode, ex.Message);
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
            // DotNetClient の検査（https のみ・ループバックだけ http）。URL・API キーは含まれない
            return CliApp.Fail(
                context, command, json, IngestExitCode.InvalidConfiguration, CliApp.ConfigurationInvalidCode, ex.Message);
        }

        using (gateway)
        {
            var log = new ConsoleLog(output);
            var api = new PleasanterOperations(
                gateway,
                new PleasanterOperationsOptions { RequestsPerSecond = config.Pleasanter.RequestsPerSecond });
            var store = new IngestStateStore(connections);
            var sourceRunner = new SourceIngestRunner(store, new MimeMailNormalizer(), api, log);
            var runner = new IngestRunner(store, new MailKitSessionFactory(null, new ConsoleDiagnostics(output)), sourceRunner, api, log);

            var owner = $"{Environment.MachineName}:{Environment.ProcessId}";
            var summary = await runner.RunAsync(
                startup,
                owner.Length > 64 ? owner[..64] : owner,
                environment,
                openFile: null,
                token).ConfigureAwait(false);
            return Report(context, summary, json);
        }
    }

    /// <summary>実行の結果を、<c>[INFO]</c> のサマリーと末尾の <c>[OK]</c>／<c>[ERROR]</c>／<c>[WARN]</c>、または枠つきの JSON で出す。</summary>
    private static int Report(CommandContext context, IngestRunSummary summary, bool json)
    {
        var output = context.Output;
        var exitCode = summary.ExitCode;
        if (!json)
        {
            foreach (var line in summary.ToLines())
            {
                output.Info(line);
            }
        }

        switch (exitCode)
        {
            case ExitCodeValues.Success:
                if (!json)
                {
                    output.Ok("Run completed.");
                }

                break;
            case IngestExitCode.Errors:
                output.Error(json
                    ? "The run finished with errors or unresolved items. See errors in the JSON result."
                    : "The run finished with errors or unresolved items. See the summary above.");
                break;
            default:
                output.Warn("More messages remain because of the message limit. Run again to continue.");
                break;
        }

        if (!json)
        {
            return exitCode;
        }

        var errors = new List<CliIssue>();
        var warnings = new List<CliIssue>();
        for (var i = 0; i < summary.Sources.Count; i++)
        {
            var source = summary.Sources[i];
            var path = $"sources[{i}]";
            if (source.StopReason is not null)
            {
                errors.Add(new CliIssue("SourceStopped", $"Source '{source.SourceId}' was stopped: {source.StopReason}.", path));
            }
            else if (IngestExitCode.HasErrors(source))
            {
                errors.Add(new CliIssue(
                    "SourceIncomplete", $"Source '{source.SourceId}' has items that need attention. See the source counts.", path));
            }

            if (source.HasMore)
            {
                warnings.Add(new CliIssue(
                    "MessagesRemain", $"Source '{source.SourceId}' has more messages than the per-run limit.", path));
            }
        }

        output.WriteJson(
            CliApp.RunCommandName,
            exitCode == ExitCodeValues.Success,
            exitCode,
            errors,
            warnings,
            writer => WriteSummary(writer, summary));
        return exitCode;
    }

    private static void WriteSummary(Utf8JsonWriter writer, IngestRunSummary summary)
    {
        writer.WriteString("startedAt", summary.StartedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        writer.WriteNumber("durationMs", (long)summary.Duration.TotalMilliseconds);
        writer.WriteNumber("searchCalls", summary.SearchCalls);
        writer.WriteNumber("createCalls", summary.CreateCalls);
        if (summary.ApiLimitRemaining is { } remaining)
        {
            writer.WriteNumber("apiLimitRemaining", remaining);
        }
        else
        {
            writer.WriteNull("apiLimitRemaining");
        }

        writer.WriteStartArray("sources");
        foreach (var s in summary.Sources)
        {
            writer.WriteStartObject();
            writer.WriteString("sourceId", s.SourceId);
            writer.WriteNumber("listed", s.Listed);
            writer.WriteBoolean("hasMore", s.HasMore);
            writer.WriteNumber("created", s.Created);
            writer.WriteNumber("recovered", s.Recovered);
            writer.WriteNumber("duplicates", s.Duplicates);
            writer.WriteNumber("held", s.Held);
            writer.WriteNumber("unknown", s.Unknown);
            writer.WriteNumber("retryLater", s.RetryLater);
            writer.WriteNumber("deferred", s.Deferred);
            writer.WriteNumber("failed", s.Failed);
            writer.WriteNumber("interrupted", s.Interrupted);
            writer.WriteNumber("reconciled", s.Reconciled);
            writer.WriteNumber("resumedAck", s.ResumedAck);
            writer.WriteNumber("reconcileDeferred", s.ReconcileDeferred);
            if (s.RetryNotBefore is { } at)
            {
                writer.WriteString("retryNotBefore", at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            }
            else
            {
                writer.WriteNull("retryNotBefore");
            }

            if (s.StopReason is null)
            {
                writer.WriteNull("stopReason");
            }
            else
            {
                writer.WriteString("stopReason", s.StopReason);
            }

            writer.WriteStartObject("heldByCode");
            foreach (var (code, count) in s.HeldByCode.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteNumber(code, count);
            }

            writer.WriteEndObject();
            writer.WriteStartObject("stateCounts");
            foreach (var (state, count) in s.StateCounts.OrderBy(p => p.Key))
            {
                writer.WriteNumber(state.ToString(), count);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>構造化ログの標準エラーへの出力（進行表示）。<see cref="IngestLogEntry"/> に収まる項目だけを出す。</summary>
    private sealed class ConsoleLog(CliOutput output) : IIngestLog
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

            output.Progress("log " + string.Join(' ', parts));
        }
    }

    /// <summary>接続中の警告（平文など）。パスワード・ユーザー名・本文は渡されない。</summary>
    private sealed class ConsoleDiagnostics(CliOutput output) : IMailDiagnostics
    {
        public void Warning(string sourceId, string code, string message) =>
            output.Warn($"[{code}] source={sourceId}: {message}");
    }
}
