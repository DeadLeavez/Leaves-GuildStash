using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Logging;

namespace Leaves_GuildStash_Server;

// On server startup, close EVERY open guild box across ALL profiles.
// Strongest crash-recovery net: a box left open by a crash / taskkill is closed the moment the server is back, releasing its lock for everyone (not waiting for the holder to log in). A clean run finds nothing - a cheap all-profiles scan.
// PostSptModLoader: after SaveCallbacks has loaded every profile, and after GuildStashItems built the tpl maps.
// Each close is a full offline close (saved, swapped, binding dropped, profile saved) with the usual backup zip.
[Injectable(TypePriority = OnLoadOrder.PostSptModLoader)]
public class GuildStashStartupSweep(
    GuildStashSessionCleanup cleanup,
    GuildStashStorage storage,
    ProfileHelper profileHelper) : IOnLoad
{
    public Task OnLoad()
    {
        if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
        {
            return Task.CompletedTask;
        }

        Dictionary<MongoId, SptProfile> profiles = profileHelper.GetProfiles();
        int totalClosed = 0;
        int profilesTouched = 0;

        foreach (KeyValuePair<MongoId, SptProfile> pair in profiles)
        {
            MongoId sessionId = pair.Key;

            // per-profile try/catch: this is IOnLoad during BOOT, so an escaping exception takes the whole server down. one bad profile costs its sweep, not the server.
            try
            {
                int closed = cleanup.CloseAllForSession(sessionId, "startup");
                if (closed > 0)
                {
                    totalClosed += closed;
                    profilesTouched++;
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

        return Task.CompletedTask;
    }

    // Drop bindings whose open box no longer exists in ANY profile.
    // The close paths all work from a box, so if the box is gone (discarded / sold / lost with a container / removed by a mod) nothing closes it and the storage stays locked for everyone. This releases those.
    // Safe: Open never rewrites the storage file, so dropping the binding just returns the stash to its last-closed state. Items from the lost session are gone either way.
    private void SweepOrphanedBindings()
    {
        Dictionary<string, OpenBinding> bindings = storage.GetBindingsSnapshot();
        if (bindings.Count == 0)
        {
            return;
        }

        // every item id anywhere, so "is this box real?" is one lookup per binding
        HashSet<string> liveItemIds = new();
        Dictionary<MongoId, SptProfile> profiles = profileHelper.GetProfiles();
        foreach (KeyValuePair<MongoId, SptProfile> pair in profiles)
        {
            PmcData? pmc = profileHelper.GetPmcProfile(pair.Key);
            if (pmc?.Inventory?.Items == null)
            {
                continue;
            }
            foreach (Item item in pmc.Inventory.Items)
            {
                liveItemIds.Add(item.Id.ToString());
            }
        }

        int released = 0;
        foreach (KeyValuePair<string, OpenBinding> binding in bindings)
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
