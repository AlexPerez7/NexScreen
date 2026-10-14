using NexScreen.Core.Update;

namespace NexScreen.Core.Tests;

public class UpdateReleaseTests
{
    private const string Hash = "bb3d59767b74ba8d8db037ea6c185eb5680d121fbdd1bde22f3bc9d660d0500b";

    private static string Release(string tag = "v0.5.0", bool prerelease = false, string assetName = "NexScreen-Setup-0.5.0.exe",
        string digest = "sha256:" + Hash) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/AlexPerez7/NexScreen/releases/tag/{{tag}}",
          "draft": false,
          "prerelease": {{(prerelease ? "true" : "false")}},
          "assets": [
            { "name": "NexScreen-0.5.0.apk", "size": 4781951, "digest": "sha256:00",
              "browser_download_url": "https://github.com/AlexPerez7/NexScreen/releases/download/{{tag}}/NexScreen-0.5.0.apk" },
            { "name": "{{assetName}}", "size": 99808162, "digest": "{{digest}}",
              "browser_download_url": "https://github.com/AlexPerez7/NexScreen/releases/download/{{tag}}/{{assetName}}" }
          ]
        }
        """;

    [Fact]
    public void ParsesInstallerAsset()
    {
        var release = UpdateRelease.Parse(Release());

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 5, 0), release.Version);
        Assert.Equal("NexScreen-Setup-0.5.0.exe", release.InstallerName);
        Assert.Equal(99808162, release.Size);
        Assert.Equal(Hash, release.Sha256);
        Assert.EndsWith("/v0.5.0/NexScreen-Setup-0.5.0.exe", release.InstallerUrl.AbsoluteUri);
    }

    [Fact]
    public void IgnoresPrereleases() => Assert.Null(UpdateRelease.Parse(Release(prerelease: true)));

    [Fact]
    public void IgnoresReleasesWithoutInstaller() => Assert.Null(UpdateRelease.Parse(Release(assetName: "otra-cosa.exe")));

    [Fact]
    public void IgnoresTagsThatAreNotVersions() => Assert.Null(UpdateRelease.Parse(Release(tag: "build-deps")));

    [Fact]
    public void DigestIsOptional() => Assert.Null(UpdateRelease.Parse(Release(digest: "md5:abc"))!.Sha256);

    [Theory]
    [InlineData("v0.4.1", true)]
    [InlineData("v0.10.0", true)]
    [InlineData("v1.0.0", true)]
    [InlineData("v0.4.0", false)]
    [InlineData("v0.3.9", false)]
    public void ComparesWithCurrentVersion(string tag, bool newer)
    {
        var release = UpdateRelease.Parse(Release(tag: tag))!;

        // La versión del ensamblado tiene cuatro componentes (0.4.0.0).
        Assert.Equal(newer, release.IsNewerThan(new Version(0, 4, 0, 0)));
    }

    [Theory]
    [InlineData("v0.5.0", "0.5.0")]
    [InlineData("0.5.0", "0.5.0")]
    [InlineData("v0.5", null)]
    [InlineData("v0.5.0.1", null)]
    [InlineData("v0.5.0-beta", null)]
    public void ParsesTags(string tag, string? expected)
    {
        Assert.Equal(expected, UpdateRelease.ParseTag(tag)?.ToString());
    }
}
