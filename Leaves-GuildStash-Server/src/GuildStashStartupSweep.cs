using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;

namespace Leaves_GuildStash_Server;

// On server startup, close EVERY open guild box across ALL profiles.
// This is the strongest crash-recovery net: anything left open by a crash / taskkill / hard restart is closed the moment the server comes back, releasing its lock for everyone immediately (rather than waiting for the original holder to log in and trip the GameStart reconcile).
// A clean run finds nothing open and this is a cheap all-profiles scan.
//
// Runs at PostSptModLoader (1100000) - AFTER SaveCallbacks (700000) has loaded every profile via SaveServer.LoadAsync, and long after GuildStashItems (PostDBModLoader+2) built the open/closed tpl maps.
// Each close is a full offline close (contents saved, box swapped, binding dropped) with the same pre-close backup zip as any other close.
// We explicitly persist each touched profile so the closed state hits disk now - there's no imminent client read to save it for us.
[Injectable(TypePriority = OnLoadOrder.PostSptModLoader)]
public class GuildStashStartupSweep(
    GuildStashSessionCleanup cleanup,
    GuildStashStorage storage,
    ProfileHelper profileHelper,
    SaveServer saveServer) : IOnLoad
{
    public async Task OnLoad()
    {
        if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
        {
            return;
        }

        var profiles = profileHelper.GetProfiles();
        int totalClosed = 0;
        int profilesTouched = 0;

        foreach (var pair in profiles)
        {
            var sessionId = pair.Key;

            // Per-profile try/catch. This runs as IOnLoad DURING BOOT: an escaping exception here doesn't just skip a cleanup, it takes the whole server down before it finishes starting.
            // A half-built profile, a locked storage file or a disk hiccup must cost us one profile's sweep, not the server. (The logout/login callers wrap their calls for the same reason.)
            try
            {
                int closed = cleanup.CloseAllForSession(sessionId, "startup");
                if (closed > 0)
                {
                    totalClosed += closed;
                    profilesTouched++;
                    // Persist the closed state to disk immediately (offline mutation - nothing else will save it soon). Base GameLogout uses the same SaveProfileAsync.
                    await saveServer.SaveProfileAsync(sessionId);
                }
            }
            catch (Exception ex)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] Startup sweep FAILED for profile {sessionId}: {ex.Message} - skipping it, continuing boot.", LogTextColor.Red);
            }
        }

        if (totalClosed > 0)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Startup sweep: closed {totalClosed} open box(es) across {profilesTouched} profile(s).", LogTextColor.Green);
        }

        try
        {
            SweepOrphanedBindings();
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Orphaned-binding sweep FAILED: {ex.Message} - continuing boot.", LogTextColor.Red);
        }
    }

    // Drop bindings whose open box no longer exists in ANY profile.
    //
    // The close paths all work from a box: CloseAllForSession scans a profile for open-tpl items and closes what it finds.
    // So if the open box itself stops existing - discarded, sold, lost with a container, or removed by another mod - nothing is left to close, the binding is never dropped, and the storage stays locked for EVERYONE with no way back short of a manual web-UI force-close.
    // This is the automatic recovery for that: a binding pointing at a box nobody has is meaningless, so release it.
    //
    // Releasing is safe and non-destructive: Open never rewrites the storage file, so the file still holds the contents from before that box was opened.
    // Dropping the binding simply returns the stash to its last-closed state - the items added during the lost session are gone either way.
    private void SweepOrphanedBindings()
    {
        var bindings = storage.GetBindingsSnapshot();
        if (bindings.Count == 0)
        {
            return;
        }

        // Every item id that exists anywhere, so "is this box real?" is one hash lookup per binding.
        var liveItemIds = new HashSet<string>();
        var profiles = profileHelper.GetProfiles();
        foreach (var pair in profiles)
        {
            var pmc = profileHelper.GetPmcProfile(pair.Key);
            if (pmc?.Inventory?.Items == null)
            {
                continue;
            }
            foreach (var item in pmc.Inventory.Items)
            {
                liveItemIds.Add(item.Id.ToString());
            }
        }

        int released = 0;
        foreach (var binding in bindings)
        {
            if (liveItemIds.Contains(binding.Key))
            {
                continue;
            }
            Leaves.Utils.Instance.printColor($"[GuildStash] Startup sweep: open box {binding.Key} for storage '{binding.Value.StorageId}' no longer exists (holder {binding.Value.Holder}) - releasing the lock; the stash keeps its last-closed contents.", LogTextColor.Yellow);
            storage.UnbindOpenBox(binding.Key);
            released++;
        }

        if (released > 0)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Startup sweep: released {released} orphaned lock(s).", LogTextColor.Green);
        }
    }
}
