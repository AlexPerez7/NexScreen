using System.Buffers.Binary;

namespace NexScreen.Core.Input;

internal enum PointerTool : byte
{
    Finger = 0,
    Pen = 1,
    Eraser = 2,
}

internal enum PointerAction : byte
{
    Down = 0,
    Move = 1,
    Up = 2,
    Cancel = 3,

    /// <summary>Lápiz cerca de la pantalla sin tocarla.</summary>
    Hover = 4,

    /// <summary>El lápiz se alejó de la pantalla.</summary>
    HoverExit = 5,
}

/// <summary>
/// Un puntero en un instante. Coordenadas normalizadas 0..1 sobre el área de video; presión 0..1;
/// inclinación en grados (-90..90).
/// </summary>
internal readonly record struct PointerSample(
    byte Id,
    PointerTool Tool,
    PointerAction Action,
    bool BarrelButton,
    double X,
    double Y,
    double Pressure,
    int TiltX,
    int TiltY);

/// <summary>
/// Payload del mensaje POINTER: [u8 cantidad] + cantidad × 12 bytes
/// [u8 id][u8 tool][u8 action][u8 buttons][u16 x][u16 y][u16 presión][i8 tiltX][i8 tiltY].
/// Los punteros táctiles de un mismo mensaje forman un frame (todos los dedos en contacto).
/// </summary>
internal static class PointerMessage
{
    public const int EntrySize = 12;
    private const double Scale = 65535.0;

    public static IReadOnlyList<PointerSample> Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty) throw new InvalidDataException("POINTER vacío");
        var count = payload[0];
        if (payload.Length != 1 + count * EntrySize)
        {
            throw new InvalidDataException($"POINTER inválido: {count} punteros en {payload.Length} bytes");
        }

        var samples = new PointerSample[count];
        for (var i = 0; i < count; i++)
        {
            var entry = payload.Slice(1 + i * EntrySize, EntrySize);
            var tool = entry[1];
            var action = entry[2];
            if (tool > (byte)PointerTool.Eraser) throw new InvalidDataException($"Herramienta desconocida: {tool}");
            if (action > (byte)PointerAction.HoverExit) throw new InvalidDataException($"Acción desconocida: {action}");

            samples[i] = new PointerSample(
                entry[0],
                (PointerTool)tool,
                (PointerAction)action,
                (entry[3] & 0x01) != 0,
                BinaryPrimitives.ReadUInt16BigEndian(entry[4..]) / Scale,
                BinaryPrimitives.ReadUInt16BigEndian(entry[6..]) / Scale,
                BinaryPrimitives.ReadUInt16BigEndian(entry[8..]) / Scale,
                (sbyte)entry[10],
                (sbyte)entry[11]);
        }

        return samples;
    }

    /// <summary>Serializa (lo usan los tests y sirve de referencia para el cliente).</summary>
    public static byte[] Write(IReadOnlyList<PointerSample> samples)
    {
        var payload = new byte[1 + samples.Count * EntrySize];
        payload[0] = (byte)samples.Count;
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            var entry = payload.AsSpan(1 + i * EntrySize, EntrySize);
            entry[0] = s.Id;
            entry[1] = (byte)s.Tool;
            entry[2] = (byte)s.Action;
            entry[3] = (byte)(s.BarrelButton ? 1 : 0);
            BinaryPrimitives.WriteUInt16BigEndian(entry[4..], ToUInt16(s.X));
            BinaryPrimitives.WriteUInt16BigEndian(entry[6..], ToUInt16(s.Y));
            BinaryPrimitives.WriteUInt16BigEndian(entry[8..], ToUInt16(s.Pressure));
            entry[10] = (byte)(sbyte)Math.Clamp(s.TiltX, -90, 90);
            entry[11] = (byte)(sbyte)Math.Clamp(s.TiltY, -90, 90);
        }

        return payload;
    }

    private static ushort ToUInt16(double value) => (ushort)Math.Round(Math.Clamp(value, 0, 1) * Scale);
}
