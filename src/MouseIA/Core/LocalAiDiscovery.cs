using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MouseIA.Core;

public sealed record LocalServer(string Name, string BaseUrl, IReadOnlyList<string> Models);

/// <summary>Finds AI servers running on this PC (Ollama, LM Studio, llama.cpp, LiteRT-LM, Jan).</summary>
public static class LocalAiDiscovery
{
    // 127.0.0.1, not localhost: some servers only listen on IPv4.
    public static readonly (string Name, string Url)[] Known =
    {
        ("Ollama", "http://127.0.0.1:11434/v1"),
        ("LM Studio", "http://127.0.0.1:1234/v1"),
        ("llama.cpp", "http://127.0.0.1:8080/v1"),
        ("LiteRT-LM", "http://127.0.0.1:9379/v1"),
        ("Jan", "http://127.0.0.1:1337/v1"),
    };

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    /// <summary>Checks the known ports, plus an extra address if given.</summary>
    public static async Task<IReadOnlyList<LocalServer>> DetectAsync(string? extraUrl, CancellationToken ct)
    {
        var candidates = Known.ToList();
        if (!string.IsNullOrWhiteSpace(extraUrl))
        {
            var normalized = Normalize(extraUrl);
            if (!candidates.Any(c => SameServer(c.Url, normalized)))
                candidates.Insert(0, (HostLabel(normalized), normalized));
        }
        var probes = candidates.Select(async c =>
        {
            var models = await ListModelsAsync(c.Url, null, ct).ConfigureAwait(false);
            return models == null ? null : new LocalServer(c.Name, c.Url, models);
        });
        var found = await Task.WhenAll(probes).ConfigureAwait(false);
        return found.Where(s => s != null).Select(s => s!).ToList();
    }

    /// <summary>Model ids served at the address, or null if nothing answers there.</summary>
    public static async Task<IReadOnlyList<string>?> ListModelsAsync(string baseUrl, string? key, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AiClient.LocalModelsUrl(baseUrl));
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseModels(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string> ParseModels(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var list = new List<string>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    var name = id.GetString();
                    // Skip embedding-only models: they can't chat.
                    if (!string.IsNullOrEmpty(name) && !name.Contains("embed", StringComparison.OrdinalIgnoreCase))
                        list.Add(name);
                }
            }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static string Normalize(string url)
    {
        var chat = AiClient.LocalChatUrl(url);
        var baseUrl = chat[..^"/chat/completions".Length];
        // "localhost" can resolve to IPv6 first, which Ollama doesn't listen on.
        return baseUrl.Replace("://localhost", "://127.0.0.1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameServer(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string HostLabel(string url)
    {
        try { var uri = new Uri(url); return uri.Host + ":" + uri.Port; }
        catch (UriFormatException) { return url; }
    }
}
