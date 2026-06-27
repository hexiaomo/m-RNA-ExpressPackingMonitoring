using System;
using System.Threading.Tasks;
using System.Windows;

namespace ExpressPackingMonitoring
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            RegisterRuntimeExceptionLogging();
            RuntimeLog.Info("App", "Application startup");

            if (AudioProbe.TryHandleCommandLine(e.Args, out int exitCode))
            {
                RuntimeLog.Info("App", $"AudioProbe command handled, exitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static void RegisterRuntimeExceptionLogging()
        {
            Current.DispatcherUnhandledException += (_, e) =>
            {
                RuntimeLog.Error("Unhandled", "DispatcherUnhandledException", e.Exception);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                RuntimeLog.Error("Unhandled", $"AppDomain unhandled exception, terminating={e.IsTerminating}", e.ExceptionObject as Exception);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                RuntimeLog.Error("Unhandled", "UnobservedTaskException", e.Exception);
                e.SetObserved();
            };
        }
    }
}
