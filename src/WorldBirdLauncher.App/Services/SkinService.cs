using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using WorldBirdLauncher.Core.Launcher;

namespace WorldBirdLauncher.App.Services;

/// <summary>账户对应的皮肤信息。</summary>
public sealed record SkinInfo(byte[] SkinPng, bool IsSlim, string Source);

/// <summary>
/// Resolves the Microsoft profile skin through Mojang's session service,
/// caching both hits and misses for 24 hours.
/// </summary>
public sealed class SkinService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(24);
    private static readonly HttpClient Http = CreateClient();
    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorldBirdLauncher", "skincache.json");
    private static readonly JsonSerializerOptions CacheOptions = new() { WriteIndented = true };

    private static Dictionary<string, CacheEntry>? _cache;

    private sealed record CacheEntry(string? SkinUrl, bool IsSlim, string? Source, bool Found, DateTimeOffset FetchedAt);

    public async Task<SkinInfo?> ResolveAsync(AccountProfile? account, CancellationToken cancellationToken = default)
    {
        if (account is null)
        {
            return null;
        }

        // A user-selected local skin takes precedence for previews and fallback rendering.
        var local = LocalSkinStore.Get(account);
        if (local is not null)
        {
            try
            {
                return new SkinInfo(File.ReadAllBytes(local), IsSlim: false, Source: "本地文件");
            }
            catch (Exception ex)
            {
                App.LogLine($"读取本地皮肤失败（{local}）：{ex.Message}");
            }
        }

        try
        {
            return await ResolveMojangAsync(account.Uuid, account.Name, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.LogLine($"获取皮肤失败（{account.Name}）：{ex.Message}");
            return null;
        }
    }

    private static async Task<SkinInfo?> ResolveMojangAsync(string? uuid, string? name, CancellationToken cancellationToken)
    {
        var key = !string.IsNullOrWhiteSpace(uuid)
            ? "uuid:" + uuid
            : string.IsNullOrWhiteSpace(name) ? null : "name:" + name!.ToLowerInvariant();
        if (key is null)
        {
            return null;
        }

        if (TryGetCache(key, out var cached))
        {
            return cached.Found ? await DownloadSkinAsync(cached.SkinUrl!, cached.IsSlim, cached.Source ?? "Mojang", cancellationToken).ConfigureAwait(false) : null;
        }

        if (string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(name))
        {
            // 名字 → uuid（官方接口：查不到返回 204/404，视为"这个 ID 不是正版"）
            var lookup = await Http.GetAsync(
                $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}", cancellationToken)
                .ConfigureAwait(false);
            if (lookup.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
            {
                SaveCache(key, new CacheEntry(null, false, null, Found: false, DateTimeOffset.Now));
                return null;
            }

            lookup.EnsureSuccessStatusCode();
            using var lookupDocument = JsonDocument.Parse(await lookup.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            uuid = lookupDocument.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(uuid))
        {
            SaveCache(key, new CacheEntry(null, false, null, Found: false, DateTimeOffset.Now));
            return null;
        }

        var profile = await Http.GetAsync(
            $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}", cancellationToken).ConfigureAwait(false);
        if (profile.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)
        {
            SaveCache(key, new CacheEntry(null, false, null, Found: false, DateTimeOffset.Now));
            return null;
        }

        profile.EnsureSuccessStatusCode();
        var parsed = ParseTextures(await profile.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), "Mojang");
        return await CacheAndDownloadAsync(key, parsed, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SkinInfo?> CacheAndDownloadAsync(
        string key,
        (string SkinUrl, bool IsSlim, string Source)? parsed,
        CancellationToken cancellationToken)
    {
        if (parsed is not { } value)
        {
            SaveCache(key, new CacheEntry(null, false, null, Found: false, DateTimeOffset.Now));
            return null;
        }

        SaveCache(key, new CacheEntry(value.SkinUrl, value.IsSlim, value.Source, Found: true, DateTimeOffset.Now));
        return await DownloadSkinAsync(value.SkinUrl, value.IsSlim, value.Source, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SkinInfo> DownloadSkinAsync(string skinUrl, bool isSlim, string source, CancellationToken cancellationToken)
    {
        var bytes = await Http.GetByteArrayAsync(skinUrl, cancellationToken).ConfigureAwait(false);
        return new SkinInfo(bytes, isSlim, source);
    }

    private static (string SkinUrl, bool IsSlim, string Source)? ParseTextures(string profileJson, string source)
    {
        using var document = JsonDocument.Parse(profileJson);
        if (!document.RootElement.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out var name) || name.GetString() != "textures" ||
                !property.TryGetProperty("value", out var value) ||
                value.GetString() is not { Length: > 0 } base64)
            {
                continue;
            }

            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            using var textureDocument = JsonDocument.Parse(decoded);
            if (!textureDocument.RootElement.TryGetProperty("textures", out var textures) ||
                !textures.TryGetProperty("SKIN", out var skin) ||
                !skin.TryGetProperty("url", out var url) ||
                url.GetString() is not { Length: > 0 } skinUrl)
            {
                return null;
            }

            var isSlim = skin.TryGetProperty("metadata", out var metadata) &&
                         metadata.TryGetProperty("model", out var model) &&
                         model.GetString() == "slim";
            return (skinUrl, isSlim, source);
        }

        return null;
    }

    private static bool TryGetCache(string key, out CacheEntry entry)
    {
        entry = null!;
        var cache = LoadCache();
        if (!cache.TryGetValue(key, out var found) || found is null)
        {
            return false;
        }

        if (DateTimeOffset.Now - found.FetchedAt > CacheLifetime)
        {
            return false;
        }

        entry = found;
        return true;
    }

    private static void SaveCache(string key, CacheEntry entry)
    {
        try
        {
            var cache = LoadCache();
            cache[key] = entry;
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache, CacheOptions));
        }
        catch (Exception ex)
        {
            App.LogLine($"写入皮肤缓存失败：{ex.Message}");
        }
    }

    private static Dictionary<string, CacheEntry> LoadCache()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            _cache = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)) ?? []
                : [];
        }
        catch (Exception)
        {
            _cache = [];
        }

        return _cache;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WorldBirdLauncher/0.2.0 (github.com/Themeda/WorldBirdLauncher)");
        return client;
    }
}
