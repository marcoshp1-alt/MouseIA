using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace MouseIA.Services;

/// <summary>Text recognition with the OCR engine built into Windows (offline).</summary>
internal static class OcrService
{
    private static OcrEngine? _engine;

    private static OcrEngine? Engine
    {
        get
        {
            if (_engine != null) return _engine;
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (_engine == null)
            {
                foreach (var language in OcrEngine.AvailableRecognizerLanguages)
                {
                    _engine = OcrEngine.TryCreateFromLanguage(language);
                    if (_engine != null) break;
                }
            }
            return _engine;
        }
    }

    public static bool Available => Engine != null;

    public static async Task<string> RecognizeAsync(BitmapSource image)
    {
        var engine = Engine;
        if (engine == null) return "";

        // Small text reads better enlarged; the engine has a size limit.
        double max = OcrEngine.MaxImageDimension;
        double longest = Math.Max(image.PixelWidth, image.PixelHeight);
        double factor = 1;
        if (Math.Min(image.PixelWidth, image.PixelHeight) < 700 && longest * 2 <= max) factor = 2;
        if (longest * factor > max) factor = max / longest;
        var prepared = ImageUtil.Scale(image, factor);

        var pixels = ImageUtil.ToBgra(prepared, out int width, out int height);
        IBuffer buffer;
        using (var writer = new DataWriter())
        {
            writer.WriteBytes(pixels);
            buffer = writer.DetachBuffer();
        }
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, width, height,
            BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var sb = new StringBuilder();
        foreach (var line in result.Lines)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line.Text);
        }
        var text = sb.ToString();
        var lang = engine.RecognizerLanguage.LanguageTag;
        if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
        {
            // The engine separates CJK characters with spaces.
            text = Regex.Replace(text, @"(?<=[　-鿿＀-￯]) (?=[　-鿿＀-￯])", "");
        }
        return text.Trim();
    }

    public static string LanguagesSummary() =>
        string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.DisplayName));
}
