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

    public static string TranslateSystem(LanguageInfo target) =>
        $"You are a professional translator. Translate into {target.English}. " +
        "Output only the translation: no notes, no quotes, no explanations. " +
        "Keep the original line breaks, lists and formatting. Keep names, code and URLs unchanged. " +
        $"If the text is already in {target.English}, return it unchanged. " +
        $"If there is no text to translate, reply with one short sentence in {target.English} saying no text was found.";

    public static string TranslateRequest(LanguageInfo target, string? text, bool hasImage)
    {
        if (!string.IsNullOrWhiteSpace(text))
            return $"Translate this into {target.English}:\n\n{Trim(text!, 20000)}" +
                   (hasImage ? "\n\n(Use the attached image to fix any recognition errors.)" : "");
        return $"Translate all the text visible in the attached image into {target.English}.";
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
