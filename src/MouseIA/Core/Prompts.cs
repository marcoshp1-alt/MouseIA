using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MouseIA.Core;

public static class Prompts
{
    /// <summary>Instructions for answers about a selection.</summary>
    public static string Assistant(LanguageInfo userLanguage, string? recognizedText, string? fileText, bool hasImage)
    {
        var sb = new StringBuilder();
        sb.Append("You are MouseIA, an assistant that lives in the user's mouse pointer on Windows. ");
        if (hasImage)
            sb.Append("The user selected part of their screen or opened a file; the attached image(s) show it. ");
        else
            sb.Append("The user is asking without any selection. ");
        sb.Append("Answer the user's request about it directly and helpfully. ");
        sb.Append($"Reply in {userLanguage.English} unless the user writes in another language or asks for one. ");
        sb.Append("Be concise: lead with the answer, then short paragraphs or simple bullet lists. ");
        sb.Append("Use plain text with at most **bold** and `code`; no headings, tables or links in markdown syntax. ");
        sb.Append("If the request is about text in the image, read it carefully and quote it exactly when needed.");
        if (!string.IsNullOrWhiteSpace(recognizedText))
        {
            sb.Append("\n\nText recognized in the selection by OCR (may contain small errors):\n\"\"\"\n");
            sb.Append(Trim(recognizedText, 6000));
            sb.Append("\n\"\"\"");
        }
        if (!string.IsNullOrWhiteSpace(fileText))
        {
            sb.Append("\n\nContents of the file the user opened:\n\"\"\"\n");
            sb.Append(Trim(fileText, 20000));
            sb.Append("\n\"\"\"");
        }
        return sb.ToString();
    }

    private static string Name(LanguageInfo l) =>
        l.Native == l.English ? l.English : $"{l.English} ({l.Native})";

    public static string TranslateSystem(LanguageInfo target) =>
        $"You are a translation engine. Your only job is to rewrite the user's text in {Name(target)}. " +
        $"Always answer in {target.English}, never in the source language, and never repeat the original text. " +
        "Output only the translation: no notes, no quotes, no labels, no explanations. " +
        "Keep line breaks and lists. Keep brand names, product names, code and URLs as they are.";

    public static string TranslateRequest(LanguageInfo target, string? text, bool hasImage, bool insist = false)
    {
        var sb = new StringBuilder();
        if (insist)
            sb.Append($"Your previous reply was not translated. This time write it in {target.English} only.\n\n");
        if (!string.IsNullOrWhiteSpace(text))
        {
            sb.Append($"Translate the text between the markers into {Name(target)}.\n<<<\n{Trim(text!, 20000)}\n>>>\n");
            if (hasImage) sb.Append("The attached image shows the same text; use it to fix recognition errors.\n");
        }
        else
        {
            sb.Append($"Translate all the text visible in the attached image into {Name(target)}.\n");
            sb.Append($"If the image has no text, say so in one short sentence in {target.English}.\n");
        }
        sb.Append($"Reply with the {target.English} translation only.");
        return sb.ToString();
    }

    /// <summary>
    /// One request for many separate pieces of on-screen text (a title, labels, paragraphs).
    /// Each comes back on its own numbered line, so it can be drawn back in its place.
    /// </summary>
    public static string TranslateBatchRequest(LanguageInfo target, IReadOnlyList<string> items)
    {
        var sb = new StringBuilder();
        sb.Append($"These are separate pieces of text from a screen (titles, labels, buttons, paragraphs). ");
        sb.Append($"Translate each one into {Name(target)}.\n");
        sb.Append("Reply with exactly one line per piece, in the same order, starting with its number in square brackets, ");
        sb.Append("like: [1] translation. Keep numbers, names, code and URLs as they are. Nothing else.\n\n");
        for (int i = 0; i < items.Count; i++)
            sb.Append('[').Append(i + 1).Append("] ").Append(Regex.Replace(items[i], @"\s+", " ").Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>Reads the numbered lines back; a missing number stays null.</summary>
    public static string?[] ParseBatch(string reply, int count)
    {
        var result = new string?[count];
        int current = -1;
        foreach (var raw in reply.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var m = Regex.Match(line, @"^(?:[-*•]\s*)?\[(\d{1,3})\]\s*(.*)$");
            if (m.Success)
            {
                // A number that isn't ours ends the previous piece too.
                current = int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= count ? n - 1 : -1;
                if (current >= 0) result[current] = CleanPiece(m.Groups[2].Value);
            }
            else if (current >= 0 && line.Length > 0 && result[current] != null)
            {
                result[current] += " " + CleanPiece(line);
            }
        }
        return result;
    }

    /// <summary>Removes markers a model sometimes copies from the request.</summary>
    public static string CleanPiece(string text) =>
        text.Replace("<<<", "").Replace(">>>", "").Trim().Trim('"', '“', '”').Trim();

    /// <summary>Pieces with no letters or digits (bullets, arrows, icons) are left as they are.</summary>
    public static bool IsTranslatable(string text) => text.Any(char.IsLetter);

    /// <summary>
    /// True when a "translation" is basically the source text again (the model echoed it).
    /// Compares the sets of words; short texts are not judged.
    /// </summary>
    public static bool LooksUntranslated(string source, string result)
    {
        static HashSet<string> Words(string s) => new(
            System.Text.RegularExpressions.Regex.Matches(s.ToLowerInvariant(), @"\p{L}{3,}").Select(m => m.Value));
        var a = Words(source);
        var b = Words(result);
        if (a.Count < 6 || b.Count == 0) return false;
        int shared = a.Count(b.Contains);
        return shared >= 0.7 * Math.Min(a.Count, b.Count);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
