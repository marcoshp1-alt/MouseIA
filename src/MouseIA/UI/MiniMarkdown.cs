using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MouseIA.UI;

/// <summary>Renders the small markdown subset the assistant is asked to use.</summary>
internal static class MiniMarkdown
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    private static readonly Regex Inline = new(@"(\*\*[^*\n]+\*\*|`[^`\n]+`)", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*([-*•])\s+", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*(\d{1,3}[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+", RegexOptions.Compiled);

    public static FlowDocument Render(string text, double fontSize = 13.5)
    {
        var doc = new FlowDocument
        {
            FontFamily = Theme.Font,
            FontSize = fontSize,
            Foreground = Theme.Text,
            PagePadding = new Thickness(0),
            LineHeight = fontSize * 1.45,
        };
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bool inCode = false;
        Paragraph? code = null;
        foreach (var raw in lines)
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                if (!inCode)
                {
                    inCode = true;
                    code = new Paragraph
                    {
                        FontFamily = Mono,
                        FontSize = fontSize - 1,
                        Background = Theme.B("#22000000"),
                        Padding = new Thickness(10, 8, 10, 8),
                        Margin = new Thickness(0, 2, 0, 8),
                    };
                    doc.Blocks.Add(code);
                }
                else
                {
                    inCode = false;
                    code = null;
                }
                continue;
            }
            if (inCode && code != null)
            {
                if (code.Inlines.Count > 0) code.Inlines.Add(new LineBreak());
                code.Inlines.Add(new Run(raw));
                continue;
            }
            if (raw.Trim().Length == 0) continue;

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            var line = raw;
            if (Heading.IsMatch(line))
            {
                line = Heading.Replace(line, "");
                paragraph.FontWeight = FontWeights.SemiBold;
            }
            else if (Bullet.IsMatch(line))
            {
                line = Bullet.Replace(line, "");
                paragraph.Margin = new Thickness(14, 0, 0, 4);
                paragraph.TextIndent = -12;
                paragraph.Inlines.Add(new Run("•  ") { Foreground = Theme.Accent });
            }
            else if (Numbered.Match(line) is { Success: true } m)
            {
                line = line[m.Length..];
                paragraph.Margin = new Thickness(18, 0, 0, 4);
                paragraph.TextIndent = -18;
                paragraph.Inlines.Add(new Run(m.Groups[1].Value + " ") { Foreground = Theme.Muted });
            }
            AddInlines(paragraph, line, fontSize);
            doc.Blocks.Add(paragraph);
        }
        return doc;
    }

    private static void AddInlines(Paragraph paragraph, string line, double fontSize)
    {
        foreach (var part in Inline.Split(line))
        {
            if (part.Length == 0) continue;
            if (part.Length > 4 && part.StartsWith("**") && part.EndsWith("**"))
                paragraph.Inlines.Add(new Bold(new Run(part[2..^2])));
            else if (part.Length > 2 && part[0] == '`' && part[^1] == '`')
                paragraph.Inlines.Add(new Run(part[1..^1])
                {
                    FontFamily = Mono,
                    FontSize = fontSize - 1,
                    Background = Theme.B("#26FFFFFF"),
                });
            else
                paragraph.Inlines.Add(new Run(part));
        }
    }

    /// <summary>Plain text for copying and speaking.</summary>
    public static string ToPlain(string text)
    {
        text = text.Replace("**", "");
        text = Regex.Replace(text, @"^\s{0,3}#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, "^```.*$\\n?", "", RegexOptions.Multiline);
        text = text.Replace("`", "");
        return text.Trim();
    }
}
