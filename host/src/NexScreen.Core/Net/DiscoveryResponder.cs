using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NexScreen.Core.Protocol;

namespace NexScreen.Core.Net;

/// <summary>Respuesta al descubrimiento: lo que la tablet muestra en la lista de PCs.</summary>
internal sealed record DiscoveryReply(int Protocol, string Name, int Port, string Version);

/// <summary>
/// Descubrimiento por WiFi: la tablet envía un broadcast UDP con <see cref="Request"/> al puerto
/// <see cref="Wire.DiscoveryPort"/> y cada host responde (unicast) con su nombre y su puerto TCP.
/// Ver docs/protocol.md.
/// </summary>
internal sealed class DiscoveryResponder
{
    public const string Request = "NEXSCREEN_DISCOVER 1";

    /// <summary>Evita que un ICMP "port unreachable" previo haga fallar ReceiveAsync en Windows.</summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly int _discoveryPort;
    private readonly int _streamPort;
    private readonly string _name;
    private readonly string _version;

    public DiscoveryResponder(int streamPort, string version, int discoveryPort = Wire.DiscoveryPort, string? name = null)
    {
        _streamPort = streamPort;
        _version = version;
        _discoveryPort = discoveryPort;
        _name = name ?? Environment.MachineName;
    }

    /// <summary>Puerto UDP en el que quedó escuchando (útil si se pidió el 0 en los tests).</summary>
    public int LocalPort { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsWindows()) udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
        LocalPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        Log.Info($"Descubrimiento WiFi activo (UDP {LocalPort})");

        var reply = BuildReply(_name, _streamPort, _version);
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                Log.Debug($"Descubrimiento: {ex.Message}");
                continue;
            }

            if (!IsRequest(received.Buffer)) continue;
            Log.Debug($"Descubrimiento: respondiendo a {received.RemoteEndPoint}");
            await udp.SendAsync(reply, received.RemoteEndPoint, ct).ConfigureAwait(false);
        }
    }

    internal static bool IsRequest(ReadOnlySpan<byte> datagram) =>
        Encoding.ASCII.GetString(datagram).Trim() == Request;

    internal static byte[] BuildReply(string name, int port, string version) =>
        JsonSerializer.SerializeToUtf8Bytes(new DiscoveryReply(Wire.Version, name, port, version), Wire.Json);
}
