namespace NexScreen.Core.Display;

/// <summary>
/// Escalas de Windows (Configuración → Pantalla → Escala). La API interna no recibe porcentajes
/// sino posiciones relativas a la escala recomendada por Windows para ese monitor:
/// "min" es cuántos pasos hay por debajo de la recomendada (negativo) y "max" por encima.
/// </summary>
internal static class DpiScale
{
    public static readonly int[] Steps = [100, 125, 150, 175, 200, 225, 250, 300, 350, 400, 450, 500];

    /// <summary>Porcentaje actual a partir de los valores relativos que devuelve Windows.</summary>
    public static int ToPercent(int minRelative, int relative)
    {
        var index = Math.Clamp(relative - minRelative, 0, Steps.Length - 1);
        return Steps[index];
    }

    /// <summary>
    /// Valor relativo para pedir <paramref name="percent"/> (o el paso más cercano), limitado a lo
    /// que admite el monitor.
    /// </summary>
    public static int ToRelative(int minRelative, int maxRelative, int percent)
    {
        var nearest = Steps
            .Select((step, index) => (Distance: Math.Abs(step - percent), Index: index))
            .MinBy(x => x.Distance).Index;
        // La recomendada está en el índice -minRelative (el mínimo siempre es 100 %).
        return Math.Clamp(nearest + minRelative, minRelative, maxRelative);
    }
}
