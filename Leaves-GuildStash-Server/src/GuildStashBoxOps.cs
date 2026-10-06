using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace Leaves_GuildStash_Server;

public static class GuildStashBoxOps
{
    // The box item plus every descendant id (BFS). box is null if the id isn't in the profile.
    public static (Item? box, List<Item> descendants) GatherSubtree(PmcData pmc, string boxId)
    {
        List<Item> items = pmc.Inventory.Items!; // always populated on a loaded profile

        Item? box = null;
        foreach (Item item in items)
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

        HashSet<string> descendantIds = new();
        List<string> currentLevelIds = new() { boxId };
        while (currentLevelIds.Count > 0)
        {
            List<string> nextLevelIds = new();
            foreach (Item item in items)
            {
                string? parent = item.ParentId;
                if (string.IsNullOrEmpty(parent))
                {
                    continue;
                }
                string id = item.Id.ToString();
                if (currentLevelIds.Contains(parent) && !descendantIds.Contains(id))
                {
                    descendantIds.Add(id);
                    nextLevelIds.Add(id);
                }
            }
            currentLevelIds = nextLevelIds;
        }

        List<Item> descendants = new();
        foreach (Item item in items)
        {
            if (descendantIds.Contains(item.Id.ToString()))
            {
                descendants.Add(item);
            }
        }
        return (box, descendants);
    }

    // Delete oldBox + its subtree and spawn a fresh box (newTpl, newBoxId) in the same slot, filled with `contents`.
    // Returns what to delete/create for the item-event response.
    // newBoxId comes from the CALLER so the open path can claim the storage lock against it before this mutates anything.
    public static (List<string> deleted, List<Item> created) ReplaceBox(
        PmcData pmc,
        Item oldBox,
        MongoId newTpl,
        MongoId newBoxId,
        string contentsRootId,
        List<Item> contents)
    {
        List<Item> items = pmc.Inventory.Items!; // always populated on a loaded profile

        // capture the old box's placement to reuse for the new one
        string? boxParentId = oldBox.ParentId; // always "hideout" in practice, but no point hard-coding it
        string? boxSlotId = oldBox.SlotId; // shouldn't ever matter, but not going to purge it
        object? boxLocation = oldBox.Location;
        Upd? boxUpd = oldBox.Upd;

        (_, List<Item> oldDescendants) = GatherSubtree(pmc, oldBox.Id.ToString());

        HashSet<string> deleteSet = new() { oldBox.Id.ToString() };
        foreach (Item toDelete in oldDescendants)
        {
            deleteSet.Add(toDelete.Id.ToString());
        }
        List<string> deleted = new(deleteSet);
        items.RemoveAll((Item item) => deleteSet.Contains(item.Id.ToString()));

        // old id -> new id for every content; seed with the contents root so top-level items reparent onto the new box
        Dictionary<string, string> idMap = new();
        idMap[contentsRootId] = newBoxId.ToString();
        foreach (Item content in contents)
        {
            idMap[content.Id.ToString()] = new MongoId().ToString();
        }

        Item newBox = new()
        {
            Id = newBoxId,
            Template = newTpl,
            ParentId = boxParentId,
            SlotId = boxSlotId,
            Location = boxLocation,
            Upd = boxUpd,
        };
        List<Item> created = new() { newBox };
        items.Add(newBox);

        // re-id each content, re-point its parent through the map
        foreach (Item content in contents)
        {
            string oldId = content.Id.ToString();
            string oldParent = content.ParentId ?? "";
            content.Id = new MongoId(idMap[oldId]);
            if (oldParent.Length > 0 && idMap.TryGetValue(oldParent, out string? newParent))
            {
                content.ParentId = newParent;
            }
            created.Add(content);
            items.Add(content);
        }

        return (deleted, created);
    }
}
