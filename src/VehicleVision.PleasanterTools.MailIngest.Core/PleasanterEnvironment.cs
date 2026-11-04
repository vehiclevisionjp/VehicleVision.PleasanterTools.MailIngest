using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.MailIngest.Core;

/// <summary>環境変数から DotNetClient の接続設定を作る。</summary>
/// <remarks>
/// DotNetClient の <c>PleasanterOptions.FromEnvironment()</c> は <c>PLEASANTER_SERVER_VERSION</c> も読むが、
/// 本ツールは版の問い合わせをしない（<c>PostJsonAsync</c> だけを使う）ので、必要な 2 つだけを読む。
/// </remarks>
public static class PleasanterEnvironment
{
    /// <summary>環境変数 <c>PLEASANTER_BASE_URL</c> / <c>PLEASANTER_API_KEY</c> から作る。</summary>
    /// <exception cref="InvalidOperationException">環境変数が無い。</exception>
    public static PleasanterOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>環境変数の読み出しを差し替えて作る（試験用）。</summary>
    public static PleasanterOptions FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        return new PleasanterOptions
        {
            BaseUrl = Required(getVariable, "PLEASANTER_BASE_URL"),
            ApiKey = Required(getVariable, "PLEASANTER_API_KEY"),
            DetectServerVersion = false,
        };
    }

    private static string Required(Func<string, string?> getVariable, string name) =>
        getVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"環境変数 {name} が設定されていません。");
}
