using System.IO;
using System.Text.Json;
using Switch2ProWirelessViiper.Core;

namespace Switch2ProWirelessViiper;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Language { get; set; } = "en-US";
    public bool FirstRunCompleted { get; set; }
    public string BluetoothAddress { get; set; } = string.Empty;
    public string ViiperAddress { get; set; } = "localhost:3242";
    public string ViiperExePath { get; set; } = string.Empty;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartToTray { get; set; }
    public bool PreloadViiper { get; set; } = true;
    public StickCalibrationProfile? StickCalibration { get; set; }
    public double AutoDisconnectMinutes { get; set; } = 30;
    public int WindowWidth { get; set; } = 980;
    public int WindowHeight { get; set; } = 680;

    public static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Switch2ProWirelessViiper",
            "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
            return loaded ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
