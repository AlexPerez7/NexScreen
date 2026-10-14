using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NexScreen.Core.Net;
using NexScreen.Core.Protocol;
using NexScreen.Core.Security;

namespace NexScreen.Core.Tests;

public class PairingTests
{
    private const string HostFp = "aa11";
    private const string ClientFp = "bb22";

    [Fact]
    public void ProofMatchesKnownVector()
    {
        // El mismo vector está en el cliente Android (PairingTest.kt) y en fake_client.py.
        Assert.Equal("d0de3459577ccda1a0a23d616cc7427506893c97c5f4f8e38074832597cbe4af",
            Pairing.ComputeProof("123456", HostFp, ClientFp));
    }

    [Fact]
    public void VerifiesOnlyTheRightPinAndPeers()
    {
        var proof = Pairing.ComputeProof("123456", HostFp, ClientFp);

        Assert.True(Pairing.VerifyProof("123456", HostFp, ClientFp, proof));
        Assert.True(Pairing.VerifyProof("123456", HostFp, ClientFp, proof.ToUpperInvariant()));
        Assert.False(Pairing.VerifyProof("123457", HostFp, ClientFp, proof));
        Assert.False(Pairing.VerifyProof("123456", "otro", ClientFp, proof));
        Assert.False(Pairing.VerifyProof("123456", HostFp, "otro", proof));
        Assert.False(Pairing.VerifyProof("123456", HostFp, ClientFp, null));
        Assert.False(Pairing.VerifyProof("123456", HostFp, ClientFp, "abc"));
    }

    [Fact]
    public void PinsHaveSixDigits()
    {
        for (var i = 0; i < 100; i++)
        {
            var pin = Pairing.NewPin();
            Assert.Equal(6, pin.Length);
            Assert.True(pin.All(char.IsAsciiDigit));
        }

        Assert.Equal("012 345", Pairing.Format("012345"));
    }

    [Fact]
    public void PairedDevicesArePersisted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexscreen-paired-{Guid.NewGuid():N}.json");
        try
        {
            var devices = new PairedDevices(path);
            devices.Add("Tablet A", "fp-a");
            devices.Add("Tablet B", "fp-b");
            devices.Add("Tablet A (renombrada)", "fp-a");
            devices.Remove("fp-b");

            var reloaded = new PairedDevices(path);
            Assert.True(reloaded.Contains("fp-a"));
            Assert.False(reloaded.Contains("fp-b"));
            Assert.Equal("Tablet A (renombrada)", Assert.Single(reloaded.All).Name);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CorruptFileMeansNoPairedDevices()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexscreen-paired-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ no es json");
        try
        {
            Assert.Empty(new PairedDevices(path).All);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- Handshake por TLS (loopback tratado como WiFi) ----

    [Fact]
    public async Task UnpairedTabletPairsWithPinAndThenConnectsDirectly()
    {
        using var hostCert = CreateCertificate("CN=host de prueba");
        using var clientCert = CreateCertificate("CN=tablet de prueba");
        var path = Path.Combine(Path.GetTempPath(), $"nexscreen-paired-{Guid.NewGuid():N}.json");
        var observer = new PinObserver();
        var paired = new PairedDevices(path);
        var handshake = new Handshake(hostCert, paired, observer, new PairingGate(), isUsb: _ => false);
        try
        {
            // Primera vez: el host pide el PIN; un PIN equivocado y después el correcto.
            await using (var tablet = await ConnectAsync(handshake, clientCert, paired: false))
            {
                var required = await tablet.ReadAsync();
                Assert.Equal(MessageType.PairRequired, required.Type);

                await tablet.SendPairAsync("000000" == observer.Pin ? "111111" : "000000");
                var wrong = Json<PairResultMessage>(await tablet.ReadAsync());
                Assert.False(wrong.Ok);
                Assert.True(wrong.Retry);

                await tablet.SendPairAsync(observer.Pin!);
                Assert.True(Json<PairResultMessage>(await tablet.ReadAsync()).Ok);

                var authenticated = await tablet.Server;
                Assert.False(authenticated.Usb);
                Assert.Equal("Tablet de prueba", authenticated.Hello.Device);
                await authenticated.Channel.DisposeAsync();
            }

            Assert.True(paired.Contains(Fingerprint.Of(clientCert)));
            Assert.Equal((true, true), (observer.Started, observer.Paired));

            // Segunda vez: emparejada de los dos lados, sin PIN.
            observer.Started = false;
            await using (var tablet = await ConnectAsync(handshake, clientCert, paired: true))
            {
                var authenticated = await tablet.Server;
                Assert.False(observer.Started);
                await authenticated.Channel.DisposeAsync();
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TooManyWrongPinsRejectTheConnection()
    {
        using var hostCert = CreateCertificate("CN=host de prueba");
        using var clientCert = CreateCertificate("CN=tablet de prueba");
        var path = Path.Combine(Path.GetTempPath(), $"nexscreen-paired-{Guid.NewGuid():N}.json");
        var observer = new PinObserver();
        var paired = new PairedDevices(path);
        var handshake = new Handshake(hostCert, paired, observer, new PairingGate(), isUsb: _ => false);
        try
        {
            await using var tablet = await ConnectAsync(handshake, clientCert, paired: true);
            Assert.Equal(MessageType.PairRequired, (await tablet.ReadAsync()).Type);

            for (var i = 1; i <= Pairing.MaxAttempts; i++)
            {
                await tablet.SendPairAsync(observer.Pin == "999999" ? "888888" : "999999");
                var result = Json<PairResultMessage>(await tablet.ReadAsync());
                Assert.False(result.Ok);
                Assert.Equal(i < Pairing.MaxAttempts, result.Retry);
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => tablet.Server);
            Assert.False(paired.Contains(Fingerprint.Of(clientCert)));
            Assert.Equal((true, false), (observer.Started, observer.Paired));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ClientWithoutCertificateIsRejected()
    {
        using var hostCert = CreateCertificate("CN=host de prueba");
        var handshake = new Handshake(hostCert, new PairedDevices(Path.GetTempFileName()), new PinObserver(), new PairingGate(),
            isUsb: _ => false);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var accepted = await listener.AcceptTcpClientAsync();
        var server = handshake.RunAsync(accepted, CancellationToken.None);

        await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        var client = ssl.AuthenticateAsClientAsync("nexscreen");
        await Assert.ThrowsAnyAsync<Exception>(() => server);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await client;
            // Con TLS 1.3 el rechazo puede llegar recién al leer.
            await ssl.ReadAsync(new byte[1]);
            throw new IOException("cerrado");
        });
    }

    private static T Json<T>((MessageType Type, byte[] Payload) message) =>
        JsonSerializer.Deserialize<T>(message.Payload, Wire.Json)!;

    private static async Task<FakeTablet> ConnectAsync(Handshake handshake, X509Certificate2 clientCert, bool paired)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await listener.AcceptTcpClientAsync();
        listener.Stop();

        var server = handshake.RunAsync(accepted, CancellationToken.None);
        X509Certificate? hostCert = null;
        var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        {
            hostCert = certificate;
            return true;
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "nexscreen",
            ClientCertificates = [clientCert],
        });

        var channel = new MessageChannel(ssl);
        await channel.WriteJsonAsync(MessageType.Hello,
            new HelloMessage(Wire.Version, "Tablet de prueba", 2304, 1440, 280, paired), CancellationToken.None);
        return new FakeTablet(channel, server, Fingerprint.Of(hostCert!), Fingerprint.Of(clientCert), [tcp, accepted]);
    }

    /// <summary>
    /// Schannel no puede usar claves efímeras: igual que HostIdentity, se reimporta como PFX
    /// (en el contenedor de claves del usuario; se borra al liberar el certificado).
    /// </summary>
    private static X509Certificate2 CreateCertificate(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pkcs12), (string?)null, X509KeyStorageFlags.UserKeySet);
    }

    private sealed class FakeTablet(MessageChannel channel, Task<AuthenticatedClient> server, string hostFp, string clientFp,
        IDisposable[] resources) : IAsyncDisposable
    {
        public Task<AuthenticatedClient> Server { get; } = server;

        public async Task<(MessageType Type, byte[] Payload)> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await channel.ReadAsync(timeout.Token) ?? throw new EndOfStreamException();
        }

        public Task SendPairAsync(string pin) => channel.WriteJsonAsync(MessageType.Pair,
            new PairMessage(Pairing.ComputeProof(pin, hostFp, clientFp)), CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await channel.DisposeAsync();
            foreach (var resource in resources) resource.Dispose();
        }
    }

    private sealed class PinObserver : IStreamObserver
    {
        public volatile string? Pin;
        public bool Started;
        public bool Paired;

        public void OnClientConnected(string device, int width, int height)
        {
        }

        public void OnStats(double fps, double mbps)
        {
        }

        public void OnClientDisconnected(string? reason)
        {
        }

        public void OnPairingStarted(string device, string pin, CancellationTokenSource cancel)
        {
            Pin = pin;
            Started = true;
        }

        public void OnPairingFinished(string device, bool paired) => Paired = paired;
    }
}
