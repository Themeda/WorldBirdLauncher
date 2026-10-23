using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace McServerList.Core.Compat;

/// <summary>
/// .NET 8 → netstandard2.0/net48 的兼容层。
///
/// 目标框架换成 net48 后，一批"新 API"不存在（`char.IsAsciiDigit`、`ArgumentNullException.ThrowIfNull`、
/// `Random.Shared`、`Stream.ReadExactly*`、`UdpClient.SendAsync(ct)`、`Socket.ConnectAsync(host,port,ct)`、
/// `string.Split(char, StringSplitOptions.TrimEntries)`、`string.Replace(a,b,StringComparison)` …）。
/// 这里统一提供等价实现，**不用 `#if`**——net8 也走同一份代码，行为一致、避免两套逻辑。
/// </summary>
internal static class NetCompat
{
    private static readonly Random RandomSource = new();
    private static readonly object RandomGate = new();

    /// <summary>`char.IsAsciiDigit`（net7+）的等价实现。</summary>
    public static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';

    /// <summary>`char.IsAsciiLetterOrDigit`（net7+）的等价实现。</summary>
    public static bool IsAsciiLetterOrDigit(char value) =>
        IsAsciiDigit(value) || (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');

    /// <summary>`ArgumentException.ThrowIfNullOrWhiteSpace`（net7+）的等价实现。</summary>
    public static void ThrowIfNullOrWhiteSpace(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("值不能为 null 或空白。", paramName);
        }
    }

    /// <summary>`ArgumentNullException.ThrowIfNull`（net6+）的等价实现。</summary>
    public static void ThrowIfNull(object? value, string paramName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }
    }

    /// <summary>`string.Split(char, StringSplitOptions.TrimEntries)`（net5+）的等价实现。</summary>
    public static string[] SplitTrim(string text, char separator) =>
        text.Split(separator).Select(part => part.Trim()).ToArray();

    /// <summary>`string.Replace(old, new, StringComparison.OrdinalIgnoreCase)`（net5+）的等价实现。</summary>
    public static string ReplaceIgnoreCase(string text, string oldValue, string newValue) =>
        Regex.Replace(text, Regex.Escape(oldValue), newValue.Replace("$", "$$"), RegexOptions.IgnoreCase);

    /// <summary>`string.Replace(old, new, StringComparison)`（netcore2.1+）的等价实现。</summary>
    public static string Replace(string text, string oldValue, string newValue, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(oldValue))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder();
        var start = 0;
        while (true)
        {
            var index = text.IndexOf(oldValue, start, comparison);
            if (index < 0)
            {
                builder.Append(text, start, text.Length - start);
                return builder.ToString();
            }

            builder.Append(text, start, index - start);
            builder.Append(newValue);
            start = index + oldValue.Length;
        }
    }

    /// <summary>`Random.Shared`（net6+）的等价实现（带锁，跨线程安全）。</summary>
    public static int NextRandom()
    {
        lock (RandomGate)
        {
            return RandomSource.Next();
        }
    }

    /// <summary>`Random.Shared.Next(maxValue)`（net6+）的等价实现。</summary>
    public static int NextRandom(int maxValue)
    {
        lock (RandomGate)
        {
            return RandomSource.Next(maxValue);
        }
    }

    /// <summary>
    /// `Environment.ProcessPath`（net6+）的等价实现。
    ///
    /// ★ 故意**不用 `Assembly.Location` 做兜底**：单文件发布下它恒为空串，net8 的
    /// `PublishSingleFile` 会在 publish 时报 `IL3000`（`'Assembly.Location' always returns an empty string
    /// for assemblies embedded in a single-file app`）——实测踩过。net48 与 net8 单文件下
    /// `Process.MainModule.FileName` 都能拿到真正的 exe 路径（调用方用它取托盘图标），拿不到就返回 null
    /// 由调用方兜底。
    /// </summary>
    public static string? ExecutablePath()
    {
        try
        {
            return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// `Dictionary.Remove(key, out value)`（netcore2.0+）的等价实现。
    /// 扩展方法永远输给实例方法，所以 net8 上仍然走 BCL 那个，不会二义性。
    /// </summary>
    public static bool Remove<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, out TValue value)
        where TKey : notnull
    {
        if (dictionary.TryGetValue(key, out var found))
        {
            value = found;
            dictionary.Remove(key);
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>`Stream.ReadExactly*`（net7+）的等价实现：读满为止，读不到就抛。</summary>
    public static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        var read = 0;
        while (read < count)
        {
            var current = stream.Read(buffer, offset + read, count - read);
            if (current <= 0)
            {
                throw new EndOfStreamException("数据流提前结束。");
            }

            read += current;
        }
    }

    /// <inheritdoc cref="ReadExactly(Stream, byte[], int, int)" />
    public static async Task ReadExactlyAsync(
        Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var current = await stream.ReadAsync(buffer, offset + read, count - read, cancellationToken)
                .ConfigureAwait(false);
            if (current <= 0)
            {
                throw new EndOfStreamException("数据流提前结束。");
            }

            read += current;
        }
    }

    /// <summary>
    /// 给"没有 CancellationToken 重载"的异步操作补上取消语义
    /// （`UdpClient.SendAsync/ReceiveAsync`、`Socket.ConnectAsync` 在 netstandard2.0 上没有带 token 的版本）。
    /// </summary>
    public static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await task.ConfigureAwait(false);
        }

        var cancelled = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
        {
            if (task != await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        return await task.ConfigureAwait(false);
    }

    /// <inheritdoc cref="WithCancellation{T}(Task{T}, CancellationToken)" />
    public static async Task WithCancellation(Task task, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancelled = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancelled))
        {
            if (task != await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        await task.ConfigureAwait(false);
    }

    // ---------- 数值 / 字符串（net48 没有 Math.Clamp / Convert.ToHexString） ----------

    /// <summary>`Math.Clamp`（netcore2+，net48 没有）的等价实现。</summary>
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <inheritdoc cref="Clamp(int,int,int)"/>
    public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;

    /// <inheritdoc cref="Clamp(int,int,int)"/>
    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

    /// <summary>`Convert.ToHexString`（net5+）的等价实现；<paramref name="lowercase"/> 为真时输出小写。</summary>
    public static string ToHexString(byte[] bytes, bool lowercase = false)
    {
        const string Upper = "0123456789ABCDEF";
        const string Lower = "0123456789abcdef";
        var table = lowercase ? Lower : Upper;
        var chars = new char[bytes.Length * 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = table[bytes[i] >> 4];
            chars[i * 2 + 1] = table[bytes[i] & 0x0F];
        }

        return new string(chars);
    }

    // ---------- 哈希（net48 没有 MD5/SHA1/SHA256.HashData） ----------

    /// <summary>`MD5.HashData(byte[])`（net5+）的等价实现。</summary>
    public static byte[] Md5(byte[] data)
    {
        using var hash = MD5.Create();
        return hash.ComputeHash(data);
    }

    /// <summary>`SHA1.HashData(byte[])`（net5+）的等价实现。</summary>
    public static byte[] Sha1(byte[] data)
    {
        using var hash = SHA1.Create();
        return hash.ComputeHash(data);
    }

    /// <summary>`SHA256.HashData(byte[])`（net5+）的等价实现。</summary>
    public static byte[] Sha256(byte[] data)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(data);
    }

    /// <summary>算一个文件的 SHA-1（小写十六进制），用于和版本 JSON / 官方 sha1 比对。</summary>
    public static string Sha1Hex(Stream stream)
    {
        using var hash = SHA1.Create();
        return ToHexString(hash.ComputeHash(stream), lowercase: true);
    }

    // ---------- 平台判断（net48 没有 OperatingSystem.IsWindows 那族） ----------

    /// <summary>`OperatingSystem.IsWindows()`（net5+）的等价实现。</summary>
    public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>`OperatingSystem.IsMacOS()`（net5+）的等价实现。</summary>
    public static bool IsMacOS() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    /// <summary>`OperatingSystem.IsLinux()`（net5+）的等价实现。</summary>
    public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    // ---------- 文件异步（net48 没有 File.*Async） ----------

    /// <summary>`File.ReadAllTextAsync`（netcore）的等价实现。</summary>
    public static Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => File.ReadAllText(path), cancellationToken);

    /// <summary>`File.WriteAllTextAsync`（netcore）的等价实现。</summary>
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        Task.Run(() => File.WriteAllText(path, contents), cancellationToken);

    /// <summary>`File.WriteAllTextAsync(path, contents, encoding, ct)`（netcore）的等价实现。</summary>
    public static Task WriteAllTextAsync(
        string path,
        string contents,
        System.Text.Encoding encoding,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => File.WriteAllText(path, contents, encoding), cancellationToken);

    /// <summary>`File.ReadAllBytesAsync`（netcore）的等价实现。</summary>
    public static Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => File.ReadAllBytes(path), cancellationToken);

    /// <summary>`File.WriteAllBytesAsync`（netcore）的等价实现。</summary>
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) =>
        Task.Run(() => File.WriteAllBytes(path, bytes), cancellationToken);

    /// <summary>
    /// `Stream.ReadAsync(byte[], ct)`（netcore2.1+ 的 Memory 重载）的等价实现，**固定返回 Task&lt;int&gt;**
    /// ——调用点要拿它和 `Task.Delay` 赛跑来判"卡住"，所以必须是 Task（net8 的 Memory 重载是 ValueTask）。
    /// 名字与 BCL 不冲突，所以 net8 上也能用同一份实现（不用 #if）。
    /// </summary>
    public static Task<int> ReadAsyncTask(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

    // ---------- 进程 / 内存 ----------

    /// <summary>`Process.WaitForExitAsync`（net5+）的等价实现：等进程退出，且等待本身可被取消。</summary>
    public static async Task WaitForExitAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default)
    {
        var exitTask = Task.Run(() => process.WaitForExit(), CancellationToken.None);
        if (cancellationToken.CanBeCanceled)
        {
            await WithCancellation(exitTask, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await exitTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// `Path.GetRelativePath`（netcore2.1+）的等价实现（net48 没有）。
    /// 只在本项目用到的场景（同一盘符内的绝对路径）下保证正确。
    /// </summary>
    public static string GetRelativePath(string relativeTo, string path)
    {
        var basePath = Path.GetFullPath(relativeTo);
        if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            basePath += Path.DirectorySeparatorChar;
        }

        var fullPath = Path.GetFullPath(path);
#if NETSTANDARD2_0
        var baseUri = new Uri(basePath);
        var pathUri = new Uri(fullPath);
        if (!string.Equals(baseUri.Scheme, pathUri.Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        var relative = baseUri.MakeRelativeUri(pathUri).ToString();
        return Uri.UnescapeDataString(relative.Replace('/', Path.DirectorySeparatorChar));
#else
        return Path.GetRelativePath(basePath, fullPath);
#endif
    }

    /// <summary>`File.Move(source, dest, overwrite: true)`（netcore3+）的等价实现。</summary>
    public static void MoveFile(string source, string destination, bool overwrite)
    {
        if (overwrite && File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(source, destination);
    }

    /// <summary>`ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true)`（netcore2.0+）的等价实现。</summary>
    public static void ExtractToDirectory(string zipPath, string targetDirectory, bool overwriteFiles)
    {
        using var archive = System.IO.Compression.ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var target = Path.Combine(targetDirectory, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!overwriteFiles && File.Exists(target))
            {
                continue;
            }

            // 不用 ZipFileExtensions.ExtractToFile：netstandard2.0 那份里不一定带得进来，直接拷流最稳
            using var source = entry.Open();
            using var destination = File.Create(target);
            source.CopyTo(destination);
        }
    }

    /// <summary>`Process.Kill(entireProcessTree: true)`（netcore3+）的等价实现（net48 只能杀自己）。</summary>
    public static void KillProcessTree(System.Diagnostics.Process process)
    {
        try
        {
#if NETSTANDARD2_0
            process.Kill();
#else
            process.Kill(entireProcessTree: true);
#endif
        }
        catch (Exception)
        {
            // 进程已经退出 / 权限不足：忽略
        }
    }

    /// <summary>
    /// 把参数列表拼成命令行字符串（net48 没有 <c>ProcessStartInfo.ArgumentList</c>）。
    /// 规则与 Windows 的命令行解析一致：含空格/制表符/引号的参数用双引号包起来并转义引号。
    /// </summary>
    public static string JoinArguments(IEnumerable<string> arguments)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var argument in arguments)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(QuoteArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return argument;
        }

        var builder = new System.Text.StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(c);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>`SHA1.HashDataAsync(stream, ct)` 的等价实现：算一个流的 SHA-1（小写十六进制）。</summary>
    public static Task<string> Sha1HexAsync(Stream stream, CancellationToken cancellationToken = default) =>
        Task.Run(() => Sha1Hex(stream), cancellationToken);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>
    /// 当前可用的物理内存字节数。net48 没有 `GC.GetGCMemoryInfo()`，所以统一走 Win32 的
    /// `GlobalMemoryStatusEx`（net8 上也照常能用，避免两套逻辑）。
    /// </summary>
    public static long AvailableMemoryBytes()
    {
        if (!IsWindows())
        {
            return 0;
        }

        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.AvailablePhysical : 0;
    }

    // ---------- LINQ（net48 没有 ToHashSet / Chunk） ----------

#if NETSTANDARD2_0
    // 这一整块**只编进 netstandard2.0 那一份**（net48 客户端消费的正是它）：
    // net8.0 那一份不能有，否则会和 BCL 里的同名方法撞成"调用二义性"（CS0121）。
    // 这么做的好处是 **App/Core 的调用点一个都不用改**，net8 上也仍然走 BCL 的原生实现（行为逐字节不变）。

    /// <summary>`string.Contains(value, StringComparison)`（netcore2.1+）的等价实现。</summary>
    public static bool Contains(this string? text, string? value, StringComparison comparison) =>
        text is not null && value is not null && text.IndexOf(value, comparison) >= 0;

    /// <summary>`string.StartsWith(char)`（netcore2.0+）的等价实现。</summary>
    public static bool StartsWith(this string? text, char value) => text is { Length: > 0 } && text[0] == value;

    /// <summary>`string.EndsWith(char)`（netcore2.0+）的等价实现。</summary>
    public static bool EndsWith(this string? text, char value) => text is { Length: > 0 } && text[^1] == value;

    /// <summary>`string.Split(char, StringSplitOptions)`（netcore2.0+）的等价实现。</summary>
    public static string[] Split(this string? text, char separator, StringSplitOptions options) =>
        (text ?? string.Empty).Split([separator], options);

    /// <summary>`HttpClient.GetStringAsync(url, ct)`（net5+）的等价实现。</summary>
    public static async Task<string> GetStringAsync(this System.Net.Http.HttpClient client, string? url, CancellationToken cancellationToken)
    {
        if (url is null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    /// <summary>`HttpClient.GetByteArrayAsync(url, ct)`（net5+）的等价实现。</summary>
    public static async Task<byte[]> GetByteArrayAsync(this System.Net.Http.HttpClient client, string? url, CancellationToken cancellationToken)
    {
        if (url is null)
        {
            throw new ArgumentNullException(nameof(url));
        }

        using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    /// <summary>`HttpContent.ReadAsStringAsync(ct)`（net5+）的等价实现。</summary>
    public static Task<string> ReadAsStringAsync(this System.Net.Http.HttpContent content, CancellationToken cancellationToken) =>
        WithCancellation(content.ReadAsStringAsync(), cancellationToken);

    /// <summary>`HttpContent.ReadAsByteArrayAsync(ct)`（net5+）的等价实现。</summary>
    public static Task<byte[]> ReadAsByteArrayAsync(this System.Net.Http.HttpContent content, CancellationToken cancellationToken) =>
        WithCancellation(content.ReadAsByteArrayAsync(), cancellationToken);

    /// <summary>`HttpContent.ReadAsStreamAsync(ct)`（net5+）的等价实现。</summary>
    public static Task<Stream> ReadAsStreamAsync(this System.Net.Http.HttpContent content, CancellationToken cancellationToken) =>
        WithCancellation(content.ReadAsStreamAsync(), cancellationToken);

    /// <summary>`CancellationTokenSource.CancelAsync()`（net8+）的等价实现。</summary>
    public static Task CancelAsync(this CancellationTokenSource source)
    {
        source.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>`Stream.WriteAsync(ReadOnlyMemory&lt;byte&gt;, ct)`（netcore2.1+）的等价实现。</summary>
    public static Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
        stream.WriteAsync(buffer.ToArray(), 0, buffer.Length, cancellationToken);

    /// <summary>`Enumerable.TakeLast`（netcore2.0+）的等价实现。</summary>
    public static IEnumerable<T> TakeLast<T>(this IEnumerable<T> source, int count)
    {
        var queue = new Queue<T>(Math.Max(count, 0) + 1);
        foreach (var item in source)
        {
            queue.Enqueue(item);
            if (queue.Count > count)
            {
                queue.Dequeue();
            }
        }

        return queue;
    }

    /// <summary>`KeyValuePair.Deconstruct`（netcore2.0+）的等价实现，让 `foreach (var (k, v) in dict)` 能用。</summary>
    public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
    {
        key = pair.Key;
        value = pair.Value;
    }

    /// <summary>`Enumerable.ToHashSet`（netstandard2.1+）的等价实现。</summary>
    // 注意：net48（.NET Framework 4.7.2+）自己就有 Enumerable.ToHashSet，**不能再提供同签名的扩展**，
    // 否则调用点会报 CS0121 二义性（实测）。所以这里只留 Chunk，ToHashSet 用 BCL 的。

    /// <summary>`Enumerable.Chunk`（net6+）的等价实现。</summary>
    public static IEnumerable<T[]> Chunk<T>(this IEnumerable<T> source, int size)
    {
        var chunk = new List<T>(size);
        foreach (var item in source)
        {
            chunk.Add(item);
            if (chunk.Count < size)
            {
                continue;
            }

            yield return chunk.ToArray();
            chunk.Clear();
        }

        if (chunk.Count > 0)
        {
            yield return chunk.ToArray();
        }
    }
#endif
}
