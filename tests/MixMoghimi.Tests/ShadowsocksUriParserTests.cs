using MixMoghimi.Services;
using System.Text;

namespace MixMoghimi.Tests;

public sealed class ShadowsocksUriParserTests
{
    [Fact]
    public void ParsesSip002Base64UserInfo()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:test-password"));
        var parsed = ShadowsocksUriParser.Parse($"ss://{encoded}@198.51.100.44:443#test-node");
        Assert.Equal("198.51.100.44", parsed.Server);
        Assert.Equal(443, parsed.ServerPort);
        Assert.Equal("aes-256-gcm", parsed.Method);
        Assert.Equal("test-password", parsed.Password);
        Assert.Equal("test-node", parsed.DisplayName);
    }

    [Fact]
    public void ParsesLegacyFullyEncodedUri()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("chacha20-ietf-poly1305:sample@proxy.example:8388"));
        var parsed = ShadowsocksUriParser.Parse("ss://" + encoded);
        Assert.Equal("proxy.example", parsed.Server);
        Assert.Equal(8388, parsed.ServerPort);
        Assert.Equal("chacha20-ietf-poly1305", parsed.Method);
    }

    [Fact]
    public void PreservesPercentCharactersInBase64Password()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pa%ssword"));
        var parsed = ShadowsocksUriParser.Parse($"ss://{encoded}@198.51.100.44:443");
        Assert.Equal("pa%ssword", parsed.Password);
    }

    [Fact]
    public void RejectsInvalidPort()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:test-password"));
        Assert.Throws<FormatException>(() => ShadowsocksUriParser.Parse($"ss://{encoded}@198.51.100.44:70000"));
    }

    [Fact]
    public void RejectsPluginLinksUntilPluginSupportIsImplemented()
    {
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:test-password"));
        Assert.Throws<FormatException>(() => ShadowsocksUriParser.Parse($"ss://{encoded}@proxy.example:443?plugin=v2ray-plugin"));
    }
}
