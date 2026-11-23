using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using ZXing;

namespace MouseIA.Services;

internal sealed record CodeResult(string Text, string Format)
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
        var pixels = ImageUtil.ToBgra(fitted, out int width, out int height);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
        };
        reader.Options.TryHarder = true;
        reader.Options.TryInverted = true;
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        Result[]? results = null;
        try { results = reader.DecodeMultiple(source); }
        catch (Exception) { }
        if (results == null || results.Length == 0)
        {
            // Small codes in big images: try again enlarged.
            if (Math.Max(image.PixelWidth, image.PixelHeight) < 900)
            {
                var big = ImageUtil.Scale(image, 2);
                pixels = ImageUtil.ToBgra(big, out width, out height);
                try { results = reader.DecodeMultiple(new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32)); }
                catch (Exception) { }
            }
        }
        if (results == null) return Array.Empty<CodeResult>();
        return results.Where(r => !string.IsNullOrEmpty(r.Text))
            .GroupBy(r => r.Text).Select(g => g.First())
            .Select(r => new CodeResult(r.Text, r.BarcodeFormat.ToString().Replace('_', ' ')))
            .ToList();
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
