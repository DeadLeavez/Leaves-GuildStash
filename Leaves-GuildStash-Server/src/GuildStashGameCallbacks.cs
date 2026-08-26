using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services;
using SPTarkov.Server.Core.Utils;

namespace Leaves_GuildStash_Server;

// Auto-closes guild boxes a player left open, by hooking the game-session lifecycle.
// GameStaticRouter injects GameCallbacks by its concrete type, so a TypeOverride subclass slots straight into the /client/game/* routes with no Harmony.
// We override only two endpoints and defer the rest to base:
//
//   - GameLogout (/client/game/logout): clean exit. Close the session's open boxes THEN call base - base saves the profile first, so the closed box is what gets persisted.
//   - GameStart (/client/game/start): login. Backstop for the one case a logout can't catch - a box left open when the SERVER restarted. A normal login with nothing open is a cheap no-op scan.
//
// A non-clean exit (crash / taskkill) fires neither; GuildStashWebSocketClosePatch covers that via the websocket disconnect, reusing the same GuildStashSessionCleanup.
[Injectable(TypeOverride = typeof(GameCallbacks))]
public class GuildStashGameCallbacks(
    HttpResponseUtil httpResponseUtil,
    Watermark watermark,
    SaveServer saveServer,
    BackupService backupService,
    GameController gameController,
    ProfileActivityService profileActivityService,
    TimeUtil timeUtil,
    GuildStashSessionCleanup cleanup)
    : GameCallbacks(httpResponseUtil, watermark, saveServer, backupService, gameController, profileActivityService, timeUtil)
{
    // Clean logout: close open boxes BEFORE base saves the profile, so the persisted state is closed.
    public override async ValueTask<string> GameLogout(string url, EmptyRequestData info, MongoId sessionID)
    {
        try
        {
            cleanup.CloseAllForSession(sessionID, "logout");
        }
        catch (Exception ex)
        {
            // Never let our cleanup break the player's logout - log and fall through to the real logout.
            Leaves.Utils.Instance.printColor($"[GuildStash] logout cleanup failed for {sessionID}: {ex.Message}", LogTextColor.Red);
        }
        return await base.GameLogout(url, info, sessionID);
    }

    // Login: reconcile any box left open across a server restart, before the client reads its inventory.
    public override async ValueTask<string> GameStart(string url, EmptyRequestData info, MongoId sessionID)
    {
        var result = await base.GameStart(url, info, sessionID);
        try
        {
            cleanup.CloseAllForSession(sessionID, "login-reconcile");
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] login-reconcile failed for {sessionID}: {ex.Message}", LogTextColor.Red);
        }
        return result;
    }
}
