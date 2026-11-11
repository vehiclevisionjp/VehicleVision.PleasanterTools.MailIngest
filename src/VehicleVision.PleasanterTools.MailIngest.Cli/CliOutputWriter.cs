using System.Text;
using VehicleVision.PleasanterTools.Core.Cli;

namespace VehicleVision.PleasanterTools.MailIngest.Cli;

/// <summary>
/// <see cref="TextWriter"/> を受ける Core.Data の <c>MigrationCommand</c> の出力を、<see cref="CliOutput"/> へ流す。
/// 行ごとに、<c>[OK]</c>・<c>[INFO]</c>・<c>[WARN]</c>・<c>[ERROR]</c> の接頭辞を見て同じ種類で出す（色・ASCII 化・秘密の伏せ・行の排他は
/// <see cref="CliOutput"/> に任せる）。接頭辞の無い行は、stdout 側なら結果、stderr 側なら進行表示。
/// </summary>
internal sealed class CliOutputWriter(CliOutput output, bool toStderr) : TextWriter
{
    private readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            Emit();
        }
        else if (value != '\r')
        {
            _line.Append(value);
        }
    }

    public override void Flush()
    {
        if (_line.Length > 0)
        {
            Emit();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        base.Dispose(disposing);
    }

    private void Emit()
    {
        var text = _line.ToString();
        _line.Clear();
        if (TryStrip(text, "[OK] ", out var rest))
        {
            output.Ok(rest);
        }
        else if (TryStrip(text, "[INFO] ", out rest))
        {
            output.Info(rest);
        }
        else if (TryStrip(text, "[WARN] ", out rest))
        {
            output.Warn(rest);
        }
        else if (TryStrip(text, "[ERROR] ", out rest))
        {
            output.Error(rest);
        }
        else if (toStderr)
        {
            output.Progress(text);
        }
        else
        {
            output.Result(text);
        }
    }

    private static bool TryStrip(string text, string prefix, out string rest)
    {
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            rest = text[prefix.Length..];
            return true;
        }

        rest = string.Empty;
        return false;
    }
}
