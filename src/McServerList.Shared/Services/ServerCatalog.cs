using System.Reflection;
using System.Text.Json;
using McServerList.Core.Models;

namespace McServerList.Core.Services;

/// <summary>
/// 内嵌的服务器数据目录（<c>servers.json</c>）。
///
/// 用途已经变了：客户端不再把它当数据源（列表只走远端下发），它现在是
/// **服务端首次启动的种子数据**（导入 SQLite 的初始目录）。
///
/// ★ 2026-10-08（M1.5 瘦身）：<c>servers.json</c>（约 1 MB）原来内嵌在 **Shared** 里，
/// 等于每份客户端 exe 都白带 1 MB。现在它内嵌到**服务端程序集**，客户端只留模型与校验逻辑；
/// 所以这里的读取入口改成显式传"从哪个程序集读"（服务端传自己的，单测传测试程序集）。
/// </summary>
public sealed class ServerCatalog
{
    private const string ResourceSuffix = ".Data.servers.json";

    private readonly Assembly _source;

    /// <summary>从指定程序集读内嵌的 <c>servers.json</c>（服务端用自己的程序集；单测用测试程序集）。</summary>
    public ServerCatalog(Assembly source) => _source = source;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public IReadOnlyList<ServerEntry> Load()
    {
        var resourceName = FindResourceName();
        using var stream = _source.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException($"未找到内嵌资源：{resourceName}");

        return LoadFrom(stream);
    }

    /// <summary>直接从 JSON 流读取（单测 / 工具用）。</summary>
    public static IReadOnlyList<ServerEntry> LoadFrom(Stream json)
    {
        var entries = JsonSerializer.Deserialize<List<ServerEntry>>(json, Options) ?? [];
        // 只过滤掉明显无效的条目；没有直连地址的（需专属客户端/官网/QQ群进入）也要保留
        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Id) &&
                        (!string.IsNullOrWhiteSpace(e.Name) || !string.IsNullOrWhiteSpace(e.Address)))
            .ToList();
    }

    /// <summary>程序集内嵌资源名（用于诊断）。</summary>
    public IReadOnlyList<string> GetResourceNames() => _source.GetManifestResourceNames();

    private string FindResourceName() =>
        GetResourceNames().FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"未找到内嵌资源（*{ResourceSuffix}）");
}
