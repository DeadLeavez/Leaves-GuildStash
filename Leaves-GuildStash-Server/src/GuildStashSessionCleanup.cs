using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;

namespace Leaves_GuildStash_Server;

// Closes any guild boxes a session left OPEN, offline (no live client to push profileChanges to).
// Driven by GuildStashGameCallbacks: on clean logout (before the profile is saved) and as a login-reconcile backstop for the one case neither logout nor the websocket-disconnect hook covers - a box that was open when the SERVER itself restarted.
//
// "Which boxes are this session's?" needs no binding lookup: any OPEN-tpl box physically sitting in a player's own profile is, by definition, theirs.
// We snapshot those item ids first (the close mutates the item list) and close each via the shared GuildStashCloser - which resolves the storage target from the persisted binding, falling back to the box's @remote tag.
[Injectable]
public class GuildStashSessionCleanup(GuildStashCloser closer, ProfileHelper profileHelper)
{
    // Close every open guild box in this session's profile.
    // Safe to call unconditionally: a session with no open boxes (the normal case) just scans its inventory, finds none, and returns 0.
    // `reason` is logged. Returns the number of boxes actually closed (so a caller can decide whether to persist the profile).
    public int CloseAllForSession(MongoId sessionId, string reason)
    {
        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items == null)
        {
            return 0;
        }

        // Snapshot the open-box item ids BEFORE closing any - CloseInProfile mutates the item list (removes the open box + subtree, spawns a fresh closed case), so we can't safely iterate it while closing.
        var openBoxIds = new List<string>();
        foreach (var item in pmc.Inventory.Items)
        {
            if (GuildStashContext.OpenToClosedTpl.ContainsKey(item.Template))
            {
                openBoxIds.Add(item.Id.ToString());
            }
        }
        if (openBoxIds.Count == 0)
        {
            return 0;
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] {reason}: closing {openBoxIds.Count} open box(es) for {sessionId}.", LogTextColor.Cyan);

        int closed = 0;
        foreach (var boxId in openBoxIds)
        {
            // Re-find the box each pass - a prior close reshaped the list (and never touches a DIFFERENT open box, since the closed replacement's tpl isn't an open tpl).
            Item? box = null;
            foreach (var item in pmc.Inventory.Items)
            {
                if (item.Id.ToString() == boxId)
                {
                    box = item;
                    break;
                }
            }
            if (box == null)
            {
                continue;
            }

            // Per-box try/catch: a close writes files, so it can throw on a disk/permission problem, and one unlucky box must not abort the whole sweep (nor, via the startup sweep, the server's boot).
            try
            {
                string? error = closer.CloseInProfile(pmc, box, sessionId, out _, out _);
                if (error != null)
                {
                    // Can't resolve a close target (no binding AND no valid tag).
                    // Leave the box open in their profile - they can retag and close it by hand - but log it so it isn't silently stuck.
                    Leaves.Utils.Instance.printColor($"[GuildStash] {reason}: could not auto-close box {boxId} for {sessionId}: {error}", LogTextColor.Red);
                }
                else
                {
                    closed++;
                }
            }
            catch (Exception ex)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] {reason}: auto-close of box {boxId} for {sessionId} THREW: {ex.Message} - leaving it open, continuing with the rest.", LogTextColor.Red);
            }
        }

        return closed;
    }
}
