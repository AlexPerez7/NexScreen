using System.Buffers;
using System.Buffers.Binary;
using System.Text.Json;
using NexScreen.Host.Video;

namespace NexScreen.Host.Protocol;

/// <summary>Framing de mensajes sobre un stream: [u8 tipo][u32 largo BE][payload].</summary>
internal sealed class MessageChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _readHeader = new byte[Wire.HeaderSize];

    public MessageChannel(Stream stream) => _stream = stream;

    public Task WriteJsonAsync<T>(MessageType type, T message, CancellationToken ct) =>
        WriteAsync(type, ReadOnlyMemory<byte>.Empty, JsonSerializer.SerializeToUtf8Bytes(message, Wire.Json), ct);

    public Task WriteVideoAsync(EncodedFrame frame, CancellationToken ct)
    {
        var prefix = new byte[Wire.VideoPrefixSize];
        BinaryPrimitives.WriteInt64BigEndian(prefix, frame.PtsUs);
        prefix[8] = (byte)frame.Flags;
        return WriteAsync(MessageType.Video, prefix, frame.Data, ct);
    }

    public Task WriteAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct) =>
        WriteAsync(type, ReadOnlyMemory<byte>.Empty, payload, ct);

    /// <summary>
    /// Escribe header + prefix + body en un solo buffer para que con TCP_NODELAY
    /// salga en la menor cantidad de segmentos posible.
    /// </summary>
    private async Task WriteAsync(MessageType type, ReadOnlyMemory<byte> prefix, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        var payloadLength = prefix.Length + body.Length;
        var total = Wire.HeaderSize + payloadLength;
        var buffer = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            buffer[0] = (byte)type;
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(1), (uint)payloadLength);
            prefix.Span.CopyTo(buffer.AsSpan(Wire.HeaderSize));
            body.Span.CopyTo(buffer.AsSpan(Wire.HeaderSize + prefix.Length));

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _stream.WriteAsync(buffer.AsMemory(0, total), ct).ConfigureAwait(false);
                await _stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Lee el próximo mensaje, o null si el otro extremo cerró la conexión.</summary>
    public async Task<(MessageType Type, byte[] Payload)?> ReadAsync(CancellationToken ct)
    {
        if (!await ReadExactAsync(_readHeader, ct).ConfigureAwait(false)) return null;

        var length = BinaryPrimitives.ReadUInt32BigEndian(_readHeader.AsSpan(1));
        if (length > Wire.MaxPayload) throw new InvalidDataException($"Mensaje demasiado grande: {length} bytes");

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(payload, ct).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Conexión cerrada a mitad de un mensaje");
        }

        return ((MessageType)_readHeader[0], payload);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var read = await _stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct)
            .ConfigureAwait(false);
        if (read == buffer.Length) return true;
        if (read == 0) return false;
        throw new EndOfStreamException("Conexión cerrada a mitad de un mensaje");
    }

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return _stream.DisposeAsync();
    }
}
