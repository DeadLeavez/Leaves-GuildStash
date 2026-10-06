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
// A web-UI force-close changes an online player's inventory out-of-band, leaving their client with a stale open box - drag an item into that phantom and the server gets a move whose parent id is gone.
// A ForceLogout websocket event would fix it but drops the player to the DESKTOP. Instead: an item-event Warning (AppendErrorToOutput) makes the client discard the response and pull a fresh profile - a reload.
// Force-close has no item-event to attach that to, so flag the session and attach it to the next one they send.
//
// Safe on a shared object: EventOutputHolder.GetOutput returns a per-session CACHED response, but ItemEventRouter.HandleEvents calls ResetOutput at the end of every request, so it's fresh per item-event.
// Known side effect: HandleEvents stops its loop once output.Warnings is non-empty, so a multi-action batch colliding with a pending resync runs only its first action. The reload follows immediately and fires at most once per force-close.
public class GuildStashResyncPatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(EventOutputHolder).GetMethod(nameof(EventOutputHolder.GetOutput))!;
    }

    [PatchPostfix]
    public static void Postfix(ItemEventRouterResponse __result, MongoId sessionId)
    {
        // fail silent: runs at the head of EVERY item-event - a bug here must never break an inventory action. worst case of doing nothing: the client stays stale until the next failing interaction
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
            using (IServiceScope scope = ServiceLocator.ServiceProvider.CreateScope())
#pragma warning restore CS0618
            {
                HttpResponseUtil? httpResponseUtil = scope.ServiceProvider.GetService<HttpResponseUtil>();
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
