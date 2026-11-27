using MixMoghimi.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MixMoghimi.Services;

public sealed record RuntimePorts(int MixedProxyPort, int ClashApiPort, string ClashSecret);

public static class SingBoxConfigFactory
{
    public static string Build(ShadowsocksServer server, RuntimePorts ports)
    {
        if (ports.MixedProxyPort is < 1024 or > 65535 || ports.ClashApiPort is < 1024 or > 65535 ||
            ports.MixedProxyPort == ports.ClashApiPort)
            throw new ArgumentOutOfRangeException(nameof(ports));

        var config = new
        {
            log = new { level = "warn", timestamp = true },
            dns = new
            {
                servers = new[]
                {
                    new
                    {
                        type = "https",
                        tag = "remote-doh",
                        server = "1.1.1.1",
                        server_port = 443,
                        path = "/dns-query",
                        tls = new { enabled = true, server_name = "cloudflare-dns.com" },
                        detour = "proxy"
                    }
                },
                final = "remote-doh",
                strategy = "prefer_ipv4"
            },
            inbounds = new[]
            {
                new
                {
                    type = "mixed",
                    tag = "local-mixed",
                    listen = "127.0.0.1",
                    listen_port = ports.MixedProxyPort,
                }
            },
            outbounds = new object[]
            {
                new
                {
                    type = "shadowsocks",
                    tag = "proxy",
                    server = server.Server,
                    server_port = server.ServerPort,
                    method = server.Method,
                    password = server.Password
                },
                new { type = "direct", tag = "direct" }
            },
            route = new
            {
                final = "proxy",
                auto_detect_interface = true
            },
            experimental = new
            {
                clash_api = new
                {
                    external_controller = $"127.0.0.1:{ports.ClashApiPort}",
                    secret = ports.ClashSecret,
                    default_mode = "rule"
                }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        });
    }
}
