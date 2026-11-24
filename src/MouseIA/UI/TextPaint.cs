using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MouseIA.Core;

namespace MouseIA.UI;

/// <summary>Paints translated text right over the original: same place, its background colour, its text colour.</summary>
internal static class TextPaint
{
    /// <summary>The background around a piece of text (median of a thin ring outside it) and the text colour.</summary>
    public static (Color Background, Color Foreground) Colors(byte[] px, int w, int h, OcrBox box)
    {
        int x0 = (int)Math.Floor(box.X), y0 = (int)Math.Floor(box.Y);
        int x1 = (int)Math.Ceiling(box.Right), y1 = (int)Math.Ceiling(box.Bottom);
        var ring = new List<(byte B, byte G, byte R)>();
        void Take(int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return;
            int i = (y * w + x) * 4;
            ring.Add((px[i], px[i + 1], px[i + 2]));
        }
        for (int d = 2; d <= 4; d += 2)
        {
            for (int x = x0 - d; x <= x1 + d; x += 2) { Take(x, y0 - d); Take(x, y1 + d); }
            for (int y = y0 - d; y <= y1 + d; y += 2) { Take(x0 - d, y); Take(x1 + d, y); }
        }
        if (ring.Count == 0) return (Color.FromRgb(30, 30, 30), System.Windows.Media.Colors.White);
        byte Median(Func<(byte B, byte G, byte R), byte> pick)
        {
            var values = ring.Select(pick).OrderBy(v => v).ToList();
            return values[values.Count / 2];
        }
        var bg = Color.FromRgb(Median(p => p.R), Median(p => p.G), Median(p => p.B));

        // The text colour: the pixels inside that differ most from the background.
        var far = new List<(int Dist, byte R, byte G, byte B)>();
        int step = Math.Max(1, (int)Math.Sqrt(Math.Max(1.0, (x1 - x0) * (double)(y1 - y0) / 4000)));
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y += step)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x += step)
            {
                int i = (y * w + x) * 4;
                int dist = Math.Abs(px[i + 2] - bg.R) + Math.Abs(px[i + 1] - bg.G) + Math.Abs(px[i] - bg.B);
                if (dist > 90) far.Add((dist, px[i + 2], px[i + 1], px[i]));
            }
        Color fg;
        if (far.Count >= 4)
        {
            var top = far.OrderByDescending(f => f.Dist).Take(Math.Max(3, far.Count / 3)).ToList();
            fg = Color.FromRgb((byte)top.Average(t => t.R), (byte)top.Average(t => t.G), (byte)top.Average(t => t.B));
        }
        else fg = Luma(bg) > 0.5 ? Color.FromRgb(20, 20, 20) : System.Windows.Media.Colors.White;
        if (Math.Abs(Luma(fg) - Luma(bg)) < 0.3) fg = Luma(bg) > 0.5 ? Color.FromRgb(20, 20, 20) : System.Windows.Media.Colors.White;
        return (bg, fg);
    }

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>The translated text over its own background, sized to sit where the original was.</summary>
    public static Border View(string translation, Rect area, int lines, Color background, Color foreground, double maxWidth, double maxHeight)
    {
        double lineHeight = area.Height / Math.Max(1, lines);
        double baseSize = Math.Clamp(lineHeight * 0.92, 9, 64);
        bool singleLine = lines <= 1;
        var text = new TextBlock
        {
            Text = translation,
            FontFamily = Theme.Font,
            Foreground = new SolidColorBrush(foreground),
            TextWrapping = singleLine ? TextWrapping.NoWrap : TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.None,
        };
        double width = area.Width + 4, height = area.Height + 4;
        double size = baseSize;
        double min = baseSize * (singleLine ? 0.72 : 0.6);
        while (true)
        {
            text.FontSize = size;
            text.Measure(new Size(singleLine ? double.PositiveInfinity : width - 4, double.PositiveInfinity));
            bool fits = singleLine ? text.DesiredSize.Width <= width - 4 : text.DesiredSize.Height <= height - 2;
            if (fits || size <= min) break;
            size = Math.Max(min, size - 0.5);
        }
        // Still too big at the smallest readable size: let the background grow instead.
        if (singleLine) width = Math.Max(width, text.DesiredSize.Width + 6);
        else height = Math.Max(height, text.DesiredSize.Height + 4);

        double x = area.X - 2, y = area.Y - 2;
        if (x + width > maxWidth - 2) x = Math.Max(2, maxWidth - 2 - width);
        if (y + height > maxHeight - 2) y = Math.Max(2, maxHeight - 2 - height);
        var view = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(background),
            Padding = new Thickness(2, 0, 2, 0),
            Child = text,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(view, x);
        Canvas.SetTop(view, y);
        return view;
    }
}
