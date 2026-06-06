using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Launcher
{
    private const uint MessageBoxIconError = 0x00000010;

    [STAThread]
    private static int Main(string[] args)
    {
        var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var appDirectory = Path.Combine(baseDirectory, "app");
        var appExe = Path.Combine(appDirectory, "Switch2ProWirelessViiper.exe");

        if (!File.Exists(appExe))
        {
            ShowError("The app runtime folder is missing. Expected:\r\n\r\n" + appExe);
            return 2;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appExe,
                Arguments = JoinArguments(args),
                WorkingDirectory = appDirectory,
                UseShellExecute = false,
            };

            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            ShowError("Failed to start Switch2ProWirelessViiper.\r\n\r\n" + ex.Message);
            return 1;
        }
    }

    private static string JoinArguments(string[] args)
    {
        if (args.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < args.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(args[i]));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '"' }) >= 0;
        if (!needsQuotes)
        {
            return value;
        }

        var builder = new StringBuilder();
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(ch);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    private static void ShowError(string message)
    {
        MessageBox(IntPtr.Zero, message, "Switch2ProWirelessViiper", MessageBoxIconError);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
