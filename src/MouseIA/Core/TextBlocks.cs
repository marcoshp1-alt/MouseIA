using System;
using System.Collections.Generic;
using System.Linq;

namespace MouseIA.Core;

/// <summary>A piece of recognized text and where it is, in image pixels.</summary>
public sealed record OcrBox(string Text, double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

/// <summary>
/// Turns recognized words into separate blocks (a title, a paragraph, a button…), so each one
/// can be translated on its own, right where it is.
/// </summary>
public static class TextBlocks
{
    /// <summary>Splits a line's words where there's a wide gap (columns, separate buttons on one row).</summary>
    public static List<OcrBox> SplitLine(IReadOnlyList<OcrBox> words)
    {
        var result = new List<OcrBox>();
        if (words.Count == 0) return result;
        var sorted = words.OrderBy(w => w.X).ToList();
        double height = sorted.Average(w => w.Height);
        var current = new List<OcrBox> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            double gap = sorted[i].X - current[^1].Right;
            if (gap > height * 2.2)
            {
                result.Add(Merge(current, " "));
                current = new List<OcrBox>();
            }
            current.Add(sorted[i]);
        }
        result.Add(Merge(current, " "));
        return result;
    }

    /// <summary>Joins line pieces that sit right under each other with a similar size into blocks.</summary>
    public static List<OcrBox> Group(IEnumerable<OcrBox> lines)
    {
        var blocks = new List<List<OcrBox>>();
        foreach (var line in lines.Where(l => l.Text.Trim().Length > 0).OrderBy(l => l.Y).ThenBy(l => l.X))
        {
            List<OcrBox>? home = null;
            foreach (var block in blocks)
            {
                var last = block[^1];
                double lineHeight = Math.Max(last.Height, line.Height);
                double gap = line.Y - last.Bottom;
                bool closeBelow = gap >= -lineHeight * 0.5 && gap <= lineHeight * 0.85;
                double ratio = line.Height / Math.Max(1, last.Height);
                bool sameSize = ratio > 0.7 && ratio < 1.45;
                double left = block.Min(b => b.X), right = block.Max(b => b.Right);
                double overlap = Math.Min(right, line.Right) - Math.Max(left, line.X);
                bool aligned = overlap > Math.Min(line.Width, right - left) * 0.3 || Math.Abs(line.X - left) < lineHeight * 1.2;
                if (closeBelow && sameSize && aligned)
                {
                    home = block;
                    break;
                }
            }
            if (home == null) blocks.Add(new List<OcrBox> { line });
            else home.Add(line);
        }
        return blocks.Select(b => Merge(b, "\n")).ToList();
    }

    private static OcrBox Merge(List<OcrBox> parts, string separator)
    {
        double x = parts.Min(p => p.X), y = parts.Min(p => p.Y);
        double right = parts.Max(p => p.Right), bottom = parts.Max(p => p.Bottom);
        return new OcrBox(string.Join(separator, parts.Select(p => p.Text.Trim())), x, y, right - x, bottom - y);
    }
}
