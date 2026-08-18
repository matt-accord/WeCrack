using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace WeCrack;

public partial class App : Application
{
    public App()
    {
        // Any unhandled exception on the UI thread — dump to %TEMP%\WeCrack-crash.log and show
        // a MessageBox before the process dies. Beats the silent-death-on-startup pattern.
        DispatcherUnhandledException += (_, e) =>
        {
            DumpCrash(e.Exception, "Dispatcher");
            MessageBox.Show(
                $"{e.Exception.GetType().Name}: {e.Exception.Message}\n\n" +
                $"Details written to %TEMP%\\WeCrack-crash.log",
                "WeCrack crashed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
            Shutdown(1);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) DumpCrash(ex, "AppDomain");
        };
    }

    private static void DumpCrash(Exception ex, string source)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "WeCrack-crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} [{source}] ===");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();
            File.AppendAllText(path, sb.ToString());
        }
        catch { /* nothing meaningful we can do here */ }
    }
}
