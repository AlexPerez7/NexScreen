using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NexScreen.Host.Net;

/// <summary>
/// Acepta conexiones TCP. Hay una sola sesión activa: un cliente nuevo reemplaza al anterior
/// (por ejemplo, al pasar de WiFi a USB).
/// </summary>
internal sealed class StreamServer
{
    private readonly HostOptions _options;

    public StreamServer(HostOptions options) => _options = options;

    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        LogAddresses();

        CancellationTokenSource? sessionCts = null;
        Task? sessionTask = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                // Buffer chico: si la red se atrasa preferimos que ffmpeg se frene a acumular latencia.
                client.SendBufferSize = 256 * 1024;
                Log.Info($"Conexión entrante desde {client.Client.RemoteEndPoint}");

                if (sessionTask is not null)
                {
                    Log.Info("Reemplazando la sesión anterior");
                    await sessionCts!.CancelAsync().ConfigureAwait(false);
                    await sessionTask.ConfigureAwait(false);
                    sessionCts.Dispose();
                }

                sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sessionTask = RunSessionAsync(client, sessionCts.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
            if (sessionTask is not null)
            {
                await sessionCts!.CancelAsync().ConfigureAwait(false);
                await sessionTask.ConfigureAwait(false);
                sessionCts.Dispose();
            }
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken ct)
    {
        var endpoint = client.Client.RemoteEndPoint;
        using (client)
        {
            try
            {
                await new ClientSession(_options).RunAsync(client, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException
                                           or InvalidOperationException or TimeoutException)
            {
                Log.Warn($"Sesión con {endpoint} terminada: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error inesperado en la sesión con {endpoint}: {ex}");
            }
        }

        Log.Info($"Cliente {endpoint} desconectado");
    }

    private void LogAddresses()
    {
        Log.Info($"Escuchando en el puerto {_options.Port}");
        Log.Info($"  USB:  ejecutá scripts\\usb-connect.ps1 y elegí \"USB\" en la tablet");

        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => (n.Name, a.Address)))
            .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork);
        foreach (var (name, address) in addresses)
        {
            Log.Info($"  WiFi: {address}:{_options.Port}  ({name})");
        }
    }
}
