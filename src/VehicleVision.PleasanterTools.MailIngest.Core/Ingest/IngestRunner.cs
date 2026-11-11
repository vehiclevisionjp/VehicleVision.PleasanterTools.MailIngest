using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Core.State;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Ingest;

/// <summary>
/// <c>run</c> の終了コード（設計 <c>アーキテクチャ方針.md</c> 第 6 節の案）。
/// </summary>
public static class IngestExitCode
{
    /// <summary>対象分がすべて成功（0 件の成功を含む。疎通の確認とは別）。</summary>
    public const int Success = 0;

    /// <summary>処理エラー・照合待ちあり（新たな保留・結果不明・失敗・停止、または次回へ残した項目）。</summary>
    public const int Errors = 1;

    /// <summary>起動設定の不正（設定 JSON・秘密・状態 DB・未対応の設定）。何も実行していない。</summary>
    public const int InvalidConfiguration = 2;

    /// <summary>エラーは無いが、件数上限により未処理のメールが残っている。</summary>
    public const int Remaining = 3;

    /// <summary>
    /// 各ソースの結果から終了コードを決める。<b>古い保留（<c>Held</c>）は数えない</b>
    /// （解除する手段が無く、毎回エラーになるため）。今回新たに保留にしたもの、結果不明・再試行待ち・未確定が残るものを数える。
    /// </summary>
    public static int From(IEnumerable<SourceRunSummary> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var errors = false;
        var remaining = false;
        foreach (var s in sources)
        {
            errors |= HasErrors(s);
            remaining |= s.HasMore;
        }

        return errors ? Errors : remaining ? Remaining : Success;
    }

    /// <summary>ソースの結果が、終了コード 1（処理エラー・照合待ち）に当たるか。<see cref="From"/> と同じ判定。</summary>
    public static bool HasErrors(SourceRunSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.StopReason is not null
            || s.Failed > 0
            || s.Held > 0
            || s.Unknown > 0
            || s.RetryLater > 0
            || s.ReconcileDeferred > 0
            || s.Deferred > 0
            || s.Outstanding(IngestState.Unknown) > 0
            || s.Outstanding(IngestState.RetryPending) > 0
            || s.Outstanding(IngestState.Pending) > 0
            || s.Outstanding(IngestState.AckPending) > 0
            || s.Outstanding(IngestState.Sending) > 0;
    }
}

/// <summary>1 回の実行（全ソース）の運用サマリー。件数だけで、本文・件名・差出人・Message-ID などは持たない。</summary>
public sealed record IngestRunSummary(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int ExitCode,
    IReadOnlyList<SourceRunSummary> Sources,
    int SearchCalls,
    int CreateCalls,
    int? ApiLimitRemaining)
{
    /// <summary>人が読む形の行（英語の固定文）。CLI は、1 行ずつ <c>[INFO]</c> を付けて出す。</summary>
    public IReadOnlyList<string> ToLines()
    {
        var lines = new List<string>
        {
            $"MailIngest run finished: exit code {ExitCode}, {Duration.TotalSeconds:0.0}s.",
            $"API calls: search {SearchCalls}, create {CreateCalls}"
                + (ApiLimitRemaining is { } remaining ? $", daily limit remaining {remaining}." : "."),
            "Attachments are not transferred (metadata only). Messages are left on the server (afterSuccess: leave).",
        };
        foreach (var s in Sources)
        {
            lines.Add(
                $"[{s.SourceId}] listed {s.Listed}, created {s.Created}, recovered {s.Recovered}, reconciled {s.Reconciled}, "
                + $"duplicates {s.Duplicates}, held {s.Held}, unknown {s.Unknown}, retry-later {s.RetryLater}, "
                + $"deferred {s.Deferred}, failed {s.Failed}"
                + (s.HasMore ? ", more messages remain (message limit)" : string.Empty)
                + (s.StopReason is not null ? $", stopped: {s.StopReason}" : string.Empty)
                + (s.RetryNotBefore is { } at ? $", retry not before {at:u}" : string.Empty));
            foreach (var (code, count) in s.HeldByCode.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                lines.Add($"  held {code}: {count}");
            }

            var states = string.Join(", ", s.StateCounts.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}"));
            if (states.Length > 0)
            {
                lines.Add($"  state totals: {states}");
            }
        }

        if (Sources.Count == 0 || Sources.All(s => s.Listed == 0 && s.Reconciled == 0 && s.ResumedAck == 0))
        {
            lines.Add("No new messages were processed. This does not by itself confirm that every source is healthy.");
        }

        return lines;
    }

    /// <summary>人が読む形（<see cref="ToLines"/> を改行でつなぐ）。</summary>
    public string ToText() => string.Join(Environment.NewLine, ToLines());
}

/// <summary>起動時の検査の結果。</summary>
public sealed record StartupCheck(IReadOnlyList<SourcePlan> Plans, IReadOnlyList<ConfigIssue> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// 取り込み実行（全ソースを順に）。ソースごとに実行の排他を取り、接続し、<see cref="SourceIngestRunner"/> で取り込む。
/// </summary>
/// <remarks>
/// <para>同じソースの実行が終わっていなければそのソースは実行しない（期限切れで引き継がない。異常終了で残ったものは人が確認して解除する。#85）。</para>
/// <para>1 ソースの失敗（接続・認証・例外）は他のソースへ波及させない。接続できないソースでも、
/// 結果不明の照合と受信後操作の再開（メールサーバへ触れない処理）は行う。</para>
/// </remarks>
public sealed class IngestRunner(
    IIngestStateStore store,
    IMailSessionFactory sessions,
    SourceIngestRunner sourceRunner,
    PleasanterOperations api,
    IIngestLog? log = null,
    TimeProvider? time = null)
{
    private readonly IIngestLog _log = log ?? NullIngestLog.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// 起動時の検査。設定の対応づけ・タイムゾーン・受信後操作の対応範囲と、メール認証情報の秘密（<c>NAME</c> / <c>NAME_FILE</c>）を
    /// 接続の前にすべて解決して確かめる。値は返さず、変数名・設定の位置だけを返す。
    /// </summary>
    public static StartupCheck Check(IngestConfig config, Func<string, string?> environment, Func<string, Stream>? openFile = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(environment);

        var errors = new List<ConfigIssue>();
        var plans = new List<SourcePlan>();
        for (var i = 0; i < config.Sources.Count; i++)
        {
            var path = $"sources[{i}]";
            var (plan, planErrors) = SourcePlan.Create(config.Pleasanter, config.Sources[i], path);
            errors.AddRange(planErrors);
            if (plan is not null)
            {
                plans.Add(plan);
            }

            var auth = config.Sources[i].Authentication;
            foreach (var (variable, field) in new[]
                     {
                         (auth.UserEnvironmentVariable, "userEnvironmentVariable"),
                         (auth.PasswordEnvironmentVariable, "passwordEnvironmentVariable"),
                     })
            {
                try
                {
                    _ = SecretResolver.Resolve(variable, environment, openFile);
                }
                catch (SecretResolutionException ex)
                {
                    errors.Add(new ConfigIssue($"{path}.authentication.{field}", "SecretUnresolved", ex.Message));
                }
            }
        }

        return new StartupCheck(plans, errors);
    }

    /// <summary>
    /// 実行する。<paramref name="check"/> は <see cref="Check"/> の結果（エラーが無いもの）。
    /// </summary>
    public async Task<IngestRunSummary> RunAsync(
        StartupCheck check,
        string ownerId,
        Func<string, string?> environment,
        Func<string, Stream>? openFile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!check.IsValid)
        {
            throw new InvalidOperationException("The startup check has errors. The run is not started.");
        }

        var started = _time.GetUtcNow();
        var results = new List<SourceRunSummary>();
        foreach (var plan in check.Plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunSourceAsync(plan, ownerId, environment, openFile, cancellationToken).ConfigureAwait(false));
        }

        var summary = new IngestRunSummary(
            started,
            _time.GetUtcNow() - started,
            IngestExitCode.From(results),
            results,
            api.SearchCalls,
            api.CreateCalls,
            api.LimitRemaining);
        return summary;
    }

    private async Task<SourceRunSummary> RunSourceAsync(
        SourcePlan plan,
        string ownerId,
        Func<string, string?> environment,
        Func<string, Stream>? openFile,
        CancellationToken cancellationToken)
    {
        var sourceId = plan.Source.SourceId;
        var run = await store.TryBeginRunAsync(sourceId, ownerId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            // 同じソースの実行が終わっていない。引き継がない（進行中の作成を再送しないため）。人が確認して解除する
            var blocked = new SourceRunSummary(sourceId) { StopReason = "RunInProgress" };
            Log(IngestLogLevel.Error, "RunInProgress", sourceId);
            blocked.StateCounts = await store.CountByStateAsync(sourceId, cancellationToken).ConfigureAwait(false);
            return blocked;
        }

        var status = "Completed";
        var summary = new SourceRunSummary(sourceId);
        try
        {
            summary = await RunConnectedAsync(plan, environment, openFile, cancellationToken).ConfigureAwait(false);
            status = summary.StopReason is null ? "Completed" : "Stopped";
        }
        catch (OperationCanceledException)
        {
            status = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            // 他のソースへ波及させない。例外の文言（接続先・本文を含み得る）は出さない
            status = "Failed";
            summary.Failed++;
            summary.StopReason ??= "SourceFailed";
            Log(IngestLogLevel.Error, "SourceFailed", sourceId, code: ex.GetType().Name);
        }
        finally
        {
            await store.EndRunAsync(run.Id, status, CancellationToken.None).ConfigureAwait(false);
        }

        summary.StateCounts = await store.CountByStateAsync(sourceId, cancellationToken).ConfigureAwait(false);
        return summary;
    }

    private async Task<SourceRunSummary> RunConnectedAsync(
        SourcePlan plan,
        Func<string, string?> environment,
        Func<string, Stream>? openFile,
        CancellationToken cancellationToken)
    {
        var source = plan.Source;
        var credentials = new MailCredentials(
            SecretResolver.Resolve(source.Authentication.UserEnvironmentVariable, environment, openFile),
            SecretResolver.Resolve(source.Authentication.PasswordEnvironmentVariable, environment, openFile));

        IMailSession session;
        try
        {
            session = await sessions.ConnectAsync(source, credentials, cancellationToken).ConfigureAwait(false);
        }
        catch (MailConnectionException ex)
        {
            // 接続・認証・TLS の失敗。人の修正待ちか一時的。メールサーバへ触れない回復（照合・受信後操作の再開）だけは行う
            var failed = new SourceRunSummary(source.SourceId) { StopReason = $"Mail{ex.Kind}" };
            Log(IngestLogLevel.Error, "MailConnectFailed", source.SourceId, code: failed.StopReason);
            await sourceRunner.RecoverAsync(plan, failed, cancellationToken).ConfigureAwait(false);
            return failed;
        }

        await using (session.ConfigureAwait(false))
        {
            return await sourceRunner.RunAsync(plan, session, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Log(IngestLogLevel level, string name, string sourceId, string? code = null) =>
        _log.Write(new IngestLogEntry(level, name, sourceId, Code: code));
}
