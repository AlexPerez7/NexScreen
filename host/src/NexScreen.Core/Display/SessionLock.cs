using System.Runtime.InteropServices;
using System.Text;

namespace NexScreen.Core.Display;

/// <summary>
/// Detecta si la sesión de Windows está bloqueada o mostrando el escritorio seguro (UAC,
/// Ctrl+Alt+Supr). En esos casos Desktop Duplication devuelve "access denied" o no entrega
/// cuadros, sin importar lo que haga el host.
/// </summary>
internal static class SessionLock
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;
    private const int WtsSessionInfoEx = 25;
    private const int WtsSessionStateLock = 0;

    /// <summary>Offset de SessionFlags en WTSINFOEXW: Level (4) + relleno hasta la unión alineada a 8 + SessionId + SessionState.</summary>
    private const int SessionFlagsOffset = 16;

    public static bool IsLocked() => IsSessionLocked() || IsOnSecureDesktop();

    /// <summary>
    /// Pantalla de bloqueo. En Windows 10/11 se dibuja en el escritorio normal, así que solo la
    /// informa el estado de la sesión.
    /// </summary>
    private static bool IsSessionLocked()
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, -1, WtsSessionInfoEx, out var buffer, out var bytes)) return false;
        try
        {
            return bytes >= SessionFlagsOffset + 4 && Marshal.ReadInt32(buffer, SessionFlagsOffset) == WtsSessionStateLock;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>Escritorio seguro (UAC, Ctrl+Alt+Supr): el escritorio de entrada deja de ser "Default".</summary>
    private static bool IsOnSecureDesktop()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) return true; // sin acceso al escritorio de entrada: es el seguro

        try
        {
            var name = new StringBuilder(256);
            return GetUserObjectInformation(desktop, UoiName, name, name.Capacity * 2, out _)
                   && !name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>Espera a que el usuario desbloquee la sesión.</summary>
    public static async Task WaitUntilUnlockedAsync(CancellationToken ct)
    {
        while (IsLocked()) await Task.Delay(1000, ct).ConfigureAwait(false);
    }

    [DllImport("wtsapi32.dll", SetLastError = true, EntryPoint = "WTSQuerySessionInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
