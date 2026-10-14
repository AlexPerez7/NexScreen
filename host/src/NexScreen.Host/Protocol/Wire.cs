using System.Text.Json;

namespace NexScreen.Host.Protocol;

/// <summary>
/// Constantes del protocolo. Ver docs/protocol.md; cualquier cambio debe reflejarse
/// también en el cliente Android (Protocol.kt).
/// </summary>
internal static class Wire
{
    public const int Version = 1;
    public const int DefaultPort = 27190;

    /// <summary>[u8 tipo][u32 largo big-endian]</summary>
    public const int HeaderSize = 5;

    /// <summary>[u64 ptsUs][u8 flags] antes del access unit Annex-B.</summary>
    public const int VideoPrefixSize = 9;

    public const int MaxPayload = 16 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

internal enum MessageType : byte
{
    Hello = 0x01,
    Config = 0x02,
    Video = 0x10,
    Ping = 0x30,
    Pong = 0x31,
}

/// <summary>Cliente → host, primer mensaje de la sesión.</summary>
internal sealed record HelloMessage(int Protocol, string Device, int Width, int Height, int Dpi);

/// <summary>Host → cliente, respuesta al Hello. El tamaño real del video lo informa el decoder.</summary>
internal sealed record ConfigMessage(int Protocol, string Codec, int MaxWidth, int MaxHeight, int Fps);
