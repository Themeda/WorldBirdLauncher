using System.Security.Cryptography;
using System.Text.Json;

namespace WorldBirdLauncher.Core.Launcher;

/// <summary>
/// Microsoft account login persistence. Only the Microsoft profile and its tokens
/// are retained; credentials use Windows DPAPI in the current-user scope.
/// </summary>
public sealed class AccountService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public AccountService(string? storeDirectory = null)
    {
        StoreDirectory = storeDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WorldBirdLauncher");
    }

    public string StoreDirectory { get; }

    private string StorePath => Path.Combine(StoreDirectory, "accounts.dat");

    /// <summary>
    /// Reads the Microsoft-only account list. Legacy files are filtered in place:
    /// Microsoft profiles are retained and all other account formats are discarded.
    /// </summary>
    public IReadOnlyList<AccountProfile> Load()
    {
        List<AccountProfile> profiles;
        var needsRewrite = false;

        try
        {
            if (!File.Exists(StorePath))
            {
                return [];
            }

            var protectedBytes = File.ReadAllBytes(StorePath);
            var plain = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            using var document = JsonDocument.Parse(plain);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            profiles = new List<AccountProfile>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!IsMicrosoftProfile(element))
                {
                    needsRewrite = true;
                    continue;
                }

                try
                {
                    var profile = element.Deserialize<AccountProfile>(Options);
                    if (profile is null || string.IsNullOrWhiteSpace(profile.Name))
                    {
                        needsRewrite = true;
                        continue;
                    }

                    profiles.Add(profile);
                    if (element.TryGetProperty("Kind", out _) ||
                        element.TryGetProperty("ClientToken", out _) ||
                        element.TryGetProperty("ServerUrl", out _) ||
                        element.TryGetProperty("UnifiedPassServerId", out _))
                    {
                        needsRewrite = true;
                    }
                }
                catch (JsonException)
                {
                    needsRewrite = true;
                }
            }
        }
        catch (Exception)
        {
            return [];
        }

        if (needsRewrite)
        {
            try
            {
                Save(profiles);
            }
            catch (Exception)
            {
                // A read-only profile must not prevent an otherwise usable login.
            }
        }

        return profiles;
    }

    public void Save(IEnumerable<AccountProfile> accounts)
    {
        Directory.CreateDirectory(StoreDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(accounts.ToList(), Options);
        var protectedBytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StorePath, protectedBytes);
    }

    /// <summary>Stores a Microsoft login result and replaces the same profile.</summary>
    public IReadOnlyList<AccountProfile> Upsert(AccountProfile profile)
    {
        var list = Load().ToList();
        list.RemoveAll(a => SameAccount(a, profile));
        list.Insert(0, profile);
        Save(list);
        return list;
    }

    /// <summary>Clears credentials while retaining the account entry for a later login.</summary>
    public IReadOnlyList<AccountProfile> SignOut(AccountProfile profile)
    {
        var signedOut = profile with
        {
            AccessToken = string.Empty,
            MicrosoftRefreshToken = null,
            MicrosoftExpiresAt = null,
        };
        return Upsert(signedOut);
    }

    /// <summary>Uses the Microsoft refresh token to renew the Minecraft session.</summary>
    public async Task<AccountProfile> RefreshMicrosoftAsync(
        AccountProfile profile, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profile.MicrosoftRefreshToken))
        {
            throw new MicrosoftAuthException("这个账户没有可用于续期的微软凭据，请重新登录。");
        }

        var token = await MicrosoftAuth.RefreshAsync(profile.MicrosoftRefreshToken!, cancellationToken)
            .ConfigureAwait(false);
        var refreshed = await MicrosoftAuth.CompleteAsync(token, cancellationToken).ConfigureAwait(false);
        Upsert(refreshed);
        return refreshed;
    }

    public IReadOnlyList<AccountProfile> Remove(AccountProfile profile)
    {
        var list = Load().ToList();
        list.RemoveAll(a => SameAccount(a, profile));
        Save(list);
        return list;
    }

    public static bool IsSignedOut(AccountProfile profile) =>
        string.IsNullOrEmpty(profile.AccessToken);

    private static bool SameAccount(AccountProfile left, AccountProfile right)
    {
        if (!string.IsNullOrWhiteSpace(left.Uuid) && !string.IsNullOrWhiteSpace(right.Uuid))
        {
            return string.Equals(left.Uuid, right.Uuid, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMicrosoftProfile(JsonElement element)
    {
        if (!element.TryGetProperty("Kind", out var kind))
        {
            return true;
        }

        if (kind.ValueKind == JsonValueKind.Number && kind.TryGetInt32(out var numeric))
        {
            return numeric == 1;
        }

        if (kind.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = kind.GetString() ?? string.Empty;
        return value == "微软" || value.Equals("Microsoft", StringComparison.OrdinalIgnoreCase);
    }
}
