using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexScreen.Core.Update;

/// <summary>Una versión publicada en GitHub Releases con su instalador de Windows.</summary>
internal sealed record UpdateRelease(Version Version, Uri InstallerUrl, string InstallerName, long Size, string? Sha256, Uri PageUrl)
{
    private static readonly Regex InstallerAsset = new(@"^NexScreen-Setup-\d+\.\d+\.\d+\.exe$", RegexOptions.IgnoreCase);

    public bool IsNewerThan(Version current) => Version > Normalize(current);

    /// <summary>
    /// Lee la respuesta de <c>/releases/latest</c>. Devuelve null si el release no trae instalador,
    /// es un borrador o pre-release, o su tag no es una versión (vX.Y.Z).
    /// </summary>
    public static UpdateRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (GetBool(root, "draft") || GetBool(root, "prerelease")) return null;
        if (ParseTag(GetString(root, "tag_name")) is not { } version) return null;
        if (!Uri.TryCreate(GetString(root, "html_url"), UriKind.Absolute, out var page)) return null;
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            if (name is null || !InstallerAsset.IsMatch(name)) continue;
            if (!Uri.TryCreate(GetString(asset, "browser_download_url"), UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps) continue;

            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new UpdateRelease(version, url, name, size, ParseDigest(GetString(asset, "digest")), page);
        }

        return null;
    }

    /// <summary>"v0.5.0" o "0.5.0" → 0.5.0 (siempre con tres componentes).</summary>
    public static Version? ParseTag(string? tag)
    {
        if (tag is null) return null;
        var text = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        return Version.TryParse(text, out var version) && version.Build >= 0 && version.Revision < 0
            ? version
            : null;
    }

    /// <summary>GitHub informa el hash como "sha256:&lt;hex&gt;". Otros algoritmos se ignoran.</summary>
    private static string? ParseDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
