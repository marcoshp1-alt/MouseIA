using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MouseIA.Core;
using MouseIA.Native;

namespace MouseIA.Services;

/// <summary>Sizes and removal of what MouseIA put on the computer. Files still in use are removed at the next start.</summary>
internal static class Cleanup
{
    private static string PendingFile => Path.Combine(AppSettings.DefaultFolder, "pending-delete.txt");

    public static long FolderSize(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return 0;
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(f =>
            {
                try { return new FileInfo(f).Length; }
                catch (Exception) { return 0L; }
            });
        }
        catch (Exception) { return 0; }
    }

    /// <summary>Deletes a folder. True if it's all gone now; otherwise the rest goes at the next start.</summary>
    public static bool DeleteFolder(string folder)
    {
        if (!Directory.Exists(folder)) return true;
        try
        {
            Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"Delete {folder}: {ex.Message}");
            // Delete what can be deleted now.
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList())
                    try { File.Delete(file); } catch (Exception) { }
            }
            catch (Exception) { }
            try
            {
                Directory.CreateDirectory(AppSettings.DefaultFolder);
                File.AppendAllLines(PendingFile, new[] { folder });
            }
            catch (Exception) { }
            return false;
        }
    }

    /// <summary>Finishes removals that were blocked last time (run at startup, before anything loads).</summary>
    public static void RunPending()
    {
        try
        {
            if (!File.Exists(PendingFile)) return;
            var left = new List<string>();
            foreach (var folder in File.ReadAllLines(PendingFile).Where(l => l.Length > 0).Distinct())
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
                catch (Exception) { left.Add(folder); }
            }
            if (left.Count == 0) File.Delete(PendingFile);
            else File.WriteAllLines(PendingFile, left);
        }
        catch (Exception ex) { Log.Write("Pending delete: " + ex.Message); }
    }
}
