using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;

namespace Leaves_GuildStash_Server;

// Closes any guild boxes a session left OPEN, offline (no live client to push changes to).
// Called on clean logout, login-reconcile, raid-start, and ws-disconnect.
// No binding lookup needed: any open-tpl box in the player's own profile is theirs. Snapshot the ids first (the close mutates the list), then close each via GuildStashCloser.
[Injectable]
public class GuildStashSessionCleanup(GuildStashCloser closer, ProfileHelper profileHelper)
{
    // Close every open guild box in this session's profile.
    // Safe to call unconditionally - no open boxes just scans, finds none, returns 0. `reason` is logged; returns the count closed.
    public int CloseAllForSession(MongoId sessionId, string reason)
    {
        PmcData? pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items == null)
        {
            return 0;
        }

        // snapshot ids before closing - each close mutates the item list, can't iterate it while closing
        List<string> openBoxIds = new();
        foreach (Item item in pmc.Inventory.Items)
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

        // re-find + per-box try/catch live in CloseBoxById. an unresolvable box is left open (already logged) for the player to retag.
        int closed = 0;
        foreach (string boxId in openBoxIds)
        {
            if (closer.CloseBoxById(pmc, boxId, sessionId, reason) == CloseBoxResult.Closed)
            {
                closed++;
            }
        }

        return closed;
    }
}
