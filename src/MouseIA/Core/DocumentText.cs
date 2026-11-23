using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MouseIA.Core;

/// <summary>Extracts plain text from text-like files and Office documents, without external libraries.</summary>
public static class DocumentText
{
    public const int MaxChars = 60000;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".log", ".ini", ".cfg", ".conf",
        ".yaml", ".yml", ".toml", ".srt", ".vtt", ".sub", ".rtf", ".tex", ".sql", ".sh", ".bat", ".cmd", ".ps1",
        ".py", ".js", ".ts", ".jsx", ".tsx", ".java", ".kt", ".kts", ".cs", ".c", ".h", ".cpp", ".hpp", ".go", ".rs",
        ".rb", ".php", ".swift", ".dart", ".lua", ".r", ".m", ".gradle", ".properties", ".css", ".scss", ".vue",
    };

    public static bool IsTextFile(string path) => TextExtensions.Contains(Path.GetExtension(path));

    public static bool IsOfficeFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".docx" or ".pptx" or ".xlsx" or ".odt" or ".odp" or ".ods";

    /// <summary>Text of a text-like or Office file, or null when the type isn't handled.</summary>
    public static string? Extract(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (TextExtensions.Contains(ext)) return ReadText(path);
        return ext switch
        {
            ".docx" => FromZip(path, n => n == "word/document.xml", "p", "t", "tab"),
            ".pptx" => FromZip(path, n => n.StartsWith("ppt/slides/slide") && n.EndsWith(".xml"), "p", "t", null),
            ".xlsx" => FromZip(path, n => n == "xl/sharedStrings.xml", "si", "t", null),
            ".odt" or ".odp" or ".ods" => FromZip(path, n => n == "content.xml", "p", null, "tab"),
            _ => null,
        };
    }

    private static string ReadText(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[MaxChars];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, read);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".html" or ".htm") text = StripHtml(text);
        return text;
    }

    private static string StripHtml(string html)
    {
        html = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<(br|/p|/div|/li|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(html);
    }

    /// <summary>Reads text runs from XML parts of a zip-based document, one line per paragraph element.</summary>
    private static string FromZip(string path, Func<string, bool> partFilter, string paragraph, string? textElement, string? tabElement)
    {
        using var zip = ZipFile.OpenRead(path);
        var parts = zip.Entries.Where(e => partFilter(e.FullName))
            .OrderBy(e => NaturalKey(e.FullName)).ToList();
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            using var stream = part.Open();
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            bool inText = textElement == null;
            while (xml.Read() && sb.Length < MaxChars)
            {
                switch (xml.NodeType)
                {
                    case XmlNodeType.Element:
                        if (textElement != null && xml.LocalName == textElement && !xml.IsEmptyElement) inText = true;
                        else if (tabElement != null && xml.LocalName == tabElement) sb.Append('\t');
                        break;
                    case XmlNodeType.EndElement:
                        if (textElement != null && xml.LocalName == textElement) inText = false;
                        else if (xml.LocalName == paragraph) sb.Append('\n');
                        break;
                    case XmlNodeType.Text:
                    case XmlNodeType.SignificantWhitespace:
                        if (inText) sb.Append(xml.Value);
                        break;
                }
            }
            sb.Append('\n');
        }
        var text = Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
        return text.Length > MaxChars ? text[..MaxChars] : text;
    }

    private static string NaturalKey(string name) => Regex.Replace(name, "\\d+", m => m.Value.PadLeft(6, '0'));
}
