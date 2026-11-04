using VehicleVision.PleasanterTools.MailIngest.Core;
using VehicleVision.PleasanterTools.Core.Data;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;
using VehicleVision.PleasanterTools.MailIngest.Data;

// check    : 接続確認（PLEASANTER_BASE_URL / PLEASANTER_API_KEY）。
// validate : 設定 JSON を検査する（接続しない。秘密の値は読まない）。
// migrate / migrate-status : 状態 DB（既定は SQLite の mailingest.db）のマイグレーションを当てる／状態を見る。
//   設定: MAILINGEST_DB_PROVIDER / MAILINGEST_DB_CONNECTIONSTRING / MAILINGEST_DB_ALLOW_INSECURE。
const string usage =
    "Usage: VehicleVision.PleasanterTools.MailIngest check | validate --config <path> | migrate | migrate-status";

if (args is ["check"])
{
    return await CheckAsync();
}

if (args is ["validate", "--config", var path])
{
    return Validate(path);
}

if (args is ["migrate" or "migrate-status"])
{
    return await MigrateAsync(args[0] == "migrate" ? MigrationCommand.ApplyArgument : MigrationCommand.StatusArgument);
}

Console.Error.WriteLine(usage);
return 2;

static async Task<int> MigrateAsync(string argument)
{
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
        return await MigrationCommand.RunAsync(database.Migrator, provider, connectionString, [argument]);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

static async Task<int> CheckAsync()
{
    try
    {
        using var gateway = PleasanterGateway.Create(PleasanterEnvironment.FromEnvironment());
        var connected = await gateway.CheckConnectionAsync();
        Console.WriteLine(connected ? "Connected." : "Connection failed.");
        return connected ? 0 : 1;
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        // 環境変数なし、または BaseUrl 不正（DotNetClient の検査。https のみ・ループバックだけ http 可）。文言に URL・API キーは含まれない
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
}

static int Validate(string path)
{
    string json;
    try
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > IngestConfigParser.MaxConfigBytes)
        {
            Console.Error.WriteLine("error [ConfigFile] 設定ファイルが無い、または大きすぎる。");
            return 2;
        }

        json = File.ReadAllText(path);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("error [ConfigFile] 設定ファイルを読めない。");
        return 2;
    }

    var result = IngestConfigParser.Parse(json);
    var errors = result.Errors.ToList();
    var warnings = result.Warnings.ToList();
    if (result.Config is { } config)
    {
        var (secretErrors, secretWarnings) = SecretEnvironmentChecker.Check(
            config,
            Environment.GetEnvironmentVariable);
        errors.AddRange(secretErrors);
        warnings.AddRange(secretWarnings);
    }

    foreach (var warning in warnings)
    {
        Console.Error.WriteLine($"warning [{warning.Code}] {warning.Path}: {warning.Message}");
    }

    foreach (var error in errors)
    {
        Console.Error.WriteLine($"error [{error.Code}] {error.Path}: {error.Message}");
    }

    if (errors.Count > 0)
    {
        return 2;
    }

    Console.WriteLine($"Configuration is valid ({result.Config!.Sources.Count} source(s)). No connection was made.");
    return 0;
}
