using FluentMigrator;

namespace VehicleVision.PleasanterTools.MailIngest.Data.Migrations;

/// <summary>取り込み項目・メールの位置・ソースの実行（設計 取り込み状態とAPI.md 第 3 節）。</summary>
/// <remarks>
/// <para>**4 種の RDBMS で通じる型だけを使う。** ID は文字列（GUID の "D" 形式）、状態は文字列、日時はネイティブ日時型。</para>
/// <para>本文・RAW・パスワード・ApiKey・添付バイナリは保存しない。</para>
/// </remarks>
[Migration(1, "取り込み状態の表")]
public sealed class M0001_IngestState : Migration
{
    public override void Up()
    {
        Create.Table("IngestItems")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("DestinationId").AsString(64).NotNullable()
            .WithColumn("SiteId").AsInt64().NotNullable()
            .WithColumn("SourceId").AsString(64).NotNullable()
            .WithColumn("ExternalKey").AsString(64).NotNullable()
            .WithColumn("KeyVersion").AsString(64).NotNullable()
            .WithColumn("ContentHash").AsString(64).NotNullable()
            .WithColumn("ProjectionHash").AsString(64).Nullable()
            .WithColumn("RecordId").AsInt64().Nullable()
            .WithColumn("MappingRevision").AsInt32().NotNullable()
            .WithColumn("State").AsString(32).NotNullable()
            .WithColumn("AttemptCount").AsInt32().NotNullable().WithDefaultValue(0)
            .WithColumn("NextAttemptAt").AsDateTime2().Nullable()
            .WithColumn("LastErrorCode").AsString(64).Nullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();

        // 同じメール（接続先・サイト・ソース・外部キー）を二重に確保させない
        Create.Index("UX_IngestItems_Key").OnTable("IngestItems")
            .OnColumn("DestinationId").Ascending()
            .OnColumn("SiteId").Ascending()
            .OnColumn("SourceId").Ascending()
            .OnColumn("ExternalKey").Ascending()
            .WithOptions().Unique();
        Create.Index("IX_IngestItems_SourceState").OnTable("IngestItems")
            .OnColumn("SourceId").Ascending()
            .OnColumn("State").Ascending();

        // 同じメールが複数の UID に現れても、レコードと受信後操作を別に追えるよう、位置を分ける
        Create.Table("MailLocations")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("IngestItemId").AsString(36).NotNullable()
            .WithColumn("SourceId").AsString(64).NotNullable()
            .WithColumn("Protocol").AsString(8).NotNullable()
            .WithColumn("LocationKey").AsString(400).NotNullable()
            .WithColumn("Folder").AsString(255).Nullable()
            .WithColumn("UidValidity").AsInt64().Nullable()
            .WithColumn("Uid").AsInt64().Nullable()
            .WithColumn("Uidl").AsString(70).Nullable()
            .WithColumn("AckState").AsString(16).NotNullable()
            .WithColumn("CreatedAt").AsDateTime2().NotNullable()
            .WithColumn("UpdatedAt").AsDateTime2().NotNullable();
        Create.Index("UX_MailLocations_Source_Key").OnTable("MailLocations")
            .OnColumn("SourceId").Ascending()
            .OnColumn("LocationKey").Ascending()
            .WithOptions().Unique();
        Create.Index("IX_MailLocations_Item").OnTable("MailLocations").OnColumn("IngestItemId").Ascending();

        // ソース単位の実行。ActiveToken は実行中だけ 'active'、終了後は実行 ID。
        // 実行中の行を (SourceId, ActiveToken) の一意制約で 1 つに絞る（NULL の扱いが RDBMS で違うため NULL を使わない）
        Create.Table("SourceRuns")
            .WithColumn("Id").AsString(36).NotNullable().PrimaryKey()
            .WithColumn("SourceId").AsString(64).NotNullable()
            .WithColumn("OwnerId").AsString(64).NotNullable()
            .WithColumn("ActiveToken").AsString(36).NotNullable()
            .WithColumn("Status").AsString(32).NotNullable()
            .WithColumn("StartedAt").AsDateTime2().NotNullable()
            .WithColumn("FinishedAt").AsDateTime2().Nullable();
        Create.Index("UX_SourceRuns_Active").OnTable("SourceRuns")
            .OnColumn("SourceId").Ascending()
            .OnColumn("ActiveToken").Ascending()
            .WithOptions().Unique();

        // ⚠️ MySQL は文字列の列が utf8mb3 で作られる（AtomFeed M0001 と同じ理由）。表ごと utf8mb4 へ直す
        foreach (var table in new[] { "IngestItems", "MailLocations", "SourceRuns" })
        {
            IfDatabase("MySql").Execute.Sql(
                $"ALTER TABLE `{table}` CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs;");
        }
    }

    public override void Down()
    {
        Delete.Table("SourceRuns");
        Delete.Table("MailLocations");
        Delete.Table("IngestItems");
    }
}
