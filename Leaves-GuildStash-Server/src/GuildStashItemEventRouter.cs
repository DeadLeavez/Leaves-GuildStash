using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// The client sends { Action: "GuildStash...", ItemId: "<box item id>" } through the game's own inventory operation queue.
// A concrete SUBCLASS of BaseInteractionRequestData on purpose: the base has a JsonConverter that switches on Action, so a subclass deserialises directly, bypassing it.
public record GuildStashActionRequest : BaseInteractionRequestData
{
    // The id of the box item the player right-clicked.
    public string? ItemId { get; set; }
}

// The real Open / Close / Convert actions.
// Each mutates the caller's profile AND echoes the same delta into the response's profileChanges (NewItems / DeletedItems) so the client applies it live - same path a flea/trader purchase uses - with no relaunch.
// Every action is a delete-old-box + spawn-fresh-box move (GuildStashBoxOps), the only shape the client op-queue accepts for a filled container.
[Injectable]
public class GuildStashItemEventRouter(GuildStashStorage storage, GuildStashCloser closer, HttpResponseUtil httpResponseUtil) : ItemEventRouterDefinition
{
    public const string OpenAction = "GuildStashOpen";
    public const string CloseAction = "GuildStashClose";
    public const string ConvertAction = "GuildStashConvert";

    protected override IEnumerable<HandledRoute> GetHandledRoutes()
    {
        return new List<HandledRoute>
        {
            new HandledRoute(OpenAction, false),
            new HandledRoute(CloseAction, false),
            new HandledRoute(ConvertAction, false),
        };
    }

    protected override ValueTask<ItemEventRouterResponse> HandleItemEventInternal(
        string url,
        PmcData pmcData,
        BaseInteractionRequestData body,
        MongoId sessionID,
        ItemEventRouterResponse output)
    {
        var request = body as GuildStashActionRequest;
        string action = request?.Action ?? "";
        string itemId = request?.ItemId ?? "";

        if (itemId.Length == 0)
        {
            return Fail(output, action, "Guild Stash: the request had no item.");
        }

        var (box, _) = GuildStashBoxOps.GatherSubtree(pmcData, itemId);
        if (box == null)
        {
            return Fail(output, action, "Guild Stash: that box isn't in your inventory.");
        }

        // Authoritative gate (same checks the client preflights).
        // Normally the client already refused a failing action, so this Warning backstop only fires on a race or a direct/unpatched client.
        string? validationError = GuildStashValidation.ValidateAction(pmcData, box, action, storage);
        if (validationError != null)
        {
            return Fail(output, action, validationError);
        }

        List<string> deleted;
        List<Item> created;
        string? error;

        // Anything unexpected (a disk error writing storage, a malformed item tree) must NOT escape as an unhandled exception: SPT would return a 500, the client's operation queue would get no usable answer, and it can jam on that.
        // Turning it into a Warning is strictly better - the player sees a reason, and AppendErrorToOutput forces a full client resync, which also repairs the desync if we happened to fail after the profile was already mutated.
        try
        {
            if (action == OpenAction)
            {
                error = HandleOpen(pmcData, box, sessionID, out deleted, out created);
            }
            else if (action == CloseAction)
            {
                error = HandleClose(pmcData, box, sessionID, out deleted, out created);
            }
            else if (action == ConvertAction)
            {
                error = HandleConvert(pmcData, box, out deleted, out created);
            }
            else
            {
                return Fail(output, action, "Guild Stash: unknown action.");
            }
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] {action} for {sessionID} THREW: {ex}", LogTextColor.Red);
            return Fail(output, action, "Guild Stash: that action failed unexpectedly - check the server log. Your stash is backed up in storage/backups.");
        }

        if (error != null)
        {
            return Fail(output, action, error);
        }

        // Mirror the delta into the response (lists pre-initialised by EventOutputHolder) so it applies live.
        // Echo ONLY the top box as deleted - the client cascades removal of its children automatically.
        // Listing the already-removed children explicitly wedges the op-queue (infinite spinner on close).
        // `deleted` still holds the full set for the profile mutation / the debug count below.
        var change = output.ProfileChanges[sessionID];
        change.Items.DeletedItems.Add(new DeletedItem { Id = box.Id });
        foreach (var item in created)
        {
            change.Items.NewItems.Add(item);
        }

        if (GuildStashContext.Config.Debug)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] {action} from {sessionID}: deleted {deleted.Count}, new {created.Count}.", LogTextColor.Cyan);
        }

        return new ValueTask<ItemEventRouterResponse>(output);
    }

    // Surface a player-facing error: push it onto the response's Warnings (via SPT's helper) so the EFT client shows it as a red notification and applies NOTHING.
    // We only ever call this BEFORE mutating the profile, so there's nothing to roll back. Also logs server-side for debugging.
    private ValueTask<ItemEventRouterResponse> Fail(ItemEventRouterResponse output, string action, string message)
    {
        Leaves.Utils.Instance.printColor($"[GuildStash] {action} rejected: {message}", LogTextColor.Red);
        httpResponseUtil.AppendErrorToOutput(output, message, BackendErrorCodes.None);
        return new ValueTask<ItemEventRouterResponse>(output);
    }

    // Open a CLOSED guild box: replace the closed case with an OPEN case full of the stored contents.
    // Preconditions (closed guild box, stash root, registered id, not already open) were checked by ValidateAction; here we just derive the values and do the swap.
    private string? HandleOpen(PmcData pmc, Item box, MongoId sessionID, out List<string> deleted, out List<Item> created)
    {
        deleted = new List<string>();
        created = new List<Item>();

        // Guaranteed by ValidateAction - guarded defensively rather than asserted.
        if (!GuildStashContext.ClosedToOpenTpl.TryGetValue(box.Template, out var openTpl))
        {
            return "Guild Stash: that isn't a closed guild box.";
        }
        string? id = GuildStashValidation.ParseRemoteTag(box, out _);
        if (id == null)
        {
            return "Guild Stash: this box needs an '@remote <id>' tag.";
        }

        // Re-check "readable" here rather than trusting ValidateAction's load: this is the authoritative path, and an unreadable file otherwise reads back as an EMPTY stash - which the next close would then make permanent.
        var stored = storage.Load(id, out bool unreadable);
        if (unreadable)
        {
            return $"Guild Stash: storage '{id}' is corrupt and can't be opened - check the server log and storage/backups.";
        }
        var contents = stored.Contents;
        string contentsRoot = stored.RootId.Length > 0 ? stored.RootId : box.Id.ToString();

        // Rescue for a closed box that still physically holds items.
        // Convert used to stuff the original case's contents INTO the closed box while registering an EMPTY storage file - and since an open deletes the closed box's whole subtree, those items were silently destroyed on the first open.
        // Convert now writes them to storage instead, but a box converted before that fix still carries them, so fold them in here rather than dropping them.
        // If BOTH the box and the file hold items we can't merge two content roots safely, so refuse loudly instead of destroying one of them.
        var (_, strays) = GuildStashBoxOps.GatherSubtree(pmc, box.Id.ToString());
        if (strays.Count > 0)
        {
            if (contents.Count > 0)
            {
                return $"Guild Stash: this box still holds {strays.Count} item(s) from before it was converted, and storage '{id}' isn't empty either. Empty one of them first.";
            }
            Leaves.Utils.Instance.printColor($"[GuildStash] Open: closed box carried {strays.Count} leftover item(s) - opening with those instead of dropping them.", LogTextColor.Yellow);
            contents = strays;
            contentsRoot = box.Id.ToString();
        }

        // Claim the storage BEFORE mutating anything.
        // THIS is the real open-lock: ValidateAction's IsOpenSomewhere check above is only a preflight, and two opens racing between that check and the bind both passed it, each spawning a box full of the same contents.
        // The new box's id is minted here so the claim can name it before ReplaceBox spawns it.
        var newBoxId = new MongoId();
        if (!storage.TryClaimStorage(newBoxId.ToString(), id, sessionID.ToString(), out _))
        {
            return "Guild Stash: that storage is already open. Close it before opening it again.";
        }

        // We deliberately DON'T re-save the file here - ReplaceBox mutates `contents` (re-ids/reparents them onto the new box), so saving would desync the file's rootId from its contents.
        // The file keeps its last-closed snapshot until the next Close.
        try
        {
            var (del, cre) = GuildStashBoxOps.ReplaceBox(pmc, box, openTpl, newBoxId, contentsRoot, contents);
            deleted = del;
            created = cre;
        }
        catch (Exception)
        {
            // Never leave a claim behind for a box that was never spawned - that locks the storage for everyone with no open box left to close it.
            storage.UnbindOpenBox(newBoxId.ToString());
            throw;
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] Opened storage '{id}' ({contents.Count} item(s)) for {sessionID}.", LogTextColor.Green);
        return null;
    }

    // Close an OPEN guild box: save its current contents to storage, replace it with an empty CLOSED case.
    // The actual mutation lives in GuildStashCloser (shared with the offline logout / login-reconcile path); here we just delegate and let the caller mirror the delta into profileChanges for the live client.
    private string? HandleClose(PmcData pmc, Item box, MongoId sessionID, out List<string> deleted, out List<Item> created)
    {
        return closer.CloseInProfile(pmc, box, sessionID, out deleted, out created);
    }

    // Convert a plain Item/THICC case (tagged '@remote <ID>') into the CLOSED guild version, keeping its contents and its tag.
    // The fresh-id check ran in ValidateAction; here we register + swap.
    private string? HandleConvert(PmcData pmc, Item box, out List<string> deleted, out List<Item> created)
    {
        deleted = new List<string>();
        created = new List<Item>();

        // Guaranteed by ValidateAction - guarded defensively rather than asserted.
        if (!GuildStashContext.BaseToClosedTpl.TryGetValue(box.Template, out var closedTpl))
        {
            return "Guild Stash: that case can't be made into a guild box.";
        }
        string? id = GuildStashValidation.ParseRemoteTag(box, out _);
        if (id == null)
        {
            return "Guild Stash: tag the case '@remote <id>' first.";
        }

        // The case's current contents go straight into the STORAGE, NOT into the closed box.
        // A CLOSED box is a sealed resting state (its grids are filtered to hold nothing) and an open replaces it wholesale, so anything left sitting inside it would simply be deleted the first time the stash was opened.
        // Snapshot the contents BEFORE ReplaceBox re-ids them.
        var (_, descendants) = GuildStashBoxOps.GatherSubtree(pmc, box.Id.ToString());
        var stored = new StoredBox
        {
            Id = id,
            RootId = box.Id.ToString(),
            Contents = descendants,
            Holder = null,
            // Stamp the case type so Open can refuse a box of a different kind later.
            BoxKey = GuildStashContext.BoxKeyForTpl(closedTpl),
        };

        // Register + fill the storage in ONE guarded step, so the id is claimed the moment the box is made (a later Convert to the same id is rejected even before this box is ever opened) and two players converting the same fresh id can't both pass the check.
        if (!storage.TryCreateStorage(stored))
        {
            return "Guild Stash: that ID is already in use. Pick a new one.";
        }

        // Spawn an EMPTY closed case in its place; its Upd (incl. the @remote tag) rides along on the new box.
        var (del, cre) = GuildStashBoxOps.ReplaceBox(pmc, box, closedTpl, new MongoId(), box.Id.ToString(), new List<Item>());
        deleted = del;
        created = cre;

        Leaves.Utils.Instance.printColor($"[GuildStash] Converted a case into guild box '{id}' ({descendants.Count} item(s) moved into storage).", LogTextColor.Green);
        return null;
    }
}
