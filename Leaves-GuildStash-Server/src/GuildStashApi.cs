using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Ws;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Servers.Ws;

namespace Leaves_GuildStash_Server;

// Body of a POST /guildstash/precheck: which action, on which box item.
public sealed class PrecheckRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = "";
}

// Body of a POST /guildstash/api/forceclose: which storage to force-release.
public sealed class ForceCloseRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

// The Guild Stash HTTP API. Self-registers by being [Injectable]; SPT routes any request we CanHandle here.
// It serves: the box-template registry the client fetches on load; a preflight endpoint that runs the action validation WITHOUT mutating (so the client can show a soft notification instead of a resync-forcing Warning); and the web UI page + its storage feed / force-close.
// The actual open/close/convert mutations go through the item-event pipeline (GuildStashItemEventRouter).
[Injectable]
public class GuildStashApi(
    ProfileHelper profileHelper,
    GuildStashStorage storage,
    GuildStashCloser closer,
    SaveServer saveServer,
    SptWebSocketConnectionHandler wsHandler,
    ModHelper modHelper) : IHttpListener, IOnLoad
{
    private const string Prefix = "/guildstash/";

    // Publish our routes to the suite-wide Dev directory (served at /webui/dev/routes). Utils.Instance is set well before any OnLoad.
    public Task OnLoad()
    {
        var routes = new List<Leaves.RouteInfo>
        {
            new Leaves.RouteInfo("GET", "/guildstash/ui", "Guild Stash web UI (storage list + force-close).", "ui"),
            new Leaves.RouteInfo("GET", "/guildstash/boxtpls", "Box-template registry { closed, open, baseTpls } the client fetches on load.", "api"),
            new Leaves.RouteInfo("GET", "/guildstash/api/storages", "All storages with contents, open ones first (powers the UI list).", "api"),
            new Leaves.RouteInfo("POST", "/guildstash/precheck", "Preflight an action { action, itemId } without mutating (soft-notification check).", "internal"),
            new Leaves.RouteInfo("POST", "/guildstash/api/forceclose", "Force-close a storage { id }: properly closes the holder's open box (no duplication) and kicks them to the login screen if online.", "api", true),
        };
        Leaves.Utils.Instance.RegisterRoutes("Guild Stash", routes);
        return Task.CompletedTask;
    }

    private readonly string uiDir = System.IO.Path.Combine(
        modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()),
        "assets", "ui");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        // "/guildstash/" is ours alone, so this never shadows an SPT route.
        return Leaves.HttpUtils.HasPrefix(context, Prefix);
    }

    public async Task Handle(MongoId sessionId, HttpContext context)
    {
        string path = context.Request.Path.Value ?? "";
        string method = context.Request.Method;

        try
        {
            if (method == "GET" && path.EndsWith("/boxtpls", StringComparison.OrdinalIgnoreCase))
            {
                await HandleBoxTpls(context);
            }
            else if (method == "POST" && path.EndsWith("/precheck", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePrecheck(sessionId, context);
            }
            else if (method == "GET" && Leaves.HttpUtils.IsGet(context, "/guildstash/ui"))
            {
                await Leaves.HttpUtils.ServeFile(context, System.IO.Path.Combine(uiDir, "guildstash.html"), "text/html; charset=utf-8");
            }
            else if (method == "GET" && path.EndsWith("/api/storages", StringComparison.OrdinalIgnoreCase))
            {
                await HandleStorages(context);
            }
            else if (method == "POST" && path.EndsWith("/api/forceclose", StringComparison.OrdinalIgnoreCase))
            {
                await HandleForceClose(context);
            }
            else
            {
                await WriteJson(context, StatusCodes.Status404NotFound, new { error = "Unknown guild-stash endpoint." });
            }
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] API error on {method} {path}: {ex.Message}", LogTextColor.Red);
            await WriteJson(context, StatusCodes.Status500InternalServerError, new { error = ex.Message });
        }
    }

    // GET /guildstash/boxtpls -> { closed:[...], open:[...], baseTpls:[...] }.
    // The client fetches this once on load so it knows which item templates are guild boxes (and thus which right-click action to show):
    //   closed -> "Open Remote", open -> "Close Remote", base (plain Item/THICC case) -> "Convert to Remote".
    private async Task HandleBoxTpls(HttpContext context)
    {
        var closed = new List<string>();
        foreach (var tpl in GuildStashContext.ClosedToOpenTpl.Keys)
        {
            closed.Add(tpl.ToString());
        }

        var open = new List<string>();
        foreach (var tpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            open.Add(tpl.ToString());
        }

        var baseTpls = new List<string>();
        foreach (var tpl in GuildStashContext.BaseToClosedTpl.Keys)
        {
            baseTpls.Add(tpl.ToString());
        }

        await WriteJson(context, StatusCodes.Status200OK, new
        {
            closed,
            open,
            baseTpls
        });
    }

    // POST /guildstash/precheck { action, itemId } -> { ok, message }.
    // Runs the exact validation the item-event would, but mutates nothing - the client calls this first and, on !ok, shows a soft notification instead of firing the action.
    // On any server-side hiccup we return ok:true so the client isn't blocked (the item-event's own Warning is still the authoritative backstop).
    private async Task HandlePrecheck(MongoId sessionId, HttpContext context)
    {
        string body = await Leaves.HttpUtils.ReadBody(context);
        var request = JsonSerializer.Deserialize<PrecheckRequest>(body, Json);
        if (request == null || request.ItemId.Length == 0 || request.Action.Length == 0)
        {
            await WriteJson(context, StatusCodes.Status200OK, new { ok = true, message = (string?)null });
            return;
        }

        var pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items == null)
        {
            await WriteJson(context, StatusCodes.Status200OK, new { ok = true, message = (string?)null });
            return;
        }

        Item? box = null;
        foreach (var item in pmc.Inventory.Items)
        {
            if (item.Id.ToString() == request.ItemId)
            {
                box = item;
                break;
            }
        }
        if (box == null)
        {
            await WriteJson(context, StatusCodes.Status200OK, new { ok = false, message = "Guild Stash: that box isn't in your inventory." });
            return;
        }

        string? error = GuildStashValidation.ValidateAction(pmc, box, request.Action, storage);
        await WriteJson(context, StatusCodes.Status200OK, new { ok = error == null, message = error });
    }

    // GET /guildstash/api/storages -> [{ id, open, holderId, holderNickname, openedUtc, boxType, totalItems, tree:[node...] }] where a node is { tpl, name, tag, count, children:[node...] }.
    // Powers the web UI: the full nested item tree (containers expand to reveal their contents), each storage's case type, and a total that counts each ENTRY once (stack size ignored).
    private async Task HandleStorages(HttpContext context)
    {
        var infos = storage.ListStorages();
        var typeMap = MapStorageToBoxName();
        var dtos = new List<StorageDto>();

        // Open storages first, then the rest - two passes, no lambda (house style).
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var info in infos)
            {
                bool wantOpen = pass == 0;
                if (info.Open != wantOpen)
                {
                    continue;
                }

                var dto = new StorageDto
                {
                    Id = info.Id,
                    Open = info.Open,
                    HolderId = info.Holder,
                    OpenedUtc = info.OpenedUtc,
                    Unreadable = info.Unreadable,
                };

                // The storage's OWN recorded case type is authoritative when it has one. Only storages written before BoxKey existed need the profile-scan guess.
                if (info.BoxKey.Length > 0)
                {
                    dto.BoxType = GuildStashContext.BoxNameForKey(info.BoxKey);
                }
                else if (typeMap.TryGetValue(info.Id.ToLowerInvariant(), out var boxType))
                {
                    dto.BoxType = boxType;
                }

                // Which flat item list + root to tree-ify. An OPEN box reads LIVE from the holder's profile (the file only holds the last-closed snapshot); a CLOSED box uses the file contents.
                List<Item> flat = info.FileContents;
                string rootId = info.RootId;
                if (info.Open && info.OpenBoxItemId != null && info.Holder != null && info.Holder.Length == 24)
                {
                    var holderPmc = profileHelper.GetPmcProfile(new MongoId(info.Holder));
                    dto.HolderNickname = holderPmc?.Info?.Nickname;
                    if (holderPmc?.Inventory?.Items != null)
                    {
                        var (_, descendants) = GuildStashBoxOps.GatherSubtree(holderPmc, info.OpenBoxItemId);
                        flat = descendants;
                        rootId = info.OpenBoxItemId;
                    }
                }

                int count = 0;
                int units = 0;
                dto.Tree = BuildTree(flat, rootId, ref count, ref units);
                dto.TotalItems = count;
                dto.TotalUnits = units;
                dtos.Add(dto);
            }
        }

        await WriteJson(context, StatusCodes.Status200OK, dtos);
    }

    // Build a nested display tree from a flat item list, rooted at the items whose ParentId == rootId. Each node carries its tpl, localised name, optional player Tag, and stack count.
    //
    // TWO totals, because they answer different questions: `total` counts ENTRIES (one per node, stack size ignored - "how many things are in here"), while `units` sums the stack sizes ("how many items in total", so a stack of 60 rounds counts 60). The UI shows both.
    private List<StorageNodeDto> BuildTree(List<Item> items, string rootId, ref int total, ref int units)
    {
        // Index children by parent id in one pass (no repeated scans over the flat list per node).
        var byParent = new Dictionary<string, List<Item>>();
        foreach (var item in items)
        {
            string pid = item.ParentId ?? "";
            if (!byParent.TryGetValue(pid, out var siblings))
            {
                siblings = new List<Item>();
                byParent[pid] = siblings;
            }
            siblings.Add(item);
        }
        return BuildChildren(byParent, rootId, ref total, ref units);
    }

    private List<StorageNodeDto> BuildChildren(Dictionary<string, List<Item>> byParent, string parentId, ref int total, ref int units)
    {
        var result = new List<StorageNodeDto>();
        if (parentId == null || !byParent.TryGetValue(parentId, out var kids))
        {
            return result;
        }
        foreach (var item in kids)
        {
            total++;
            string tpl = item.Template.ToString();
            var node = new StorageNodeDto
            {
                Tpl = tpl,
                Name = Leaves.Utils.Instance.getLocale("en", tpl, " Name") ?? tpl,
                Count = (int)(item.Upd != null && item.Upd.StackObjectsCount != null ? item.Upd.StackObjectsCount.Value : 1),
            };
            if (item.Upd != null && item.Upd.Tag != null)
            {
                node.Tag = item.Upd.Tag.Name;
            }
            units += node.Count;
            node.Children = BuildChildren(byParent, item.Id.ToString(), ref total, ref units);
            result.Add(node);
        }
        return result;
    }

    // storageId (lowercase) -> the box's display name, derived by finding a guild box (in ANY loaded profile) tagged '@remote <id>' and reading its tpl.
    // Only the FALLBACK for pre-BoxKey storages that don't record their own case type; a storage no box currently points at simply gets no type (null).
    private Dictionary<string, string> MapStorageToBoxName()
    {
        var result = new Dictionary<string, string>();
        var profiles = profileHelper.GetProfiles();
        foreach (var pair in profiles)
        {
            var pmc = profileHelper.GetPmcProfile(pair.Key);
            if (pmc?.Inventory?.Items == null)
            {
                continue;
            }
            foreach (var item in pmc.Inventory.Items)
            {
                if (!GuildStashContext.TplToBoxName.TryGetValue(item.Template, out var boxName))
                {
                    continue;
                }
                string? id = GuildStashValidation.ParseRemoteTag(item, out _);
                if (id == null)
                {
                    continue;
                }
                if (!result.ContainsKey(id))
                {
                    result[id] = boxName;
                }
            }
        }
        return result;
    }

    // POST /guildstash/api/forceclose { id } -> { ok, changed, closed, kicked, message }.
    //
    // This used to just drop the bindings, which duplicated the contents whenever the orphaned open box was still sitting in someone's stash - and if that someone was ONLINE, their client carried on with a live open box the server had stopped tracking.
    // So now it does the REAL thing:
    //   1. For each binding on this storage, find the open box in its holder's profile and run the ordinary offline close (contents saved to storage, box swapped back to an empty closed case, binding dropped) - the exact path logout/startup use, backup zip included. No duplication.
    //   2. Persist that profile, since there's no imminent client write to do it for us.
    //   3. If the holder is still connected, flag a resync so their next item-event reloads their inventory (their client is otherwise holding a stale open box). Heavy, but force-close is a recovery tool, not a routine action.
    // Anything left unresolvable (a binding whose box no longer exists at all) falls through to the old lock-drop, which is correct for a genuine orphan.
    private async Task HandleForceClose(HttpContext context)
    {
        string body = await Leaves.HttpUtils.ReadBody(context);
        var request = JsonSerializer.Deserialize<ForceCloseRequest>(body, Json);
        if (request == null || request.Id.Length == 0)
        {
            await WriteJson(context, StatusCodes.Status400BadRequest, new { ok = false, error = "id required" });
            return;
        }

        // Ids fold to lowercase everywhere else (tag parse, filenames, bindings); match that here or a mixed-case id from a hand-made request silently matches nothing and reports success.
        string id = request.Id.ToLowerInvariant();

        int closed = 0;
        int kicked = 0;
        var bindings = storage.GetBindingsSnapshot();
        foreach (var pair in bindings)
        {
            if (pair.Value.StorageId != id)
            {
                continue;
            }

            string? holder = pair.Value.Holder;
            if (holder == null || holder.Length != 24)
            {
                continue;
            }
            var holderId = new MongoId(holder);

            var pmc = profileHelper.GetPmcProfile(holderId);
            if (pmc?.Inventory?.Items == null)
            {
                continue;
            }

            Item? openBox = null;
            foreach (var item in pmc.Inventory.Items)
            {
                if (item.Id.ToString() == pair.Key)
                {
                    openBox = item;
                    break;
                }
            }
            if (openBox == null)
            {
                // Genuine orphan - the box is gone. The lock-drop below is the right answer.
                continue;
            }

            string? error = closer.CloseInProfile(pmc, openBox, holderId, out _, out _);
            if (error != null)
            {
                Leaves.Utils.Instance.printColor($"[GuildStash] Force-close of '{id}': could not close {holderId}'s open box: {error}", LogTextColor.Red);
                continue;
            }
            closed++;

            await saveServer.SaveProfileAsync(holderId);

            if (wsHandler.IsWebSocketConnected(holderId))
            {
                // Flag a resync rather than pushing NotificationEventType.ForceLogout.
                // ForceLogout works but drops the player to the DESKTOP; flagging makes their next item-event carry a Warning, which reloads their inventory in place (GuildStashResyncPatch).
                GuildStashContext.FlagResync(holderId.ToString());
                kicked++;
                Leaves.Utils.Instance.printColor($"[GuildStash] Force-close of '{id}': {holderId} is online - closed their box and queued a client reload for their next action.", LogTextColor.Yellow);
            }
        }

        // Mop up whatever's left: bindings with no reachable box, and the storage's vestigial holder field.
        bool changed = storage.ForceClose(id);
        changed = changed || closed > 0;

        string message;
        if (closed > 0)
        {
            message = $"Closed {closed} open box(es) properly - contents saved, no duplication.";
            if (kicked > 0)
            {
                message += $" {kicked} online player(s) will reload their inventory on their next action.";
            }
        }
        else if (changed)
        {
            message = "Released a stale lock (no open box existed - the stash keeps its last-closed contents).";
        }
        else
        {
            message = "Nothing to do - that storage wasn't locked.";
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] Web UI force-closed storage '{id}': {message}", LogTextColor.Yellow);
        await WriteJson(context, StatusCodes.Status200OK, new { ok = true, changed, closed, kicked, message });
    }

    private static async Task WriteJson(HttpContext context, int statusCode, object payload)
    {
        await Leaves.HttpUtils.WriteJson(context, statusCode, payload, Json);
    }
}

// Wire shapes for the web UI storage feed.
public sealed class StorageDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("open")] public bool Open { get; set; }
    [JsonPropertyName("holderId")] public string? HolderId { get; set; }
    [JsonPropertyName("holderNickname")] public string? HolderNickname { get; set; }
    [JsonPropertyName("openedUtc")] public string? OpenedUtc { get; set; }
    // The case type this stash is (e.g. "Item case" / "T H I C C item case"), or null if unknown.
    [JsonPropertyName("boxType")] public string? BoxType { get; set; }
    // The storage file exists but wouldn't parse - it is NOT really empty, it's corrupt. Open refuses it.
    [JsonPropertyName("unreadable")] public bool Unreadable { get; set; }
    // Count of every item ENTRY in the tree, each counted once (stack size ignored).
    [JsonPropertyName("totalItems")] public int TotalItems { get; set; }
    // Every entry's stack size SUMMED - a stack of 60 rounds counts 60, not 1.
    [JsonPropertyName("totalUnits")] public int TotalUnits { get; set; }
    // The full nested item tree (top-level entries; containers carry their own children).
    [JsonPropertyName("tree")] public List<StorageNodeDto> Tree { get; set; } = new List<StorageNodeDto>();
}

// One node in a storage's item tree - an item, plus whatever nests inside it (grid contents, mods, etc.).
public sealed class StorageNodeDto
{
    [JsonPropertyName("tpl")] public string Tpl { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    // The player's own inventory tag on this item (e.g. a case they named), if any.
    [JsonPropertyName("tag")] public string? Tag { get; set; }
    // Stack size (shown as "x N" when > 1). Still just ONE entry for the total-items count.
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("children")] public List<StorageNodeDto> Children { get; set; } = new List<StorageNodeDto>();
}
