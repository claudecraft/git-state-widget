using Microsoft.Win32;

namespace GitStateWidget;

/// <summary>
/// Start-with-Windows via the per-user Run key. No elevation, no scheduled task, no launcher
/// script: the exe is a GUI app, so there is no console to hide in the first place.
/// </summary>
public static class Startup {
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GitStateWidget";

    public static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsEnabled() {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        var v = key?.GetValue(ValueName) as string;
        return !string.IsNullOrEmpty(v);
    }

    public static void Set(bool enabled) {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key == null) {
            return;
        }
        if (enabled) {
            key.SetValue(ValueName, "\"" + ExePath + "\"");
        } else {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
