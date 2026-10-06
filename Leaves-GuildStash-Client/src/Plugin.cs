using BepInEx;
using BepInEx.Logging;
using IcyClawz.CustomInteractions;

namespace Leaves_GuildStash_Client
{
    // Client half of the Guild Stash mod. Registers the context-menu actions (Open / Close / Convert).
    // BepInDependency guarantees IcyClawz loads first, so CustomInteractionsManager exists in our Awake.
    // UI Fixes is a SOFT dependency: if present (and loaded before us), the multi-select context entries
    // light up; if absent, MultiSelectInterop silently reports an empty selection and we fall back to the
    // single-box entries. GUID "Tyfon.UIFixes" is the SPT 4.0.x / UI Fixes 5.x id (see MultiSelectInterop.cs).
    [BepInPlugin(PluginGuid, "Leaves-GuildStash-Client", "0.1.0")]
    [BepInDependency("com.IcyClawz.CustomInteractions")]
    [BepInDependency("Tyfon.UIFixes", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Leaves.You.Alone.GuildStashClient";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;

            CustomInteractionsManager.Register(new GuildStashInteractionsProvider());

            GuildStashInteractionsProvider.LoadRegistry();
            Log.LogInfo("[GuildStash] Client interactions registered (Open / Close / Convert).");
        }
    }
}
