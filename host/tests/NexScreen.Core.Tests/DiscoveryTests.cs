using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NexScreen.Core.Net;
using NexScreen.Core.Protocol;

namespace NexScreen.Core.Tests;

public class DiscoveryTests
{
    [Theory]
    [InlineData("NEXSCREEN_DISCOVER 1", true)]
    [InlineData("NEXSCREEN_DISCOVER 1\n", true)]
    [InlineData("NEXSCREEN_DISCOVER 2", false)]
    [InlineData("hola", false)]
    public void RecognizesRequest(string text, bool expected)
    {
        Assert.Equal(expected, DiscoveryResponder.IsRequest(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void ReplyIsCamelCaseJson()
    {
        var json = Encoding.UTF8.GetString(DiscoveryResponder.BuildReply("ALEX-LAPTOP", 27190, "0.4.0"));

        Assert.Equal("""{"protocol":1,"name":"ALEX-LAPTOP","port":27190,"version":"0.4.0"}""", json);
    }

    [Fact]
    public async Task RespondsOverLoopback()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var responder = new DiscoveryResponder(27190, "0.4.0", discoveryPort: 0, name: "PRUEBA");
        var run = responder.RunAsync(cts.Token);
        while (responder.LocalPort == 0) await Task.Delay(10, cts.Token);

        using var client = new UdpClient(AddressFamily.InterNetwork);
        await client.SendAsync(Encoding.ASCII.GetBytes(DiscoveryResponder.Request), new IPEndPoint(IPAddress.Loopback, responder.LocalPort), cts.Token);
        var received = await client.ReceiveAsync(cts.Token);

        var reply = JsonSerializer.Deserialize<DiscoveryReply>(received.Buffer, Wire.Json)!;
        Assert.Equal("PRUEBA", reply.Name);
        Assert.Equal(27190, reply.Port);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }
}
