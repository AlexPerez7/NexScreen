using System.Security.Cryptography;
using System.Text;

namespace NexScreen.Core.Security;

/// <summary>
/// Emparejamiento por PIN. La tablet demuestra que conoce el PIN que muestra el PC enviando
/// HMAC-SHA256(PIN, contexto + huella del host + huella de la tablet). Las huellas atan la prueba
/// a esta conexión TLS: no sirve para otra. Ver docs/protocol.md.
/// </summary>
internal static class Pairing
{
    public const int PinLength = 6;
    public const int MaxAttempts = 3;
    public static readonly TimeSpan PinLifetime = TimeSpan.FromMinutes(2);

    private const string Context = "NexScreen pairing v2";

    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    /// <summary>"482 913" para mostrar.</summary>
    public static string Format(string pin) => $"{pin[..3]} {pin[3..]}";

    public static string ComputeProof(string pin, string hostFingerprint, string clientFingerprint)
    {
        var message = Encoding.UTF8.GetBytes($"{Context}\n{hostFingerprint}\n{clientFingerprint}");
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(pin), message)).ToLowerInvariant();
    }

    public static bool VerifyProof(string pin, string hostFingerprint, string clientFingerprint, string? proof)
    {
        if (proof is null) return false;
        var expected = Encoding.ASCII.GetBytes(ComputeProof(pin, hostFingerprint, clientFingerprint));
        var received = Encoding.ASCII.GetBytes(proof.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
