using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MouseIA.Core;
using MouseIA.Native;

namespace MouseIA;

internal static class Program
{
    private const string MutexName = @"Local\MouseIA.SingleInstance";
    private const string SignalName = @"Local\MouseIA.OpenSettings";

    [STAThread]
    private static int Main(string[] args)
    {
        // Restarted by the previous instance (e.g. to run as administrator): let it finish closing first.
        int afterIndex = Array.IndexOf(args, "--after");
        if (afterIndex >= 0 && afterIndex + 1 < args.Length && int.TryParse(args[afterIndex + 1], out int previous))
        {
            try { System.Diagnostics.Process.GetProcessById(previous).WaitForExit(8000); }
            catch (ArgumentException) { }
        }
        else if (!Elevation.IsSelfElevated)
        {
            // Set to run as administrator: start that way (Windows asks), or carry on normally if refused.
            try
            {
                var path = System.IO.Path.Combine(AppSettings.DefaultFolder, "settings.json");
                var protector = new DpapiProtector();
                var settings = AppSettings.Load(path, protector);
                if (settings.RunAsAdmin && !args.Contains("--no-admin"))
                {
                    if (Elevation.Relaunch("--after", Environment.ProcessId.ToString())) return 0;
                    // Refused: don't ask at every start. It can be turned on again in Settings > Activation.
                    settings.RunAsAdmin = false;
                    settings.Save(path, protector);
                }
            }
            catch (Exception ex) { Log.Write("Admin start: " + ex.Message); }
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool first);
        if (!first)
        {
            // Already running: ask that instance to show its settings.
            try
            {
                using var signal = EventWaitHandle.OpenExisting(SignalName);
                signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        using var openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        // WPF caps animations at 60 fps by default; run them at the monitor's own rate (120, 144, 240 Hz…).
        System.Windows.Media.Animation.Timeline.DesiredFrameRateProperty.OverrideMetadata(
            typeof(System.Windows.Media.Animation.Timeline),
            new FrameworkPropertyMetadata { DefaultValue = Win32.MaxRefreshRate() });
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("Unhandled: " + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Fatal: " + e.ExceptionObject);
        TaskSchedulerUnobserved();

        var controller = new AppController(app);
        bool fromStartup = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        app.Startup += (_, _) =>
        {
            controller.Start(fromStartup);
            var listener = new Thread(() =>
            {
                while (true)
                {
                    openSignal.WaitOne();
                    app.Dispatcher.BeginInvoke(() => controller.OpenSettings());
                }
            }) { IsBackground = true, Name = "MouseIA.Signal" };
            listener.Start();
        };
        app.Exit += (_, _) => controller.Dispose();
        return app.Run();
    }

    private static void TaskSchedulerUnobserved()
    {
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("Task: " + e.Exception);
            e.SetObserved();
        };
    }
}
