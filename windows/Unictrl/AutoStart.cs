using Microsoft.Win32;
namespace Unictrl;

/// Per-user startup works for installed and portable copies without administrator access.
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Command => "\"" + (Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Unictrl.exe")) + "\" --background";
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue("Unictrl") as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static bool Enable() => Change(true);
    public static bool Disable() => Change(false);
    private static bool Change(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue("Unictrl", Command, RegistryValueKind.String); else key.DeleteValue("Unictrl", false);
            return IsEnabled() == enabled;
        }
        catch (Exception error) { Log.Write("autostart_failed", error.GetType().Name); return false; }
    }
}
