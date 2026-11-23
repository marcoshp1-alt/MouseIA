using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MouseIA.Core;
using MouseIA.Native;

namespace MouseIA.Services;

/// <summary>Finds new versions on GitHub and installs them: the installer silently, or the portable exe in place.</summary>
internal static class Updater
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public static Version Current =>
        UpdateInfo.Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0));

    /// <summary>True when this copy was installed by the setup (under %LocalAppData%\Programs\MouseIA).</summary>
    public static bool IsInstalled
    {
        get
        {
            var exe = Environment.ProcessPath ?? "";
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MouseIA");
            return exe.StartsWith(programs, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The newest release if it is newer than this copy, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateInfo.LatestApiUrl);
        request.Headers.UserAgent.ParseAdd("MouseIA/" + Current);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no release published yet
        response.EnsureSuccessStatusCode();
        var info = UpdateInfo.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return info != null && info.IsNewerThan(Current) ? info : null;
    }

    public static bool CanInstall(UpdateInfo info) => IsInstalled ? info.SetupUrl != null : info.PortableUrl != null || info.SetupUrl != null;

    /// <summary>Downloads the update and starts replacing this copy. The caller exits the app right after.</summary>
    public static async Task InstallAsync(UpdateInfo info, IProgress<int>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "MouseIA-update");
        Directory.CreateDirectory(folder);
        bool useSetup = IsInstalled || info.PortableUrl == null;
        var url = useSetup ? info.SetupUrl! : info.PortableUrl!;
        var file = Path.Combine(folder, useSetup ? $"MouseIA-Setup-{info.Version}.exe" : $"MouseIA-{info.Version}.exe");
        await DownloadAsync(url, file, progress, ct).ConfigureAwait(false);

        if (useSetup)
        {
            // The setup closes MouseIA, installs, and opens it again (see installer/MouseIA.iss).
            Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true,
            });
            return;
        }

        // Portable: a tiny script waits for this process to end, swaps the exe and starts it again.
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown exe path.");
        var script = Path.Combine(folder, "update.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $":wait\r\ntasklist /FI \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul\r\n" +
            "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
            $"move /Y \"{file}\" \"{exe}\" >nul\r\n" +
            $"start \"\" \"{exe}\"\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static async Task DownloadAsync(string url, string path, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var partial = path + ".part";
        await using (var output = File.Create(partial))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int last = -1, read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0)
                {
                    int percent = (int)(done * 100 / total);
                    if (percent != last) { last = percent; progress?.Report(percent); }
                }
            }
        }
        File.Move(partial, path, overwrite: true);
        Log.Write($"Update downloaded: {Path.GetFileName(path)}");
    }
}
