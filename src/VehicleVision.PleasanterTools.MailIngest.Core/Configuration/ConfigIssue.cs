namespace VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

/// <summary>設定検査で見つかった問題・警告。値そのもの（秘密を含み得る）は載せない。</summary>
/// <param name="Path">JSON 上の位置（例: <c>sources[0].tls</c>）。</param>
/// <param name="Code">機械可読の分類コード。</param>
/// <param name="Message">固定の説明文。設定値は含めない。</param>
public sealed record ConfigIssue(string Path, string Code, string Message);

/// <summary>設定の読込み結果。<see cref="Errors"/> が空のときだけ <see cref="Config"/> が有効。</summary>
public sealed class ConfigParseResult
{
    internal ConfigParseResult(
        IngestConfig? config,
        IReadOnlyList<ConfigIssue> errors,
        IReadOnlyList<ConfigIssue> warnings)
    {
        Config = errors.Count == 0 ? config : null;
        Errors = errors;
        Warnings = warnings;
    }

    public IngestConfig? Config { get; }

    public IReadOnlyList<ConfigIssue> Errors { get; }

    public IReadOnlyList<ConfigIssue> Warnings { get; }

    public bool IsValid => Errors.Count == 0;
}

/// <summary>検査中に問題を集める。</summary>
internal sealed class ConfigIssues
{
    private readonly List<ConfigIssue> _errors = [];
    private readonly List<ConfigIssue> _warnings = [];

    public IReadOnlyList<ConfigIssue> Errors => _errors;

    public IReadOnlyList<ConfigIssue> Warnings => _warnings;

    public void Error(string path, string code, string message) => _errors.Add(new ConfigIssue(path, code, message));

    public void Warn(string path, string code, string message) => _warnings.Add(new ConfigIssue(path, code, message));
}
