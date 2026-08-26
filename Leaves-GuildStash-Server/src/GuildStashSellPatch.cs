using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// Refuse to SELL an OPEN guild box to a trader.
// Twin of GuildStashDiscardPatch, same reasoning: selling one destroys everyone's items and strands the storage lock.
//
// The declarative route does not work.
// A trader's `items_buy_prohibited` list is the "right" way to say "this trader won't buy that" - and GuildStashLateFilters does populate it, because the CLIENT reads it and greys the sell option out, which is the nicer UX.
// But NOTHING in the SPT server reads `ItemsBuyProhibited`, exactly like `IsUnsaleable`/`IsUndiscardable`: it's a client-side hint, so on its own it is not a guarantee. This patch is the guarantee.
//
// Harmony because `TradeHelper.SellItem` isn't virtual. Returning false skips the sale entirely (no items removed, no money paid) and the appended Warning makes the client resync so its view matches the profile.
public class GuildStashSellPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(TradeHelper).GetMethod(nameof(TradeHelper.SellItem))!;
    }

    [PatchPrefix]
    public static bool Prefix(
        PmcData profileWithItemsToSell,
        ProcessSellTradeRequestData sellRequest,
        MongoId sessionID,
        ItemEventRouterResponse output)
    {
        // Fail OPEN (return true): this is the generic sell path for every item in the game, so a fault here must never stop a player selling their loot.
        try
        {
            if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
            {
                return true;
            }
            if (sellRequest?.Items == null || profileWithItemsToSell?.Inventory?.Items == null)
            {
                return true;
            }

            // Index the ids being sold. SPT strips whitespace out of the incoming ids before matching, so mirror that here or a padded id would slip past this check and still sell.
            var sellingIds = new HashSet<string>();
            foreach (var itemToSell in sellRequest.Items)
            {
                if (itemToSell?.Id == null)
                {
                    continue;
                }
                sellingIds.Add(Regex.Replace(itemToSell.Id, @"\s+", ""));
            }
            if (sellingIds.Count == 0)
            {
                return true;
            }

            foreach (var item in profileWithItemsToSell.Inventory.Items)
            {
                if (!sellingIds.Contains(item.Id.ToString()))
                {
                    continue;
                }
                if (!GuildStashContext.IsOpenBox(item.Template))
                {
                    continue;
                }

                Leaves.Utils.Instance.printColor($"[GuildStash] Refused a trader sale of OPEN guild box {item.Id} by {sessionID}.", LogTextColor.Yellow);

#pragma warning disable CS0618
                using (var scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
                {
                    var httpResponseUtil = scope.ServiceProvider.GetService<HttpResponseUtil>();
                    if (httpResponseUtil != null)
                    {
                        httpResponseUtil.AppendErrorToOutput(
                            output,
                            "Guild Stash: close this box before selling it - selling it would destroy everything inside.",
                            BackendErrorCodes.None);
                    }
                }

                // Refuse the WHOLE sale, not just this item: the request is one basket, and letting the rest through while silently dropping this one would take payment for a different set of goods than the client asked to sell.
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Sell guard threw ({ex.Message}) - allowing the sale through.", LogTextColor.Red);
            return true;
        }
    }
}
