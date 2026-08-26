using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;

namespace Leaves_GuildStash_Server;

// Close every open guild box when a player deploys into a raid.
//
// WHY THIS IS SAFE (checked in the 4.0.13 source, not assumed - it's the whole question):
// post-raid, LocationLifecycleService.EndLocalRaid -> HandlePostRaidPmc -> InRaidHelper.SetInventory only removes and re-adds the items under `Inventory.Equipment` and `Inventory.QuestRaidItems`.
// The STASH is never read from the client's post-raid profile - the server's copy is authoritative across a raid.
// So a box closed here CANNOT be resurrected by the raid ending, and its contents cannot be duplicated (which is exactly what would happen if the client's in-raid stash copy were the one that won).
//
// WHY DO IT AT ALL: a box left open holds the storage lock for the entire raid, so nobody else can use that stash for 40 minutes - and if the player crashes mid-raid the lock survives until the next server restart.
// Deploying is a natural "you're done with the stash for a while" moment.
//
// PREFIX, not postfix: the box is closed before the raid is built, so nothing the raid records can reference an open box.
// The player's client shows its own stale stash copy during the raid and reloads it from the server on return, which is where they'll see the box closed.
//
// Fika: every REAL player's client hits /client/match/local/start when they deploy (the headless host uses /fika/raid/headless/start instead and never touches this - and a headless owns no boxes anyway).
// Same empirical finding LootFuckery's StartLocalRaidPatch relies on.
// A map-to-map transit fires this again, which is a harmless no-op once nothing is open.
public class GuildStashRaidStartPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.StartLocalRaid))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId)
    {
        // Whole body guarded. This sits on the live deploy path: a throwing prefix propagates into the route handler and the player simply cannot enter a raid.
        // Failing to close a box is a minor annoyance (the lock is held a while longer, and logout / the startup sweep still catch it); blocking a raid is not. Nothing in here may escape, service lookup included.
        try
        {
            if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
            {
                return;
            }

            // GuildStashSessionCleanup is SCOPED (plain [Injectable]), so it cannot be resolved from the root provider the way LootFuckery resolves its singleton - we have to open a scope.
            // That's safe for our state: GuildStashStorage's bindings and its lock are STATIC precisely because DI hands out a fresh instance per scope, and the profile objects come from the singleton SaveServer, so a throwaway scope still sees the same locks and mutates the same live profiles.
#pragma warning disable CS0618
            using (var scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
            {
                var cleanup = scope.ServiceProvider.GetService<GuildStashSessionCleanup>();
                if (cleanup == null)
                {
                    return;
                }

                int closed = cleanup.CloseAllForSession(sessionId, "raid-start");
                if (closed == 0)
                {
                    return;
                }

                // Persist immediately. The raid end saves the profile anyway, but a crash mid-raid would otherwise lose the close and strand the lock until the next server restart - which is one of the cases this whole hook exists to remove.
                var saveServer = scope.ServiceProvider.GetService<SaveServer>();
                if (saveServer != null)
                {
                    saveServer.SaveProfileAsync(sessionId).GetAwaiter().GetResult();
                }

                Leaves.Utils.Instance.printColor($"[GuildStash] raid-start: closed {closed} open box(es) for {sessionId} before deploy.", LogTextColor.Cyan);
            }
        }
        catch (Exception)
        {
            // Swallowed by design - see above. A raid must always be enterable.
        }
    }
}
