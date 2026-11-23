using System;
using System.Text.Json;

namespace MouseIA.Core;

/// <summary>A release published on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string? SetupUrl, string? PortableUrl, string PageUrl)
{
    public const string Repository = "odorizzioficial/MouseIA";

    public static string LatestApiUrl => $"https://api.github.com/repos/{Repository}/releases/latest";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>"v1.2.0" or "1.2" → Version; null when the tag isn't a version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(new[] { '-', '+', ' ' });
        if (cut > 0) text = text[..cut];
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? Normalize(v) : null;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Reads GitHub's "latest release" answer. Drafts and pre-releases are ignored.</summary>
    public static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var version = ParseVersion(tag);
        if (version == null) return null;
        string? setup = null, portable = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url == null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)) setup ??= url;
                else if (name.Contains("Portable", StringComparison.OrdinalIgnoreCase)) portable ??= url;
            }
        }
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(version, tag!, notes.Trim(), setup, portable, page);
    }

    public bool IsNewerThan(Version current) => Version > Normalize(current);
}
