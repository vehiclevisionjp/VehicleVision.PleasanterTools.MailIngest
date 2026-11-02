using VehicleVision.PleasanterTools.MailIngest.Core.State;
using VehicleVision.PleasanterTools.MailIngest.Data;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Tests;

/// <summary>SQLite の一時 DB での、取り込み状態の試験。</summary>
public sealed class IngestStateStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mailingest-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;

    public IngestStateStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = $"Data Source={Path.Combine(_directory, "mailingest.db")}";
        MailIngestDatabase.Instance.Migrator.MigrateUp(DatabaseProvider.Sqlite, _connectionString);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 一時ファイル。残っても試験結果に影響しない
        }
    }

    private IngestStateStore NewStore(TimeProvider? time = null) =>
        new(new DbConnectionFactory(DatabaseProvider.Sqlite, _connectionString), time);

    private static NewIngestItem Item(string key = "k1", string source = "src", long site = 1, string hash = "h1") =>
        new("dest", site, source, key, "mailingest-external-key-v1", hash, 1);

    [Fact]
    public void アプリ_ID_から名前が導かれ_マイグレーションが適用される()
    {
        Assert.Equal("mailingest", MailIngestDatabase.Instance.AppId);
        Assert.Equal("mailingest.db", MailIngestDatabase.Instance.DefaultSqliteFileName);
        Assert.Equal("MAILINGEST_DB_PROVIDER", MailIngestDatabase.Instance.ProviderSetting);
        Assert.False(MailIngestDatabase.Instance.Migrator.HasPendingMigrations(DatabaseProvider.Sqlite, _connectionString));
    }

    [Fact]
    public async Task 確保は新規なら_Pending_で作り_同じキーなら既存を返す()
    {
        var store = NewStore();

        var first = await store.EnsureAsync(Item());
        var second = await store.EnsureAsync(Item(hash: "different"));

        Assert.True(first.Created);
        Assert.Equal(IngestState.Pending, first.Item.State);
        Assert.Equal(0, first.Item.AttemptCount);
        Assert.False(second.Created);
        Assert.Equal(first.Item.Id, second.Item.Id);
        Assert.Equal("h1", second.Item.ContentHash); // 先着の内容ハッシュを保つ（比較は呼び出し側）
    }

    [Fact]
    public async Task キーはサイト_ソース_接続先ごとに別になる()
    {
        var store = NewStore();

        var a = await store.EnsureAsync(Item());
        var b = await store.EnsureAsync(Item(source: "other"));
        var c = await store.EnsureAsync(Item(site: 2));

        Assert.True(a.Created && b.Created && c.Created);
        Assert.Equal(3, new[] { a.Item.Id, b.Item.Id, c.Item.Id }.Distinct().Count());
    }

    [Fact]
    public async Task 並行した確保でも一意制約で_1件だけが作られる()
    {
        var store = NewStore();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.EnsureAsync(Item())));

        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Item.Id).Distinct());
    }

    [Fact]
    public async Task 正常な遷移で_試行回数_レコード_ID_投射ハッシュを更新する()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = NewStore(time);
        var item = (await store.EnsureAsync(Item())).Item;
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(await store.TransitionAsync(item.Id, IngestState.Pending, IngestState.Sending));
        Assert.True(await store.TransitionAsync(
            item.Id,
            IngestState.Sending,
            IngestState.AckPending,
            new TransitionData { RecordId = 42, ProjectionHash = "p1" }));
        Assert.True(await store.TransitionAsync(item.Id, IngestState.AckPending, IngestState.Completed));

        var done = (await store.FindAsync("dest", 1, "src", "k1"))!;
        Assert.Equal(IngestState.Completed, done.State);
        Assert.Equal(1, done.AttemptCount);
        Assert.Equal(42, done.RecordId);
        Assert.Equal("p1", done.ProjectionHash);
        Assert.Equal(time.GetUtcNow(), done.UpdatedAt);
        Assert.Equal(TimeSpan.FromMinutes(5), done.UpdatedAt - done.CreatedAt);
        Assert.Equal(TimeSpan.Zero, done.UpdatedAt.Offset);
    }

    [Theory]
    [InlineData(IngestState.Pending, IngestState.Completed)]
    [InlineData(IngestState.Pending, IngestState.AckPending)]
    [InlineData(IngestState.Sending, IngestState.Pending)]
    [InlineData(IngestState.Unknown, IngestState.Pending)]
    [InlineData(IngestState.Unknown, IngestState.Sending)]
    [InlineData(IngestState.Held, IngestState.Pending)]
    [InlineData(IngestState.Completed, IngestState.Pending)]
    [InlineData(IngestState.AckPending, IngestState.Sending)]
    public async Task 許されない遷移は拒否する(IngestState from, IngestState to)
    {
        var store = NewStore();
        var item = (await store.EnsureAsync(Item())).Item;

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TransitionAsync(item.Id, from, to));
        Assert.False(IngestStateTransitions.IsAllowed(from, to));
    }

    [Fact]
    public async Task 遷移元の状態が違えば更新せず_false_を返す()
    {
        var store = NewStore();
        var item = (await store.EnsureAsync(Item())).Item;
        await store.TransitionAsync(item.Id, IngestState.Pending, IngestState.Sending);

        // 別の実行が先に進めた後で、同じ Pending → Sending をもう一度試みる
        Assert.False(await store.TransitionAsync(item.Id, IngestState.Pending, IngestState.Sending));
        Assert.Equal(1, (await store.FindAsync("dest", 1, "src", "k1"))!.AttemptCount);
    }

    [Fact]
    public async Task 送信中のまま再起動したら_結果不明にし_自動で再送しない()
    {
        var store = NewStore();
        var sending = (await store.EnsureAsync(Item("a"))).Item;
        var pending = (await store.EnsureAsync(Item("b"))).Item;
        var otherSource = (await store.EnsureAsync(Item("c", source: "other"))).Item;
        await store.TransitionAsync(sending.Id, IngestState.Pending, IngestState.Sending);
        await store.TransitionAsync(otherSource.Id, IngestState.Pending, IngestState.Sending);

        // 再起動後
        var restarted = NewStore();
        var changed = await restarted.MarkInterruptedAsUnknownAsync("src");

        Assert.Equal(1, changed);
        Assert.Equal(IngestState.Unknown, (await restarted.FindAsync("dest", 1, "src", "a"))!.State);
        Assert.Equal(IngestState.Pending, (await restarted.FindAsync("dest", 1, "src", "b"))!.State);
        Assert.Equal(IngestState.Sending, (await restarted.FindAsync("dest", 1, "other", "c"))!.State);
        Assert.NotEqual(Guid.Empty, pending.Id);
    }

    [Fact]
    public async Task 再起動しても状態は残る()
    {
        var store = NewStore();
        var item = (await store.EnsureAsync(Item())).Item;
        await store.TransitionAsync(item.Id, IngestState.Pending, IngestState.Sending);
        await store.TransitionAsync(item.Id, IngestState.Sending, IngestState.Held, new TransitionData { LastErrorCode = "Conflict" });

        var found = await NewStore().FindAsync("dest", 1, "src", "k1");

        Assert.Equal(IngestState.Held, found!.State);
        Assert.Equal("Conflict", found.LastErrorCode);
    }

    [Fact]
    public async Task 待機中の再試行時刻を保存して読める()
    {
        var store = NewStore();
        var item = (await store.EnsureAsync(Item())).Item;
        var next = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        await store.TransitionAsync(item.Id, IngestState.Pending, IngestState.Sending);
        await store.TransitionAsync(item.Id, IngestState.Sending, IngestState.RetryPending, new TransitionData { NextAttemptAt = next });

        var found = (await store.FindAsync("dest", 1, "src", "k1"))!;

        Assert.Equal(IngestState.RetryPending, found.State);
        Assert.Equal(next, found.NextAttemptAt);
    }

    [Fact]
    public async Task 位置から既処理の項目を引け_同じ位置は二重に登録しない()
    {
        var store = NewStore();
        var item = (await store.EnsureAsync(Item())).Item;
        var location = new MailLocation("imap", "imap:INBOX:100:7", "INBOX", 100, 7, null);

        await store.AddLocationAsync(item.Id, "src", location);
        await store.AddLocationAsync(item.Id, "src", location);

        Assert.Equal(item.Id, (await store.FindByLocationAsync("src", "imap:INBOX:100:7"))!.Id);
        Assert.Null(await store.FindByLocationAsync("other", "imap:INBOX:100:7"));
        Assert.Null(await store.FindByLocationAsync("src", "imap:INBOX:100:8"));
    }

    [Fact]
    public async Task 同じメールが複数の位置に現れても追え_受信後操作の状態は位置ごとに持つ()
    {
        var store = NewStore();
        var item = (await store.EnsureAsync(Item())).Item;
        await store.AddLocationAsync(item.Id, "src", new MailLocation("pop3", "pop3:UIDL-A", null, null, null, "UIDL-A"));
        await store.AddLocationAsync(item.Id, "src", new MailLocation("pop3", "pop3:UIDL-B", null, null, null, "UIDL-B"));

        Assert.True(await store.SetAckStateAsync("src", "pop3:UIDL-A", AckState.Done));
        Assert.False(await store.SetAckStateAsync("src", "pop3:UIDL-Z", AckState.Done));

        Assert.Equal(item.Id, (await store.FindByLocationAsync("src", "pop3:UIDL-A"))!.Id);
        Assert.Equal(item.Id, (await store.FindByLocationAsync("src", "pop3:UIDL-B"))!.Id);
    }

    [Fact]
    public async Task 同じソースの実行は同時に開始できず_終了すれば開始できる()
    {
        var store = NewStore();

        var first = await store.TryBeginRunAsync("src", "owner-1");
        var second = await NewStore().TryBeginRunAsync("src", "owner-2");
        var otherSource = await store.TryBeginRunAsync("other", "owner-2");

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.NotNull(otherSource);
        Assert.Equal("owner-1", (await store.FindActiveRunAsync("src"))!.OwnerId);

        await store.EndRunAsync(first.Id, "Completed");

        Assert.Null(await store.FindActiveRunAsync("src"));
        Assert.NotNull(await store.TryBeginRunAsync("src", "owner-2"));
    }

    [Fact]
    public async Task 異常終了で残った実行は_自動で引き継がれない()
    {
        var time = new FakeTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var crashed = await NewStore(time).TryBeginRunAsync("src", "owner-1");
        time.Advance(TimeSpan.FromDays(30));

        Assert.NotNull(crashed);
        Assert.Null(await NewStore(time).TryBeginRunAsync("src", "owner-2"));
    }

    [Fact]
    public async Task 並行した実行開始でも_1つだけが成功する()
    {
        var results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => NewStore().TryBeginRunAsync("src", "owner-" + i)));

        Assert.Single(results, r => r is not null);
    }

    [Fact]
    public void 保存する表に本文_RAW_パスワードなどの列を持たない()
    {
        using var connection = new DbConnectionFactory(DatabaseProvider.Sqlite, _connectionString).Create();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT m.name || '.' || p.name FROM sqlite_master m, pragma_table_info(m.name) p WHERE m.type = 'table' AND m.name IN ('IngestItems', 'MailLocations', 'SourceRuns')";
        var columns = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.DoesNotContain(
            columns,
            c => new[] { "body", "raw", "password", "apikey", "subject", "attachment" }
                .Any(word => c.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.Contains("IngestItems.ExternalKey", columns);
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan span) => _now += span;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
