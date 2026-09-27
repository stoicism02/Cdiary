using Microsoft.Win32;

namespace Cdiary;

public static class StartupSettings
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Cdiary";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
    }

    public static string BuildCommand(string executablePath) => $"\"{executablePath}\" --background";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            var path = Environment.ProcessPath ?? throw new InvalidOperationException("无法找到程序路径。");
            key.SetValue(ValueName, BuildCommand(path), RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, false);
    }

    public static void RefreshPathIfEnabled()
    {
        if (IsEnabled) SetEnabled(true);
    }
}
