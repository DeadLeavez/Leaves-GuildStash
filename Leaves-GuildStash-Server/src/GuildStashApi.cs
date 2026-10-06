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
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Ws;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Http;
using SPTarkov.Server.Core.Servers.Ws;

namespace Leaves_GuildStash_Server;

// POST Body /guildstash/precheck
public sealed class PrecheckRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = ""; // box item id

    // The tag text the client currently sees on that box. Convert only, and only used to answer the
    // preflight - the real Convert op re-reads our own copy. Null/absent = fall back to our copy.
    [JsonPropertyName("tag")]
    public string? Tag { get; set; }
}

// POST Body /guildstash/api/forceclose
public sealed class ForceCloseRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = ""; // storage id
}

[Injectable]
public class GuildStashApi(
    ProfileHelper profileHelper,
    GuildStashStorage storage,
    GuildStashCloser closer,
    SptWebSocketConnectionHandler wsHandler) : IHttpListener, IOnLoad
{
    public Task OnLoad()
    {
        List<Leaves.RouteInfo> routes = new()
        {
            // The /guildstash/ui page (guildstash.html) is served by the Leaves-WebUI mod now, which
            // registers it under "Web UI (static files)". Left commented so the move is visible.
            //   new Leaves.RouteInfo("GET", "/guildstash/ui", "Guild Stash web UI (storage list + force-close).", "ui"),
            new Leaves.RouteInfo("GET", "/guildstash/boxtpls", "Box-template registry { closed, open, baseTpls } the client fetches on load.", "api"),
            new Leaves.RouteInfo("GET", "/guildstash/api/storages", "All storages with contents and item trees (powers the UI list).", "api"),
            new Leaves.RouteInfo("POST", "/guildstash/precheck", "Preflight an action { action, itemId, tag? } without mutating (soft-notification check); `tag` is the client's live tag, Convert only.", "internal"),
            new Leaves.RouteInfo("POST", "/guildstash/api/forceclose", "Force-close a storage { id }: closes the holder's open box for real (no dupe) and reloads their client if online.", "api", true),
        };
        Leaves.Utils.Instance.RegisterRoutes("Guild Stash", routes);
        return Task.CompletedTask;
    }

    // guildstash.html moved to the Leaves-WebUI mod, which serves every Leaves mod's static UI files.
    // This listener is the JSON API + the client precheck/forceclose surface only now.
    //   private readonly string uiDir = System.IO.Path.Combine(
    //       modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "assets", "ui");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string Prefix = "/guildstash/";

    // /guildstash/ui (the page file) is served by the Leaves-WebUI mod now - excluded here so exactly
    // one listener claims it (listener dispatch order is not guaranteed). Everything else under
    // /guildstash/ - boxtpls, precheck, /api/* - is still ours.
    public bool CanHandle(MongoId sessionId, HttpContext context)
    {
        if (!Leaves.HttpUtils.HasPrefix(context, Prefix))
        {
            return false;
        }
        return (context.Request.Path.Value ?? "").TrimEnd('/') != "/guildstash/ui";
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
            // /guildstash/ui static serve moved to the Leaves-WebUI mod (guildstash.html);
            // CanHandle already routes that path elsewhere.
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
    private async Task HandleBoxTpls(HttpContext context)
    {
        List<string> closed = new();
        foreach (MongoId tpl in GuildStashContext.ClosedToOpenTpl.Keys)
        {
            closed.Add(tpl.ToString());
        }

        List<string> open = new();
        foreach (MongoId tpl in GuildStashContext.OpenToClosedTpl.Keys)
        {
            open.Add(tpl.ToString());
        }

        List<string> baseTpls = new();
        foreach (MongoId tpl in GuildStashContext.BaseToClosedTpl.Keys)
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
    private async Task HandlePrecheck(MongoId sessionId, HttpContext context)
    {
        string body = await Leaves.HttpUtils.ReadBody(context);
        PrecheckRequest? request = JsonSerializer.Deserialize<PrecheckRequest>(body, Json);
        if (request == null || request.ItemId.Length == 0 || request.Action.Length == 0)
        {
            await WriteJson(context, StatusCodes.Status200OK, new { ok = true, message = (string?)null });
            return;
        }

        PmcData? pmc = profileHelper.GetPmcProfile(sessionId);
        if (pmc?.Inventory?.Items == null)
        {
            await WriteJson(context, StatusCodes.Status200OK, new { ok = true, message = (string?)null });
            return;
        }

        Item? box = pmc.Inventory.Items.FirstOrDefault((Item item) => item.Id.ToString() == request.ItemId);
        if (box == null)
        {
            await WriteJson(context, StatusCodes.Status200OK, new { ok = false, message = "Guild Stash: that box isn't in your inventory." });
            return;
        }

        // TAG-RACE: our profile copy can lag the player's tag by one queued item-event op, and from here a
        // missing tag and a malformed one look identical. So for Convert we judge the CLIENT's live tag
        // text instead - parsed by our own ParseRemoteTag, so the rules are unchanged. Preflight only.
        string? error = GuildStashValidation.ValidateAction(pmc, box, request.Action, storage, request.Tag);
        await WriteJson(context, StatusCodes.Status200OK, new { ok = error == null, message = error });
    }

    // Wire shapes for the web UI storage feed.
    public sealed class StorageDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("open")] public bool Open { get; set; }
        [JsonPropertyName("holderId")] public string? HolderId { get; set; }
        [JsonPropertyName("holderNickname")] public string? HolderNickname { get; set; }
        [JsonPropertyName("openedUtc")] public string? OpenedUtc { get; set; }
        [JsonPropertyName("boxType")] public string? BoxType { get; set; } // case type, e.g. "Item case" / "T H I C C item case"
        [JsonPropertyName("unreadable")] public bool Unreadable { get; set; } // file exists but won't parse
        [JsonPropertyName("totalItems")] public int TotalItems { get; set; } // item ENTRIES, stack size ignored
        [JsonPropertyName("totalUnits")] public int TotalUnits { get; set; } // stack sizes SUMMED (a stack of 60 = 60)
        [JsonPropertyName("tree")] public List<StorageNodeDto> Tree { get; set; } = new List<StorageNodeDto>(); // nested item tree
    }

    // One node in a box's item tree - an item plus whatever nests inside it (grid contents, mods).
    public sealed class StorageNodeDto
    {
        [JsonPropertyName("tpl")] public string Tpl { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("tag")] public string? Tag { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; } // stack size; still one entry for the total-items count
        [JsonPropertyName("children")] public List<StorageNodeDto> Children { get; set; } = new List<StorageNodeDto>();
    }

    // GET /guildstash/api/storages -> StorageDto
    private async Task HandleStorages(HttpContext context)
    {
        List<GuildStashStorage.StorageInfo> allStorageInfos = storage.ListStorages();
        List<StorageDto> allBoxes = new();

        // Natural order - whatever ListStorages() yields. Presentation ordering (open first, etc.) is the web UI's job.
        foreach (GuildStashStorage.StorageInfo storageInfo in allStorageInfos)
        {
            StorageDto box = new()
            {
                Id = storageInfo.Id,
                Open = storageInfo.Open,
                HolderId = storageInfo.Holder,
                OpenedUtc = storageInfo.OpenedUtc,
                Unreadable = storageInfo.Unreadable,
            };

            // BoxKey is the recorded case type; pre-BoxKey storages get no type
            if (storageInfo.BoxKey.Length > 0)
            {
                box.BoxType = GuildStashContext.BoxNameForKey(storageInfo.BoxKey);
            }

            // OPEN box: tree from the holder's live profile (the file is only the last-closed snapshot). CLOSED box: from the file.
            List<Item> flat = storageInfo.FileContents;
            string rootId = storageInfo.RootId;
            if (storageInfo.Open && storageInfo.OpenBoxItemId != null && storageInfo.Holder != null && storageInfo.Holder.Length == 24)
            {
                PmcData? holderPmc = profileHelper.GetPmcProfile(new MongoId(storageInfo.Holder));
                box.HolderNickname = holderPmc?.Info?.Nickname;
                if (holderPmc?.Inventory?.Items != null)
                {
                    (_, List<Item> descendants) = GuildStashBoxOps.GatherSubtree(holderPmc, storageInfo.OpenBoxItemId);
                    flat = descendants;
                    rootId = storageInfo.OpenBoxItemId;
                }
            }

            int count = 0;
            int units = 0;
            box.Tree = BuildTree(flat, rootId, ref count, ref units);
            box.TotalItems = count;
            box.TotalUnits = units;
            allBoxes.Add(box);
        }

        await WriteJson(context, StatusCodes.Status200OK, allBoxes);
    }

    // Nested tree from a flat item list, rooted at ParentId == rootId. Each node: tpl, localised name, optional Tag, stack count.
    // Two totals: `total` = entries (things in here), `units` = summed stack sizes (a stack of 60 = 60). UI shows both.
    private List<StorageNodeDto> BuildTree(List<Item> items, string rootId, ref int total, ref int units)
    {
        // index children by parent id for one lookup per node
        Dictionary<string, List<Item>> parentLookup = new();
        foreach (Item item in items)
        {
            string currentItemPid = item.ParentId ?? "";
            if (!parentLookup.TryGetValue(currentItemPid, out List<Item>? siblings))
            {
                siblings = new List<Item>();
                parentLookup[currentItemPid] = siblings;
            }
            siblings.Add(item);
        }
        return BuildChildren(parentLookup, rootId, ref total, ref units);
    }

    private List<StorageNodeDto> BuildChildren(Dictionary<string, List<Item>> byParent, string parentId, ref int total, ref int units)
    {
        List<StorageNodeDto> subBox = new();
        if (parentId == null || !byParent.TryGetValue(parentId, out List<Item>? children))
        {
            return subBox;
        }
        foreach (Item child in children)
        {
            total++;
            string tpl = child.Template.ToString();
            StorageNodeDto node = new()
            {
                Tpl = tpl,
                Name = Leaves.Utils.Instance.getLocale("en", tpl, " Name") ?? tpl,
                Count = (int)(child.Upd != null && child.Upd.StackObjectsCount != null ? child.Upd.StackObjectsCount.Value : 1),
            };
            if (child.Upd != null && child.Upd.Tag != null)
            {
                node.Tag = child.Upd.Tag.Name;
            }
            units += node.Count;
            node.Children = BuildChildren(byParent, child.Id.ToString(), ref total, ref units);
            subBox.Add(node);
        }
        return subBox;
    }

    // POST /guildstash/api/forceclose { id } -> { ok, changed, closed, kicked, message }.
    // Closes each holder's open box for real via CloseBoxById (contents saved, no dupe), reloads online holders' clients.
    // A binding whose box is gone falls through to storage.ForceClose - a plain lock-drop, correct for a real orphan.
    private async Task HandleForceClose(HttpContext context)
    {
        string body = await Leaves.HttpUtils.ReadBody(context);
        ForceCloseRequest? request = JsonSerializer.Deserialize<ForceCloseRequest>(body, Json);
        if (request == null || request.Id.Length == 0)
        {
            await WriteJson(context, StatusCodes.Status400BadRequest, new { ok = false, error = "id required" });
            return;
        }

        // lowercase, to match the binding keys
        string id = request.Id.ToLowerInvariant();

        int closed = 0;
        int kicked = 0;
        Dictionary<string, OpenBinding> bindings = storage.GetBindingsSnapshot();
        foreach (KeyValuePair<string, OpenBinding> pair in bindings)
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
            MongoId holderId = new(holder);

            PmcData? pmc = profileHelper.GetPmcProfile(holderId);
            if (pmc?.Inventory?.Items == null)
            {
                continue;
            }

            // NotFound here = genuine orphan
            if (closer.CloseBoxById(pmc, pair.Key, holderId, $"Force-close of '{id}'") != CloseBoxResult.Closed)
            {
                continue;
            }
            closed++;

            if (wsHandler.IsWebSocketConnected(holderId))
            {
                GuildStashContext.FlagResync(holderId.ToString());
                kicked++;
                Leaves.Utils.Instance.printColor($"[GuildStash] Force-close of '{id}': {holderId} is online - closed their box and queued a client reload for their next action.", LogTextColor.Yellow);
            }
        }

        // mop up: bindings with no reachable box, and the storage's stale holder field
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
