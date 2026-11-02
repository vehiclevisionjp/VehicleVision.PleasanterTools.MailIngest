using System.Text;
using MimeKit.Text;

namespace VehicleVision.PleasanterTools.MailIngest.Mail.Mime;

/// <summary>
/// HTML から表示用のテキストを取り出す。MimeKit の <see cref="HtmlTokenizer"/> を使った限定的な抽出。
/// </summary>
/// <remarks>
/// <para><b>これは HTML のサニタイザーではない。</b>HTML をそのまま Pleasanter へ保存・表示しない。得られたテキストが
/// Markdown として安全とは限らない（記法のリテラル化は別途。表示仕様は実機未確認）。</para>
/// <para><c>script</c>・<c>style</c>・<c>head</c> の中身を捨て、ブロック要素と <c>br</c> を改行にする。
/// 外部の画像・URL・CSS は取得せず、リンクは表示文字列だけを残す。</para>
/// </remarks>
public static class HtmlTextExtractor
{
    private static readonly HashSet<HtmlTagId> Suppressed = [HtmlTagId.Script, HtmlTagId.Style, HtmlTagId.Head, HtmlTagId.Title];

    private static readonly HashSet<string> LineBreaks = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "p", "div", "tr", "li", "ul", "ol", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "table", "hr",
    };

    public static string ToText(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var builder = new StringBuilder();
        var suppressDepth = 0;
        var tokenizer = new HtmlTokenizer(new StringReader(html));

        while (tokenizer.ReadNextToken(out var token))
        {
            switch (token)
            {
                case HtmlTagToken tag:
                    if (Suppressed.Contains(tag.Id))
                    {
                        if (tag.IsEndTag)
                        {
                            suppressDepth = Math.Max(0, suppressDepth - 1);
                        }
                        else if (!tag.IsEmptyElement)
                        {
                            suppressDepth++;
                        }
                    }
                    else if (suppressDepth == 0 && LineBreaks.Contains(tag.Name))
                    {
                        builder.Append('\n');
                    }

                    break;
                case HtmlDataToken data when suppressDepth == 0 && data.Kind == HtmlTokenKind.Data:
                    builder.Append(CollapseWhitespace(data.Data));
                    break;
                default:
                    break;
            }
        }

        return TidyLines(builder.ToString());
    }

    /// <summary>HTML の空白（連続・改行・タブ）を 1 つの空白にまとめる。</summary>
    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousSpace = false;
        foreach (var c in text)
        {
            if (c is ' ' or '\t' or '\r' or '\n' or '\f' or ' ')
            {
                if (!previousSpace)
                {
                    builder.Append(' ');
                }

                previousSpace = true;
            }
            else
            {
                builder.Append(c);
                previousSpace = false;
            }
        }

        return builder.ToString();
    }

    /// <summary>行頭・行末の空白を除き、3 行以上の連続した空行を 1 行の空行にまとめる。</summary>
    private static string TidyLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim(' ')).ToList();
        var result = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (result.Length > 0)
            {
                result.Append('\n');
                if (blank > 1)
                {
                    result.Append('\n');
                }
            }

            blank = 0;
            result.Append(line);
        }

        return result.ToString();
    }
}
