using System;
using System.Reflection;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Captures a player's mayor pick as a MayorVoteOrSelect command (identified by the mayor's
    /// string ID, not the male/female random slot) so every peer applies the exact same mayor.
    ///
    /// Guards are strict: the engine applies initial mayors while restoring a save, so capture must
    /// stay completely inert during loading / before the level is live. (Hooking this method without
    /// guards previously crashed the process during level load.)
    /// </summary>
    [HarmonyPatch]
    public static class MayorSyncPatch
    {
        private static Type _mayorType;
        private static PropertyInfo _idProp;
        private static DXObject _activeMayorModal;
        private static int _lastDispatchedMayorLevel;
        private static int _lastDispatchedMayorTick = -1;
        private static int _remoteMayorCommittedForCount = -1;

        internal static void SetActiveMayorWindow(DXObject modal)
        {
            _activeMayorModal = modal;
            ModLogger.Info($"[MayorSync] Tracked open mayor selection modal (inModal={DXModalScreen.IsInModal}).");
        }

        internal static void NotifyRemoteMayorCommitted(int currentMayorCount)
        {
            _remoteMayorCommittedForCount = currentMayorCount;
        }

        public static void CloseActiveMayorWindow()
        {
            var modal = _activeMayorModal;
            _activeMayorModal = null;
            if (modal == null || modal.Scene == null) return;

            if (!GameBridge.CloseZXWindow(modal))
            {
                try { DXModalScreen.RemoveModal(modal); }
                catch { }
            }
            ModLogger.Info("[MayorSync] Closed peer's open mayor selection modal after selection.");
        }

        internal static void ResetTracking()
        {
            _activeMayorModal = null;
            _lastDispatchedMayorLevel = 0;
            _lastDispatchedMayorTick = -1;
            _remoteMayorCommittedForCount = -1;
        }

        internal static bool IsRemoteMayorCommittedForCurrentCount()
        {
            if (_remoteMayorCommittedForCount < 0) return false;
            int cur = GameBridge.GetLevelCollectionCount("MayorStates");
            if (cur > _remoteMayorCommittedForCount)
            {
                _remoteMayorCommittedForCount = -1;
                return false;
            }
            return true;
        }

        public static MethodBase TargetMethod()
        {
            _mayorType = GameBridge.GameAssembly?.GetType("ZX.ZXMayor");
            if (_mayorType == null) return null;
            return AccessTools.Method(_mayorType, ObfuscatedSymbols.Method_ZXMayor_Apply, new[] { GameBridge.LevelStateType });
        }

        [HarmonyPrefix]
        public static bool Prefix(object __instance)
        {
            if (TABCoopMod.Sync.CoopSessionManager.IsGameplayInputLocked)
                return false;

            if (!CommandInterceptor.IsInterceptionEnabled
                || CommandExecutor.IsReplayingCommand
                || LockstepClock.LoadingLevel
                || LockstepClock.IsSimulationThreadActive
                || DXGame.Current == null
                || DXGame.Current.IAIterationInt <= 0)
                return true;

            if (__instance == null) return true;

            if (_idProp == null)
                _idProp = _mayorType?.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
            string id = null;
            try { id = _idProp?.GetValue(__instance) as string; } catch { }
            if (string.IsNullOrEmpty(id)) return true;

            int mayorLevel = GameBridge.GetMayorLevel(__instance);
            int currentCount = GameBridge.GetLevelCollectionCount("MayorStates");
            int curTick = DXGame.Current.IAIterationInt;

            if ((mayorLevel > 0 && currentCount >= mayorLevel)
                || (mayorLevel > 0 && _lastDispatchedMayorLevel == mayorLevel && curTick >= _lastDispatchedMayorTick && curTick - _lastDispatchedMayorTick < 30))
            {
                ModLogger.Info($"[MayorSync] Suppressed duplicate local mayor click '{id}' (level={mayorLevel}, currentCount={currentCount}).");
                CloseActiveMayorWindow();
                return false;
            }

            _lastDispatchedMayorLevel = mayorLevel;
            _lastDispatchedMayorTick = curTick;
            _activeMayorModal = null;

            CommandInterceptor.Dispatch(new NetCommand
            {
                Type = NetCommandType.MayorVoteOrSelect,
                PlayerId = CommandInterceptor.LocalPlayerId,
                TargetSimTick = curTick,
                MayorId = id
            });
            return false;
        }
    }

    /// <summary>
    /// Tracks the local mayor selection ZXWindow opened by ZXGame.ShowMayorSelection so that
    /// when a peer's MayorVoteOrSelect executes first, any still-open mayor popup is closed.
    /// Also suppresses stale delayed popups once IsChoosingMayor has been cleared by the peer's pick.
    /// </summary>
    [HarmonyPatch]
    public static class MayorModalTrackerPatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ShowMayorSelection, Type.EmptyTypes);

        [HarmonyPrefix]
        public static bool Prefix(out bool __state)
        {
            __state = !DXModalScreen.IsInModal;
            if (CommandInterceptor.IsInterceptionEnabled
                && GameBridge.CurrentLevelState != null
                && (!GameBridge.IsChoosingMayor() || MayorSyncPatch.IsRemoteMayorCommittedForCurrentCount()))
            {
                ModLogger.Info("[MayorSync] Suppressed stale ShowMayorSelection popup (already chosen or committed).");
                return false;
            }
            if (__state && LockstepClock.IsDeterministicModeActive)
            {
                int mayorLevel = Math.Min(4, Math.Max(1, GameBridge.GetLevelCollectionCount("MayorStates") + 1));
                LockstepClock.ReseedLevelRandomForMayorSelection(mayorLevel);
            }
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (__state && LockstepClock.IsDeterministicModeActive)
            {
                LockstepClock.ReanchorLevelRandomToCurrentTick();
            }
            if (__state && DXModalScreen.IsInModal && GameBridge.IsChoosingMayor())
            {
                var modal = DXModalScreen.CurrentModalObject;
                if (GameBridge.IsZXWindow(modal))
                {
                    MayorSyncPatch.SetActiveMayorWindow(modal);
                }
            }
        }
    }
}
