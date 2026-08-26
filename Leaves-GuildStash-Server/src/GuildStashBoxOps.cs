using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_GuildStash_Server;

// The shared box surgery.
// Every state change - open, close, convert - is the SAME move: delete the old case plus its whole subtree, and spawn a brand-new case (fresh ids for the case AND every content), already filled, delivered to the stash ROOT as one item tree.
// This is the ONLY way to fill a box live without wedging the client op-queue (you cannot live-add a child-bearing item into a nested container).
// Fresh ids also dodge duplicate-id corruption.
public static class GuildStashBoxOps
{
    // The box item plus every descendant id (BFS). box is null if the id isn't in the profile.
    public static (Item? box, List<Item> descendants) GatherSubtree(PmcData pmc, string boxId)
    {
        var items = pmc.Inventory.Items;

        Item? box = null;
        foreach (var item in items)
        {
            if (item.Id.ToString() == boxId)
            {
                box = item;
                break;
            }
        }
        if (box == null)
        {
            return (null, new List<Item>());
        }

        var descendantIds = new HashSet<string>();
        var frontier = new List<string> { boxId };
        while (frontier.Count > 0)
        {
            var next = new List<string>();
            foreach (var item in items)
            {
                string? parent = item.ParentId;
                if (string.IsNullOrEmpty(parent))
                {
                    continue;
                }
                string id = item.Id.ToString();
                if (frontier.Contains(parent) && !descendantIds.Contains(id))
                {
                    descendantIds.Add(id);
                    next.Add(id);
                }
            }
            frontier = next;
        }

        var descendants = new List<Item>();
        foreach (var item in items)
        {
            if (descendantIds.Contains(item.Id.ToString()))
            {
                descendants.Add(item);
            }
        }
        return (box, descendants);
    }

    // Delete oldBox + its subtree and spawn a fresh box (newTpl, newBoxId) in the exact same slot, filled with `contents`.
    // `contentsRootId` is the id that the TOP-LEVEL contents currently name as their ParentId (the storage's saved root on open; the old box's own id on convert) - it's remapped to the new box so the top level nests inside it.
    // Returns what to delete/create for the item-event response.
    //
    // `newBoxId` is minted by the CALLER rather than here so the open path can claim the storage lock against that id BEFORE this method mutates anything (see GuildStashStorage.TryClaimStorage).
    public static (List<string> deleted, List<Item> created) ReplaceBox(
        PmcData pmc, Item oldBox, MongoId newTpl, MongoId newBoxId, string contentsRootId, List<Item> contents)
    {
        var items = pmc.Inventory.Items;

        // Where the old box sits - the new one takes the same spot so nothing visibly moves.
        var boxParentId = oldBox.ParentId;
        var boxSlotId = oldBox.SlotId;
        var boxLocation = oldBox.Location;
        var boxUpd = oldBox.Upd;

        // Delete the old box + its whole subtree.
        var (_, oldDescendants) = GatherSubtree(pmc, oldBox.Id.ToString());
        var deleteSet = new HashSet<string> { oldBox.Id.ToString() };
        foreach (var d in oldDescendants)
        {
            deleteSet.Add(d.Id.ToString());
        }
        var deleted = new List<string>(deleteSet);
        items.RemoveAll((Item item) => deleteSet.Contains(item.Id.ToString()));

        // Fresh id for every content (old id -> new id).
        // Seed the map with the contents root so top-level items reparent onto the new box.
        var idMap = new Dictionary<string, string>();
        idMap[contentsRootId] = newBoxId.ToString();
        foreach (var content in contents)
        {
            idMap[content.Id.ToString()] = new MongoId().ToString();
        }

        var newBox = new Item
        {
            Id = newBoxId,
            Template = newTpl,
            ParentId = boxParentId,
            SlotId = boxSlotId,
            Location = boxLocation,
            Upd = boxUpd,
        };
        var created = new List<Item> { newBox };
        items.Add(newBox);

        // Re-id every content and re-point its parent through the map.
        foreach (var content in contents)
        {
            string oldId = content.Id.ToString();
            string oldParent = content.ParentId ?? "";
            content.Id = new MongoId(idMap[oldId]);
            if (oldParent.Length > 0 && idMap.TryGetValue(oldParent, out var newParent))
            {
                content.ParentId = newParent;
            }
            created.Add(content);
            items.Add(content);
        }

        return (deleted, created);
    }
}
