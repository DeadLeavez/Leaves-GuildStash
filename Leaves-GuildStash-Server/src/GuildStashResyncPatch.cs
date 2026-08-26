using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// Make a flagged session's client RELOAD, without logging it out.
//
// A web-UI force-close changes a player's inventory out-of-band.
// If they're online, their client is left holding a stale open box - and worse than cosmetic: dragging an item into that phantom box would send the server a move whose parent id no longer exists.
// The first fix for this pushed a `ForceLogout` websocket event, which works but drops the player to the DESKTOP.
//
// The gentler lever is the one this mod started with, before notifications existed: an item-event Warning (`AppendErrorToOutput`) makes the client discard the response and pull a fresh profile - a reload, not a logout.
// Force-close has no item-event of its own to attach that to, so we flag the session and attach it to whatever item-event they send NEXT.
//
// WHY THIS IS SAFE ON A SHARED OBJECT: `EventOutputHolder.GetOutput` returns a CACHED response per session, which looks alarming - a stray Warning could otherwise haunt later responses.
// It can't: `ItemEventRouter.HandleEvents` calls `ResetOutput` at the end of every request, so the object is fresh per item-event.
//
// Side effect worth knowing: `HandleEvents` breaks out of its loop once `output.Warnings` is non-empty, so a multi-action batch that collides with a pending resync applies only its first action.
// The client reloads from the server immediately afterwards, so the player sees the true state either way - and this fires at most once per force-close, which is a rare recovery action.
public class GuildStashResyncPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(EventOutputHolder).GetMethod(nameof(EventOutputHolder.GetOutput))!;
    }

    [PatchPostfix]
    public static void Postfix(ItemEventRouterResponse __result, MongoId sessionId)
    {
        // Fail silent: this runs at the head of EVERY item-event in the game.
        // A fault here must never stop an ordinary inventory action - the worst case of doing nothing is that the player's client stays stale a little longer, which the next failing interaction resolves anyway.
        try
        {
            if (__result == null)
            {
                return;
            }
            if (!GuildStashContext.TakeResync(sessionId.ToString()))
            {
                return;
            }

#pragma warning disable CS0618
            using (var scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
            {
                var httpResponseUtil = scope.ServiceProvider.GetService<HttpResponseUtil>();
                if (httpResponseUtil == null)
                {
                    return;
                }
                httpResponseUtil.AppendErrorToOutput(
                    __result,
                    "Guild Stash: a stash you had open was force-closed - reloading your inventory.",
                    BackendErrorCodes.None);
            }

            Leaves.Utils.Instance.printColor($"[GuildStash] Delivered a forced client reload to {sessionId}.", LogTextColor.Yellow);
        }
        catch (Exception)
        {
            // Swallowed by design - see above.
        }
    }
}
