using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// 加载器 × 游戏版本 × Java 的适配拦截（2026-10-08 用户要求"做实际的界面拦截"）。
/// 阈值来自官方 meta 的 profile JSON 采样：loader 自带的 ASM 决定了它能读多新的类文件
/// （ASM 9.6 → Java 22、9.7.1 → 24、9.8 → 25），实测 0.16.9 + Java 25 会
/// `Unsupported class file major version 69` → `MixinPreProcessorException ... from mod iris`。
/// </summary>
public class LoaderCompatibilityTests
{
    [Theory]
    [InlineData("0.14.25", 22)]
    [InlineData("0.15.11", 22)]
    [InlineData("0.16.5", 22)]
    [InlineData("0.16.9", 24)]
    [InlineData("0.17.0", 25)]
    [InlineData("0.19.5", 25)]
    public void Fabric的最高可用Java(string loaderVersion, int expected) =>
        Assert.Equal(expected, LoaderCompatibility.MaxSupportedJava("Fabric", loaderVersion));

    [Theory]
    [InlineData("0.20.0", 21)]
    [InlineData("0.24.0", 22)]
    public void Quilt的最高可用Java(string loaderVersion, int expected) =>
        Assert.Equal(expected, LoaderCompatibility.MaxSupportedJava("Quilt", loaderVersion));

    [Fact]
    public void 旧Fabric配新Java会被拦下()
    {
        // 用户上次踩的那次：1.20.1 + Fabric 0.16.9 + Java 25
        var check = LoaderCompatibility.Check("Fabric", "0.16.9", "1.20.1", javaMajor: 25);
        Assert.False(check.Ok);
        Assert.Contains("Java 24", check.Reason!);

        // 同一套组合换成 Java 21 就正常
        Assert.True(LoaderCompatibility.Check("Fabric", "0.16.9", "1.20.1", javaMajor: 21).Ok);
        // 新 loader + Java 25 正常（应用现在的默认组合）
        Assert.True(LoaderCompatibility.Check("Fabric", "0.19.5", "1.20.1", javaMajor: 25).Ok);
    }

    [Fact]
    public void 老Forge只认Java8()
    {
        Assert.False(LoaderCompatibility.Check("Forge", "14.23.5.2859", "1.12.2", javaMajor: 17).Ok);
        Assert.True(LoaderCompatibility.Check("Forge", "14.23.5.2859", "1.12.2", javaMajor: 8).Ok);
        // 1.13 之后的 Forge 不受这条限制
        Assert.True(LoaderCompatibility.Check("Forge", "47.2.0", "1.20.1", javaMajor: 21).Ok);
    }

    [Fact]
    public void NeoForge至少Java17()
    {
        Assert.False(LoaderCompatibility.Check("NeoForge", "21.1.255", "1.21.1", javaMajor: 8).Ok);
        Assert.True(LoaderCompatibility.Check("NeoForge", "21.1.255", "1.21.1", javaMajor: 21).Ok);
    }

    [Fact]
    public void 原版不安装时不拦()
    {
        Assert.True(LoaderCompatibility.Check("不安装", null, "1.20.1", javaMajor: 25).Ok);
        Assert.True(LoaderCompatibility.Check("原版", "随便", "1.20.1", javaMajor: 25).Ok);
    }
}
