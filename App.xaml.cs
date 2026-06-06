using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace Switch2ProWirelessViiper;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\Switch2ProWirelessViiper";

    private Window? _window;
    private Mutex? _singleInstanceMutex;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var ownsSingleInstance);
        if (!ownsSingleInstance)
        {
            var settings = AppSettings.Load();
            var isChinese = settings.Language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
                            settings.Language.Equals("zh", StringComparison.OrdinalIgnoreCase);
            var isJapanese = settings.Language.Equals("ja-JP", StringComparison.OrdinalIgnoreCase) ||
                             settings.Language.Equals("ja", StringComparison.OrdinalIgnoreCase);
            MessageBox(
                IntPtr.Zero,
                isJapanese
                    ? "Switch 2 Pro Wireless VIIPER は既に実行中です。タスクバーまたはシステムトレイから既存のウィンドウを開いてください。"
                    : isChinese
                        ? "Switch 2 Pro Wireless VIIPER 已经在运行。请从任务栏或系统托盘打开现有窗口。"
                        : "Switch 2 Pro Wireless VIIPER is already running. Open the existing window from the taskbar or system tray.",
                "Switch 2 Pro Wireless VIIPER",
                0x00000030);
            Exit();
            return;
        }

        _window = new MainWindow();
        var mainWindow = (MainWindow)_window;

        if (!mainWindow.ShouldStartToTray)
        {
            _window.Activate();
        }
        else
        {
            // Skip Activate() to start completely hidden without flashing.
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        var details = e.Exception.ToString();
        TryWriteCrashLog(details);
        MessageBox(
            IntPtr.Zero,
            details,
            "Switch 2 Pro Wireless VIIPER",
            0x00000010);
        e.Handled = true;
    }

    private static void TryWriteCrashLog(string details)
    {
        try
        {
            var directory = Path.GetDirectoryName(AppSettings.SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "crash.log"), details);
            }
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}


