using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Services;

namespace Leaves_GuildStash_Server;

// Close every open guild box when a player deploys into a raid.
//
// Safe because the stash is server-authoritative across a raid: post-raid SetInventory only re-applies Inventory.Equipment + QuestRaidItems from the client, never the stash. A box closed here can't be resurrected or its contents duped.
// Why bother: an open box holds the storage lock for the whole raid (and past a mid-raid crash, until restart). Deploying is a natural "done for a while" point.
// Prefix not postfix: close before the raid is built, so nothing it records references an open box.
// Fika: every real player's client hits StartLocalRaid on deploy (headless uses a different route, owns no boxes anyway). Map transit re-fires this - a no-op once nothing's open.
public class GuildStashRaidStartPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(LocationLifecycleService).GetMethod(nameof(LocationLifecycleService.StartLocalRaid))!;
    }

    [PatchPrefix]
    public static void Prefix(MongoId sessionId)
    {
        // whole body guarded: a throwing prefix on this path means the player can't enter a raid. a missed close is minor (logout / startup sweep still catch it); a blocked raid is not.
        try
        {
            if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
            {
                return;
            }

            // GuildStashSessionCleanup is Scoped, so open a scope to resolve it. Safe: the storage bindings + lock are static, and profiles come from the singleton SaveServer, so a throwaway scope still hits the same state.
#pragma warning disable CS0618
            using (IServiceScope scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
            {
                GuildStashSessionCleanup? cleanup = scope.ServiceProvider.GetService<GuildStashSessionCleanup>();
                if (cleanup == null)
                {
                    return;
                }

                int closed = cleanup.CloseAllForSession(sessionId, "raid-start");
                if (closed == 0)
                {
                    return;
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
