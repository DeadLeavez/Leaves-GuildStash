using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using IcyClawz.CustomInteractions;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using SPT.Reflection.Utils;

namespace Leaves_GuildStash_Client
{
    // Adds the guild-stash context-menu entries.
    // IcyClawz calls GetCustomInteractions every time an item's right-click menu is built; we look at the item's TEMPLATE against the registry the server handed us (GET /guildstash/boxtpls, fetched once on load) and return the one entry that fits:
    //   closed guild box -> "Open Remote"    open guild box -> "Close Remote"
    //   plain Item/THICC case -> "Convert to Remote"
    // So a closed box only ever offers Open, an open box only Close. The server re-validates every action (tag, tpl) regardless of the menu.
    internal sealed class GuildStashInteractionsProvider : ICustomInteractionsProvider
    {
        // Template-id sets from the server. Populated by FetchRegistry (background, once). Read-only after.
        private static readonly HashSet<string> ClosedTpls = new HashSet<string>();
        private static readonly HashSet<string> OpenTpls = new HashSet<string>();
        private static readonly HashSet<string> BaseTpls = new HashSet<string>();
        private static bool _registryLoaded;

        // Kick off the one-time registry fetch. Called from Plugin.Awake; runs off-thread (we only touch the thread-safe sets, never a Unity object).
        // Menus built before it finishes simply show no guild entries until it lands (a second or two after the server is reachable).
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
                    Plugin.Dbg($"Registry loaded: {ClosedTpls.Count} closed, {OpenTpls.Count} open, {BaseTpls.Count} base tpls.");
                }
                catch (Exception ex)
                {
                    Plugin.Dbg("Registry fetch FAILED: " + ex.Message);
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

        // True when the item sits directly in the stash's main grid (container id "hideout"), i.e. the stash root.
        // Item/ItemAddress/IContainer are plain EFT classes (not Unity objects), so ordinary null checks are safe here. Mirrors the server's stash-root check for Open.
        private static bool IsInStashRoot(Item item)
        {
            ItemAddress address = item.CurrentAddress;
            if (address == null)
            {
                return false;
            }
            EFT.InventoryLogic.IContainer container = address.Container;
            if (container == null)
            {
                return false;
            }
            return container.ID == "hideout";
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

            if (ClosedTpls.Contains(tpl))
            {
                // Only openable from the stash root (matches the server check). Grey the entry out elsewhere and say why, rather than letting the click fail with a warning.
                interactions.Add(new CustomInteraction(context)
                {
                    Caption = () => "Open Remote",
                    Enabled = () => IsInStashRoot(item),
                    Error = () => "Move the box into your stash to open it.",
                    Action = () => TryAction("GuildStashOpen", itemId),
                });
            }
            else if (OpenTpls.Contains(tpl))
            {
                interactions.Add(new CustomInteraction(context)
                {
                    Caption = () => "Close Remote",
                    Enabled = () => true,
                    Action = () => TryAction("GuildStashClose", itemId),
                });
            }
            else if (BaseTpls.Contains(tpl))
            {
                // Convert respawns the case where it stands, and delivering a FILLED case into a nested container wedges the op-queue - so, like Open, it's stash-root only.
                // Grey it out elsewhere with the reason rather than letting the click fail.
                interactions.Add(new CustomInteraction(context)
                {
                    Caption = () => "Convert to Remote",
                    Enabled = () => IsInStashRoot(item),
                    Error = () => "Move the case into your stash to convert it.",
                    Action = () => TryAction("GuildStashConvert", itemId),
                });
            }

            return interactions;
        }

        // Menu-click entry point. Preflight the server's validation first: on a soft "you can't do that", show a lightweight in-game notification (no client reset) and stop.
        // Only a passing check escalates to the real item-event.
        // Runs on the Unity main thread (the click is) - the preflight is one quick localhost POST, and NotificationManagerClass must be called from the main thread anyway.
        private static void TryAction(string action, string itemId)
        {
            string reason = Precheck(action, itemId);
            if (reason != null)
            {
                NotificationManagerClass.DisplayWarningNotification(reason, ENotificationDurationType.Default);
                return;
            }
            SendAction(action, itemId);
        }

        // Ask the server whether this action is allowed, without doing it.
        // Returns null when allowed (or when the check itself couldn't run - fail open, since the item-event's own Warning is the real backstop), otherwise the player-facing reason to show.
        private static string Precheck(string action, string itemId)
        {
            try
            {
                string body = "{\"action\":\"" + action + "\",\"itemId\":\"" + itemId + "\"}";
                string response = RequestHandler.PostJson("/guildstash/precheck", body);
                JObject root = JObject.Parse(response);

                JToken okToken = root["ok"];
                bool ok = okToken != null && okToken.Value<bool>();
                if (ok)
                {
                    return null;
                }

                JToken messageToken = root["message"];
                if (messageToken != null && messageToken.Type != JTokenType.Null)
                {
                    return messageToken.ToString();
                }
                return "Guild Stash: that action isn't allowed right now.";
            }
            catch (Exception ex)
            {
                Plugin.Dbg("Precheck failed (allowing action through): " + ex.Message);
                return null;
            }
        }

        // Enqueue our custom item-event through the game's OWN operation queue.
        // The queue serializes the plain object into { data: [ <op> ], ... }, POSTs to items/moving, and applies the response's profileChanges automatically - so the box swaps live, no relaunch.
        //
        // MUST run on the Unity main thread (the context-menu click already is).
        // The callback is NOT optional: the queue Invokes it with no null check, so a null one NREs and jams the queue.
        private static void SendAction(string action, string itemId)
        {
            Plugin.Capture = true; // open the first-chance exception capture window for this action
            Plugin.Dbg($"Sending {action} for item {itemId} through the operation queue...");
            try
            {
                object session = PatchConstants.BackEndSession;
                if (session == null)
                {
                    Plugin.Dbg("ERROR: No BackEndSession (need to be at the menu with a profile loaded).");
                    return;
                }

                MethodInfo send = session.GetType().GetMethod("SendOperationRightNow", BindingFlags.Public | BindingFlags.Instance);
                if (send == null)
                {
                    Plugin.Dbg("ERROR: SendOperationRightNow not found on " + session.GetType().FullName);
                    return;
                }

                Type callbackType = send.GetParameters()[1].ParameterType;
                MethodInfo handler = typeof(GuildStashInteractionsProvider).GetMethod(nameof(OnActionResult), BindingFlags.NonPublic | BindingFlags.Static);
                Delegate callback = Delegate.CreateDelegate(callbackType, handler);

                object operation = new { Action = action, ItemId = itemId };
                send.Invoke(session, new object[] { operation, callback });
                Plugin.Dbg($"{action} enqueued. Awaiting apply/result...");
            }
            catch (Exception ex)
            {
                Plugin.Dbg($"{action} FAILED: " + ex);
            }
        }

        // Operation-completion callback.
        // Object param (not the game's IResult) so we don't hard-reference an obfuscated type; Delegate.CreateDelegate binds it to the real Callback delegate by contravariance.
        private static void OnActionResult(object result)
        {
            Plugin.Dbg("Guild-stash operation callback FIRED. Result: " + result);
        }
    }
}
