using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using ZXing;

namespace MouseIA.Services;

/// <param name="Box">Where the code sits in the decoded image, in its pixels (null if unknown).</param>
internal sealed record CodeResult(string Text, string Format, System.Windows.Rect? Box = null)
{
    public bool IsLink => Uri.TryCreate(Text, UriKind.Absolute, out var uri) &&
                          (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public bool IsWifi => Text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads QR codes and barcodes (offline) with ZXing.</summary>
internal static class QrService
{
    public static IReadOnlyList<CodeResult> Decode(BitmapSource image)
    {
        // Large screenshots decode faster and just as well when scaled down.
        var fitted = ImageUtil.Fit(image, 1600);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
        };
        reader.Options.TryHarder = true;
        reader.Options.TryInverted = true;
        var found = Run(reader, fitted, (double)image.PixelWidth / fitted.PixelWidth);
        if (found.Count == 0 && Math.Max(image.PixelWidth, image.PixelHeight) < 900)
        {
            // Small codes: try again enlarged.
            found = Run(reader, ImageUtil.Scale(image, 2), 0.5);
        }
        return found.GroupBy(r => r.Text).Select(g => g.First()).ToList();
    }

    private static List<CodeResult> Run(BarcodeReaderGeneric reader, BitmapSource image, double toOriginal)
    {
        var list = new List<CodeResult>();
        var pixels = ImageUtil.ToBgra(image, out int width, out int height);
        Result[]? results = null;
        try { results = reader.DecodeMultiple(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32)); }
        catch (Exception) { }
        if (results == null) return list;
        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.Text)) continue;
            System.Windows.Rect? box = null;
            var points = r.ResultPoints?.Where(p => p != null).ToList();
            if (points is { Count: >= 2 })
            {
                double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
                double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
                // QR points are the centres of the corner squares: widen to cover the whole code.
                double padX = Math.Max(6, (maxX - minX) * 0.2), padY = Math.Max(6, (maxY - minY) * 0.2);
                if (maxY - minY < 4) padY = Math.Max(padY, 18); // 1D barcodes give a line
                box = new System.Windows.Rect(
                    (minX - padX) * toOriginal, (minY - padY) * toOriginal,
                    (maxX - minX + 2 * padX) * toOriginal, (maxY - minY + 2 * padY) * toOriginal);
            }
            list.Add(new CodeResult(r.Text, r.BarcodeFormat.ToString().Replace('_', ' '), box));
        }
        return list;
    }

    /// <summary>"WIFI:T:WPA;S:name;P:pass;;" -> (name, password).</summary>
    public static (string Ssid, string Password)? ParseWifi(string text)
    {
        if (!text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return null;
        string ssid = "", password = "";
        foreach (var part in SplitEscaped(text[5..]))
        {
            if (part.StartsWith("S:")) ssid = part[2..];
            else if (part.StartsWith("P:")) password = part[2..];
        }
        return (ssid, password);
    }

    private static IEnumerable<string> SplitEscaped(string text)
    {
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length) { current.Append(text[++i]); continue; }
            if (c == ';') { yield return current.ToString(); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
