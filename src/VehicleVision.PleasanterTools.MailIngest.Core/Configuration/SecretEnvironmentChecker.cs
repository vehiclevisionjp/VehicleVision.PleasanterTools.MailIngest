namespace VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

/// <summary>
/// 設定が参照する秘密の環境変数について、<c>NAME</c> と <c>NAME_FILE</c> の同時指定を検査する。
/// </summary>
/// <remarks>値・ファイルのパスは読まず、結果にも載せない。値の解決（ファイル読込み・サイズ上限）は受信実装で行う。</remarks>
public static class SecretEnvironmentChecker
{
    public static (IReadOnlyList<ConfigIssue> Errors, IReadOnlyList<ConfigIssue> Warnings) Check(
        IngestConfig config,
        Func<string, string?> lookup)
    {
        var errors = new List<ConfigIssue>();
        var warnings = new List<ConfigIssue>();

        Check(config.Pleasanter.ApiKeyEnvironmentVariable, "pleasanter.apiKeyEnvironmentVariable");
        for (var i = 0; i < config.Sources.Count; i++)
        {
            var auth = config.Sources[i].Authentication;
            Check(auth.UserEnvironmentVariable, $"sources[{i}].authentication.userEnvironmentVariable");
            Check(auth.PasswordEnvironmentVariable, $"sources[{i}].authentication.passwordEnvironmentVariable");
        }

        return (errors, warnings);

        void Check(string name, string path)
        {
            var direct = !string.IsNullOrEmpty(lookup(name));
            var file = !string.IsNullOrEmpty(lookup(name + "_FILE"));
            if (direct && file)
            {
                errors.Add(new ConfigIssue(path, "SecretConflict", "The environment variable and its _FILE variable are both set. Set only one."));
            }
            else if (!direct && !file)
            {
                warnings.Add(new ConfigIssue(path, "SecretNotSet", "Neither the environment variable nor its _FILE variable is set. It is required at run time."));
            }
        }
    }
}
