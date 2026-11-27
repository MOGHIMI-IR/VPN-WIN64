using MixMoghimi.Models;
using MixMoghimi.Services;
using System.Text.Json;

namespace MixMoghimi.Tests;

public sealed class SingBoxConfigFactoryTests
{
    [Fact]
    public void BuildsLoopbackMixedInboundAndProxyDefaultRoute()
    {
        var server = new ShadowsocksServer("198.51.100.44", 443, "aes-256-gcm", "fake-test-password", "test");
        string json = SingBoxConfigFactory.Build(server, new RuntimePorts(2080, 30999, "f".PadLeft(64, 'a')));
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert.Equal("mixed", root.GetProperty("inbounds")[0].GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", root.GetProperty("inbounds")[0].GetProperty("listen").GetString());
        Assert.Equal(2080, root.GetProperty("inbounds")[0].GetProperty("listen_port").GetInt32());
        Assert.Equal("shadowsocks", root.GetProperty("outbounds")[0].GetProperty("type").GetString());
        Assert.Equal("proxy", root.GetProperty("route").GetProperty("final").GetString());
        Assert.Equal("127.0.0.1:30999", root.GetProperty("experimental").GetProperty("clash_api").GetProperty("external_controller").GetString());
    }

    [Theory]
    [InlineData("{\"up\":123,\"down\":456}", 123L, 456L)]
    [InlineData("data: {\"up\":7,\"down\":9}", 7L, 9L)]
    public void ParsesSingBoxTrafficSamples(string sample, long expectedUp, long expectedDown)
    {
        Assert.True(SingBoxService.TryParseTrafficSample(sample, out long up, out long down));
        Assert.Equal(expectedUp, up);
        Assert.Equal(expectedDown, down);
    }

    [Fact]
    public void IgnoresMalformedTrafficSamples()
    {
        Assert.False(SingBoxService.TryParseTrafficSample("event: traffic", out long up, out long down));
        Assert.Equal(0, up);
        Assert.Equal(0, down);
    }
}
