using LiveSub.Core;

namespace LiveSub.Core.Tests;

public class UpdatesTests
{
    [Fact]
    public void VersionComparison()
    {
        Assert.Equal([0, 1, 2], AppVersion.Parse("windows-0.1.2"));
        Assert.Equal([1, 10, 0], AppVersion.Parse("v1.10"));
        Assert.Equal([0, 1, 0], AppVersion.Parse("0.1.0+abc123"));
        Assert.True(AppVersion.IsNewer("0.1.10", "0.1.9"));
        Assert.True(AppVersion.IsNewer("0.2.0", "0.1.99"));
        Assert.False(AppVersion.IsNewer("0.1.2", "0.1.2"));
        Assert.False(AppVersion.IsNewer("0.1.1", "0.1.2"));
    }

    private const string Feed = """
    [
     {"tag_name":"macos-0.1.2","assets":[{"name":"LiveSub-0.1.2-macOS.zip","browser_download_url":"https://x/m.zip","size":1}]},
     {"tag_name":"windows-0.1.0","body":"old","assets":[]},
     {"tag_name":"windows-0.1.1","body":" new things ","html_url":"https://x/page","assets":[
        {"name":"LiveSub-0.1.1-Windows-x64.zip","browser_download_url":"https://x/a.zip","size":2},
        {"name":"LiveSub-0.1.1-Windows-x64-vulkan.zip","browser_download_url":"https://x/v.zip","size":3}]},
     {"tag_name":"windows-0.1.10","draft":true,"assets":[]},
     {"tag_name":"windows-0.1.3","prerelease":true,"assets":[]}
    ]
    """;

    [Fact]
    public void PicksNewestReleaseOfThisPlatformAndVariant()
    {
        var cpu = ReleaseFeed.Latest(Feed, "windows-", "-Windows-x64.zip");
        Assert.Equal("0.1.1", cpu?.Version);
        Assert.Equal("https://x/a.zip", cpu?.AssetUrl);
        Assert.Equal("new things", cpu?.Notes);
        Assert.Equal("https://x/v.zip", ReleaseFeed.Latest(Feed, "windows-", "-Windows-x64-vulkan.zip")?.AssetUrl);
        Assert.Equal("0.1.2", ReleaseFeed.Latest(Feed, "macos-", "-macOS.zip")?.Version);
        Assert.Null(ReleaseFeed.Latest("nope", "windows-", ".zip"));
    }

    [Fact]
    public void OfferRespectsSkippedVersion()
    {
        var r = new ReleaseInfo("0.1.3", "", null, 0, null);
        Assert.True(UpdatePolicy.ShouldOffer(r, "0.1.2", ""));
        Assert.False(UpdatePolicy.ShouldOffer(r, "0.1.2", "0.1.3"));
        Assert.True(UpdatePolicy.ShouldOffer(r, "0.1.2", "0.1.2"));
        Assert.False(UpdatePolicy.ShouldOffer(r, "0.1.3", ""));
    }

    [Fact]
    public void CheckSchedule()
    {
        var now = DateTime.Now;
        Assert.True(UpdatePolicy.CheckDue(now, null, null));
        Assert.False(UpdatePolicy.CheckDue(now, now.AddHours(-1), now.AddHours(-1)));
        Assert.True(UpdatePolicy.CheckDue(now, now.AddHours(-25), now.AddHours(-25)));
        Assert.False(UpdatePolicy.CheckDue(now, now.AddHours(-1), now.AddMinutes(-10)));
        Assert.True(UpdatePolicy.CheckDue(now, now.AddHours(-1), now.AddMinutes(-40)));
    }
}
