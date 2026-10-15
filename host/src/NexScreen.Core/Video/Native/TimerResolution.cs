using System.Runtime.InteropServices;

namespace NexScreen.Core.Video.Native;

/// <summary>
/// Pide a Windows una resolución de temporizador de 1 ms mientras dura (por defecto son ~15,6 ms,
/// que quitan fluidez a las esperas del pacing de cuadros). Se puede anidar.
/// </summary>
internal sealed class TimerResolution : IDisposable
{
    private bool _disposed;

    private TimerResolution() => TimeBeginPeriod(1);

    public static TimerResolution Request() => new();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TimeEndPeriod(1);
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
