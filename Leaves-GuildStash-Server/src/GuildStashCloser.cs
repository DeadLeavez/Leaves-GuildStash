using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;

namespace Leaves_GuildStash_Server;

// The close mutation, factored out of the item-event router so it can run WITHOUT a live client too.
// A "close" turns an OPEN guild box back into an empty CLOSED case: it saves the box's current contents to storage/<id>.jsonc, swaps the open box for a fresh empty closed case in the profile, and drops the open-binding.
// It mutates the passed PmcData directly and reports the delta via `deleted`/`created`.
//
// The item-event path (GuildStashItemEventRouter) mirrors that delta into profileChanges so a live client applies it instantly.
// The offline path (GuildStashSessionCleanup, on logout / login-reconcile) ignores the delta entirely - there's no live client to push to; the mutated profile is simply saved to disk and the player sees a closed box the next time their client reads it.
[Injectable]
public class GuildStashCloser(GuildStashStorage storage)
{
    // Close an OPEN guild box in `pmc`: save its contents to storage, replace it with an empty CLOSED case.
    // Returns null on success, or a player-facing error message (no valid close target, wrong item, etc.).
    // Callers that echo to a live client use `deleted`/`created`; the offline callers discard them.
    // `sessionId` is the box owner - used to snapshot their whole profile into the pre-close backup zip.
    public string? CloseInProfile(PmcData pmc, Item box, MongoId sessionId, out List<string> deleted, out List<Item> created)
    {
        deleted = new List<string>();
        created = new List<Item>();

        if (!GuildStashContext.OpenToClosedTpl.TryGetValue(box.Template, out var closedTpl))
        {
            return "Guild Stash: that isn't an open guild box.";
        }

        // Resolve which storage to write to.
        // The BINDING (recorded when this box was opened) wins over the current tag - editing the tag while open must NOT redirect the close to another file.
        // The tag is only a fallback for when the binding is gone (e.g. server restarted since the box was opened).
        string? tagId = GuildStashValidation.ParseRemoteTag(box, out _);
        string? id;
        if (storage.TryGetBinding(box.Id.ToString(), out var binding))
        {
            string boundId = binding.StorageId;
            id = boundId;
            if (tagId != null && tagId != boundId)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] Close: tag was changed to '{tagId}' while open, but the box was opened as '{boundId}' - writing to '{boundId}'.", LogTextColor.Yellow);
            }
            else if (tagId == null)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] Close: tag was removed/invalidated while open - writing to the bound id '{boundId}'.", LogTextColor.Yellow);
            }
        }
        else
        {
            // No binding (server restarted since open, or a force-close dropped it) - trust the tag, but only as far as an ALREADY-REGISTERED, not-currently-open storage.
            // Without those two guards a retag while open could point this close at ANY id and overwrite another player's stash wholesale (or mint an unregistered one), with nothing but the backup zip as a trace.
            id = tagId;
            if (id == null)
            {
                return "Guild Stash: this box needs a valid '@remote <ID>' tag (letters and numbers only).";
            }
            if (!storage.Exists(id))
            {
                return $"Guild Stash: no storage '{id}' exists - retag this box with the ID it was opened as, then close it.";
            }
            if (storage.IsOpenSomewhere(id, out _))
            {
                return $"Guild Stash: storage '{id}' is open in another box - retag this box with the ID it was opened as.";
            }
            Leaves.Utils.Instance.printColor($"[GuildStash] Close: no open-binding for this box (restart?) - falling back to tag '{id}'.", LogTextColor.Yellow);
        }

        // Hard backup BEFORE any destructive write: zip the previous storage file + the holder's whole profile (which still has the open box full of items) so a mis-save can always be recovered.
        storage.SnapshotBeforeClose(id, sessionId);

        // Snapshot the current contents (live instances, values captured on serialize) BEFORE we delete them.
        var (_, descendants) = GuildStashBoxOps.GatherSubtree(pmc, box.Id.ToString());
        var stored = new StoredBox
        {
            Id = id,
            RootId = box.Id.ToString(),
            Contents = descendants,
            Holder = null,
            // (Re-)stamp the case type on every close, which is also what backfills the pre-BoxKey storages that Open currently grandfathers in.
            BoxKey = GuildStashContext.BoxKeyForTpl(box.Template),
        };
        storage.Save(stored);

        // Spawn a fresh EMPTY closed case in its place.
        var (del, cre) = GuildStashBoxOps.ReplaceBox(pmc, box, closedTpl, new MongoId(), box.Id.ToString(), new List<Item>());
        deleted = del;
        created = cre;

        // Storage written and the open box is gone - drop its binding.
        storage.UnbindOpenBox(box.Id.ToString());

        Leaves.Utils.Instance.printColor($"[GuildStash] Closed storage '{id}' ({descendants.Count} item(s) saved).", LogTextColor.Green);
        return null;
    }
}
