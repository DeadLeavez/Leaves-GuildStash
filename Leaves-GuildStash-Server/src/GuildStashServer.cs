using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers;
using SPTarkov.Server.Core.Models.Logging;
using SPTarkov.Server.Core.Utils.Json.Converters;

namespace Leaves_GuildStash_Server;

// Server half of the Guild Stash mod.
// OnLoad reads config and registers our custom item-event action bodies (the one wiring SPT can't do itself); everything else self-registers via [Injectable].
[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 1)]
public class GuildStashServer(ModHelper modHelper) : IOnLoad
{
    public Task OnLoad()
    {
        string modPath = modHelper.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly());
        GuildStashContext.Config = modHelper.GetJsonDataFromFile<GuildStashConfig>(modPath, "config/config.jsonc");

        Leaves.Utils.Instance.printColor($"[GuildStash] Config read: enabled={GuildStashContext.Config.Enabled}, debug={GuildStashContext.Config.Debug}, {GuildStashContext.Config.Boxes.Count} box(es).", LogTextColor.Magenta);

        if (!GuildStashContext.Config.Enabled)
        {
            Leaves.Utils.Instance.printColor("[GuildStash] Disabled in config - HTTP API not serving.", LogTextColor.Yellow);
            return Task.CompletedTask;
        }

        // Our custom item-event actions need explicit deserializers: BaseInteractionRequestData's converter throws on any Action it doesn't recognise.
        BaseInteractionRequestDataConverter.RegisterModDataHandler(
            GuildStashItemEventRouter.OpenAction,
            (string json) => JsonSerializer.Deserialize<GuildStashActionRequest>(json));
        BaseInteractionRequestDataConverter.RegisterModDataHandler(
            GuildStashItemEventRouter.CloseAction,
            (string json) => JsonSerializer.Deserialize<GuildStashActionRequest>(json));
        BaseInteractionRequestDataConverter.RegisterModDataHandler(
            GuildStashItemEventRouter.ConvertAction,
            (string json) => JsonSerializer.Deserialize<GuildStashActionRequest>(json));

        // Close any open guild box when its owner deploys, so a raid never holds the storage lock.
        // Harmony rather than a DI TypeOverride because LocationLifecycleService is a big-constructor Singleton and our cleanup is Scoped.
        new GuildStashRaidStartPatch().Enable();

        // Refuse discarding an OPEN box, server-side: the item-template flags don't stop the client and nothing in SPT reads them.
        new GuildStashDiscardPatch().Enable();
        new GuildStashSellPatch().Enable();

        // Deliver a queued client reload (from a web-UI force-close) on the session's next item-event.
        new GuildStashResyncPatch().Enable();

        // Alt-F4 / crash: close open boxes once the websocket has stayed gone past the grace period.
        new GuildStashWebSocketClosePatch().Enable();

        Leaves.Utils.Instance.printColor("[GuildStash] Server up - /guildstash/ HTTP API + Open/Close/Convert item-event actions registered, raid-start auto-close patched in.", LogTextColor.Cyan);
        return Task.CompletedTask;
    }
}
