namespace VehicleVision.PleasanterTools.Template.Core;

/// <summary>Pleasanter の接続設定を呼び出し時に解決する。</summary>
public interface IPleasanterOptionsProvider
{
    Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>接続先 Pleasanter の設定。</summary>
/// <remarks>
/// 実値は環境変数か Git 管理外の設定ファイルから読む。
/// **API キーを設定ファイルへ直書きしてコミットしないこと。**
/// </remarks>
public sealed class PleasanterOptions
{
    /// <summary>API のバージョンの既定。</summary>
    public const decimal DefaultApiVersion = 1.1m;

    /// <summary>1 回の呼び出しの上限の既定。</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>ベース URL。末尾のスラッシュは付けない。</summary>
    public required string BaseUrl { get; init; }

    /// <summary>API キー。**サーバ側だけが持つ。ブラウザへ渡さない。**</summary>
    public required string ApiKey { get; init; }

    /// <summary>API のバージョン。リクエストボディの <c>ApiVersion</c> に載せる。</summary>
    public decimal ApiVersion { get; init; } = DefaultApiVersion;

    /// <summary>1 回の呼び出しの上限。</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>環境変数 <c>PLEASANTER_BASE_URL</c> / <c>PLEASANTER_API_KEY</c> から作る。</summary>
    public static PleasanterOptions FromEnvironment() => new()
    {
        BaseUrl = Required("PLEASANTER_BASE_URL"),
        ApiKey = Required("PLEASANTER_API_KEY"),
    };

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"環境変数 {name} が設定されていません。");
}

/// <summary>固定した Pleasanter 接続設定を返す。</summary>
public sealed class StaticPleasanterOptionsProvider(PleasanterOptions options)
    : IPleasanterOptionsProvider
{
    public Task<PleasanterOptions> GetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(options);
}
