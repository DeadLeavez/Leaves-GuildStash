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
// GameCallbacks is injected by concrete type, so a TypeOverride subclass slots into /client/game/* with no Harmony.
// Overrides two endpoints, defers the rest to base:
//   GameLogout: clean exit. Close boxes THEN call base, so the persisted profile has the closed box.
//   GameStart: login. Backstop for a box left open across a SERVER restart (logout can't catch that). No-op when nothing's open.
// A crash / taskkill fires neither - GuildStashWebSocketClosePatch covers that via the websocket drop.
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
    public override async ValueTask<string> GameLogout(string url, EmptyRequestData info, MongoId sessionID)
    {
        try // our cleanup must never break logout
        {
            cleanup.CloseAllForSession(sessionID, "logout");
        }
        catch (Exception ex)
        {
            Leaves.Utils.Instance.printColor($"[GuildStash] logout cleanup failed for {sessionID}: {ex.Message}", LogTextColor.Red);
        }
        return await base.GameLogout(url, info, sessionID);
    }

    // reconcile a box left open across a server restart
    public override async ValueTask<string> GameStart(string url, EmptyRequestData info, MongoId sessionID)
    {
        string result = await base.GameStart(url, info, sessionID);
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
