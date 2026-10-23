namespace McServerList.Core.Models;

/// <summary>玩法与加载器的固定取值，供筛选器与数据文件共用。</summary>
public static class ServerCategories
{
    public const string Survival = "生存";
    public const string Creative = "创造";
    public const string MiniGame = "小游戏";
    public const string Rpg = "RPG";
    public const string Building = "建筑";
    public const string Modded = "模组服";

    /// <summary>插件服（原版核心 + 插件，如粘液科技、领地对战等）。</summary>
    public const string PluginServer = "插件服";
    public const string Prison = "监狱";
    public const string SkyBlock = "空岛";
    public const string Pvp = "PvP";
    public const string Anarchy = "无政府";
    public const string Pokemon = "宝可梦";
    public const string Roleplay = "角色扮演";

    public const string LoaderVanilla = "原版";
    public const string LoaderForge = "Forge";
    public const string LoaderFabric = "Fabric";
    public const string LoaderNeoForge = "NeoForge";

    /// <summary>判定不出具体加载器（例如原版核心 + 未知模组）时使用。</summary>
    public const string LoaderOther = "其他";

    public const string RegionDomestic = "国内";
    public const string RegionOverseas = "国外";

    public static readonly IReadOnlyList<string> AllRegions = [RegionDomestic, RegionOverseas];

    public static readonly IReadOnlyList<string> AllLoaders =
        [LoaderVanilla, LoaderForge, LoaderFabric, LoaderNeoForge, LoaderOther];

    /// <summary>版本筛选分组（1.6.x–1.21.x 的全部小版本 + 26.x）。</summary>
    public static readonly IReadOnlyList<string> VersionGroups =
    [
        "1.6.x", "1.7.x", "1.8.x", "1.9.x", "1.10.x", "1.11.x", "1.12.x", "1.13.x", "1.14.x", "1.15.x",
        "1.16.x", "1.17.x", "1.18.x", "1.19.x", "1.20.x", "1.21.x", "26.x",
    ];
}
