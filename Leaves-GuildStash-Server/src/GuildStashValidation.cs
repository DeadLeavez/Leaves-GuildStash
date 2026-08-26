using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_GuildStash_Server;

// The pure (no-mutation) validation for the three actions, shared by two callers:
//   - the item-event router, as the authoritative gate right before it mutates;
//   - the /guildstash/precheck HTTP endpoint, so the CLIENT can find out a "you can't do that" reason and show a soft notification, instead of letting the item-event fail with a hard Warning (which forces a full client resync).
// Same checks, same messages, both places.
public static class GuildStashValidation
{
    // Returns null if the action is allowed for this box, or a player-facing reason if not.
    public static string? ValidateAction(PmcData pmc, Item box, string action, GuildStashStorage storage)
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
            return ValidateConvert(pmc, box, storage);
        }
        return "Guild Stash: unknown action.";
    }

    // Both Open and Convert spawn a replacement case into the box's current slot.
    // Doing that anywhere but the stash root means live-adding a child-bearing item into a NESTED container, which wedges the client op-queue (silent hang).
    // So both are gated to the root.
    private static string? RequireStashRoot(PmcData pmc, Item box, string what)
    {
        string stashId = pmc.Inventory != null && pmc.Inventory.Stash != null ? pmc.Inventory.Stash.ToString() : "";
        if (stashId.Length == 0 || box.ParentId != stashId)
        {
            return $"Guild Stash: move the case into your stash before {what}.";
        }
        return null;
    }

    private static string? ValidateOpen(PmcData pmc, Item box, GuildStashStorage storage)
    {
        if (!GuildStashContext.ClosedToOpenTpl.ContainsKey(box.Template))
        {
            return "Guild Stash: that isn't a closed guild box.";
        }

        // Only openable from the stash root - opening spawns a big open case that must land there.
        string? rootError = RequireStashRoot(pmc, box, "opening it");
        if (rootError != null)
        {
            return rootError;
        }

        string? id = ParseRemoteTag(box, out string? tagError);
        if (id == null)
        {
            return tagError != null ? tagError : "Guild Stash: this box needs an '@remote <id>' tag.";
        }

        // Only storages created through Convert exist - opening an unregistered id would mint free storage.
        if (!storage.Exists(id))
        {
            return "Guild Stash: no storage with that ID exists. Convert a case to create it first.";
        }

        // Lock - refuse if it's already open somewhere (possibly this player, via a second box).
        if (storage.IsOpenSomewhere(id, out _))
        {
            return "Guild Stash: that storage is already open. Close it before opening it again.";
        }

        var stored = storage.Load(id, out bool unreadable);

        // A storage file that exists but won't parse reads back as EMPTY.
        // Opening it would show an empty box and the next close would overwrite the (recoverable) corrupt file with that emptiness - so refuse and point at the backups instead of quietly destroying the stash.
        if (unreadable)
        {
            return $"Guild Stash: storage '{id}' is corrupt and can't be opened - check the server log and storage/backups.";
        }

        // Case types must match.
        // Contents saved from one case carry that case's grid names and cell coordinates; handing them to a different case gives the client a layout it can't place.
        // Storages written before BoxKey existed have no recorded type, so they're grandfathered (any box may open them) and get stamped on their next close.
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

        // The close needs a storage id - normally from the open-binding, or the tag as a fallback.
        // The fallback may only ever target an already-registered, currently-idle storage, or a retag while open could redirect the write over someone else's stash (see GuildStashCloser for the full reasoning - these checks must stay in step with it).
        if (!storage.TryGetBinding(box.Id.ToString(), out _))
        {
            string? tagId = ParseRemoteTag(box, out string? tagError);
            if (tagId == null)
            {
                return tagError != null ? tagError : "Guild Stash: this box needs an '@remote <id>' tag.";
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

    private static string? ValidateConvert(PmcData pmc, Item box, GuildStashStorage storage)
    {
        if (!GuildStashContext.BaseToClosedTpl.ContainsKey(box.Template))
        {
            return "Guild Stash: that case can't be made into a guild box.";
        }

        // Same stash-root rule as Open: converting a case that's sitting inside another container respawns it there, and a filled case delivered into a nested container wedges the client op-queue.
        string? rootError = RequireStashRoot(pmc, box, "converting it");
        if (rootError != null)
        {
            return rootError;
        }

        string? id = ParseRemoteTag(box, out string? tagError);
        if (id == null)
        {
            return tagError != null ? tagError : "Guild Stash: tag the case '@remote <id>' first.";
        }

        // The id must be brand new - an existing id already belongs to a storage.
        if (storage.Exists(id))
        {
            return "Guild Stash: that ID is already in use. Pick a new one.";
        }

        return null;
    }

    // Pull the storage id out of an item tag.
    // The "@remote" marker may appear ANYWHERE in the tag; the id is the ONE token after it, and it must be the LAST thing in the tag - so a player names the box BEFORE the marker: "Shared Ammo @remote guildammo" -> id "guildammo".
    // The id is whitespace-delimited, letters-and-numbers only (it becomes a filename), and case-folds to lowercase ("guildAmmo" == that same id).
    // Returns the id, or null with `error` set to a specific reason for a MALFORMED marker (so the caller can warn precisely); a plain missing marker returns (null, null) - the caller's generic message.
    public static string? ParseRemoteTag(Item box, out string? error)
    {
        error = null;

        if (box.Upd == null || box.Upd.Tag == null || box.Upd.Tag.Name == null)
        {
            return null;
        }

        string name = box.Upd.Tag.Name;
        const string marker = "@remote";
        int markerIndex = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        // The marker must be followed by whitespace, so "@remotely" isn't mistaken for it.
        int pos = markerIndex + marker.Length;
        if (pos >= name.Length || !char.IsWhiteSpace(name[pos]))
        {
            return null;
        }
        while (pos < name.Length && char.IsWhiteSpace(name[pos]))
        {
            pos++;
        }

        // The id token: the run of non-whitespace characters after the marker.
        int start = pos;
        while (pos < name.Length && !char.IsWhiteSpace(name[pos]))
        {
            pos++;
        }
        string token = name.Substring(start, pos - start);

        if (token.Length == 0)
        {
            error = "Guild Stash: add an id after '@remote', e.g. '@remote guildammo'.";
            return null;
        }

        // Letters and numbers only - a delimiter like '-' or '_' is rejected (not silently truncated).
        foreach (char ch in token)
        {
            bool alnum = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9');
            if (!alnum)
            {
                error = $"Guild Stash: the id '{token}' can only contain letters and numbers - no dashes, underscores or symbols.";
                return null;
            }
        }

        // The id must be the LAST token - anything after it means the name wasn't put before the marker.
        while (pos < name.Length && char.IsWhiteSpace(name[pos]))
        {
            pos++;
        }
        if (pos < name.Length)
        {
            error = "Guild Stash: the id must be last - put the name BEFORE '@remote' (e.g. 'Shared Ammo @remote guildammo').";
            return null;
        }

        // Case-insensitive: fold to lowercase so "guildAmmo" and "guildammo" are the same storage.
        return token.ToLowerInvariant();
    }
}
