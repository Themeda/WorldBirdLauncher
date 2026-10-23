using System.Text.Json;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.Core.Tests;

/// <summary>
/// Java 检测的回归测试（2026-10-06 自研替换 StarLight 的 JavaUtil 时踩过的坑）：
/// ① 剪枝规则里绝不能出现 `bin`——javaw.exe 就在 &lt;JavaHome&gt;\bin 里，剪掉它一个 Java 都扫不到；
/// ② 同一个 bin 目录里 java.exe / javaw.exe 并存时只算一个，且优先用 javaw.exe（不弹控制台）；
/// ③ 深度扫描的结果要"记得住"（java-scan.json），否则 D:\java 这类自定义目录下次就丢了。
/// </summary>
public class JavaDetectionTests
{
    [Fact]
    public void 扫目录树时能进入JavaHome的bin目录()
    {
        var root = NewTempRoot();
        try
        {
            var javaHome = Path.Combine(root, "myjava");
            Directory.CreateDirectory(Path.Combine(javaHome, "bin"));
            File.WriteAllText(Path.Combine(javaHome, "bin", "javaw.exe"), string.Empty);

            var found = JavaRuntimeService.ScanForJavaExecutables(root, maxDepth: 5);

            Assert.Equal([Path.Combine(javaHome, "bin", "javaw.exe")], found);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 同一个bin里java和javaw并存时只取javaw()
    {
        var root = NewTempRoot();
        try
        {
            var bin = Path.Combine(root, "jdk-8", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, "java.exe"), string.Empty);
            File.WriteAllText(Path.Combine(bin, "javaw.exe"), string.Empty);
            File.WriteAllText(Path.Combine(bin, "javac.exe"), string.Empty);

            var found = JavaRuntimeService.ScanForJavaExecutables(root, maxDepth: 5, CancellationToken.None);

            Assert.Single(found);
            Assert.EndsWith("javaw.exe", found[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 扫不存在的目录不抛异常也不返回结果() =>
        Assert.Empty(JavaRuntimeService.ScanForJavaExecutables(
            Path.Combine(Path.GetTempPath(), "mcsl-not-exist-" + Guid.NewGuid().ToString("N")),
            maxDepth: 3));

    [Fact]
    public void 快扫会带上上次深度扫描记住的Java()
    {
        var root = NewTempRoot();
        try
        {
            // 布局：root\runtime（本项目的 Java 安装目录）+ root\custom\java\bin\javaw.exe
            var javaExe = WriteFakeJava(Path.Combine(root, "custom", "java"), "1.8.0_281", "amd64");
            var service = new JavaRuntimeService(Path.Combine(root, "runtime"));
            WriteScanCache(service.JavaScanCachePath, javaExe);

            var detected = service.Detect(deepSearch: false);

            var remembered = Assert.Single(detected, j =>
                string.Equals(j.JavaPath, javaExe, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(8, remembered.MajorVersion);
            Assert.True(remembered.Is64Bit);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void 记忆里已经不存在的Java会被丢掉()
    {
        var root = NewTempRoot();
        try
        {
            var service = new JavaRuntimeService(Path.Combine(root, "runtime"));
            WriteScanCache(service.JavaScanCachePath, Path.Combine(root, "gone", "bin", "javaw.exe"));

            var detected = service.Detect(deepSearch: false);

            Assert.DoesNotContain(detected, j => j.JavaPath.Contains("gone", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcsl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>造一个只有 release 描述文件的假 Java（Detect 读 release，不会真的启动它）。</summary>
    private static string WriteFakeJava(string javaHome, string version, string arch)
    {
        var bin = Path.Combine(javaHome, "bin");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "javaw.exe");
        File.WriteAllText(exe, string.Empty);
        File.WriteAllText(
            Path.Combine(javaHome, "release"),
            $"JAVA_VERSION=\"{version}\"{Environment.NewLine}OS_ARCH=\"{arch}\"{Environment.NewLine}");
        return exe;
    }

    private static void WriteScanCache(string cachePath, params string[] paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        File.WriteAllText(
            cachePath,
            JsonSerializer.Serialize(new { UpdatedUtc = DateTimeOffset.UtcNow.ToString("O"), Paths = paths }));
    }
}
