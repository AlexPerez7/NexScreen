using static NexScreen.Core.Display.NativeMethods;

namespace NexScreen.Core.Display;

internal enum DisplayPosition
{
    Right,
    Left,
    Above,
    Below,
}

/// <summary>
/// Monitor virtual del Virtual Display Driver. El driver siempre expone al menos un monitor,
/// así que "encender/apagar" significa agregarlo o quitarlo del escritorio con la API CCD (lo
/// mismo que "Extender" / "Desconectar esta pantalla" en Configuración). No requiere admin.
/// </summary>
internal static class VirtualDisplay
{
    private static readonly TimeSpan ModeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OutputTimeout = TimeSpan.FromSeconds(5);

    public static bool IsVirtual(DisplayInfo display) =>
        display.AdapterName.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase)
        || display.DeviceId.Contains("MttVDD", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adaptador del driver (para saber si está instalado y qué modos ofrece).</summary>
    public static DisplayInfo? Find() => DisplayDevices.List().FirstOrDefault(IsVirtual);

    /// <summary>
    /// Agrega el monitor virtual al escritorio con la resolución pedida. Sin <paramref name="position"/>
    /// usa la última posición que dejó el usuario, o a la derecha si no hay una válida.
    /// Devuelve el nombre GDI (\\.\DISPLAYn) con el que quedó en el escritorio.
    /// </summary>
    public static async Task<string> AttachAsync(
        int width, int height, int refreshRate, DisplayPosition? position, CancellationToken ct)
    {
        var display = Find() ?? throw new InvalidOperationException("No se encontró el Virtual Display Driver");

        if (!HasMode(display.DeviceName, width, height, refreshRate))
        {
            if (!VddSettings.EnsureResolution(width, height, refreshRate))
            {
                throw new InvalidOperationException(
                    $"El driver no ofrece {width}x{height}@{refreshRate} aunque está en {VddSettings.DefaultPath}");
            }

            Log.Info($"Agregando {width}x{height}@{refreshRate} al monitor virtual y recargando el driver");
            await VddSettings.SendCommandAsync("RELOAD_DRIVER", ct).ConfigureAwait(false);
            // Al recargar, el adaptador puede reaparecer con otro nombre (\\.\DISPLAYn).
            await WaitUntilAsync(
                () => Find() is { } d && HasMode(d.DeviceName, width, height, refreshRate),
                ModeTimeout, "el driver no publicó la resolución nueva", ct).ConfigureAwait(false);
        }

        // IddCx presenta el monitor a través de una fuente de la GPU: ese nombre GDI es el que cuenta.
        var gdiName = DisplayConfig.AttachVirtual();
        await WaitUntilAsync(
            () => DisplayDevices.List().Any(d => d.DeviceName == gdiName && d.Attached),
            OutputTimeout, $"{gdiName} no se agregó al escritorio", ct).ConfigureAwait(false);

        // Ya en el escritorio, ChangeDisplaySettingsEx sí puede fijar resolución y posición.
        var others = DisplayDevices.List().Where(d => d.Attached && d.DeviceName != gdiName && !IsVirtual(d)).ToList();
        var (x, y) = ChoosePosition(others, width, height, position, HostState.Load());
        var mode = DevMode.Create();
        mode.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency | DmPosition;
        mode.PelsWidth = (uint)width;
        mode.PelsHeight = (uint)height;
        mode.DisplayFrequency = (uint)refreshRate;
        mode.PositionX = x;
        mode.PositionY = y;
        Apply(gdiName, ref mode, "configurar el monitor virtual");
        Log.Info($"Monitor virtual {gdiName}: {width}x{height}@{refreshRate} en ({x},{y})");

        await WaitUntilAsync(
            () => DxgiOutputs.FindIndex(gdiName) is not null,
            OutputTimeout, $"DXGI no expone {gdiName}", ct).ConfigureAwait(false);
        return gdiName;
    }

    /// <summary>Índice DXGI actual del monitor virtual (cambia si se reordenan las pantallas).</summary>
    public static int OutputIndex(string gdiName) =>
        DxgiOutputs.FindIndex(gdiName)
        ?? throw new InvalidOperationException($"El monitor virtual {gdiName} ya no está en el escritorio");

    /// <summary>
    /// Quita el monitor virtual del escritorio; las ventanas vuelven a los monitores reales.
    /// Con <paramref name="rememberPosition"/> guarda antes su posición para respetarla la próxima vez
    /// (solo al cerrar una sesión: al arrancar puede estar duplicando otra pantalla).
    /// </summary>
    public static void Detach(bool rememberPosition)
    {
        if (DisplayConfig.VirtualSourceName() is null) return;
        if (rememberPosition) RememberPosition();
        if (DisplayConfig.DetachVirtual()) Log.Info("Monitor virtual quitado del escritorio");
    }

    /// <summary>
    /// Guarda la posición actual del monitor virtual. Se llama al cerrar la sesión y también cuando
    /// el usuario reordena las pantallas, para no perderla si el host se cierra a la fuerza.
    /// </summary>
    public static void RememberPosition()
    {
        var gdiName = DisplayConfig.VirtualSourceName();
        if (gdiName is null) return;
        if (DisplayDevices.List().FirstOrDefault(d => d.DeviceName == gdiName && d.Attached) is not { } current) return;

        var state = HostState.Load();
        if (state.VirtualDisplayX == current.X && state.VirtualDisplayY == current.Y) return;

        state.VirtualDisplayX = current.X;
        state.VirtualDisplayY = current.Y;
        state.Save();
        Log.Info($"Posición del monitor virtual guardada: ({current.X},{current.Y})");
    }

    private static bool HasMode(string deviceName, int width, int height, int refreshRate) =>
        DisplayDevices.Modes(deviceName).Contains((width, height, refreshRate));

    internal static (int X, int Y) ChoosePosition(
        IReadOnlyList<DisplayInfo> physical, int width, int height, DisplayPosition? position, HostState state)
    {
        if (position is null
            && state is { VirtualDisplayX: { } x, VirtualDisplayY: { } y }
            && IsValidPlacement(physical, x, y, width, height))
        {
            return (x, y);
        }

        return ComputePosition(physical, width, height, position ?? DisplayPosition.Right);
    }

    /// <summary>Válida si no se superpone con ningún monitor y comparte un borde con alguno.</summary>
    internal static bool IsValidPlacement(IReadOnlyList<DisplayInfo> physical, int x, int y, int width, int height)
    {
        if (physical.Count == 0) return true;

        var overlaps = physical.Any(d => x < d.X + d.Width && d.X < x + width && y < d.Y + d.Height && d.Y < y + height);
        var touches = physical.Any(d =>
            ((x + width == d.X || d.X + d.Width == x) && y < d.Y + d.Height && d.Y < y + height)
            || ((y + height == d.Y || d.Y + d.Height == y) && x < d.X + d.Width && d.X < x + width));
        return !overlaps && touches;
    }

    /// <summary>Ubica el monitor pegado al borde del área ocupada por los monitores reales.</summary>
    internal static (int X, int Y) ComputePosition(IReadOnlyList<DisplayInfo> physical, int width, int height, DisplayPosition position)
    {
        if (physical.Count == 0) return (0, 0);
        var primary = physical.FirstOrDefault(d => d.Primary) ?? physical[0];

        return position switch
        {
            DisplayPosition.Right => (physical.Max(d => d.X + d.Width), primary.Y),
            DisplayPosition.Left => (physical.Min(d => d.X) - width, primary.Y),
            DisplayPosition.Above => (primary.X, physical.Min(d => d.Y) - height),
            DisplayPosition.Below => (primary.X, physical.Max(d => d.Y + d.Height)),
            _ => throw new ArgumentOutOfRangeException(nameof(position)),
        };
    }

    private static void Apply(string deviceName, ref DevMode mode, string action)
    {
        var result = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry | CdsNoReset, IntPtr.Zero);
        if (result == DispChangeSuccessful)
        {
            result = ApplyDisplaySettings(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
        }

        if (result != DispChangeSuccessful)
        {
            throw new InvalidOperationException($"No se pudo {action} (ChangeDisplaySettingsEx devolvió {result})");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string error, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Timeout: {error}");
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
}
