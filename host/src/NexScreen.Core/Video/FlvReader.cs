using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace NexScreen.Core.Video;

/// <summary>
/// Lee H.264 desde un stream FLV (salida de ffmpeg) y lo convierte a access units Annex-B.
/// Usamos FLV porque cada paquete viene con su tamaño explícito: a diferencia de un
/// stream Annex-B crudo, no hay que esperar al siguiente start code para saber dónde
/// termina un frame, así que no se agrega un frame de latencia.
/// </summary>
internal sealed class FlvReader
{
    private const int TagTypeVideo = 9;
    private const int CodecIdAvc = 7;
    private const int AvcSequenceHeader = 0;
    private const int AvcNalu = 1;

    private static readonly byte[] StartCode = [0, 0, 0, 1];

    private readonly Stream _stream;
    private int _nalLengthSize = 4;

    public FlvReader(Stream stream) => _stream = stream;

    public async IAsyncEnumerable<EncodedFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[9];
        if (!await ReadExactAsync(header, ct).ConfigureAwait(false)) yield break;
        if (header[0] != 'F' || header[1] != 'L' || header[2] != 'V')
        {
            throw new InvalidDataException("La salida de ffmpeg no es FLV");
        }

        var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5));
        if (dataOffset > header.Length)
        {
            if (!await ReadExactAsync(new byte[dataOffset - header.Length], ct).ConfigureAwait(false)) yield break;
        }

        // Cada iteración lee PreviousTagSize (4) + header del tag (11).
        var tagHeader = new byte[15];
        while (await ReadExactAsync(tagHeader, ct).ConfigureAwait(false))
        {
            var tagType = tagHeader[4] & 0x1F;
            var dataSize = (tagHeader[5] << 16) | (tagHeader[6] << 8) | tagHeader[7];
            var timestampMs = (long)((tagHeader[11] << 24) | (tagHeader[8] << 16) | (tagHeader[9] << 8) | tagHeader[10]);

            var data = new byte[dataSize];
            if (!await ReadExactAsync(data, ct).ConfigureAwait(false)) yield break;
            if (tagType != TagTypeVideo) continue;

            var frame = ParseVideoTag(data, timestampMs);
            if (frame is not null) yield return frame.Value;
        }
    }

    internal EncodedFrame? ParseVideoTag(ReadOnlySpan<byte> data, long timestampMs)
    {
        if (data.Length < 5) return null;
        if ((data[0] & 0x80) != 0) throw new NotSupportedException("Enhanced FLV no soportado");

        var frameType = (data[0] >> 4) & 0x07;
        var codecId = data[0] & 0x0F;
        if (codecId != CodecIdAvc) throw new NotSupportedException($"Codec FLV {codecId} no soportado");

        var compositionTime = (data[2] << 16) | (data[3] << 8) | data[4];
        if ((compositionTime & 0x800000) != 0) compositionTime |= unchecked((int)0xFF000000);
        var ptsUs = (timestampMs + compositionTime) * 1000;
        var body = data[5..];

        return data[1] switch
        {
            AvcSequenceHeader => new EncodedFrame(ParseDecoderConfiguration(body), ptsUs, FrameFlags.Config),
            AvcNalu => new EncodedFrame(AvccToAnnexB(body), ptsUs, frameType == 1 ? FrameFlags.KeyFrame : FrameFlags.None),
            _ => null,
        };
    }

    /// <summary>AVCDecoderConfigurationRecord (ISO 14496-15) → SPS/PPS en Annex-B.</summary>
    private byte[] ParseDecoderConfiguration(ReadOnlySpan<byte> record)
    {
        if (record.Length < 7) throw new InvalidDataException("AVCDecoderConfigurationRecord truncado");
        _nalLengthSize = (record[4] & 0x03) + 1;

        using var output = new MemoryStream();
        var position = 5;
        var spsCount = record[position++] & 0x1F;
        for (var i = 0; i < spsCount; i++) CopyParameterSet(record, ref position, output);

        if (position >= record.Length) throw new InvalidDataException("AVCDecoderConfigurationRecord sin PPS");
        var ppsCount = record[position++];
        for (var i = 0; i < ppsCount; i++) CopyParameterSet(record, ref position, output);

        return output.ToArray();
    }

    private static void CopyParameterSet(ReadOnlySpan<byte> record, ref int position, MemoryStream output)
    {
        if (position + 2 > record.Length) throw new InvalidDataException("Parameter set truncado");
        var length = BinaryPrimitives.ReadUInt16BigEndian(record[position..]);
        position += 2;
        if (position + length > record.Length) throw new InvalidDataException("Parameter set truncado");

        output.Write(StartCode);
        output.Write(record.Slice(position, length));
        position += length;
    }

    /// <summary>NAL units con prefijo de largo (AVCC) → Annex-B.</summary>
    private byte[] AvccToAnnexB(ReadOnlySpan<byte> avcc)
    {
        using var output = new MemoryStream(avcc.Length + 16);
        var position = 0;
        while (position < avcc.Length)
        {
            if (position + _nalLengthSize > avcc.Length) throw new InvalidDataException("Largo de NAL truncado");

            var length = 0;
            for (var i = 0; i < _nalLengthSize; i++) length = (length << 8) | avcc[position + i];
            position += _nalLengthSize;
            if (length < 0 || position + length > avcc.Length) throw new InvalidDataException("NAL truncado");

            output.Write(StartCode);
            output.Write(avcc.Slice(position, length));
            position += length;
        }

        return output.ToArray();
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var read = await _stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        return read == buffer.Length;
    }
}
