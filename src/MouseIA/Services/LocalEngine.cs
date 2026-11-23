using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MouseIA.Core;
using MouseIA.Native;

namespace MouseIA.Services;

public sealed class LocalEngineException : Exception
{
    public LocalEngineException(string message) : base(message) { }
}

/// <summary>
/// Runs a model file chosen by the user: .gguf with llama.cpp (downloaded on first use)
/// or .litertlm with the LiteRT-LM command line. Both expose an OpenAI-compatible server.
/// </summary>
internal static class LocalEngine
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static string _runningKey = "";
    private static string _baseUrl = "";
    private static string _modelId = "local";
    private static readonly StringBuilder Output = new();

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = true, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly HttpClient LocalHttp = new(new SocketsHttpHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(4),
    };

    public static string EnginesFolder => Path.Combine(AppSettings.DefaultFolder, "engines");

    public static bool IsRunning => _process is { HasExited: false };

    public static bool IsLiteRt(string path) => path.EndsWith(".litertlm", StringComparison.OrdinalIgnoreCase);

    /// <summary>A vision projector next to the model, e.g. mmproj-model-f16.gguf.</summary>
    public static string? FindMmproj(string modelPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(modelPath);
            if (folder == null || !Directory.Exists(folder)) return null;
            return Directory.EnumerateFiles(folder, "*.gguf")
                .Where(f => Path.GetFileName(f).Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Contains("f16", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    public static string? InstalledLlamaServer(string variant)
    {
        var folder = Path.Combine(EnginesFolder, "llama-" + variant);
        if (!Directory.Exists(folder)) return null;
        return Directory.EnumerateFiles(folder, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static string LiteRtRoot => Path.Combine(EnginesFolder, "litert");
    private static string LiteRtBin => Path.Combine(LiteRtRoot, "bin");

    public static string? FindLiteRtCli()
    {
        var names = new[] { "litert-lm.exe", "litert-lm.cmd", "litert-lm" };
        var paths = new List<string> { LiteRtBin };
        paths.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        paths.Add(Path.Combine(home, ".local", "bin"));
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var py in SafeDirs(Path.Combine(appData, "Python"))) paths.Add(Path.Combine(py, "Scripts"));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var py in SafeDirs(Path.Combine(local, "Programs", "Python"))) paths.Add(Path.Combine(py, "Scripts"));
        foreach (var dir in paths)
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    private static IEnumerable<string> SafeDirs(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder) : Array.Empty<string>(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>Starts (or reuses) the engine for the chosen file. Returns the base URL and the model id to send.</summary>
    public static async Task<(string BaseUrl, string Model)> EnsureRunningAsync(AppSettings settings, IProgress<string>? progress, CancellationToken ct)
    {
        var file = settings.LocalModelFile;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            throw new LocalEngineException(Loc.T("engine_no_file"));
        var mmproj = IsLiteRt(file) ? "" : (File.Exists(settings.LocalMmproj) ? settings.LocalMmproj : FindMmproj(file) ?? "");
        var key = $"{file}|{mmproj}|{settings.LocalBackend}";

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (key == _runningKey && IsRunning && await IsHealthyAsync(ct).ConfigureAwait(false))
                return (_baseUrl, _modelId);
            Stop();
            if (IsLiteRt(file))
                await StartLiteRtAsync(file, settings.LocalBackend, progress, ct).ConfigureAwait(false);
            else
                await StartLlamaAsync(file, mmproj, settings.LocalBackend, progress, ct).ConfigureAwait(false);
            _runningKey = key;
            return (_baseUrl, _modelId);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static bool HasVision(AppSettings settings)
    {
        var file = settings.LocalModelFile;
        if (string.IsNullOrEmpty(file)) return false;
        if (IsLiteRt(file)) return true; // LiteRT-LM Gemma models are multimodal
        return File.Exists(settings.LocalMmproj) || FindMmproj(file) != null;
    }

    // ---- llama.cpp ----

    private static async Task StartLlamaAsync(string model, string mmproj, string backend, IProgress<string>? progress, CancellationToken ct)
    {
        var variants = backend == "cpu" ? new[] { "cpu" } : new[] { "vulkan", "cpu" };
        string lastError = "";
        foreach (var variant in variants)
        {
            var exe = InstalledLlamaServer(variant) ?? await DownloadLlamaAsync(variant, progress, ct).ConfigureAwait(false);
            int port = FreePort();
            var args = new StringBuilder();
            args.Append($"-m \"{model}\" --host 127.0.0.1 --port {port} -c 8192");
            if (!string.IsNullOrEmpty(mmproj)) args.Append($" --mmproj \"{mmproj}\"");
            args.Append(variant == "cpu" ? " -ngl 0" : " -ngl 999");
            progress?.Report(Loc.T("engine_loading"));
            if (await LaunchAndWaitAsync(exe, args.ToString(), port, "/health", ct).ConfigureAwait(false))
            {
                _modelId = Path.GetFileNameWithoutExtension(model);
                return;
            }
            lastError = Tail();
            Log.Write($"llama-server ({variant}) failed: {lastError}");
            Stop();
        }
        throw new LocalEngineException(Loc.Format("engine_failed", Shorten(lastError)));
    }

    private static async Task<string> DownloadLlamaAsync(string variant, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report(Loc.T("engine_finding"));
        var url = await FindLlamaAssetAsync(variant, ct).ConfigureAwait(false)
                  ?? throw new LocalEngineException(Loc.T("engine_no_download"));
        var folder = Path.Combine(EnginesFolder, "llama-" + variant);
        var zipPath = Path.Combine(EnginesFolder, $"llama-{variant}.zip");
        await DownloadAsync(url, zipPath, "engine_downloading", progress, ct).ConfigureAwait(false);
        progress?.Report(Loc.T("engine_extracting"));
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, folder);
        File.Delete(zipPath);
        return InstalledLlamaServer(variant) ?? throw new LocalEngineException(Loc.T("engine_no_download"));
    }

    /// <summary>Finds the newest llama.cpp release that has a Windows build for the variant (vulkan or cpu).</summary>
    private static async Task<string?> FindLlamaAssetAsync(string variant, CancellationToken ct)
    {
        var pattern = new Regex($@"^llama-b\d+-bin-win-{variant}-x64\.zip$", RegexOptions.IgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20");
        request.Headers.UserAgent.ParseAdd("MouseIA/1.1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets)) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (pattern.IsMatch(name)) return asset.GetProperty("browser_download_url").GetString();
            }
        }
        return null;
    }

    // ---- LiteRT-LM ----

    private static async Task StartLiteRtAsync(string model, string backend, IProgress<string>? progress, CancellationToken ct)
    {
        var cli = FindLiteRtCli() ?? await InstallLiteRtAsync(progress, ct).ConfigureAwait(false);
        // The server accepts the file path itself as the model; only a path with a comma
        // (the separator for ",gpu") needs importing under a simple name first.
        string reference = model;
        if (model.Contains(','))
        {
            reference = "mouseia-" + Regex.Replace(Path.GetFileNameWithoutExtension(model).ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            progress?.Report(Loc.T("engine_importing"));
            var import = await RunToEndAsync(cli, $"import \"{model}\" {reference}", TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            if (import.ExitCode != 0) Log.Write("litert-lm import: " + import.Output);
        }
        int port = FreePort();
        progress?.Report(Loc.T("engine_loading"));
        if (!await LaunchAndWaitAsync(cli, $"serve --host 127.0.0.1 --port {port}", port, "/v1/models", ct).ConfigureAwait(false))
        {
            var error = Tail();
            Stop();
            throw new LocalEngineException(Loc.Format("engine_failed", Shorten(error)));
        }
        _modelId = reference + (backend == "cpu" ? ",cpu" : ",gpu");
    }

    private static async Task DownloadAsync(string url, string path, string messageKey, IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = File.Create(path);
        var buffer = new byte[1 << 16];
        long done = 0;
        int lastPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            int percent = total > 0 ? (int)(done * 100 / total) : -1;
            if (percent != lastPercent && percent % 5 == 0)
            {
                lastPercent = percent;
                progress?.Report(Loc.Format(messageKey, percent >= 0 ? percent + "%" : $"{done / 1048576} MB"));
            }
        }
    }

    /// <summary>
    /// Installs the LiteRT-LM command line into MouseIA's own folder: uv (a single exe) fetches a private
    /// Python and the litert-lm package. Nothing is added to the system or to PATH.
    /// </summary>
    public static async Task<string> InstallLiteRtAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var uvFolder = Path.Combine(LiteRtRoot, "uv");
        var uv = Directory.Exists(uvFolder)
            ? Directory.EnumerateFiles(uvFolder, "uv.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (uv == null)
        {
            var zip = Path.Combine(LiteRtRoot, "uv.zip");
            await DownloadAsync("https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip",
                zip, "litert_downloading", progress, ct).ConfigureAwait(false);
            if (Directory.Exists(uvFolder)) Directory.Delete(uvFolder, recursive: true);
            ZipFile.ExtractToDirectory(zip, uvFolder);
            File.Delete(zip);
            uv = Directory.EnumerateFiles(uvFolder, "uv.exe", SearchOption.AllDirectories).FirstOrDefault()
                 ?? throw new LocalEngineException(Loc.T("litert_install_failed"));
        }
        progress?.Report(Loc.T("litert_installing"));
        var env = new Dictionary<string, string>
        {
            ["UV_TOOL_DIR"] = Path.Combine(LiteRtRoot, "tools"),
            ["UV_TOOL_BIN_DIR"] = LiteRtBin,
            ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(LiteRtRoot, "python"),
            ["UV_CACHE_DIR"] = Path.Combine(LiteRtRoot, "cache"),
            ["UV_PYTHON_PREFERENCE"] = "only-managed",
            ["UV_NO_MODIFY_PATH"] = "1",
        };
        var result = await RunToEndAsync(uv, "tool install --force --python 3.12 litert-lm", TimeSpan.FromMinutes(20), ct, env)
            .ConfigureAwait(false);
        var cli = FindLiteRtCli();
        if (result.ExitCode != 0 || cli == null)
        {
            Log.Write("uv tool install litert-lm: " + result.Output);
            throw new LocalEngineException(Loc.Format("litert_install_failed_detail", Shorten(result.Output.Trim())));
        }
        // The package cache isn't needed once installed.
        try { Directory.Delete(Path.Combine(LiteRtRoot, "cache"), recursive: true); } catch (Exception) { }
        return cli;
    }

    // ---- process handling ----

    private static async Task<bool> LaunchAndWaitAsync(string exe, string args, int port, string healthPath, CancellationToken ct)
    {
        lock (Output) Output.Clear();
        var info = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        if (!process.Start()) return false;
        JobObject.Attach(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _baseUrl = $"http://127.0.0.1:{port}/v1";

        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            try
            {
                using var response = await LocalHttp.GetAsync($"http://127.0.0.1:{port}{healthPath}", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var root = _baseUrl.EndsWith("/v1") ? _baseUrl[..^3] : _baseUrl;
            using var response = await LocalHttp.GetAsync(root + (IsLiteRt(_runningKey.Split('|')[0]) ? "/v1/models" : "/health"), ct)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    private static async Task<(int ExitCode, string Output)> RunToEndAsync(string exe, string args, TimeSpan timeout,
        CancellationToken ct, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        if (environment != null)
            foreach (var (name, value) in environment) info.Environment[name] = value;
        using var process = Process.Start(info) ?? throw new LocalEngineException(Loc.T("litert_install_failed"));
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try { await process.WaitForExitAsync(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
        return (process.ExitCode, (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false)));
    }

    private static void Append(string? line)
    {
        if (line == null) return;
        lock (Output)
        {
            Output.AppendLine(line);
            if (Output.Length > 20000) Output.Remove(0, Output.Length - 20000);
        }
    }

    private static string Tail()
    {
        lock (Output)
        {
            var lines = Output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var useful = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                                          l.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                                          l.Contains("unknown", StringComparison.OrdinalIgnoreCase)).TakeLast(3).ToList();
            return string.Join("\n", useful.Count > 0 ? useful : lines.TakeLast(3));
        }
    }

    private static string Shorten(string text) => text.Length > 300 ? text[^300..] : text;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void Stop()
    {
        var process = _process;
        _process = null;
        _runningKey = "";
        if (process == null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
        process.Dispose();
    }
}

/// <summary>Ties child processes to MouseIA so they end even if MouseIA crashes.</summary>
internal static class JobObject
{
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private static IntPtr _job;

    public static void Attach(Process process)
    {
        try
        {
            if (_job == IntPtr.Zero)
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
            }
            AssignProcessToJobObject(_job, process.Handle);
        }
        catch (Exception ex)
        {
            Log.Write("Job object: " + ex.Message);
        }
    }
}
