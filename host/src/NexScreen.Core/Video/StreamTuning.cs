using NexScreen.Core.Display;

namespace NexScreen.Core.Video;

/// <summary>
/// Ajustes del stream que dependen del dispositivo: no es lo mismo una tablet de 60 Hz que un
/// celular de 120 Hz con más píxeles. Lógica pura, con tests.
/// </summary>
internal static class StreamTuning
{
    /// <summary>Referencia de <see cref="HostOptions.BitrateMbps"/>: la tablet con la que se calibró (Galaxy Tab S9 FE).</summary>
    public const int ReferenceWidth = 2304;
    public const int ReferenceHeight = 1440;
    public const int ReferenceFps = 60;

    /// <summary>Densidad de Android de la tablet de referencia, en la que 150 % deja el texto como en un notebook.</summary>
    public const int ReferenceDpi = 280;
    public const int ReferenceScalePercent = 150;

    public const int MinAutoFps = 30;
    public const int MaxAutoFps = 120;
    public const int MinBitrateMbps = 2;
    public const int MaxBitrateMbps = 80;

    /// <summary>
    /// Los fps pedidos, o la frecuencia de la pantalla del dispositivo (60 si no la informa: las
    /// apps viejas no la envían).
    /// </summary>
    public static int ChooseFps(int? requested, int clientRefreshRate) =>
        requested ?? (clientRefreshRate > 0 ? Math.Clamp(clientRefreshRate, MinAutoFps, MaxAutoFps) : ReferenceFps);

    /// <summary>
    /// Tamaño al que se transmite una pantalla: el suyo, achicado (nunca agrandado) para entrar en el
    /// máximo del dispositivo manteniendo el aspecto, y con lados pares como exige H.264 en NV12.
    /// </summary>
    public static (int Width, int Height) FitSize(int width, int height, int maxWidth, int maxHeight)
    {
        var factor = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
        return ((int)(width * factor) / 2 * 2, (int)(height * factor) / 2 * 2);
    }

    /// <summary>
    /// Escala el bitrate de referencia a la resolución y los fps del dispositivo. Los píxeles cuentan
    /// completos; los fps, con raíz cuadrada: a más cuadros por segundo, menos cambia cada uno.
    /// </summary>
    public static int ScaleBitrate(int referenceMbps, int width, int height, int fps)
    {
        var pixels = (double)width * height / (ReferenceWidth * ReferenceHeight);
        var motion = Math.Sqrt((double)fps / ReferenceFps);
        return (int)Math.Clamp(Math.Round(referenceMbps * pixels * motion), MinBitrateMbps, MaxBitrateMbps);
    }

    /// <summary>
    /// Escala de Windows según la densidad de Android del dispositivo (densityDpi), que ya tiene en
    /// cuenta a qué distancia se mira: un celular usa más densidad que una tablet para que el texto
    /// se lea igual. Calibrada con la tablet de referencia (280 dpi → 150 %).
    /// </summary>
    public static int AutoScalePercent(int dpi)
    {
        if (dpi <= 0) return ReferenceScalePercent;
        var ideal = (double)dpi * ReferenceScalePercent / ReferenceDpi;
        return DpiScale.Steps.MinBy(step => Math.Abs(step - ideal));
    }
}
