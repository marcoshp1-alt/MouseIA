using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MouseIA.Services;

internal static class ImageUtil
{
    public static BitmapSource Crop(BitmapSource source, Int32Rect rect)
    {
        var bounded = new Int32Rect(
            Math.Clamp(rect.X, 0, source.PixelWidth - 1),
            Math.Clamp(rect.Y, 0, source.PixelHeight - 1),
            0, 0);
        bounded.Width = Math.Clamp(rect.Width, 1, source.PixelWidth - bounded.X);
        bounded.Height = Math.Clamp(rect.Height, 1, source.PixelHeight - bounded.Y);
        var cropped = new CroppedBitmap(source, bounded);
        // Copy the pixels so the full screenshot can be released.
        var copy = FromBgra(ToBgra(cropped, out int w, out int h), w, h);
        return copy;
    }

    /// <summary>Scales so the longest side is at most maxSide (never enlarges unless allowUpscale).</summary>
    public static BitmapSource Fit(BitmapSource source, double maxSide, bool allowUpscale = false)
    {
        double longest = Math.Max(source.PixelWidth, source.PixelHeight);
        double factor = maxSide / longest;
        if (factor >= 1 && !allowUpscale) return source;
        if (Math.Abs(factor - 1) < 0.01) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return scaled;
    }

    public static BitmapSource Scale(BitmapSource source, double factor)
    {
        if (Math.Abs(factor - 1) < 0.01) return source;
        var scaled = new TransformedBitmap(source, new ScaleTransform(factor, factor));
        scaled.Freeze();
        return scaled;
    }

    public static byte[] ToBgra(BitmapSource source, out int width, out int height)
    {
        BitmapSource bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        width = bgra.PixelWidth;
        height = bgra.PixelHeight;
        var pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    public static BitmapSource FromBgra(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static string ToJpegBase64(BitmapSource source, double maxSide, int quality = 88)
    {
        var fitted = Fit(source, maxSide);
        BitmapSource opaque = new FormatConvertedBitmap(Flatten(fitted), PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return Convert.ToBase64String(stream.ToArray());
    }

    /// <summary>
    /// JPEG has no alpha: blends transparent pixels onto white so they don't turn black.
    /// Pure pixel math, so it is safe on worker threads.
    /// </summary>
    public static BitmapSource Flatten(BitmapSource source)
    {
        var format = source.Format;
        bool hasAlpha = format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 ||
                        format == PixelFormats.Prgba64 || format == PixelFormats.Rgba64 || format == PixelFormats.Rgba128Float;
        if (!hasAlpha) return source;
        var pixels = ToBgra(source, out int w, out int h);
        bool any = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            byte a = pixels[i];
            if (a == 255) continue;
            any = true;
            int inv = 255 - a;
            pixels[i - 3] = (byte)((pixels[i - 3] * a + 255 * inv) / 255);
            pixels[i - 2] = (byte)((pixels[i - 2] * a + 255 * inv) / 255);
            pixels[i - 1] = (byte)((pixels[i - 1] * a + 255 * inv) / 255);
            pixels[i] = 255;
        }
        return any ? FromBgra(pixels, w, h) : source;
    }

    public static byte[] ToPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    public static BitmapSource Load(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        // Normalize to BGRA so later steps never deal with odd formats (CMYK, indexed...).
        var normalized = FromBgra(ToBgra(frame, out int w, out int h), w, h);
        return normalized;
    }
}
