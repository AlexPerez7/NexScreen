using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace NexScreen.Core.Update;

/// <summary>Consulta GitHub Releases y descarga el instalador de la versión nueva.</summary>
internal sealed class UpdateClient : IDisposable
{
    public const string LatestReleaseUrl = "https://api.github.com/repos/AlexPerez7/NexScreen/releases/latest";

    private readonly HttpClient _http;

    public UpdateClient(string appVersion)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // La API de GitHub rechaza los pedidos sin User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NexScreen", appVersion));
    }

    /// <summary>El último release publicado, o null si no hay ninguno con instalador.</summary>
    public async Task<UpdateRelease?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return UpdateRelease.Parse(json);
    }

    /// <summary>
    /// Descarga el instalador en <paramref name="directory"/> y verifica tamaño y SHA-256. Si ya
    /// estaba descargado y es válido, no lo vuelve a bajar. Devuelve la ruta del archivo.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateRelease release, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.InstallerName);
        if (File.Exists(path) && await IsValidAsync(path, release, ct).ConfigureAwait(false)) return path;

        var partial = path + ".part";
        using (var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(partial);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        if (!await IsValidAsync(partial, release, ct).ConfigureAwait(false))
        {
            File.Delete(partial);
            throw new InvalidDataException("El instalador descargado no coincide con el publicado (tamaño o SHA-256).");
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    private static async Task<bool> IsValidAsync(string path, UpdateRelease release, CancellationToken ct)
    {
        if (release.Size > 0 && new FileInfo(path).Length != release.Size) return false;
        if (release.Sha256 is null) return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant() == release.Sha256;
    }

    public void Dispose() => _http.Dispose();
}
