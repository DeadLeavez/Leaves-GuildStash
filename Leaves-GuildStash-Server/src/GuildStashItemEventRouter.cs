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

// Client sends { Action: "GuildStash...", ItemId: "<box item id>" } through the game's inventory op-queue.
// Concrete subclass on purpose: BaseInteractionRequestData's JsonConverter switches on Action; a subclass deserialises directly, past it.
public record GuildStashActionRequest : BaseInteractionRequestData
{
    // the box item the player right-clicked
    public string? ItemId { get; set; }
}

// The real Open / Close / Convert actions.
// Each mutates the profile AND sends the changes into profileChanges so the client applies it live, no relaunch.
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
        GuildStashActionRequest? request = body as GuildStashActionRequest;
        string action = request?.Action ?? "";
        string itemId = request?.ItemId ?? "";

        if (itemId.Length == 0)
        {
            return Fail(output, action, "Guild Stash: the request had no item.");
        }

        //Gather the box, and get checking if it exists for "free"
        (Item? box, _) = GuildStashBoxOps.GatherSubtree(pmcData, itemId);
        if (box == null)
        {
            return Fail(output, action, "Guild Stash: that box isn't in your inventory.");
        }

        // authoritative gate (the client preflights the same checks); this only fires on a race or an unpatched client
        string? validationError = GuildStashValidation.ValidateAction(pmcData, box, action, storage);
        if (validationError != null)
        {
            return Fail(output, action, validationError);
        }

        List<string> deleted;
        List<Item> created;
        string? error;

        // don't let an exception escape: SPT returns a 500, the op-queue gets no answer and can jam. a Warning gives a reason and forces a resync, which also repairs a desync if we failed mid-mutation
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

        // echo ONLY the top box as deleted - the client cascades the children. listing them explicitly wedges the op-queue (infinite spinner on close).
        // `deleted` still has the full set for the debug count below.
        ProfileChange change = output.ProfileChanges[sessionID];
        change.Items!.DeletedItems.Add(new DeletedItem { Id = box.Id });
        foreach (Item item in created)
        {
            change.Items!.NewItems.Add(item);
        }

        if (GuildStashContext.Config.Debug)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] {action} from {sessionID}: deleted {deleted.Count}, new {created.Count}.", LogTextColor.Cyan);
        }

        return new ValueTask<ItemEventRouterResponse>(output);
    }

    // player-facing error: pushed onto Warnings, so the client shows a red notification and applies NOTHING.
    // only ever called BEFORE mutating the profile - nothing to roll back.
    private ValueTask<ItemEventRouterResponse> Fail(ItemEventRouterResponse output, string action, string message)
    {
        Leaves.Utils.Instance.printColor($"[GuildStash] {action} rejected: {message}", LogTextColor.Red);
        httpResponseUtil.AppendErrorToOutput(output, message, BackendErrorCodes.None);
        return new ValueTask<ItemEventRouterResponse>(output);
    }

    // Open a closed box: swap it for an open case full of the stored contents. Preconditions already checked by ValidateAction.
    private string? HandleOpen(PmcData pmc, Item box, MongoId sessionID, out List<string> deleted, out List<Item> created)
    {
        deleted = new List<string>();
        created = new List<Item>();

        // guaranteed by ValidateAction; guarded, not asserted
        if (!GuildStashContext.ClosedToOpenTpl.TryGetValue(box.Template, out MongoId openTpl))
        {
            return "Guild Stash: that isn't a closed guild box.";
        }
        string? id = GuildStashValidation.ParseRemoteTag(box, out _);
        if (id == null)
        {
            return "Guild Stash: this box needs an '@r <id>;' tag.";
        }

        // re-check readable here: an unreadable file reads back as an EMPTY stash, and the next close would make that permanent
        StoredBox stored = storage.Load(id, out bool unreadable);
        if (unreadable)
        {
            return $"Guild Stash: storage '{id}' is corrupt and can't be opened - check the server log and storage/backups.";
        }
        List<Item> contents = stored.Contents;
        string contentsRoot = stored.RootId.Length > 0 ? stored.RootId : box.Id.ToString();

        // rescue: a box converted before the fix still physically holds its items (Convert now writes them to storage). fold them in rather than drop them on open.
        // if both the box AND the file hold items, we can't merge two roots safely - refuse loudly instead of destroying one.
        (_, List<Item> strays) = GuildStashBoxOps.GatherSubtree(pmc, box.Id.ToString());
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

        // claim the storage BEFORE mutating anything - THIS is the real open-lock (ValidateAction's check is just a preflight two racing opens can both pass).
        // mint the new box id here so the claim can name it before ReplaceBox spawns it.
        MongoId newBoxId = new();
        if (!storage.TryClaimStorage(newBoxId.ToString(), id, sessionID.ToString(), out _))
        {
            return "Guild Stash: that storage is already open. Close it before opening it again.";
        }

        // don't re-save the file here: ReplaceBox re-ids `contents` onto the new box, so a save would desync rootId from contents. the file keeps its last-closed snapshot until Close.
        try
        {
            (List<string> del, List<Item> cre) = GuildStashBoxOps.ReplaceBox(pmc, box, openTpl, newBoxId, contentsRoot, contents);
            deleted = del;
            created = cre;
        }
        catch (Exception)
        {
            // spawn failed - drop the claim, or the storage locks with no box to close it
            storage.UnbindOpenBox(newBoxId.ToString());
            throw;
        }

        Leaves.Utils.Instance.printColor($"[GuildStash] Opened storage '{id}' ({contents.Count} item(s)) for {sessionID}.", LogTextColor.Green);
        return null;
    }

    // Close an open box. Mutation is in GuildStashCloser (shared with the offline paths); here we just delegate.
    private string? HandleClose(PmcData pmc, Item box, MongoId sessionID, out List<string> deleted, out List<Item> created)
    {
        return closer.CloseInProfile(pmc, box, sessionID, out deleted, out created);
    }

    // Convert a tagged Item/THICC case into the closed guild version, keeping contents + tag. Fresh-id check already ran in ValidateAction.
    private string? HandleConvert(PmcData pmc, Item box, out List<string> deleted, out List<Item> created)
    {
        deleted = new List<string>();
        created = new List<Item>();

        // guaranteed by ValidateAction; guarded, not asserted
        if (!GuildStashContext.BaseToClosedTpl.TryGetValue(box.Template, out MongoId closedTpl))
        {
            return "Guild Stash: that case can't be made into a guild box.";
        }
        string? id = GuildStashValidation.ParseRemoteTag(box, out _);
        if (id == null)
        {
            return GuildStashValidation.ConvertNeedsTagMessage;
        }

        // contents go straight into STORAGE, not the closed box: a closed box holds nothing and open replaces it wholesale, so anything inside would be deleted on first open.
        // snapshot before ReplaceBox re-ids them.
        (_, List<Item> descendants) = GuildStashBoxOps.GatherSubtree(pmc, box.Id.ToString());
        StoredBox stored = new()
        {
            Id = id,
            RootId = box.Id.ToString(),
            Contents = descendants,
            Holder = null,
            // stamp the case type so Open can refuse a wrong-kind box later
            BoxKey = GuildStashContext.BoxKeyForTpl(closedTpl),
        };

        // register + fill in one guarded step, so the id is claimed the instant the box exists and two converts of the same id can't both pass
        if (!storage.TryCreateStorage(stored))
        {
            return "Guild Stash: that ID is already in use. Pick a new one.";
        }

        // spawn an empty closed case; its Upd (incl. the @r tag) rides along
        (List<string> del, List<Item> cre) = GuildStashBoxOps.ReplaceBox(pmc, box, closedTpl, new MongoId(), box.Id.ToString(), new List<Item>());
        deleted = del;
        created = cre;

        Leaves.Utils.Instance.printColor($"[GuildStash] Converted a case into guild box '{id}' ({descendants.Count} item(s) moved into storage).", LogTextColor.Green);
        return null;
    }
}
