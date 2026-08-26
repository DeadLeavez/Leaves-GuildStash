using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// Refuse to discard an OPEN guild box, server-side.
//
// Discarding one destroys everyone's items and strands the storage lock (nothing left in any profile for a close to work from - it takes the startup orphan sweep to recover, and only back to the last-closed contents).
// The item template flags don't stop it: `IsUnsaleable`/`IsUndiscardable` are set on the open box and the client discarded it anyway, and NOTHING in the SPT server reads either one.
// So the block has to live where the deletion actually happens.
//
// Harmony, because `InventoryController.DiscardItem` isn't virtual (no DI TypeOverride) - and `InventoryCallbacks.RemoveItem`, one level up, isn't either.
//
// Returning false SKIPS the original, so the item is never removed, and we append a Warning so the client shows a reason and resyncs.
// The resync matters: without it the client would have already drawn the item as gone and would sit desynced from a profile that still has it.
//
// CLOSED boxes are deliberately still discardable - they're empty by construction and cost a GP coin to replace, so a player must be able to bin one they no longer want.
// The stash itself is untouched by discarding its box; the contents live in storage/<id>.jsonc.
public class GuildStashDiscardPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryController).GetMethod(nameof(InventoryController.DiscardItem))!;
    }

    [PatchPrefix]
    public static bool Prefix(PmcData pmcData, InventoryRemoveRequestData request, MongoId sessionId, ItemEventRouterResponse output)
    {
        // Anything unexpected must let the ORIGINAL run (return true).
        // This sits on the generic discard path used by every item in the game, so a fault here must never stop a player binning a bandage.
        try
        {
            if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
            {
                return true;
            }
            if (request == null || request.Item == null || pmcData?.Inventory?.Items == null)
            {
                return true;
            }

            string targetId = request.Item.ToString();
            foreach (var item in pmcData.Inventory.Items)
            {
                if (item.Id.ToString() != targetId)
                {
                    continue;
                }

                if (!GuildStashContext.IsOpenBox(item.Template))
                {
                    return true;
                }

                Leaves.Utils.Instance.printColor($"[GuildStash] Refused a discard of OPEN guild box {targetId} by {sessionId}.", LogTextColor.Yellow);

#pragma warning disable CS0618
                using (var scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
                {
                    var httpResponseUtil = scope.ServiceProvider.GetService<HttpResponseUtil>();
                    if (httpResponseUtil == null)
                    {
                        // No way to tell the player why.
                        // Letting the discard proceed would destroy shared items, so refuse silently instead - the client resyncs on its own soon enough and the box reappears.
                        return false;
                    }
                    httpResponseUtil.AppendErrorToOutput(
                        output,
                        "Guild Stash: close this box before getting rid of it - discarding it would destroy everything inside.",
                        BackendErrorCodes.None);
                }

                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Discard guard threw ({ex.Message}) - allowing the discard through.", LogTextColor.Red);
            return true;
        }
    }
}
