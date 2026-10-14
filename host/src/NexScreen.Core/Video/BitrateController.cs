namespace NexScreen.Core.Video;

/// <summary>
/// Bitrate adaptativo (AIMD): baja rápido cuando la red no da abasto y sube de a poco cuando
/// demuestra que aguanta. Recibe una medición por segundo:
/// <list type="bullet">
/// <item><c>blockedRatio</c>: fracción del segundo que el envío del video estuvo bloqueado esperando
/// al socket. Con la red holgada es casi 0; cuando el buffer de envío se llena, crece.</item>
/// <item><c>sentMbps</c>: lo que realmente se envió. Si la pantalla está quieta se envía poco, y eso
/// no prueba que la red aguante más: esos segundos no cuentan para subir.</item>
/// </list>
/// Lógica pura (sin reloj propio) para poder testearla.
/// </summary>
internal sealed class BitrateController
{
    /// <summary>A partir de este valor, un segundo cuenta como congestionado (dos seguidos bajan el bitrate).</summary>
    internal const double CongestedRatio = 0.25;

    /// <summary>Un solo segundo así de bloqueado alcanza para bajar.</summary>
    internal const double SevereRatio = 0.5;

    /// <summary>Por debajo de esto el segundo cuenta como holgado.</summary>
    internal const double ClearRatio = 0.05;

    /// <summary>Para contar como prueba de que la red aguanta, hay que haber enviado al menos esta fracción del bitrate.</summary>
    internal const double MinLoad = 0.4;

    internal const double DecreaseFactor = 0.7;

    /// <summary>Al bajar se apunta a esta fracción de lo que se llegó a enviar con la red congestionada.</summary>
    internal const double ThroughputMargin = 0.85;

    /// <summary>Tras un cambio, la captura nueva tarda en arrancar y la red en vaciarse: no medir.</summary>
    internal static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(3);

    /// <summary>Si la congestión vuelve antes de esto después de subir, la subida falló.</summary>
    internal static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(15);

    /// <summary>Segundos holgados (con carga) que hacen falta para subir; se duplica con cada subida fallida.</summary>
    internal const int BaseHoldWindows = 10;

    internal const int MaxHoldWindows = 60;

    private TimeSpan? _lastChange;
    private TimeSpan? _lastIncrease;
    private int _congestedWindows;
    private int _clearWindows;
    private int _holdWindows = BaseHoldWindows;

    public BitrateController(int maxMbps)
    {
        if (maxMbps < 1) throw new ArgumentOutOfRangeException(nameof(maxMbps));
        MaxMbps = maxMbps;
        MinMbps = Math.Min(maxMbps, Math.Max(2, maxMbps / 4));
        CurrentMbps = maxMbps;
    }

    public int MaxMbps { get; }

    public int MinMbps { get; }

    public int CurrentMbps { get; private set; }

    /// <summary>Procesa un segundo de envío. Devuelve el bitrate nuevo si hay que cambiarlo.</summary>
    public int? OnWindow(double blockedRatio, double sentMbps, TimeSpan now)
    {
        if (_lastChange is { } change && now - change < Cooldown)
        {
            _congestedWindows = 0;
            return null;
        }

        // La última subida aguantó: la próxima vuelve a esperar lo normal.
        if (_lastIncrease is { } increase && now - increase >= ProbeWindow)
        {
            _lastIncrease = null;
            _holdWindows = BaseHoldWindows;
        }

        if (blockedRatio >= CongestedRatio)
        {
            _clearWindows = 0;
            _congestedWindows++;
            if (blockedRatio < SevereRatio && _congestedWindows < 2) return null;
            _congestedWindows = 0;
            return Decrease(sentMbps, now);
        }

        _congestedWindows = 0;
        if (blockedRatio > ClearRatio || sentMbps < CurrentMbps * MinLoad) return null;

        if (++_clearWindows < _holdWindows || CurrentMbps >= MaxMbps) return null;
        _clearWindows = 0;
        _lastIncrease = now;
        var step = Math.Max(1, MaxMbps / 6);
        return Change(Math.Min(MaxMbps, CurrentMbps + step), now);
    }

    private int? Decrease(double sentMbps, TimeSpan now)
    {
        if (CurrentMbps <= MinMbps) return null;

        // Una subida que congestiona enseguida: esperar más antes de volver a probar.
        if (_lastIncrease is not null)
        {
            _holdWindows = Math.Min(_holdWindows * 2, MaxHoldWindows);
            _lastIncrease = null;
        }

        // Lo que se llegó a enviar con la red saturada es una estimación de lo que da la red.
        var target = Math.Min(CurrentMbps * DecreaseFactor, sentMbps * ThroughputMargin);
        var next = Math.Clamp((int)Math.Floor(target), MinMbps, CurrentMbps - 1);
        return Change(next, now);
    }

    private int Change(int mbps, TimeSpan now)
    {
        CurrentMbps = mbps;
        _lastChange = now;
        return mbps;
    }
}
