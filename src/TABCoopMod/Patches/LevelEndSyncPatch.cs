using System;
using System.Reflection;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Sync;

namespace TABCoopMod.Patches
{
    internal static class LevelEndSyncHelper
    {
        public static bool HandleTriggerGameEndPrefix(bool won)
        {
            if (!CoopSessionManager.IsSessionActive || !CoopSessionManager.IsInSyncedLevel || CoopSessionManager.Transport == null)
                return true;

            if (CoopSessionManager.Transport.IsHost)
            {
                CoopSessionManager.NotifyHostLevelEnded(won);
                return true;
            }

            if (CoopSessionManager.IsReceivingRemoteLevelEnd)
                return true;

            ModLogger.Info($"[LevelEndSyncPatch] Suppressed premature Client TriggerGameEnd(won={won}) while awaiting Host end state.");
            return false;
        }

        public static bool HandleShowGameEndPrefix(bool won, bool isIntroDelay, string source)
        {
            if (!isIntroDelay)
                return true;

            if (!CoopSessionManager.IsSessionActive || !CoopSessionManager.IsInSyncedLevel || CoopSessionManager.Transport == null)
                return true;

            if (CoopSessionManager.Transport.IsHost)
            {
                CoopSessionManager.NotifyHostLevelEnded(won);
                return true;
            }

            if (CoopSessionManager.IsReceivingRemoteLevelEnd)
                return true;

            ModLogger.Info($"[LevelEndSyncPatch] Suppressed premature Client {source}(won={won}) while awaiting Host end state.");
            return false;
        }
    }

    [HarmonyPatch]
    public static class TriggerGameEndSyncPatch
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeZXSystemGameLevel != null
                ? AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_TriggerGameEnd, new[] { typeof(bool) })
                : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(bool __0)
        {
            return LevelEndSyncHelper.HandleTriggerGameEndPrefix(__0);
        }
    }

    [HarmonyPatch]
    public static class ShowCampaignEndSyncPatch
    {
        public static MethodBase TargetMethod()
        {
            return GameBridge.TypeZXSystemGameLevel != null
                ? AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowCampaignEnd, new[] { typeof(bool), typeof(bool) })
                : null;
        }

        [HarmonyPrefix]
        public static bool Prefix(bool __0, bool __1)
        {
            return LevelEndSyncHelper.HandleShowGameEndPrefix(__0, __1, "ShowCampaignEnd");
        }
    }
}
