using static NexScreen.Core.Input.PointerNative;

namespace NexScreen.Core.Input;

/// <summary>Rectángulo del monitor capturado, en píxeles físicos del escritorio.</summary>
internal readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public PointerNative.Point ToPixel(double nx, double ny) => new()
    {
        X = X + (int)Math.Round(Math.Clamp(nx, 0, 1) * (Width - 1)),
        Y = Y + (int)Math.Round(Math.Clamp(ny, 0, 1) * (Height - 1)),
    };
}

/// <summary>
/// Convierte muestras de la tablet en frames de InjectSyntheticPointerInput y lleva el estado
/// que Windows exige: cada frame táctil incluye todos los contactos activos, un contacto debe
/// empezar con DOWN y terminar con UP, y el lápiz pasa por hover → contacto → hover.
/// No llama a Win32, así que se puede testear.
/// </summary>
internal sealed class PointerTranslator
{
    public const int MaxTouchContacts = 10;
    private const int ContactRadius = 2;
    private const uint TouchPressure = 512;
    private const uint MaxPenPressure = 1024;

    private readonly Dictionary<byte, TouchContact> _contacts = new();
    private uint? _primarySlot;
    private bool _penInRange;
    private bool _penInContact;
    private PointerNative.Point _penLocation;

    private sealed class TouchContact(uint slot, PointerNative.Point location)
    {
        public uint Slot { get; } = slot;
        public PointerNative.Point Location { get; set; } = location;
    }

    public bool HasActiveTouches => _contacts.Count > 0;

    /// <summary>Frame táctil con todos los contactos activos (los no mencionados se repiten quietos).</summary>
    public PointerTypeInfo[] TouchFrame(IReadOnlyList<PointerSample> fingers, ScreenRect screen)
    {
        var frame = new List<PointerTypeInfo>();
        var mentioned = new HashSet<byte>();
        var released = new List<byte>();

        foreach (var sample in fingers)
        {
            if (!mentioned.Add(sample.Id)) continue;
            var location = screen.ToPixel(sample.X, sample.Y);
            var known = _contacts.TryGetValue(sample.Id, out var contact);

            switch (sample.Action)
            {
                case PointerAction.Down or PointerAction.Move:
                    if (!known)
                    {
                        // Un MOVE sin DOWN previo (se perdió el evento) se trata como DOWN.
                        var slot = FreeSlot();
                        if (slot is null) continue;
                        contact = new TouchContact(slot.Value, location);
                        _contacts[sample.Id] = contact;
                        _primarySlot ??= slot;
                        frame.Add(Touch(contact, FlagDown | FlagInRange | FlagInContact));
                    }
                    else
                    {
                        contact!.Location = location;
                        frame.Add(Touch(contact, FlagUpdate | FlagInRange | FlagInContact));
                    }

                    break;
                case PointerAction.Up or PointerAction.Cancel when known:
                    contact!.Location = location;
                    var flags = FlagUp | (sample.Action == PointerAction.Cancel ? FlagCanceled : 0);
                    frame.Add(Touch(contact, flags));
                    released.Add(sample.Id);
                    break;
            }
        }

        foreach (var (id, contact) in _contacts)
        {
            if (!mentioned.Contains(id)) frame.Add(Touch(contact, FlagUpdate | FlagInRange | FlagInContact));
        }

        foreach (var id in released) _contacts.Remove(id);
        if (_contacts.Count == 0) _primarySlot = null;

        return frame.ToArray();
    }

    /// <summary>Levanta todos los contactos (al cerrar la sesión, para que no quede un dedo "pegado").</summary>
    public PointerTypeInfo[] ReleaseAllTouches()
    {
        var frame = _contacts.Values.Select(c => Touch(c, FlagUp | FlagCanceled)).ToArray();
        _contacts.Clear();
        _primarySlot = null;
        return frame;
    }

    /// <summary>Frames del lápiz para una muestra (pueden ser dos si hay que cerrar un contacto perdido).</summary>
    public IReadOnlyList<PointerTypeInfo> PenFrames(PointerSample sample, ScreenRect screen)
    {
        var frames = new List<PointerTypeInfo>();
        var location = sample.Action is PointerAction.HoverExit ? _penLocation : screen.ToPixel(sample.X, sample.Y);
        _penLocation = location;

        switch (sample.Action)
        {
            case PointerAction.Hover:
                if (_penInContact) frames.Add(Pen(sample, location, FlagUp | FlagInRange, inContact: false));
                frames.Add(Pen(sample, location, FlagUpdate | FlagInRange, inContact: false));
                (_penInRange, _penInContact) = (true, false);
                break;
            case PointerAction.Down or PointerAction.Move:
                var flags = _penInContact ? FlagUpdate : FlagDown;
                frames.Add(Pen(sample, location, flags | FlagInRange | FlagInContact | FlagFirstButton, inContact: true));
                (_penInRange, _penInContact) = (true, true);
                break;
            case PointerAction.Up or PointerAction.Cancel when _penInContact:
                var upFlags = FlagUp | FlagInRange | (sample.Action == PointerAction.Cancel ? FlagCanceled : 0);
                frames.Add(Pen(sample, location, upFlags, inContact: false));
                _penInContact = false;
                break;
            case PointerAction.HoverExit when _penInRange:
                if (_penInContact) frames.Add(Pen(sample, location, FlagUp | FlagInRange, inContact: false));
                frames.Add(Pen(sample, location, FlagUpdate, inContact: false));
                (_penInRange, _penInContact) = (false, false);
                break;
        }

        return frames;
    }

    private uint? FreeSlot()
    {
        for (uint slot = 0; slot < MaxTouchContacts; slot++)
        {
            if (_contacts.Values.All(c => c.Slot != slot)) return slot;
        }

        return null;
    }

    private PointerTypeInfo Touch(TouchContact contact, uint flags)
    {
        if (contact.Slot == _primarySlot) flags |= FlagPrimary;
        var info = new PointerTypeInfo { Type = PtTouch };
        info.Touch.PointerInfo.PointerType = PtTouch;
        info.Touch.PointerInfo.PointerId = contact.Slot;
        info.Touch.PointerInfo.PointerFlags = flags | FlagConfidence;
        info.Touch.PointerInfo.PtPixelLocation = contact.Location;
        info.Touch.TouchMask = TouchMaskContactArea | TouchMaskOrientation | TouchMaskPressure;
        info.Touch.RcContact = new PointerNative.Rect
        {
            Left = contact.Location.X - ContactRadius,
            Top = contact.Location.Y - ContactRadius,
            Right = contact.Location.X + ContactRadius,
            Bottom = contact.Location.Y + ContactRadius,
        };
        info.Touch.Orientation = 90;
        info.Touch.Pressure = TouchPressure;
        return info;
    }

    private static PointerTypeInfo Pen(PointerSample sample, PointerNative.Point location, uint flags, bool inContact)
    {
        var eraser = sample.Tool == PointerTool.Eraser;
        if (sample.BarrelButton && inContact) flags |= FlagSecondButton;

        var info = new PointerTypeInfo { Type = PtPen };
        info.Pen.PointerInfo.PointerType = PtPen;
        info.Pen.PointerInfo.PointerFlags = flags | FlagPrimary;
        info.Pen.PointerInfo.PtPixelLocation = location;
        info.Pen.PenFlags = (sample.BarrelButton ? PenFlagBarrel : 0)
                            | (eraser ? PenFlagInverted : 0)
                            | (eraser && inContact ? PenFlagEraser : 0);
        info.Pen.PenMask = PenMaskPressure | PenMaskTiltX | PenMaskTiltY;
        info.Pen.Pressure = inContact ? (uint)Math.Round(Math.Clamp(sample.Pressure, 0, 1) * MaxPenPressure) : 0;
        info.Pen.TiltX = Math.Clamp(sample.TiltX, -90, 90);
        info.Pen.TiltY = Math.Clamp(sample.TiltY, -90, 90);
        return info;
    }
}
