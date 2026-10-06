using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Serialization;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// One shared storage, persisted as storage/<ID>.jsonc.
// Contents is the item subtree inside the box while closed - ids here are throwaway (remapped on every open, overwritten wholesale on close).
public sealed class StoredBox
{
    // PROPERTIES not fields: SPT's JsonUtil serializes only properties; a public field silently round-trips as empty
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("contents")]
    public List<Item> Contents { get; set; } = new List<Item>();

    // the id top-level contents name as ParentId (the open box's id when it was closed). Remapped to the new open box on the next open. Empty if the box never held anything.
    [JsonPropertyName("rootId")]
    public string RootId { get; set; } = "";

    // sessionId of whoever last opened it, or null. Soft marker only - the real open-lock is the bindings (TryClaimStorage).
    [JsonPropertyName("holder")]
    public string? Holder { get; set; }

    // which KIND of guild box (config `key`). Checked on Open: a THICC's saved layout references cells an Item case doesn't have, so opening with the wrong type hands the client an inventory it can't lay out.
    // empty on pre-BoxKey storages - grandfathered (any box opens them), stamped on next close.
    [JsonPropertyName("boxKey")]
    public string BoxKey { get; set; } = "";
}

// One open-box item bound to a storage ID. Persisted (storage/_bindings.json) so a close after a restart still knows which file to write.
public sealed class OpenBinding
{
    // the storage this box was opened against - authoritative target for its close
    [JsonPropertyName("storageId")]
    public string StorageId { get; set; } = "";

    // sessionId that opened it, for diagnostics
    [JsonPropertyName("holder")]
    public string? Holder { get; set; }

    // ISO-8601 UTC, for stale-binding cleanup
    [JsonPropertyName("openedUtc")]
    public string? OpenedUtc { get; set; }
}

// Reads/writes the per-id storage files at storage/<ID>.jsonc.
// The csproj build-copy globs exclude storage/, so a rebuild never clobbers live storage.
// Items go through SPT's JsonUtil - plain System.Text.Json mangles MongoId.
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
        // lowercase the filename - case-consistent on Linux too, even though ids already fold in ParseRemoteTag
        return System.IO.Path.Combine(StorageDir(), id.ToLowerInvariant() + ".jsonc");
    }

    // storage by id, or an empty StoredBox if the file doesn't exist yet (a never-opened id = empty box)
    public StoredBox Load(string id)
    {
        return Load(id, out _);
    }

    // as above, but `unreadable` splits "no file yet" (false, a legit empty stash) from "file won't parse" (true).
    // matters: treat a corrupt file as empty and the next Close overwrites it - recoverable corruption becomes permanent loss. Callers acting on the contents (Open) must refuse when true.
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
                StoredBox? box = jsonUtil.Deserialize<StoredBox>(raw);
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

    // atomic: write to a temp file, then move it over - a crash mid-write can't leave a half-written storage.
    // StoreGate too: keeps in-process readers off the file between write and move, and dodges a Windows sharing-violation on the move.
    public void Save(StoredBox box)
    {
        string file = PathFor(box.Id);
        string json = jsonUtil.Serialize(box, true)!;

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

    // Called by the close path JUST BEFORE it overwrites storage/<id>.jsonc and mutates the profile.
    // Zips a recovery snapshot to storage/backups/<id>_<utc>.zip: the previous storage file + the holder's whole profile (the open box still has the live items, so it's the copy to restore from).
    // Keeps EVERY snapshot for now (hoard rather than lose progress); add a keep-last-N prune later if it grows.
    // Best-effort: failures are logged, never block the close. ListStorages doesn't scan backups/.
    public void SnapshotBeforeClose(string id, MongoId sessionId)
    {
        try
        {
            string backupDir = System.IO.Path.Combine(StorageDir(), "backups");
            Directory.CreateDirectory(backupDir);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            string zipPath = System.IO.Path.Combine(backupDir, id.ToLowerInvariant() + "_" + stamp + ".zip");

            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                // previous storage snapshot (absent on a first-ever close)
                string storageFile = PathFor(id);
                if (File.Exists(storageFile))
                {
                    zip.CreateEntryFromFile(storageFile, id.ToLowerInvariant() + ".jsonc");
                }

                // holder's full profile from memory (pre-mutation). separate try so a fetch hiccup still leaves the storage snapshot in the zip.
                try
                {
                    SptProfile? profile = profileHelper.GetFullProfile(sessionId);
                    if (profile != null)
                    {
                        string profileJson = jsonUtil.Serialize(profile, true)!;
                        ZipArchiveEntry entry = zip.CreateEntry("profile_" + sessionId.ToString() + ".json");
                        using (Stream stream = entry.Open())
                        using (StreamWriter writer = new(stream))
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

    // registered = a file exists = was created through Convert. Open requires it; Convert forbids it.
    public bool Exists(string id)
    {
        return File.Exists(PathFor(id));
    }

    // register a new storage atomically - fails if the id is taken. one guarded step so two converts of the same fresh id can't both pass.
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

    // is this storage open somewhere (a binding points at it)? Scans persisted bindings, so it survives a restart.
    // PREFLIGHT only - the authoritative open-lock is TryClaimStorage.
    public bool IsOpenSomewhere(string id, out string? holder)
    {
        holder = null;
        lock (StoreGate)
        {
            foreach (OpenBinding binding in BindingsLocked().Values)
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

    // STATIC on purpose. [Injectable] with no type = Scoped, so DI hands out a fresh GuildStashStorage per request.
    // Per-instance state would give concurrent requests their own binding map AND their own lock: _bindings.json writes lose updates and the lock guards nothing. Static state behind a static gate is the only thing shared across requests.
    // The gate guards the storage FILES too (claim/write), so check-then-write can't interleave. C# locks are reentrant, so nested public calls are fine.
    // Side effect: the map is cached for the server's life, so a hand-edit of _bindings.json needs a restart.
    private static readonly object StoreGate = new object();
    private static Dictionary<string, OpenBinding>? bindings;

    private string BindingsPath()
    {
        return System.IO.Path.Combine(StorageDir(), "_bindings.json");
    }

    // lazy-load the persisted bindings on first access. Caller MUST hold StoreGate.
    private Dictionary<string, OpenBinding> BindingsLocked()
    {
        if (bindings != null)
        {
            return bindings;
        }

        Dictionary<string, OpenBinding> loaded = new();
        string file = BindingsPath();
        if (File.Exists(file))
        {
            try
            {
                string raw = File.ReadAllText(file);
                Dictionary<string, OpenBinding>? map = jsonUtil.Deserialize<Dictionary<string, OpenBinding>>(raw);
                if (map != null)
                {
                    foreach (KeyValuePair<string, OpenBinding> pair in map)
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

    // Caller MUST hold StoreGate. Atomic like Save - a half-written _bindings.json drops every binding at once.
    private void PersistBindingsLocked()
    {
        Dictionary<string, OpenBinding> plain = new(BindingsLocked());
        string json = jsonUtil.Serialize(plain, true)!;
        WriteFileAtomic(BindingsPath(), json);
    }

    // copy of every binding, for the startup orphan sweep
    public Dictionary<string, OpenBinding> GetBindingsSnapshot()
    {
        lock (StoreGate)
        {
            return new Dictionary<string, OpenBinding>(BindingsLocked());
        }
    }

    // THE open-lock: atomically claim a storage for an about-to-be-spawned open box, refusing if any binding already points at it.
    // The box id is minted by the CALLER so the claim can name it before any profile mutation. Returns false + the current holder when taken.
    public bool TryClaimStorage(string openBoxItemId, string storageId, string? holder, out string? currentHolder)
    {
        currentHolder = null;
        lock (StoreGate)
        {
            Dictionary<string, OpenBinding> map = BindingsLocked();
            foreach (OpenBinding binding in map.Values)
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

    // drop a binding: on close (after the storage is written), or to release a claim whose box never spawned
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

    // raw summary of one storage for the web UI; names/tree resolved by the API layer
    public sealed class StorageInfo
    {
        public string Id = "";
        public bool Open;
        public string? Holder;         // sessionId that has it open, if any
        public string? OpenBoxItemId;  // the live open box's item id in the holder's profile (for live contents)
        public string? OpenedUtc;
        // the file's last-closed contents + their parent id. The API trees these for a CLOSED box; an OPEN box's tree comes from the holder's profile.
        public string RootId = "";
        public List<Item> FileContents = new List<Item>();
        // config `key` of the case type ("" for pre-BoxKey storages)
        public string BoxKey = "";
        // file exists but won't parse - surfaced in the UI so a corrupt stash isn't silently empty
        public bool Unreadable;
    }

    // every registered storage (<id>.jsonc; _bindings.json is skipped), open/holder state cross-referenced from bindings
    public List<StorageInfo> ListStorages()
    {
        // storageId (lowercase) -> (openBoxItemId, binding). Binding.StorageId is already lowercased.
        Dictionary<string, (string boxItemId, OpenBinding binding)> openByStorage = new();
        lock (StoreGate)
        {
            foreach (KeyValuePair<string, OpenBinding> pair in BindingsLocked())
            {
                if (!openByStorage.ContainsKey(pair.Value.StorageId))
                {
                    openByStorage[pair.Value.StorageId] = (pair.Key, pair.Value);
                }
            }
        }

        List<StorageInfo> result = new();
        foreach (string file in Directory.GetFiles(StorageDir(), "*.jsonc"))
        {
            // Windows matches wildcards against 8.3 short names too, so an explicit check keeps a leftover ".jsonc.tmp" out
            if (!file.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // lowercase to match ids everywhere else, or the binding lookup misses on case and a mixed-case file reads as idle
            string id = System.IO.Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            StoredBox box = Load(id, out bool unreadable);

            StorageInfo info = new()
            {
                Id = id,
                RootId = box.RootId,
                FileContents = box.Contents,
                BoxKey = box.BoxKey,
                Unreadable = unreadable,
            };
            if (openByStorage.TryGetValue(id, out (string boxItemId, OpenBinding binding) open))
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

    // Force-release a stuck storage: drop bindings pointing at it, clear its holder.
    // RECOVERY ONLY - if the orphaned open box still exists in a profile, its contents will duplicate the file's. Returns true if anything changed.
    public bool ForceClose(string id)
    {
        bool changed = false;

        lock (StoreGate)
        {
            Dictionary<string, OpenBinding> map = BindingsLocked();
            List<string> toRemove = new();
            foreach (KeyValuePair<string, OpenBinding> pair in map)
            {
                if (pair.Value.StorageId == id)
                {
                    toRemove.Add(pair.Key);
                }
            }
            foreach (string key in toRemove)
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
                StoredBox box = Load(id);
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
