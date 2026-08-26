using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Servers.Ws;

namespace Leaves_GuildStash_Server;

// Close a player's open boxes after their websocket goes away and STAYS away - the alt-F4 / crash case, the one exit that fires neither GameLogout nor anything else in realtime.
//
// THE TRAP THIS AVOIDS (measured 2026-08-26, don't undo it): the websocket disconnects during completely NORMAL play.
// In one recorded raid it dropped 1s after deploying, and again while the player sat in the menu - reconnecting 5s later.
// So `OnClose` is NOT "the player left". Acting on it directly, which is how this was originally planned, would have closed a box out from under someone browsing their own stash.
//
// Hence: on close, wait `wsDisconnectGraceSeconds`, then close ONLY if the socket is still gone.
// A blip reconnects and we do nothing; a relaunch reconnects and the GameStart login-reconcile handles it; a real alt-F4 never reconnects and the boxes close.
//
// Firing DURING a raid is harmless and deliberate: the in-raid disconnect lasts the whole raid, so this will run, but raid-start already closed everything and it finds nothing to do.
// In the one case where it does find something (a raid-start close that failed), closing mid-raid is still safe - the stash is server-authoritative across a raid, so the raid ending can't resurrect or duplicate anything.
//
// Harmony because `SptWebSocketConnectionHandler.OnClose` isn't virtual. The handler itself is a Singleton, so the reconnect check pulls straight off the root provider; the cleanup is Scoped and needs a scope.
public class GuildStashWebSocketClosePatch : AbstractPatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(SptWebSocketConnectionHandler).GetMethod(nameof(SptWebSocketConnectionHandler.OnClose))!;
    }

    [PatchPostfix]
    public static void Postfix(HttpContext context)
    {
        try
        {
            if (GuildStashContext.Config == null || !GuildStashContext.Config.Enabled)
            {
                return;
            }
            int grace = GuildStashContext.Config.WsDisconnectGraceSeconds;
            if (grace <= 0)
            {
                return;
            }

            // Same derivation the original uses - the session id is the last path segment of the socket URL.
            string? path = context?.Request?.Path.Value;
            if (path == null)
            {
                return;
            }
            var segments = path.Split('/');
            string sessionId = segments[segments.Length - 1];
            if (sessionId.Length != 24)
            {
                return;
            }

            // Fire and forget - OnClose must return promptly, and the whole point is to answer later.
            // Assigned to a discard so it reads as deliberate rather than a missing await.
            _ = CloseIfStillGoneAsync(sessionId, grace);
        }
        catch (Exception)
        {
            // Never let this interfere with the server's own socket teardown.
        }
    }

    private static async Task CloseIfStillGoneAsync(string sessionId, int graceSeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(graceSeconds));

#pragma warning disable CS0618
            var provider = ServiceLocator.ServiceProvider;
#pragma warning restore CS0618

            var wsHandler = provider.GetService<SptWebSocketConnectionHandler>();
            var sessionMongoId = new MongoId(sessionId);

            // Reconnected inside the grace window: a menu blip, a raid, or a quick relaunch. Leave them be.
            if (wsHandler != null && wsHandler.IsWebSocketConnected(sessionMongoId))
            {
                return;
            }

            using (var scope = provider.CreateScope())
            {
                var cleanup = scope.ServiceProvider.GetService<GuildStashSessionCleanup>();
                if (cleanup == null)
                {
                    return;
                }

                int closed = cleanup.CloseAllForSession(sessionMongoId, "ws-disconnect");
                if (closed == 0)
                {
                    return;
                }

                var saveServer = scope.ServiceProvider.GetService<SaveServer>();
                if (saveServer != null)
                {
                    await saveServer.SaveProfileAsync(sessionMongoId);
                }

                Leaves.Utils.Instance.printColor($"[GuildStash] ws-disconnect: {sessionId} stayed gone for {graceSeconds}s - closed {closed} open box(es).", LogTextColor.Cyan);
            }
        }
        catch (Exception ex)
        {
            // This runs detached, so an escaping exception has nowhere useful to go - log and drop it.
            // The startup sweep is still the backstop for anything left open.
            Leaves.Utils.Instance.printColor($"[GuildStash] ws-disconnect cleanup for {sessionId} failed: {ex.Message}", LogTextColor.Red);
        }
    }
}
