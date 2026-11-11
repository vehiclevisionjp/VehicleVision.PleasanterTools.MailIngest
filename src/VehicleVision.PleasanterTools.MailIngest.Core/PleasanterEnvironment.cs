using VehicleVision.PleasanterTools.Core.Cli;
using VehicleVision.PleasanterTools.Core.Contracts;
using VehicleVision.PleasanterTools.DotNetClient;

namespace VehicleVision.PleasanterTools.MailIngest.Core;

/// <summary>環境変数から DotNetClient の接続設定を作る（<c>check</c> 用）。</summary>
/// <remarks>
/// <para>名前は製品群で共通の <c>VVPT_PLEASANTER_*</c>（<see cref="PleasanterEnvironmentNames"/>）。
/// 旧名（<c>PLEASANTER_*</c>）は読まない（無視する。検出も案内もしない）。旧名だけが設定された環境は、新名が未設定として扱われる。</para>
/// <para>DotNetClient の <c>PleasanterOptions.FromEnvironment()</c> は版の問い合わせの設定も読むが、
/// 本ツールは版の問い合わせをしない（<c>PostJsonAsync</c> だけを使う）ので、必要な値だけを読んでここで <see cref="PleasanterOptions"/> に入れる。
/// 接続先の検査（https のみ・ループバックだけ http 可）は DotNetClient が行う。</para>
/// <para><c>run</c> は、この仕組みを使わない。設定ファイルが指す環境変数名（<c>NAME</c> / <c>NAME_FILE</c>）から API キーを読む。</para>
/// </remarks>
public static class PleasanterEnvironment
{
    /// <summary>環境変数 <c>VVPT_PLEASANTER_BASE_URL</c> と、<c>VVPT_PLEASANTER_API_KEY</c>（または <c>VVPT_PLEASANTER_API_KEY_FILE</c>）から作る。</summary>
    /// <exception cref="UsageException">未設定・両方指定・ファイルを読めない（メッセージに値を含めない）。</exception>
    public static PleasanterOptions FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>環境変数の読み出しを差し替えて作る（試験用）。</summary>
    /// <exception cref="UsageException">未設定・両方指定・ファイルを読めない（メッセージに値を含めない）。</exception>
    public static PleasanterOptions FromEnvironment(Func<string, string?> getVariable)
    {
        ArgumentNullException.ThrowIfNull(getVariable);
        var baseUrl = getVariable(PleasanterEnvironmentNames.BaseUrl);
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new UsageException($"Set {PleasanterEnvironmentNames.BaseUrl}.");
        }

        var apiKey = PleasanterApiKey.Resolve(getVariable)
            ?? throw new UsageException($"Set {PleasanterEnvironmentNames.ApiKey} or {PleasanterEnvironmentNames.ApiKeyFile}.");
        return new PleasanterOptions
        {
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            DetectServerVersion = false,
        };
    }
}
