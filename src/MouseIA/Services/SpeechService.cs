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
    private static dynamic? _sapi;
    private static System.Windows.Threading.DispatcherTimer? _sapiWatch;

    public static event Action? SpeakingEnded;

    public static bool IsSpeaking { get; private set; }

    /// <summary>A voice from either of Windows' speech systems.</summary>
    public sealed record VoiceOption(string Id, string Name, string Language, bool Natural, bool Sapi)
    {
        public string Label => Name.Replace("Microsoft ", "") + (Natural ? " ✦" : "");
    }

    private static bool LooksNatural(string name) =>
        name.Contains("Natural", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Neural", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Online", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every installed voice, read fresh each time so newly installed voices show up on their own.
    /// OneCore voices (Windows.Media) plus SAPI 5 voices, where third-party and natural-voice engines register.
    /// </summary>
    public static IReadOnlyList<VoiceOption> AllVoices()
    {
        var list = new List<VoiceOption>();
        try
        {
            foreach (var v in SpeechSynthesizer.AllVoices)
                list.Add(new VoiceOption("winrt:" + v.Id, v.DisplayName, v.Language, LooksNatural(v.DisplayName), false));
        }
        catch (Exception ex) { Native.Log.Write("OneCore voices: " + ex.Message); }
        try
        {
            var voice = Sapi();
            if (voice != null)
            {
                dynamic tokens = voice.GetVoices("", "");
                int count = tokens.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic token = tokens.Item(i);
                    string name = token.GetDescription(0);
                    string language = SapiLanguage((string)token.GetAttribute("Language"));
                    // Built-in desktop voices exist in both systems; keep one of each.
                    if (list.Any(v => !v.Sapi && v.Name == name)) continue;
                    list.Add(new VoiceOption("sapi:" + (string)token.Id, name, language, LooksNatural(name), true));
                }
            }
        }
        catch (Exception ex) { Native.Log.Write("SAPI voices: " + ex.Message); }
        return list;
    }

    /// <summary>SAPI lists languages as hex LCIDs, e.g. "416" for pt-BR (several may be separated by ';').</summary>
    private static string SapiLanguage(string? value)
    {
        try
        {
            var first = (value ?? "").Split(';')[0].Trim();
            if (first.Length == 0) return "";
            return new System.Globalization.CultureInfo(Convert.ToInt32(first, 16)).Name;
        }
        catch (Exception) { return ""; }
    }

    private static dynamic? Sapi()
    {
        if (_sapi != null) return _sapi;
        var type = Type.GetTypeFromProgID("SAPI.SpVoice");
        if (type == null) return null;
        _sapi = Activator.CreateInstance(type);
        return _sapi;
    }

    private static bool SameLanguage(string a, string b) =>
        a.Split('-')[0].Equals(b.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The voice to use for a language: the one chosen in settings when it speaks that language,
    /// otherwise the best installed one (exact region, then natural voices first).
    /// </summary>
    public static VoiceOption? VoiceFor(string code, string preferredId = "auto")
    {
        var voices = AllVoices();
        if (preferredId != "auto")
        {
            var chosen = voices.FirstOrDefault(v => v.Id == preferredId);
            if (chosen != null && SameLanguage(chosen.Language, code)) return chosen;
        }
        var prefix = code.Split('-')[0];
        var preferredRegion = prefix switch { "pt" => "pt-BR", "en" => "en-US", "es" => "es-MX", "zh" => "zh-CN", _ => code };
        return voices
            .Where(v => SameLanguage(v.Language, code))
            .OrderByDescending(v => v.Natural)
            .ThenByDescending(v => v.Language.Equals(code, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.Language.Equals(preferredRegion, StringComparison.OrdinalIgnoreCase))
            .ThenBy(v => v.Sapi)
            .FirstOrDefault();
    }

    /// <summary>Speaks text; false when no voice is installed for the language.</summary>
    public static async Task<bool> SpeakAsync(string text, string languageCode, double rate, string voiceId = "auto")
    {
        var voice = VoiceFor(languageCode, voiceId);
        if (voice == null || string.IsNullOrWhiteSpace(text)) return false;
        Stop();
        if (voice.Sapi) return SpeakSapi(text, voice, rate);

        var info = SpeechSynthesizer.AllVoices.FirstOrDefault(v => "winrt:" + v.Id == voice.Id);
        if (info == null) return false;
        using var synthesizer = new SpeechSynthesizer();
        synthesizer.Voice = info;
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

    private static bool SpeakSapi(string text, VoiceOption option, double rate)
    {
        var voice = Sapi();
        if (voice == null) return false;
        dynamic tokens = voice.GetVoices("", "");
        int count = tokens.Count;
        for (int i = 0; i < count; i++)
        {
            dynamic token = tokens.Item(i);
            if ("sapi:" + (string)token.Id == option.Id)
            {
                voice.Voice = token;
                break;
            }
        }
        // SAPI rates go from -10 to 10; 1.0x is 0.
        voice.Rate = (int)Math.Clamp(Math.Round((rate - 1.0) * 10), -10, 10);
        const int SVSFlagsAsync = 1, SVSFPurgeBeforeSpeak = 2, SVSFIsNotXML = 16;
        voice.Speak(text, SVSFlagsAsync | SVSFPurgeBeforeSpeak | SVSFIsNotXML);
        IsSpeaking = true;
        _sapiWatch?.Stop();
        _sapiWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _sapiWatch.Tick += (_, _) =>
        {
            bool done;
            try { done = (int)_sapi!.Status.RunningState == 1; } // SRSEDone
            catch (Exception) { done = true; }
            if (!done) return;
            _sapiWatch?.Stop();
            if (IsSpeaking)
            {
                IsSpeaking = false;
                SpeakingEnded?.Invoke();
            }
        };
        _sapiWatch.Start();
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
        if (player != null)
        {
            try
            {
                player.Pause();
                player.Dispose();
            }
            catch (Exception) { }
        }
        if (_sapi != null)
        {
            try { _sapi.Speak("", 2 | 1); } // purge whatever is playing
            catch (Exception) { }
            _sapiWatch?.Stop();
        }
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
