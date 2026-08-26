using System;
using System.IO;
using System.Runtime.ExceptionServices;
using BepInEx;
using BepInEx.Logging;
using IcyClawz.CustomInteractions;
using UnityEngine;

namespace Leaves_GuildStash_Client
{
    // Client half of the Guild Stash mod. Registers the context-menu actions (Open / Close / Convert) and an AUTO-FLUSHED debug log.
    // The game buffers Player.log and a hung/killed client never flushes it, so we mirror Unity error/exception messages (and our own trace) into BepInEx\guildstash-debug.log with AutoFlush, to capture a real apply exception even on a hang.
    //
    // BepInDependency on IcyClawz guarantees that plugin loads first, so CustomInteractionsManager exists by the time our Awake runs.
    [BepInPlugin(PluginGuid, "Leaves-GuildStash-Client", "0.1.0")]
    [BepInDependency("com.IcyClawz.CustomInteractions")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "Leaves.You.Alone.GuildStashClient";

        // Shared logger so the provider can write to the BepInEx console without holding its own ref.
        internal static ManualLogSource Log;

        // Auto-flushed debug file (survives a client hang, unlike the buffered Player.log).
        private static StreamWriter _dbg;

        // First-chance capture window: opened by a claim click, capped so it never runs away.
        internal static bool Capture;
        private static int _captureCount;

        private void Awake()
        {
            Log = Logger;

            try
            {
                // Absolute path (dev diagnostic) - same hardcoded install this plugin already targets.
                _dbg = new StreamWriter(@"G:\Tarkov\SPT-AKI\SPT-4.0.13\BepInEx\guildstash-debug.log", append: true);
                _dbg.AutoFlush = true;
                Dbg("==== plugin loaded " + DateTime.Now.ToString("HH:mm:ss") + " ====");
                Application.logMessageReceived += OnUnityLog;
                // FirstChanceException fires for EVERY thrown exception (any thread, even caught ones) the instant it's thrown - the only way to see an exception that hangs a background worker or gets swallowed.
                // Gated by Capture so it's silent during normal play (set true on claim).
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
            }
            catch (Exception ex)
            {
                Log.LogError("[GuildStash] could not open debug log: " + ex.Message);
            }

            CustomInteractionsManager.Register(new GuildStashInteractionsProvider());
            // Fetch the closed/open/base template registry so the menu knows which entry to show per item.
            GuildStashInteractionsProvider.LoadRegistry();
            Log.LogInfo("[GuildStash] Client interactions registered (Open / Close / Convert).");
        }

        // Our own trace line -> both BepInEx console and the auto-flushed file.
        internal static void Dbg(string message)
        {
            if (Log != null)
            {
                Log.LogInfo("[GuildStash] " + message);
            }
            if (_dbg != null)
            {
                _dbg.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message);
            }
        }

        // Every thrown exception (any thread), while a capture window is open.
        // Capped at 200 lines so a routine exception storm can't bloat the file.
        // Writes type + message + stack to the flushed log.
        private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            if (!Capture || _dbg == null)
            {
                return;
            }
            if (_captureCount >= 200)
            {
                return;
            }
            _captureCount++;
            var ex = e.Exception;
            _dbg.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  [FirstChance] " + ex.GetType().FullName + ": " + ex.Message);
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                _dbg.WriteLine(ex.StackTrace.TrimEnd());
            }
        }

        // Mirror the GAME's error/exception log lines into our flushed file - this is what captures the real stack trace from the operation-queue apply when the client hangs before Player.log flushes.
        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
            {
                return;
            }
            if (_dbg != null)
            {
                _dbg.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + type + "] " + condition);
                if (!string.IsNullOrEmpty(stackTrace))
                {
                    _dbg.WriteLine(stackTrace.TrimEnd());
                }
            }
        }
    }
}
