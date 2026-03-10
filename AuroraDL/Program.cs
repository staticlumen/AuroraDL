using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace auroradl;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogCrash("UI thread exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogCrash("Unhandled exception", ex);
        };

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }

    private static void LogCrash(string title, Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "auroradl");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "widget-crash.log");
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine(new string('-', 60));
            File.AppendAllText(file, sb.ToString());
        }
        catch
        {
            // Ignore logging failures.
        }
    }
}

