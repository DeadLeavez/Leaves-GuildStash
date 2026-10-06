using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Services;

namespace Leaves_GuildStash_Server;

// The blanket "a guild box lives in the stash and nowhere else" filter pass, split out of GuildStashItems so it can run LATE.
//
// Late because SVM loads after us and rewrites the grid filters of the containers it manages, wiping our exclusion off the cases it touches (notably the two we clone from). Whoever writes last wins: +99000 is still inside the DB-mod phase.
// A guild box somewhere it shouldn't be = a mod loading even later rewrote that container. Check the log count first.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 99000)]
public class GuildStashLateFilters(DatabaseService databaseService) : IOnLoad
{
    // topmost "Item" node - whitelisting a grid to just this allows everything
    private const string ItemRootTpl = "54009119af1c881c07000029";

    public Task OnLoad()
    {
        if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
        {
            return Task.CompletedTask;
        }

        BlacklistGuildBoxesEverywhere(databaseService.GetItems());
        ProhibitTraderPurchase();
        return Task.CompletedTask;
    }

    // A guild box belongs in the stash and NOWHERE else: blacklist every guild tpl from every grid in the game.
    // Blanket, not mirror-the-base: our boxes are clones with new tpls, so no existing ExcludedFilter names them - mirroring only covers grids that name the base and misses the rest. Excluding ours everywhere has no holes.
    // One exemption: the stash - or a box couldn't be owned. Subtle: the stash grid ships with zero filter entries, so the synthesise branch below would otherwise lock boxes out of the stash entirely.
    private const string StashParentNode = "566abbb64bdc2d144c8b457d";

    // Stop every trader BUYING a guild box (adds our tpls to each trader's items_buy_prohibited).
    // Selling a full open box to a vendor destroys it - everyone's items go with it and the lock strands until the next restart's orphan sweep.
    // Not the item's own flags: IsUnsaleable/IsUndiscardable are set and the client sells/discards anyway, and SPT reads neither. The server-side blocks here and in GuildStashDiscardPatch are the real ones.
    // Late so trader data is fully loaded and any mod that rewrites trader bases has already run.
    private void ProhibitTraderPurchase()
    {
        HashSet<MongoId> allGuildTpls = new();
        foreach (MongoId tpl in GuildStashContext.ClosedToOpenTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        foreach (MongoId tpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        if (allGuildTpls.Count == 0)
        {
            return;
        }

        int tradersPatched = 0;
        foreach (KeyValuePair<MongoId, Trader> pair in databaseService.GetTraders())
        {
            TraderBase traderBase = pair.Value.Base;
            if (traderBase == null)
            {
                continue;
            }

            // some traders ship without the block - give them an empty one
            if (traderBase.ItemsBuyProhibited == null)
            {
                traderBase.ItemsBuyProhibited = new ItemBuyData
                {
                    Category = new HashSet<MongoId>(),
                    IdList = new HashSet<MongoId>(),
                };
            }
            if (traderBase.ItemsBuyProhibited.IdList == null)
            {
                traderBase.ItemsBuyProhibited.IdList = new HashSet<MongoId>();
            }

            foreach (MongoId guildTpl in allGuildTpls)
            {
                traderBase.ItemsBuyProhibited.IdList.Add(guildTpl);
            }
            tradersPatched++;
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] Guild boxes marked unsellable at {tradersPatched} trader(s).", LogTextColor.Cyan);
    }

    private void BlacklistGuildBoxesEverywhere(Dictionary<MongoId, TemplateItem> itemDb)
    {
        HashSet<MongoId> allGuildTpls = new();
        foreach (MongoId tpl in GuildStashContext.ClosedToOpenTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        foreach (MongoId tpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        if (allGuildTpls.Count == 0)
        {
            return;
        }

        MongoId itemRoot = new(ItemRootTpl);
        MongoId stashNode = new(StashParentNode);

        int containersPatched = 0;
        int filtersSynthesised = 0;

        foreach (KeyValuePair<MongoId, TemplateItem> entry in itemDb)
        {
            TemplateItem template = entry.Value;
            if (template.Properties == null || template.Properties.Grids == null)
            {
                continue;
            }
            if (template.Parent == stashNode)
            {
                continue;
            }

            bool touched = false;
            foreach (Grid grid in template.Properties.Grids)
            {
                if (grid.Properties == null)
                {
                    continue;
                }

                // an empty filter list accepts anything - synthesise one that still accepts anything (Item root) but can carry our exclusion, or the ban has a hole where a container is most permissive
                bool hasFilter = false;
                if (grid.Properties.Filters != null)
                {
                    foreach (GridFilter existing in grid.Properties.Filters)
                    {
                        hasFilter = true;
                        break;
                    }
                }
                if (!hasFilter)
                {
                    // most invasive thing this pass does - log which grids so a misbehaving container can be traced
                    if (GuildStashContext.Config.Debug)
                    {
                        Leaves.Utils.Instance.printColor($"[GuildStash] \tsynthesising a filter on unfiltered grid '{grid.Name}' of {entry.Key} ({template.Name}).", LogTextColor.Yellow);
                    }

                    grid.Properties.Filters = new List<GridFilter>
                    {
                        new GridFilter
                        {
                            Filter = new HashSet<MongoId> { itemRoot },
                            ExcludedFilter = new HashSet<MongoId>(),
                        },
                    };
                    filtersSynthesised++;
                }

                foreach (GridFilter filter in grid.Properties.Filters!) // non-null: synthesised just above when it was null/empty
                {
                    if (filter.ExcludedFilter == null)
                    {
                        filter.ExcludedFilter = new HashSet<MongoId>();
                    }
                    foreach (MongoId guildTpl in allGuildTpls)
                    {
                        filter.ExcludedFilter.Add(guildTpl);
                    }
                    touched = true;
                }
            }

            if (touched)
            {
                containersPatched++;
            }
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] Guild boxes blacklisted from {containersPatched} container template(s) ({filtersSynthesised} unfiltered grid(s) given a filter); stash exempt.", LogTextColor.Cyan);
    }
}
