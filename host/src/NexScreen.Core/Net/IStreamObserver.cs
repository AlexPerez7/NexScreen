namespace NexScreen.Core.Net;

/// <summary>
/// Eventos del servidor para la interfaz (la app de bandeja). Se llaman desde hilos de fondo.
/// </summary>
internal interface IStreamObserver
{
    void OnClientConnected(string device, int width, int height);

    void OnStats(double fps, double mbps);

    void OnClientDisconnected(string? reason);
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
}
