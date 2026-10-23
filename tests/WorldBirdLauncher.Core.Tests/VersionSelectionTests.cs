using WorldBirdLauncher.Core.Models;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// 直连启动的版本选择规则：不支持跨版本 → 严格同档；支持跨版本 → 交给用户挑（这里是候选列表）。
/// </summary>
public class VersionSelectionTests
{
    private static ServerEntry Entry(string version, string? range = null, params string[] groups) => new()
    {
        Id = "test",
        Name = "测试服",
        Version = version,
        VersionRange = range,
        VersionGroups = groups,
    };

    [Theory]
    [InlineData("1.7.10 (GTNH)", "1.7.10")]
    [InlineData("1.20.1 生存", "1.20.1")]
    [InlineData("26.2", "26.2")]
    public void 版本档会去掉备注(string version, string expected) =>
        Assert.Equal(expected, VersionSelection.TargetBand(Entry(version)));

    [Fact]
    public void 不支持跨版本时只认同档版本()
    {
        var entry = Entry("1.20.x");
        var installed = new[] { "1.20.1", "1.20.4", "1.21.1", "1.19.4", "测试光影" };

        var candidates = VersionSelection.PickStrictCandidates(entry, installed);

        Assert.Equal(["1.20.4", "1.20.1"], candidates); // 高的在前，1.21.1 不算
    }

    [Fact]
    public void 具体版本号时自定义目录名也算同档()
    {
        var entry = Entry("1.7.10");
        var installed = new[] { "1.7.10", "1.7.10光影", "1.7.2", "1.12.2" };

        var candidates = VersionSelection.PickStrictCandidates(entry, installed);

        Assert.Contains("1.7.10", candidates);
        Assert.Contains("1.7.10光影", candidates);
        Assert.DoesNotContain("1.7.2", candidates);
        Assert.DoesNotContain("1.12.2", candidates);
    }

    [Fact]
    public void 支持跨版本时候选按兼容区间取()
    {
        var entry = Entry("1.8.x", "1.8.x ~ 1.19.x");
        var installed = new[] { "1.7.10", "1.12.2", "1.16.4", "1.16.4光明", "1.20.1", "26.1" };

        var candidates = VersionSelection.PickCompatibleCandidates(entry, installed);

        Assert.Equal(["1.16.4", "1.16.4光明", "1.12.2"], candidates);
    }

    [Fact]
    public void 远程推荐版本取同档最高的正式版()
    {
        var remote = new[] { "1.20", "1.20.1", "1.20.4", "1.20.6", "1.21", "1.20.5-pre1" };

        Assert.Equal("1.20.6", VersionSelection.PickRecommendedRemoteVersion("1.20.x", remote));
        Assert.Equal("1.7.10", VersionSelection.PickRecommendedRemoteVersion("1.7.10", ["1.7.2", "1.7.10"]));
        Assert.Null(VersionSelection.PickRecommendedRemoteVersion("1.30.x", remote));
    }

    [Fact]
    public void 跨版本判定()
    {
        Assert.True(VersionSelection.SupportsCrossVersion(Entry("1.8.x", "1.8.x ~ 1.19.x")));
        Assert.False(VersionSelection.SupportsCrossVersion(Entry("1.20.x")));
    }
}
