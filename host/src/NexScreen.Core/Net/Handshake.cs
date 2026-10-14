using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using NexScreen.Core.Protocol;
using NexScreen.Core.Security;

namespace NexScreen.Core.Net;

/// <summary>Cliente que pasó el handshake: canal listo para la sesión y su Hello.</summary>
internal sealed record AuthenticatedClient(MessageChannel Channel, HelloMessage Hello, bool Usb);

/// <summary>
/// Primera parte de una conexión, antes de tocar la sesión activa. Por USB (adb reverse llega
/// desde 127.0.0.1) el canal va sin cifrar. Por WiFi va con TLS y la tablet tiene que estar
/// emparejada: si no lo está, se le pide el PIN que muestra el PC. Ver docs/protocol.md.
/// </summary>
internal sealed class Handshake
{
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    private readonly X509Certificate2? _certificate;
    private readonly PairedDevices _paired;
    private readonly IStreamObserver _observer;
    private readonly PairingGate _gate;
    private readonly Func<IPAddress, bool> _isUsb;

    /// <param name="isUsb">Qué conexiones van sin cifrar. Por defecto, las de loopback (adb reverse).</param>
    public Handshake(X509Certificate2? certificate, PairedDevices paired, IStreamObserver observer, PairingGate gate,
        Func<IPAddress, bool>? isUsb = null)
    {
        _certificate = certificate;
        _paired = paired;
        _observer = observer;
        _gate = gate;
        _isUsb = isUsb ?? IPAddress.IsLoopback;
    }

    public async Task<AuthenticatedClient> RunAsync(TcpClient client, CancellationToken ct)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        if (_isUsb(remote.Address))
        {
            var plain = new MessageChannel(client.GetStream());
            try
            {
                return new AuthenticatedClient(plain, await ReadHelloAsync(plain, ct).ConfigureAwait(false), Usb: true);
            }
            catch
            {
                await plain.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        if (_certificate is null) throw new InvalidOperationException("No hay certificado TLS: solo se aceptan conexiones por USB");

        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        MessageChannel? channel = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TlsTimeout);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = true,
                    // Los certificados son autofirmados: no hay cadena que validar. La confianza
                    // sale del emparejamiento (huella guardada en PairedDevices).
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token).ConfigureAwait(false);
            }

            var clientFingerprint = Fingerprint.Of(
                ssl.RemoteCertificate ?? throw new InvalidDataException("La tablet no presentó su certificado"));
            channel = new MessageChannel(ssl);
            var hello = await ReadHelloAsync(channel, ct).ConfigureAwait(false);

            // Hace falta que confíen los dos: si la tablet no reconoce este PC (por ejemplo, se
            // regeneró el certificado) tampoco alcanza con que el PC la conozca a ella.
            if (!hello.Paired || !_paired.Contains(clientFingerprint))
            {
                await PairAsync(channel, hello, Fingerprint.Of(_certificate), clientFingerprint, ct).ConfigureAwait(false);
            }

            return new AuthenticatedClient(channel, hello, Usb: false);
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            else await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task PairAsync(MessageChannel channel, HelloMessage hello, string hostFingerprint, string clientFingerprint, CancellationToken ct)
    {
        if (!_gate.TryEnter())
        {
            await channel.WriteJsonAsync(MessageType.PairResult,
                new PairResultMessage(false, "El PC está emparejando otra tablet. Inténtalo en un momento."), ct).ConfigureAwait(false);
            throw new InvalidOperationException($"{hello.Device} quiso emparejarse mientras había otro emparejamiento en curso");
        }

        var paired = false;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cancel.CancelAfter(Pairing.PinLifetime);
        try
        {
            var pin = Pairing.NewPin();
            Log.Info($"{hello.Device} quiere conectarse por WiFi y no está emparejada: esperando el PIN");
            _observer.OnPairingStarted(hello.Device, pin, cancel);
            await channel.WriteJsonAsync(MessageType.PairRequired, new PairRequiredMessage(Environment.MachineName), ct)
                .ConfigureAwait(false);

            for (var attempt = 1; ; attempt++)
            {
                var proof = await ReadPairAsync(channel, cancel.Token).ConfigureAwait(false);
                if (Pairing.VerifyProof(pin, hostFingerprint, clientFingerprint, proof))
                {
                    _paired.Add(hello.Device, clientFingerprint);
                    await channel.WriteJsonAsync(MessageType.PairResult, new PairResultMessage(true), ct).ConfigureAwait(false);
                    paired = true;
                    Log.Info($"{hello.Device} quedó emparejada");
                    return;
                }

                var retry = attempt < Pairing.MaxAttempts;
                Log.Warn($"{hello.Device}: PIN incorrecto ({attempt}/{Pairing.MaxAttempts})");
                await channel.WriteJsonAsync(MessageType.PairResult,
                    new PairResultMessage(false, retry ? "Código incorrecto." : "Código incorrecto. Se agotaron los intentos.", retry),
                    ct).ConfigureAwait(false);
                if (!retry) throw new InvalidDataException("Se agotaron los intentos de emparejamiento");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Venció el PIN o el usuario tocó "Cancelar" en el PC.
            try
            {
                await channel.WriteJsonAsync(MessageType.PairResult,
                    new PairResultMessage(false, "Se canceló el emparejamiento en el PC o venció el código."), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }

            throw new TimeoutException("El emparejamiento se canceló o venció el código");
        }
        finally
        {
            _observer.OnPairingFinished(hello.Device, paired);
            _gate.Exit();
        }
    }

    /// <summary>Espera el Pair de la tablet (mientras tanto responde los PING).</summary>
    private static async Task<string?> ReadPairAsync(MessageChannel channel, CancellationToken ct)
    {
        while (true)
        {
            var message = await channel.ReadAsync(ct).ConfigureAwait(false)
                ?? throw new IOException("La tablet cerró la conexión durante el emparejamiento");
            switch (message.Type)
            {
                case MessageType.Pair:
                    return JsonSerializer.Deserialize<PairMessage>(message.Payload, Wire.Json)?.Proof;
                case MessageType.Ping:
                    await channel.WriteAsync(MessageType.Pong, message.Payload, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task<HelloMessage> ReadHelloAsync(MessageChannel channel, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HelloTimeout);

        var message = await channel.ReadAsync(timeout.Token).ConfigureAwait(false)
            ?? throw new IOException("El cliente cerró la conexión antes del Hello");
        if (message.Type != MessageType.Hello) throw new InvalidDataException($"Se esperaba Hello y llegó {message.Type}");

        var hello = JsonSerializer.Deserialize<HelloMessage>(message.Payload, Wire.Json)
            ?? throw new InvalidDataException("Hello vacío");
        if (hello.Protocol != Wire.Version)
        {
            throw new InvalidDataException($"Versión de protocolo incompatible: cliente {hello.Protocol}, host {Wire.Version}");
        }
        if (hello.Width <= 0 || hello.Height <= 0) throw new InvalidDataException("Resolución de cliente inválida");

        return hello;
    }
}

/// <summary>Permite un solo emparejamiento a la vez (el PC muestra un solo PIN).</summary>
internal sealed class PairingGate
{
    private int _busy;

    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _busy, 0);
}
