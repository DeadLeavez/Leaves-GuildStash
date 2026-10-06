using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Trade;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// Refuse to SELL an OPEN guild box to a trader. Twin of GuildStashDiscardPatch - selling one destroys everyone's items and strands the lock.
//
// items_buy_prohibited is the declarative way and GuildStashLateFilters does set it (the client greys out the sell option, nicer UX), but SPT reads none of it server-side. This patch is the actual guarantee.
// Harmony because TradeHelper.SellItem isn't virtual. Return false skips the sale; the Warning resyncs the client.
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
        // fail OPEN (return true): generic sell path for every item - a bug here must never block a player's sale
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

            // SPT strips whitespace from incoming ids before matching - mirror that or a padded id slips past and still sells
            HashSet<string> sellingIds = new();
            foreach (SoldItem itemToSell in sellRequest.Items)
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

            foreach (Item item in profileWithItemsToSell.Inventory.Items)
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
                using (IServiceScope scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
                {
                    HttpResponseUtil? httpResponseUtil = scope.ServiceProvider.GetService<HttpResponseUtil>();
                    if (httpResponseUtil != null)
                    {
                        httpResponseUtil.AppendErrorToOutput(
                            output,
                            "Guild Stash: close this box before selling it - selling it would destroy everything inside.",
                            BackendErrorCodes.None);
                    }
                }

                // refuse the whole sale, not just this item - it's one basket; dropping one silently would pay out for the wrong goods
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
