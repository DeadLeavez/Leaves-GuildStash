using SPTarkov.Server.Core.Models.Common;

namespace Leaves_GuildStash_Server;

// Mod-wide state (config + the generated box-tpl maps), filled by GuildStashServer.OnLoad.
// Static because scoped DI services and Harmony patches can't share instance state.
public static class GuildStashContext
{
    // set in OnLoad before any request; non-null for the server's life
    public static GuildStashConfig Config = null!;

    // filled on load by GuildStashItems; both directions so a swap can go either way
    public static readonly Dictionary<MongoId, MongoId> ClosedToOpenTpl = new Dictionary<MongoId, MongoId>();
    public static readonly Dictionary<MongoId, MongoId> OpenToClosedTpl = new Dictionary<MongoId, MongoId>();

    // vanilla Item case / THICC tpl -> the closed guild tpl Convert turns it into
    public static readonly Dictionary<MongoId, MongoId> BaseToClosedTpl = new Dictionary<MongoId, MongoId>();

    // guild tpl (either state) -> config `key`. a storage records the key, not the tpl: it survives an ids.jsonc regen, and both states share it
    public static readonly Dictionary<MongoId, string> TplToBoxKey = new Dictionary<MongoId, string>();

    // config `key` -> display name, for wording a box-type mismatch in the UI
    public static readonly Dictionary<string, string> BoxKeyToName = new Dictionary<string, string>();

    // config `key` for a guild tpl, or "" if it isn't a guild box
    public static string BoxKeyForTpl(MongoId tpl)
    {
        if (TplToBoxKey.TryGetValue(tpl, out string? key))
        {
            return key;
        }
        return "";
    }

    // display name for a config key; falls back to the key for a box since removed from config
    public static string BoxNameForKey(string key)
    {
        if (BoxKeyToName.TryGetValue(key, out string? name))
        {
            return name;
        }
        return key;
    }

    public static bool IsClosedBox(MongoId tpl)
    {
        return ClosedToOpenTpl.ContainsKey(tpl);
    }

    public static bool IsOpenBox(MongoId tpl)
    {
        return OpenToClosedTpl.ContainsKey(tpl);
    }

    // ---- Pending client resyncs ---------------------------------------------------------------------

    // sessions whose client inventory the server changed behind its back (only a web-UI force-close of an online holder, today).
    // GuildStashResyncPatch drains it on their next item-event and attaches a Warning, which reloads the client.
    // static + locked: scoped services can't hold cross-request state.
    private static readonly object ResyncGate = new object();
    private static readonly HashSet<string> PendingResync = new HashSet<string>();

    public static void FlagResync(string sessionId)
    {
        lock (ResyncGate)
        {
            PendingResync.Add(sessionId);
        }
    }

    // true once per flag; act on it immediately
    public static bool TakeResync(string sessionId)
    {
        lock (ResyncGate)
        {
            return PendingResync.Remove(sessionId);
        }
    }
}
