using System.Text.Json;

namespace NexScreen.Core.Protocol;

/// <summary>
/// Constantes del protocolo. Ver docs/protocol.md; cualquier cambio debe reflejarse
/// también en el cliente Android (Protocol.kt).
/// </summary>
internal static class Wire
{
    public const int Version = 2;
    public const int DefaultPort = 27190;

    /// <summary>UDP: la tablet busca hosts en la red con un broadcast a este puerto.</summary>
    public const int DiscoveryPort = 27191;

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

    /// <summary>Host → tablet (solo WiFi): la tablet no está emparejada; hay que pedir el PIN.</summary>
    PairRequired = 0x03,

    /// <summary>Tablet → host: prueba de que el usuario ingresó el PIN que muestra el PC.</summary>
    Pair = 0x04,

    /// <summary>Host → tablet: resultado de un Pair.</summary>
    PairResult = 0x05,
    Video = 0x10,

    /// <summary>Tablet → host: dedos y lápiz. Ver PointerMessage.</summary>
    Pointer = 0x20,
    Ping = 0x30,
    Pong = 0x31,
}

/// <summary>
/// Cliente → host, primer mensaje de la sesión. <see cref="Paired"/>: la tablet ya confía en el
/// certificado TLS de este host (por WiFi; por USB se ignora).
/// </summary>
internal sealed record HelloMessage(int Protocol, string Device, int Width, int Height, int Dpi, bool Paired = false);

/// <summary>Host → cliente, respuesta al Hello. El tamaño real del video lo informa el decoder.</summary>
internal sealed record ConfigMessage(int Protocol, string Codec, int MaxWidth, int MaxHeight, int Fps);

/// <summary>Host → tablet: hay que emparejar. <see cref="Host"/> es el nombre del PC.</summary>
internal sealed record PairRequiredMessage(string Host);

/// <summary>Tablet → host: HMAC del PIN (ver <c>Pairing.ComputeProof</c>), en hexadecimal.</summary>
internal sealed record PairMessage(string Proof);

/// <summary>Host → tablet. Si no es <see cref="Ok"/> y <see cref="Retry"/>, la tablet puede volver a pedir el PIN.</summary>
internal sealed record PairResultMessage(bool Ok, string? Error = null, bool Retry = false);
