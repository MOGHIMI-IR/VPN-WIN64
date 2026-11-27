using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MixMoghimi.Services;

public sealed class DeviceIdentityService
{
    private const string AppFolder = "MIX-MOGHIMI";
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);
    private readonly string _tokenFile;

    public DeviceIdentityService()
    {
        Directory.CreateDirectory(_root);
        _tokenFile = Path.Combine(_root, "device.dat");
    }

    public string RootDirectory => _root;

    public string GetInstallationHash()
    {
        string machineGuid = ReadMachineGuid();
        string cpuId = ReadRegistryValue(Registry.LocalMachine,
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "Identifier");
        string systemVolume = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
        string material = $"MIX-MOGHIMI|v1|{machineGuid}|{cpuId}|{systemVolume.ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    public string? LoadDeviceToken()
    {
        try
        {
            if (!File.Exists(_tokenFile)) return null;
            byte[] protectedBytes = File.ReadAllBytes(_tokenFile);
            byte[] plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            string token = Encoding.UTF8.GetString(plain);
            return token.Length == 64 && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : null;
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
    }

    public void SaveDeviceToken(string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid device token.", nameof(token));
        byte[] plain = Encoding.UTF8.GetBytes(token.ToLowerInvariant());
        byte[] protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        string temp = _tokenFile + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, _tokenFile, overwrite: true);
    }

    private static string ReadMachineGuid()
    {
        string value = ReadRegistryValue(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Cryptography", "MachineGuid");
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("شناسهٔ امن این دستگاه قابل خواندن نیست.");
        return value.Trim();
    }

    private static string ReadRegistryValue(RegistryKey root, string path, string name)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path, writable: false);
            return Convert.ToString(key?.GetValue(name)) ?? string.Empty;
        }
        catch (UnauthorizedAccessException) { return string.Empty; }
        catch (System.Security.SecurityException) { return string.Empty; }
    }
}
