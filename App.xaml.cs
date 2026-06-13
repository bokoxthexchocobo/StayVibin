using System.IO;
using System.Windows;
using System.Windows.Threading;
using StayVibin.Services;

namespace StayVibin;

/// <summary>
/// Application entry point. Installs global exception handlers that log crashes to
/// disk (and show a dialog for UI-thread faults) so failures are never silent.
/// </summary>
public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash(e.ExceptionObject as Exception, "AppDomain");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception, "Dispatcher");
        MessageBox.Show(
            e.Exception.Message + "\n\nLogged to:\n" + AppPaths.CrashLogPath,
            "StayVibin - unexpected error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrash(Exception? ex, string source)
    {
        if (ex is null) return;
        try
        {
            File.AppendAllText(AppPaths.CrashLogPath,
                $"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ==={Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* nothing more we can do */ }
    }
}
