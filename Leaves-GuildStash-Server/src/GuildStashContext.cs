using SPTarkov.Server.Core.Models.Common;

namespace Leaves_GuildStash_Server;

// Mod-wide state, filled by GuildStashServer.OnLoad and read by the HTTP API, the storage services and the Harmony patches.
// Static-bridge pattern: keeps the DI-loaded pieces and the config in one place.
public static class GuildStashContext
{
    // Set during OnLoad, before any request can arrive. Non-null for the whole server lifetime.
    public static GuildStashConfig Config = null!;

    // The generated guild-box tpls, filled by GuildStashItems on load.
    // Maps let the actions/tag flow ask "is this a guild box? closed or open? what's its counterpart to swap to?" by template.
    public static readonly Dictionary<MongoId, MongoId> ClosedToOpenTpl = new Dictionary<MongoId, MongoId>();
    public static readonly Dictionary<MongoId, MongoId> OpenToClosedTpl = new Dictionary<MongoId, MongoId>();

    // Base vanilla container tpl (Item case / THICC) -> the CLOSED guild tpl to convert it into.
    public static readonly Dictionary<MongoId, MongoId> BaseToClosedTpl = new Dictionary<MongoId, MongoId>();

    // Guild-box tpl (CLOSED or OPEN) -> the box's display name (config Name), for the web UI's type column ("what kind of case is this stash?").
    // Filled by GuildStashItems alongside the tpl maps above.
    public static readonly Dictionary<MongoId, string> TplToBoxName = new Dictionary<MongoId, string>();

    // Guild-box tpl (CLOSED or OPEN) -> the box's config `key` ("itemcase" / "thicc").
    // The key, not the tpl, is what a storage records as its BoxKey: it survives an ids.jsonc regeneration, and both states of the same box share it, so a closed box and its open counterpart match the same stash.
    public static readonly Dictionary<MongoId, string> TplToBoxKey = new Dictionary<MongoId, string>();

    // Config `key` -> display name, so a box-type mismatch can be reported in words ("this is a Guild Item Case, but that stash was made from a Guild THICC Item Case") rather than as two opaque keys.
    public static readonly Dictionary<string, string> BoxKeyToName = new Dictionary<string, string>();

    // The box's config `key` for a guild tpl, or "" if it isn't a guild box.
    public static string BoxKeyForTpl(MongoId tpl)
    {
        if (TplToBoxKey.TryGetValue(tpl, out var key))
        {
            return key;
        }
        return "";
    }

    // A human name for a config key, falling back to the key itself for a box that's been removed from config since a stash was made with it.
    public static string BoxNameForKey(string key)
    {
        if (BoxKeyToName.TryGetValue(key, out var name))
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

    // Sessions whose client is holding an inventory the server has changed behind its back - currently only set by a web-UI force-close of an ONLINE holder.
    // GuildStashResyncPatch drains this on the session's next item-event and attaches a Warning, which makes the client reload.
    // Static + locked because a scoped service can't hold cross-request state.
    private static readonly object ResyncGate = new object();
    private static readonly HashSet<string> PendingResync = new HashSet<string>();

    public static void FlagResync(string sessionId)
    {
        lock (ResyncGate)
        {
            PendingResync.Add(sessionId);
        }
    }

    // True exactly once per flag - the caller is expected to act on it immediately.
    public static bool TakeResync(string sessionId)
    {
        lock (ResyncGate)
        {
            return PendingResync.Remove(sessionId);
        }
    }
}
