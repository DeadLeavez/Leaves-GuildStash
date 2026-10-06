using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;

namespace Leaves_GuildStash_Server;

// CloseBoxById outcome.
public enum CloseBoxResult
{
    Closed,
    NotFound,    // id not in the profile: closed earlier this sweep, or a real orphan
    Unresolved,  // CloseInProfile refused it; box left open
    Threw,       // close threw mid-write; box left open
}

[Injectable]
public class GuildStashCloser(GuildStashStorage storage, SaveServer saveServer)
{
    // Returns null on success, or a player-facing error message.
    // Saves the storage file, the backup, and the profile on success.
    // deletedIds/createdIds: the item changes, only the event router reads it (to mirror the close to a live client).
    public string? CloseInProfile(PmcData pmc, Item box, MongoId callerId, out List<string> deletedIds, out List<Item> createdIds)
    {
        deletedIds = new List<string>();
        createdIds = new List<Item>();

        if (!GuildStashContext.OpenToClosedTpl.TryGetValue(box.Template, out MongoId closedTpl))
        {
            return "Guild Stash: that isn't an open guild box.";
        }

        // the binding wins over the live tag. a retag on an open box must not redirect the close
        string? taggedStorageId = GuildStashValidation.ParseRemoteTag(box, out _);
        string? targetStorageId;
        if (storage.TryGetBinding(box.Id.ToString(), out OpenBinding binding))
        {
            string bindingStorageId = binding.StorageId;
            targetStorageId = bindingStorageId;
            if (taggedStorageId != null && taggedStorageId != bindingStorageId)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] Close: tag was changed to '{taggedStorageId}' while open, but the box was opened as '{bindingStorageId}' - writing to '{bindingStorageId}'.", LogTextColor.Yellow);
            }
            else if (taggedStorageId == null)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] Close: tag was removed/invalidated while open - writing to the bound id '{bindingStorageId}'.", LogTextColor.Yellow);
            }
        }
        else
        {
            // no binding (server restarted since open, or a force-close dropped it), so fall back to the tag,
            // but only for an already-registered, currently-idle storage: without both guards a retag could aim this close at any id and fuck up another player's stash.
            targetStorageId = taggedStorageId;
            if (targetStorageId == null)
            {
                return "Guild Stash: this box needs a valid '@r <ID>;' tag (letters and numbers only).";
            }
            if (!storage.Exists(targetStorageId))
            {
                return $"Guild Stash: no storage '{targetStorageId}' exists - retag this box with the ID it was opened as, then close it.";
            }
            if (storage.IsOpenSomewhere(targetStorageId, out _))
            {
                return $"Guild Stash: storage '{targetStorageId}' is open in another box - retag this box with the ID it was opened as.";
            }
            Leaves.Utils.Instance.printColor($"[GuildStash] Close: no open-binding for this box (restart?) - falling back to tag '{targetStorageId}'.", LogTextColor.Yellow);
        }

        // recovery point: everything below mutates the storage file and the profile
        storage.SnapshotBeforeClose(targetStorageId, callerId);

        // grab the contents before ReplaceBox drops them
        (_, List<Item> descendants) = GuildStashBoxOps.GatherSubtree(pmc, box.Id.ToString());
        StoredBox stored = new()
        {
            Id = targetStorageId,
            RootId = box.Id.ToString(),
            Contents = descendants,
            Holder = null,
            // re-stamped every close, which also backfills the pre-BoxKey stashes that Open grandfathers in
            BoxKey = GuildStashContext.BoxKeyForTpl(box.Template),
        };
        storage.Save(stored);

        (deletedIds, createdIds) = GuildStashBoxOps.ReplaceBox(pmc, box, closedTpl, new MongoId(), box.Id.ToString(), new List<Item>());

        storage.UnbindOpenBox(box.Id.ToString());

        // storage file is already on disk; land the matching profile change too, or a crash here reopens the box with the items gone
        saveServer.SaveProfileAsync(callerId).GetAwaiter().GetResult();

        Leaves.Utils.Instance.printColor($"[GuildStash] Closed storage '{targetStorageId}' ({descendants.Count} item(s) saved).", LogTextColor.Green);
        return null;
    }

    // Saves the storage and profile on success (via CloseInProfile)
    public CloseBoxResult CloseBoxById(PmcData pmc, string boxId, MongoId callerId, string logCloserLabel)
    {
        if (pmc.Inventory?.Items == null)
        {
            return CloseBoxResult.NotFound;
        }

        Item? box = null;
        foreach (Item item in pmc.Inventory.Items)
        {
            if (item.Id.ToString() == boxId)
            {
                box = item;
                break;
            }
        }
        if (box == null)
        {
            return CloseBoxResult.NotFound;
        }

        try
        {
            string? error = CloseInProfile(pmc, box, callerId, out _, out _);
            if (error != null)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] {logCloserLabel}: could not close box {boxId} for {callerId}: {error}", LogTextColor.Red);
                return CloseBoxResult.Unresolved;
            }
            return CloseBoxResult.Closed;
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] {logCloserLabel}: close of box {boxId} for {callerId} THREW: {ex.Message} - leaving it open, continuing with the rest.", LogTextColor.Red);
            return CloseBoxResult.Threw;
        }
    }
}
