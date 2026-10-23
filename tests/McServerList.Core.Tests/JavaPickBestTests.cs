using McServerList.Core.Launcher;

namespace McServerList.Core.Tests;

/// <summary>
/// Java 选择的回归（2026-10-08 用户要求）：
/// 1.13 之前的老版本（LWJGL2）只能用 Java 8；其余版本允许用更高的 Java，
/// 且优先"正好那版"，没有就用"≥ 要求里最小的"——本机没 Java 17 但有 21 时直接用 21，不再白下一份。
/// </summary>
public class JavaPickBestTests
{
    private static JavaRuntime Java(int major) => new(
        JavaPath: $@"C:\java\{major}\bin\javaw.exe",
        LibraryPath: $@"C:\java\{major}\bin",
        MajorVersion: major,
        Version: $"{major}.0.1",
        Is64Bit: true);

    [Theory]
    [InlineData("1.7.10", false)]
    [InlineData("1.7.10-Forge_10.13.4.1614", false)]
    [InlineData("1.12.2", false)]
    [InlineData("1.12.2-Forge_14.23.5.2859", false)]
    [InlineData("1.13", true)]
    [InlineData("1.16.4", true)]
    [InlineData("1.18.2", true)]
    [InlineData("1.20.1-fabric-loader_0.19.5", true)]
    [InlineData("26.1", true)]
    [InlineData("自定义版本名", true)]
    public void 老版本只认Java8(string versionId, bool allowsNewer) =>
        Assert.Equal(allowsNewer, JavaRuntimeService.AllowsNewerJava(versionId));

    [Fact]
    public void 老版本只会挑Java8()
    {
        var runtimes = new[] { Java(8), Java(17), Java(21), Java(25) };

        Assert.Equal(8, JavaRuntimeService.PickBest(runtimes, "1.12.2", requiredMajor: 8)!.MajorVersion);
        // 没有 Java 8 就不挑（调用方据此提示 / 下载 Java 8），而不是拿 Java 21 去跑 1.12.2
        Assert.Null(JavaRuntimeService.PickBest([Java(17), Java(21)], "1.12.2", requiredMajor: 8));
    }

    [Fact]
    public void 正好等于要求的优先()
    {
        var runtimes = new[] { Java(8), Java(17), Java(21), Java(25) };

        Assert.Equal(17, JavaRuntimeService.PickBest(runtimes, "1.18.2", requiredMajor: 17)!.MajorVersion);
        Assert.Equal(21, JavaRuntimeService.PickBest(runtimes, "1.21.1", requiredMajor: 21)!.MajorVersion);
        Assert.Equal(25, JavaRuntimeService.PickBest(runtimes, "26.1", requiredMajor: 25)!.MajorVersion);
    }

    [Fact]
    public void 没有正好那版就用最接近的更高版_不必再下载()
    {
        // 用户场景：本机没有 Java 17，只有 Java 21 → 1.18.2 / 1.19.4 直接用 21
        var runtimes = new[] { Java(8), Java(21) };

        Assert.Equal(21, JavaRuntimeService.PickBest(runtimes, "1.18.2", requiredMajor: 17)!.MajorVersion);
        Assert.Equal(21, JavaRuntimeService.PickBest(runtimes, "1.19.4", requiredMajor: 17)!.MajorVersion);
        // 21 与 25 都在时取更接近要求的 21
        Assert.Equal(21, JavaRuntimeService.PickBest([Java(21), Java(25)], "1.20.1", requiredMajor: 17)!.MajorVersion);
    }

    [Fact]
    public void 一个都不满足时返回空()
    {
        var runtimes = new[] { Java(8), Java(17) };

        Assert.Null(JavaRuntimeService.PickBest(runtimes, "26.1", requiredMajor: 25));
        Assert.Null(JavaRuntimeService.PickBest([], "1.18.2", requiredMajor: 17));
    }
}
