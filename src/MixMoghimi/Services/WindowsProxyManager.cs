using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MixMoghimi.Services;

public static class WindowsProxyManager
{
    private const string InternetSettingsPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string BackupPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MIX-MOGHIMI", "proxy-backup.dat");

    private sealed record Snapshot(int? ProxyEnable, string? ProxyServer, string? ProxyOverride,
        string? AutoConfigUrl, int? AutoDetect, bool ProxyServerExists, bool ProxyOverrideExists,
        bool AutoConfigUrlExists, bool AutoDetectExists);

    public static void Enable(int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        if (!File.Exists(BackupPath)) SaveSnapshot();

        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
                ?? throw new InvalidOperationException("تنظیمات پراکسی ویندوز در دسترس نیست.");
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
            key.SetValue("ProxyOverride", "<local>;localhost;127.0.0.1;::1", RegistryValueKind.String);
            key.SetValue("AutoDetect", 0, RegistryValueKind.DWord);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            Refresh();
        }
        catch
        {
            Restore();
            throw;
        }
    }

    public static void RestoreAfterUnexpectedExit() => Restore();

    public static void Restore()
    {
        if (!File.Exists(BackupPath)) return;
        try
        {
            byte[] protectedBytes = File.ReadAllBytes(BackupPath);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            Snapshot snapshot = JsonSerializer.Deserialize<Snapshot>(plain)
                ?? throw new InvalidDataException("Proxy backup invalid.");
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: true)
                ?? throw new InvalidOperationException("تنظیمات پراکسی ویندوز در دسترس نیست.");
            RestoreInt(key, "ProxyEnable", snapshot.ProxyEnable);
            RestoreString(key, "ProxyServer", snapshot.ProxyServer, snapshot.ProxyServerExists);
            RestoreString(key, "ProxyOverride", snapshot.ProxyOverride, snapshot.ProxyOverrideExists);
            RestoreString(key, "AutoConfigURL", snapshot.AutoConfigUrl, snapshot.AutoConfigUrlExists);
            RestoreInt(key, "AutoDetect", snapshot.AutoDetect, snapshot.AutoDetectExists);
            Refresh();
            File.Delete(BackupPath);
        }
        catch (CryptographicException)
        {
            // A backup encrypted under another Windows account must never be applied.
            File.Delete(BackupPath);
        }
    }

    private static void SaveSnapshot()
    {
        using RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath, writable: false)
            ?? throw new InvalidOperationException("تنظیمات پراکسی ویندوز در دسترس نیست.");
        object? enable = key.GetValue("ProxyEnable", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        object? proxy = key.GetValue("ProxyServer", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        object? bypass = key.GetValue("ProxyOverride", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        object? pac = key.GetValue("AutoConfigURL", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        object? auto = key.GetValue("AutoDetect", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var snapshot = new Snapshot(
            enable is null ? null : Convert.ToInt32(enable),
            proxy as string, bypass as string, pac as string,
            auto is null ? null : Convert.ToInt32(auto),
            proxy is not null, bypass is not null, pac is not null, auto is not null);
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        string temp = BackupPath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, BackupPath, overwrite: true);
    }

    private static void RestoreInt(RegistryKey key, string name, int? value, bool existed = true)
    {
        if (!existed || value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value.Value, RegistryValueKind.DWord);
    }

    private static void RestoreString(RegistryKey key, string name, string? value, bool existed)
    {
        if (!existed || value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); // SETTINGS_CHANGED
        InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); // REFRESH
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}
