using VehicleVision.PleasanterTools.Core.Cli;
using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.MailIngest.Data;

namespace VehicleVision.PleasanterTools.MailIngest.Cli;

/// <summary>状態 DB の接続先の解決。名前（設定名・既定の SQLite ファイル名）は Core.Data がアプリ ID から導く。</summary>
/// <remarks>
/// 設定: <c>MAILINGEST_DB_PROVIDER</c>（既定は sqlite）、<c>MAILINGEST_DB_CONNECTIONSTRING</c>、
/// <c>MAILINGEST_DB_ALLOW_INSECURE</c>。接続文字列は設定ファイルへ書かず、環境変数から読む。
/// </remarks>
internal static class DatabaseSetup
{
    /// <exception cref="InvalidOperationException">設定が不正（値は含まれない）。</exception>
    public static DatabaseConnectionInfo Resolve(Func<string, string?> environment, string contentRoot)
    {
        var database = MailIngestDatabase.Instance;
        var provider = database.ParseProvider(environment(database.ProviderSetting), DatabaseProvider.Sqlite);
        var connectionString = database.ResolveConnectionString(provider, environment(database.ConnectionStringSetting), contentRoot);
        database.EnsureSecure(provider, connectionString, EnvironmentNames.IsTrue(environment(database.AllowInsecureSetting)));
        return new DatabaseConnectionInfo(provider, connectionString);
    }

    /// <summary>Core.Data の <see cref="MigrationCommand"/> を実行する。結果は <paramref name="output"/>、進行・失敗は <paramref name="error"/>。</summary>
    public static async Task<int> MigrateAsync(
        string argument, DatabaseConnectionInfo info, TextWriter output, TextWriter error, CancellationToken cancellationToken) =>
        await MigrationCommand
            .RunAsync(
                MailIngestDatabase.Instance.Migrator, info.Provider, info.ConnectionString, [argument],
                cancellationToken: cancellationToken, output: output, error: error)
            .ConfigureAwait(false);
}
