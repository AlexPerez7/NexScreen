using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace NexScreen.Tray;

internal enum TrayState
{
    Waiting,
    Connected,
    Error,
}

/// <summary>Íconos de la bandeja: el ícono de la app con un punto de estado en la esquina.</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<TrayState, Icon> _icons = new();

    public TrayIcons()
    {
        var size = SystemInformation.SmallIconSize;
        using var stream = typeof(TrayIcons).Assembly.GetManifestResourceStream("nexscreen.ico")
            ?? throw new InvalidOperationException("Falta el recurso nexscreen.ico");
        using var baseIcon = new Icon(stream, size);
        using var baseBitmap = baseIcon.ToBitmap();

        _icons[TrayState.Waiting] = WithDot(baseBitmap, size, Color.FromArgb(0x9C, 0xA3, 0xAF));
        _icons[TrayState.Connected] = WithDot(baseBitmap, size, Color.FromArgb(0x22, 0xC5, 0x5E));
        _icons[TrayState.Error] = WithDot(baseBitmap, size, Color.FromArgb(0xF5, 0x9E, 0x0B));
    }

    public Icon this[TrayState state] => _icons[state];

    private static Icon WithDot(Bitmap source, Size size, Color color)
    {
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(source, 0, 0, size.Width, size.Height);
            var dot = Math.Max(5, size.Width * 7 / 16);
            var rect = new Rectangle(size.Width - dot, size.Height - dot, dot - 1, dot - 1);
            using var brush = new SolidBrush(color);
            using var border = new Pen(Color.FromArgb(0x1F, 0x29, 0x37), Math.Max(1, size.Width / 16f));
            g.FillEllipse(brush, rect);
            g.DrawEllipse(border, rect);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle no es dueño del handle: se clona y se libera el original.
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
