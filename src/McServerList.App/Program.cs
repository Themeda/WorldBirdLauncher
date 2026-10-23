#if NET48
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace McServerList.App;

/// <summary>
/// net48「小体积单文件」版的入口。
///
/// net48 没有 .NET 8 那种自包含单文件发布，所以这里自己实现（对应阶段 3）：
/// ① 托管依赖（Core/Shared/System.Text.Json/WebView2 托管部分/DnsClient…）全部**压缩后附加在 exe 文件尾部**，
///    运行时用 <see cref="AppDomain.AssemblyResolve"/> 从载荷里按名字加载；
/// ② 原生 `WebView2Loader.dll` 不能从内存加载，首次启动时解压到
///    `%LOCALAPPDATA%\McServerList\native\&lt;版本&gt;` 并 `SetDllDirectory` 指过去；
/// ③ 绑定重定向（.exe.config）也不需要了——解析器按"简单名"返回嵌入的那份程序集，任何版本请求都能满足。
///
/// 载荷布局：`[exe 原始字节][zip 载荷][8 字节长度(小端)][8 字节魔数 "MCSLPAY1"]`。
/// 这个入口只在 net48 上启用（见 csproj 的 StartupObject）；net8 那条路仍是官方自包含单文件。
/// </summary>
internal static class Program
{
    private const string PayloadMagic = "MCSLPAY1";
    private const int FooterSize = 16;

    private static readonly object Gate = new();
    /// <summary>简单名 → 已经加载出来的程序集（重复请求直接复用，别重复 <see cref="Assembly.Load(byte[])"/>）。</summary>
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> RequestCounts = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, byte[]>? _entries;
    private static byte[]? _payload;
    private static bool _loading;
    private static int _resolveRequests;

    [STAThread]
    public static void Main()
    {
        // ★ 关键（踩过的坑）：Main 里**只能**做"挂解析器 + 调 Start"，绝不能直接引用 App/Core/Shared 的类型。
        // 因为 JIT 编译 Main 时就会去解析它引用的类型：一旦引用 App.Main()，CLR 会在**解析器注册之前**
        // 加载 McServerList.Shared → FileNotFoundException（"未能加载文件或程序集 McServerList.Shared"）。
        // 把真正的启动逻辑放到另一个方法里，它的 JIT 发生在解析器挂好之后。
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        Start();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Start()
    {
        // 递归风险由 ReadEntry 里的 _loading / Resolved 两道保护挡住（否则读载荷时会栈溢出 0xC00000FD）
        try
        {
            PrepareNativeLibraries();
        }
        catch (Exception ex)
        {
            // 原生库准备失败不阻断启动：WebView2 那条路会自己报错，其余功能照常
            Log("准备原生库失败：" + ex);
        }

        App.Main();
    }

    /// <summary>托管依赖的解析：从 exe 尾部的载荷里按"简单名.dll"取。</summary>
    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        Assembly? cached;
        lock (Gate)
        {
            // ★ 关键（踩过的坑）：这里**不能**"同一个简单名只解析一次、之后一律返回 null"。
            // 同一个简单名会有**多个版本**的引用（实测：Unsafe 4.0.4.1 与 6.0.0.0；
            // System.Text.Json 9.0.0.0 与 8.0.0.5）。第一次解析成功后如果不再应答，
            // 第二次请求（另一个版本）就会直接 FileNotFoundException —— 因为没有 .exe.config 的绑定重定向。
            // 新版做法：按简单名缓存住已经加载出来的那份，任何版本的请求都复用同一个实例
            // （等价于 config 里的 bindingRedirect；.NET Framework 会接受解析器返回的这份程序集）。
            if (Loaded.TryGetValue(name, out cached))
            {
                if (_resolveRequests++ < 200)
                {
                    Log($"解析程序集：{args.Name} → 复用已加载的 {cached.GetName().Version}");
                }

                return cached;
            }

            // 防呆：万一某个名字被反复请求（比如返回的程序集被拒），到上限就放手，别把进程转死
            RequestCounts.TryGetValue(name, out var count);
            RequestCounts[name] = ++count;
            if (count > 10)
            {
                Log($"解析程序集：{args.Name} 请求次数过多（{count}），放弃");
                return null;
            }

            if (_resolveRequests++ < 200)
            {
                Log("解析程序集：" + args.Name);
            }
        }

        var bytes = ReadEntry(name + ".dll");
        if (bytes is null)
        {
            return null;
        }

        try
        {
            var loaded = Assembly.Load(bytes);
            var loadedName = loaded.GetName();
            if (string.Equals(loadedName.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                lock (Gate)
                {
                    Loaded[name] = loaded;
                }

                return loaded;
            }

            Log($"解析程序集：{name} → 载荷里那份名字对不上（{loadedName.Name}），交给默认加载");
            return null;
        }
        catch (Exception ex)
        {
            Log($"解析程序集 {name} 失败：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>把单文件入口的关键步骤写进 `%LOCALAPPDATA%\McServerList\singlefile.log`（排查"打不开"用）。</summary>
    private static void Log(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "McServerList");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "singlefile.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception)
        {
            // 记日志失败不影响启动
        }
    }

    /// <summary>把原生 WebView2Loader.dll 解到用户目录并让 Windows 的 DLL 搜索能找到它。</summary>
    private static void PrepareNativeLibraries()
    {
        const string nativeName = "WebView2Loader.dll";
        var bytes = ReadEntry(nativeName);
        if (bytes is null)
        {
            Log("载荷里没有 " + nativeName + "（可能是开发构建，dll 本来就在 exe 旁边）");
            return; // 没有载荷（开发时的普通构建）：dll 本来就在 exe 旁边
        }

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "McServerList", "native", version);
        Directory.CreateDirectory(directory);

        var target = Path.Combine(directory, nativeName);
        if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length)
        {
            File.WriteAllBytes(target, bytes);
        }

        Log($"已释放原生库：{target}（{bytes.Length} 字节），SetDllDirectory={SetDllDirectory(directory)}");
    }

    private static byte[]? ReadEntry(string fileName)
    {
        lock (Gate)
        {
            if (_entries is null)
            {
                if (_loading)
                {
                    return null; // 读载荷过程中的程序集请求一律不处理，交给默认加载
                }

                _loading = true;
                try
                {
                    _entries = LoadPayload() ?? [];
                    Log($"载荷读取完成：{_entries.Count} 个文件");
                }
                finally
                {
                    _loading = false;
                }
            }

            return _entries.TryGetValue(fileName, out var bytes) ? bytes : null;
        }
    }

    /// <summary>读出 exe 尾部的 zip 载荷（没有载荷就返回 null）。</summary>
    private static Dictionary<string, byte[]>? LoadPayload()
    {
        Log("载荷：读取开始");
        var exePath = Assembly.GetExecutingAssembly().Location;
        Log("载荷：exe=" + exePath);
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        var all = File.ReadAllBytes(exePath);
        Log("载荷：exe 字节数=" + all.Length);
        if (all.Length <= FooterSize)
        {
            return null;
        }

        var magic = Encoding.ASCII.GetString(all, all.Length - 8, 8);
        if (!string.Equals(magic, PayloadMagic, StringComparison.Ordinal))
        {
            return null;
        }

        var length = BitConverter.ToInt64(all, all.Length - FooterSize);
        if (length <= 0 || length > all.Length - FooterSize)
        {
            return null;
        }

        _payload = new byte[length];
        Buffer.BlockCopy(all, (int)(all.Length - FooterSize - length), _payload, 0, (int)length);
        Log("载荷：zip 字节数=" + _payload.Length);

        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream(_payload, writable: false);
        Log("载荷：打开 zip…");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Log("载荷：zip 条目数=" + archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            // 注意：这里**不能**用 `EndsWith('/')`——net48 上那会绑到 Shared 里的 NetCompat 扩展方法，
            // 而 Shared 恰恰是要靠这段代码才加载得进来的（JIT 阶段就要解析 → 死锁式 FileNotFoundException）。
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            entries[Path.GetFileName(entry.FullName)] = buffer.ToArray();
        }

        return entries;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);
}
#endif
