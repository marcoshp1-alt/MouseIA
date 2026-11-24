using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MouseIA.Native;

namespace MouseIA;

internal static class Program
{
    private const string MutexName = @"Local\MouseIA.SingleInstance";
    private const string SignalName = @"Local\MouseIA.OpenSettings";

    [STAThread]
    private static int Main(string[] args)
    {
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
