using System;
using System.Reflection;
using System.Threading;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Patches DXRandom's parameterless constructor, which vanilla seeds from
    /// ThreadId * Ticks (nondeterministic). In deterministic mode we re-seed it via the private
    /// init_genrand(uint) from a synced counter so every process draws the same sequence.
    ///
    /// Kept in its own patch class so a failure here can never disable the rest of
    /// DeterministicSandboxPatches (that exact mistake once took down all determinism patches).
    /// </summary>
    [HarmonyPatch]
    public static class DXRandomCtorPatch
    {
        private static MethodInfo _initGenrand;
        private static Action<DXRandom, uint> _fastInitGenrand;
        private static long _seedCounter;
        private static long _nonSimSeedCounter;

        private static void EnsureInitGenrand()
        {
            if (_initGenrand != null) return;
            _initGenrand = AccessTools.Method(typeof(DXRandom), "init_genrand", new[] { typeof(uint) });
            if (_initGenrand != null)
                _fastInitGenrand = Delegate.CreateDelegate(typeof(Action<DXRandom, uint>), _initGenrand, false) as Action<DXRandom, uint>;
        }

        /// <summary>Restart the deterministic seed sequence (per run / per level).</summary>
        public static void ResetCounter()
        {
            Interlocked.Exchange(ref _seedCounter, 0L);
            Interlocked.Exchange(ref _nonSimSeedCounter, 0L);
        }

        /// <summary>Anchors the synced simulation DXRandom construction counter to a specific sim tick.</summary>
        public static void ResetSimCounterForTick(int tick)
        {
            Interlocked.Exchange(ref _seedCounter, (long)(uint)tick << 16);
        }

        /// <summary>Re-seeds an existing DXRandom instance in-place with zero allocations when delegate binds.</summary>
        public static void ReseedInstance(DXRandom rng, uint seed)
        {
            if (rng == null) return;
            EnsureInitGenrand();
            if (_fastInitGenrand != null)
                _fastInitGenrand(rng, seed);
            else
                _initGenrand?.Invoke(rng, new object[] { seed });
        }

        /// <summary>
        /// Only the logic tick / physics step / level deserialization worker may consume the synced
        /// seed sequence. UI and render threads also construct DXRandom (menu particles, loader
        /// fades); letting their frame counts interleave with the logic stream shifted the seeds by
        /// a variable amount and produced the A/B position attractor. They get a private counter.
        /// </summary>
        private static bool IsSimConstruction()
        {
            if (LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
                return false;
            int main = LockstepClock.MainThreadId;
            bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;
            if (LockstepClock.InDeterministicContext
                || (LockstepClock.LogicTickActive && !onMain)
                || (LockstepClock.InPhysicsStep && !onMain)
                || (LockstepClock.InPhysicsStep && LockstepClock.PhysicsThreadId == Thread.CurrentThread.ManagedThreadId))
                return true;

            // Save deserialization runs on a pool thread while LoadingLevel is set; keep it synced so
            // load-time state that survives (e.g. DXLevel.Random) is identical across processes.
            return LockstepClock.LoadingLevel && !onMain;
        }

        public static MethodBase TargetMethod()
        {
            return AccessTools.Constructor(typeof(DXRandom), Type.EmptyTypes);
        }

        [HarmonyPrefix]
        public static bool Prefix(DXRandom __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            Interlocked.Increment(ref DeterministicSandboxPatches.DiagRngCtorCalls);

            EnsureInitGenrand();
            if (_initGenrand == null) return true;

            long n = IsSimConstruction()
                ? Interlocked.Increment(ref _seedCounter)
                : Interlocked.Increment(ref _nonSimSeedCounter);
            uint seed = unchecked((uint)(LockstepClock.SyncedRandomSeed + (uint)(n * 2654435761L)));
            try
            {
                if (_fastInitGenrand != null)
                    _fastInitGenrand(__instance, seed);
                else
                    _initGenrand.Invoke(__instance, new object[] { seed });
            }
            catch { return true; }
            return false;
        }
    }
}
