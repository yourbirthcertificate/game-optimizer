using System.Globalization;
using System.Text;

namespace OptiGame.Core.Library.ConfigDiscovery;

internal static class SteamConfigAccount
{
    public static string? SingleSteamId(Func<string?> steamRoot)
    {
        try
        {
            if (steamRoot() is not { } root) return null;
            var path = ConfigPaths.CanonicalLocal(Path.Combine(root, "config", "loginusers.vdf"));
            if (path is null || ConfigPaths.Inspect(path) != PathState.File) return null;

            var users = KnownConfigSource.KeyValues(ConfigPaths.ReadBounded(path)).Children
                .Where(node => node.Key.Equals("users", StringComparison.OrdinalIgnoreCase)).ToArray();
            return users is [{ Value: null, Children: [var user] }] &&
                user.Value is null && user.Key.Length == 17 && ConfigPaths.IsId(user.Key) ? user.Key : null;
        }
        catch (Exception ex) when (ConfigPaths.IsReadError(ex) || ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    public static string? SingleUserdataId(string? root)
    {
        // Steam's public desktop individual ID has universe 1, account type 1, instance 1.
        const ulong publicIndividual = 0x0110000100000000;
        return ulong.TryParse(SingleSteamId(() => root), NumberStyles.None, CultureInfo.InvariantCulture, out var steamId) &&
            (steamId & 0xffffffff00000000) == publicIndividual && (uint)steamId != 0
            ? ((uint)steamId).ToString(CultureInfo.InvariantCulture)
            : null;
    }
}
