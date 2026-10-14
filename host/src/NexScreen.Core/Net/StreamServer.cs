using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using NexScreen.Core.Security;

namespace NexScreen.Core.Net;

/// <summary>
/// Acepta conexiones TCP. Hay una sola sesión activa: un cliente nuevo reemplaza al anterior
/// (por ejemplo, al pasar de WiFi a USB), pero solo después de pasar el handshake. Así una
/// conexión no emparejada desde la red no puede cortar la sesión de la tablet.
/// </summary>
internal sealed class StreamServer
{
    /// <summary>Handshakes simultáneos (TLS, Hello o emparejamiento): limita conexiones basura.</summary>
    private const int MaxPendingHandshakes = 4;

    private readonly HostOptions _options;
    private readonly IStreamObserver _observer;
    private readonly PairedDevices _paired;
    private readonly PairingGate _pairingGate = new();
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _pendingHandshakes = new(MaxPendingHandshakes, MaxPendingHandshakes);

    private CancellationTokenSource? _sessionCts;
    private volatile Task? _activeSession;

    /// <summary>True mientras hay una tablet conectada.</summary>
    public bool HasActiveSession => _activeSession is { IsCompleted: false };

    public StreamServer(HostOptions options, IStreamObserver? observer = null, PairedDevices? paired = null)
    {
        _options = options;
        _observer = observer ?? NullStreamObserver.Instance;
        _paired = paired ?? new PairedDevices();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var certificate = LoadCertificate();
        var handshake = new Handshake(certificate, _paired, _observer, _pairingGate);

        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        LogAddresses(certificate is not null);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                // Buffer chico: si la red se atrasa preferimos que ffmpeg se frene a acumular latencia.
                client.SendBufferSize = 256 * 1024;
                var endpoint = client.Client.RemoteEndPoint;
                Log.Info($"Conexión entrante desde {endpoint}");

                if (!_pendingHandshakes.Wait(0))
                {
                    Log.Warn($"Demasiadas conexiones a medio establecer; se rechaza {endpoint}");
                    client.Dispose();
                    continue;
                }

                _ = HandleClientAsync(handshake, client, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            await _sessionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await StopSessionAsync().ConfigureAwait(false);
            }
            finally
            {
                _sessionLock.Release();
            }
        }
    }

    private static X509Certificate2? LoadCertificate()
    {
        try
        {
            return HostIdentity.LoadOrCreate();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"No se pudo cargar el certificado del host; la conexión por WiFi queda desactivada: {ex.Message}");
            return null;
        }
    }

    private async Task HandleClientAsync(Handshake handshake, TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint;
        AuthenticatedClient authenticated;
        try
        {
            authenticated = await handshake.RunAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                var reason = ex is OperationCanceledException ? "tiempo de espera agotado" : ex.Message;
                Log.Warn($"Conexión de {endpoint} rechazada: {reason}");
            }

            client.Dispose();
            return;
        }
        finally
        {
            _pendingHandshakes.Release();
        }

        try
        {
            await _sessionLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await authenticated.Channel.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            return;
        }

        try
        {
            if (ct.IsCancellationRequested)
            {
                await authenticated.Channel.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
                return;
            }

            if (_activeSession is { IsCompleted: false }) Log.Info("Reemplazando la sesión anterior");
            await StopSessionAsync().ConfigureAwait(false);

            var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sessionCts = sessionCts;
            _activeSession = RunSessionAsync(client, authenticated, sessionCts.Token);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>Corta la sesión activa y espera que quite el monitor virtual. Con _sessionLock tomado.</summary>
    private async Task StopSessionAsync()
    {
        if (_activeSession is not { } session) return;
        await _sessionCts!.CancelAsync().ConfigureAwait(false);
        await session.ConfigureAwait(false);
        _sessionCts.Dispose();
        (_sessionCts, _activeSession) = (null, null);
    }

    private async Task RunSessionAsync(TcpClient client, AuthenticatedClient authenticated, CancellationToken ct)
    {
        // Que la sesión arranque fuera de _sessionLock (y del hilo que lo tomó).
        await Task.Yield();
        var endpoint = client.Client.RemoteEndPoint;
        string? reason = null;
        using (client)
        await using (authenticated.Channel)
        {
            try
            {
                var link = authenticated.Usb ? "USB" : "WiFi (cifrado)";
                Log.Info($"Sesión por {link} con {authenticated.Hello.Device}");
                await new ClientSession(_options, _observer).RunAsync(authenticated.Channel, authenticated.Hello, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException
                                           or InvalidOperationException or TimeoutException)
            {
                Log.Warn($"Sesión con {endpoint} terminada: {ex.Message}");
                reason = ex.Message;
            }
            catch (Exception ex)
            {
                Log.Error($"Error inesperado en la sesión con {endpoint}: {ex}");
                reason = ex.Message;
            }
        }

        Log.Info($"Cliente {endpoint} desconectado");
        _observer.OnClientDisconnected(reason);
    }

    private void LogAddresses(bool wifi)
    {
        Log.Info($"Escuchando en el puerto {_options.Port}");
        Log.Info($"  USB:  ejecuta scripts\\usb-connect.ps1 y elige \"USB\" en la tablet");
        if (!wifi) return;

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => (n.Name, a.Address)))
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork);
        foreach (var (name, address) in addresses)
        {
            Log.Info($"  WiFi: {address}:{_options.Port}  ({name}, cifrado y con emparejamiento)");
        }
    }
}
