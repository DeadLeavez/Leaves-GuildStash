using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers.Ws;

namespace Leaves_GuildStash_Server;

// Close a player's open boxes after their websocket goes away and STAYS away - alt-F4 / crash, the one exit nothing else catches in realtime.
//
// TRAP (don't undo): the websocket also drops in normal play - measured ~1s after deploy, and on 5s menu reconnects. OnClose is NOT "the player left"; acting on it directly closes a box out from under someone using their stash.
// So: wait wsDisconnectGraceSeconds, then close only if still gone. A blip/relaunch reconnects (login-reconcile covers a relaunch); a real alt-F4 doesn't.
//
// Firing during a raid is fine: raid-start already closed everything, so this finds nothing. If raid-start failed and it does close something, that's still safe - the stash is server-authoritative across a raid.
//
// Harmony because OnClose isn't virtual. The handler is a Singleton (reconnect check off the root provider); the cleanup is Scoped (needs a scope).
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

            // session id = last path segment of the socket URL (same as the original)
            string? path = context?.Request?.Path.Value;
            if (path == null)
            {
                return;
            }
            string[] segments = path.Split('/');
            string sessionId = segments[segments.Length - 1];
            if (sessionId.Length != 24)
            {
                return;
            }

            // fire and forget: OnClose must return now, the answer comes later. discard = deliberate, not a missing await.
            _ = CloseIfStillGoneAsync(sessionId, grace);
        }
        catch (Exception)
        {
            // never interfere with the server's own socket teardown
        }
    }

    private static async Task CloseIfStillGoneAsync(string sessionId, int graceSeconds)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(graceSeconds));

#pragma warning disable CS0618
            IServiceProvider provider = ServiceLocator.ServiceProvider;
#pragma warning restore CS0618

            SptWebSocketConnectionHandler? wsHandler = provider.GetService<SptWebSocketConnectionHandler>();
            MongoId sessionMongoId = new(sessionId);

            // reconnected inside the grace window (blip / raid / relaunch) - leave it
            if (wsHandler != null && wsHandler.IsWebSocketConnected(sessionMongoId))
            {
                return;
            }

            using (IServiceScope scope = provider.CreateScope())
            {
                GuildStashSessionCleanup? cleanup = scope.ServiceProvider.GetService<GuildStashSessionCleanup>();
                if (cleanup == null)
                {
                    return;
                }

                int closed = cleanup.CloseAllForSession(sessionMongoId, "ws-disconnect");
                if (closed == 0)
                {
                    return;
                }

                Leaves.Utils.Instance.printColor($"[GuildStash] ws-disconnect: {sessionId} stayed gone for {graceSeconds}s - closed {closed} open box(es).", LogTextColor.Cyan);
            }
        }
        catch (Exception ex)
        {
            // detached - an exception has nowhere to go. log and drop; the startup sweep backstops anything left open.
            Leaves.Utils.Instance.printColor($"[GuildStash] ws-disconnect cleanup for {sessionId} failed: {ex.Message}", LogTextColor.Red);
        }
    }
}
