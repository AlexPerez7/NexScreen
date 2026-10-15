namespace NexScreen.Core.Video.Native;

/// <summary>
/// Convierte las formas de puntero de Desktop Duplication a BGRA con alfa para dibujarlas encima de
/// la imagen (la duplicación entrega el escritorio sin el cursor). Lógica pura, con tests.
/// </summary>
internal static class CursorImage
{
    public const int Monochrome = 1;
    public const int Color = 2;
    public const int MaskedColor = 4;

    /// <summary>BGRA sin premultiplicar. <paramref name="height"/> es el de la forma tal como la informa DXGI.</summary>
    public static (int Width, int Height, byte[] Bgra) ToBgra(int type, int width, int height, int pitch, ReadOnlySpan<byte> shape)
    {
        switch (type)
        {
            case Color:
                return (width, height, CopyRows(width, height, pitch, shape));
            case MaskedColor:
                return (width, height, MaskedToBgra(width, height, pitch, shape));
            case Monochrome:
                // Mide el doble: arriba la máscara AND y abajo la XOR.
                return (width, height / 2, MonochromeToBgra(width, height / 2, pitch, shape));
            default:
                throw new NotSupportedException($"Tipo de puntero {type} no soportado");
        }
    }

    private static byte[] CopyRows(int width, int height, int pitch, ReadOnlySpan<byte> shape)
    {
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++) shape.Slice(y * pitch, width * 4).CopyTo(output.AsSpan(y * width * 4));
        return output;
    }

    /// <summary>Alfa 0 = pixel opaco con ese color; alfa 255 = XOR con la pantalla (se aproxima como transparente).</summary>
    private static byte[] MaskedToBgra(int width, int height, int pitch, ReadOnlySpan<byte> shape)
    {
        var output = CopyRows(width, height, pitch, shape);
        for (var i = 0; i < output.Length; i += 4) output[i + 3] = output[i + 3] == 0 ? (byte)255 : (byte)0;
        return output;
    }

    private static byte[] MonochromeToBgra(int width, int height, int pitch, ReadOnlySpan<byte> shape)
    {
        var output = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var mask = 0x80 >> (x % 8);
                var and = (shape[y * pitch + x / 8] & mask) != 0;
                var xor = (shape[(y + height) * pitch + x / 8] & mask) != 0;
                var offset = (y * width + x) * 4;

                // AND=0: el pixel lo define XOR (negro o blanco, opaco). AND=1 y XOR=0: transparente.
                // AND=1 y XOR=1 invierte la pantalla; se aproxima con blanco opaco.
                var opaque = !and || xor;
                var value = xor ? (byte)255 : (byte)0;
                output[offset] = output[offset + 1] = output[offset + 2] = value;
                output[offset + 3] = opaque ? (byte)255 : (byte)0;
            }
        }

        return output;
    }
}
