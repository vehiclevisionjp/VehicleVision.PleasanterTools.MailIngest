using Dapper;
using VehicleVision.PleasanterTools.MailIngest.Core.State;

namespace VehicleVision.PleasanterTools.MailIngest.Data;

/// <summary>取り込み状態を RDBMS へ保管する。</summary>
/// <remarks>
/// <para>SQL は <see cref="SqlDialect.Format"/> を通して書く（識別子は角括弧で囲む）。方言差を持ち込まない。
/// 日時は <see cref="DbTime.ForDb"/> で書き、<see cref="DbTime.AsUtc(DateTime)"/> で読む。</para>
/// <para>本文・RAW・パスワード・ApiKey・添付バイナリは保存しない。</para>
/// <para>初期は 1 台・SQLite の単発 CLI を前提とする（他の RDBMS・複数ホストは未検証）。</para>
/// </remarks>
public sealed class IngestStateStore(IDbConnectionFactory connections, TimeProvider? time = null) : IIngestStateStore
{
    private const string ActiveToken = "active";

    private const string ItemColumns =
        "[Id], [DestinationId], [SiteId], [SourceId], [ExternalKey], [KeyVersion], [ContentHash], [ProjectionHash], "
        + "[RecordId], [MappingRevision], [State], [AttemptCount], [NextAttemptAt], [LastErrorCode], [CreatedAt], [UpdatedAt]";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private string Sql(string sql) => SqlDialect.Format(connections.Provider, sql);

    private DateTime Now() => DbTime.ForDb(_time.GetUtcNow().UtcDateTime);

    public async Task<IngestItem?> FindAsync(
        string destinationId,
        long siteId,
        string sourceId,
        string externalKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            Sql($"SELECT {ItemColumns} FROM [IngestItems] WHERE [DestinationId] = @DestinationId AND [SiteId] = @SiteId "
                + "AND [SourceId] = @SourceId AND [ExternalKey] = @ExternalKey"),
            new { DestinationId = destinationId, SiteId = siteId, SourceId = sourceId, ExternalKey = externalKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToItem();
    }

    public async Task<EnsureResult> EnsureAsync(NewIngestItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var existing = await FindAsync(item.DestinationId, item.SiteId, item.SourceId, item.ExternalKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new EnsureResult(existing, Created: false);
        }

        var id = Guid.NewGuid();
        var now = Now();
        try
        {
            await using var connection = connections.Create();
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [IngestItems] ([Id], [DestinationId], [SiteId], [SourceId], [ExternalKey], [KeyVersion], "
                    + "[ContentHash], [MappingRevision], [State], [AttemptCount], [CreatedAt], [UpdatedAt]) "
                    + "VALUES (@Id, @DestinationId, @SiteId, @SourceId, @ExternalKey, @KeyVersion, "
                    + "@ContentHash, @MappingRevision, @State, 0, @Now, @Now)"),
                new
                {
                    Id = id.ToString("D"),
                    item.DestinationId,
                    item.SiteId,
                    item.SourceId,
                    item.ExternalKey,
                    item.KeyVersion,
                    item.ContentHash,
                    item.MappingRevision,
                    State = nameof(IngestState.Pending),
                    Now = now,
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 一意制約で競合した（他の実行が先に確保した）なら、既存を返す。それ以外の失敗は伝える
            existing = await FindAsync(item.DestinationId, item.SiteId, item.SourceId, item.ExternalKey, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                throw;
            }

            return new EnsureResult(existing, Created: false);
        }

        var created = await FindAsync(item.DestinationId, item.SiteId, item.SourceId, item.ExternalKey, cancellationToken)
            .ConfigureAwait(false);
        return new EnsureResult(created!, Created: true);
    }

    public async Task<bool> TransitionAsync(
        Guid id,
        IngestState from,
        IngestState to,
        TransitionData? data = null,
        CancellationToken cancellationToken = default)
    {
        if (!IngestStateTransitions.IsAllowed(from, to))
        {
            throw new InvalidOperationException($"許されない状態遷移: {from} -> {to}");
        }

        data ??= new TransitionData();
        await using var connection = connections.Create();
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [IngestItems] SET [State] = @To, "
                + "[AttemptCount] = [AttemptCount] + @AttemptIncrement, "
                + "[RecordId] = COALESCE(@RecordId, [RecordId]), "
                + "[ProjectionHash] = COALESCE(@ProjectionHash, [ProjectionHash]), "
                + "[LastErrorCode] = COALESCE(@LastErrorCode, [LastErrorCode]), "
                + "[NextAttemptAt] = COALESCE(@NextAttemptAt, [NextAttemptAt]), "
                + "[UpdatedAt] = @Now "
                + "WHERE [Id] = @Id AND [State] = @From"),
            new
            {
                Id = id.ToString("D"),
                From = from.ToString(),
                To = to.ToString(),
                AttemptIncrement = to == IngestState.Sending ? 1 : 0,
                data.RecordId,
                data.ProjectionHash,
                data.LastErrorCode,
                NextAttemptAt = data.NextAttemptAt is { } next ? DbTime.ForDb(next.UtcDateTime) : (DateTime?)null,
                Now = Now(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<int> MarkInterruptedAsUnknownAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [IngestItems] SET [State] = @Unknown, [UpdatedAt] = @Now "
                + "WHERE [SourceId] = @SourceId AND [State] = @Sending"),
            new
            {
                SourceId = sourceId,
                Unknown = nameof(IngestState.Unknown),
                Sending = nameof(IngestState.Sending),
                Now = Now(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task AddLocationAsync(
        Guid ingestItemId,
        string sourceId,
        MailLocation location,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(location);

        await using var connection = connections.Create();
        var already = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            Sql("SELECT COUNT(*) FROM [MailLocations] WHERE [SourceId] = @SourceId AND [LocationKey] = @LocationKey"),
            new { SourceId = sourceId, location.LocationKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (already > 0)
        {
            return;
        }

        var now = Now();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("INSERT INTO [MailLocations] ([Id], [IngestItemId], [SourceId], [Protocol], [LocationKey], [Folder], "
                + "[UidValidity], [Uid], [Uidl], [AckState], [CreatedAt], [UpdatedAt]) "
                + "VALUES (@Id, @IngestItemId, @SourceId, @Protocol, @LocationKey, @Folder, "
                + "@UidValidity, @Uid, @Uidl, @AckState, @Now, @Now)"),
            new
            {
                Id = Guid.NewGuid().ToString("D"),
                IngestItemId = ingestItemId.ToString("D"),
                SourceId = sourceId,
                location.Protocol,
                location.LocationKey,
                location.Folder,
                location.UidValidity,
                location.Uid,
                location.Uidl,
                AckState = nameof(AckState.Pending),
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IngestItem?> FindByLocationAsync(
        string sourceId,
        string locationKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<ItemRow>(new CommandDefinition(
            Sql("SELECT i.[Id], i.[DestinationId], i.[SiteId], i.[SourceId], i.[ExternalKey], i.[KeyVersion], i.[ContentHash], "
                + "i.[ProjectionHash], i.[RecordId], i.[MappingRevision], i.[State], i.[AttemptCount], i.[NextAttemptAt], "
                + "i.[LastErrorCode], i.[CreatedAt], i.[UpdatedAt] "
                + "FROM [IngestItems] i INNER JOIN [MailLocations] l ON l.[IngestItemId] = i.[Id] "
                + "WHERE l.[SourceId] = @SourceId AND l.[LocationKey] = @LocationKey"),
            new { SourceId = sourceId, LocationKey = locationKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToItem();
    }

    public async Task<bool> SetAckStateAsync(
        string sourceId,
        string locationKey,
        AckState state,
        CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [MailLocations] SET [AckState] = @AckState, [UpdatedAt] = @Now "
                + "WHERE [SourceId] = @SourceId AND [LocationKey] = @LocationKey"),
            new { AckState = state.ToString(), Now = Now(), SourceId = sourceId, LocationKey = locationKey },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed == 1;
    }

    public async Task<SourceRun?> TryBeginRunAsync(
        string sourceId,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = Now();
        try
        {
            await using var connection = connections.Create();
            await connection.ExecuteAsync(new CommandDefinition(
                Sql("INSERT INTO [SourceRuns] ([Id], [SourceId], [OwnerId], [ActiveToken], [Status], [StartedAt]) "
                    + "VALUES (@Id, @SourceId, @OwnerId, @ActiveToken, 'Running', @Now)"),
                new { Id = id.ToString("D"), SourceId = sourceId, OwnerId = ownerId, ActiveToken, Now = now },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // (SourceId, ActiveToken) の一意制約: 同じソースの実行が終わっていない。引き継がない
            if (await FindActiveRunAsync(sourceId, cancellationToken).ConfigureAwait(false) is not null)
            {
                return null;
            }

            throw;
        }

        return new SourceRun(id, sourceId, ownerId, new DateTimeOffset(DbTime.AsUtc(now)));
    }

    public async Task EndRunAsync(Guid runId, string status, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            Sql("UPDATE [SourceRuns] SET [ActiveToken] = @Id, [Status] = @Status, [FinishedAt] = @Now "
                + "WHERE [Id] = @Id AND [ActiveToken] = @ActiveToken"),
            new { Id = runId.ToString("D"), Status = status, Now = Now(), ActiveToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SourceRun?> FindActiveRunAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        await using var connection = connections.Create();
        var row = await connection.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            Sql("SELECT [Id], [SourceId], [OwnerId], [StartedAt] FROM [SourceRuns] "
                + "WHERE [SourceId] = @SourceId AND [ActiveToken] = @ActiveToken"),
            new { SourceId = sourceId, ActiveToken },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null
            ? null
            : new SourceRun(Guid.Parse(row.Id), row.SourceId, row.OwnerId, new DateTimeOffset(DbTime.AsUtc(row.StartedAt)));
    }

    // SQLite は record のコンストラクタ（名前つき）を文字列・整数から戻せないため、行はプロパティの単純なクラスで受ける
    private sealed class ItemRow
    {
        public string Id { get; set; } = string.Empty;

        public string DestinationId { get; set; } = string.Empty;

        public long SiteId { get; set; }

        public string SourceId { get; set; } = string.Empty;

        public string ExternalKey { get; set; } = string.Empty;

        public string KeyVersion { get; set; } = string.Empty;

        public string ContentHash { get; set; } = string.Empty;

        public string? ProjectionHash { get; set; }

        public long? RecordId { get; set; }

        public int MappingRevision { get; set; }

        public string State { get; set; } = string.Empty;

        public int AttemptCount { get; set; }

        public DateTime? NextAttemptAt { get; set; }

        public string? LastErrorCode { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public IngestItem ToItem() => new(
            Guid.Parse(Id),
            DestinationId,
            SiteId,
            SourceId,
            ExternalKey,
            KeyVersion,
            ContentHash,
            ProjectionHash,
            RecordId,
            MappingRevision,
            Enum.Parse<IngestState>(State),
            AttemptCount,
            DbTime.AsUtc(NextAttemptAt) is { } next ? new DateTimeOffset(next) : null,
            LastErrorCode,
            new DateTimeOffset(DbTime.AsUtc(CreatedAt)),
            new DateTimeOffset(DbTime.AsUtc(UpdatedAt)));
    }

    private sealed class RunRow
    {
        public string Id { get; set; } = string.Empty;

        public string SourceId { get; set; } = string.Empty;

        public string OwnerId { get; set; } = string.Empty;

        public DateTime StartedAt { get; set; }
    }
}
