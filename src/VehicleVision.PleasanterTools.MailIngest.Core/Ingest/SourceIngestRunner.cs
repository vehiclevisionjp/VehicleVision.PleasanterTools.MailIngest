using System.Diagnostics;
using VehicleVision.PleasanterTools.DotNetClient;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Core.Identity;
using VehicleVision.PleasanterTools.MailIngest.Core.Mail;
using VehicleVision.PleasanterTools.MailIngest.Core.State;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Ingest;

/// <summary>API 成功の後、その結果を状態 DB へ確定できなかった。受信後操作へ進まず、ソースの処理を失敗として止める。</summary>
internal sealed class IngestCommitException(string message) : Exception(message);

/// <summary>
/// 1 ソースの取り込み（取得 → 正規化 → 重複排除・状態確保 → 対応づけ → 既存キー照合 → 作成 → DB 確定）。
/// 設計 <c>取り込み状態とAPI.md</c> 第 4 節の状態遷移に従う。
/// </summary>
/// <remarks>
/// <para>**1 通の失敗は、そのメールを保留（<see cref="IngestState.Held"/>）にして残し、後続のメールは続ける。**
/// サーバ上のメールは削除・既読化しない（受信後操作は <c>leave</c> だけ。markSeen・move・delete は後続の #84）。</para>
/// <para>**作成は再送しない。** 反映済みの可能性があるとき（応答の喪失・送信後のタイムアウト・5xx・形の不正な応答）は
/// <see cref="IngestState.Unknown"/> へ遷移して止める。送信前に <see cref="IngestState.Sending"/> を永続化するので、
/// 再起動で残っても結果不明として扱える（照合は #82）。</para>
/// <para>API 成功の後に DB の確定へ失敗したら、受信後操作へ進まず、ソースの処理を失敗として止める。
/// <see cref="IngestState.Sending"/> が残るので、次回は結果不明として照合する。</para>
/// <para>**推奨どおりの実装であり、確定仕様ではない。** 実機のメールサーバ・Pleasanter では未検証。</para>
/// </remarks>
public sealed class SourceIngestRunner(
    IIngestStateStore store,
    IMailNormalizer normalizer,
    PleasanterOperations api,
    IIngestLog? log = null,
    TimeProvider? time = null)
{
    private readonly IIngestLog _log = log ?? NullIngestLog.Instance;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>接続・認証済みのセッションから、1 回分（件数上限まで）を取り込む。</summary>
    public async Task<SourceRunSummary> RunAsync(
        SourcePlan plan,
        IMailSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(session);

        var source = plan.Source;
        var summary = new SourceRunSummary(source.SourceId);

        // 新しいメールより先に、前回までの結果不明の照合と、API 成功済みの受信後操作の再開を行う
        await RecoverAsync(plan, summary, cancellationToken).ConfigureAwait(false);

        MailListing listing;
        try
        {
            var settled = await store.ListSettledLocationKeysAsync(source.SourceId, cancellationToken).ConfigureAwait(false);
            listing = await session.ListAsync(source.Limits.MessagesPerRun, settled.Contains, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MailConnectionException ex)
        {
            summary.StopReason = $"Mail{ex.Kind}";
            Log(IngestLogLevel.Error, "MailListFailed", source.SourceId, code: summary.StopReason);
            return summary;
        }

        summary.Listed = listing.Candidates.Count;
        summary.HasMore = listing.HasMore;
        var stopwatch = Stopwatch.StartNew();

        foreach (var candidate in listing.Candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (summary.StopReason is not null || api.HaltReason is not null)
            {
                summary.StopReason ??= api.HaltReason;
                summary.Deferred++;
                continue;
            }

            try
            {
                await ProcessAsync(plan, session, candidate, summary, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MailConnectionException ex)
            {
                summary.StopReason = $"Mail{ex.Kind}";
                Log(IngestLogLevel.Error, "MailFetchFailed", source.SourceId, code: summary.StopReason);
            }
            catch (IngestCommitException)
            {
                summary.Failed++;
                summary.StopReason = "StateCommitFailed";
                Log(IngestLogLevel.Error, "StateCommitFailed", source.SourceId, code: summary.StopReason);
            }
            catch (Exception ex)
            {
                // 分類できない例外は成功にも空本文にもしない。失敗として数え、後続のメールは続ける。例外の文言は出さない
                summary.Failed++;
                Log(IngestLogLevel.Error, "UnexpectedError", source.SourceId, code: ex.GetType().Name);
            }
        }

        if (api.HaltReason is not null)
        {
            summary.StopReason ??= api.HaltReason;
        }

        summary.RetryNotBefore = api.RetryNotBefore;
        Log(
            IngestLogLevel.Information,
            "SourceRunFinished",
            source.SourceId,
            count: summary.Listed,
            durationMs: stopwatch.ElapsedMilliseconds);
        return summary;
    }

    /// <summary>件数の多い異常時に 1 回の実行が膨らまないよう、状態ごとに 1 回で扱う件数を絞る。</summary>
    private const int RecoveryBatch = 500;

    /// <summary>
    /// 前回までの未完了を片づける（メールサーバへは触れない）。実行の排他（<c>TryBeginRunAsync</c>）を取った後で呼ぶ。
    /// <list type="number">
    /// <item>再起動で残った <c>Sending</c> を <c>Unknown</c> にする（自動再送はしない）。</item>
    /// <item><c>AckPending</c>: API は成功済み。API を再実行せず、受信後操作を再開して <c>Completed</c> にする（<c>leave</c> は何もしない）。</item>
    /// <item><c>Unknown</c>: 外部キーで検索して照合する。1 件かつ投射ハッシュ一致なら RecordId を回復して <c>AckPending</c>。
    /// 0 件は時間を空けて検索のみ最大回数まで行い、解消しなければ <c>Held</c>（<b>即 Create しない</b>。索引・レプリカ遅延・権限不足の可能性）。
    /// 複数・不一致・確認不能は <c>Held</c>。検索自体の失敗は <c>Unknown</c> のまま次回へ残す。</item>
    /// </list>
    /// メールサーバに接続できない実行でも呼べる。
    /// </summary>
    public async Task RecoverAsync(SourcePlan plan, SourceRunSummary summary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(summary);

        var sourceId = plan.Source.SourceId;
        summary.Interrupted = await store.MarkInterruptedAsUnknownAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (summary.Interrupted > 0)
        {
            Log(IngestLogLevel.Warning, "InterruptedMarkedUnknown", sourceId, count: summary.Interrupted);
        }

        foreach (var item in await store.ListByStateAsync(sourceId, IngestState.AckPending, RecoveryBatch, cancellationToken)
                     .ConfigureAwait(false))
        {
            if (await TryRecoverAsync(summary, sourceId, item, () => CompleteAckAsync(plan, item, cancellationToken), cancellationToken)
                    .ConfigureAwait(false))
            {
                summary.ResumedAck++;
            }
        }

        foreach (var item in await store.ListByStateAsync(sourceId, IngestState.Unknown, RecoveryBatch, cancellationToken)
                     .ConfigureAwait(false))
        {
            if (api.HaltReason is not null)
            {
                summary.StopReason ??= api.HaltReason;
                summary.ReconcileDeferred++;
                continue;
            }

            await TryRecoverAsync(summary, sourceId, item, () => ReconcileAsync(plan, summary, item, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> TryRecoverAsync(
        SourceRunSummary summary,
        string sourceId,
        IngestItem item,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 1 件の失敗は他へ波及させない。状態は変えず、次回に回す
            cancellationToken.ThrowIfCancellationRequested();
            summary.Failed++;
            Log(IngestLogLevel.Error, "RecoveryFailed", sourceId, item.Id, ex.GetType().Name);
            return false;
        }
    }

    private async Task ReconcileAsync(
        SourcePlan plan,
        SourceRunSummary summary,
        IngestItem item,
        CancellationToken cancellationToken)
    {
        var target = plan.Source.Target;
        var sourceId = plan.Source.SourceId;
        if (item.ProjectionHash is not { } expected)
        {
            await HoldFromAsync(summary, sourceId, item, IngestState.Unknown, "ReconcileNoProjection", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var attempts = Math.Max(1, api.Options.ReconcileSearchAttempts);
        KeyLookup lookup = default!;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            lookup = await api.FindByKeyAsync(
                target.SiteId,
                target.ExternalKeyColumn,
                target.ProjectionHashColumn,
                item.ExternalKey,
                cancellationToken).ConfigureAwait(false);
            if (lookup.Outcome != KeyLookupOutcome.None || attempt == attempts)
            {
                break;
            }

            await api.DelayAsync(api.Options.ReconcileInterval, cancellationToken).ConfigureAwait(false);
        }

        switch (lookup.Outcome)
        {
            case KeyLookupOutcome.Failed:
                summary.ReconcileDeferred++;
                Log(IngestLogLevel.Warning, "ReconcileLookupFailed", sourceId, item.Id, FailureCode(lookup.Failure), lookup.Failure?.StatusCode);
                return;
            case KeyLookupOutcome.One:
                if (!string.Equals(lookup.ProjectionHash, expected, StringComparison.Ordinal))
                {
                    await HoldFromAsync(summary, sourceId, item, IngestState.Unknown, "ReconcileMismatch", cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                if (await store.TransitionAsync(
                        item.Id,
                        IngestState.Unknown,
                        IngestState.AckPending,
                        new TransitionData { RecordId = lookup.RecordId },
                        cancellationToken).ConfigureAwait(false))
                {
                    await CompleteAckAsync(plan, item, cancellationToken).ConfigureAwait(false);
                    summary.Reconciled++;
                }

                return;
            case KeyLookupOutcome.Multiple:
                await HoldFromAsync(summary, sourceId, item, IngestState.Unknown, "ReconcileMultiple", cancellationToken)
                    .ConfigureAwait(false);
                return;
            case KeyLookupOutcome.Inconclusive:
                await HoldFromAsync(summary, sourceId, item, IngestState.Unknown, "ReconcileInconclusive", cancellationToken)
                    .ConfigureAwait(false);
                return;
            default:
                // 時間を空けても 0 件。即 Create せず、人が確認する（再 Create は運用者の明示再開だけ）
                await HoldFromAsync(summary, sourceId, item, IngestState.Unknown, "ReconcileNotFound", cancellationToken)
                    .ConfigureAwait(false);
                return;
        }
    }

    /// <summary>受信後操作（<c>leave</c> は何もしない）を完了して <see cref="IngestState.Completed"/> にする。markSeen・move・delete は #84。</summary>
    private async Task CompleteAckAsync(SourcePlan plan, IngestItem item, CancellationToken cancellationToken)
    {
        await store.SetItemAckStateAsync(item.Id, AckState.Done, cancellationToken).ConfigureAwait(false);
        await store.TransitionAsync(item.Id, IngestState.AckPending, IngestState.Completed, null, cancellationToken)
            .ConfigureAwait(false);
        Log(IngestLogLevel.Information, "ItemCompleted", plan.Source.SourceId, item.Id);
    }

    private async Task HoldFromAsync(
        SourceRunSummary summary,
        string sourceId,
        IngestItem item,
        IngestState from,
        string code,
        CancellationToken cancellationToken)
    {
        if (await store.TransitionAsync(item.Id, from, IngestState.Held, new TransitionData { LastErrorCode = code }, cancellationToken)
                .ConfigureAwait(false))
        {
            summary.CountHeld(code);
            Log(IngestLogLevel.Warning, "ItemHeld", sourceId, item.Id, code);
        }
    }

    private async Task ProcessAsync(
        SourcePlan plan,
        IMailSession session,
        MailCandidate candidate,
        SourceRunSummary summary,
        CancellationToken cancellationToken)
    {
        var source = plan.Source;
        var destinationId = plan.Destination.DestinationId;
        var target = source.Target;

        // 取得（サーバ上のメールは変えない）。上限を超えたら取り込まず、保留として残す
        var raw = await session.OpenRawAsync(candidate, source.Limits.RawBytes, cancellationToken).ConfigureAwait(false);
        if (raw.Stream is null)
        {
            await HoldByLocationAsync(plan, summary, candidate, "RawTooLarge", cancellationToken).ConfigureAwait(false);
            if (source.Protocol == MailProtocol.Pop3)
            {
                // POP3 は上限超過で転送を打ち切ると接続が不定になる（呼び出し側が破棄する）。この接続ではこれ以上取らない
                summary.StopReason ??= "Pop3SessionUnusable";
            }

            return;
        }

        NormalizeOutcome outcome;
        await using (raw.Stream.ConfigureAwait(false))
        {
            outcome = await normalizer
                .NormalizeAsync(raw.Stream, source.Limits, _time.GetUtcNow(), receivedAt: null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!outcome.IsSuccess || outcome.Mail is not { } mail)
        {
            var code = outcome.FailureCode ?? "InvalidMime";
            if (outcome.RawSha256 is { } failedRaw)
            {
                var rawKey = ExternalKey.CreateFromRawHash(destinationId, target.SiteId, source.SourceId, failedRaw);
                await HoldNewAsync(
                    plan, summary, candidate, rawKey, ExternalKey.RawVersion, rawKey, code, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await HoldByLocationAsync(plan, summary, candidate, code, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // 重複排除の外部キー（Message-ID が妥当なら Message-ID、欠落・不正なら RAW の SHA-256 の別名前空間）
        string key;
        string keyVersion;
        if (outcome.MessageIdIsValid && mail.MessageId is { } messageId)
        {
            key = ExternalKey.Create(destinationId, target.SiteId, source.SourceId, messageId);
            keyVersion = ExternalKey.Version;
        }
        else if (outcome.RawSha256 is { } rawSha)
        {
            key = ExternalKey.CreateFromRawHash(destinationId, target.SiteId, source.SourceId, rawSha);
            keyVersion = ExternalKey.RawVersion;
        }
        else
        {
            await HoldByLocationAsync(plan, summary, candidate, "NoIdentity", cancellationToken).ConfigureAwait(false);
            return;
        }

        var contentHash = ContentHash.Compute(mail, outcome.AttachmentDigests);

        // 状態確保。同じキーが既にあれば既処理（内容ハッシュを比べる）
        var ensure = await store.EnsureAsync(
            new NewIngestItem(destinationId, target.SiteId, source.SourceId, key, keyVersion, contentHash, target.MappingRevision),
            cancellationToken).ConfigureAwait(false);
        var item = ensure.Item;

        if (!ensure.Created)
        {
            if (DuplicateJudge.Judge(item.ContentHash, contentHash) == DuplicateOutcome.Conflict)
            {
                // 同一キー・別内容。自動で更新・上書きせず、このメールを保留にして残す（削除・既読化しない）
                await HoldByLocationAsync(plan, summary, candidate, "ContentConflict", cancellationToken, contentHash)
                    .ConfigureAwait(false);
                return;
            }

            switch (item.State)
            {
                case IngestState.Pending:
                    break;
                case IngestState.RetryPending:
                    if (item.NextAttemptAt is { } next && next > _time.GetUtcNow())
                    {
                        summary.Deferred++;
                        return;
                    }

                    if (!await store.TransitionAsync(item.Id, IngestState.RetryPending, IngestState.Pending, null, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return;
                    }

                    break;
                default:
                    // 同じメール（別の位置に現れた複製を含む）。再作成しない。位置だけ記録する
                    await store.AddLocationAsync(item.Id, source.SourceId, candidate.Location, cancellationToken).ConfigureAwait(false);
                    if (item.State == IngestState.Completed)
                    {
                        await store.SetAckStateAsync(source.SourceId, candidate.Location.LocationKey, AckState.Done, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    summary.Duplicates++;
                    return;
            }
        }

        await store.AddLocationAsync(item.Id, source.SourceId, candidate.Location, cancellationToken).ConfigureAwait(false);

        // 対応づけ。1 列でも変換できなければ、そのメール全体を失敗にする（一部の列だけ送らない）
        var mapped = plan.Mapping.Evaluate(mail, plan.Context);
        if (mapped.Values is not { } values)
        {
            await HoldAsync(summary, source.SourceId, item, "MappingFailed", cancellationToken).ConfigureAwait(false);
            return;
        }

        var projectionHash = ProjectionHash.Compute(values, target.MappingRevision, plan.ReservedColumns);
        var reserved = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [target.ExternalKeyColumn] = key,
            [target.ProjectionHashColumn] = projectionHash,
        };
        var record = mapped.ToApiRecord(plan.Context, reserved);

        // 既存キー照合。サーバに同じキーのレコードがあるなら、作らない
        var lookup = await api.FindByKeyAsync(
            target.SiteId,
            target.ExternalKeyColumn,
            target.ProjectionHashColumn,
            key,
            cancellationToken).ConfigureAwait(false);

        switch (lookup.Outcome)
        {
            case KeyLookupOutcome.Failed:
                // 検索は非更新。状態を進めず（Pending のまま）次回へ残す。止める判断（認証・429・日次枠）は api.HaltReason
                summary.RetryLater++;
                Log(IngestLogLevel.Warning, "LookupFailed", source.SourceId, item.Id, FailureCode(lookup.Failure), lookup.Failure?.StatusCode);
                return;
            case KeyLookupOutcome.Multiple:
                await HoldAsync(summary, source.SourceId, item, "DuplicateKeyOnServer", cancellationToken).ConfigureAwait(false);
                return;
            case KeyLookupOutcome.Inconclusive:
                await HoldAsync(summary, source.SourceId, item, "LookupInconclusive", cancellationToken).ConfigureAwait(false);
                return;
            case KeyLookupOutcome.One:
                if (!string.Equals(lookup.ProjectionHash, projectionHash, StringComparison.Ordinal))
                {
                    // DB に無いのにサーバにある（DB 消失・外部の作成・手動編集）。人が確認する。上書きも再作成もしない
                    await HoldAsync(summary, source.SourceId, item, "ServerRecordDiffers", cancellationToken).ConfigureAwait(false);
                    return;
                }

                // 同じキーで内容も一致するレコードがある。再作成せず RecordId を回復して確定する
                if (!await store.TransitionAsync(
                        item.Id,
                        IngestState.Pending,
                        IngestState.Sending,
                        new TransitionData { ProjectionHash = projectionHash },
                        cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await CommitAsync(plan, item, lookup.RecordId!.Value, projectionHash, cancellationToken).ConfigureAwait(false);
                summary.Recovered++;
                return;
        }

        if (api.HaltReason is not null)
        {
            // 検索の応答で、日次枠の残量 0・認証拒否・429 を知った。新規の送信へ進まず、Pending のまま次回へ残す
            summary.RetryLater++;
            return;
        }

        // 作成。送信前に Sending を永続化する（再起動で残ったら結果不明として扱う。投射ハッシュも先に残して照合に使う）
        if (!await store.TransitionAsync(
                item.Id,
                IngestState.Pending,
                IngestState.Sending,
                new TransitionData { ProjectionHash = projectionHash },
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var result = await api.CreateAsync(target.SiteId, record, cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess && result.Id is > 0)
        {
            await CommitAsync(plan, item, result.Id.Value, projectionHash, cancellationToken).ConfigureAwait(false);
            summary.Created++;
            return;
        }

        var errorCode = FailureCode(result);
        if (result.MayHaveBeenApplied)
        {
            // 反映済みの可能性がある。単純に再送しない。外部キーで照合してから判断する
            await store.TransitionAsync(
                item.Id,
                IngestState.Sending,
                IngestState.Unknown,
                new TransitionData { LastErrorCode = errorCode },
                cancellationToken).ConfigureAwait(false);
            summary.Unknown++;
            Log(IngestLogLevel.Warning, "CreateResultUnknown", source.SourceId, item.Id, errorCode, result.StatusCode);
        }
        else if (result.ErrorKind == PleasanterErrorKind.Unauthorized)
        {
            // 未作成が確実（認証の拒否）。メールの問題ではないので、保留にせず次回へ回す。毎通の再認証ループはしない
            await store.TransitionAsync(
                item.Id,
                IngestState.Sending,
                IngestState.RetryPending,
                new TransitionData { LastErrorCode = errorCode },
                cancellationToken).ConfigureAwait(false);
            summary.RetryLater++;
            Log(IngestLogLevel.Error, "CreateUnauthorized", source.SourceId, item.Id, errorCode, result.StatusCode);
        }
        else
        {
            // 400 / 403 / 404 などの恒久エラー。メールを保留にして残す
            await store.TransitionAsync(
                item.Id,
                IngestState.Sending,
                IngestState.Held,
                new TransitionData { LastErrorCode = errorCode },
                cancellationToken).ConfigureAwait(false);
            summary.CountHeld(errorCode);
            Log(IngestLogLevel.Warning, "ItemHeld", source.SourceId, item.Id, errorCode, result.StatusCode);
        }
    }

    /// <summary>API 成功を DB へ確定し、受信後操作（<c>leave</c> は何もしない）を完了して <see cref="IngestState.Completed"/> にする。</summary>
    private async Task CommitAsync(
        SourcePlan plan,
        IngestItem item,
        long recordId,
        string projectionHash,
        CancellationToken cancellationToken)
    {
        bool committed;
        try
        {
            committed = await store.TransitionAsync(
                item.Id,
                IngestState.Sending,
                IngestState.AckPending,
                new TransitionData { RecordId = recordId, ProjectionHash = projectionHash },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // API は成功している。受信後操作へ進まず、失敗として止める（Sending が残る → 次回は結果不明として照合する）
            throw new IngestCommitException("Committing to the database after the API call succeeded failed.");
        }

        if (!committed)
        {
            throw new IngestCommitException("Committing to the database after the API call succeeded was rejected because of a state mismatch.");
        }

        await CompleteAckAsync(plan, item, cancellationToken).ConfigureAwait(false);
    }

    private async Task HoldAsync(
        SourceRunSummary summary,
        string sourceId,
        IngestItem item,
        string code,
        CancellationToken cancellationToken)
    {
        if (await store.TransitionAsync(
                item.Id,
                IngestState.Pending,
                IngestState.Held,
                new TransitionData { LastErrorCode = code },
                cancellationToken).ConfigureAwait(false))
        {
            summary.CountHeld(code);
            Log(IngestLogLevel.Warning, "ItemHeld", sourceId, item.Id, code);
        }
    }

    /// <summary>外部キーを作れないメールを、サーバ上の位置から作った識別で保留として残す。</summary>
    private Task HoldByLocationAsync(
        SourcePlan plan,
        SourceRunSummary summary,
        MailCandidate candidate,
        string code,
        CancellationToken cancellationToken,
        string? contentHash = null)
    {
        var key = ExternalKey.CreateFromLocation(
            plan.Destination.DestinationId,
            plan.Source.Target.SiteId,
            plan.Source.SourceId,
            candidate.Location.LocationKey);
        return HoldNewAsync(plan, summary, candidate, key, ExternalKey.LocationVersion, contentHash ?? key, code, cancellationToken);
    }

    private async Task HoldNewAsync(
        SourcePlan plan,
        SourceRunSummary summary,
        MailCandidate candidate,
        string key,
        string keyVersion,
        string contentHash,
        string code,
        CancellationToken cancellationToken)
    {
        var source = plan.Source;
        var ensure = await store.EnsureAsync(
            new NewIngestItem(
                plan.Destination.DestinationId,
                source.Target.SiteId,
                source.SourceId,
                key,
                keyVersion,
                contentHash,
                source.Target.MappingRevision),
            cancellationToken).ConfigureAwait(false);
        await store.AddLocationAsync(ensure.Item.Id, source.SourceId, candidate.Location, cancellationToken).ConfigureAwait(false);

        // 既に保留・処理済みなら、記録だけ（二重に数えない）
        if (ensure.Item.State == IngestState.Pending)
        {
            await HoldAsync(summary, source.SourceId, ensure.Item, code, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FailureCode(PleasanterResult? result) => result switch
    {
        null => "ApiFailed",
        { IsInvalidResponse: true } => "ApiInvalidResponse",
        { StatusCode: { } status } => $"Api{status}",
        _ => "ApiNoResponse",
    };

    private void Log(
        IngestLogLevel level,
        string name,
        string sourceId,
        Guid? itemId = null,
        string? code = null,
        int? httpStatus = null,
        int? count = null,
        long? durationMs = null)
        => _log.Write(new IngestLogEntry(level, name, sourceId, itemId, code, httpStatus, count, durationMs));
}
