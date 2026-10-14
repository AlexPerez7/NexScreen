namespace NexScreen.Core.Net;

/// <summary>
/// Estadísticas del stream de los últimos segundos. <see cref="BitrateMbps"/> es el bitrate del
/// encoder en este momento: queda por debajo de <see cref="MaxBitrateMbps"/> si la red se congestionó.
/// </summary>
internal readonly record struct StreamStats(double Fps, double Mbps, int BitrateMbps, int MaxBitrateMbps);

/// <summary>
/// Eventos del servidor para la interfaz (la app de bandeja). Se llaman desde hilos de fondo.
/// </summary>
internal interface IStreamObserver
{
    void OnClientConnected(string device, int width, int height);

    void OnStats(StreamStats stats);

    void OnClientDisconnected(string? reason);

    /// <summary>
    /// Una tablet no emparejada quiere conectarse por WiFi: hay que mostrarle el PIN al usuario.
    /// Cancelar <paramref name="cancel"/> anula el emparejamiento (botón "Cancelar").
    /// </summary>
    void OnPairingStarted(string device, string pin, CancellationTokenSource cancel);

    /// <summary>Terminó el emparejamiento (con éxito o no): ocultar el PIN.</summary>
    void OnPairingFinished(string device, bool paired);
}

internal sealed class NullStreamObserver : IStreamObserver
{
    public static readonly NullStreamObserver Instance = new();

    public void OnClientConnected(string device, int width, int height)
    {
    }

    public void OnStats(StreamStats stats)
    {
    }

    public void OnClientDisconnected(string? reason)
    {
    }

    public void OnPairingStarted(string device, string pin, CancellationTokenSource cancel)
    {
    }

    public void OnPairingFinished(string device, bool paired)
    {
    }
}
