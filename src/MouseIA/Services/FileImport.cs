using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using MouseIA.Core;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MouseIA.Services;

internal enum ImportKind { Image, Pdf, Video, Document, Unsupported }

internal sealed class ImportedFile
{
    public required string Name { get; init; }
    public required ImportKind Kind { get; init; }
    public List<BitmapSource> Images { get; } = new();
    public string? Text { get; set; }
    public string? Note { get; set; }
}

/// <summary>Turns files into pictures and text the assistant can read.</summary>
internal static class FileImport
{
    public const int MaxPdfPages = 4;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".ico", ".heic", ".heif", ".avif", ".jxr", ".dds",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".3gp", ".ts", ".mts",
    };

    public static string DialogFilter => (Loc.IsPortuguese ? "Arquivos suportados|" : "Supported files|") + FilterPatterns +
                                         (Loc.IsPortuguese ? "|Todos os arquivos|*.*" : "|All files|*.*");

    private const string FilterPatterns =
        "*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic;*.avif;*.pdf;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;" +
        "*.docx;*.pptx;*.xlsx;*.odt;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.srt;*.log;*.py;*.js;*.cs;*.kt;*.java";

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensions.Contains(ext) || VideoExtensions.Contains(ext) ||
               ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
               DocumentText.IsTextFile(path) || DocumentText.IsOfficeFile(path);
    }

    public static async Task<ImportedFile> ImportAsync(string path)
    {
        var ext = Path.GetExtension(path);
        var name = Path.GetFileName(path);
        if (ImageExtensions.Contains(ext))
        {
            var file = new ImportedFile { Name = name, Kind = ImportKind.Image };
            file.Images.Add(await LoadImageAsync(path));
            return file;
        }
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return await ImportPdfAsync(path, name);
        if (VideoExtensions.Contains(ext)) return await ImportVideoAsync(path, name);
        var text = await Task.Run(() => DocumentText.Extract(path));
        if (text != null) return new ImportedFile { Name = name, Kind = ImportKind.Document, Text = text };
        return new ImportedFile { Name = name, Kind = ImportKind.Unsupported };
    }

    private static async Task<BitmapSource> LoadImageAsync(string path)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return ImageUtil.Load(stream);
            });
        }
        catch (Exception)
        {
            // Formats WPF can't read (HEIC, AVIF...) may still decode through Windows codecs.
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            return await DecodeAsync(stream);
        }
    }

    private static async Task<ImportedFile> ImportPdfAsync(string path, string name)
    {
        var result = new ImportedFile { Name = name, Kind = ImportKind.Pdf };
        var file = await StorageFile.GetFileFromPathAsync(path);
        var pdf = await PdfDocument.LoadFromFileAsync(file);
        uint pages = Math.Min(pdf.PageCount, (uint)MaxPdfPages);
        for (uint i = 0; i < pages; i++)
        {
            using var page = pdf.GetPage(i);
            using var stream = new InMemoryRandomAccessStream();
            var options = new PdfPageRenderOptions { DestinationWidth = 1400 };
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            result.Images.Add(await DecodeAsync(stream));
        }
        if (pdf.PageCount > pages) result.Note = $"{pages}/{pdf.PageCount}";
        return result;
    }

    private static async Task<ImportedFile> ImportVideoAsync(string path, string name)
    {
        var result = new ImportedFile { Name = name, Kind = ImportKind.Video };
        var file = await StorageFile.GetFileFromPathAsync(path);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var props = clip.GetVideoEncodingProperties();
        int width = (int)Math.Max(props.Width, 2), height = (int)Math.Max(props.Height, 2);
        double scale = Math.Min(1.0, 1280.0 / Math.Max(width, height));
        width = Math.Max(2, (int)(width * scale));
        height = Math.Max(2, (int)(height * scale));
        var duration = composition.Duration;
        foreach (var fraction in new[] { 0.1, 0.5, 0.9 })
        {
            var at = TimeSpan.FromTicks((long)(duration.Ticks * fraction));
            using var frame = await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame);
            result.Images.Add(await DecodeAsync(frame));
        }
        result.Note = duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
        return result;
    }

    /// <summary>Decodes an image stream with Windows' own codecs into a WPF bitmap.</summary>
    private static async Task<BitmapSource> DecodeAsync(IRandomAccessStream stream)
    {
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        var buffer = new Windows.Storage.Streams.Buffer((uint)(width * height * 4));
        bitmap.CopyToBuffer(buffer);
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(pixels);
        }
        return ImageUtil.FromBgra(pixels, width, height);
    }
}
