using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// One shared storage, persisted as user/guildstash/<ID>.jsonc.
// Contents is the item subtree that lives inside the box while it's closed (ids here are throwaway - remapped to fresh ids every time a box is opened, and overwritten wholesale on close).
public sealed class StoredBox
{
    // NOTE: these are PROPERTIES, not fields, on purpose - SPT's JsonUtil (System.Text.Json) only serializes properties, so public fields would silently write/read as empty (this bit us: a close reported "N items saved" but the jsonc came out empty).
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("contents")]
    public List<Item> Contents { get; set; } = new List<Item>();

    // The item id that the TOP-LEVEL contents name as their ParentId (the open box's own id at the moment it was closed).
    // Remapped to the freshly-spawned open box's id on the next open so the contents nest back inside it. Empty when the box has never held anything.
    [JsonPropertyName("rootId")]
    public string RootId { get; set; } = "";

    // sessionId of whoever currently has the box open, or null when nobody does.
    // Soft marker only - the actual open-lock lives in the bindings (TryClaimStorage); this just records who last opened it.
    [JsonPropertyName("holder")]
    public string? Holder { get; set; }

    // Which KIND of guild box this stash belongs to - the config `key` ("itemcase" / "thicc").
    // Recorded at Convert and re-stamped on every Close, and checked on Open: contents saved from a THICC reference grid names and cell coordinates that simply don't exist inside an Item case, so opening a stash with the wrong case type hands the client an inventory it can't lay out.
    // Empty on storages written before this was added - those are grandfathered in (any box may open them) and stamped on their next close.
    [JsonPropertyName("boxKey")]
    public string BoxKey { get; set; } = "";
}

// Records that one open-box item is currently bound to a storage ID.
// Persisted (storage/_bindings.json) so it survives a server crash/restart - the close then still knows which file to write, and it's a handle for future recovery tooling (find the orphaned open box for a storage, etc.).
public sealed class OpenBinding
{
    // The storage ID this open box was opened against - the authoritative target for its close.
    [JsonPropertyName("storageId")]
    public string StorageId { get; set; } = "";

    // Who opened it (sessionId), for diagnostics / a future "who's using this" view.
    [JsonPropertyName("holder")]
    public string? Holder { get; set; }

    // When it was opened (ISO-8601 UTC), for stale-binding cleanup later.
    [JsonPropertyName("openedUtc")]
    public string? OpenedUtc { get; set; }
}

// Reads/writes the per-ID storage files, one per shared box, at <mod>/storage/<ID>.jsonc.
// The csproj's build-copy globs deliberately DON'T include storage/, so a rebuild that re-copies the mod never clobbers live storage.
// Items go through SPT's JsonUtil so MongoId/_tpl/Upd round-trip exactly (plain System.Text.Json mangles MongoId).
[Injectable]
public class GuildStashStorage(ModHelper modHelper, JsonUtil jsonUtil, ProfileHelper profileHelper)
{
    private string? storageDir;

    private string StorageDir()
    {
        if (storageDir == null)
        {
            string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
            storageDir = System.IO.Path.Combine(modPath, "storage");
            Directory.CreateDirectory(storageDir);
        }
        return storageDir;
    }

    private string PathFor(string id)
    {
        // Always lowercase the filename - ids already fold to lowercase in ParseRemoteTag, but this makes the file layout case-consistent on a case-sensitive filesystem (Linux) too.
        return System.IO.Path.Combine(StorageDir(), id.ToLowerInvariant() + ".jsonc");
    }

    // Load a storage by id, or an empty StoredBox (with that id) if the file doesn't exist yet - a never-before-opened id is simply an empty box.
    public StoredBox Load(string id)
    {
        return Load(id, out _);
    }

    // As above, but `unreadable` distinguishes "no file yet" (false - a legitimately empty stash) from "the file is there but won't parse" (true).
    // That difference MATTERS: a truncated/corrupt file used to come back as an empty StoredBox, so the stash looked empty in-game and the next Close cheerfully overwrote it with the empty box's contents - turning a recoverable corruption into permanent loss.
    // Callers that are about to act on the contents (Open) must refuse when this is true.
    public StoredBox Load(string id, out bool unreadable)
    {
        unreadable = false;
        string file = PathFor(id);

        lock (StoreGate)
        {
            if (!File.Exists(file))
            {
                return new StoredBox { Id = id };
            }

            try
            {
                string raw = File.ReadAllText(file);
                var box = jsonUtil.Deserialize<StoredBox>(raw);
                if (box == null)
                {
                    unreadable = true;
                    return new StoredBox { Id = id };
                }
                box.Id = id;
                if (box.Contents == null)
                {
                    box.Contents = new List<Item>();
                }
                return box;
            }
            catch (Exception ex)
            {
                unreadable = true;
                Leaves.Utils.Instance.printColor($"[GuildStash] Failed to read storage {id}: {ex.Message}", LogTextColor.Red);
                return new StoredBox { Id = id };
            }
        }
    }

    // Write a storage file.
    // ATOMIC: serialize to a temp file first, then move it over the real one, so a crash mid-write can never leave a half-written (unparseable) storage behind - a reader sees either the whole old file or the whole new one.
    // Taking StoreGate as well keeps in-process readers (the web UI's ListStorages, chiefly) from reading between the write and the move, and stops the move itself failing on a Windows sharing violation because someone had the file open.
    public void Save(StoredBox box)
    {
        string file = PathFor(box.Id);
        string json = jsonUtil.Serialize(box, true);

        lock (StoreGate)
        {
            WriteFileAtomic(file, json);
        }
    }

    // Shared by Save and PersistBindingsLocked. Caller MUST hold StoreGate.
    private void WriteFileAtomic(string file, string contents)
    {
        string temp = file + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, file, true);
    }

    // Called by the close path (GuildStashCloser) JUST BEFORE a close overwrites storage/<id>.jsonc and mutates the holder's profile.
    // Zips a full recovery snapshot into storage/backups/<id>_<utc>.zip:
    //   - the PREVIOUS storage file (last-closed contents), if any, and
    //   - the holder's WHOLE profile as it is right now - the open box still holds the live items, so this is the authoritative copy to restore from if the close ever mis-saves.
    // Both are text and compress hard, so the zip stays small despite the profile being large.
    // While the auto-close edge cases are unproven we hoard EVERY snapshot (user's explicit "store too much rather than lose progress"); add a keep-last-N prune later if the folder grows.
    // Best-effort: any failure is logged but NEVER blocks the close (gameplay must not hang on a backup).
    // The backups/ subdir is never enumerated by ListStorages (non-recursive *.jsonc scan of the root).
    public void SnapshotBeforeClose(string id, MongoId sessionId)
    {
        try
        {
            string backupDir = System.IO.Path.Combine(StorageDir(), "backups");
            Directory.CreateDirectory(backupDir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string zipPath = System.IO.Path.Combine(backupDir, id.ToLowerInvariant() + "_" + stamp + ".zip");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // Previous storage snapshot (may not exist yet on a first-ever close).
                string storageFile = PathFor(id);
                if (File.Exists(storageFile))
                {
                    zip.CreateEntryFromFile(storageFile, id.ToLowerInvariant() + ".jsonc");
                }

                // The holder's full profile, serialized from memory (pre-mutation).
                // Wrapped separately so a profile-fetch hiccup still leaves us the storage snapshot in the zip.
                try
                {
                    var profile = profileHelper.GetFullProfile(sessionId);
                    if (profile != null)
                    {
                        string profileJson = jsonUtil.Serialize(profile, true);
                        var entry = zip.CreateEntry("profile_" + sessionId.ToString() + ".json");
                        using (var stream = entry.Open())
                        using (var writer = new StreamWriter(stream))
                        {
                            writer.Write(profileJson);
                        }
                    }
                }
                catch (Exception pex)
                {
                    Leaves.Utils.Instance.printColor($"[GuildStash] Backup: could not capture profile {sessionId}: {pex.Message}", LogTextColor.Yellow);
                }
            }
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Backup zip for '{id}' failed (closing anyway): {ex.Message}", LogTextColor.Red);
        }
    }

    // Whether a storage with this id has been registered (a file exists).
    // Convert creates the file, so "exists" == "was created through Convert" - Open requires it, Convert forbids it.
    public bool Exists(string id)
    {
        return File.Exists(PathFor(id));
    }

    // Register a brand-new storage, atomically: fails if that id is already taken.
    // Convert used to do Exists() then Save() as two separate steps, so two players converting the same fresh id could both see it free and both "claim" it.
    public bool TryCreateStorage(StoredBox box)
    {
        lock (StoreGate)
        {
            if (Exists(box.Id))
            {
                return false;
            }
            Save(box);
            return true;
        }
    }

    // Whether this storage is currently OPEN somewhere (a binding points at it) - i.e. locked.
    // Scans the persisted bindings, so it holds across a restart. Returns the holder session id via `holder` when so.
    // This is the PREFLIGHT check only - the authoritative open-lock is TryClaimStorage.
    public bool IsOpenSomewhere(string id, out string? holder)
    {
        holder = null;
        lock (StoreGate)
        {
            foreach (var binding in BindingsLocked().Values)
            {
                if (binding.StorageId == id)
                {
                    holder = binding.Holder;
                    return true;
                }
            }
        }
        return false;
    }

    // ---- Open-box bindings (open-box item id -> storage it was opened against) ----------------------

    // STATIC on purpose.
    // [Injectable] with no InjectionType means SCOPED (see SPT's Injectable.cs), so DI hands out a FRESH GuildStashStorage for every HTTP request.
    // Per-INSTANCE state therefore gave two concurrent requests their own copy of the binding map AND their own lock object: the read-modify-write of _bindings.json lost updates, and the "lock" locked nothing.
    // Static state behind a static gate is the only thing genuinely shared across requests.
    //
    // The gate guards the storage FILES too (claim/write), so a check-then-write can't be interleaved.
    // C# locks are reentrant, so a public method that calls another one nests harmlessly.
    //
    // Side effect of caching the map for the server's lifetime: a hand-edit of _bindings.json now needs a restart to be picked up (it used to be re-read on every request).
    private static readonly object StoreGate = new object();
    private static Dictionary<string, OpenBinding>? bindings;

    private string BindingsPath()
    {
        return System.IO.Path.Combine(StorageDir(), "_bindings.json");
    }

    // Lazy-load the persisted bindings on first access (survives a restart, unlike an in-memory dict).
    // Caller MUST hold StoreGate.
    private Dictionary<string, OpenBinding> BindingsLocked()
    {
        if (bindings != null)
        {
            return bindings;
        }

        var loaded = new Dictionary<string, OpenBinding>();
        string file = BindingsPath();
        if (File.Exists(file))
        {
            try
            {
                string raw = File.ReadAllText(file);
                var map = jsonUtil.Deserialize<Dictionary<string, OpenBinding>>(raw);
                if (map != null)
                {
                    foreach (var pair in map)
                    {
                        loaded[pair.Key] = pair.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] Failed to read _bindings.json: {ex.Message}", LogTextColor.Red);
            }
        }

        bindings = loaded;
        return bindings;
    }

    // Caller MUST hold StoreGate.
    // Atomic for the same reason Save is - a half-written _bindings.json would drop or corrupt every open-box binding at once.
    private void PersistBindingsLocked()
    {
        var plain = new Dictionary<string, OpenBinding>(BindingsLocked());
        string json = jsonUtil.Serialize(plain, true);
        WriteFileAtomic(BindingsPath(), json);
    }

    // A copy of every current binding (openBoxItemId -> binding), for the startup orphan sweep.
    public Dictionary<string, OpenBinding> GetBindingsSnapshot()
    {
        lock (StoreGate)
        {
            return new Dictionary<string, OpenBinding>(BindingsLocked());
        }
    }

    // THE open-lock: atomically claim a storage for an about-to-be-spawned open box, refusing if ANY binding already points at it.
    // Replaces the old check-then-bind pair (ValidateOpen's IsOpenSomewhere, then a BindOpenBox several steps later) - two opens racing between those two points both passed the check and both got a box full of the same contents.
    // The open box's id is minted by the CALLER so the claim can name it BEFORE any profile mutation happens. Returns false (with the current holder) when it's taken.
    public bool TryClaimStorage(string openBoxItemId, string storageId, string? holder, out string? currentHolder)
    {
        currentHolder = null;
        lock (StoreGate)
        {
            var map = BindingsLocked();
            foreach (var binding in map.Values)
            {
                if (binding.StorageId == storageId)
                {
                    currentHolder = binding.Holder;
                    return false;
                }
            }

            map[openBoxItemId] = new OpenBinding
            {
                StorageId = storageId,
                Holder = holder,
                OpenedUtc = DateTime.UtcNow.ToString("o"),
            };
            PersistBindingsLocked();
        }
        return true;
    }

    public bool TryGetBinding(string openBoxItemId, out OpenBinding binding)
    {
        lock (StoreGate)
        {
            return BindingsLocked().TryGetValue(openBoxItemId, out binding!);
        }
    }

    // Drop a binding (called on close, once the storage has been written - and to release a claim whose box never got spawned).
    public void UnbindOpenBox(string openBoxItemId)
    {
        lock (StoreGate)
        {
            if (BindingsLocked().Remove(openBoxItemId))
            {
                PersistBindingsLocked();
            }
        }
    }

    // ---- Web-UI support -----------------------------------------------------------------------------

    // A raw summary of one storage for the web UI (names/nicknames/tree are resolved by the API layer).
    public sealed class StorageInfo
    {
        public string Id = "";
        public bool Open;
        public string? Holder;         // sessionId that has it open, if any
        public string? OpenBoxItemId;  // the live open box's item id in the holder's profile (for live contents)
        public string? OpenedUtc;
        // The FILE's contents (last-closed snapshot) and the id its top-level items name as their parent.
        // The API builds the display tree from these for a CLOSED box; an OPEN box's live tree comes from the holder's profile instead (via OpenBoxItemId).
        public string RootId = "";
        public List<Item> FileContents = new List<Item>();
        // The config `key` of the case type this stash belongs to ("" for pre-BoxKey storages).
        public string BoxKey = "";
        // The file exists but wouldn't parse - shown in the UI so a corrupt stash is obvious rather than silently looking empty.
        public bool Unreadable;
    }

    // Enumerate every registered storage (each <id>.jsonc; _bindings.json is .json so it's skipped), with its open/holder state cross-referenced from the bindings.
    public List<StorageInfo> ListStorages()
    {
        // storageId (lowercase) -> (openBoxItemId, binding). Binding.StorageId is already lowercased.
        var openByStorage = new Dictionary<string, (string boxItemId, OpenBinding binding)>();
        lock (StoreGate)
        {
            foreach (var pair in BindingsLocked())
            {
                if (!openByStorage.ContainsKey(pair.Value.StorageId))
                {
                    openByStorage[pair.Value.StorageId] = (pair.Key, pair.Value);
                }
            }
        }

        var result = new List<StorageInfo>();
        foreach (var file in Directory.GetFiles(StorageDir(), "*.jsonc"))
        {
            // Belt and braces: Windows still matches wildcards against 8.3 short names, so an explicit extension check keeps a leftover "<id>.jsonc.tmp" from a crashed write out of the listing.
            if (!file.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Ids fold to lowercase everywhere (tag parse, filenames); match that here so the binding lookup can't miss on case (a pre-existing mixed-case file would otherwise read as idle).
            string id = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var box = Load(id, out bool unreadable);

            var info = new StorageInfo
            {
                Id = id,
                RootId = box.RootId,
                FileContents = box.Contents,
                BoxKey = box.BoxKey,
                Unreadable = unreadable,
            };
            if (openByStorage.TryGetValue(id, out var open))
            {
                info.Open = true;
                info.OpenBoxItemId = open.boxItemId;
                info.Holder = open.binding.Holder;
                info.OpenedUtc = open.binding.OpenedUtc;
            }

            result.Add(info);
        }
        return result;
    }

    // Force-release a stuck storage: drop any bindings pointing at it and clear its holder, so it can be opened again.
    // RECOVERY ONLY - if the orphaned open box still exists in a player's profile, its contents duplicate the file's last-saved contents. Returns true if anything was cleared.
    public bool ForceClose(string id)
    {
        bool changed = false;

        lock (StoreGate)
        {
            var map = BindingsLocked();
            var toRemove = new List<string>();
            foreach (var pair in map)
            {
                if (pair.Value.StorageId == id)
                {
                    toRemove.Add(pair.Key);
                }
            }
            foreach (var key in toRemove)
            {
                if (map.Remove(key))
                {
                    changed = true;
                }
            }
            if (changed)
            {
                PersistBindingsLocked();
            }

            if (Exists(id))
            {
                var box = Load(id);
                if (box.Holder != null)
                {
                    box.Holder = null;
                    Save(box);
                    changed = true;
                }
            }
        }

        return changed;
    }
}
