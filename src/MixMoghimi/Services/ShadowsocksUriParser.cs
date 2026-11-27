using MixMoghimi.Models;
using System.Net;
using System.Text;

namespace MixMoghimi.Services;

public static class ShadowsocksUriParser
{
    private static readonly HashSet<string> SupportedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "aes-128-gcm", "aes-192-gcm", "aes-256-gcm",
        "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305", "none",
        // Legacy ciphers retained for compatibility with existing Shadowsocks servers.
        "aes-128-ctr", "aes-192-ctr", "aes-256-ctr",
        "aes-128-cfb", "aes-192-cfb", "aes-256-cfb",
        "rc4-md5", "chacha20-ietf", "xchacha20"
    };

    public static ShadowsocksServer Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("کانفیگ خالی است.");

        string line = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .FirstOrDefault(x => x.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException("در این نسخه فقط کانفیگ Shadowsocks با لینک ss:// پشتیبانی می‌شود.");

        string body = line[5..];
        int hashIndex = body.IndexOf('#');
        string displayName = hashIndex >= 0 ? Uri.UnescapeDataString(body[(hashIndex + 1)..]) : "Shadowsocks";
        if (hashIndex >= 0) body = body[..hashIndex];
        int queryIndex = body.IndexOf('?');
        string query = queryIndex >= 0 ? body[(queryIndex + 1)..] : string.Empty;
        if (queryIndex >= 0) body = body[..queryIndex];
        if (!string.IsNullOrWhiteSpace(query) && query.Contains("plugin=", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("این سرور به افزونهٔ SIP003 نیاز دارد که هنوز در MIX فعال نشده است.");

        string decoded;
        int at = body.LastIndexOf('@');
        if (at >= 0)
        {
            string userInfo = body[..at];
            string hostPart = body[(at + 1)..];
            decoded = DecodeUserInfo(userInfo) + "@" + hostPart;
        }
        else
        {
            decoded = DecodeBase64(body);
        }

        int separator = decoded.LastIndexOf('@');
        if (separator <= 0 || separator == decoded.Length - 1)
            throw new FormatException("ساختار لینک Shadowsocks معتبر نیست.");

        string credentials = decoded[..separator];
        string hostPort = decoded[(separator + 1)..];
        int colon = credentials.IndexOf(':');
        if (colon <= 0 || colon == credentials.Length - 1)
            throw new FormatException("روش رمزنگاری یا رمز کانفیگ ناقص است.");

        // DecodeUserInfo has already URL-unescaped plain SIP002 userinfo; base64-decoded
        // credentials must remain byte-for-byte intact (a literal '%' is not an escape).
        string method = credentials[..colon].Trim().ToLowerInvariant();
        string password = credentials[(colon + 1)..];
        if (!SupportedMethods.Contains(method))
            throw new FormatException("روش رمزنگاری این سرور توسط موتور فعلی پشتیبانی نمی‌شود.");
        if (string.IsNullOrWhiteSpace(password))
            throw new FormatException("رمز کانفیگ خالی است.");

        string host;
        string portText;
        if (hostPort.StartsWith('['))
        {
            int close = hostPort.IndexOf(']');
            if (close < 2 || close + 2 > hostPort.Length || hostPort[close + 1] != ':')
                throw new FormatException("آدرس IPv6 کانفیگ معتبر نیست.");
            host = hostPort[1..close];
            portText = hostPort[(close + 2)..];
        }
        else
        {
            int lastColon = hostPort.LastIndexOf(':');
            if (lastColon <= 0) throw new FormatException("پورت سرور در کانفیگ وجود ندارد.");
            host = hostPort[..lastColon];
            portText = hostPort[(lastColon + 1)..];
        }

        if (string.IsNullOrWhiteSpace(host) || !int.TryParse(portText, out int port) || port is < 1 or > 65535)
            throw new FormatException("آدرس یا پورت سرور معتبر نیست.");

        return new ShadowsocksServer(host, port, method, password,
            string.IsNullOrWhiteSpace(displayName) ? "Shadowsocks" : displayName);
    }

    private static string DecodeUserInfo(string value)
    {
        string raw = Uri.UnescapeDataString(value);
        if (raw.Contains(':')) return raw;
        return DecodeBase64(raw);
    }

    private static string DecodeBase64(string value)
    {
        string normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(normalized)); }
        catch (FormatException ex) { throw new FormatException("بخش رمزگذاری‌شدهٔ کانفیگ قابل خواندن نیست.", ex); }
    }
}
