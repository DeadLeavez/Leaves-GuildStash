using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_GuildStash_Server;

// Pure validation (no changes) for the three actions,
public static class GuildStashValidation
{
    public const string ConvertNeedsTagMessage = "Guild Stash: tag the case '@r <id>;' first.";

    // Returns null if the action is allowed for this box, or a player-facing reason if not.
    public static string? ValidateAction(PmcData pmc, Item box, string action, GuildStashStorage storage, string? clientTag = null)
    {
        if (action == GuildStashItemEventRouter.OpenAction)
        {
            return ValidateOpen(pmc, box, storage);
        }
        if (action == GuildStashItemEventRouter.CloseAction)
        {
            return ValidateClose(box, storage);
        }
        if (action == GuildStashItemEventRouter.ConvertAction)
        {
            return ValidateConvert(pmc, box, storage, clientTag);
        }
        return "Guild Stash: unknown action.";
    }

    // True only when the box sits directly in the player's stash root
    private static bool IsBoxInStashRoot(PmcData pmc, Item box)
    {
        if (pmc.Inventory == null || pmc.Inventory.Stash == null)
        {
            return false;
        }
        string stashId = pmc.Inventory.Stash.ToString() ?? "";
        return stashId.Length != 0 && box.ParentId == stashId;
    }

    //Check if it can open
    private static string? ValidateOpen(PmcData pmc, Item box, GuildStashStorage storage)
    {
        if (!GuildStashContext.ClosedToOpenTpl.ContainsKey(box.Template))
        {
            return "Guild Stash: that isn't a closed guild box.";
        }

        if (!IsBoxInStashRoot(pmc, box))
        {
            return "Guild Stash: move the case into your stash before opening it.";
        }

        string? id = ParseRemoteTag(box, out string? tagError);
        if (id == null)
        {
            return tagError != null ? tagError : "Guild Stash: this box needs an '@r <id>;' tag.";
        }

        // unregistered id = would mint free storage
        if (!storage.Exists(id))
        {
            return "Guild Stash: no storage with that ID exists. Convert a case to create it first.";
        }

        // refuse if already open somewhere (maybe this player via a second box)
        if (storage.IsOpenSomewhere(id, out _))
        {
            return "Guild Stash: that storage is already open. Close it before opening it again.";
        }

        StoredBox stored = storage.Load(id, out bool unreadable);

        // a corrupt file reads back EMPTY; opening then closing would overwrite the recoverable file with nothing - refuse
        // hopefulyl never happens...
        if (unreadable)
        {
            return $"Guild Stash: storage '{id}' is corrupt and can't be opened - check the server log and storage/backups.";
        }

        // case types must match, or shit gets out of bounds (no idea what happens in that case tbh fam)
        string boxKey = GuildStashContext.BoxKeyForTpl(box.Template);
        if (stored.BoxKey.Length > 0 && boxKey.Length > 0 && stored.BoxKey != boxKey)
        {
            string want = GuildStashContext.BoxNameForKey(stored.BoxKey);
            string have = GuildStashContext.BoxNameForKey(boxKey);
            return $"Guild Stash: storage '{id}' is a {want} - it can only be opened with a {want}, not a {have}.";
        }

        return null;
    }

    private static string? ValidateClose(Item box, GuildStashStorage storage)
    {
        if (!GuildStashContext.OpenToClosedTpl.ContainsKey(box.Template))
        {
            return "Guild Stash: that isn't an open guild box.";
        }

        // close needs a storage id: the binding, or the tag as fallback. Please never happen. I don't want to debug this.
        if (!storage.TryGetBinding(box.Id.ToString(), out _))
        {
            string? tagId = ParseRemoteTag(box, out string? tagError);
            if (tagId == null)
            {
                return tagError != null ? tagError : "Guild Stash: this box needs an '@r <id>;' tag.";
            }
            if (!storage.Exists(tagId))
            {
                return $"Guild Stash: no storage '{tagId}' exists - retag this box with the ID it was opened as, then close it.";
            }
            if (storage.IsOpenSomewhere(tagId, out _))
            {
                return $"Guild Stash: storage '{tagId}' is open in another box - retag this box with the ID it was opened as.";
            }
        }

        return null;
    }

    private static string? ValidateConvert(PmcData pmc, Item box, GuildStashStorage storage, string? clientTag)
    {
        if (!GuildStashContext.BaseToClosedTpl.ContainsKey(box.Template))
        {
            return "Guild Stash: that case can't be made into a guild box."; //Should be impossible
        }

        // same stash-root rule as Open
        if (!IsBoxInStashRoot(pmc, box))
        {
            return "Guild Stash: move the case into your stash before converting it.";
        }

        // prefer the client's live tag when precheck handed one over - our copy may lag it by a queued op
        string? id;
        string? tagError;
        if (clientTag != null)
        {
            id = ParseRemoteTag(clientTag, out tagError);
        }
        else
        {
            id = ParseRemoteTag(box, out tagError);
        }
        if (id == null)
        {
            return tagError != null ? tagError : ConvertNeedsTagMessage;
        }

        // id must be brand new
        if (storage.Exists(id))
        {
            return "Guild Stash: that ID is already in use. Pick a new one.";
        }

        return null;
    }

    // Parse the storage id out of the tag text the SERVER holds for this box.
    public static string? ParseRemoteTag(Item box, out string? error)
    {
        if (box.Upd == null || box.Upd.Tag == null)
        {
            error = null;
            return null;
        }
        return ParseRemoteTag(box.Upd.Tag.Name, out error);
    }

    // Pull the storage id out of a tag string.
    // Strict grammar: the literal "@r ", an alphanumeric id, then ';' ("FreeText @r guildammo; FreeText").
    public static string? ParseRemoteTag(string? name, out string? error)
    {
        error = null;

        if (name == null)
        {
            return null;
        }

        const string marker = "@r "; //Space at the end prevents other @rambunctious tags.

        int markerIndex = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        // id is everything between "@r " and the next ';'
        int start = markerIndex + marker.Length;
        int end = name.IndexOf(';', start);
        if (end < 0)
        {
            error = "Guild Stash: end the id with ';' - e.g. '@r guildammo;'.";
            return null;
        }

        // fold to lowercase
        string token = name.Substring(start, end - start).ToLowerInvariant();

        if (token.Length == 0)
        {
            error = "Guild Stash: add an id between '@r' and ';' - e.g. '@r guildammo;'.";
            return null;
        }

        // letters and numbers only - any space, tab, padding or symbol means it's malformed, reject the whole thing
        foreach (char ch in token)
        {
            bool alnum = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9');
            if (!alnum)
            {
                error = $"Guild Stash: the id '{token}' can only contain letters and numbers - no spaces, dashes, underscores or symbols.";
                return null;
            }
        }

        return token;
    }
}
