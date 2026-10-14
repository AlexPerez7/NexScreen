namespace NexScreen.Core.Net;

/// <summary>
/// Eventos del servidor para la interfaz (la app de bandeja). Se llaman desde hilos de fondo.
/// </summary>
internal interface IStreamObserver
{
    void OnClientConnected(string device, int width, int height);

    void OnStats(double fps, double mbps);

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

    public void OnStats(double fps, double mbps)
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
