using VehicleVision.PleasanterTools.MailIngest.Core;
using VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

// check    : 接続確認（PLEASANTER_BASE_URL / PLEASANTER_API_KEY）。
// validate : 設定 JSON を検査する（接続しない。秘密の値は読まない）。
const string usage =
    "Usage: VehicleVision.PleasanterTools.MailIngest check | validate --config <path>";

if (args is ["check"])
{
    return await CheckAsync();
}

if (args is ["validate", "--config", var path])
{
    return Validate(path);
}

Console.Error.WriteLine(usage);
return 2;

static async Task<int> CheckAsync()
{
    try
    {
        using var httpClient = new HttpClient();
        var client = new PleasanterApiClient(httpClient, PleasanterOptions.FromEnvironment());
        var connected = await client.CheckConnectionAsync();
        Console.WriteLine(connected ? "Connected." : "Connection failed.");
        return connected ? 0 : 1;
    }
    catch (InvalidOperationException ex)
    {
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
