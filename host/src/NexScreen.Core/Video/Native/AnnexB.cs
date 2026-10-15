namespace NexScreen.Core.Video.Native;

/// <summary>Utilidades sobre H.264 en formato Annex-B (start codes 00 00 01 / 00 00 00 01). Lógica pura, con tests.</summary>
internal static class AnnexB
{
    public const int NalSlice = 1;
    public const int NalIdrSlice = 5;
    public const int NalSps = 7;
    public const int NalPps = 8;

    /// <summary>Los NAL units del buffer: tipo, inicio del start code, inicio del payload y fin.</summary>
    public readonly record struct Nal(int Type, int Start, int PayloadStart, int End);

    public static List<Nal> Split(ReadOnlySpan<byte> data)
    {
        var nals = new List<Nal>();
        var current = -1;
        var currentPayload = 0;
        var i = 0;
        while (i + 3 <= data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                // El start code de 4 bytes (00 00 00 01) arranca un byte antes.
                var codeStart = i > 0 && data[i - 1] == 0 ? i - 1 : i;
                if (current >= 0) nals.Add(MakeNal(data, current, currentPayload, codeStart));
                current = codeStart;
                currentPayload = i + 3;
                i += 3;
            }
            else
            {
                i++;
            }
        }

        if (current >= 0) nals.Add(MakeNal(data, current, currentPayload, data.Length));
        return nals;
    }

    private static Nal MakeNal(ReadOnlySpan<byte> data, int start, int payload, int end) =>
        new(payload < data.Length ? data[payload] & 0x1F : 0, start, payload, end);

    /// <summary>True si el access unit trae un slice IDR.</summary>
    public static bool ContainsIdr(ReadOnlySpan<byte> data) => Split(data).Any(n => n.Type == NalIdrSlice);

    /// <summary>SPS y PPS del buffer, con sus start codes (lo que la tablet espera en el cuadro de configuración). Null si no hay.</summary>
    public static byte[]? ExtractParameterSets(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        foreach (var nal in Split(data))
        {
            if (nal.Type is NalSps or NalPps) output.Write(data[nal.Start..nal.End]);
        }

        return output.Length == 0 ? null : output.ToArray();
    }
}
