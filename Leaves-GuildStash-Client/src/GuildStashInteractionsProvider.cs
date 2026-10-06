using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using IcyClawz.CustomInteractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using SPT.Reflection.Utils;
using UIFixesInterop;

namespace Leaves_GuildStash_Client
{
    // Adds the guild-stash context-menu entries. IcyClawz calls GetCustomInteractions every time a right-click menu is built.
    // We match the item's tpl against the registry the server handed us (GET /guildstash/boxtpls, fetched once on load):
    //   closed box  -> "Open Remote"
    //   open box    -> "Close Remote"
    //   base case   -> "Convert to Remote" (thicc + regular item cases by default)
    internal sealed class GuildStashInteractionsProvider : ICustomInteractionsProvider
    {
        // Template-id sets from the server.
        private static readonly HashSet<string> ClosedTpls = new HashSet<string>();
        private static readonly HashSet<string> OpenTpls = new HashSet<string>();
        private static readonly HashSet<string> BaseTpls = new HashSet<string>();
        private static volatile bool _registryLoaded;

        // one-time registry fetch
        internal static void LoadRegistry()
        {
            Task.Run(() =>
            {
                try
                {
                    string json = RequestHandler.GetJson("/guildstash/boxtpls");
                    JObject root = JObject.Parse(json);
                    FillSet(root, "closed", ClosedTpls);
                    FillSet(root, "open", OpenTpls);
                    FillSet(root, "baseTpls", BaseTpls);
                    _registryLoaded = true;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError("[GuildStash] Registry fetch failed: " + ex.Message);
                }
            });
        }

        private static void FillSet(JObject root, string field, HashSet<string> set)
        {
            JToken array = root[field];
            if (array == null)
            {
                return;
            }
            foreach (JToken entry in array)
            {
                string tpl = entry.ToString();
                if (tpl.Length > 0)
                {
                    set.Add(tpl);
                }
            }
        }

        // The tag text this client currently sees on the item, or null if it has none.
        //
        // Sent to /guildstash/precheck for Convert ONLY. The server's profile copy can lag this by one
        // queued item-event op, which makes an untagged case and a just-tagged one indistinguishable from
        // its side. We do NOT parse it here - the server runs its own ParseRemoteTag on the string, so
        // there's exactly one set of tag rules. The real Convert op sends no tag at all.
        private static string CurrentTagName(Item item)
        {
            TagComponent tag = item.GetItemComponent<TagComponent>();
            if (tag == null)
            {
                return null;
            }
            return tag.Name;
        }

        private static bool IsInStashRoot(Item item)
        {
            if (item.CurrentAddress == null)
            {
                return false;
            }
            if (item.CurrentAddress.Container == null)
            {
                return false;
            }
            return item.CurrentAddress.Container.ID == "hideout";
        }

        public IEnumerable<CustomInteraction> GetCustomInteractions(ItemUiContext context, EItemViewType viewType, Item item)
        {
            List<CustomInteraction> interactions = new List<CustomInteraction>();

            if (item == null || !_registryLoaded)
            {
                return interactions;
            }

            string tpl = item.StringTemplateId;
            string itemId = item.Id;

            // When UI Fixes multi-select holds more than one item AND this box is part of that selection,
            // swap the single-box entry for a batch entry that fans the same action over every matching
            // box in the selection. Right-clicking a non-selected item clears the selection client-side,
            // so in practice this only ever fires for a member; SelectionContains is belt-and-suspenders.
            bool multi = MultiSelect.Count > 1 && SelectionContains(item);

            if (ClosedTpls.Contains(tpl))
            {
                if (multi)
                {
                    int count = CountSelectionMatching(ClosedTpls);
                    interactions.Add(new CustomInteraction(context)
                    {
                        Caption = () => "Open Remote (" + count + ")",
                        Enabled = () => AllSelectionMatchingInStashRoot(ClosedTpls),
                        Error = () => "Move every selected box into your stash to open them.",
                        Action = () => BatchAction("GuildStashOpen", ClosedTpls),
                    });
                }
                else
                {
                    interactions.Add(new CustomInteraction(context)
                    {
                        Caption = () => "Open Remote",
                        Enabled = () => IsInStashRoot(item),
                        Error = () => "Move the box into your stash to open it.", // shown as the greyed-out reason when Enabled is false
                        Action = () => TryAction("GuildStashOpen", itemId),
                    });
                }
            }
            else if (OpenTpls.Contains(tpl))
            {
                if (multi)
                {
                    int count = CountSelectionMatching(OpenTpls);
                    interactions.Add(new CustomInteraction(context)
                    {
                        Caption = () => "Close Remote (" + count + ")",
                        Enabled = () => true,
                        Action = () => BatchAction("GuildStashClose", OpenTpls),
                    });
                }
                else
                {
                    interactions.Add(new CustomInteraction(context)
                    {
                        Caption = () => "Close Remote",
                        Enabled = () => true,
                        Action = () => TryAction("GuildStashClose", itemId),
                    });
                }
            }
            else if (BaseTpls.Contains(tpl))
            {
                // grey out with a reason rather than let the click fail.
                // Tag state isn't checked here - it's read at click time and sent with the precheck, so a
                // case tagged a moment ago still converts on the first try.
                interactions.Add(new CustomInteraction(context)
                {
                    Caption = () => "Convert to Remote",
                    Enabled = () => IsInStashRoot(item),
                    Error = () => "Move the case into your stash to convert it.",
                    Action = () => TryAction("GuildStashConvert", itemId, CurrentTagName(item)),
                });
            }

            return interactions;
        }

        // --- UI Fixes multi-select helpers -------------------------------------------------------
        // All of these no-op safely when UI Fixes is absent: MultiSelect.Count is 0 and
        // MultiSelect.Items is empty, so the callers never reach a batch path.

        // Is this exact item one of the currently multi-selected items?
        private static bool SelectionContains(Item item)
        {
            foreach (Item selected in MultiSelect.Items)
            {
                if (ReferenceEquals(selected, item))
                {
                    return true;
                }
                if (selected != null && selected.Id == item.Id)
                {
                    return true;
                }
            }
            return false;
        }

        // How many selected items are boxes this action applies to (a mixed selection is fine - the
        // others are skipped). This drives the "(N)" count in the caption.
        private static int CountSelectionMatching(HashSet<string> tpls)
        {
            int count = 0;
            foreach (Item selected in MultiSelect.Items)
            {
                if (selected != null && tpls.Contains(selected.StringTemplateId))
                {
                    count++;
                }
            }
            return count;
        }

        // Every selected box of a matching type must be in the stash root for the batch Open to enable.
        private static bool AllSelectionMatchingInStashRoot(HashSet<string> tpls)
        {
            foreach (Item selected in MultiSelect.Items)
            {
                if (selected == null || !tpls.Contains(selected.StringTemplateId))
                {
                    continue;
                }
                if (!IsInStashRoot(selected))
                {
                    return false;
                }
            }
            return true;
        }

        // Fan a single-box action out over every selected box whose tpl is in `tpls`. UI Fixes' Apply
        // walks the selection in grid order, one item per frame; each box still goes through the exact
        // same precheck + op path as the single-box action, so a bad box just gets skipped with its own
        // notification and the rest carry on.
        private static void BatchAction(string action, HashSet<string> tpls)
        {
            MultiSelect.Apply(selected =>
            {
                if (selected == null || !tpls.Contains(selected.StringTemplateId))
                {
                    return;
                }
                TryAction(action, selected.Id);
            });
        }

        private static void TryAction(string action, string itemId, string currentTag = null)
        {
            string reason = Precheck(action, itemId, currentTag);
            if (reason != null)
            {
                NotificationManagerClass.DisplayWarningNotification(reason, ENotificationDurationType.Default);
                return;
            }
            SendAction(action, itemId);
        }

        // Ask the server whether this action is allowed, without doing it.
        private static string Precheck(string action, string itemId, string currentTag)
        {
            try
            {
                JObject payload = new JObject
                {
                    ["action"] = action,
                    ["itemId"] = itemId
                };

                // Convert passes its live tag so the server can judge it despite the queued-op lag.
                // Omitted entirely when there's no tag, so the server falls back to its own copy.
                if (currentTag != null)
                {
                    payload["tag"] = currentTag;
                }

                string body = payload.ToString(Formatting.None);

                string response = RequestHandler.PostJson("/guildstash/precheck", body);
                JObject root = JObject.Parse(response);

                if (root.Value<bool>("ok"))
                {
                    return null;
                }

                string message = root.Value<string>("message");
                if (message != null)
                {
                    return message;
                }
                return "Guild Stash: that action isn't allowed right now.";
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[GuildStash] Precheck failed (allowing action through): " + ex.Message);
                return null;
            }
        }

        // send the custom item-event through the game's own op-queue - it serializes to { data: [<op>] }, POSTs items/moving, applies profileChanges
        private static void SendAction(string action, string itemId)
        {
            try
            {
                ISession session = PatchConstants.BackEndSession;
                if (session == null)
                {
                    Plugin.Log.LogError("[GuildStash] No BackEndSession (need to be at the menu with a profile loaded).");
                    return;
                }

                object operation = new { Action = action, ItemId = itemId };
                session.SendOperationRightNow(operation, OnActionResult);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[GuildStash] {action} failed: " + ex);
            }
        }

        // completion callback - the queue Invokes it with no null check, so it must exist even though it does nothing
        private static void OnActionResult(object result)
        {
        }
    }
}
