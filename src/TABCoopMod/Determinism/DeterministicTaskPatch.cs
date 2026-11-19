using System;
using System.Reflection;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Forces the position-affecting DXHelper_Task.For loops inside DXPhysicWorld.UpdateDefaultTime
    /// to run sequentially while the deterministic sandbox is active. Those loops apply forces and
    /// read back positions from multiple tasks in scheduler-dependent order — the only simulated state
    /// touched by parallelism once IsIADeterministic + IsMultiplayer are on. Patching
    /// UpdateDefaultTime itself is impossible: MonoMod refuses to rewrite its filtered-catch IL
    /// (HarmonyException), so we neutralize the parallelism at its source.
    ///
    /// Both physics loops are serialized (see DeterministicTaskPatch.Active). Everything else
    /// (DXScene.UpdateObjects culling, texture baking, level-table loading) keeps its native
    /// parallelism; serializing those made large saves (6666, 33k entities) take minutes.
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
        // Serialize every DXHelper_Task.For loop that runs on the physics thread, plus MapDrawer.
        //
        // Loop 2 (InPhysicsReadback: OnNewPositionComputed -> DXWorldGrid.SetNewCellFor) was already
        // serialized. Loop 1 (InPhysicsStep: StartUpdatePhysics) used to keep native multi-threading,
        // on the assumption that it "only writes per-entity Body forces/fields". That assumption is
        // wrong: DXHelper_Task.For splits the body range by minIterationsForConcurrency/maxCores, so
        // the partition — and therefore the order in which shared accumulators, broadphase inserts
        // and arbiter candidates are touched — depends on core count and on scheduler timing. Two
        // peers (or even two runs on the same machine) then integrate slightly different rounding
        // and drift apart. A/A testing on one binary, one save, one seed, with no input at all,
        // drifted in roughly half of runs until Loop 1 was serialized too.
        //
        // Cost: physics step time goes up (measured physFor1 ~9ms/t on a 17k-entity late save), which
        // is acceptable — correctness first. Only the physics thread is affected, so concurrent
        // render/culling tasks on MainThread keep their parallelism.
        //
        // Logic-tick serialization can be turned off with TABCOOP_SERIALIZE_LOGIC=0 if a player would
        // rather have the extra FPS than the extra safety; physics serialization is not optional.
        private static bool Active => LockstepClock.IsDeterministicModeActive
            && ((LockstepClock.InPhysicsReadback && LockstepClock.IsPhysicsThreadActive())
                || LockstepClock.InPhysicsStep && LockstepClock.IsPhysicsThreadActive()
                // The logic tick itself parallelizes entity work through the same helper. Any
                // order-sensitive accumulation or shared scratch buffer it touches makes the tick
                // depend on core count / scheduler timing, which is exactly the residual A/A drift.
                // Escape hatch: TABCOOP_SERIALIZE_LOGIC=0 restores native logic-tick parallelism
                // (faster on very large saves, but re-opens the drift window).
                || (Testing.TestHarnessConfig.SerializeLogic
                    && (LockstepClock.InDeterministicContext || LockstepClock.IsLogicWorkerActive()))
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
