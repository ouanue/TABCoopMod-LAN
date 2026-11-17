using System;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Forces the position-affecting DXHelper_Task.For loops inside DXPhysicWorld.UpdateDefaultTime
    /// to run sequentially while the deterministic sandbox is active. Those two loops apply forces and
    /// read back positions from multiple tasks in scheduler-dependent order — the only simulated state
    /// touched by parallelism once IsIADeterministic + IsMultiplayer are on. Patching
    /// UpdateDefaultTime itself is impossible: MonoMod refuses to rewrite its filtered-catch IL
    /// (HarmonyException), so we neutralize the parallelism at its source.
    ///
    /// Everything else (DXScene.UpdateObjects culling, texture baking, level-table loading) keeps its
    /// native parallelism; serializing those made large saves (6666, 33k entities) take minutes.
    /// </summary>
    /// <summary>
    /// Brackets ZX.GameSystems.ZXMapDrawer.#=zl2XxwLg=(DXLevel) so level/map RNG streams are
    /// seeded identically and DXHelper_Task.For runs sequentially during terrain/tree/grass generation.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicMapDrawerPatch
    {
        public static System.Reflection.MethodBase TargetMethod()
        {
            var type = GameBridge.GameAssembly?.GetType("ZX.GameSystems.ZXMapDrawer");
            return type != null ? AccessTools.Method(type, "#=zl2XxwLg=", new[] { typeof(DXLevel) }) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(DXLevel __0, out bool __state)
        {
            __state = LockstepClock.InMapDrawer;
            LockstepClock.InMapDrawer = true;
            if (LockstepClock.IsDeterministicModeActive)
            {
                if (__0 != null)
                    __0.Random = new DXRandom(LockstepClock.EffectiveLoadSeed);
                LockstepClock.ResetSyncedRandomStreamsForInitLevel();
            }
        }

        [HarmonyFinalizer]
        public static void Finalizer(bool __state)
        {
            LockstepClock.InMapDrawer = __state;
        }
    }

    [HarmonyPatch]
    public static class DeterministicTaskPatch
    {
        // Serialize physics position readback (Loop 2: OnNewPositionComputed -> DXWorldGrid.SetNewCellFor)
        // on the physics thread only (so concurrent render tasks on MainThread are never serialized),
        // and serialize MapDrawer generation loops. Physics Loop 1 (StartUpdatePhysics) only writes
        // per-entity Body forces/fields and keeps native DXHelper_Task.For multi-threading across cores.
        private static bool Active => LockstepClock.IsDeterministicModeActive
            && ((LockstepClock.InPhysicsReadback && LockstepClock.IsPhysicsThreadActive())
                || LockstepClock.InMapDrawer);

        [HarmonyPatch]
        public static class ForIndex
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int> __2)
            {
                if (!Active) return true;
                for (int i = __0; i < __1; i++) __2(i);
                return false;
            }
        }

        [HarmonyPatch]
        public static class ForRange
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int, int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int, int> __2)
            {
                if (!Active) return true;
                if (!LockstepClock.InPhysicsStep)
                {
                    __2(__0, __1);
                    return false;
                }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                __2(__0, __1);
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                if (LockstepClock.InPhysicsReadback)
                    DeterministicSandboxPatches.DiagPhysFor2Ticks += dt;
                else
                    DeterministicSandboxPatches.DiagPhysFor1Ticks += dt;
                return false;
            }
        }

        [HarmonyPatch]
        public static class ForRangeWithThread
        {
            public static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(DXHelper_Task), "For",
                    new[] { typeof(int), typeof(int), typeof(Action<int, int, int>), typeof(int), typeof(int) });

            [HarmonyPrefix]
            public static bool Prefix(int __0, int __1, Action<int, int, int> __2)
            {
                if (!Active) return true;
                __2(__0, __1, 0);
                return false;
            }
        }
    }
}
