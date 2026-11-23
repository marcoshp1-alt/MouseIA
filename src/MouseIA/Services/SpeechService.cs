using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;

namespace MouseIA.Services;

/// <summary>Text-to-speech and dictation with the voices built into Windows.</summary>
internal static class SpeechService
{
    private static MediaPlayer? _player;

    public static event Action? SpeakingEnded;

    public static bool IsSpeaking { get; private set; }

    public static IReadOnlyList<VoiceInformation> Voices
    {
        get
        {
            try { return SpeechSynthesizer.AllVoices.ToList(); }
            catch (Exception) { return Array.Empty<VoiceInformation>(); }
        }
    }

    /// <summary>The best installed voice for a language code like "pt-BR" or "ja".</summary>
    public static VoiceInformation? VoiceFor(string code)
    {
        var voices = Voices;
        var exact = voices.FirstOrDefault(v => string.Equals(v.Language, code, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var prefix = code.Split('-')[0];
        // Prefer the Brazilian voice for plain "pt", the US voice for "en".
        var preferred = prefix switch { "pt" => "pt-BR", "en" => "en-US", "es" => "es-MX", "zh" => "zh-CN", _ => "" };
        return voices.FirstOrDefault(v => string.Equals(v.Language, preferred, StringComparison.OrdinalIgnoreCase))
               ?? voices.FirstOrDefault(v => v.Language.Split('-')[0].Equals(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Speaks text; false when no voice is installed for the language.</summary>
    public static async Task<bool> SpeakAsync(string text, string languageCode, double rate)
    {
        var voice = VoiceFor(languageCode);
        if (voice == null || string.IsNullOrWhiteSpace(text)) return false;
        Stop();
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Voice = voice;
        synthesizer.Options.SpeakingRate = Math.Clamp(rate, 0.5, 2.0);
        var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
        var player = new MediaPlayer();
        player.MediaEnded += (_, _) => Finish(player);
        player.MediaFailed += (_, _) => Finish(player);
        player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player = player;
        IsSpeaking = true;
        player.Play();
        return true;
    }

    private static void Finish(MediaPlayer player)
    {
        if (!ReferenceEquals(_player, player)) return;
        IsSpeaking = false;
        SpeakingEnded?.Invoke();
    }

    public static void Stop()
    {
        var player = _player;
        _player = null;
        if (player == null) return;
        try
        {
            player.Pause();
            player.Dispose();
        }
        catch (Exception) { }
        if (IsSpeaking)
        {
            IsSpeaking = false;
            SpeakingEnded?.Invoke();
        }
    }

    // ---- dictation ----

    public enum ListenError { None, NotSupported, PrivacyDeclined, NoMicrophone, Other }

    public sealed record ListenResult(string Text, ListenError Error, string Detail);

    /// <summary>Listens once and returns what was said.</summary>
    public static async Task<ListenResult> ListenAsync(string languageCode, CancellationToken ct)
    {
        SpeechRecognizer recognizer;
        try
        {
            var supported = SpeechRecognizer.SupportedTopicLanguages;
            var match = supported.FirstOrDefault(l => l.LanguageTag.Equals(languageCode, StringComparison.OrdinalIgnoreCase))
                        ?? supported.FirstOrDefault(l => l.LanguageTag.Split('-')[0]
                            .Equals(languageCode.Split('-')[0], StringComparison.OrdinalIgnoreCase));
            recognizer = match != null ? new SpeechRecognizer(match) : new SpeechRecognizer();
        }
        catch (Exception ex)
        {
            return new ListenResult("", ListenError.NotSupported, ex.Message);
        }

        using (recognizer)
        {
            try
            {
                recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(6);
                recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
                var compiled = await recognizer.CompileConstraintsAsync();
                if (compiled.Status != SpeechRecognitionResultStatus.Success)
                    return new ListenResult("", ListenError.NotSupported, compiled.Status.ToString());

                var operation = recognizer.RecognizeAsync();
                using (ct.Register(() => { try { operation.Cancel(); } catch (Exception) { } }))
                {
                    var result = await operation;
                    if (result.Status == SpeechRecognitionResultStatus.Success)
                        return new ListenResult(result.Text, ListenError.None, "");
                    return new ListenResult("", ListenError.Other, result.Status.ToString());
                }
            }
            catch (OperationCanceledException)
            {
                return new ListenResult("", ListenError.None, "canceled");
            }
            catch (Exception ex) when ((uint)ex.HResult == 0x80045509)
            {
                // "Online speech recognition" is off in Windows privacy settings.
                return new ListenResult("", ListenError.PrivacyDeclined, ex.Message);
            }
            catch (Exception ex) when ((uint)ex.HResult is 0x80070005 or 0x8007048F or 0xC00D36FA)
            {
                return new ListenResult("", ListenError.NoMicrophone, ex.Message);
            }
            catch (Exception ex)
            {
                return new ListenResult("", ListenError.Other, ex.Message);
            }
        }
    }

    public static string SystemLanguageTag()
    {
        try { return SpeechRecognizer.SystemSpeechLanguage.LanguageTag; }
        catch (Exception) { return ApplicationLanguages.Languages.FirstOrDefault() ?? "en-US"; }
    }
}
