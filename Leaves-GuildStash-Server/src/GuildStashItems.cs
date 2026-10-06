using System.Reflection;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Services.Mod;

namespace Leaves_GuildStash_Server;

// Builds the guild-box items on load: a closed + open clone of each base container in config.
// The two ids per box are pinned in an IdStore (config/ids.jsonc) so an owned box survives restarts.
// Fills GuildStashContext's tpl maps.
//
// After PostDBModLoader so base containers (modded ones included) are already in the DB to clone.
// +2 not +1: must land after GuildStashServer (+1) fills GuildStashContext.Config; this no-ops on a null config.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class GuildStashItems(
    DatabaseService databaseService,
    CustomItemService customItemService,
    ModHelper modHelper)
    : IOnLoad
{
    // topmost "Item" node - whitelisting a grid to just this allows everything (OverrideFilter uses it)
    private const string ItemRootTpl = "54009119af1c881c07000029";

    // default grid colours so open vs closed read at a glance; per-box override via config
    private const string DefaultClosedColor = "red";
    private const string DefaultOpenColor = "green";

    public Task OnLoad()
    {
        if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
        {
            return Task.CompletedTask;
        }

        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        // runtime-owned: empty on a fresh install, GetOrCreate mints ids, saved at the end
        Leaves.IdStore ids = new(modPath, "config/ids.jsonc");

        Dictionary<MongoId, TemplateItem> itemDb = databaseService.GetItems();

        Leaves.Utils.Instance.printColor("[GuildStash] Building guild-box items:", LogTextColor.Cyan);

        // per-box OverrideFilter: grids to whitelist wide open
        HashSet<MongoId> allowAllTpls = new();

        foreach (GuildStashBoxDef box in GuildStashContext.Config.Boxes)
        {
            CreateBoxPair(box, ids, itemDb, allowAllTpls);
        }

        ids.Save();

        // second pass, now every guild tpl exists: the no-nesting blacklist needs the full tpl set first
        ApplyGuildBoxFilters(itemDb, allowAllTpls);
        ProtectOpenBoxes(itemDb);
        // the "boxes go nowhere but the stash" pass runs later (GuildStashLateFilters) - it has to beat every other mod's filter rewrites

        SellClosedBoxes();

        Leaves.Utils.Instance.printColor($"[GuildStash] {GuildStashContext.ClosedToOpenTpl.Count} guild-box pair(s) registered.", LogTextColor.Cyan);
        return Task.CompletedTask;
    }

    // stock every closed box at the configured trader (default Ref, LL1, 1 GP) - cheap; useless without a remote
    private void SellClosedBoxes()
    {
        GuildStashSaleConfig sale = GuildStashContext.Config.ClosedBoxSale;
        if (sale == null || !sale.Enabled)
        {
            return;
        }

        MongoId trader = new(sale.Trader);
        MongoId currency = new(sale.Currency);
        foreach (MongoId closedTpl in GuildStashContext.ClosedToOpenTpl.Keys)
        {
            Leaves.Utils.Instance.addTrade(trader, closedTpl, currency, sale.Price, sale.LoyaltyLevel);
        }
        Leaves.Utils.Instance.printColor($"[GuildStash] Listed {GuildStashContext.ClosedToOpenTpl.Count} closed box(es) at trader {sale.Trader} (LL{sale.LoyaltyLevel}, {sale.Price} x {sale.Currency}).", LogTextColor.Cyan);
    }

    private void CreateBoxPair(GuildStashBoxDef box, Leaves.IdStore ids, Dictionary<MongoId, TemplateItem> itemDb, HashSet<MongoId> allowAllTpls)
    {
        if (box.Key.Length == 0 || box.CloneFrom.Length == 0)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Skipping box with empty key/cloneFrom.", LogTextColor.Red);
            return;
        }

        MongoId cloneFromTpl = new(box.CloneFrom);
        if (!itemDb.TryGetValue(cloneFromTpl, out TemplateItem? baseTemplate))
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Base container {box.CloneFrom} for box '{box.Key}' not in item DB - skipping.", LogTextColor.Red);
            return;
        }

        // take the base's category + handbook parent so the clone slots in beside it
        string parentNode = baseTemplate.Parent.ToString();

        string? handbookParentId = null;
        HandbookBase handbook = databaseService.GetHandbook();
        foreach (HandbookItem entry in handbook.Items)
        {
            if (entry.Id == cloneFromTpl)
            {
                handbookParentId = entry.ParentId.ToString();
                break;
            }
        }

        MongoId closedTpl = ids.GetOrCreate(box.Key + "_closed");
        MongoId openTpl = ids.GetOrCreate(box.Key + "_open");

        string closedColor = box.ClosedColor != null ? box.ClosedColor : DefaultClosedColor;
        string openColor = box.OpenColor != null ? box.OpenColor : DefaultOpenColor;
        CreateClone(closedTpl, cloneFromTpl, parentNode, handbookParentId,
            box.Name + " (Closed)", box.ShortName, closedColor);
        CreateClone(openTpl, cloneFromTpl, parentNode, handbookParentId,
            box.Name + " (Open)", box.ShortName, openColor);

        ApplyOuterSize(closedTpl, box, itemDb);
        ApplyOuterSize(openTpl, box, itemDb);

        GuildStashContext.ClosedToOpenTpl[closedTpl] = openTpl;
        GuildStashContext.OpenToClosedTpl[openTpl] = closedTpl;

        // keyed by base container: two boxes cloning the same case would collide, one unreachable via Convert
        if (GuildStashContext.BaseToClosedTpl.ContainsKey(cloneFromTpl))
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Box '{box.Key}' clones {box.CloneFrom}, which another box already claims - Convert can only ever produce ONE of them. Give each box its own cloneFrom.", LogTextColor.Red);
        }
        GuildStashContext.BaseToClosedTpl[cloneFromTpl] = closedTpl;

        // both states share the config key; a storage records it so Open can refuse a wrong-type box
        GuildStashContext.TplToBoxKey[closedTpl] = box.Key;
        GuildStashContext.TplToBoxKey[openTpl] = box.Key;
        GuildStashContext.BoxKeyToName[box.Key] = box.Name;

        if (box.OverrideFilter)
        {
            allowAllTpls.Add(closedTpl);
            allowAllTpls.Add(openTpl);
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] \t'{box.Key}': closed={closedTpl}, open={openTpl} (from {box.CloneFrom})", LogTextColor.Yellow);
    }

    private void CreateClone(MongoId newId, MongoId cloneFromTpl, string parentNode, string? handbookParentId,
        string name, string shortName, string backgroundColor)
    {
        NewItemFromCloneDetails details = new()
        {
            ItemTplToClone = cloneFromTpl,
            OverrideProperties = new TemplateItemProperties
            {
                BackgroundColor = backgroundColor,
            },
            NewId = newId.ToString(),
            ParentId = parentNode,
            HandbookParentId = handbookParentId,
            // worthless without a remote - no resale value
            FleaPriceRoubles = 1,
            HandbookPriceRoubles = 1,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new LocaleDetails
                {
                    Name = name,
                    ShortName = shortName,
                    Description = name,
                },
            },
        };

        customItemService.CreateItemFromClone(details);
    }

    // set Width/Height (cells) from config; each null keeps the base container's value
    private void ApplyOuterSize(MongoId tpl, GuildStashBoxDef box, Dictionary<MongoId, TemplateItem> itemDb)
    {
        if (box.Width == null && box.Height == null)
        {
            return;
        }
        if (!itemDb.TryGetValue(tpl, out TemplateItem? template) || template.Properties == null)
        {
            return;
        }
        if (box.Width != null)
        {
            template.Properties.Width = box.Width;
        }
        if (box.Height != null)
        {
            template.Properties.Height = box.Height;
        }
    }

    // Harden the OPEN boxes: an open box physically holds the shared contents, so losing one loses everyone's items and strands the lock.
    // Closed boxes are left alone on purpose - empty by construction, cheap from Ref, a player should be able to bin one.
    //
    // These are CLIENT-SIDE HINTS ONLY. The server reads none of them; play-testing showed IsUnsaleable/IsUndiscardable don't even stop the client, though CanSellOnRagfair does.
    // Real enforcement is server-side (GuildStashDiscardPatch, GuildStashSellPatch); this is just the UI not offering the action.
    private void ProtectOpenBoxes(Dictionary<MongoId, TemplateItem> itemDb)
    {
        foreach (MongoId openTpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            if (!itemDb.TryGetValue(openTpl, out TemplateItem? template) || template.Properties == null)
            {
                continue;
            }
            template.Properties.IsUnsaleable = true;
            template.Properties.CanSellOnRagfair = false;
            template.Properties.IsUndiscardable = true;

            // client-only: stops the client offering "discard" at all. GuildStashDiscardPatch already refuses it server-side, but only with a Warning - ESC past that and you're staring at a ghost box.
            // Safe for Close: that uses the server-pushed REMOVE, which this doesn't touch.
            // Note the capital R: SPT's property is IsUnRemovable, JSON "IsUnremovable".
            template.Properties.IsUnRemovable = true;
        }
    }

    // Grid filters for the guild boxes:
    //   closed: hold NOTHING (empty whitelist + Item root blacklisted) - open replaces the box wholesale, so anything stuffed in a closed box is lost.
    //   open: everything except other guild cases (no box-in-box). OverrideFilter boxes get an allow-all whitelist; others keep their native one.
    // These gate manual drag/drop only; server-injected contents bypass them.
    private void ApplyGuildBoxFilters(Dictionary<MongoId, TemplateItem> itemDb, HashSet<MongoId> allowAllTpls)
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

        MongoId itemRoot = new(ItemRootTpl);

        foreach (MongoId tpl in allGuildTpls)
        {
            if (!itemDb.TryGetValue(tpl, out TemplateItem? template))
            {
                continue;
            }
            if (template.Properties == null || template.Properties.Grids == null)
            {
                continue;
            }

            bool isClosed = GuildStashContext.ClosedToOpenTpl.ContainsKey(tpl);
            bool allowAll = allowAllTpls.Contains(tpl);

            foreach (Grid grid in template.Properties.Grids)
            {
                if (grid.Properties == null || grid.Properties.Filters == null)
                {
                    continue;
                }

                foreach (GridFilter filter in grid.Properties.Filters)
                {
                    if (isClosed)
                    {
                        filter.Filter = new HashSet<MongoId>();
                        filter.ExcludedFilter = new HashSet<MongoId> { itemRoot };
                        continue;
                    }

                    // open box
                    if (allowAll)
                    {
                        filter.Filter = new HashSet<MongoId> { itemRoot };
                    }
                    if (filter.ExcludedFilter == null)
                    {
                        filter.ExcludedFilter = new HashSet<MongoId>();
                    }
                    foreach (MongoId guildTpl in allGuildTpls)
                    {
                        filter.ExcludedFilter.Add(guildTpl);
                    }
                }
            }
        }
    }
}
