using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MouseIA.Core;

public sealed record ChatTurn(bool FromUser, string Text);

public sealed class AiRequest
{
    public string Provider { get; init; } = ProviderIds.Gemini;
    public string ApiKey { get; init; } = "";
    /// <summary>Models to try in order; later ones are faster fallbacks.</summary>
    public IReadOnlyList<string> Models { get; init; } = Array.Empty<string>();
    /// <summary>OpenAI-compatible base URL (local provider only), e.g. http://127.0.0.1:11434/v1.</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>Stable id for gateways that route a conversation by session (OpenCode).</summary>
    public string SessionId { get; init; } = "";
    public string SystemPrompt { get; init; } = "";
    /// <summary>Conversation, ending with the newest user turn.</summary>
    public IReadOnlyList<ChatTurn> History { get; init; } = Array.Empty<ChatTurn>();
    /// <summary>JPEG images (base64), attached to the first user turn.</summary>
    public IReadOnlyList<string> ImagesJpegBase64 { get; init; } = Array.Empty<string>();
    public int MaxOutputTokens { get; init; } = 4096;

    /// <summary>The same request without pictures, for a model that can't read them.</summary>
    public AiRequest WithoutImages() => new()
    {
        Provider = Provider,
        ApiKey = ApiKey,
        Models = Models,
        BaseUrl = BaseUrl,
        SessionId = SessionId,
        SystemPrompt = SystemPrompt,
        History = History,
        ImagesJpegBase64 = Array.Empty<string>(),
        MaxOutputTokens = MaxOutputTokens,
    };
}

public enum AiErrorKind { Network, Timeout, InvalidKey, Forbidden, ModelMissing, RateLimit, NoCredit, Server, Other }

public sealed class AiException : Exception
{
    public AiErrorKind Kind { get; }
    public string Detail { get; }

    public AiException(AiErrorKind kind, string detail, Exception? inner = null)
        : base($"{kind}: {detail}", inner)
    {
        Kind = kind;
        Detail = detail;
    }
}

public sealed class AiResult
{
    public string Text { get; init; } = "";
    public string Model { get; init; } = "";
    public bool Truncated { get; init; }
    public bool UsedFallback { get; init; }
}

public static class ModelCatalog
{
    private static readonly Dictionary<string, string[]> Tiers = new()
    {
        [ProviderIds.Gemini] = new[] { "gemini-flash-lite-latest", "gemini-3.6-flash", "gemini-pro-latest" },
        [ProviderIds.OpenAi] = new[] { "gpt-6-luna", "gpt-6.1-terra", "gpt-6.1-sol" },
        [ProviderIds.Claude] = new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5" },
        // xAI lists one current chat model; the previous flagship is the fallback.
        [ProviderIds.Grok] = new[] { "grok-4.5", "grok-4.7", "grok-4.7" },
    };

    public static int TierOf(string quality) => quality switch { "smart" => 2, "balanced" => 1, _ => 0 };

    /// <summary>The chosen model followed by faster fallbacks.</summary>
    public static IReadOnlyList<string> ModelsFor(AppSettings settings)
    {
        // Zen, Go and compatible endpoints list their own models; there is nothing to fall back to.
        if (ProviderIds.IsCustomEndpoint(settings.Provider))
        {
            var chosen = settings.ModelFor(settings.Provider);
            return string.IsNullOrWhiteSpace(chosen) ? Array.Empty<string>() : new[] { chosen };
        }
        if (settings.Provider == ProviderIds.Local)
            return new[] { string.IsNullOrWhiteSpace(settings.LocalModel) ? "local-model" : settings.LocalModel };
        var tiers = Tiers[settings.Provider];
        var list = new List<string>();
        for (int tier = TierOf(settings.Quality); tier >= 0; tier--)
            if (!list.Contains(tiers[tier])) list.Add(tiers[tier]);
        if (settings.Provider == ProviderIds.Grok && !list.Contains("grok-4.5")) list.Add("grok-4.5");
        return list;
    }

    public static string DisplayName(string provider) => provider switch
    {
        ProviderIds.OpenAi => "ChatGPT",
        ProviderIds.Claude => "Claude",
        ProviderIds.Grok => "Grok",
        ProviderIds.Local => "IA local",
        ProviderIds.OpenCodeZen => "OpenCode Zen",
        ProviderIds.OpenCodeGo => "OpenCode Go",
        ProviderIds.Compatible => "OpenAI compatível",
        _ => "Gemini",
    };
}

public static class AiClient
{
    public const string OpenCodeZenBase = "https://opencode.ai/inference";
    public const string OpenCodeGoBase = "https://opencode.ai/inference/go";
    /// <summary>Sent with every OpenCode request; Go refuses calls without a session.</summary>
    public static readonly string OpenCodeSession = "ses_" + Guid.NewGuid().ToString("n");

    private static readonly HttpClient Cloud = CreateClient(useProxy: true);
    private static readonly HttpClient Local = CreateClient(useProxy: false);

    private static HttpClient CreateClient(bool useProxy)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = useProxy,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
        };
        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MouseIA/1.0");
        return client;
    }

    /// <summary>Streams an answer. onText receives each new piece of visible text.</summary>
    public static async Task<AiResult> StreamAsync(AiRequest request, Action<string> onText, CancellationToken ct)
    {
        if (request.Models.Count == 0) throw new ArgumentException("No model.");
        AiException? last = null;
        for (int i = 0; i < request.Models.Count; i++)
        {
            var model = request.Models[i];
            bool emitted = false;
            try
            {
                var result = await SendOnceAsync(request, model, text => { emitted = true; onText(text); }, ct)
                    .ConfigureAwait(false);
                return new AiResult { Text = result.text, Model = model, Truncated = result.truncated, UsedFallback = i > 0 };
            }
            catch (AiException ex) when (!emitted && i + 1 < request.Models.Count && ShouldTryNextModel(ex.Kind))
            {
                last = ex;
            }
        }
        throw last ?? new AiException(AiErrorKind.Other, "No answer.");
    }

    private static bool ShouldTryNextModel(AiErrorKind kind) =>
        kind is AiErrorKind.ModelMissing or AiErrorKind.RateLimit or AiErrorKind.Server or AiErrorKind.Timeout;

    private static readonly string[] ImageWords = { "image", "vision", "modalit", "multimodal" };

    private static async Task<(string text, bool truncated)> SendOnceAsync(
        AiRequest request, string model, Action<string> onText, CancellationToken ct)
    {
        bool emitted = false;
        try
        {
            return await SendAsync(request, model, text => { emitted = true; onText(text); }, ct).ConfigureAwait(false);
        }
        catch (AiException ex) when (!emitted && request.ImagesJpegBase64.Count > 0 &&
                                     Array.IndexOf(ProviderIds.ChatCompletions, request.Provider) >= 0 &&
                                     LooksLikeImageRefusal(ex))
        {
            // A text-only model rejects the picture; the same question without it still answers.
            return await SendAsync(request.WithoutImages(), model, onText, ct).ConfigureAwait(false);
        }
    }

    private static bool LooksLikeImageRefusal(AiException ex) =>
        (ex.Kind is AiErrorKind.Other or AiErrorKind.ModelMissing or AiErrorKind.InvalidKey or AiErrorKind.Forbidden) &&
        ImageWords.Any(w => ex.Detail.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static async Task<(string text, bool truncated)> SendAsync(
        AiRequest request, string model, Action<string> onText, CancellationToken ct)
    {
        using var message = BuildHttpRequest(request, model);
        // A server on this PC or in the local network must not go through the system proxy,
        // and deserves longer timeouts, like the local models.
        bool local = request.Provider == ProviderIds.Local || (message.RequestUri != null && IsPrivateHost(message.RequestUri));
        var client = local ? Local : Cloud;
        // Local models may need minutes to load into memory.
        var firstByte = local ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(60);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(firstByte);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException(AiErrorKind.Timeout, "No response.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiException(AiErrorKind.Network, ex.Message, ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, ct).ConfigureAwait(false);
                throw Classify(request.Provider, (int)response.StatusCode, body);
            }

            var text = new StringBuilder();
            string finish = "";
            string streamError = "";
            var think = new ThinkFilter();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            // Each read gets its own idle timeout so a stalled stream ends.
            await foreach (var data in SseReader.ReadEventsAsync(stream, TimeSpan.FromSeconds(local ? 300 : 90), ct)
                               .ConfigureAwait(false))
            {
                if (data == "[DONE]") break;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var piece = ReadEvent(request.Provider, doc.RootElement, ref finish, ref streamError);
                    if (request.Provider == ProviderIds.Local) piece = think.Feed(piece);
                    if (piece.Length > 0)
                    {
                        text.Append(piece);
                        onText(piece);
                    }
                }
                if (streamError.Length > 0) break;
            }
            if (streamError.Length > 0 && text.Length == 0)
                throw new AiException(AiErrorKind.Server, streamError);
            if (text.Length == 0 && finish.Length == 0)
                throw new AiException(AiErrorKind.Other, "Empty answer.");
            return (text.ToString(), finish == "MAX_TOKENS");
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return ""; }
    }

    // ---- requests ----

    internal static HttpRequestMessage BuildHttpRequest(AiRequest r, string model)
    {
        string url;
        byte[] body;
        var headers = new List<(string, string)>();
        switch (r.Provider)
        {
            case ProviderIds.OpenAi:
                url = "https://api.openai.com/v1/responses";
                body = BuildOpenAi(r, model);
                headers.Add(("Authorization", "Bearer " + r.ApiKey));
                break;
            case ProviderIds.Claude:
                url = "https://api.anthropic.com/v1/messages";
                body = BuildAnthropic(r, model);
                headers.Add(("x-api-key", r.ApiKey));
                headers.Add(("anthropic-version", "2023-06-01"));
                break;
            case ProviderIds.Grok:
                url = "https://api.x.ai/v1/chat/completions";
                body = BuildChatCompletions(r, model, maxTokensName: "max_completion_tokens");
                headers.Add(("Authorization", "Bearer " + r.ApiKey));
                break;
            case ProviderIds.Local:
                url = LocalChatUrl(r.BaseUrl);
                body = BuildChatCompletions(r, model);
                if (!string.IsNullOrWhiteSpace(r.ApiKey)) headers.Add(("Authorization", "Bearer " + r.ApiKey));
                break;
            case ProviderIds.OpenCodeZen:
                url = OpenCodeZenBase + "/openai/v1/chat/completions";
                body = BuildChatCompletions(r, model);
                headers.Add(("Authorization", "Bearer " + r.ApiKey));
                headers.Add(("x-opencode-session", SessionFor(r)));
                break;
            case ProviderIds.OpenCodeGo:
                url = OpenCodeGoBase + "/openai/v1/chat/completions";
                body = BuildChatCompletions(r, model);
                headers.Add(("Authorization", "Bearer " + r.ApiKey));
                // The Go plan refuses anonymous calls: every request must name a session.
                headers.Add(("x-opencode-session", SessionFor(r)));
                break;
            case ProviderIds.Compatible:
                url = LocalChatUrl(r.BaseUrl);
                body = BuildChatCompletions(r, model);
                if (!string.IsNullOrWhiteSpace(r.ApiKey)) headers.Add(("Authorization", "Bearer " + r.ApiKey));
                break;
            default:
                url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:streamGenerateContent?alt=sse";
                body = BuildGemini(r);
                headers.Add(("x-goog-api-key", r.ApiKey));
                break;
        }
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(body),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        foreach (var (name, value) in headers) message.Headers.TryAddWithoutValidation(name, value);
        return message;
    }

    /// <summary>Turns "http://host:port", ".../v1" or ".../v1/chat/completions" into the chat URL.</summary>
    public static string LocalChatUrl(string baseUrl)
    {
        var url = (baseUrl ?? "").Trim();
        if (!url.Contains("://")) url = "http://" + url;
        url = url.TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
        var uri = new Uri(url);
        if (uri.AbsolutePath is "" or "/") url += "/v1";
        return url + "/chat/completions";
    }

    public static string LocalModelsUrl(string baseUrl)
    {
        var chat = LocalChatUrl(baseUrl);
        return chat[..^"/chat/completions".Length] + "/models";
    }

    /// <summary>Address a provider listens on; the user's own for a compatible endpoint.</summary>
    public static string BaseUrlFor(string provider, string customBaseUrl) => provider switch
    {
        ProviderIds.OpenCodeZen => OpenCodeZenBase,
        ProviderIds.OpenCodeGo => OpenCodeGoBase,
        ProviderIds.Compatible => customBaseUrl,
        _ => "",
    };

    public static string ChatUrlFor(string provider, string baseUrl) =>
        ProviderIds.IsCustomEndpoint(provider) && provider != ProviderIds.Compatible
            ? BaseUrlFor(provider, "") + "/openai/v1/chat/completions"
            : LocalChatUrl(baseUrl);

    public static string ModelsUrlFor(string provider, string baseUrl) =>
        ProviderIds.IsCustomEndpoint(provider) && provider != ProviderIds.Compatible
            ? BaseUrlFor(provider, "") + "/v1/models"
            : LocalModelsUrl(baseUrl);

    /// <summary>Model ids an OpenAI-compatible endpoint offers, or null when it doesn't answer.</summary>
    public static async Task<IReadOnlyList<string>?> ListModelsAsync(string url, string apiKey, string sessionId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        if (!string.IsNullOrWhiteSpace(sessionId)) request.Headers.TryAddWithoutValidation("x-opencode-session", sessionId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await (IsPrivateHost(url) ? Local : Cloud).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return LocalAiDiscovery.ParseModels(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string SessionFor(AiRequest r) => string.IsNullOrWhiteSpace(r.SessionId) ? OpenCodeSession : r.SessionId;

    /// <summary>A server on this PC or in the local network: no proxy, and longer timeouts.</summary>
    public static bool IsPrivateHost(string url) =>
        Uri.TryCreate(url.Contains("://") ? url : "http://" + url, UriKind.Absolute, out var uri) && IsPrivateHost(uri);

    private static bool IsPrivateHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
                return bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
            return ip.IsIPv6LinkLocal;
        }
        return host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static string TurnText(ChatTurn turn) => string.IsNullOrWhiteSpace(turn.Text) ? "." : turn.Text;

    private static byte[] BuildGemini(AiRequest r) => Json(w =>
    {
        w.WriteStartObject("systemInstruction");
        w.WriteStartArray("parts");
        w.WriteStartObject(); w.WriteString("text", r.SystemPrompt); w.WriteEndObject();
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteStartArray("contents");
        for (int i = 0; i < r.History.Count; i++)
        {
            var turn = r.History[i];
            w.WriteStartObject();
            w.WriteString("role", turn.FromUser ? "user" : "model");
            w.WriteStartArray("parts");
            if (i == 0)
            {
                foreach (var image in r.ImagesJpegBase64)
                {
                    w.WriteStartObject();
                    w.WriteStartObject("inlineData");
                    w.WriteString("mimeType", "image/jpeg");
                    w.WriteString("data", image);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
            }
            w.WriteStartObject(); w.WriteString("text", TurnText(turn)); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartObject("generationConfig");
        w.WriteNumber("maxOutputTokens", r.MaxOutputTokens);
        w.WriteEndObject();
    });

    private static byte[] BuildOpenAi(AiRequest r, string model) => Json(w =>
    {
        w.WriteString("model", model);
        w.WriteBoolean("stream", true);
        w.WriteBoolean("store", false);
        w.WriteString("instructions", r.SystemPrompt);
        w.WriteNumber("max_output_tokens", r.MaxOutputTokens);
        w.WriteStartArray("input");
        for (int i = 0; i < r.History.Count; i++)
        {
            var turn = r.History[i];
            w.WriteStartObject();
            if (turn.FromUser)
            {
                w.WriteString("role", "user");
                w.WriteStartArray("content");
                if (i == 0)
                {
                    foreach (var image in r.ImagesJpegBase64)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "input_image");
                        w.WriteString("image_url", "data:image/jpeg;base64," + image);
                        w.WriteEndObject();
                    }
                }
                w.WriteStartObject();
                w.WriteString("type", "input_text");
                w.WriteString("text", TurnText(turn));
                w.WriteEndObject();
                w.WriteEndArray();
            }
            else
            {
                w.WriteString("role", "assistant");
                w.WriteString("content", TurnText(turn));
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    private static byte[] BuildAnthropic(AiRequest r, string model) => Json(w =>
    {
        w.WriteString("model", model);
        w.WriteNumber("max_tokens", r.MaxOutputTokens);
        w.WriteBoolean("stream", true);
        w.WriteString("system", r.SystemPrompt);
        w.WriteStartArray("messages");
        for (int i = 0; i < r.History.Count; i++)
        {
            var turn = r.History[i];
            w.WriteStartObject();
            w.WriteString("role", turn.FromUser ? "user" : "assistant");
            w.WriteStartArray("content");
            if (i == 0 && turn.FromUser)
            {
                foreach (var image in r.ImagesJpegBase64)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "image");
                    w.WriteStartObject("source");
                    w.WriteString("type", "base64");
                    w.WriteString("media_type", "image/jpeg");
                    w.WriteString("data", image);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
            }
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", TurnText(turn));
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    /// <summary>OpenAI-compatible Chat Completions (Ollama, LM Studio, llama.cpp, LiteRT-LM, Jan, vLLM).</summary>
    private static byte[] BuildChatCompletions(AiRequest r, string model, string maxTokensName = "max_tokens") => Json(w =>
    {
        w.WriteString("model", model);
        w.WriteBoolean("stream", true);
        w.WriteNumber(maxTokensName, r.MaxOutputTokens);
        w.WriteStartArray("messages");
        w.WriteStartObject();
        w.WriteString("role", "system");
        w.WriteString("content", r.SystemPrompt);
        w.WriteEndObject();
        for (int i = 0; i < r.History.Count; i++)
        {
            var turn = r.History[i];
            w.WriteStartObject();
            w.WriteString("role", turn.FromUser ? "user" : "assistant");
            // Text-only models reject image parts, so plain text stays a string.
            if (i == 0 && turn.FromUser && r.ImagesJpegBase64.Count > 0)
            {
                w.WriteStartArray("content");
                foreach (var image in r.ImagesJpegBase64)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "image_url");
                    w.WriteStartObject("image_url");
                    w.WriteString("url", "data:image/jpeg;base64," + image);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteStartObject();
                w.WriteString("type", "text");
                w.WriteString("text", TurnText(turn));
                w.WriteEndObject();
                w.WriteEndArray();
            }
            else
            {
                w.WriteString("content", TurnText(turn));
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
    });

    // ---- responses ----

    private static string Str(JsonElement e, params string[] path)
    {
        foreach (var key in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) return "";
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
    }

    internal static string ReadEvent(string provider, JsonElement root, ref string finish, ref string error)
    {
        if (root.ValueKind != JsonValueKind.Object) return "";
        switch (provider)
        {
            case ProviderIds.OpenAi:
            {
                var type = Str(root, "type");
                if (type is "response.output_text.delta" or "response.refusal.delta") return Str(root, "delta");
                if (type == "response.completed") finish = "STOP";
                else if (type == "response.incomplete")
                    finish = Str(root, "response", "incomplete_details", "reason") == "max_output_tokens" ? "MAX_TOKENS" : "INCOMPLETE";
                else if (type == "response.failed")
                    error = FirstNonEmpty(Str(root, "response", "error", "message"), "The answer failed.");
                else if (type == "error")
                    error = FirstNonEmpty(Str(root, "message"), Str(root, "error", "message"), "Stream error.");
                return "";
            }
            case ProviderIds.Claude:
            {
                var type = Str(root, "type");
                if (type == "content_block_delta" && Str(root, "delta", "type") == "text_delta") return Str(root, "delta", "text");
                if (type == "message_delta")
                {
                    var reason = Str(root, "delta", "stop_reason");
                    if (reason.Length > 0) finish = reason == "max_tokens" ? "MAX_TOKENS" : "STOP";
                }
                else if (type == "error")
                    error = FirstNonEmpty(Str(root, "error", "message"), "Stream error.");
                return "";
            }
            case ProviderIds.Local:
            case ProviderIds.Grok:
            case ProviderIds.OpenCodeZen:
            case ProviderIds.OpenCodeGo:
            case ProviderIds.Compatible:
            {
                if (root.TryGetProperty("error", out var err))
                {
                    error = err.ValueKind == JsonValueKind.String ? err.GetString() ?? "" : Str(err, "message");
                    if (error.Length == 0) error = "Stream error.";
                    return "";
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0) return "";
                var choice = choices[0];
                var reason = Str(choice, "finish_reason");
                if (reason.Length > 0) finish = reason == "length" ? "MAX_TOKENS" : "STOP";
                return Str(choice, "delta", "content");
            }
            default:
            {
                if (root.TryGetProperty("error", out var err))
                {
                    error = FirstNonEmpty(Str(err, "message"), "Stream error.");
                    return "";
                }
                if (!root.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array ||
                    candidates.GetArrayLength() == 0) return "";
                var candidate = candidates[0];
                var reason = Str(candidate, "finishReason");
                if (reason.Length > 0) finish = reason == "MAX_TOKENS" ? "MAX_TOKENS" : "STOP";
                var sb = new StringBuilder();
                if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts) &&
                    parts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True) continue;
                        sb.Append(Str(part, "text"));
                    }
                }
                return sb.ToString();
            }
        }
    }

    private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? "";

    internal static AiException Classify(string provider, int status, string body)
    {
        string message = "";
        string code = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err))
            {
                message = err.ValueKind == JsonValueKind.String ? err.GetString() ?? "" : Str(err, "message");
                code = FirstNonEmpty(Str(err, "code"), Str(err, "type"), Str(err, "status"));
            }
        }
        catch (JsonException) { }
        if (message.Length == 0) message = body.Length > 300 ? body[..300] : body;

        bool billing = Contains(code, "quota") || Contains(code, "billing") || Contains(code, "credit") ||
                       Contains(message, "credit balance") || Contains(message, "current quota");
        var kind = status switch
        {
            401 => AiErrorKind.InvalidKey,
            400 when provider == ProviderIds.Gemini && Contains(message, "API key") => AiErrorKind.InvalidKey,
            402 => AiErrorKind.NoCredit,
            400 or 429 when billing && provider != ProviderIds.Gemini => AiErrorKind.NoCredit,
            403 => AiErrorKind.Forbidden,
            404 => AiErrorKind.ModelMissing,
            400 when Contains(message, "model") && (Contains(message, "not found") || Contains(message, "does not exist")) => AiErrorKind.ModelMissing,
            429 => AiErrorKind.RateLimit,
            >= 500 => AiErrorKind.Server,
            _ => AiErrorKind.Other,
        };
        return new AiException(kind, message);
    }

    private static bool Contains(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads "data:" payloads from a server-sent event stream.</summary>
public static class SseReader
{
    public static async IAsyncEnumerable<string> ReadEventsAsync(
        Stream stream, TimeSpan idleTimeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(idleTimeout);
                try
                {
                    line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new AiException(AiErrorKind.Timeout, "The stream stalled.");
                }
                catch (IOException ex)
                {
                    throw new AiException(AiErrorKind.Network, ex.Message, ex);
                }
            }
            if (line == null)
            {
                if (data.Length > 0) yield return data.ToString();
                yield break;
            }
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]);
            }
            else if (line[0] == '{' && data.Length == 0)
            {
                // Some servers send bare JSON lines (one object per line).
                yield return line;
            }
        }
    }
}

/// <summary>Hides reasoning that local models wrap in &lt;think&gt; tags, across chunk boundaries.</summary>
public sealed class ThinkFilter
{
    private readonly StringBuilder _pending = new();
    private bool _inside;

    public string Feed(string text)
    {
        if (text.Length == 0) return text;
        _pending.Append(text);
        var output = new StringBuilder();
        while (true)
        {
            var tag = _inside ? "</think>" : "<think>";
            var buffer = _pending.ToString();
            var at = buffer.IndexOf(tag, StringComparison.Ordinal);
            if (at >= 0)
            {
                if (!_inside) output.Append(buffer, 0, at);
                _pending.Remove(0, at + tag.Length);
                _inside = !_inside;
                continue;
            }
            int keep = 0;
            for (int n = Math.Min(buffer.Length, tag.Length - 1); n > 0; n--)
            {
                if (string.CompareOrdinal(buffer, buffer.Length - n, tag, 0, n) == 0) { keep = n; break; }
            }
            if (!_inside) output.Append(buffer, 0, buffer.Length - keep);
            _pending.Remove(0, buffer.Length - keep);
            return output.ToString();
        }
    }
}
