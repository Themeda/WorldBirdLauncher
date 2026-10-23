using System.Text.Json;

namespace McServerList.Core.Launcher;

/// <summary>
/// 启动用的版本 JSON 链（自身 + 沿 <c>inheritsFrom</c> 的祖先）——自研，用来替代 StarLight 的 <c>GameCoreUtil</c>。
///
/// 只解析启动真正需要的字段；<c>arguments</c> / <c>libraries</c> 保留原始 <see cref="JsonElement"/>，
/// 由 <see cref="GameArgumentsBuilder"/> 按"与 StarLight.Core 2.0.5 逐项对齐"的规则去用。
/// 生命周期：<see cref="JsonElement"/> 依赖 <see cref="JsonDocument"/>，所以这里自己持有到 <see cref="Dispose"/>。
/// </summary>
internal sealed class MinecraftVersionChain : IDisposable
{
    private readonly List<JsonDocument> _documents = [];
    private readonly List<MinecraftVersionEntry> _entries = [];

    private MinecraftVersionChain()
    {
    }

    /// <summary>版本链：<c>[0]</c> 是自身，后面依次是父版本、祖父版本…</summary>
    public IReadOnlyList<MinecraftVersionEntry> Entries => _entries;

    public MinecraftVersionEntry Self => _entries[0];

    /// <summary>加载版本链；版本目录或 JSON 不存在时抛 <see cref="FileNotFoundException"/>。</summary>
    public static MinecraftVersionChain Load(string gameRoot, string versionId, int maxDepth = 5)
    {
        var chain = new MinecraftVersionChain();
        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var currentId = versionId;
            for (var hop = 0; hop < maxDepth && !string.IsNullOrWhiteSpace(currentId); hop++)
            {
                if (!visited.Add(currentId!))
                {
                    break;
                }

                var directory = Path.Combine(gameRoot, "versions", currentId);
                var jsonPath = Path.Combine(directory, currentId + ".json");
                if (!File.Exists(jsonPath))
                {
                    if (hop == 0)
                    {
                        throw new FileNotFoundException($"找不到版本 JSON：{jsonPath}");
                    }

                    break;
                }

                var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
                chain._documents.Add(document);
                chain._entries.Add(MinecraftVersionEntry.Parse(document.RootElement, jsonPath, directory, visited.Contains));
                currentId = chain._entries[^1].InheritsFrom;
            }

            return chain;
        }
        catch
        {
            chain.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var document in _documents)
        {
            document.Dispose();
        }

        _documents.Clear();
    }
}

/// <summary>版本 JSON 里启动需要的字段。</summary>
internal sealed class MinecraftVersionEntry
{
    public string Id { get; private init; } = string.Empty;

    /// <summary><c>type</c>（release / snapshot / old_beta…），用于拼 <c>--versionType</c>。</summary>
    public string? Type { get; private init; }

    public string? MainClass { get; private init; }

    public string? InheritsFrom { get; private set; }

    /// <summary>资源索引名（新版本做法是 <c>assetIndex.id</c>，老版本是 <c>assets</c>）。</summary>
    public string? Assets { get; private init; }

    /// <summary>老版本（1.12.2 及更早）的游戏参数串。</summary>
    public string? MinecraftArguments { get; private init; }

    /// <summary><c>arguments.game</c> 原始数组；为 <c>null</c> 表示这是老版本（<c>IsNewVersion = false</c>）。</summary>
    public JsonElement? ArgumentsGame { get; private set; }

    /// <summary><c>arguments.jvm</c> 原始数组（可能是对象元素，带 rules）。</summary>
    public JsonElement? ArgumentsJvm { get; private set; }

    /// <summary><c>libraries</c> 原始元素列表。</summary>
    public List<JsonElement> Libraries { get; private init; } = [];

    public string JsonPath { get; private init; } = string.Empty;

    public string Directory { get; private init; } = string.Empty;

    /// <summary>与 StarLight 一致：有 <c>arguments.game</c> 才算"新版本"。</summary>
    public bool IsNewVersion => ArgumentsGame is { ValueKind: JsonValueKind.Array };

    internal static MinecraftVersionEntry Parse(
        JsonElement root,
        string jsonPath,
        string directory,
        Func<string, bool> isVisited)
    {
        var entry = new MinecraftVersionEntry
        {
            Id = ReadString(root, "id") ?? Path.GetFileNameWithoutExtension(jsonPath),
            Type = ReadString(root, "type"),
            MainClass = ReadString(root, "mainClass"),
            InheritsFrom = ReadString(root, "inheritsFrom"),
            MinecraftArguments = ReadString(root, "minecraftArguments"),
            Assets = ReadString(root, "assets") ?? ReadNestedString(root, "assetIndex", "id"),
            JsonPath = jsonPath,
            Directory = directory,
        };

        if (entry.InheritsFrom is not null && isVisited(entry.InheritsFrom))
        {
            entry.InheritsFrom = null; // 防环
        }

        if (root.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.Object)
        {
            if (arguments.TryGetProperty("game", out var game) && game.ValueKind == JsonValueKind.Array)
            {
                entry.ArgumentsGame = game.Clone();
            }

            if (arguments.TryGetProperty("jvm", out var jvm) && jvm.ValueKind == JsonValueKind.Array)
            {
                entry.ArgumentsJvm = jvm.Clone();
            }
        }

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                entry.Libraries.Add(library.Clone());
            }
        }

        return entry;
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadNestedString(JsonElement root, string name, string nested) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? ReadString(value, nested)
            : null;
}
