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
}

public sealed class AppSettings
{
    // AI
    public string Provider { get; set; } = ProviderIds.Gemini;
    [JsonIgnore] public string GeminiKey { get; set; } = "";
    [JsonIgnore] public string OpenAiKey { get; set; } = "";
    [JsonIgnore] public string ClaudeKey { get; set; } = "";
    [JsonIgnore] public string LocalKey { get; set; } = "";
    public string Quality { get; set; } = "fast"; // fast | balanced | smart
    public string LocalUrl { get; set; } = "http://127.0.0.1:11434/v1";
    public string LocalModel { get; set; } = "";

    // Stored (encrypted) forms of the keys.
    public string GeminiKeyStored { get; set; } = "";
    public string OpenAiKeyStored { get; set; } = "";
    public string ClaudeKeyStored { get; set; } = "";
    public string LocalKeyStored { get; set; } = "";

    // Activation
    public string Hotkey { get; set; } = "Alt+Shift+M";
    public bool Shake { get; set; } = true;
    public int ShakeSensitivity { get; set; } = 5; // 1..10
    public bool DisableInFullscreen { get; set; } = true;
    public bool TypeToAsk { get; set; } = true;

    // Translator and voice
    public string TranslateTo { get; set; } = "auto"; // language code or "auto" (= interface language)
    public bool SpeakTranslation { get; set; } = false;
    public double VoiceRate { get; set; } = 1.0;
    public string VoiceInputLanguage { get; set; } = "auto";

    // Selection helpers
    public bool AutoQr { get; set; } = true;
    public bool AutoOcr { get; set; } = true;

    // General
    public string UiLanguage { get; set; } = "auto"; // auto | pt | en
    public bool StartWithWindows { get; set; } = true;
    public bool FirstRunDone { get; set; }

    [JsonIgnore]
    public bool HasCloudKey => !string.IsNullOrWhiteSpace(KeyFor(Provider));

    /// <summary>True when an AI is configured for the chosen provider.</summary>
    [JsonIgnore]
    public bool HasAi => Provider == ProviderIds.Local
        ? !string.IsNullOrWhiteSpace(LocalUrl)
        : HasCloudKey;

    public string KeyFor(string provider) => provider switch
    {
        ProviderIds.Gemini => GeminiKey,
        ProviderIds.OpenAi => OpenAiKey,
        ProviderIds.Claude => ClaudeKey,
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
        LocalKeyStored = protector.Protect(LocalKey.Trim());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public void Normalize()
    {
        if (Provider is not (ProviderIds.Gemini or ProviderIds.OpenAi or ProviderIds.Claude or ProviderIds.Local))
            Provider = ProviderIds.Gemini;
        if (Quality is not ("fast" or "balanced" or "smart")) Quality = "fast";
        ShakeSensitivity = Math.Clamp(ShakeSensitivity, 1, 10);
        VoiceRate = Math.Clamp(VoiceRate, 0.5, 2.0);
        LocalUrl = (LocalUrl ?? "").Trim();
        LocalModel = (LocalModel ?? "").Trim();
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Alt+Shift+M";
    }

    private static string SafeUnprotect(ISecretProtector protector, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try { return protector.Unprotect(stored); }
        catch (Exception) { return ""; }
    }
}
