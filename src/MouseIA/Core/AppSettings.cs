using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseIA.Core;

/// <summary>Encrypts secrets (API keys) before they are written to disk.</summary>
public interface ISecretProtector
{
    string Protect(string plain);
    string Unprotect(string stored);
}

/// <summary>Used where no OS protection exists (tests on other systems).</summary>
public sealed class PlainProtector : ISecretProtector
{
    public string Protect(string plain) => plain;
    public string Unprotect(string stored) => stored;
}

public static class ProviderIds
{
    public const string Gemini = "gemini";
    public const string OpenAi = "openai";
    public const string Claude = "claude";
    public const string Local = "local";
    public const string Grok = "grok";
}

public sealed class AppSettings
{
    // AI
    public string Provider { get; set; } = ProviderIds.Gemini;
    [JsonIgnore] public string GeminiKey { get; set; } = "";
    [JsonIgnore] public string OpenAiKey { get; set; } = "";
    [JsonIgnore] public string ClaudeKey { get; set; } = "";
    [JsonIgnore] public string GrokKey { get; set; } = "";
    [JsonIgnore] public string LocalKey { get; set; } = "";
    public string Quality { get; set; } = "fast"; // fast | balanced | smart
    public string LocalUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public string LocalModel { get; set; } = "";
    /// <summary>"file": MouseIA runs a model file itself; "server": an external server (Ollama, LM Studio…).</summary>
    public string LocalMode { get; set; } = "file";
    /// <summary>A .gguf or .litertlm file, usually downloaded from Hugging Face.</summary>
    public string LocalModelFile { get; set; } = "";
    /// <summary>Optional vision projector (mmproj-*.gguf) for .gguf models.</summary>
    public string LocalMmproj { get; set; } = "";
    public string LocalBackend { get; set; } = "auto"; // auto (GPU, falls back to CPU) | cpu

    // Stored (encrypted) forms of the keys.
    public string GeminiKeyStored { get; set; } = "";
    public string OpenAiKeyStored { get; set; } = "";
    public string ClaudeKeyStored { get; set; } = "";
    public string GrokKeyStored { get; set; } = "";
    public string LocalKeyStored { get; set; } = "";

    // Activation
    public string Hotkey { get; set; } = "Alt+Shift+M";
    public bool Shake { get; set; } = true;
    public int ShakeSensitivity { get; set; } = 5; // 1..10
    public bool DisableInFullscreen { get; set; } = true;
    public bool TypeToAsk { get; set; } = true;

    // Mouse actions while the pointer is open: ask | translate | translate_speak | copy_text | qr | none
    public string LeftDragAction { get; set; } = "ask";
    public string RightDragAction { get; set; } = "translate";
    public string ScrollAction { get; set; } = "none";
    /// <summary>Also read aloud the translation laid over the screen.</summary>
    public bool SpeakOverlayTranslation { get; set; } = false;

    // Monitors: all | cursor | a device name such as \\.\DISPLAY2
    public string MonitorMode { get; set; } = "all";

    // Updates
    public bool AutoUpdate { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    public string SkippedVersion { get; set; } = "";

    // Translator and voice
    public string TranslateTo { get; set; } = "auto"; // language code or "auto" (= interface language)
    public bool SpeakTranslation { get; set; } = false;
    public double VoiceRate { get; set; } = 1.0;
    /// <summary>Use (and download once) MouseIA's own natural voices.</summary>
    public bool NaturalVoices { get; set; } = true;
    /// <summary>"auto" picks the best voice for each language (natural/neural first), or a voice id.</summary>
    public string VoiceId { get; set; } = "auto";
    public string VoiceInputLanguage { get; set; } = "auto";

    // Selection helpers
    public bool AutoQr { get; set; } = true;
    public bool AutoOcr { get; set; } = true;

    // Look
    public bool GlassEffect { get; set; } = true;
    /// <summary>How solid the glass is: 0.3 (very see-through) .. 1.0 (opaque).</summary>
    public double GlassOpacity { get; set; } = 0.85;

    // General
    public string UiLanguage { get; set; } = "auto"; // auto | pt | en
    public bool StartWithWindows { get; set; } = true;
    public bool FirstRunDone { get; set; }

    [JsonIgnore]
    public bool HasCloudKey => !string.IsNullOrWhiteSpace(KeyFor(Provider));

    /// <summary>True when an AI is configured for the chosen provider.</summary>
    [JsonIgnore]
    public bool UsesModelFile => Provider == ProviderIds.Local && LocalMode == "file";

    [JsonIgnore]
    public bool HasAi => Provider == ProviderIds.Local
        ? (LocalMode == "file" ? !string.IsNullOrWhiteSpace(LocalModelFile) : !string.IsNullOrWhiteSpace(LocalUrl))
        : HasCloudKey;

    public string KeyFor(string provider) => provider switch
    {
        ProviderIds.Gemini => GeminiKey,
        ProviderIds.OpenAi => OpenAiKey,
        ProviderIds.Claude => ClaudeKey,
        ProviderIds.Grok => GrokKey,
        ProviderIds.Local => LocalKey,
        _ => "",
    };

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        return copy;
    }

    // ---- persistence ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseIA");

    public static AppSettings Load(string path, ISecretProtector protector)
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (settings != null)
                {
                    settings.GeminiKey = SafeUnprotect(protector, settings.GeminiKeyStored);
                    settings.OpenAiKey = SafeUnprotect(protector, settings.OpenAiKeyStored);
                    settings.ClaudeKey = SafeUnprotect(protector, settings.ClaudeKeyStored);
                    settings.GrokKey = SafeUnprotect(protector, settings.GrokKeyStored);
                    settings.LocalKey = SafeUnprotect(protector, settings.LocalKeyStored);
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception)
        {
            // A damaged file falls back to the defaults.
        }
        return new AppSettings();
    }

    public void Save(string path, ISecretProtector protector)
    {
        Normalize();
        GeminiKeyStored = protector.Protect(GeminiKey.Trim());
        OpenAiKeyStored = protector.Protect(OpenAiKey.Trim());
        ClaudeKeyStored = protector.Protect(ClaudeKey.Trim());
        GrokKeyStored = protector.Protect(GrokKey.Trim());
        LocalKeyStored = protector.Protect(LocalKey.Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public void Normalize()
    {
        if (Provider is not (ProviderIds.Gemini or ProviderIds.OpenAi or ProviderIds.Claude or ProviderIds.Grok or ProviderIds.Local))
            Provider = ProviderIds.Gemini;
        if (Quality is not ("fast" or "balanced" or "smart")) Quality = "fast";
        ShakeSensitivity = Math.Clamp(ShakeSensitivity, 1, 10);
        GlassOpacity = Math.Clamp(GlassOpacity, 0.3, 1.0);
        if (LocalMode is not ("file" or "server")) LocalMode = "file";
        if (LocalBackend is not ("auto" or "cpu")) LocalBackend = "auto";
        if (!IsAction(LeftDragAction)) LeftDragAction = "ask";
        if (!IsAction(RightDragAction)) RightDragAction = "translate";
        if (!IsAction(ScrollAction)) ScrollAction = "none";
        if (string.IsNullOrWhiteSpace(MonitorMode)) MonitorMode = "all";
        if (string.IsNullOrWhiteSpace(VoiceId)) VoiceId = "auto";
        LocalModelFile = (LocalModelFile ?? "").Trim();
        LocalMmproj = (LocalMmproj ?? "").Trim();
        VoiceRate = Math.Clamp(VoiceRate, 0.5, 2.0);
        LocalUrl = (LocalUrl ?? "").Trim();
        LocalModel = (LocalModel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Alt+Shift+M";
    }

    public static readonly string[] Actions = { "ask", "translate", "translate_speak", "copy_text", "qr", "lens", "none" };

    public static bool IsAction(string? value) => Array.IndexOf(Actions, value) >= 0;

    private static string SafeUnprotect(ISecretProtector protector, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return protector.Unprotect(stored); }
        catch (Exception) { return ""; }
    }
}
