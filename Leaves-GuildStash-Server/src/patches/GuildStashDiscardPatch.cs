using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// Refuse to discard an OPEN guild box, server-side.
//
// Discarding one destroys everyone's items and strands the lock (recovery is the startup sweep, back to last-closed only).
// The item-template flags don't stop it - IsUnsaleable/IsUndiscardable are set and the client discards anyway, and SPT reads neither - so the block lives where the deletion happens.
// Harmony because InventoryController.DiscardItem isn't virtual (nor is InventoryCallbacks.RemoveItem one level up).
//
// Return false skips the original; the appended Warning gives the player a reason AND resyncs the client, which has already drawn the item as gone.
//
// CLOSED boxes stay discardable on purpose - empty, a GP coin to replace. The stash is untouched either way; contents live in storage/<id>.jsonc.
// This is in *theory* impossible to ever happen.
public class GuildStashDiscardPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(InventoryController).GetMethod(nameof(InventoryController.DiscardItem))!;
    }

    [PatchPrefix]
    public static bool Prefix(PmcData pmcData, InventoryRemoveRequestData request, MongoId sessionId, ItemEventRouterResponse output)
    {
        // on fault, let the ORIGINAL run (return true): this is the discard path for every item in the game - a bug here must never stop someones discarding
        try
        {
            if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
            {
                return true;
            }
            if (request == null || request.Item.IsEmpty || pmcData?.Inventory?.Items == null)
            {
                return true;
            }

            string targetId = request.Item.ToString();
            foreach (Item item in pmcData.Inventory.Items)
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
                using (IServiceScope scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
                {
                    HttpResponseUtil? httpResponseUtil = scope.ServiceProvider.GetService<HttpResponseUtil>();
                    if (httpResponseUtil == null)
                    {
                        // can't tell the player why, but proceeding would destroy shared items - refuse silently, the client resyncs soon enough
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
