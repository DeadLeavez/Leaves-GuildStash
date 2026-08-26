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

// Builds the guild-box items on server load: for every box in config, a CLOSED and an OPEN clone of the base container.
// The two ids per box are pinned via LeavesUtil's IdStore (config/ids.jsonc) so a box a player owns keeps the same item across restarts.
// Fills GuildStashContext's tpl maps so the actions and tag-conversion flow can recognise a guild box and find its counterpart.
//
// Runs after PostDBModLoader (like ProgressiveContainer/ShinyAirdropGuns) so the base container templates - including modded ones - are already in the DB when we clone them.
// +2 (not +1) so it lands AFTER GuildStashServer (+1) has populated GuildStashContext.Config - equal priorities run in undefined order, and this class silently no-ops on a null config.
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 2)]
public class GuildStashItems(
    DatabaseService databaseService,
    CustomItemService customItemService,
    ModHelper modHelper)
    : IOnLoad
{
    // The topmost "Item" node - every real item descends from it, so a grid whitelisted to just this allows everything. Used by the OverrideFilter option.
    private const string ItemRootTpl = "54009119af1c881c07000029";

    // Default grid background colours (EFT colour names) to tell the two states apart at a glance.
    // Overridable per-box via config (closedColor / openColor).
    private const string DefaultClosedColor = "red";
    private const string DefaultOpenColor = "green";

    public Task OnLoad()
    {
        if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
        {
            return Task.CompletedTask;
        }

        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        // ids.jsonc is runtime-owned (see the .csproj exclusion) - starts empty on a fresh install and GetOrCreate mints the ids, saved back at the end.
        var ids = new Leaves.IdStore(modPath, "config/ids.jsonc");

        var itemDb = databaseService.GetItems();

        Leaves.Utils.Instance.printColor("[GuildStash] Building guild-box items:", LogTextColor.Cyan);

        // Tpls whose grid whitelist should be replaced with "allow everything" (per-box OverrideFilter).
        var allowAllTpls = new HashSet<MongoId>();

        foreach (var box in GuildStashContext.Config.Boxes)
        {
            CreateBoxPair(box, ids, itemDb, allowAllTpls);
        }

        ids.Save();

        // Second pass, after every guild tpl exists: fix up grid filters.
        // EVERY guild box blacklists ALL guild cases (no nesting boxes in boxes); boxes flagged OverrideFilter also get an allow-all whitelist.
        // Done here (not per-clone) because the blacklist needs the full tpl set first.
        ApplyGuildBoxFilters(itemDb, allowAllTpls);
        ProtectOpenBoxes(itemDb);
        // The blanket "guild boxes go nowhere but the stash" pass does NOT run here - it has to land after every other mod has finished rewriting container filters. See GuildStashLateFilters.

        SellClosedBoxes();

        Leaves.Utils.Instance.printColor($"[GuildStash] {GuildStashContext.ClosedToOpenTpl.Count} guild-box pair(s) registered.", LogTextColor.Cyan);
        return Task.CompletedTask;
    }

    // Stock every CLOSED guild box at the configured trader (default Ref, loyalty 1, 1 GP coin).
    // They're worthless without a remote, so a cheap trade is just a convenient way to get one.
    private void SellClosedBoxes()
    {
        var sale = GuildStashContext.Config.ClosedBoxSale;
        if (sale == null || !sale.Enabled)
        {
            return;
        }

        var trader = new MongoId(sale.Trader);
        var currency = new MongoId(sale.Currency);
        foreach (var closedTpl in GuildStashContext.ClosedToOpenTpl.Keys)
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

        var cloneFromTpl = new MongoId(box.CloneFrom);
        if (!itemDb.TryGetValue(cloneFromTpl, out var baseTemplate))
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Base container {box.CloneFrom} for box '{box.Key}' not in item DB - skipping.", LogTextColor.Red);
            return;
        }

        // Parent NODE (item category) and handbook parent, taken from the base so the clone slots into the same places in the item tree and handbook as the container it copies.
        string parentNode = baseTemplate.Parent.ToString();

        string? handbookParentId = null;
        var handbook = databaseService.GetHandbook();
        foreach (var entry in handbook.Items)
        {
            if (entry.Id == cloneFromTpl)
            {
                handbookParentId = entry.ParentId.ToString();
                break;
            }
        }

        MongoId closedTpl = ids.GetOrCreate(box.Key + "_closed");
        MongoId openTpl = ids.GetOrCreate(box.Key + "_open");

        // Distinct grid background colours so open vs closed are obvious at a glance in the stash.
        string closedColor = box.ClosedColor != null ? box.ClosedColor : DefaultClosedColor;
        string openColor = box.OpenColor != null ? box.OpenColor : DefaultOpenColor;
        CreateClone(closedTpl, cloneFromTpl, parentNode, handbookParentId,
            box.Name + " (Closed)", box.ShortName, closedColor);
        CreateClone(openTpl, cloneFromTpl, parentNode, handbookParentId,
            box.Name + " (Open)", box.ShortName, openColor);

        // Optional outer-size override (null = leave the base container's footprint unchanged).
        ApplyOuterSize(closedTpl, box, itemDb);
        ApplyOuterSize(openTpl, box, itemDb);

        GuildStashContext.ClosedToOpenTpl[closedTpl] = openTpl;
        GuildStashContext.OpenToClosedTpl[openTpl] = closedTpl;

        // BaseToClosedTpl is keyed by the BASE container, so two config boxes cloned from the same case would silently overwrite each other and leave one of them unreachable via Convert. Say so.
        if (GuildStashContext.BaseToClosedTpl.ContainsKey(cloneFromTpl))
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] Box '{box.Key}' clones {box.CloneFrom}, which another box already claims - Convert can only ever produce ONE of them. Give each box its own cloneFrom.", LogTextColor.Red);
        }
        GuildStashContext.BaseToClosedTpl[cloneFromTpl] = closedTpl;

        // Both states map to the same human name (config Name) so the web UI can label a stash by its case type.
        GuildStashContext.TplToBoxName[closedTpl] = box.Name;
        GuildStashContext.TplToBoxName[openTpl] = box.Name;

        // ...and to the same config key, which is what a storage records so Open can refuse a box of the wrong case type (a THICC's saved layout doesn't fit inside an Item case).
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
        var details = new NewItemFromCloneDetails
        {
            ItemTplToClone = cloneFromTpl,
            OverrideProperties = new TemplateItemProperties
            {
                BackgroundColor = backgroundColor,
            },
            NewId = newId.ToString(),
            ParentId = parentNode,
            HandbookParentId = handbookParentId,
            // No real value: a guild box is worthless without a remote, so it can't be sold/flea'd for gain.
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

    // Override the box's outer footprint (Width/Height in cells) if the config supplies one.
    // Each is independent - a null leaves that dimension at the base container's value.
    private void ApplyOuterSize(MongoId tpl, GuildStashBoxDef box, Dictionary<MongoId, TemplateItem> itemDb)
    {
        if (box.Width == null && box.Height == null)
        {
            return;
        }
        if (!itemDb.TryGetValue(tpl, out var template) || template.Properties == null)
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

    // Harden the OPEN boxes specifically.
    // An open box is the one state that physically holds the shared contents, so losing one loses everyone's items AND strands the storage lock (nothing left in any profile to close).
    // CLOSED boxes are deliberately left alone: they're empty by construction, they cost a GP coin from Ref, and a player should be able to bin one they no longer want.
    //
    // These are CLIENT-SIDE HINTS ONLY - the SPT server reads none of them (verified), and play-testing showed `IsUnsaleable`/`IsUndiscardable` don't actually stop the client either, while `CanSellOnRagfair` does.
    // The real guarantees are the server-side guards (GuildStashDiscardPatch, GuildStashSellPatch); everything here is about the UI not offering the action in the first place.
    private void ProtectOpenBoxes(Dictionary<MongoId, TemplateItem> itemDb)
    {
        foreach (var openTpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            if (!itemDb.TryGetValue(openTpl, out var template) || template.Properties == null)
            {
                continue;
            }
            template.Properties.IsUnsaleable = true;
            template.Properties.CanSellOnRagfair = false;
            template.Properties.IsUndiscardable = true;

            // The one the Sacred Amulet has that we didn't - the likely reason IT can't be discarded.
            // Config-gated because if it turns out to block server-pushed removals too, it breaks Close. See GuildStashConfig.OpenBoxUnremovable for the full reasoning.
            if (GuildStashContext.Config.OpenBoxUnremovable)
            {
                // NOTE the capital R - SPT's C# property is IsUnRemovable, mapped to JSON "IsUnremovable".
                template.Properties.IsUnRemovable = true;
            }
        }

        if (GuildStashContext.Config.OpenBoxUnremovable)
        {
            Leaves.Utils.Instance.printColor("[GuildStash] Open boxes marked IsUnremovable (config openBoxUnremovable) - if Close ever stops working, turn this off first.", LogTextColor.Yellow);
        }
    }

    // Fix up the guild boxes' grid filters:
    //   - CLOSED boxes hold NOTHING: empty whitelist + the "Item" root in the blacklist. A closed box is a sealed resting state; anything a player stuffed in would be lost the next time it's opened (open replaces the box wholesale), so we forbid manual placement outright.
    //   - OPEN boxes hold everything EXCEPT other guild cases (so you can't nest a box in a box). Boxes flagged OverrideFilter get an allow-all whitelist; otherwise the native whitelist is kept and only the guild-case blacklist is added.
    // (Server-injected contents bypass these filters - they gate manual drag/drop only.)
    private void ApplyGuildBoxFilters(Dictionary<MongoId, TemplateItem> itemDb, HashSet<MongoId> allowAllTpls)
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

        var itemRoot = new MongoId(ItemRootTpl);

        foreach (var tpl in allGuildTpls)
        {
            if (!itemDb.TryGetValue(tpl, out var template))
            {
                continue;
            }
            if (template.Properties == null || template.Properties.Grids == null)
            {
                continue;
            }

            bool isClosed = GuildStashContext.ClosedToOpenTpl.ContainsKey(tpl);
            bool allowAll = allowAllTpls.Contains(tpl);

            foreach (var grid in template.Properties.Grids)
            {
                if (grid.Properties == null || grid.Properties.Filters == null)
                {
                    continue;
                }

                foreach (var filter in grid.Properties.Filters)
                {
                    if (isClosed)
                    {
                        // Hold nothing: empty whitelist, everything (the Item root) blacklisted.
                        filter.Filter = new HashSet<MongoId>();
                        filter.ExcludedFilter = new HashSet<MongoId> { itemRoot };
                        continue;
                    }

                    // Open box.
                    if (allowAll)
                    {
                        filter.Filter = new HashSet<MongoId> { itemRoot };
                    }
                    if (filter.ExcludedFilter == null)
                    {
                        filter.ExcludedFilter = new HashSet<MongoId>();
                    }
                    foreach (var guildTpl in allGuildTpls)
                    {
                        filter.ExcludedFilter.Add(guildTpl);
                    }
                }
            }
        }
    }
}
