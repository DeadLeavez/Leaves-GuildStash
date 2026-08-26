using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Services;

namespace Leaves_GuildStash_Server;

// The blanket "a guild box lives in the stash and nowhere else" filter pass, split out of GuildStashItems so it can run LATE.
//
// WHY LATE (learned the hard way 2026-08-25): run inside GuildStashItems at PostDBModLoader+2, this pass added the guild tpls to every container's ExcludedFilter - except the two the config actually clones from (Item case, THICC).
// SVM loads after us and rewrites the grid filters of the containers it manages, resetting them to "Filter=[ItemRoot], ExcludedFilter=[]" and wiping our exclusion off exactly those two.
// Whoever writes last wins, so we write last: PostDBModLoader + 99000 is still inside the DB-mod phase (item templates are read per-request afterwards, so nothing has cached them yet).
//
// If a guild box ever turns up somewhere it shouldn't, suspect a mod that loads even later and rewrites that container's filters - check the count in the log line below first.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 99000)]
public class GuildStashLateFilters(DatabaseService databaseService) : IOnLoad
{
    // The topmost "Item" node - every real item descends from it, so a grid whitelisted to just this allows everything.
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

    // A guild box belongs in the stash and NOWHERE else: blacklist every guild tpl (closed AND open) from every grid in the game.
    //
    // Why a blanket ban rather than mirroring the base case's exclusions: our boxes are CLONES with brand-new tpls, so no existing ExcludedFilter names them - a guild box could go where its vanilla original can't (a backpack, and from there into a raid, lost with everyone's items in it).
    // Mirroring the base only covers grids that name the base and silently misses the rest; excluding ours everywhere has no holes.
    //
    // ONE exemption: the STASH itself - or a guild box couldn't be owned at all.
    // It matters more than it looks: the stash grid ships with ZERO filter entries, so the "no filters yet" branch below would synthesise one and lock guild boxes out of the stash entirely.
    private const string StashParentNode = "566abbb64bdc2d144c8b457d";

    // Stop every trader BUYING a guild box, by adding our tpls to each trader's `items_buy_prohibited` id-list.
    // Selling a full open box to a vendor destroys it: everyone's items go with it and the storage lock is stranded until the orphan sweep at the next restart.
    //
    // Not the item template's own flags: `IsUnsaleable`/`IsUndiscardable` are set on the open box and the client sold/discarded it anyway, and NOTHING in the SPT server reads them.
    // `IsUnremovable` might be the real lever but is deliberately NOT set - our own Close tells the client to REMOVE the open box, so blocking client-side removal could break the close.
    // The server-side blocks here and in GuildStashDiscardPatch don't have that failure mode.
    //
    // Runs in the LATE pass with the filters: trader data is fully loaded and any mod that rewrites trader bases has already had its turn.
    private void ProhibitTraderPurchase()
    {
        var allGuildTpls = new HashSet<MongoId>();
        foreach (var tpl in GuildStashContext.ClosedToOpenTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        foreach (var tpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        if (allGuildTpls.Count == 0)
        {
            return;
        }

        int tradersPatched = 0;
        foreach (var pair in databaseService.GetTraders())
        {
            var traderBase = pair.Value.Base;
            if (traderBase == null)
            {
                continue;
            }

            // Some traders ship without the block entirely - give them an empty one to fill.
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

            foreach (var guildTpl in allGuildTpls)
            {
                traderBase.ItemsBuyProhibited.IdList.Add(guildTpl);
            }
            tradersPatched++;
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] Guild boxes marked unsellable at {tradersPatched} trader(s).", LogTextColor.Cyan);
    }

    private void BlacklistGuildBoxesEverywhere(Dictionary<MongoId, TemplateItem> itemDb)
    {
        var allGuildTpls = new HashSet<MongoId>();
        foreach (var tpl in GuildStashContext.ClosedToOpenTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        foreach (var tpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            allGuildTpls.Add(tpl);
        }
        if (allGuildTpls.Count == 0)
        {
            return;
        }

        var itemRoot = new MongoId(ItemRootTpl);
        var stashNode = new MongoId(StashParentNode);

        int containersPatched = 0;
        int filtersSynthesised = 0;

        foreach (var entry in itemDb)
        {
            var template = entry.Value;
            if (template.Properties == null || template.Properties.Grids == null)
            {
                continue;
            }
            if (template.Parent == stashNode)
            {
                continue;
            }

            bool touched = false;
            foreach (var grid in template.Properties.Grids)
            {
                if (grid.Properties == null)
                {
                    continue;
                }

                // A grid with no filter entries at all accepts anything.
                // Give it one that still accepts anything (the Item root whitelists every item) but excludes our boxes - otherwise the blanket ban would have a hole exactly where a container is most permissive.
                bool hasFilter = false;
                if (grid.Properties.Filters != null)
                {
                    foreach (var existing in grid.Properties.Filters)
                    {
                        hasFilter = true;
                        break;
                    }
                }
                if (!hasFilter)
                {
                    // Log these under debug: giving a previously-unfiltered grid a filter is the most invasive thing this pass does, so the list of containers it happened to needs to be visible if one of them ever misbehaves in-game.
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

                foreach (var filter in grid.Properties.Filters)
                {
                    if (filter.ExcludedFilter == null)
                    {
                        filter.ExcludedFilter = new HashSet<MongoId>();
                    }
                    foreach (var guildTpl in allGuildTpls)
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
