using System.Text;

namespace VehicleVision.PleasanterTools.MailIngest.Core.Configuration;

/// <summary>秘密の解決に失敗した。メッセージには変数名だけを含め、値・ファイルのパス・内容は含めない。</summary>
public sealed class SecretResolutionException(string message) : Exception(message);

/// <summary>
/// 秘密を環境変数 <c>NAME</c> か <c>NAME_FILE</c> から解決する（設計 <c>アーキテクチャ方針.md</c> 第 6 節）。
/// </summary>
/// <remarks>
/// <para><c>NAME</c> と <c>NAME_FILE</c> の同時指定はエラー。ファイルは絶対パス・UTF-8・64 KiB 以下で、
/// 末尾の改行 1 個だけを除く。空・読めないものはエラー。JSON の秘密へのフォールバックはしない。</para>
/// <para>OS の権限で実行者以外の読み取りを制限すること。パス・内容はログ・例外へ出さない。</para>
/// </remarks>
public static class SecretResolver
{
    /// <summary>ファイルの大きさの上限。</summary>
    public const int MaxFileBytes = 64 * 1024;

    public static string Resolve(string name, Func<string, string?> environment, Func<string, Stream>? openFile = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(environment);
        openFile ??= path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var direct = environment(name);
        var filePath = environment(name + "_FILE");
        var hasDirect = !string.IsNullOrEmpty(direct);
        var hasFile = !string.IsNullOrEmpty(filePath);

        if (hasDirect && hasFile)
        {
            throw new SecretResolutionException($"{name} と {name}_FILE が同時に指定されている。どちらか一方にする。");
        }

        if (hasDirect)
        {
            return direct!;
        }

        if (!hasFile)
        {
            throw new SecretResolutionException($"{name} も {name}_FILE も設定されていない。");
        }

        if (!Path.IsPathFullyQualified(filePath!))
        {
            throw new SecretResolutionException($"{name}_FILE は絶対パスで指定する。");
        }

        string text;
        try
        {
            using var stream = openFile(filePath!);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxFileBytes)
                {
                    throw new SecretResolutionException($"{name}_FILE のファイルが {MaxFileBytes} バイトを超えている。");
                }

                buffer.Write(chunk, 0, read);
            }

            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (SecretResolutionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // 例外のメッセージはパスを含むため、そのまま出さない
            throw new SecretResolutionException($"{name}_FILE を UTF-8 のテキストとして読めない。");
        }

        if (text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text = text[..^2];
        }
        else if (text.EndsWith('\n'))
        {
            text = text[..^1];
        }

        return text.Length == 0
            ? throw new SecretResolutionException($"{name}_FILE の内容が空。")
            : text;
    }
}
