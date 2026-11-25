using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MouseIA.Core;

namespace MouseIA.Services;

/// <summary>Builds a request for whichever AI is configured, starting the local model file if needed.</summary>
internal static class AiSetup
{
    public sealed record Prepared(AiRequest Request, bool DroppedImages);

    public static async Task<Prepared> PrepareAsync(AppSettings settings, string system, IReadOnlyList<ChatTurn> turns,
        IReadOnlyList<string> imagesJpeg, int maxTokens, IProgress<string>? progress, CancellationToken ct)
    {
        var provider = settings.Provider;
        // OpenCode Zen/Go have their own fixed address; a compatible endpoint uses the one typed in.
        string baseUrl = settings.BaseUrlFor(provider) is { Length: > 0 } own ? own : settings.LocalUrl;
        IReadOnlyList<string> models = ModelCatalog.ModelsFor(settings);
        bool dropped = false;
        if (settings.UsesModelFile)
        {
            var (url, model) = await LocalEngine.EnsureRunningAsync(settings, progress, ct).ConfigureAwait(false);
            baseUrl = url;
            models = new[] { model };
            if (imagesJpeg.Count > 0 && !LocalEngine.HasVision(settings))
            {
                // A text-only model would reject the picture; the recognized text still goes along.
                imagesJpeg = Array.Empty<string>();
                dropped = true;
            }
        }
        var request = new AiRequest
        {
            Provider = provider,
            ApiKey = provider == ProviderIds.Local && settings.UsesModelFile ? "" : settings.KeyFor(provider),
            BaseUrl = baseUrl,
            SessionId = settings.OpenCodeSessionId,
            Models = models,
            SystemPrompt = system,
            History = turns,
            ImagesJpegBase64 = imagesJpeg,
            MaxOutputTokens = maxTokens,
        };
        return new Prepared(request, dropped);
    }
}
