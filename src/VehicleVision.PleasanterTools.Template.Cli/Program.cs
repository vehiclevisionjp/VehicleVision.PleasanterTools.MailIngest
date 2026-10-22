using VehicleVision.PleasanterTools.Template.Core;

// 接続確認だけを行う最小の CLI。ツールの実体はここへ足す。
// 接続先は環境変数 PLEASANTER_BASE_URL / PLEASANTER_API_KEY で渡す。
if (args.Length != 1 || args[0] != "check")
{
    Console.Error.WriteLine("Usage: VehicleVision.PleasanterTools.Template check");
    return 2;
}

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
