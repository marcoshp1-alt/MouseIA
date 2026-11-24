using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MouseIA.Core;
using MouseIA.Native;

namespace MouseIA.Services;

/// <summary>
/// MouseIA's own natural voices (Kokoro, a neural voice model), female and male, running offline
/// on the computer through the sherpa-onnx engine. Downloaded once by MouseIA itself; nothing to set up.
/// </summary>
internal static class NeuralVoices
{
    public const string FemaleId = "kokoro:female";
    public const string MaleId = "kokoro:male";

    private const string EngineUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/v1.13.8/sherpa-onnx-v1.13.8-win-x64-shared-MT-Release.tar.bz2";
    private const string ModelUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_0.tar.bz2";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly object InstallGate = new();
    private static Task<bool>? _install;

    public static string Folder => Path.Combine(AppSettings.DefaultFolder, "voices");
    private static string ReadyFile => Path.Combine(Folder, "ready-1.txt");

    /// <summary>Download progress 0..1, or -1 when not downloading.</summary>
    public static double Progress { get; private set; } = -1;
    public static event Action? StatusChanged;

    public static bool Installed => File.Exists(ReadyFile);
    public static bool Installing => _install is { IsCompleted: false };

    private sealed record Speaker(string Lang, int Female, int Male);

    /// <summary>Languages the voices speak (espeak-ng voice name and speaker numbers in the model).</summary>
    private static Speaker? SpeakerFor(string code)
    {
        var lower = code.ToLowerInvariant();
        return lower.Split('-')[0] switch
        {
            "pt" => new Speaker("pt-br", 42, 43),
            "en" => lower.EndsWith("gb") ? new Speaker("en-gb", 21, 26) : new Speaker("en-us", 3, 16),
            "es" => new Speaker("es", 28, 29),
            "fr" => new Speaker("fr-fr", 30, 30),
            "it" => new Speaker("it", 35, 36),
            "hi" => new Speaker("hi", 31, 33),
            _ => null,
        };
    }

    public static bool Speaks(string code) => SpeakerFor(code) != null;

    // ---- install ----

    /// <summary>Downloads the engine and the voices once (about 160 MB). Safe to call many times.</summary>
    public static Task<bool> EnsureInstalledAsync()
    {
        if (Installed) return Task.FromResult(true);
        lock (InstallGate)
        {
            if (_install == null || _install.IsCompleted) _install = Task.Run(InstallAsync);
            return _install;
        }
    }

    private static async Task<bool> InstallAsync()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Progress = 0;
            StatusChanged?.Invoke();
            var engineArchive = Path.Combine(Folder, "engine.tar.bz2");
            var modelArchive = Path.Combine(Folder, "voices.tar.bz2");
            await DownloadAsync(EngineUrl, engineArchive, 0, 0.16).ConfigureAwait(false);
            await DownloadAsync(ModelUrl, modelArchive, 0.16, 0.97).ConfigureAwait(false);
            await ExtractAsync(engineArchive).ConfigureAwait(false);
            await ExtractAsync(modelArchive).ConfigureAwait(false);
            File.Delete(engineArchive);
            File.Delete(modelArchive);
            if (Locate() == null) throw new IOException("voice files missing after extraction");
            File.WriteAllText(ReadyFile, "Kokoro multi-lang v1.0 int8 + sherpa-onnx v1.13.8");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Neural voices install: " + ex.Message);
            return false;
        }
        finally
        {
            Progress = -1;
            StatusChanged?.Invoke();
        }
    }

    private static async Task DownloadAsync(string url, string path, double from, double to)
    {
        if (File.Exists(path)) File.Delete(path);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = File.Create(path);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        double reported = -1;
        while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            done += read;
            if (total <= 0) continue;
            double p = from + (to - from) * done / total;
            if (p - reported >= 0.01)
            {
                reported = p;
                Progress = p;
                StatusChanged?.Invoke();
            }
        }
    }

    /// <summary>Unpacks with the tar built into Windows 10/11.</summary>
    private static async Task ExtractAsync(string archive)
    {
        var tar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");
        var info = new ProcessStartInfo(File.Exists(tar) ? tar : "tar.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Folder,
        };
        info.ArgumentList.Add("-xjf");
        info.ArgumentList.Add(archive);
        info.ArgumentList.Add("-C");
        info.ArgumentList.Add(Folder);
        using var process = Process.Start(info) ?? throw new IOException("tar did not start");
        var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException("tar: " + error.Trim());
    }

    private sealed record Files(string Library, string Runtime, string Model, string Voices, string Tokens, string Data);

    private static Files? Locate()
    {
        try
        {
            var library = Directory.GetFiles(Folder, "sherpa-onnx-c-api.dll", SearchOption.AllDirectories).FirstOrDefault();
            var model = Directory.GetFiles(Folder, "model.int8.onnx", SearchOption.AllDirectories).FirstOrDefault();
            if (library == null || model == null) return null;
            var libDir = Path.GetDirectoryName(library)!;
            var modelDir = Path.GetDirectoryName(model)!;
            var files = new Files(library, Path.Combine(libDir, "onnxruntime.dll"), model,
                Path.Combine(modelDir, "voices.bin"), Path.Combine(modelDir, "tokens.txt"), Path.Combine(modelDir, "espeak-ng-data"));
            return File.Exists(files.Runtime) && File.Exists(files.Voices) && File.Exists(files.Tokens) && Directory.Exists(files.Data)
                ? files : null;
        }
        catch (Exception) { return null; }
    }

    // ---- speaking ----

    private static readonly object EngineGate = new();
    private static IntPtr _tts;
    private static string _ttsLang = "";
    private static Native? _native;
    private static int _generation;
    private static Windows.Media.Playback.MediaPlayer? _player;

    public static bool IsSpeaking { get; private set; }
    public static event Action? SpeakingEnded;

    /// <summary>
    /// Starts speaking with the female or male voice; completes once the voice is audible.
    /// False if it can't (not installed, language not spoken, engine failed).
    /// </summary>
    public static Task<bool> SpeakAsync(string text, string code, double rate, bool male)
    {
        var speaker = SpeakerFor(code);
        if (speaker == null || !Installed || string.IsNullOrWhiteSpace(text)) return Task.FromResult(false);
        Stop();
        int generation = ++_generation;
        int sid = male ? speaker.Male : speaker.Female;
        float speed = (float)Math.Clamp(rate, 0.5, 2.0);
        var chunks = Chunks(text);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsSpeaking = true;
        _ = Task.Run(async () =>
        {
            try
            {
                // Make the next piece while the current one plays.
                var next = Task.Run(() => Synthesize(chunks[0], speaker.Lang, sid, speed, generation));
                for (int i = 0; i < chunks.Count; i++)
                {
                    var wav = await next.ConfigureAwait(false);
                    if (generation != _generation || wav == null) break;
                    if (i + 1 < chunks.Count)
                    {
                        var piece = chunks[i + 1];
                        next = Task.Run(() => Synthesize(piece, speaker.Lang, sid, speed, generation));
                    }
                    var playing = PlayAsync(wav, generation);
                    started.TrySetResult(true);
                    await playing.ConfigureAwait(false);
                    try { File.Delete(wav); } catch (Exception) { }
                    if (generation != _generation) break;
                }
            }
            catch (Exception ex) { Log.Write("Neural voice: " + ex.Message); }
            finally
            {
                started.TrySetResult(false);
                if (generation == _generation && IsSpeaking)
                {
                    IsSpeaking = false;
                    SpeakingEnded?.Invoke();
                }
            }
        });
        return started.Task;
    }

    public static void Stop()
    {
        _generation++;
        var player = _player;
        _player = null;
        if (player != null)
        {
            try { player.Pause(); player.Dispose(); } catch (Exception) { }
        }
        if (IsSpeaking)
        {
            IsSpeaking = false;
            SpeakingEnded?.Invoke();
        }
    }

    private static Task PlayAsync(string wav, int generation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var player = new Windows.Media.Playback.MediaPlayer();
        player.MediaEnded += (_, _) => done.TrySetResult();
        player.MediaFailed += (_, _) => done.TrySetResult();
        player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(wav));
        if (generation != _generation)
        {
            player.Dispose();
            return Task.CompletedTask;
        }
        _player = player;
        player.Play();
        // Stop() disposes the player without raising MediaEnded; poll for that.
        _ = Task.Run(async () =>
        {
            while (!done.Task.IsCompleted)
            {
                if (generation != _generation) done.TrySetResult();
                await Task.Delay(100).ConfigureAwait(false);
            }
            if (ReferenceEquals(_player, player)) _player = null;
            try { player.Dispose(); } catch (Exception) { }
        });
        return done.Task;
    }

    /// <summary>Short first piece so the voice starts quickly; then sentence groups.</summary>
    internal static List<string> Chunks(string text)
    {
        var sentences = Regex.Split(text.Replace("\r", ""), @"(?<=[.!?;:…])\s+|\n+")
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var sentence in sentences)
        {
            int limit = chunks.Count == 0 ? 140 : 320;
            if (current.Length > 0 && current.Length + sentence.Length > limit)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(sentence);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        if (chunks.Count == 0) chunks.Add(text.Trim());
        return chunks;
    }

    /// <summary>Makes a WAV file for one piece of text; null on failure or when stopped.</summary>
    private static string? Synthesize(string text, string lang, int sid, float speed, int generation)
    {
        lock (EngineGate)
        {
            if (generation != _generation) return null;
            if (!EnsureEngine(lang)) return null;
            var utf8 = Encoding.UTF8.GetBytes(text + "\0");
            var audio = _native!.Generate(_tts, utf8, sid, speed);
            if (audio == IntPtr.Zero) return null;
            try
            {
                var info = Marshal.PtrToStructure<GeneratedAudio>(audio);
                if (info.Count <= 0 || info.Samples == IntPtr.Zero) return null;
                var samples = new float[info.Count];
                Marshal.Copy(info.Samples, samples, 0, info.Count);
                var path = Path.Combine(Path.GetTempPath(), $"mouseia-voice-{Guid.NewGuid():N}.wav");
                WriteWav(path, samples, info.SampleRate);
                return path;
            }
            finally { _native.DestroyAudio(audio); }
        }
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * 32767f, -32768f, 32767f));
    }

    /// <summary>Loads the model for a language (kept loaded until another language is needed).</summary>
    private static bool EnsureEngine(string lang)
    {
        if (_tts != IntPtr.Zero && _ttsLang == lang) return true;
        var files = Locate();
        if (files == null) return false;
        _native ??= new Native(files);
        if (_tts != IntPtr.Zero)
        {
            _native.Destroy(_tts);
            _tts = IntPtr.Zero;
        }
        var config = new TtsConfig
        {
            Model = new ModelConfig
            {
                Vits = new VitsConfig { Model = "", Lexicon = "", Tokens = "", DataDir = "", NoiseScale = 0.667f, NoiseScaleW = 0.8f, LengthScale = 1, DictDir = "" },
                NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                Debug = 0,
                Provider = "cpu",
                Matcha = new MatchaConfig { AcousticModel = "", Vocoder = "", Lexicon = "", Tokens = "", DataDir = "", NoiseScale = 1, LengthScale = 1, DictDir = "" },
                Kokoro = new KokoroConfig
                {
                    Model = files.Model, Voices = files.Voices, Tokens = files.Tokens, DataDir = files.Data,
                    LengthScale = 1, DictDir = "", Lexicon = "", Lang = lang,
                },
                Kitten = new KittenConfig { Model = "", Voices = "", Tokens = "", DataDir = "", LengthScale = 1 },
                Zipvoice = new ZipvoiceConfig { Tokens = "", Encoder = "", Decoder = "", Vocoder = "", DataDir = "", Lexicon = "", FeatScale = 0.1f, TShift = 0.5f, TargetRms = 0.1f, GuidanceScale = 1 },
                Pocket = new PocketConfig { LmFlow = "", LmMain = "", Encoder = "", Decoder = "", TextConditioner = "", VocabJson = "", TokenScoresJson = "", VoiceEmbeddingCacheCapacity = 50 },
                Supertonic = new SupertonicConfig { DurationPredictor = "", TextEncoder = "", VectorEstimator = "", Vocoder = "", TtsJson = "", UnicodeIndexer = "", VoiceStyle = "" },
            },
            RuleFsts = "",
            MaxNumSentences = 1,
            RuleFars = "",
            SilenceScale = 0.2f,
        };
        _tts = _native.Create(ref config);
        _ttsLang = _tts == IntPtr.Zero ? "" : lang;
        if (_tts == IntPtr.Zero) Log.Write("Neural voice: engine did not load");
        return _tts != IntPtr.Zero;
    }

    // ---- native interface (sherpa-onnx C API, v1.13.8 layout) ----

    private sealed class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr CreateFn(ref TtsConfig config);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DestroyFn(IntPtr tts);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr GenerateFn(IntPtr tts, byte[] utf8Text, int sid, float speed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void DestroyAudioFn(IntPtr audio);

        public readonly CreateFn Create;
        public readonly DestroyFn Destroy;
        public readonly GenerateFn Generate;
        public readonly DestroyAudioFn DestroyAudio;

        public Native(Files files)
        {
            // The runtime first, from our folder, so the engine finds it rather than any other copy.
            NativeLibrary.Load(files.Runtime);
            var lib = NativeLibrary.Load(files.Library);
            Create = Get<CreateFn>(lib, "SherpaOnnxCreateOfflineTts");
            Destroy = Get<DestroyFn>(lib, "SherpaOnnxDestroyOfflineTts");
            Generate = Get<GenerateFn>(lib, "SherpaOnnxOfflineTtsGenerate");
            DestroyAudio = Get<DestroyAudioFn>(lib, "SherpaOnnxDestroyOfflineTtsGeneratedAudio");
        }

        private static T Get<T>(IntPtr lib, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GeneratedAudio { public IntPtr Samples; public int Count; public int SampleRate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct VitsConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string Model, Lexicon, Tokens, DataDir;
        public float NoiseScale, NoiseScaleW, LengthScale;
        [MarshalAs(UnmanagedType.LPStr)] public string DictDir;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MatchaConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string AcousticModel, Vocoder, Lexicon, Tokens, DataDir;
        public float NoiseScale, LengthScale;
        [MarshalAs(UnmanagedType.LPStr)] public string DictDir;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KokoroConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string Model, Voices, Tokens, DataDir;
        public float LengthScale;
        [MarshalAs(UnmanagedType.LPStr)] public string DictDir, Lexicon, Lang;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KittenConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string Model, Voices, Tokens, DataDir;
        public float LengthScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ZipvoiceConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string Tokens, Encoder, Decoder, Vocoder, DataDir, Lexicon;
        public float FeatScale, TShift, TargetRms, GuidanceScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PocketConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string LmFlow, LmMain, Encoder, Decoder, TextConditioner, VocabJson, TokenScoresJson;
        public int VoiceEmbeddingCacheCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SupertonicConfig
    {
        [MarshalAs(UnmanagedType.LPStr)] public string DurationPredictor, TextEncoder, VectorEstimator, Vocoder, TtsJson, UnicodeIndexer, VoiceStyle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModelConfig
    {
        public VitsConfig Vits;
        public int NumThreads;
        public int Debug;
        [MarshalAs(UnmanagedType.LPStr)] public string Provider;
        public MatchaConfig Matcha;
        public KokoroConfig Kokoro;
        public KittenConfig Kitten;
        public ZipvoiceConfig Zipvoice;
        public PocketConfig Pocket;
        public SupertonicConfig Supertonic;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TtsConfig
    {
        public ModelConfig Model;
        [MarshalAs(UnmanagedType.LPStr)] public string RuleFsts;
        public int MaxNumSentences;
        [MarshalAs(UnmanagedType.LPStr)] public string RuleFars;
        public float SilenceScale;
    }
}
