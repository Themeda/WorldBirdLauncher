using System.IO;
using System.Text.Json;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Services;

/// <summary>
/// 本机指定的皮肤文件。
///
/// 离线账户的 ID 不是正版时，Mojang / 皮肤站都查不到皮肤，头像与 3D 预览就只剩首字母 / 默认皮肤。
/// 这里给用户一个兜底：自己挑一张皮肤 PNG，按「账户类型 + 账户名」记住。
/// **只记路径，不拷贝文件**（用户的皮肤文件仍在自己磁盘上）。
/// </summary>
internal static class LocalSkinStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppSettings.Directory, "local-skins.json");

    private static Dictionary<string, string>? _map;

    /// <summary>该账户指定的皮肤文件路径；没指定或文件已不存在时返回 null。</summary>
    public static string? Get(AccountProfile? account)
    {
        if (account is null)
        {
            return null;
        }

        var map = Load();
        return map.TryGetValue(KeyOf(account), out var path) && !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? path
            : null;
    }

    /// <summary>指定 / 清除该账户的本地皮肤文件。</summary>
    public static void Set(AccountProfile account, string? path)
    {
        var map = Load();
        if (string.IsNullOrWhiteSpace(path))
        {
            map.Remove(KeyOf(account));
        }
        else
        {
            map[KeyOf(account)] = path!;
        }

        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(map, Options));
        }
        catch (Exception ex)
        {
            App.LogLine($"保存本地皮肤设置失败：{ex.Message}");
        }
    }

    private static string KeyOf(AccountProfile account) =>
        $"{account.Kind}:{account.Name}".ToLowerInvariant();

    private static Dictionary<string, string> Load()
    {
        if (_map is not null)
        {
            return _map;
        }

        try
        {
            _map = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception)
        {
            _map = [];
        }

        return _map;
    }
}
