namespace NexScreen.Core.Video.Native;

/// <summary>
/// Decide cuándo enviar un cuadro a una tasa fija de fps, a partir de cuándo cambia la pantalla.
/// Lógica pura (sin relojes ni GPU) para poder probarla.
///
/// Se manda por una rejilla de tiempo regular: si la pantalla cambia más rápido que los fps (144 Hz
/// para 90 fps) se manda lo último que hay en cada instante de la rejilla, no cada vez que llega algo;
/// si cambia más lento, se manda apenas llega. Con la pantalla quieta se repite el último cuadro cada
/// <c>heartbeat</c> para mantener viva la conexión, y un keyframe pedido sale de inmediato.
/// </summary>
internal sealed class FramePacer(int fps, TimeSpan heartbeat)
{
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(0.5);

    private readonly TimeSpan _interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, fps));
    private TimeSpan _nextSend;
    private TimeSpan? _lastSend;

    /// <param name="Send">Mandar un cuadro ahora.</param>
    /// <param name="WaitMs">Si no, cuántos ms esperar una imagen nueva antes de volver a decidir.</param>
    public readonly record struct Decision(bool Send, int WaitMs);

    public Decision Next(TimeSpan now, bool hasFrame, bool changed, bool keyFrameRequested, bool queueFull)
    {
        if (!hasFrame) return new Decision(false, (int)heartbeat.TotalMilliseconds);

        // El destino no da abasto: no apilar cuadros, esperar un poco y reintentar.
        if (queueFull) return new Decision(false, 1);

        var idleFor = _lastSend is { } last ? now - last : TimeSpan.MaxValue;
        var wanted = changed || keyFrameRequested || idleFor >= heartbeat;
        if (!wanted) return new Decision(false, CeilMs(heartbeat - idleFor));

        var due = _lastSend is null ? now : _nextSend;
        if (now + Slack >= due) return new Decision(true, 0);

        return new Decision(false, CeilMs(due - now));
    }

    /// <summary>Registra que se mandó un cuadro en <paramref name="now"/> y avanza la rejilla.</summary>
    public void Sent(TimeSpan now)
    {
        var next = (_lastSend is null ? now : _nextSend) + _interval;
        // Si se atrasó, no mandar una ráfaga para recuperar: seguir desde ahora, un intervalo después.
        _nextSend = next < now ? now + _interval : next;
        _lastSend = now;
    }

    private static int CeilMs(TimeSpan value) => Math.Max(1, (int)Math.Ceiling(value.TotalMilliseconds));
}
