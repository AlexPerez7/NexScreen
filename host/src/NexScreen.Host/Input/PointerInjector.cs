using System.ComponentModel;
using System.Runtime.InteropServices;
using static NexScreen.Host.Input.PointerNative;

namespace NexScreen.Host.Input;

/// <summary>
/// Inyecta táctil y lápiz reales en Windows con dispositivos de puntero sintéticos: Windows los
/// trata como una pantalla táctil y un lápiz físicos (gestos, presión, inclinación, botón lateral).
/// No requiere admin, pero Windows no deja inyectar en ventanas elevadas (UIPI).
/// </summary>
internal sealed class PointerInjector : IDisposable
{
    private const int MaxErrorsLogged = 5;

    private readonly PointerTranslator _translator = new();
    private readonly Func<ScreenRect> _screen;
    private IntPtr _touchDevice;
    private IntPtr _penDevice;
    private int _errorsLogged;

    public PointerInjector(Func<ScreenRect> screen) => _screen = screen;

    public void Inject(IReadOnlyList<PointerSample> samples)
    {
        // InjectSyntheticPointerInput interpreta las coordenadas relativas a la esquina superior
        // izquierda del escritorio virtual, no como coordenadas absolutas: con un monitor a la
        // izquierda del principal el origen es negativo y sin esta corrección los toques se pierden.
        var origin = DesktopOrigin();
        var monitor = _screen();
        var screen = monitor with { X = monitor.X - origin.X, Y = monitor.Y - origin.Y };
        if (Log.Verbose)
        {
            var first = samples[0];
            var pixel = screen.ToPixel(first.X, first.Y);
            Log.Debug($"POINTER x{samples.Count}: {first.Tool} {first.Action} ({first.X:F3},{first.Y:F3}) -> " +
                      $"({pixel.X},{pixel.Y}) relativo al origen ({origin.X},{origin.Y}) en {monitor}");
        }

        var fingers = samples.Where(s => s.Tool == PointerTool.Finger).ToList();
        if (fingers.Count > 0)
        {
            Send(ref _touchDevice, PtTouch, PointerTranslator.MaxTouchContacts, _translator.TouchFrame(fingers, screen));
        }

        foreach (var sample in samples.Where(s => s.Tool != PointerTool.Finger))
        {
            foreach (var frame in _translator.PenFrames(sample, screen))
            {
                Send(ref _penDevice, PtPen, 1, [frame]);
            }
        }
    }

    private static (int X, int Y) DesktopOrigin() =>
        (GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen));

    private void Send(ref IntPtr device, uint type, int maxCount, PointerTypeInfo[] frame)
    {
        if (frame.Length == 0) return;

        if (device == IntPtr.Zero)
        {
            device = CreateSyntheticPointerDevice(type, (uint)maxCount, PointerFeedbackDefault);
            if (device == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "No se pudo crear el dispositivo de puntero sintético");
            }
        }

        if (!InjectSyntheticPointerInput(device, frame, (uint)frame.Length) && _errorsLogged < MaxErrorsLogged)
        {
            _errorsLogged++;
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            Log.Warn($"Windows rechazó un evento de {(type == PtTouch ? "táctil" : "lápiz")}: {error.Message}");
        }
    }

    public void Dispose()
    {
        if (_touchDevice != IntPtr.Zero)
        {
            var release = _translator.ReleaseAllTouches();
            if (release.Length > 0) InjectSyntheticPointerInput(_touchDevice, release, (uint)release.Length);
            DestroySyntheticPointerDevice(_touchDevice);
            _touchDevice = IntPtr.Zero;
        }

        if (_penDevice != IntPtr.Zero)
        {
            DestroySyntheticPointerDevice(_penDevice);
            _penDevice = IntPtr.Zero;
        }
    }
}
