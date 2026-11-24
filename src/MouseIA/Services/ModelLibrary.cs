using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MouseIA.Native;

namespace MouseIA.Services;

/// <summary>
/// The local AI models MouseIA offers to download, the ones already on this computer,
/// and their cache: sizes, deleting and cleaning.
/// </summary>
internal static class ModelLibrary
{
    public sealed record Download(string Url, string FileName, long Size);

    public sealed record Entry(string Id, string Name, string DetailPt, string DetailEn, string Engine, bool Recommended,
        IReadOnlyList<Download> Files)
    {
        public long TotalSize => Files.Sum(f => f.Size);
        public string Detail => Loc.IsPortuguese ? DetailPt : DetailEn;
        public string Folder => Path.Combine(ModelsFolder, Id);
        public string ModelPath => Path.Combine(Folder, Files[0].FileName);
        public string? MmprojPath => Files.Count > 1 ? Path.Combine(Folder, Files[1].FileName) : null;
        public bool IsDownloaded => Files.All(f => File.Exists(Path.Combine(Folder, f.FileName)));
    }

    private const string Hf = "https://huggingface.co/";

    private static Download F(string repo, string file, long size) => new($"{Hf}{repo}/resolve/main/{file}", file, size);

    /// <summary>Hand-picked, all from their official or best-known Hugging Face repositories.</summary>
    public static readonly IReadOnlyList<Entry> Catalog = new List<Entry>
    {
        new("gemma4-e4b-litert", "Gemma 4 E4B (LiteRT)",
            "Google · o padrão do MouseIA · rápido na placa de vídeo, lê imagens e traduz muito bem",
            "Google · MouseIA's default · fast on the graphics card, reads images, translates very well",
            "LiteRT-LM", true,
            new[] { F("litert-community/gemma-4-E4B-it-litert-lm", "gemma-4-E4B-it.litertlm", 3_650_000_000) }),
        new("gemma4-e2b-litert", "Gemma 4 E2B (LiteRT)",
            "Google · a versão leve do padrão, para PCs mais simples",
            "Google · the light version of the default, for simpler PCs",
            "LiteRT-LM", false,
            new[] { F("litert-community/gemma-4-E2B-it-litert-lm", "gemma-4-E2B-it.litertlm", 2_590_000_000) }),
        new("gemma4-e4b-gguf", "Gemma 4 E4B (GGUF)",
            "Google · o mesmo modelo no llama.cpp, com visão",
            "Google · the same model on llama.cpp, with vision",
            "llama.cpp", false,
            new[]
            {
                F("ggml-org/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-Q4_0.gguf", 4_590_807_392),
                F("ggml-org/gemma-4-E4B-it-GGUF", "mmproj-gemma-4-E4B-it-Q8_0.gguf", 559_874_816),
            }),
        new("gemma4-e2b-gguf", "Gemma 4 E2B (GGUF)",
            "Google · leve, com visão, no llama.cpp",
            "Google · light, with vision, on llama.cpp",
            "llama.cpp", false,
            new[]
            {
                F("ggml-org/gemma-4-E2B-it-GGUF", "gemma-4-E2B-it-Q4_0.gguf", 2_841_481_184),
                F("ggml-org/gemma-4-E2B-it-GGUF", "mmproj-gemma-4-E2B-it-Q8_0.gguf", 557_368_064),
            }),
        new("gemma4-12b-gguf", "Gemma 4 12B",
            "Google · mais inteligente · ideal com placa de vídeo de 8 GB ou mais",
            "Google · smarter · best with an 8 GB+ graphics card",
            "llama.cpp", false,
            new[]
            {
                F("unsloth/gemma-4-12b-it-GGUF", "gemma-4-12b-it-Q4_K_M.gguf", 7_121_861_440),
                F("unsloth/gemma-4-12b-it-GGUF", "mmproj-F16.gguf", 175_115_840),
            }),
        new("gemma4-26b-gguf", "Gemma 4 26B A4B",
            "Google · o mais forte da lista · para PCs potentes (16 GB+ de vídeo ou 32 GB de RAM)",
            "Google · the strongest here · for powerful PCs (16 GB+ VRAM or 32 GB RAM)",
            "llama.cpp", false,
            new[]
            {
                F("ggml-org/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it-Q4_0.gguf", 14_618_145_824),
                F("ggml-org/gemma-4-26B-A4B-it-GGUF", "mmproj-gemma-4-26B-A4B-it-Q8_0.gguf", 806_408_320),
            }),
        new("qwen35-0.8b-gguf", "Qwen3.5 0.8B",
            "Alibaba · minúsculo e muito rápido, para PCs fracos",
            "Alibaba · tiny and very fast, for weak PCs",
            "llama.cpp", false,
            new[]
            {
                F("unsloth/Qwen3.5-0.8B-GGUF", "Qwen3.5-0.8B-Q4_K_M.gguf", 532_517_120),
                F("unsloth/Qwen3.5-0.8B-GGUF", "mmproj-F16.gguf", 204_987_232),
            }),
        new("qwen35-2b-gguf", "Qwen3.5 2B",
            "Alibaba · pequeno, rápido e com visão",
            "Alibaba · small, fast, with vision",
            "llama.cpp", false,
            new[]
            {
                F("unsloth/Qwen3.5-2B-GGUF", "Qwen3.5-2B-Q4_K_M.gguf", 1_280_835_840),
                F("unsloth/Qwen3.5-2B-GGUF", "mmproj-F16.gguf", 668_227_264),
            }),
        new("qwen35-4b-gguf", "Qwen3.5 4B",
            "Alibaba · ótimo equilíbrio, forte em vários idiomas",
            "Alibaba · great balance, strong in many languages",
            "llama.cpp", false,
            new[]
            {
                F("unsloth/Qwen3.5-4B-GGUF", "Qwen3.5-4B-Q4_K_M.gguf", 2_740_937_888),
                F("unsloth/Qwen3.5-4B-GGUF", "mmproj-F16.gguf", 672_423_616),
            }),
        new("qwen35-9b-gguf", "Qwen3.5 9B",
            "Alibaba · mais inteligente · ideal com placa de vídeo de 8 GB ou mais",
            "Alibaba · smarter · best with an 8 GB+ graphics card",
            "llama.cpp", false,
            new[]
            {
                F("unsloth/Qwen3.5-9B-GGUF", "Qwen3.5-9B-Q4_K_M.gguf", 5_680_522_464),
                F("unsloth/Qwen3.5-9B-GGUF", "mmproj-F16.gguf", 918_166_080),
            }),
    };

    public const string BrowseUrl = "https://huggingface.co/models?apps=llama.cpp&sort=trending";

    /// <summary>Documents\MouseIA Cache\&lt;PC&gt;\Modelos</summary>
    public static string ModelsFolder => Path.Combine(LocalEngine.CacheFolder, "Modelos");

    // ---- downloading ----

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly Dictionary<string, (CancellationTokenSource Cts, double Progress)> Active = new();
    public static event Action? Changed;

    public static bool IsDownloading(string id) { lock (Active) return Active.ContainsKey(id); }
    public static double ProgressOf(string id) { lock (Active) return Active.TryGetValue(id, out var a) ? a.Progress : 0; }

    public static void Cancel(string id)
    {
        lock (Active) if (Active.TryGetValue(id, out var a)) a.Cts.Cancel();
    }

    /// <summary>Downloads every file of an entry; resumes a broken download where it stopped.</summary>
    public static async Task DownloadAsync(Entry entry)
    {
        var cts = new CancellationTokenSource();
        lock (Active)
        {
            if (Active.ContainsKey(entry.Id)) return;
            Active[entry.Id] = (cts, 0);
        }
        Changed?.Invoke();
        try
        {
            Directory.CreateDirectory(entry.Folder);
            long total = Math.Max(1, entry.TotalSize), before = 0;
            foreach (var file in entry.Files)
            {
                var path = Path.Combine(entry.Folder, file.FileName);
                if (!File.Exists(path)) await DownloadFileAsync(file, path, before, total, entry.Id, cts.Token).ConfigureAwait(false);
                before += file.Size;
            }
        }
        finally
        {
            lock (Active) Active.Remove(entry.Id);
            Changed?.Invoke();
        }
    }

    private static async Task DownloadFileAsync(Download file, string path, long before, long total, string id, CancellationToken ct)
    {
        var part = path + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
        if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // no resume: start over
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long done = have;
            int read;
            var last = DateTime.UtcNow;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if ((DateTime.UtcNow - last).TotalMilliseconds < 250) continue;
                last = DateTime.UtcNow;
                lock (Active) if (Active.TryGetValue(id, out var a)) Active[id] = (a.Cts, Math.Min(1, (before + done) / (double)total));
                Changed?.Invoke();
            }
        }
        File.Move(part, path, overwrite: true);
    }

    // ---- what's on this computer ----

    public sealed record Installed(string Path, string Name, long Size, Entry? Entry);

    /// <summary>Models downloaded by MouseIA plus the one chosen by hand (if it lives elsewhere).</summary>
    public static List<Installed> InstalledModels(string? chosen)
    {
        var list = new List<Installed>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path, Entry? entry)
        {
            if (!File.Exists(path) || !seen.Add(System.IO.Path.GetFullPath(path))) return;
            list.Add(new Installed(path, entry?.Name ?? System.IO.Path.GetFileNameWithoutExtension(path), SafeLength(path), entry));
        }
        foreach (var entry in Catalog.Where(e => File.Exists(e.ModelPath))) Add(entry.ModelPath, entry);
        try
        {
            if (Directory.Exists(ModelsFolder))
                foreach (var file in Directory.EnumerateFiles(ModelsFolder, "*.*", SearchOption.AllDirectories).Where(IsModelFile))
                    Add(file, null);
        }
        catch (Exception) { }
        if (!string.IsNullOrEmpty(chosen)) Add(chosen, null);
        return list;
    }

    public static bool IsModelFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (name.EndsWith(".litertlm", StringComparison.OrdinalIgnoreCase)) return true;
        return name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase) &&
               !name.StartsWith("mtp-", StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception) { return 0; }
    }

    /// <summary>
    /// Cache files LiteRT-LM made for a model: files named after it (but not the model itself),
    /// next to the model and in the cache folder.
    /// </summary>
    public static List<string> CacheFilesFor(string model)
    {
        var name = System.IO.Path.GetFileName(model);
        var result = new List<string>();
        foreach (var dir in new[] { System.IO.Path.GetDirectoryName(model), LocalEngine.CacheFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (dir == null || !Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, name + "*"))
                    if (!System.IO.Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase)) result.Add(file);
            }
            catch (Exception) { }
        }
        return result;
    }

    /// <summary>Every cache file in the cache folder (anything there that isn't a model).</summary>
    public static List<string> AllCacheFiles()
    {
        var result = new List<string>();
        try
        {
            if (Directory.Exists(LocalEngine.CacheFolder))
                foreach (var file in Directory.EnumerateFiles(LocalEngine.CacheFolder))
                    if (!IsModelFile(file)) result.Add(file);
        }
        catch (Exception) { }
        return result;
    }

    public static long SizeOf(IEnumerable<string> files) => files.Sum(SafeLength);

    /// <summary>The model, its vision file and the link MouseIA made for it in the cache folder.</summary>
    public static List<string> FilesOfModel(Installed model)
    {
        var files = new List<string> { model.Path };
        if (model.Entry?.MmprojPath is { } mmproj && File.Exists(mmproj)) files.Add(mmproj);
        var link = System.IO.Path.Combine(LocalEngine.CacheFolder, System.IO.Path.GetFileName(model.Path));
        if (File.Exists(link) && !link.Equals(model.Path, StringComparison.OrdinalIgnoreCase)) files.Add(link);
        // Leftovers of an unfinished download.
        files.AddRange(files.ToList().Select(f => f + ".part").Where(File.Exists));
        return files;
    }

    /// <summary>Deletes files; returns how many couldn't be deleted (in use).</summary>
    public static int Delete(IEnumerable<string> files)
    {
        int failed = 0;
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex)
            {
                failed++;
                Log.Write($"Delete {file}: {ex.Message}");
            }
        }
        // Empty model folders go too.
        try
        {
            if (Directory.Exists(ModelsFolder))
                foreach (var dir in Directory.GetDirectories(ModelsFolder))
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception) { }
        return failed;
    }

    public static string FormatSize(long bytes)
    {
        double gb = bytes / 1073741824.0;
        if (gb >= 1) return $"{gb:0.0} GB";
        double mb = bytes / 1048576.0;
        return mb >= 1 ? $"{mb:0} MB" : $"{bytes / 1024.0:0} KB";
    }
}
