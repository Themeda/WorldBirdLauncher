using McServerList.Core.Models;

namespace McServerList.Core.Tests;

public class VersionCompatibilityTests
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
    [InlineData("1.20.1", "1.20.x")]
    [InlineData("26.1", "26.x")]
    [InlineData("1.21.4", "1.21.x")]
    public void 版本分组映射正确(string versionId, string expected) =>
        Assert.Equal(expected, VersionCompatibility.GroupOf(versionId));

    /// <summary>
    /// 直连参数的分水岭：--quickPlayMultiplayer 是 1.20 才有的，更老的版本必须用 --server/--port
    /// （老版本会把 quickPlay 静默忽略，表现为"点了启动但没进服"）。
    /// </summary>
    [Theory]
    [InlineData("1.21.1", true)]
    [InlineData("1.20", true)]
    [InlineData("26.1", true)]              // 26.x 命名也是新版
    [InlineData("1.19.4", false)]
    [InlineData("1.19.4-OptiFine_HD_U_I4", false)]
    [InlineData("1.12.2-Forge_14.23.5.2859", false)]
    [InlineData("1.7.10", false)]
    [InlineData("我的整合包", true)]            // 解析不出游戏版本时按新版（保持原行为）
    public void 是否支持quickPlay按游戏版本判断(string versionId, bool expected) =>
        Assert.Equal(expected, VersionCompatibility.SupportsQuickPlay(versionId));

    [Fact]
    public void 同分组视为匹配()
    {
        var entry = Entry("1.20.x", null, "1.20.x");
        Assert.True(VersionCompatibility.IsCompatible(entry, "1.20.4"));
        Assert.False(VersionCompatibility.IsCompatible(entry, "1.21.4"));
    }

    [Fact]
    public void 跨版本区间内的版本都算匹配()
    {
        var entry = Entry("1.8.x", "1.8.x ~ 1.19.x");

        Assert.True(VersionCompatibility.IsCompatible(entry, "1.8.9"));
        Assert.True(VersionCompatibility.IsCompatible(entry, "1.12.2"));
        Assert.True(VersionCompatibility.IsCompatible(entry, "1.19.2"));
        Assert.False(VersionCompatibility.IsCompatible(entry, "1.20.1"));
        Assert.False(VersionCompatibility.IsCompatible(entry, "26.1"));
    }

    [Fact]
    public void 新版本号方案不会与旧版本互串()
    {
        var entry = Entry("26.2", "1.12.2 ~ 26.1");

        Assert.True(VersionCompatibility.IsCompatible(entry, "26.1"));
        Assert.False(VersionCompatibility.IsCompatible(entry, "26.2"));
        Assert.True(VersionCompatibility.IsCompatible(entry, "1.20.1"));
    }

    [Fact]
    public void 未选择版本时不拦截()
    {
        var entry = Entry("1.20.x", null, "1.20.x");
        Assert.True(VersionCompatibility.IsCompatible(entry, null));
    }

    /// <summary>
    /// 2026-10-08 修：编目里 19 台服务器（Hypixel / Wynncraft / CubeCraft / PikaNetwork …）的跨版本区间
    /// 只写在 <c>version</c> 文案里（"1.8 – 26.x"），<c>versionRange</c> 是空的。
    /// 以前会被当成"严格版本"服务器，拿文案里的"1.8"当目标档 → 本机装着 1.18.2 也要你下 1.8。
    /// </summary>
    [Theory]
    [InlineData("1.8 – 26.x", true)]
    [InlineData("1.20 – 1.21", true)]
    [InlineData("1.7~1.20", true)]
    [InlineData("1.8 - 1.21", true)]
    [InlineData("1.20.1", false)]
    [InlineData("1.20.1-生存", false)]      // 连字符后缀不算区间
    [InlineData("1.16.4-OptiFine_HD_U_G7", false)]
    public void 只写在版本文案里的区间也算跨版本(string version, bool expected) =>
        Assert.Equal(expected, VersionSelection.SupportsCrossVersion(Entry(version)));

    [Fact]
    public void 区间文案的目标档取下界()
    {
        Assert.Equal("1.8", VersionSelection.TargetBand(Entry("1.8 – 26.x")));
        Assert.Equal("1.20", VersionSelection.TargetBand(Entry("1.20 – 1.21")));
        Assert.Equal("1.20.1", VersionSelection.TargetBand(Entry("1.20.1 (GTNH)")));
    }

    [Fact]
    public void 版本文案区间的服务器弹窗给已装且兼容的版本()
    {
        // PikaNetwork 场景：声明 1.8 – 26.x、本机装着这些
        var entry = Entry("1.8 – 26.x", null,
            "1.8.x", "1.12.x", "1.18.x", "1.20.x", "1.21.x", "26.x");
        var installed = new[] { "1.12.2", "1.18.2 (2)", "1.20.1-fabric-loader_0.19.5", "26.1" };

        Assert.True(VersionSelection.SupportsCrossVersion(entry));
        Assert.Equal(
            ["26.1", "1.20.1-fabric-loader_0.19.5", "1.18.2 (2)", "1.12.2"],
            VersionSelection.PickCompatibleCandidates(entry, installed));
    }
}
