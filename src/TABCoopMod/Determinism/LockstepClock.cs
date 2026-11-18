using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Determinism
{
    /// <summary>
    /// Controls deterministic simulation stepping, GUID bifurcation (Synced Entity IDs vs Local UI IDs),
    /// and deterministic Mersenne Twister (DXRandom) seeding.
    /// </summary>
    public static class LockstepClock
    {
        public const double MsPerLogicTick = 33.333333333333336; // 30 Hz IA Update
        public const double MsPerPhysicsTick = 83.33333333333333; // 12 Hz Physics Update

        /// <summary>
        /// True when a multiplayer or deterministic replay session is active.
        /// </summary>
        public static bool IsDeterministicModeActive { get; private set; }

        /// <summary>
        /// True while the main thread is executing inside a synchronized logic tick or replaying a synced command.
        /// Used by DXGuid.New() patch to separate gameplay entity IDs from local UI object IDs.
        /// </summary>
        [ThreadStatic]
        public static bool InDeterministicContext;

        /// <summary>
        /// Cross-thread flag set while a logic tick is executing. The engine runs entity updates
        /// inside a Task on a worker thread, so [ThreadStatic] InDeterministicContext cannot reach
        /// DXGuid.New()/DXRandom there; this global flag does. Safe because the main thread blocks
        /// in WaitForGameLogic until the tick finishes.
        /// </summary>
        public static volatile bool LogicTickActive;

        /// <summary>
        /// True while a save/level is being loaded. During load, DXRandom.Global draws must use the
        /// seeded shared instance. NOTE: DXGuid is deliberately NOT routed through this flag — doing
        /// so gave load-time temp/UI objects synced low IDs, polluting the entity set with a
        /// process-varying count. Load-time GUIDs stay local (high bit) and excluded from hashing.
        /// </summary>
        public static volatile bool LoadingLevel;

        /// <summary>
        /// Controls whether the fixed-step clock is allowed to advance. Default: always ready
        /// (single-process deterministic replay). Network lockstep swaps in a gated implementation.
        /// </summary>
        public static ITickGate TickGate { get; set; } = new AlwaysReadyTickGate();

        public static int CurrentSimTick => DXGame.Current?.IAIterationInt ?? 0;

        private static long _syncedEntityIdCounter = 100000L;
        private static long _localUiIdCounter = 1L;
        private static int _syncedRandomSeed = 1337;
        public static int EffectiveLoadSeed => HasPendingPostLoadCalibration ? PendingLoadSeed : _syncedRandomSeed;
        private static DXRandom _syncedGlobalRandom;
        private static DXRandom _auxGlobalRandom;
        private static DXRandom _renderGlobalRandom;

        /// <summary>True while DXGame.UpdatePhysics() is on the stack (physics callbacks run outside LogicTickActive).</summary>
        public static volatile bool InPhysicsContext;

        /// <summary>Managed thread ID executing UpdatePhysics() (used to keep async physics off MainThread render RNG/GUIDs).</summary>
        public static volatile int PhysicsThreadId;

        internal static bool IsPhysicsThreadActive()
        {
            if (!InPhysicsContext) return false;
            int pt = PhysicsThreadId;
            int cur = Thread.CurrentThread.ManagedThreadId;
            return pt == 0 || cur == pt || (pt != MainThreadId && cur != MainThreadId);
        }

        /// <summary>
        /// True whenever the calling thread is currently executing deterministic simulation work
        /// (sync/replay MainThread context, async _TaskLogic worker thread, or physics step thread).
        /// Command interceptors and CBehaviour UI isolation use this so worker-thread AI/weapon ticks
        /// are never misidentified as local player UI clicks.
        /// </summary>
        public static bool IsSimulationThreadActive
        {
            get
            {
                if (InDeterministicContext) return true;
                int main = MainThreadId;
                bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;
                return (LogicTickActive && !onMain) || IsPhysicsThreadActive();
            }
        }

        /// <summary>
        /// Only true on frames where UpdateGameLogic_Prefix allowed a logic tick to advance.
        /// Prevents UpdatePhysics() from stepping ahead during MainMenu, LoadingLevel, or PausedBySession.
        /// </summary>
        public static bool AllowPhysicsThisFrame { get; set; }

        /// <summary>
        /// Same span as InPhysicsContext, but also gates DeterministicTaskPatch.
        /// </summary>
        public static volatile bool InPhysicsStep;

        /// <summary>
        /// True only after DXPhysicWorld.Step() completes (inside OnFinishPhysics..UpdatePhysics_Exit)
        /// during Loop 2 (OnNewPositionComputed), which mutates shared DXWorldGrid cells and must
        /// run sequentially. Loop 1 (StartUpdatePhysics) only writes per-entity Body forces/fields and
        /// keeps native DXHelper_Task.For multi-threading.
        /// </summary>
        public static volatile bool InPhysicsReadback;

        /// <summary>
        /// True while ZXMapDrawer.#=z0Pkwp_k=(DXLevel) runs terrain/tree/grass generation during
        /// InitLevel. Serializing DXHelper_Task.For inside this window prevents worker threads from
        /// consuming DXRandom streams in nondeterministic order.
        /// </summary>
        public static volatile bool InMapDrawer;

        /// <summary>
        /// True while executing our deterministic end-of-physics CSprite pull-animation pass so
        /// CSprite.#=zykGuoNdyOv10tsMpy8FzNkE=() runs for every pullable entity rather than only
        /// those inside the local camera viewport during render-time UpdateSceneObjectParallel.
        /// </summary>
        public static volatile bool AllowCSpritePullUpdate;

        /// <summary>
        /// Managed thread id of the render/frame thread (captured from DXGame.StartFrame). Used to
        /// tell the level-loading worker thread apart from UI/render threads that also construct
        /// DXRandom while a save is deserializing.
        /// </summary>
        public static int MainThreadId { get; private set; }

        [ThreadStatic]
        private static bool _allowRawGameTimeSet;

        /// <summary>
        /// True only while LockstepClock explicitly updates DXGame.GameTime for a simulation step.
        /// Prevents DXGame._TaskGameTime background thread from mutating GameTime/GameTimeInt
        /// mid-tick or mid-physics.
        /// </summary>
        internal static bool AllowRawGameTimeSet
        {
            get => _allowRawGameTimeSet;
            set => _allowRawGameTimeSet = value;
        }

        internal static void SetDeterministicGameTime(DXGame game, double value)
        {
            if (game == null) return;
            _allowRawGameTimeSet = true;
            try { game.GameTime = value; }
            finally { _allowRawGameTimeSet = false; }
        }

        /// <summary>
        /// True while the game is paused by an explicit synchronized pause command. The engine's
        /// save loader sets DXGame.Paused=true for its own bookkeeping and never clears it, so the
        /// fixed-step clock must key off THIS flag rather than DXGame.Paused.
        /// </summary>
        public static bool PausedBySession { get; private set; }

        public static void SetSessionPause(bool paused)
        {
            PausedBySession = paused;
            if (paused) ResetHostTickPacing();
        }

        // Real-time monotonic pacing for live Host co-op play (offline replay remains unthrottled).
        private static double _nextHostRealTickMs;

        public static void ResetHostTickPacing() => _nextHostRealTickMs = 0.0;

        /// <summary>
        /// Advances the Host monotonic real-time accumulator and returns how many fixed 30Hz ticks
        /// should execute this frame (clamped to [0, maxTicks]). Multi-tick catchup prevents low
        /// render FPS (e.g. dual-window GPU sharing or unfocused background window) from slowing
        /// simulation below 1.0x real time and also supports >1x game speeds cleanly.
        /// </summary>
        internal static int ConsumeHostRealTimeTicks(double gameSpeed, int maxTicks = 3, double paceMultiplier = 1.0)
        {
            if (gameSpeed <= 0.001)
            {
                _nextHostRealTickMs = 0.0;
                return 0;
            }

            double clampedPace = paceMultiplier > 0.1 ? paceMultiplier : 1.0;
            double stepMs = (MsPerLogicTick * clampedPace) / gameSpeed;
            double nowMs = System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            // Clamp stale backlog to <= 2 ticks so a temporary GC/physics/render spike cannot
            // trap Host in an 8-tick catchup death spiral (4 FPS).
            if (_nextHostRealTickMs <= 0.0 || nowMs - _nextHostRealTickMs > Math.Max(120.0, stepMs * 3.0))
            {
                _nextHostRealTickMs = _nextHostRealTickMs <= 0.0 ? nowMs : nowMs - stepMs * 1.5;
            }

            int count = 0;
            // 1.5ms tolerance prevents ~30-31Hz frame pacing jitter from skipping alternate frames.
            while (count < maxTicks && nowMs + 1.5 >= _nextHostRealTickMs)
            {
                count++;
                _nextHostRealTickMs += stepMs;
            }
            return count;
        }

        internal static void RefundHostRealTimeTicks(double gameSpeed, int unrunTicks)
        {
            if (unrunTicks <= 0 || gameSpeed <= 0.001 || _nextHostRealTickMs <= 0.0)
                return;
            double stepMs = MsPerLogicTick / gameSpeed;
            _nextHostRealTickMs -= stepMs * unrunTicks;
        }

        /// <summary>Executes an action under InDeterministicContext on the calling thread.</summary>
        public static void RunInDeterministicContext(Action action)
        {
            bool prev = InDeterministicContext;
            InDeterministicContext = true;
            try
            {
                EnsureBehaviourRandomOnCurrentThread();
                action?.Invoke();
            }
            finally { InDeterministicContext = prev; }
        }

        [ThreadStatic] private static int _audioRngDepth;
        [ThreadStatic] private static int _visualVfxDepth;

        /// <summary>
        /// True while executing inside ZXSystemAudio synchronous playback/selection on this thread.
        /// Audio depends on local camera frustum and singleton caches, so its PRNG draws must
        /// never consume the shared synced or scene streams.
        /// </summary>
        public static bool InAudioContext => _audioRngDepth > 0;

        /// <summary>
        /// True while executing inside the pure-visual VFX spawner (#=zg10wm6mM9hhx) on this thread.
        /// Several VFX helpers check CheckFastObjectVisibility() / IsPixelAreaVisible / VisibleCells
        /// (which depend on local camera viewport), yet draw DXRandom.Global and allocate bare DXEntities.
        /// </summary>
        public static bool InVisualVfxContext => _visualVfxDepth > 0;

        public static void EnterAudioContext() => _audioRngDepth++;

        public static void ExitAudioContext()
        {
            if (_audioRngDepth > 0) _audioRngDepth--;
        }

        public static void EnterVisualVfxContext() => _visualVfxDepth++;

        public static void ExitVisualVfxContext()
        {
            if (_visualVfxDepth > 0) _visualVfxDepth--;
        }

        public static bool ShouldUseSyncedSceneRandom()
        {
            if (InAudioContext || InVisualVfxContext) return false;
            bool onMain = Thread.CurrentThread.ManagedThreadId == MainThreadId;
            return InDeterministicContext
                || IsPhysicsThreadActive()
                || (LogicTickActive && !onMain)
                || (LoadingLevel && !onMain);
        }

        /// <summary>
        /// Seed stashed by the session/verifier before LoadSaveFileAndEnter. The counter must not be
        /// calibrated until the async loader finishes (see OnLevelLoadComplete), because at load start
        /// DXGame.Current.Entities still holds the OLD level and the save's GameTime is not restored.
        /// </summary>
        public static int PendingLoadSeed { get; private set; } = 1337;
        public static bool HasPendingPostLoadCalibration { get; private set; }

        /// <summary>Host's synced entity-id counter at snapshot time; applied to the Client after load.</summary>
        public static long PendingSnapshotEntityIdCounter { get; private set; }

        public static long CurrentSyncedEntityIdCounter => _syncedEntityIdCounter;

        /// <summary>Adopt the Host's entity-id counter captured in the snapshot header.</summary>
        public static void SetSyncedEntityIdCounter(long value)
        {
            if (value > 0) _syncedEntityIdCounter = value;
        }

        /// <summary>Record the current (render/frame) thread for load-vs-UI thread classification.</summary>
        public static void CaptureMainThread() => MainThreadId = Thread.CurrentThread.ManagedThreadId;

        public static void EnableDeterministicSandbox(int initialSeed, long startingEntityIdCounter = 100000L)
        {
            _syncedRandomSeed = initialSeed;
            _syncedEntityIdCounter = startingEntityIdCounter;
            _syncedGlobalRandom = new DXRandom(initialSeed);
            _auxGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x5EEDBEEF));
            _renderGlobalRandom = new DXRandom(unchecked(initialSeed ^ 0x1234ABCD));
            _syncedSceneRandom = new DXRandom(unchecked(initialSeed ^ 0x517CC1B7));
            _lastReanchoredSimTick = -1;

            // The parameterless-ctor seed counter must restart together with the shared RNG, otherwise
            // a run that allocated a different number of DXRandom instances before syncing starts
            // would get a permanently shifted seed sequence.
            DXRandomCtorPatch.ResetCounter();

            // Keep vanilla CMovable on the FindPathConcurrent branch instead of its buggy
            // IsMultiplayer branch (hardcodes cellSize=1 and recurses infinitely on null paths);
            // DeterministicPathfindingPatch executes FindPathConcurrent synchronously and preserves cellSize.
            SetEngineMultiplayerMode(false);

            // Set IsIADeterministic AFTER SetEngineMultiplayerMode(false), because SetPlayerMode
            // overwrites DXGame.IsIADeterministic = (mode == Multiplayer)!
            DXGame.IsIADeterministic = true;
            AllowPhysicsThisFrame = false;
            IsDeterministicModeActive = true;

            // Unload background async physics/time threads if they were already running
            DXGame.Current?.UnloadPhysics();

            ModLogger.Info($"[LockstepClock] Deterministic sandbox ENABLED (Seed={initialSeed}, StartEntityId={startingEntityIdCounter}, IsIADeterministic={DXGame.IsIADeterministic}).");
        }

        public static void DisableDeterministicSandbox()
        {
            IsDeterministicModeActive = false;
            InDeterministicContext = false;
            LogicTickActive = false;
            InPhysicsContext = false;
            PhysicsThreadId = 0;
            InPhysicsStep = false;
            InPhysicsReadback = false;
            AllowPhysicsThisFrame = false;
            _lastReanchoredSimTick = -1;
            DeterministicSandboxPatches.ResetAsyncTickState();
            LoadingLevel = false;
            HasPendingPostLoadCalibration = false;
            PendingSnapshotEntityIdCounter = 0;
            PausedBySession = false;
            TickGate = new AlwaysReadyTickGate();
            DXGame.IsIADeterministic = false;
            SetEngineMultiplayerMode(false);
            ModLogger.Info("[LockstepClock] Deterministic sandbox DISABLED.");
        }

        private static MethodInfo _setPlayerModeMethod;

        /// <summary>
        /// Switches ZXGame's ZXPlayerModeType. Multiplayer makes CMovable compute paths synchronously
        /// on the logic thread (DXWorldGrid.FindPath) instead of the nondeterministic background queue.
        /// </summary>
        public static void SetEngineMultiplayerMode(bool multiplayer)
        {
            try
            {
                if (_setPlayerModeMethod == null)
                {
                    _setPlayerModeMethod = AccessTools.Method(GameBridge.TypeZXGame,
                        ObfuscatedSymbols.Method_ZXGame_SetPlayerMode);
                }
                if (_setPlayerModeMethod == null) return;

                var paramType = _setPlayerModeMethod.GetParameters()[0].ParameterType;
                object mode = Enum.ToObject(paramType, multiplayer ? 1 : 0);
                _setPlayerModeMethod.Invoke(null, new[] { mode });
                ModLogger.Info($"[LockstepClock] ZXPlayerModeType set to {(multiplayer ? "Multiplayer" : "Singleplayer")}.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] SetEngineMultiplayerMode failed: {ex.Message}");
            }
        }

        public static ulong NextSyncedGuid()
        {
            long next = Interlocked.Increment(ref _syncedEntityIdCounter);
            return (ulong)next;
        }

        public static ulong NextLocalUiGuid()
        {
            long next = Interlocked.Increment(ref _localUiIdCounter);
            return 0x8000000000000000UL | (ulong)next;
        }

        public static DXRandom GetSyncedGlobalRandom()
        {
            if (_syncedGlobalRandom == null)
            {
                _syncedGlobalRandom = new DXRandom(_syncedRandomSeed);
            }
            return _syncedGlobalRandom;
        }

        /// <summary>
        /// Fixed-seed RNG for Global draws made OUTSIDE the logic tick (physics callbacks, scene
        /// updates). DXRandom.Global is [ThreadStatic] and seeded from new Random(), so without this
        /// those draws are nondeterministic between processes. Kept separate from the logic stream so
        /// render-frame counts can never shift the simulation's random sequence.
        /// </summary>
        public static DXRandom GetAuxGlobalRandom()
        {
            if (_auxGlobalRandom == null)
            {
                _auxGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x5EEDBEEF));
            }
            return _auxGlobalRandom;
        }

        /// <summary>
        /// Separate fixed-seed stream for pure rendering-phase draws. Keeping it apart from the
        /// physics stream means frame/viewport-dependent visual randomness can never shift the
        /// random sequence consumed by deterministic simulation callbacks.
        /// </summary>
        public static DXRandom GetRenderGlobalRandom()
        {
            if (_renderGlobalRandom == null)
            {
                _renderGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x1234ABCD));
            }
            return _renderGlobalRandom;
        }

        private static DXRandom _syncedSceneRandom = new DXRandom(0x517CC1B7);

        /// <summary>
        /// Dedicated synced RNG backing DXScene.GlobalRandom (and DXLinq.RandomElement / DXHelper_Random)
        /// during deterministic simulation and level loading, keeping it isolated from render-thread draws.
        /// </summary>
        public static DXRandom GetSyncedSceneRandom()
        {
            if (_syncedSceneRandom == null)
            {
                _syncedSceneRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x517CC1B7));
            }
            return _syncedSceneRandom;
        }

        public static int SyncedRandomSeed => _syncedRandomSeed;

        private static int _behaviourResetGeneration;
        [ThreadStatic] private static int _behaviourResetGenerationOnThread;
        private static readonly Dictionary<Type, DXRandom> _sharedBehaviourRandoms = new Dictionary<Type, DXRandom>();
        private static DXRandom[] _sharedBehaviourRandomArray = Array.Empty<DXRandom>();
        private static readonly object _behaviourSync = new object();
        private static int _lastReanchoredSimTick = -1;
        [ThreadStatic] private static ulong _currentBehaviourEntityId;
        [ThreadStatic] private static uint _currentBehaviourRngCounter;

        public static void EnterEntityBehaviourRngScope(ulong entityId)
        {
            _currentBehaviourEntityId = entityId;
            _currentBehaviourRngCounter = 0u;
        }

        public static void ExitEntityBehaviourRngScope()
        {
            _currentBehaviourEntityId = 0UL;
        }

        public static bool TryNextEntityBehaviourRandomUInt(out uint value)
        {
            ulong entityId = _currentBehaviourEntityId;
            if (entityId == 0UL || InAudioContext || InVisualVfxContext || LoadingLevel)
            {
                value = 0u;
                return false;
            }

            uint drawIdx = ++_currentBehaviourRngCounter;
            uint tick = (uint)(DXGame.Current?.IAIterationInt ?? 0);
            ulong z = entityId
                + ((ulong)unchecked((uint)_syncedRandomSeed) << 32)
                + (ulong)tick * 0x9E3779B97F4A7C15UL
                + (ulong)drawIdx * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z = z ^ (z >> 31);
            value = (uint)(z >> 32);
            return true;
        }

        private static uint MixTickSeed(int baseSeed, int tick, uint streamSalt)
        {
            uint h = unchecked((uint)baseSeed) ^ unchecked((uint)tick * 0x9E3779B9u) ^ streamSalt;
            h ^= h >> 16;
            h *= 0x85EBCA6Bu;
            h ^= h >> 13;
            h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h != 0u ? h : 1u;
        }

        /// <summary>
        /// Deterministically re-anchors the shared simulation PRNG instances and construction counter
        /// at the start of each simulation tick in O(1), isolating local 1-tick combat/projectile
        /// variance from propagating into map-wide global RNG draws. Behaviour RNG draws inside
        /// ZXBehaviour.Run() are further isolated per entity ID via ZXBehaviourRunRngScopePatch.
        /// </summary>
        public static void ReanchorSimulationRngForTick(int tick)
        {
            if (!IsDeterministicModeActive || LoadingLevel || tick < 0 || _lastReanchoredSimTick == tick)
                return;
            _lastReanchoredSimTick = tick;

            DXRandomCtorPatch.ResetSimCounterForTick(tick);

            int baseSeed = _syncedRandomSeed;
            DXRandomCtorPatch.ReseedInstance(GetSyncedGlobalRandom(), MixTickSeed(baseSeed, tick, 0x9E3779B9u));
            DXRandomCtorPatch.ReseedInstance(GetAuxGlobalRandom(), MixTickSeed(baseSeed, tick, 0x5EEDBEEFu));
            DXRandomCtorPatch.ReseedInstance(GetSyncedSceneRandom(), MixTickSeed(baseSeed, tick, 0x517CC1B7u));

            var level = DXLevel.Current;
            if (level?.Random != null)
                DXRandomCtorPatch.ReseedInstance(level.Random, MixTickSeed(baseSeed, tick, 0x243F6A88u));
        }

        /// <summary>
        /// Deterministically seeds DXLevel.Current.Random for ZXGame.ShowMayorSelection from
        /// (SyncedRandomSeed, mayorLevel) so every peer rolls identical male/female mayor choices
        /// regardless of wall-clock ExecuteWithDelay(6000) or modal-retry tick skew.
        /// </summary>
        public static void ReseedLevelRandomForMayorSelection(int mayorLevel)
        {
            var level = DXLevel.Current;
            if (!IsDeterministicModeActive || level?.Random == null) return;
            uint seed = MixTickSeed(_syncedRandomSeed, mayorLevel, 0x4D415952u);
            DXRandomCtorPatch.ReseedInstance(level.Random, seed);
            ModLogger.Info($"[MayorSync] Seeded DXLevel.Random deterministically for Mayor level={mayorLevel} (baseSeed={_syncedRandomSeed}, drawSeed=0x{seed:X8}).");
        }

        /// <summary>Re-anchors DXLevel.Current.Random back to the current simulation tick stream.</summary>
        public static void ReanchorLevelRandomToCurrentTick()
        {
            var level = DXLevel.Current;
            if (!IsDeterministicModeActive || level?.Random == null) return;
            int tick = DXGame.Current?.IAIterationInt ?? 0;
            DXRandomCtorPatch.ReseedInstance(level.Random, MixTickSeed(_syncedRandomSeed, tick, 0x243F6A88u));
        }

        /// <summary>
        /// DXBehaviour&lt;TData&gt;._GlobalRandom is [ThreadStatic], and DXGame.UpdateGameLogic runs each
        /// tick's _TaskLogic on an arbitrary ThreadPool worker thread. Giving each worker its own new
        /// DXRandom(seed) would restart or interleave per-thread sequences whenever ThreadPool picks a
        /// different worker across ticks/processes. Bind every worker thread to the SAME per-type shared
        /// DXRandom instance (safe because IsIADeterministic runs _TaskLogic sequentially under WaitForGameLogic).
        /// </summary>
        public static void EnsureBehaviourRandomOnCurrentThread()
        {
            int gen = Volatile.Read(ref _behaviourResetGeneration);
            if (_behaviourResetGenerationOnThread == gen) return;
            _behaviourResetGenerationOnThread = gen;

            try
            {
                lock (_behaviourSync)
                {
                    if (_sharedBehaviourRandoms.Count == 0) return;
                    foreach (var kv in _sharedBehaviourRandoms)
                    {
                        try
                        {
                            AccessTools.Field(kv.Key, "_Seed")?.SetValue(null, _syncedRandomSeed);
                            AccessTools.Field(kv.Key, "_GlobalRandom")?.SetValue(null, kv.Value);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] EnsureBehaviourRandomOnCurrentThread failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Resets the shared deterministic DXRandom instance for every closed DXBehaviour&lt;TData&gt;
        /// generic type and bumps the generation counter so the active _TaskLogic thread rebinds to it.
        /// </summary>
        public static void ResetBehaviourRandom(int seed)
        {
            try
            {
                var asm = Core.GameBridge.GameAssembly;
                if (asm == null) return;

                Type open = typeof(DXBehaviour<>);
                Type baseData = typeof(DXBehaviourData);
                int resetCount = 0;

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }

                lock (_behaviourSync)
                {
                    _sharedBehaviourRandoms.Clear();
                    int idx = 0;
                    foreach (var t in types)
                    {
                        if (t == null || t.IsAbstract || t.IsGenericTypeDefinition || !baseData.IsAssignableFrom(t))
                            continue;
                        try
                        {
                            Type closed = open.MakeGenericType(t);
                            var shared = new DXRandom(unchecked(seed ^ (idx * 0x45D9F3B)));
                            _sharedBehaviourRandoms[closed] = shared;
                            AccessTools.Field(closed, "_Seed")?.SetValue(null, seed);
                            AccessTools.Field(closed, "_GlobalRandom")?.SetValue(null, shared);
                            idx++;
                            resetCount++;
                        }
                        catch { /* closed generic not constructible for this T — skip */ }
                    }
                    var arr = new DXRandom[_sharedBehaviourRandoms.Count];
                    _sharedBehaviourRandoms.Values.CopyTo(arr, 0);
                    Volatile.Write(ref _sharedBehaviourRandomArray, arr);
                }

                ModLogger.Info($"[LockstepClock] Reset DXBehaviour<*>.GlobalRandom (types={resetCount}, seed={seed}).");
                Interlocked.Increment(ref _behaviourResetGeneration);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] ResetBehaviourRandom failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans all currently loaded entities in the level and sets _syncedEntityIdCounter
        /// strictly above the maximum existing non-UI entity ID so newly spawned entities never collide.
        /// </summary>
        public static void CalibrateEntityIdCounterFromLoadedLevel(int seed)
        {
            long maxId = 100000L;
            if (DXGame.Current?.Entities != null)
            {
                foreach (var ent in DXGame.Current.Entities)
                {
                    if (ent != null && (ent.ID & 0x8000000000000000UL) == 0)
                    {
                        if ((long)ent.ID > maxId)
                            maxId = (long)ent.ID;
                    }
                }
            }
            // Stay clear of long.MaxValue: saved IDs are already close to 2^63-1 on late saves.
            long start = maxId > long.MaxValue - 8192L ? long.MaxValue - 8192L : maxId + 1000L;
            EnableDeterministicSandbox(seed, start);
        }

        /// <summary>
        /// Resets shared RNG streams right at the start of ZXSystemGameLevel.InitLevel so
        /// MapDrawer terrain generation always sees the exact same deterministic sequence regardless
        /// of draws made during ReadSaveFile.
        /// </summary>
        public static void ResetSyncedRandomStreamsForInitLevel()
        {
            _syncedGlobalRandom = new DXRandom(_syncedRandomSeed);
            _auxGlobalRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x5EEDBEEF));
            _syncedSceneRandom = new DXRandom(unchecked(_syncedRandomSeed ^ 0x517CC1B7));
            _lastReanchoredSimTick = -1;
            DXRandomCtorPatch.ResetCounter();
        }

        /// <summary>
        /// Called before LoadSaveFileAndEnter. The actual calibration is deferred to the level-load
        /// completion postfix, when the async loader has restored every entity and GameTime.
        /// </summary>
        public static void RequestPostLoadCalibration(int seed, long snapshotEntityIdCounter = 0, bool markLoading = true)
        {
            PendingLoadSeed = seed;
            PendingSnapshotEntityIdCounter = snapshotEntityIdCounter;
            HasPendingPostLoadCalibration = true;
            if (markLoading) LoadingLevel = true;
            AllowPhysicsThisFrame = false;
            DXGame.LastGameTimeIAUpdate = 0.0;
            DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0;
        }

        /// <summary>
        /// Applies the full deterministic baseline to a loaded level (synced entity ID counter,
        /// sorted entity/physics lists, seeded RNGs, cleared path cache, aligned logic/physics
        /// clocks, normalized CMovable speeds & body velocity/force, and frozen camera intro).
        /// Called symmetrically on both Host and Client.
        /// </summary>
        public static void ApplyDeterministicLevelBaseline(int seed, long snapshotCounter = 0)
        {
            CalibrateEntityIdCounterFromLoadedLevel(seed);

            // A Client adopts the Host's snapshot-time counter so entities spawned on either side
            // after the snapshot share one ID space. Only move forward, never rewind below loaded IDs.
            if (snapshotCounter > _syncedEntityIdCounter)
                _syncedEntityIdCounter = snapshotCounter;
            RenderVisualInterpolationPatch.Reset();
            LoadingLevel = false;

            GameBridge.SortEntitiesById();
            ResetColonistSystem();
            NormalizeMovablesAndPhysicsState();
            GameBridge.SeedLevelRandom(seed);
            ResetBehaviourRandom(seed);
            _lastReanchoredSimTick = -1;
            DXWorldGrid.ResetPathsCached();
            GameBridge.AlignSimulationClocks();
            // The 6s opening camera pan is wall-clock driven; on two processes it lands on
            // different frames, changing which cells are "watched" (and thus visibility-driven
            // RNG/sprite work). Freeze it to the save's camera for a reproducible world.
            GameBridge.DisableCameraIntroAnimation();
        }

        /// <summary>
        /// Resets SysColonists' round-robin building cursor and rebuilds its building list in sorted
        /// entity order so wandering colonists pick identical destinations across Host and Client.
        /// </summary>
        private static void ResetColonistSystem()
        {
            try
            {
                var tSys = Core.GameBridge.GameAssembly?.GetType(Core.ObfuscatedSymbols.Type_SysColonists);
                if (tSys == null) return;
                object inst = AccessTools.Method(tSys, Core.ObfuscatedSymbols.Method_SysColonists_GetCurrent)?.Invoke(null, null);
                if (inst == null) return;

                var fCursor = AccessTools.Field(tSys, Core.ObfuscatedSymbols.Field_SysColonists_Cursor);
                int prevCursor = fCursor != null ? (int)fCursor.GetValue(inst) : -1;
                fCursor?.SetValue(inst, 0);
                AccessTools.Method(tSys, Core.ObfuscatedSymbols.Method_SysColonists_RebuildList)?.Invoke(inst, null);

                int count = 0;
                var list = AccessTools.Field(tSys, Core.ObfuscatedSymbols.Field_SysColonists_List)?.GetValue(inst) as System.Collections.IList;
                if (list != null) count = list.Count;
                ModLogger.Info($"[LockstepClock] Reset SysColonists (prevCursor={prevCursor}, buildings={count}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] ResetColonistSystem failed: {ex.Message}");
            }
        }

        private static bool _movableMetaInit;
        private static Type _typeCMovable;
        private static FieldInfo _fMovableCachedSpeed;
        private static MethodInfo _mMovableComputeSpeed;

        private static void NormalizeMovablesAndPhysicsState()
        {
            try
            {
                var game = DXGame.Current;
                if (game?.Entities == null) return;

                if (!_movableMetaInit)
                {
                    _movableMetaInit = true;
                    _typeCMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
                    if (_typeCMovable != null)
                    {
                        _fMovableCachedSpeed = _typeCMovable.GetField("#=zLnAE$gO8EOG2",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        _mMovableComputeSpeed = AccessTools.Method(_typeCMovable, "#=zRwhr6_cVbBr_", Type.EmptyTypes);
                    }
                }

                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;

                    if (_typeCMovable != null && _fMovableCachedSpeed != null && _mMovableComputeSpeed != null && e.HasComponents)
                    {
                        var comps = e.Components;
                        if (comps != null)
                        {
                            for (int c = 0; c < comps.Count; c++)
                            {
                                var comp = comps[c];
                                if (comp != null && _typeCMovable.IsInstanceOfType(comp))
                                {
                                    float spd = (float)_mMovableComputeSpeed.Invoke(comp, null);
                                    _fMovableCachedSpeed.SetValue(comp, spd);
                                    break;
                                }
                            }
                        }
                    }

                    var body = e.Body;
                    if (body != null && body.Handle != IntPtr.Zero && e.PhysicalType != DXBodyType.Static)
                    {
                        body.PhysicVelocity = PointF.Empty;
                        body.PhysicForce = PointF.Empty;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[LockstepClock] NormalizeMovablesAndPhysicsState failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs on the main thread from ZXSystemGameLevel.#=zwK_pUIeRBpbp Postfix — the first moment
        /// the loaded level is fully restored. Calibrates synced entity IDs, sorts update order,
        /// seeds level RNG and zero-phases the logic/physics clocks.
        /// </summary>
        public static void OnLevelLoadComplete()
        {
            if (!IsDeterministicModeActive)
            {
                LoadingLevel = false;
                return;
            }

            try
            {
                int seed = HasPendingPostLoadCalibration ? PendingLoadSeed : _syncedRandomSeed;
                long snapshotCounter = PendingSnapshotEntityIdCounter;
                HasPendingPostLoadCalibration = false;
                PendingSnapshotEntityIdCounter = 0;

                if (Sync.CoopSessionManager.IsSessionActive)
                {
                    bool isHost = Sync.CoopSessionManager.Transport != null && Sync.CoopSessionManager.Transport.IsHost;
                    if (isHost)
                    {
                        if (!Sync.CoopSessionManager.IsHostSnapshotReloadPending)
                        {
                            // Initial Host load before exporting coop_host_snapshot.zxsav: calibrate ID counter
                            // and suppress the 6s intro zoom/blur/HUD slide so the exported snapshot retains
                            // the normal starting camera and never leaks intro state into the snapshot reload.
                            CalibrateEntityIdCounterFromLoadedLevel(seed);
                            GameBridge.DisableCameraIntroAnimation();
                            LoadingLevel = false;
                            PausedBySession = true;
                            var initHostGame = DXGame.Current;
                            if (initHostGame != null)
                            {
                                if (initHostGame.Paused) initHostGame.Paused = false;
                                bool keepLocked = Sync.CoopSessionManager.IsSessionActive;
                                if (keepLocked)
                                {
                                    initHostGame.InputEnabled = false;
                                }
                                else if (!initHostGame.InputEnabled)
                                {
                                    initHostGame.InputEnabled = true;
                                    ModLogger.Info("[LockstepClock] Restored InputEnabled=true after initial Host level load.");
                                }
                            }
                            GameBridge.ClearWaitCursor();
                            GameBridge.ClearLoadingFadeOverlay();
                            ModLogger.Info($"[LockstepClock] Host initial level load complete: counter={_syncedEntityIdCounter}, holding paused for snapshot export. | {GameBridge.GetHudDiagSummary()}");
                            return;
                        }

                        ApplyDeterministicLevelBaseline(seed, snapshotCounter);
                        PausedBySession = true;
                        var hostGame = DXGame.Current;
                        if (hostGame != null)
                        {
                            if (hostGame.Paused) hostGame.Paused = false;
                            bool keepLocked = Sync.CoopSessionManager.IsSessionActive;
                            if (keepLocked)
                            {
                                hostGame.InputEnabled = false;
                            }
                            else if (!hostGame.InputEnabled)
                            {
                                hostGame.InputEnabled = true;
                                ModLogger.Info("[LockstepClock] Restored InputEnabled=true after Host snapshot reload (intro-zoom bypass).");
                            }
                        }
                        GameBridge.ClearWaitCursor();
                        GameBridge.ClearLoadingFadeOverlay();
                        ModLogger.Info($"[LockstepClock] Host snapshot reload complete: baseline applied (seed={seed}, counter={_syncedEntityIdCounter}). | {GameBridge.GetHudDiagSummary()}");
                        return;
                    }

                    // Client: apply baseline and keep paused until Host sends SessionStart.
                    ApplyDeterministicLevelBaseline(seed, snapshotCounter);
                    PausedBySession = true;
                    var clientGame = DXGame.Current;
                    if (clientGame != null)
                    {
                        if (clientGame.Paused)
                        {
                            clientGame.Paused = false;
                            ModLogger.Info("[LockstepClock] Cleared load-time pause on Client (session remains paused until SessionStart).");
                        }
                        bool keepLocked = Sync.CoopSessionManager.IsSessionActive;
                        if (keepLocked)
                        {
                            clientGame.InputEnabled = false;
                        }
                        else if (!clientGame.InputEnabled)
                        {
                            clientGame.InputEnabled = true;
                            ModLogger.Info("[LockstepClock] Restored InputEnabled=true on Client after snapshot load.");
                        }
                    }
                    GameBridge.ClearWaitCursor();
                    GameBridge.ClearLoadingFadeOverlay();
                    ModLogger.Info($"[LockstepClock] Client level load complete: ID counter -> {_syncedEntityIdCounter}, seed={seed}, waiting for SessionStart.");
                    return;
                }

                ApplyDeterministicLevelBaseline(seed, snapshotCounter);

                // The engine's save loader leaves DXGame.Paused = true (only the "Continue" button
                // clears it). Our fixed-step clock honors Paused by freezing, so a programmatic load
                // would never tick. Restore the running state here.
                PausedBySession = false;
                var game = DXGame.Current;
                if (game != null)
                {
                    if (game.Paused)
                    {
                        game.Paused = false;
                        ModLogger.Info("[LockstepClock] Cleared load-time pause at level completion.");
                    }
                    if (!game.InputEnabled) game.InputEnabled = true;
                }
                GameBridge.ClearWaitCursor();
                GameBridge.ClearLoadingFadeOverlay();

                ModLogger.Info($"[LockstepClock] Level load complete: ID counter -> {_syncedEntityIdCounter}, seed={seed}, clocks aligned.");
            }
            catch (Exception ex)
            {
                LoadingLevel = false;
                ModLogger.Error("[LockstepClock] OnLevelLoadComplete failed", ex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float QuantizeScalar(float value, double scale, double invScale)
        {
            double rounded = Math.Round((double)value * scale, MidpointRounding.ToEven) * invScale;
            float q = (float)rounded;
            return q == 0f ? 0f : q;
        }

        internal static float QuantizeCoord(float value)
        {
            int scaleInt = Testing.TestHarnessConfig.PhysicsQuantScale;
            if (scaleInt <= 0 || !IsFinite(value)) return value;
            double scale = scaleInt;
            return QuantizeScalar(value, scale, 1.0 / scale);
        }

        /// <summary>
        /// Quantizes active dynamic bodies' positions and velocities to a fixed binary grid (default 1/256 cell)
        /// at the end of each physics step so Chipmunk floating-point mantissa tail differences cannot accumulate
        /// across steps or trigger cpBBTree / broadphase boundary splits between peers.
        /// </summary>
        internal static void QuantizePhysicsBodiesPostStep()
        {
            int scaleInt = Testing.TestHarnessConfig.PhysicsQuantScale;
            if (scaleInt <= 0) return;

            var bodies = DXPhysicWorld.Bodies;
            if (bodies == null || bodies.Count == 0) return;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double scale = scaleInt;
            double invScale = 1.0 / scale;
            int nCells = DXWorldGrid.NCells;
            float maxCoord = nCells > 1 ? nCells - (float)invScale : float.MaxValue;
            int modified = 0;

            int dynTotal = 0;

            lock (DXGame.SyncLogic)
            {
                int count = bodies.Count;
                for (int i = 0; i < count; i++)
                {
                    DXPhysicBody body = bodies[i];
                    if (body == null || body.Handle == IntPtr.Zero) continue;

                    DXEntity ent = body.GameEntity;
                    if (ent == null || ent.Disposed) continue;
                    if (ent.PhysicalType != DXBodyType.Dynamic) continue;
                    if ((ent.ID & 0x8000000000000000UL) != 0UL) continue;
                    dynTotal++;

                    PointF rawPhysPos = body.PhysicPosition;
                    bool posValid = IsFinite(rawPhysPos.X) && IsFinite(rawPhysPos.Y);
                    PointF rawPhysVel = body.PhysicVelocity;
                    bool velValid = IsFinite(rawPhysVel.X) && IsFinite(rawPhysVel.Y);

                    bool anyChanged = false;

                    if (posValid)
                    {
                        float qx = QuantizeScalar(rawPhysPos.X, scale, invScale);
                        float qy = QuantizeScalar(rawPhysPos.Y, scale, invScale);
                        if (nCells > 1)
                        {
                            if (rawPhysPos.X >= 0f && qx < 0f) qx = 0f;
                            else if (rawPhysPos.X < nCells && qx >= nCells) qx = maxCoord;
                            if (rawPhysPos.Y >= 0f && qy < 0f) qy = 0f;
                            else if (rawPhysPos.Y < nCells && qy >= nCells) qy = maxCoord;
                        }
                        var qPos = new PointF(qx, qy);

                        if (rawPhysPos.X != qx || rawPhysPos.Y != qy)
                        {
                            body.PhysicPosition = qPos;
                            anyChanged = true;
                        }

                        // Keep LastPositionComputed / ent.LastPosition untouched when the body moved this step
                        // so RenderVisualInterpolationPatch continues interpolating cleanly; only sync Last*
                        // when snapping an already-stationary body onto the grid.
                        bool wasStationary = body.LastPositionComputed == body.PositionComputed && body.PositionComputed == rawPhysPos;
                        if (body.PositionComputed.X != qx || body.PositionComputed.Y != qy)
                        {
                            body.PositionComputed = qPos;
                            anyChanged = true;
                        }
                        if (wasStationary && (body.LastPositionComputed.X != qx || body.LastPositionComputed.Y != qy))
                        {
                            body.LastPositionComputed = qPos;
                        }

                        PointF entPos = ent.Position;
                        if (entPos.X != qx || entPos.Y != qy)
                        {
                            ent.Position = qPos;
                            anyChanged = true;
                        }
                        if (wasStationary && (ent.LastPosition.X != qx || ent.LastPosition.Y != qy))
                        {
                            ent.LastPosition = qPos;
                        }
                    }

                    if (velValid)
                    {
                        float qvx = QuantizeScalar(rawPhysVel.X, scale, invScale);
                        float qvy = QuantizeScalar(rawPhysVel.Y, scale, invScale);
                        var qVel = new PointF(qvx, qvy);

                        if (rawPhysVel.X != qvx || rawPhysVel.Y != qvy)
                        {
                            body.PhysicVelocity = qVel;
                            anyChanged = true;
                        }
                        if (body.Velocity.X != qvx || body.Velocity.Y != qvy)
                        {
                            body.Velocity = qVel;
                            anyChanged = true;
                        }
                    }

                    if (anyChanged) modified++;
                }
            }

            DeterministicSandboxPatches.DiagPhysQuantTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            DeterministicSandboxPatches.DiagPhysQuantModified += modified;
            DeterministicSandboxPatches.DiagPhysQuantDynTotal += dynTotal;
        }
    }

    /// <summary>
    /// Patches DXGuid.New(), DXRandom.Global, and DXGame.UpdateGameLogic / LoadPhysics
    /// to enforce deterministic execution when LockstepClock.IsDeterministicModeActive is true.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicSandboxPatches
    {
        // 1. Patch DXGuid.New() for dual-track ID generation
        [HarmonyPatch(typeof(DXGuid), nameof(DXGuid.New))]
        [HarmonyPrefix]
        public static bool DXGuid_New_Prefix(ref ulong __result)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return true;
            System.Threading.Interlocked.Increment(ref DiagGuidCalls);

            // During (async) save loading or pure-visual VFX particle creation, generated IDs belong
            // to local/transient objects and must never consume the shared synced ID counter.
            if (LockstepClock.InVisualVfxContext || LockstepClock.LoadingLevel)
            {
                __result = LockstepClock.NextLocalUiGuid();
                return false;
            }

            int main = LockstepClock.MainThreadId;
            bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;
            if (LockstepClock.InDeterministicContext
                || (LockstepClock.LogicTickActive && !onMain)
                || LockstepClock.IsPhysicsThreadActive())
            {
                __result = LockstepClock.NextSyncedGuid();
                return false;
            }

            // Only isolate render-thread transient IDs into the 0x8000... UI range once a synced
            // level or deterministic replay is actually running. Offline/pre-sync map generation
            // (e.g., Host starting a New Survival Game after clicking Co-op Host in the menu) must
            // produce normal non-masked entity IDs so StateHasher, ControlServer, and replays track them.
            if (Sync.CoopSessionManager.IsInSyncedLevel || Testing.DeterministicReplayVerifier.IsRunning)
            {
                __result = LockstepClock.NextLocalUiGuid();
                return false;
            }

            return true;
        }

        // 2. Patch DXRandom.Global getter to return deterministic PRNG in deterministic context
        [HarmonyPatch(typeof(DXRandom), nameof(DXRandom.Global), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool DXRandom_Global_Prefix(ref DXRandom __result)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return true;
            System.Threading.Interlocked.Increment(ref DiagRngGlobalCalls);

            // DXRandom.Global is [ThreadStatic] and seeded from new Random()/real ticks, so it must
            // never be handed out while the sandbox is active. Only the logic worker and the level-
            // loading worker may draw from the shared synced stream: the main/render thread runs
            // menu animations and loader fades whose frame counts differ between processes, and
            // feeding those into the synced stream shifted every later draw (the A/B attractor).
            int main = LockstepClock.MainThreadId;
            bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;

            if (LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
                __result = LockstepClock.GetRenderGlobalRandom();
            else if (LockstepClock.IsPhysicsThreadActive())
                __result = LockstepClock.GetAuxGlobalRandom();
            else if (LockstepClock.InDeterministicContext || (LockstepClock.LogicTickActive && !onMain)
                || (LockstepClock.LoadingLevel && !onMain))
                __result = LockstepClock.GetSyncedGlobalRandom();
            else
                __result = LockstepClock.GetRenderGlobalRandom();
            return false;
        }

        private static readonly MethodInfo _mUpdateGameLogic = AccessTools.Method(typeof(DXGame), "UpdateGameLogic");
        private static readonly MethodInfo _mUpdatePhysics = AccessTools.Method(typeof(DXGame), "UpdatePhysics");
        private static readonly MethodInfo _mWaitForGameLogic = AccessTools.Method(typeof(DXGame), "WaitForGameLogic");
        private static readonly FieldInfo _fTaskLogic = AccessTools.Field(typeof(DXGame), "_TaskLogic");
        [ThreadStatic] private static bool _inSingleTickStep;

        private static bool _asyncTickInFlight;
        internal static bool IsAsyncTickInFlight => _asyncTickInFlight;
        private static volatile bool _asyncPhysicsChained;
        private static volatile bool _asyncPhysicsStepped;
        private static bool _inFinalizeAsyncTick;
        private static long _asyncLaunchTs;
        private static long _asyncWaitStartTs;
        private static double _diagAsyncWaitMs;
        private static double _diagWorkerLogicMs;
        private static readonly object _deferredAsyncLock = new object();
        private static readonly List<Action> _deferredAsyncPostMethods = new List<Action>();

        internal static void ResetAsyncTickState()
        {
            _asyncTickInFlight = false;
            _asyncPhysicsChained = false;
            _asyncPhysicsStepped = false;
            _inFinalizeAsyncTick = false;
            _asyncLaunchTs = 0;
            _asyncWaitStartTs = 0;
            _diagAsyncWaitMs = 0.0;
            _diagWorkerLogicMs = 0.0;
            lock (_deferredAsyncLock) { _deferredAsyncPostMethods.Clear(); }
            RenderVisualInterpolationPatch.Reset();
        }

        internal static void EnqueueDeferredAsyncPostMethod(Action action)
        {
            if (action == null) return;
            lock (_deferredAsyncLock) { _deferredAsyncPostMethods.Add(action); }
        }

        internal static void FlushPendingAsyncTick(DXGame game)
        {
            if (!_asyncTickInFlight || game == null) return;
            _mWaitForGameLogic?.Invoke(game, null);
        }

        internal static double DiagLastFrameDtMs { get; private set; }
        internal static double DiagAvgFrameMs { get; private set; }
        internal static double DiagAvgTickLogicMs { get; private set; }
        internal static double DiagAvgPhysicsMs { get; private set; }
        internal static int DiagLastWantedTicks { get; private set; }
        internal static int DiagLastRanTicks { get; private set; }
        private static double _diagLastEntryMs;

        // Detailed deterministic hotspot telemetry (accumulated per 60-tick window)
        internal static long DiagTickMovableSyncTicks;
        internal static long DiagTickMovableSyncCalls;
        internal static long DiagTickPullSyncTicks;
        internal static long DiagTickPullSyncCalls;
        internal static long DiagTickPathfindTicks;
        internal static long DiagTickPathfindCalls;
        internal static long DiagTickHashTicks;
        internal static long DiagTickPostDrainTicks;
        internal static long DiagPhysFor1Ticks;
        internal static long DiagPhysFor2Ticks;
        internal static long DiagPhysQuantTicks;
        internal static long DiagPhysQuantModified;
        internal static long DiagPhysQuantDynTotal;
        internal static long DiagRngGlobalCalls;
        internal static long DiagRngGenCalls;
        internal static long DiagRngCtorCalls;
        internal static long DiagGuidCalls;
        internal static long DiagPostWrapCalls;
        internal static int DiagTickWindowCount;
        internal static int DiagPhysStepWindowCount;
        private static readonly double _swTickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // 3. Fixed-step clock: drive GameTime in exact 33.333..ms increments and only advance a
        //    tick when the active ITickGate allows it. Vanilla's UpdateGameLogic runs a Task and
        //    loops `while (GameTime >= LastGameTimeIAUpdate + c)`. When render FPS drops below 30Hz
        //    (e.g. two windows side-by-side) or GameSpeed > 1x, outer UpdateGameLogic_Prefix steps
        //    up to 8 sequential single-tick passes per frame, running physics + turn flush + hashing
        //    + PostMethods between every tick to preserve bit-identical interleaving.
        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPrefix]
        public static bool UpdateGameLogic_Prefix(DXGame __instance)
        {
            if (!_inSingleTickStep)
                RenderVisualInterpolationPatch.ExitVisualSceneUpdate(__instance);

            if (!LockstepClock.IsDeterministicModeActive) return true;

            if (_inSingleTickStep)
            {
                int tickIdx = __instance.IAIterationInt;
                if (Sync.CoopSessionManager.IsInSyncedLevel)
                {
                    Sync.CoopSessionManager.OnSimulationTickStarting(tickIdx);
                    Sync.CoopSessionManager.FlushDueTurnCommands(tickIdx);
                }

                if (LockstepClock.PausedBySession)
                {
                    LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                    LockstepClock.AllowPhysicsThisFrame = false;
                    return false;
                }

                LockstepClock.LogicTickActive = true;
                LockstepClock.InDeterministicContext = true;
                LockstepClock.AllowPhysicsThisFrame = true;
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick);
                return true;
            }

            // Defensive flush if UpdateGameLogic is ever reached while a prior async tick is in flight.
            if (_asyncTickInFlight)
                _mWaitForGameLogic?.Invoke(__instance, null);

            // Never touch GameTime or run logic during MainMenu or async level loading:
            // MainMenu ticks would advance LastGameTimeIAUpdate to wall-clock ~15s and later
            // clobber the save's restored GameTime while ReadSaveFile/InitLevel runs on T9/T11.
            if (LockstepClock.LoadingLevel || DXScene.Current == null || !(DXScene.Current.RootObject is DXIsometricGridObject))
            {
                LockstepClock.ResetHostTickPacing();
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            // Background windows still need Visible=true so OnPreRender keeps drawing both windows
            // side-by-side, but NEVER force Focused=true: DXGame.ProcessInputEvents relies on
            // Focused to ignore global mouse/keyboard polling on the unfocused window so two
            // instances on the same PC do not mirror local camera/selection/hotkeys.
            if (!__instance.Visible) __instance.Visible = true;

            if (DXGame.LastGameTimeIAUpdate == 0.0)
                DXGame.LastGameTimeIAUpdate = __instance.GameTime;

            bool inCoop = Sync.CoopSessionManager.IsInSyncedLevel;
            if (inCoop)
            {
                if (__instance.CursorLocked && !Core.GameBridge.IsGameLoading())
                    Core.GameBridge.ClearWaitCursor();

                // Poll transport and flush outgoing Client inputs right before deciding this frame's ticks
                // so packets arriving during RenderFrame are handled without waiting another full render frame.
                Sync.CoopSessionManager.PumpNetworkBeforeLogic();
            }

            double swFreqMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double nowEntryMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
            if (_diagLastEntryMs > 0.0)
            {
                double dt = nowEntryMs - _diagLastEntryMs;
                DiagLastFrameDtMs = dt;
                DiagAvgFrameMs = DiagAvgFrameMs <= 0.0 ? dt : DiagAvgFrameMs * 0.85 + dt * 0.15;
            }
            _diagLastEntryMs = nowEntryMs;

            if (LockstepClock.PausedBySession)
            {
                if (inCoop)
                {
                    int pausedTick = __instance.IAIterationInt;
                    if (LockstepClock.TickGate == null || LockstepClock.TickGate.IsTickReady(pausedTick))
                        Sync.CoopSessionManager.FlushDueTurnCommands(pausedTick);
                }
                LockstepClock.ResetHostTickPacing();
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            if (Testing.DeterministicReplayVerifier.IsRunning
                && __instance.Paused
                && !DXVision.GUI.DXModalScreen.IsInModal
                && !Core.GameBridge.IsLevelEnded())
            {
                // EnterGameView queues an InvokeOnStartFrame action that sets DXGame.Paused=true
                // on the frame after OnLevelLoadComplete clears it; keep replay runs unpaused.
                __instance.Paused = false;
            }

            if (__instance.Paused || !Core.GameBridge.IsCurrentLevelSystemEnabled() || Core.GameBridge.IsLevelEnded())
            {
                LockstepClock.ResetHostTickPacing();
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            int maxTicksThisFrame = 1;
            bool isHost = inCoop && Sync.CoopSessionManager.Transport != null && Sync.CoopSessionManager.Transport.IsHost;
            if (inCoop)
            {
                if (isHost)
                {
                    Sync.CoopSessionManager.GetHostPaceAdjustment(out double hostPaceMult, out int hostMaxTicks);
                    if (hostMaxTicks <= 0)
                    {
                        LockstepClock.ResetHostTickPacing();
                        maxTicksThisFrame = 0;
                    }
                    else
                    {
                        maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(__instance.GameSpeed, hostMaxTicks, hostPaceMult);
                    }
                }
                else
                {
                    int curTick = __instance.IAIterationInt;
                    int readyAhead = (LockstepClock.TickGate is TurnInputTickGate netGate)
                        ? (netGate.ReadyThrough - curTick)
                        : 1;
                    if (readyAhead < 0)
                    {
                        maxTicksThisFrame = 0;
                    }
                    else if (readyAhead <= Sync.CoopSessionManager.InputDelayTicks)
                    {
                        // Pace Client smoothly at 30Hz * GameSpeed so it doesn't burn the entire InputDelayTicks
                        // cushion in a single frame and then stall for the next 2 frames.
                        maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(__instance.GameSpeed, 1);
                    }
                    else
                    {
                        // Client fell behind Host by more than InputDelayTicks: run multi-tick catchup so
                        // sub-30 FPS clients cannot drift seconds behind Host.
                        int behind = readyAhead - Sync.CoopSessionManager.InputDelayTicks;
                        int catchup = behind >= 16 ? 3 : (behind >= 6 ? 2 : 1);
                        maxTicksThisFrame = LockstepClock.ConsumeHostRealTimeTicks(__instance.GameSpeed, catchup);
                        if (behind >= 3)
                        {
                            maxTicksThisFrame = Math.Max(maxTicksThisFrame, Math.Min(catchup, behind));
                            LockstepClock.ResetHostTickPacing();
                        }
                        else if (maxTicksThisFrame < 1)
                        {
                            maxTicksThisFrame = 1;
                        }
                    }
                }
            }
            else
            {
                LockstepClock.ResetHostTickPacing();
            }

            if (maxTicksThisFrame <= 0)
            {
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                return false;
            }

            DiagLastWantedTicks = maxTicksThisFrame;

            // [LAN desync fix] Default the async tick overlap OFF in live co-op. With async overlap,
            // physics runs on the _TaskLogic continuation thread while RenderFrame/Present runs on
            // MainThread; per-machine thread scheduling makes moving zombie positions drift linearly
            // (the ~2-minute Auto-resync / MissingOnClient loop). Single-process replay already forces
            // this off (DeterministicReplayVerifier.IsRunning), which is why replay PASSes while live
            // co-op drifts. Running the tick synchronously keeps physics on the main thread and matches
            // the replay-deterministic path, at the cost of capping the host to ~30fps instead of
            // ~100fps. Set TABCOOP_ENABLE_ASYNC_TICK=1 to re-enable the old overlap behavior.
            bool canOverlapAsync = inCoop
                && Testing.TestHarnessConfig.EnableAsyncTick
                && !Testing.DeterministicReplayVerifier.IsRunning
                && __instance.GameSpeed <= 1.05
                && maxTicksThisFrame == 1;

            if (canOverlapAsync)
            {
                int asyncNextTick = __instance.IAIterationInt;
                Sync.CoopSessionManager.OnSimulationTickStarting(asyncNextTick);
                if (LockstepClock.TickGate == null || LockstepClock.TickGate.IsTickReady(asyncNextTick))
                {
                    Sync.CoopSessionManager.FlushDueTurnCommands(asyncNextTick);
                    if (!LockstepClock.PausedBySession && !(__instance.IAIterationInt != asyncNextTick))
                    {
                        if (maxTicksThisFrame > 1)
                            LockstepClock.RefundHostRealTimeTicks(__instance.GameSpeed, maxTicksThisFrame - 1);
                        _asyncTickInFlight = true;
                        _asyncPhysicsChained = false;
                        _asyncPhysicsStepped = false;
                        _asyncLaunchTs = System.Diagnostics.Stopwatch.GetTimestamp();
                        LockstepClock.LogicTickActive = true;
                        LockstepClock.InDeterministicContext = false;
                        LockstepClock.AllowPhysicsThisFrame = false;
                        LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate + LockstepClock.MsPerLogicTick);
                        DiagLastRanTicks = 1;
                        return true;
                    }
                }

                LockstepClock.RefundHostRealTimeTicks(__instance.GameSpeed, maxTicksThisFrame);
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);
                LockstepClock.AllowPhysicsThisFrame = false;
                DiagLastRanTicks = 0;
                return false;
            }

            int ticksRan = 0;
            // After running at least 1 tick, allow a wider budget when catching up multiple ticks
            // (especially late-game Client catchup where 1 tick alone takes >10ms).
            double frameSimBudgetMs = (!isHost && maxTicksThisFrame > 1)
                ? 28.0
                : (maxTicksThisFrame > 1 ? 18.0 : 10.0);
            for (int step = 0; step < maxTicksThisFrame; step++)
            {
                if (LockstepClock.PausedBySession) break;
                if (ticksRan >= 1)
                {
                    double simElapsedMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs - nowEntryMs;
                    if (simElapsedMs >= frameSimBudgetMs) break;
                }

                int nextTick = __instance.IAIterationInt;
                if (LockstepClock.TickGate != null && !LockstepClock.TickGate.IsTickReady(nextTick))
                    break;

                if (_mUpdateGameLogic == null) break;

                // Mirror vanilla 1-tick-per-frame PostMethods boundaries between consecutive ticks
                // so deferred AddToScene/RemoveFromScene/spawn callbacks never execute at differing
                // tick boundaries on Host vs Client when catchup runs multiple ticks per frame.
                if (ticksRan > 0)
                {
                    long tDrainStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool prevRendering = __instance.RenderingFrame;
                    __instance.RenderingFrame = false;
                    try
                    {
                        __instance.PostMethods_OnFinishFrame?.ExecuteAll();
                        __instance.PostMethods_OnSafeLogicAndDrawing?.ExecuteAll();
                        __instance.PostMethods_OnStartFrame?.ExecuteAll();
                    }
                    finally
                    {
                        __instance.RenderingFrame = prevRendering;
                    }
                    DiagTickPostDrainTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tDrainStart;
                }

                double tLogicStart = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
                _inSingleTickStep = true;
                try
                {
                    _mUpdateGameLogic.Invoke(__instance, null);
                }
                finally
                {
                    _inSingleTickStep = false;
                }
                double tLogicEnd = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
                double lMs = tLogicEnd - tLogicStart;
                DiagAvgTickLogicMs = DiagAvgTickLogicMs <= 0.0 ? lMs : DiagAvgTickLogicMs * 0.9 + lMs * 0.1;

                // Honor a SetPaused(true) command flushed right before/during this tick.
                if (LockstepClock.PausedBySession && ticksRan == 0 && __instance.IAIterationInt == nextTick)
                    break;

                ticksRan++;

                if (DXGame.IsIADeterministic && _mUpdatePhysics != null)
                {
                    double tPhysStart = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
                    LockstepClock.AllowPhysicsThisFrame = true;
                    _mUpdatePhysics.Invoke(__instance, null);
                    double pMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs - tPhysStart;
                    if (pMs > 0.2)
                    {
                        DiagAvgPhysicsMs = DiagAvgPhysicsMs <= 0.0 ? pMs : DiagAvgPhysicsMs * 0.9 + pMs * 0.1;
                        DiagPhysStepWindowCount++;
                    }
                }

                if (inCoop)
                {
                    long tHashStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Sync.CoopSessionManager.OnSimulationTickCompleted(__instance.IAIterationInt);
                    DiagTickHashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tHashStart;
                }

                DiagTickWindowCount++;
                if (DiagTickWindowCount >= 60)
                    LogPerfWindow(__instance, ticksRan);
            }

            DiagLastRanTicks = ticksRan;
            if (ticksRan > 0)
                RenderVisualInterpolationPatch.OnLogicTickCompleted(ticksRan);
            if (isHost && !LockstepClock.PausedBySession && ticksRan < maxTicksThisFrame)
                LockstepClock.RefundHostRealTimeTicks(__instance.GameSpeed, maxTicksThisFrame - ticksRan);

            if (ticksRan == 0)
                LockstepClock.SetDeterministicGameTime(__instance, DXGame.LastGameTimeIAUpdate);

            // Physics was already stepped deterministically after each individual tick above.
            LockstepClock.AllowPhysicsThisFrame = false;
            return false;
        }

        private static void LogPerfWindow(DXGame game, int ranThisFrame)
        {
            int tc = DiagTickWindowCount;
            double movMs = DiagTickMovableSyncTicks * _swTickToMs / tc;
            double pullMs = DiagTickPullSyncTicks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
            double pathMs = DiagTickPathfindTicks * _swTickToMs / tc;
            double hashMs = DiagTickHashTicks * _swTickToMs / tc;
            double drainMs = DiagTickPostDrainTicks * _swTickToMs / tc;
            double pFor1Ms = DiagPhysFor1Ticks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
            double pFor2Ms = DiagPhysFor2Ticks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
            double pQuantMs = DiagPhysQuantTicks * _swTickToMs / Math.Max(1, DiagPhysStepWindowCount);
            long pQuantMod = DiagPhysQuantModified / Math.Max(1, DiagPhysStepWindowCount);
            long pQuantDyn = DiagPhysQuantDynTotal / Math.Max(1, DiagPhysStepWindowCount);
            if (Testing.TestHarnessConfig.Enabled)
            {
                ModLogger.Info($"[Perf] tick={game.IAIterationInt} frame={DiagAvgFrameMs:F1}ms logic={DiagAvgTickLogicMs:F1}ms(wait={_diagAsyncWaitMs:F1}ms) phys={DiagAvgPhysicsMs:F1}ms(x{DiagPhysStepWindowCount}/60t) " +
                    $"| movSync={movMs:F2}ms({DiagTickMovableSyncCalls / tc}/t) pullSync={pullMs:F2}ms({DiagTickPullSyncCalls / Math.Max(1, DiagPhysStepWindowCount)}/p) " +
                    $"physFor1={pFor1Ms:F2}ms physFor2={pFor2Ms:F2}ms quant={pQuantMs:F2}ms({pQuantMod}/{pQuantDyn}) path={pathMs:F2}ms({DiagTickPathfindCalls / tc}/t) coopTick={hashMs:F2}ms drain={drainMs:F2}ms " +
                    $"| calls/t: rngG={DiagRngGlobalCalls / tc} gen={DiagRngGenCalls / tc} ctor={DiagRngCtorCalls / tc} guid={DiagGuidCalls / tc} post={DiagPostWrapCalls / tc} want={DiagLastWantedTicks} ran={ranThisFrame}");
            }
            DiagTickMovableSyncTicks = 0; DiagTickMovableSyncCalls = 0;
            DiagTickPullSyncTicks = 0; DiagTickPullSyncCalls = 0;
            DiagTickPathfindTicks = 0; DiagTickPathfindCalls = 0;
            DiagTickHashTicks = 0; DiagTickPostDrainTicks = 0;
            DiagPhysFor1Ticks = 0; DiagPhysFor2Ticks = 0;
            DiagPhysQuantTicks = 0; DiagPhysQuantModified = 0;
            DiagPhysQuantDynTotal = 0;
            DiagRngGlobalCalls = 0; DiagRngGenCalls = 0; DiagRngCtorCalls = 0; DiagGuidCalls = 0; DiagPostWrapCalls = 0;
            DiagTickWindowCount = 0; DiagPhysStepWindowCount = 0;
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPostfix]
        public static void UpdateGameLogic_Postfix(DXGame __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return;

            // Async overlapped tick continues running in _TaskLogic (and chained UpdatePhysics)
            // across RenderFrame/Present and is finalized by StartFrame -> FlushPendingAsyncTick
            // before ComputeSceneAvance / OnSetCamera of the next frame.
            if (_asyncTickInFlight && !_inSingleTickStep)
            {
                var origTask = _fTaskLogic?.GetValue(__instance) as System.Threading.Tasks.Task;
                if (origTask != null && _fTaskLogic != null)
                {
                    long launchTs = _asyncLaunchTs;
                    var cont = origTask.ContinueWith(_ =>
                    {
                        double swMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        double workerMs = (System.Diagnostics.Stopwatch.GetTimestamp() - launchTs) * swMs;
                        _diagWorkerLogicMs = _diagWorkerLogicMs <= 0.0 ? workerMs : _diagWorkerLogicMs * 0.9 + workerMs * 0.1;
                        DiagAvgTickLogicMs = _diagWorkerLogicMs;

                        try
                        {
                            if (DXGame.IsIADeterministic && _mUpdatePhysics != null
                                && __instance != null && !__instance.Disposing
                                && !LockstepClock.LoadingLevel && !LockstepClock.PausedBySession
                                && DXScene.Current != null && DXScene.Current.RootObject is DXIsometricGridObject)
                            {
                                double tPhysStart = System.Diagnostics.Stopwatch.GetTimestamp() * swMs;
                                _asyncPhysicsChained = true;
                                LockstepClock.AllowPhysicsThisFrame = true;
                                _mUpdatePhysics.Invoke(__instance, null);
                                LockstepClock.AllowPhysicsThisFrame = false;
                                double pMs = System.Diagnostics.Stopwatch.GetTimestamp() * swMs - tPhysStart;
                                if (pMs > 0.2)
                                {
                                    DiagAvgPhysicsMs = DiagAvgPhysicsMs <= 0.0 ? pMs : DiagAvgPhysicsMs * 0.9 + pMs * 0.1;
                                    DiagPhysStepWindowCount++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            ModLogger.Warn($"[LockstepClock] Async UpdatePhysics continuation error: {ex}");
                        }
                        finally
                        {
                            LockstepClock.AllowPhysicsThisFrame = false;
                            LockstepClock.LogicTickActive = false;
                        }
                    }, System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
                    _fTaskLogic.SetValue(__instance, cont);
                }
                return;
            }

            _mWaitForGameLogic?.Invoke(__instance, null);
            LockstepClock.LogicTickActive = false;
            LockstepClock.InDeterministicContext = false;
        }

        [HarmonyPatch(typeof(DXGame), "WaitForGameLogic")]
        [HarmonyPrefix]
        public static void WaitForGameLogic_Prefix()
        {
            if (_asyncTickInFlight && !_inFinalizeAsyncTick)
                _asyncWaitStartTs = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        [HarmonyPatch(typeof(DXGame), "WaitForGameLogic")]
        [HarmonyPostfix]
        public static void WaitForGameLogic_Postfix(DXGame __instance)
        {
            if (!_asyncTickInFlight || _inFinalizeAsyncTick) return;
            _inFinalizeAsyncTick = true;
            try
            {
                bool didChainedPhysics = _asyncPhysicsChained;
                bool didAsyncPhysStep = _asyncPhysicsStepped;
                _asyncTickInFlight = false;
                _asyncPhysicsChained = false;
                _asyncPhysicsStepped = false;
                double swFreqMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double waitMs = _asyncWaitStartTs > 0
                    ? (System.Diagnostics.Stopwatch.GetTimestamp() - _asyncWaitStartTs) * swFreqMs
                    : 0.0;
                _asyncWaitStartTs = 0;
                _diagAsyncWaitMs = _diagAsyncWaitMs <= 0.0 ? waitMs : _diagAsyncWaitMs * 0.9 + waitMs * 0.1;

                LockstepClock.LogicTickActive = false;
                LockstepClock.InDeterministicContext = false;

                if (__instance == null || __instance.Disposing || !LockstepClock.IsDeterministicModeActive
                    || LockstepClock.LoadingLevel || DXScene.Current == null || !(DXScene.Current.RootObject is DXIsometricGridObject))
                {
                    lock (_deferredAsyncLock) { _deferredAsyncPostMethods.Clear(); }
                    return;
                }

                if (!didChainedPhysics && DXGame.IsIADeterministic && _mUpdatePhysics != null)
                {
                    double tPhysStart = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs;
                    LockstepClock.AllowPhysicsThisFrame = true;
                    _mUpdatePhysics.Invoke(__instance, null);
                    LockstepClock.AllowPhysicsThisFrame = false;
                    double pMs = System.Diagnostics.Stopwatch.GetTimestamp() * swFreqMs - tPhysStart;
                    if (pMs > 0.2)
                    {
                        DiagAvgPhysicsMs = DiagAvgPhysicsMs <= 0.0 ? pMs : DiagAvgPhysicsMs * 0.9 + pMs * 0.1;
                        DiagPhysStepWindowCount++;
                    }
                }

                if (didAsyncPhysStep)
                {
                    RenderVisualInterpolationPatch.OnPhysicsStepCompleted();
                    CSpritePullIsolationPatch.RunDeterministicPullUpdates(__instance);
                }

                if (Sync.CoopSessionManager.IsInSyncedLevel)
                {
                    long tHashStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    Sync.CoopSessionManager.OnSimulationTickCompleted(__instance.IAIterationInt);
                    DiagTickHashTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tHashStart;
                }
                RenderVisualInterpolationPatch.OnLogicTickCompleted(1);

                List<Action> deferred = null;
                lock (_deferredAsyncLock)
                {
                    if (_deferredAsyncPostMethods.Count > 0)
                    {
                        deferred = new List<Action>(_deferredAsyncPostMethods);
                        _deferredAsyncPostMethods.Clear();
                    }
                }
                if (deferred != null)
                {
                    long tDrainStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool prevRendering = __instance.RenderingFrame;
                    __instance.RenderingFrame = false;
                    try
                    {
                        for (int i = 0; i < deferred.Count; i++)
                            LockstepClock.RunInDeterministicContext(deferred[i]);
                        // Flush nested frame post-methods (e.g. DXComponent.set_Entity -> SceneChanged)
                        // queued by the deferred callbacks above, matching sync DXPostMethodManager.ExecuteAll().
                        __instance.PostMethods_OnFinishFrame?.ExecuteAll();
                        __instance.PostMethods_OnSafeLogicAndDrawing?.ExecuteAll();
                        __instance.PostMethods_OnStartFrame?.ExecuteAll();
                    }
                    finally { __instance.RenderingFrame = prevRendering; }
                    DiagTickPostDrainTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tDrainStart;
                }

                DiagTickWindowCount++;
                if (DiagTickWindowCount >= 60)
                    LogPerfWindow(__instance, 1);
            }
            finally
            {
                _inFinalizeAsyncTick = false;
            }
        }

        // 4. Suppress the engine's background GameTime/physics threads while deterministic:
        //    they advance GameTime by real frame time and would destroy fixed-step reproducibility.
        //    UpdatePhysics() is still called synchronously from SetCamera when IsIADeterministic.
        [HarmonyPatch(typeof(DXGame), "LoadPhysics")]
        [HarmonyPrefix]
        public static bool LoadPhysics_Prefix() => !LockstepClock.IsDeterministicModeActive;

        [HarmonyPatch(typeof(DXGame), "LoadPhysicsOld")]
        [HarmonyPrefix]
        public static bool LoadPhysicsOld_Prefix() => !LockstepClock.IsDeterministicModeActive;

        // Mark the physics section so DXRandom.Global draws from physics callbacks use a stream that
        // render-phase draws cannot perturb, and so DeterministicTaskPatch only serializes the two
        // position-affecting For-loops (not scene culling / texture baking / level tables).
        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPrefix]
        public static bool UpdatePhysics_Enter(out uint __state)
        {
            __state = 0;
            if (!LockstepClock.IsDeterministicModeActive) return true;
            if (!LockstepClock.AllowPhysicsThisFrame || LockstepClock.LoadingLevel || LockstepClock.PausedBySession)
                return false;

            __state = DXPhysicWorld.NUpdates;
            LockstepClock.AllowPhysicsThisFrame = false;
            LockstepClock.PhysicsThreadId = Thread.CurrentThread.ManagedThreadId;
            LockstepClock.InPhysicsContext = true;
            LockstepClock.InPhysicsStep = true;
            LockstepClock.InPhysicsReadback = false;
            return true;
        }

        [HarmonyPatch(typeof(DXGame), "OnFinishPhysics")]
        [HarmonyPostfix]
        public static void OnFinishPhysics_Postfix()
        {
            if (LockstepClock.InPhysicsStep)
                LockstepClock.InPhysicsReadback = true;
        }

        [HarmonyPatch(typeof(DXGame), "UpdatePhysics")]
        [HarmonyPostfix]
        public static void UpdatePhysics_Exit(uint __state)
        {
            bool didStep = LockstepClock.InPhysicsStep;
            LockstepClock.InPhysicsContext = false;
            LockstepClock.PhysicsThreadId = 0;
            LockstepClock.InPhysicsStep = false;
            LockstepClock.InPhysicsReadback = false;

            if (LockstepClock.IsDeterministicModeActive)
            {
                DXPhysicWorld.TimeLastBodyPosition = DXPhysicWorld.TimeBodyPosition;
                if (didStep && DXPhysicWorld.NUpdates != __state && !LockstepClock.LoadingLevel)
                {
                    LockstepClock.QuantizePhysicsBodiesPostStep();
                    if (_asyncTickInFlight)
                    {
                        _asyncPhysicsStepped = true;
                    }
                    else
                    {
                        RenderVisualInterpolationPatch.OnPhysicsStepCompleted();
                        CSpritePullIsolationPatch.RunDeterministicPullUpdates(DXGame.Current);
                    }
                }
            }
        }

        // Prevent DXEntity.UpdateSceneObjectParallel from mutating authoritative Entity.Position and
        // triggering DXWorldGrid.SetNewCellFor; when inside RenderVisualInterpolationPatch's scene pass,
        // return the thread's already-projected visual position so DXEntity.Position = InterpolatedPosition
        // becomes a zero-work no-op.
        [HarmonyPatch(typeof(DXPhysicBody), nameof(DXPhysicBody.InterpolatedPosition), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool InterpolatedPosition_Prefix(DXPhysicBody __instance, ref PointF __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            if (RenderVisualInterpolationPatch.TryGetProjectedEntityPos(__instance?.GameEntity, out PointF visualPos))
            {
                __result = visualPos;
                return false;
            }
            __result = __instance.PositionComputed;
            return false;
        }

        // Remember which thread runs the frame loop; DXRandomCtorPatch uses it to keep the
        // level-loading worker's seed sequence synced while the UI thread stays off that counter.
        // Accumulate wall-clock dtMs FIRST, then flush any completed async tick so OnPhysicsStepCompleted
        // preserves exact overshoot without jumping frame 1 of the next physics interval.
        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPrefix]
        public static void StartFrame_CaptureMainThread()
        {
            if (LockstepClock.MainThreadId == 0) LockstepClock.CaptureMainThread();
            RenderVisualInterpolationPatch.UpdateFrameAlpha();
            FlushPendingAsyncTick(DXGame.Current);
        }
    }

    /// <summary>
    /// Isolates visual scene interpolation, resync snap smoothing, and visual GameTime
    /// from deterministic simulation state across the entire DXGame.SetCamera scene-update pass
    /// (covering CSprite, CSelectable, CShadow, custom components, and isometric Z-sorting).
    /// </summary>
    public static class RenderVisualInterpolationPatch
    {
        public struct RestoreState
        {
            public DXEntity Entity;
            public PointF OrigPos;
        }

        private struct VisualSnapEntry
        {
            public float OffsetX;
            public float OffsetY;
            public long StartTimestamp;
        }

        private const double VisualSnapExpireMs = 600.0;
        private const double VisualSnapTauMs = 180.0;
        private const float MaxVisualSnapOffsetCells = 3.5f;

        private static readonly double _swToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static long _lastStartFrameWallTs;
        private static double _accumPhysicsElapsedMs;
        private static double _accumLogicElapsedMs;
        private static double _visualGameTimeRemainderMs;
        private static volatile bool _hasPhysicsStep;
        private static volatile float _frameInterpAlpha = -1f;

        private static readonly ConcurrentDictionary<ulong, VisualSnapEntry> _visualSnapOffsets =
            new ConcurrentDictionary<ulong, VisualSnapEntry>();
        private static readonly List<ulong> _staleVisualSnapIds = new List<ulong>();
        private static volatile bool _hasVisualSnapOffsets;

        private static long _currentFrameTimestamp;

        private static bool _posAccessorsCompiled;
        private static Func<DXEntity, PointF> _fastGetRawPos;
        private static Action<DXEntity, PointF> _fastSetRawPos;

        private static volatile bool _inSetCamera;
        private static volatile bool _inVisualSceneUpdate;
        private static double _savedAuthGameTime;
        private static bool _hasSavedVisualGameTime;
        private static readonly List<RestoreState> _projectedRestoreList = new List<RestoreState>(2048);

        internal static bool InSetCamera => _inSetCamera;

        internal static void BeginSetCamera() => _inSetCamera = true;
        internal static void EndSetCamera(DXGame game)
        {
            _inSetCamera = false;
            ExitVisualSceneUpdate(game);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool TryGetProjectedEntityPos(DXEntity ent, out PointF pos)
        {
            if (_inVisualSceneUpdate && ent != null && _fastGetRawPos != null)
            {
                pos = _fastGetRawPos(ent);
                return true;
            }
            pos = default;
            return false;
        }

        public static void RecordVisualSnapOffset(ulong entityId, float offsetX, float offsetY)
        {
            if (entityId == 0) return;
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_visualSnapOffsets.TryGetValue(entityId, out var prev))
            {
                double prevAgeMs = (nowTs - prev.StartTimestamp) * _swToMs;
                if (prevAgeMs > 0.0 && prevAgeMs < VisualSnapExpireMs)
                {
                    float prevDecay = (float)Math.Exp(-prevAgeMs / VisualSnapTauMs);
                    offsetX += prev.OffsetX * prevDecay;
                    offsetY += prev.OffsetY * prevDecay;
                }
            }

            float magSq = offsetX * offsetX + offsetY * offsetY;
            if (magSq < 0.0025f) return;
            if (magSq > MaxVisualSnapOffsetCells * MaxVisualSnapOffsetCells)
            {
                float scale = MaxVisualSnapOffsetCells / (float)Math.Sqrt(magSq);
                offsetX *= scale;
                offsetY *= scale;
            }

            _visualSnapOffsets[entityId] = new VisualSnapEntry
            {
                OffsetX = offsetX,
                OffsetY = offsetY,
                StartTimestamp = nowTs
            };
            _hasVisualSnapOffsets = true;
        }

        private static void EnsureRawPosAccessors()
        {
            if (_posAccessorsCompiled) return;
            _posAccessorsCompiled = true;
            try
            {
                var fPos = typeof(DXEntity).GetField("_Position", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fPos == null || fPos.FieldType != typeof(PointF))
                {
                    ModLogger.Warn("[RenderInterp] DXEntity._Position field not found; staying discrete.");
                    return;
                }

                var dmGet = new DynamicMethod("TABCoop_GetRawEntityPos", typeof(PointF), new[] { typeof(DXEntity) },
                    typeof(RenderVisualInterpolationPatch).Module, skipVisibility: true);
                var ilG = dmGet.GetILGenerator();
                ilG.Emit(OpCodes.Ldarg_0);
                ilG.Emit(OpCodes.Ldfld, fPos);
                ilG.Emit(OpCodes.Ret);
                _fastGetRawPos = (Func<DXEntity, PointF>)dmGet.CreateDelegate(typeof(Func<DXEntity, PointF>));

                var dmSet = new DynamicMethod("TABCoop_SetRawEntityPos", typeof(void), new[] { typeof(DXEntity), typeof(PointF) },
                    typeof(RenderVisualInterpolationPatch).Module, skipVisibility: true);
                var ilS = dmSet.GetILGenerator();
                ilS.Emit(OpCodes.Ldarg_0);
                ilS.Emit(OpCodes.Ldarg_1);
                ilS.Emit(OpCodes.Stfld, fPos);
                ilS.Emit(OpCodes.Ret);
                _fastSetRawPos = (Action<DXEntity, PointF>)dmSet.CreateDelegate(typeof(Action<DXEntity, PointF>));

                ModLogger.Info("[Perf] Compiled DynamicMethods TABCoop_GetRawEntityPos / SetRawEntityPos.");
            }
            catch (Exception ex)
            {
                _fastGetRawPos = null;
                _fastSetRawPos = null;
                ModLogger.Warn($"[RenderInterp] Failed to compile raw position accessors: {ex.Message}");
            }
        }

        internal static void Reset()
        {
            _hasPhysicsStep = false;
            _accumPhysicsElapsedMs = 0.0;
            _accumLogicElapsedMs = 0.0;
            _visualGameTimeRemainderMs = 0.0;
            _lastStartFrameWallTs = 0;
            _frameInterpAlpha = -1f;
            _hasVisualSnapOffsets = false;
            _visualSnapOffsets.Clear();
            _currentFrameTimestamp = 0;
            _inSetCamera = false;
            _inVisualSceneUpdate = false;
            _hasSavedVisualGameTime = false;
        }

        internal static void OnLogicTickCompleted(int ticksRan)
        {
            if (ticksRan <= 0) return;
            if (ticksRan > 1)
            {
                _accumLogicElapsedMs = 0.0;
            }
            else
            {
                _accumLogicElapsedMs -= LockstepClock.MsPerLogicTick;
                if (_accumLogicElapsedMs < 0.0) _accumLogicElapsedMs = 0.0;
                if (_accumLogicElapsedMs > LockstepClock.MsPerLogicTick * 0.95)
                    _accumLogicElapsedMs = LockstepClock.MsPerLogicTick * 0.95;
            }
            _visualGameTimeRemainderMs = _accumLogicElapsedMs;
        }

        internal static void OnPhysicsStepCompleted()
        {
            if (_hasPhysicsStep)
            {
                _accumPhysicsElapsedMs -= LockstepClock.MsPerPhysicsTick;
                if (_accumPhysicsElapsedMs < 0.0) _accumPhysicsElapsedMs = 0.0;
                if (_accumPhysicsElapsedMs > LockstepClock.MsPerPhysicsTick * 0.95)
                    _accumPhysicsElapsedMs = LockstepClock.MsPerPhysicsTick * 0.95;
            }
            else
            {
                _accumPhysicsElapsedMs = 0.0;
                _hasPhysicsStep = true;
            }

            float a = (float)(_accumPhysicsElapsedMs / LockstepClock.MsPerPhysicsTick);
            _frameInterpAlpha = a < 0f ? 0f : (a > 1f ? 1f : a);
        }

        internal static void UpdateFrameAlpha()
        {
            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            double dtMs = _lastStartFrameWallTs > 0 ? (nowTs - _lastStartFrameWallTs) * _swToMs : 0.0;
            _lastStartFrameWallTs = nowTs;
            _currentFrameTimestamp = nowTs;

            var game = DXGame.Current;

            if (_hasVisualSnapOffsets)
            {
                _staleVisualSnapIds.Clear();
                foreach (var kv in _visualSnapOffsets)
                {
                    if ((nowTs - kv.Value.StartTimestamp) * _swToMs > VisualSnapExpireMs)
                        _staleVisualSnapIds.Add(kv.Key);
                }
                for (int i = 0; i < _staleVisualSnapIds.Count; i++)
                    _visualSnapOffsets.TryRemove(_staleVisualSnapIds[i], out _);
                _hasVisualSnapOffsets = !_visualSnapOffsets.IsEmpty;
            }

            if (!LockstepClock.IsDeterministicModeActive
                || Testing.DeterministicReplayVerifier.IsRunning
                || LockstepClock.LoadingLevel
                || !_hasPhysicsStep
                || DXGame.IsInDesigner
                || game == null
                || game.Disposing
                || DXScene.Current == null
                || !(DXScene.Current.RootObject is DXIsometricGridObject)
                || Core.GameBridge.IsGameLoading())
            {
                _frameInterpAlpha = -1f;
                _visualGameTimeRemainderMs = 0.0;
                return;
            }

            EnsureRawPosAccessors();
            if (_fastGetRawPos == null || _fastSetRawPos == null)
            {
                _frameInterpAlpha = -1f;
                _visualGameTimeRemainderMs = 0.0;
                return;
            }

            if (!LockstepClock.PausedBySession && game != null && !game.Paused && game.GameSpeed > 0.001)
            {
                double clampedDt = dtMs < 0.0 ? 0.0 : (dtMs > 100.0 ? 100.0 : dtMs);
                double scaledDt = clampedDt * game.GameSpeed;

                _accumPhysicsElapsedMs += scaledDt;
                double maxPhysAccum = LockstepClock.MsPerPhysicsTick * 1.35;
                if (_accumPhysicsElapsedMs > maxPhysAccum)
                    _accumPhysicsElapsedMs = maxPhysAccum;

                _accumLogicElapsedMs += scaledDt;
                double maxLogicAccum = LockstepClock.MsPerLogicTick * 1.35;
                if (_accumLogicElapsedMs > maxLogicAccum)
                    _accumLogicElapsedMs = maxLogicAccum;
                _visualGameTimeRemainderMs = Math.Min(LockstepClock.MsPerLogicTick * 0.98, _accumLogicElapsedMs);
            }
            else
            {
                _visualGameTimeRemainderMs = 0.0;
            }

            float a = (float)(_accumPhysicsElapsedMs / LockstepClock.MsPerPhysicsTick);
            _frameInterpAlpha = a < 0f ? 0f : (a > 1f ? 1f : a);
        }

        internal static void EnterVisualSceneUpdate(DXGame game)
        {
            if (!LockstepClock.IsDeterministicModeActive
                || Testing.DeterministicReplayVerifier.IsRunning
                || LockstepClock.LoadingLevel
                || DXGame.IsInDesigner
                || game == null
                || game.Disposing
                || DXScene.Current == null
                || !(DXScene.Current.RootObject is DXIsometricGridObject)
                || Core.GameBridge.IsGameLoading())
                return;

            EnsureRawPosAccessors();
            if (_fastGetRawPos == null || _fastSetRawPos == null) return;

            _inVisualSceneUpdate = true;
            _hasSavedVisualGameTime = false;
            if (!LockstepClock.PausedBySession && !game.Paused && game.GameSpeed > 0.001 && _visualGameTimeRemainderMs >= 1.0)
            {
                _savedAuthGameTime = game.GameTime;
                _hasSavedVisualGameTime = true;
                LockstepClock.SetDeterministicGameTime(game, _savedAuthGameTime + _visualGameTimeRemainderMs);
            }

            float alpha = _frameInterpAlpha;
            if (alpha < 0f) return;

            bool physLockTaken = false;
            try
            {
                // DXPhysicWorld.Clear() / UnloadLevel runs on a background loader thread under
                // DXPhysicWorld.Sync while Window_Paint can still pump SetCamera fade frames.
                // Never stall teardown or race against Bodies shrinking mid-iteration.
                Monitor.TryEnter(DXPhysicWorld.Sync, ref physLockTaken);
                if (!physLockTaken) return;

                var bodies = DXPhysicWorld.Bodies;
                if (bodies == null || bodies.Count == 0) return;

                _projectedRestoreList.Clear();
                bool hasSnaps = _hasVisualSnapOffsets;
                long nowTs = _currentFrameTimestamp;
                int count = bodies.Count;

                for (int i = 0; i < count; i++)
                {
                    if (i >= bodies.Count) break;
                    var body = bodies[i];
                    if (body == null || body.Handle == IntPtr.Zero) continue;
                    var ent = body.GameEntity;
                    if (ent == null || ent.Disposed || ent.PhysicalType != DXBodyType.Dynamic) continue;
                    if (ent.SceneObject == null) continue;

                    PointF lastPos = body.LastPositionComputed;
                    PointF curPos = body.PositionComputed;

                    float snapOffX = 0f, snapOffY = 0f;
                    if (hasSnaps && _visualSnapOffsets.TryGetValue(ent.ID, out var snap))
                    {
                        double ageMs = (nowTs - snap.StartTimestamp) * _swToMs;
                        if (ageMs < 0.0) ageMs = 0.0;
                        if (ageMs < VisualSnapExpireMs)
                        {
                            float decay = (float)Math.Exp(-ageMs / VisualSnapTauMs);
                            snapOffX = snap.OffsetX * decay;
                            snapOffY = snap.OffsetY * decay;
                        }
                    }

                    if (lastPos == curPos && snapOffX == 0f && snapOffY == 0f)
                        continue;

                    PointF origPos = _fastGetRawPos(ent);
                    float ddx = origPos.X - curPos.X;
                    float ddy = origPos.Y - curPos.Y;
                    if (ddx * ddx + ddy * ddy > 0.01f) continue;

                    PointF basePos = lastPos == curPos
                        ? curPos
                        : new PointF(
                            lastPos.X + (curPos.X - lastPos.X) * alpha,
                            lastPos.Y + (curPos.Y - lastPos.Y) * alpha);
                    PointF interpPos = new PointF(basePos.X + snapOffX, basePos.Y + snapOffY);
                    if (interpPos == origPos) continue;

                    _projectedRestoreList.Add(new RestoreState { Entity = ent, OrigPos = origPos });
                    _fastSetRawPos(ent, interpPos);
                }
            }
            finally
            {
                if (physLockTaken)
                    Monitor.Exit(DXPhysicWorld.Sync);
            }
        }

        internal static void ExitVisualSceneUpdate(DXGame game)
        {
            if (!_inVisualSceneUpdate) return;
            _inVisualSceneUpdate = false;

            if (_fastSetRawPos != null && _projectedRestoreList.Count > 0)
            {
                for (int i = 0; i < _projectedRestoreList.Count; i++)
                {
                    var item = _projectedRestoreList[i];
                    if (item.Entity != null)
                        _fastSetRawPos(item.Entity, item.OrigPos);
                }
                _projectedRestoreList.Clear();
            }

            if (_hasSavedVisualGameTime && game != null)
            {
                LockstepClock.SetDeterministicGameTime(game, _savedAuthGameTime);
                _hasSavedVisualGameTime = false;
            }
        }
    }

    [HarmonyPatch(typeof(DXGame), "SetCamera")]
    public static class SetCameraVisualPassPatch
    {
        [HarmonyPrefix]
        public static void Prefix() => RenderVisualInterpolationPatch.BeginSetCamera();

        [HarmonyFinalizer]
        public static void Finalizer(DXGame __instance) => RenderVisualInterpolationPatch.EndSetCamera(__instance);
    }

    [HarmonyPatch(typeof(DXWorldGrid), nameof(DXWorldGrid.UpdateVisibleCells), new[] { typeof(RectangleF), typeof(int) })]
    public static class UpdateVisibleCellsVisualPassPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (RenderVisualInterpolationPatch.InSetCamera)
                RenderVisualInterpolationPatch.EnterVisualSceneUpdate(DXGame.Current);
        }
    }

    /// <summary>
    /// Smooths camera panning frame deltas during ZXSystemGameLevel.OnSetCamera and lifts the
    /// engine's 0.7f per-frame displacement clamp (which saturates at >=16.7ms / wide zoom and
    /// causes camera pan velocity to drop/stutter on slower frames).
    /// </summary>
    [HarmonyPatch]
    public static class SmoothCameraPanPatch
    {
        public struct CamState
        {
            public bool Active;
            public double OrigElapsed;
            public RectangleF PrevCam;
            public double UsedDtMs;
        }

        private static readonly double _swToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        private static long _lastCamTs;
        private static double _smoothedDtMs = 16.67;
        private static MethodInfo _mApplyCamBounds;

        public static MethodBase TargetMethod()
        {
            Type tLevelSys = GameBridge.TypeZXSystemGameLevel ?? AccessTools.TypeByName(ObfuscatedSymbols.Type_ZXSystemGameLevel);
            return tLevelSys != null ? AccessTools.Method(tLevelSys, "OnSetCamera", Type.EmptyTypes) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(out CamState __state)
        {
            __state = default;
            if (!LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel)
                return;
            var scene = DXScene.Current;
            if (scene == null)
                return;

            long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
            double rawDtMs = _lastCamTs > 0 ? (nowTs - _lastCamTs) * _swToMs : 16.67;
            _lastCamTs = nowTs;
            if (rawDtMs < 2.0 || rawDtMs > 200.0) rawDtMs = 16.67;
            _smoothedDtMs = _smoothedDtMs * 0.65 + rawDtMs * 0.35;
            double useDtMs = _smoothedDtMs < 4.0 ? 4.0 : (_smoothedDtMs > 55.0 ? 55.0 : _smoothedDtMs);

            __state.Active = true;
            __state.OrigElapsed = scene.ElapsedTime;
            __state.PrevCam = scene.CameraArea;
            __state.UsedDtMs = useDtMs;
            scene.ElapsedTime = useDtMs;
        }

        [HarmonyFinalizer]
        public static void Finalizer(object __instance, CamState __state)
        {
            if (!__state.Active)
                return;
            var scene = DXScene.Current;
            if (scene == null)
                return;

            scene.ElapsedTime = __state.OrigElapsed;

            RectangleF curCam = scene.CameraArea;
            RectangleF prevCam = __state.PrevCam;
            if (Math.Abs(curCam.Width - prevCam.Width) < 0.001f
                && Math.Abs(curCam.Height - prevCam.Height) < 0.001f)
            {
                float desiredStep = Math.Min(2.4f, 0.0007f * (float)__state.UsedDtMs * prevCam.Width);
                if (desiredStep > 0.705f)
                {
                    float dx = curCam.X - prevCam.X;
                    float dy = curCam.Y - prevCam.Y;
                    bool pannedX = Math.Abs(Math.Abs(dx) - 0.7f) < 0.02f;
                    bool pannedY = Math.Abs(Math.Abs(dy) - 0.7f) < 0.02f;
                    if (pannedX || pannedY)
                    {
                        float scale = desiredStep / 0.7f;
                        var adjusted = new RectangleF(
                            prevCam.X + (pannedX ? dx * scale : dx),
                            prevCam.Y + (pannedY ? dy * scale : dy),
                            curCam.Width,
                            curCam.Height);
                        if (_mApplyCamBounds == null && __instance != null)
                            _mApplyCamBounds = AccessTools.Method(__instance.GetType(), "#=zTPyBK8tWy$hm", new[] { typeof(RectangleF) });
                        _mApplyCamBounds?.Invoke(__instance, new object[] { adjusted });
                    }
                }
            }
        }
    }

    /// <summary>
    /// Replaces background-thread pathfinding (DXWorldGrid.FindPathConcurrent / HighPriority) with
    /// deterministic synchronous execution on the active logic thread when deterministic mode is active.
    /// This eliminates background worker scheduling races and avoids vanilla's buggy IsMultiplayer branch
    /// in CMovable (which hardcodes cellSize=1 and recurses infinitely on null paths).
    /// </summary>
    [HarmonyPatch(typeof(DXWorldGrid))]
    public static class DeterministicPathfindingPatch
    {
        private static FieldInfo _movableRequestField;

        [HarmonyPatch(nameof(DXWorldGrid.FindPathConcurrent))]
        [HarmonyPrefix]
        public static bool FindPathConcurrent_Prefix(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes,
            bool useCache)
        {
            if (!LockstepClock.IsDeterministicModeActive)
            {
                pfr = null;
                return true;
            }

            RunSyncPathfinding(out pfr, cellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes);
            return false;
        }

        [HarmonyPatch(nameof(DXWorldGrid.FindPathConcurrentWithHighPriority))]
        [HarmonyPrefix]
        public static bool FindPathConcurrentWithHighPriority_Prefix(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes,
            bool useCache)
        {
            if (!LockstepClock.IsDeterministicModeActive)
            {
                pfr = null;
                return true;
            }

            RunSyncPathfinding(out pfr, cellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes);
            return false;
        }

        private static void RunSyncPathfinding(
            out DXWorldGrid.PathFindingRequest pfr,
            int cellSize,
            Point p1,
            Point p2,
            uint mask,
            bool nearPath,
            DXWorldGrid.PathFindingRequest.OnPathFoundDelegate onPathFound,
            object userData,
            int? maxNodes)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int safeCellSize = cellSize < 1 ? 1 : (cellSize > 3 ? 3 : cellSize);
            var req = new DXWorldGrid.PathFindingRequest(safeCellSize, p1, p2, mask, nearPath, onPathFound, userData, maxNodes, false);
            pfr = req;

            // Mirror CMovable.#=ziMavkfK9ZJji before invoking onPathFound so #=zxg3EOEU9RJ22's
            // reference check (`request == #=ziMavkfK9ZJji`) passes even if Harmony stages `out pfr`.
            if (userData != null)
            {
                if (_movableRequestField == null)
                    _movableRequestField = AccessTools.Field(userData.GetType(), ObfuscatedSymbols.Field_CMovable_PendingPathRequest);
                _movableRequestField?.SetValue(userData, req);
            }

            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Computing;
            req.Path = DXWorldGrid.FindPath(safeCellSize, p1, p2, mask, nearPath, maxNodes);
            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Computed;
            onPathFound?.Invoke(req);
            req.Status = DXWorldGrid.PathFindingRequest.TStatus.Finished;

            // If no path was found, #=zxg3EOEU9RJ22 clears #=ziMavkfK9ZJji; keep `out pfr` null as well.
            if (req.Path == null)
                pfr = null;

            System.Threading.Interlocked.Add(ref DeterministicSandboxPatches.DiagTickPathfindTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagTickPathfindCalls);
        }
    }

    /// <summary>
    /// Immediately consumes synchronous pathfinding results inside CMovable.#=zUPt0E_k= instead
    /// of leaving #=zdzxO_Le$3wwY unapplied until the next IsIAFrameModule(4) tick (+66.7ms).
    /// Mirrors vanilla IsMultiplayer immediate re-entry right after storing #=zdzxO_Le$3wwY.
    /// </summary>
    [HarmonyPatch]
    public static class CMovableImmediatePathConsumePatch
    {
        [ThreadStatic]
        private static bool _inRecursiveConsume;
        private static Type _tMovable;
        private static FieldInfo _fNewPath;
        private static MethodInfo _mUpdatePath;
        private static Func<object, object> _fastGetNewPath;
        private static Action<object, PointF, bool> _fastUpdatePath;
        private static bool _compiled;

        public static MethodBase TargetMethod()
        {
            _tMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
            if (_tMovable == null) return null;
            _fNewPath = AccessTools.Field(_tMovable, "#=zdzxO_Le$3wwY");
            _mUpdatePath = AccessTools.Method(_tMovable, "#=zUPt0E_k=", new[] { typeof(PointF), typeof(bool) });
            return _mUpdatePath;
        }

        private static void EnsureFastDelegates()
        {
            if (_compiled) return;
            _compiled = true;
            try
            {
                if (_tMovable == null || _fNewPath == null || _mUpdatePath == null) return;

                var dmGet = new DynamicMethod("TABCoop_GetMovableNewPath", typeof(object), new[] { typeof(object) },
                    typeof(CMovableImmediatePathConsumePatch).Module, skipVisibility: true);
                var ilG = dmGet.GetILGenerator();
                ilG.Emit(OpCodes.Ldarg_0);
                ilG.Emit(OpCodes.Castclass, _tMovable);
                ilG.Emit(OpCodes.Ldfld, _fNewPath);
                ilG.Emit(OpCodes.Ret);
                _fastGetNewPath = (Func<object, object>)dmGet.CreateDelegate(typeof(Func<object, object>));

                var dmCall = new DynamicMethod("TABCoop_CallMovableUpdatePath", typeof(void),
                    new[] { typeof(object), typeof(PointF), typeof(bool) },
                    typeof(CMovableImmediatePathConsumePatch).Module, skipVisibility: true);
                var ilC = dmCall.GetILGenerator();
                ilC.Emit(OpCodes.Ldarg_0);
                ilC.Emit(OpCodes.Castclass, _tMovable);
                ilC.Emit(OpCodes.Ldarg_1);
                ilC.Emit(OpCodes.Ldarg_2);
                ilC.Emit(OpCodes.Call, _mUpdatePath);
                ilC.Emit(OpCodes.Ret);
                _fastUpdatePath = (Action<object, PointF, bool>)dmCall.CreateDelegate(typeof(Action<object, PointF, bool>));
            }
            catch (Exception ex)
            {
                _fastGetNewPath = null;
                _fastUpdatePath = null;
                ModLogger.Warn($"[Perf] CMovableImmediatePathConsume compile failed (using reflection): {ex.Message}");
            }
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, PointF __0, bool __1)
        {
            if (!LockstepClock.IsDeterministicModeActive || _inRecursiveConsume || __instance == null)
                return;

            EnsureFastDelegates();
            object pending = _fastGetNewPath != null ? _fastGetNewPath(__instance) : _fNewPath?.GetValue(__instance);
            if (pending == null) return;

            _inRecursiveConsume = true;
            try
            {
                if (_fastUpdatePath != null)
                    _fastUpdatePath(__instance, __0, __1);
                else
                    _mUpdatePath?.Invoke(__instance, new object[] { __0, __1 });
            }
            finally
            {
                _inRecursiveConsume = false;
            }
        }
    }

    /// <summary>
    /// Runs after ZXSystemGameLevel.#=zwK_pUIeRBpbp (the main-thread finish action of the async
    /// loader). This is the first point where all saved entities and GameTime are restored, so it is
    /// the only safe place to calibrate the synced ID counter and align the simulation clocks.
    /// </summary>
    [HarmonyPatch]
    public static class LevelLoadCompletePatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_OnLevelLoadComplete, Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            LockstepClock.OnLevelLoadComplete();
            Testing.DeterministicReplayVerifier.OnLevelLoadComplete();
            Sync.CoopSessionManager.OnLevelLoadComplete();
        }
    }

    /// <summary>
    /// ZXSystemGameLevel.OnStartUpdate runs inside DXGame._TaskLogic right before the deterministic
    /// entity update loop. Rebind that worker thread's [ThreadStatic] behaviour RNG once per tick
    /// instead of patching DXEntity.Update (which would destroy AggressiveInlining across 50k+ entities).
    /// </summary>
    [HarmonyPatch]
    public static class EntityUpdateBehaviourRandomPatch
    {
        public static MethodBase TargetMethod()
        {
            var t = Core.GameBridge.TypeZXSystemGameLevel ?? AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXSystemGameLevel);
            return t != null ? AccessTools.Method(t, "OnStartUpdate", Type.EmptyTypes) : null;
        }

        [HarmonyPrefix]
        public static void Prefix()
        {
            if (LockstepClock.IsDeterministicModeActive)
            {
                LockstepClock.EnsureBehaviourRandomOnCurrentThread();
                var game = DXGame.Current;
                if (game != null)
                    LockstepClock.ReanchorSimulationRngForTick(game.IAIterationInt);
            }
        }
    }

    /// <summary>
    /// Logs any engine message-box alert raised while deterministic mode is active. The save loader
    /// reports deserialization failures this way (no exception escapes), so a silent Client-side
    /// load failure shows up here.
    /// </summary>
    [HarmonyPatch]
    public static class MessageBoxAlertDiagPatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowAlert,
                new[] { typeof(string), typeof(string), typeof(Action) });

        [HarmonyPrefix]
        public static void Prefix(string __0, string __1)
        {
            if (LockstepClock.IsDeterministicModeActive)
                ModLogger.Warn($"[MsgBox] {__0}: {__1}");
        }
    }

    /// <summary>
    /// DXGame.IsFrameSceneModule(n) is true when the RENDER frame counter is a multiple of n. Two
    /// processes render at different rates (focused vs background window), so any logic or UI action
    /// gated on it drifts apart. In deterministic mode, gate on the logic tick counter instead.
    /// </summary>
    [HarmonyPatch(typeof(DXGame), "IsFrameSceneModule")]
    public static class FrameSceneModuleDeterminismPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(int iteration, ref bool __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            var game = DXGame.Current;
            __result = iteration > 0 && game != null && (game.IAIterationInt % iteration) == 0;
            return false;
        }
    }

    /// <summary>
    /// Preserves deserialized in-flight projectile kinematics across ZX.Entities.Proyectile.OnSceneAdded.
    /// On load, OnSceneAdded advances Arrow Origin by +0.3f, overwrites Position = Origin, and
    /// recomputes DTime/DX/DY. A Host-to-Client snapshot load runs OnSceneAdded a second time unless
    /// preserved, mutating in-flight projectiles before tick 0. Newly spawned projectiles enter
    /// OnSceneAdded with DTime == 0f; save-restored in-flight projectiles already have DTime > 0f.
    /// </summary>
    [HarmonyPatch]
    public static class ProyectileLoadPreservePatch
    {
        public sealed class ProyectileSavedState
        {
            public PointF Origin;
            public PointF Position;
            public PointF LastPosition;
            public float H;
            public float LastH;
            public float OriginH;
            public float Distance;
            public float DistanceH;
            public float DX;
            public float DY;
            public float DH;
            public float DTime;
            public float FactorTime;
            public float HFactor;
        }

        private static bool _metaInit;
        private static PropertyInfo _pOrigin, _pPosition, _pLastPosition;
        private static PropertyInfo _pH, _pLastH, _pOriginH, _pDistance, _pDistanceH;
        private static PropertyInfo _pDX, _pDY, _pDH, _pDTime, _pFactorTime, _pHFactor;

        private static void EnsureMeta(Type t)
        {
            if (_metaInit || t == null) return;
            _metaInit = true;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
            _pOrigin = t.GetProperty("Origin", F);
            _pPosition = t.GetProperty("Position", F);
            _pLastPosition = t.GetProperty("LastPosition", F);
            _pH = t.GetProperty("H", F);
            _pLastH = t.GetProperty("LastH", F);
            _pOriginH = t.GetProperty("OriginH", F);
            _pDistance = t.GetProperty("Distance", F);
            _pDistanceH = t.GetProperty("DistanceH", F);
            _pDX = t.GetProperty("DX", F);
            _pDY = t.GetProperty("DY", F);
            _pDH = t.GetProperty("DH", F);
            _pDTime = t.GetProperty("DTime", F);
            _pFactorTime = t.GetProperty("FactorTime", F);
            _pHFactor = t.GetProperty("HFactor", F);
        }

        public static MethodBase TargetMethod()
        {
            Type t = GameBridge.GameAssembly?.GetType("ZX.Entities.Proyectile");
            EnsureMeta(t);
            return t != null ? AccessTools.Method(t, "OnSceneAdded", Type.EmptyTypes) : null;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance, ref ProyectileSavedState __state)
        {
            __state = null;
            if (!LockstepClock.IsDeterministicModeActive || __instance == null) return;
            EnsureMeta(__instance.GetType());
            if (_pDTime == null) return;

            float dTime = (float)_pDTime.GetValue(__instance);
            if (dTime <= 0f) return;

            __state = new ProyectileSavedState
            {
                Origin = _pOrigin != null ? (PointF)_pOrigin.GetValue(__instance) : PointF.Empty,
                Position = _pPosition != null ? (PointF)_pPosition.GetValue(__instance) : PointF.Empty,
                LastPosition = _pLastPosition != null ? (PointF)_pLastPosition.GetValue(__instance) : PointF.Empty,
                H = _pH != null ? (float)_pH.GetValue(__instance) : 0f,
                LastH = _pLastH != null ? (float)_pLastH.GetValue(__instance) : 0f,
                OriginH = _pOriginH != null ? (float)_pOriginH.GetValue(__instance) : 0f,
                Distance = _pDistance != null ? (float)_pDistance.GetValue(__instance) : 0f,
                DistanceH = _pDistanceH != null ? (float)_pDistanceH.GetValue(__instance) : 0f,
                DX = _pDX != null ? (float)_pDX.GetValue(__instance) : 0f,
                DY = _pDY != null ? (float)_pDY.GetValue(__instance) : 0f,
                DH = _pDH != null ? (float)_pDH.GetValue(__instance) : 0f,
                DTime = dTime,
                FactorTime = _pFactorTime != null ? (float)_pFactorTime.GetValue(__instance) : 0f,
                HFactor = _pHFactor != null ? (float)_pHFactor.GetValue(__instance) : 0f,
            };
        }

        [HarmonyPostfix]
        public static void Postfix(object __instance, ProyectileSavedState __state)
        {
            if (__state == null || __instance == null) return;

            _pOrigin?.SetValue(__instance, __state.Origin);
            _pLastPosition?.SetValue(__instance, __state.LastPosition);
            _pPosition?.SetValue(__instance, __state.Position);
            _pH?.SetValue(__instance, __state.H);
            _pLastH?.SetValue(__instance, __state.LastH);
            _pOriginH?.SetValue(__instance, __state.OriginH);
            _pDistance?.SetValue(__instance, __state.Distance);
            _pDistanceH?.SetValue(__instance, __state.DistanceH);
            _pDX?.SetValue(__instance, __state.DX);
            _pDY?.SetValue(__instance, __state.DY);
            _pDH?.SetValue(__instance, __state.DH);
            _pDTime?.SetValue(__instance, __state.DTime);
            _pFactorTime?.SetValue(__instance, __state.FactorTime);
            _pHFactor?.SetValue(__instance, __state.HFactor);
        }
    }

    /// <summary>
    /// Deterministically refreshes CSprite animation progress (#=zbBN4HvXVWuDv) right before
    /// CMovable logic/physics force computation. Vanilla only updates #=zbBN4HvXVWuDv inside
    /// UpdateSceneObjectParallel for entities visible in the local camera viewport, yet CMovable
    /// reads it for zombie gait acceleration and harpy jump timing on all entities.
    ///
    /// Uses a compiled DynamicMethod with skipVisibility so all 20k+ movables run with zero
    /// reflection/boxing overhead and immediately early-return when neither jumping nor gait-moving.
    /// </summary>
    [HarmonyPatch]
    public static class CMovableSpriteProgressSyncPatch
    {
        private static bool _metaInit;
        private static MethodInfo _mGetSprite;
        private static FieldInfo _fCurAnim;
        private static PropertyInfo _pTimeAnimStart;
        private static FieldInfo _fAnimProgress;
        private static MethodInfo _mAnimProgress;
        private static Action<object, int> _fastSync;

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            var asm = GameBridge.GameAssembly;
            if (asm == null) return;

            Type tEntity = GameBridge.TypeZXEntity;
            Type tSprite = asm.GetType("ZX.Components.CSprite");
            Type tSpriteAnim = asm.GetType("ZX.Components.SpriteAnimation");
            Type tMovable = asm.GetType("ZX.Components.CMovable");

            if (tEntity != null)
                _mGetSprite = AccessTools.Method(tEntity, "#=zbkLIvkCtqwewx0xa2A==", Type.EmptyTypes);
            if (tSprite != null)
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fCurAnim = tSprite.GetField("#=za7pUzvqbWjrs", F);
                _pTimeAnimStart = tSprite.GetProperty("TimeAnimationStart", F);
                _fAnimProgress = tSprite.GetField("#=zbBN4HvXVWuDv", F);
            }
            if (tSpriteAnim != null)
                _mAnimProgress = AccessTools.Method(tSpriteAnim, "#=z_y5dBgkM5Q4t", new[] { typeof(int) });

            _fastSync = TryCompileFastSync(tMovable, tEntity, tSprite, tSpriteAnim);
        }

        private static Action<object, int> TryCompileFastSync(Type tMovable, Type tEntity, Type tSprite, Type tSpriteAnim)
        {
            try
            {
                if (tMovable == null || tEntity == null || tSprite == null || tSpriteAnim == null) return null;
                if (_mGetSprite == null || _fCurAnim == null || _pTimeAnimStart == null || _fAnimProgress == null || _mAnimProgress == null) return null;

                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fTargetPos = tMovable.GetField("#=zx1L3mWRqDcT7", F);
                var fGaitFactor = tMovable.GetField("#=z0l82Y9z76YJp58yPhQ==", F);
                var mGetJumpDestiny = tMovable.GetProperty("JumpDestiny", F)?.GetGetMethod(true);
                var mGetEntity = typeof(DXComponent).GetProperty("Entity", F)?.GetGetMethod(true);
                var mGetTimeAnimStart = _pTimeAnimStart.GetGetMethod(true);
                var mPointFNullableHasValue = typeof(PointF?).GetProperty("HasValue")?.GetGetMethod();

                if (fTargetPos == null || fGaitFactor == null || mGetJumpDestiny == null || mGetEntity == null ||
                    mGetTimeAnimStart == null || mPointFNullableHasValue == null)
                    return null;

                var dm = new DynamicMethod("TABCoop_FastMovableSpriteSync", typeof(void), new[] { typeof(object), typeof(int) },
                    typeof(CMovableSpriteProgressSyncPatch).Module, skipVisibility: true);
                var il = dm.GetILGenerator();

                var locMov = il.DeclareLocal(tMovable);
                var locTargetOpt = il.DeclareLocal(typeof(PointF?));
                var locJumpOpt = il.DeclareLocal(typeof(PointF?));
                var locEnt = il.DeclareLocal(tEntity);
                var locSprite = il.DeclareLocal(tSprite);

                var lblCheckJump = il.DefineLabel();
                var lblProceed = il.DefineLabel();
                var lblRet = il.DefineLabel();

                // mov = (CMovable)arg0;
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, tMovable);
                il.Emit(OpCodes.Stloc, locMov);

                // if (mov.#=z0l82Y9z76YJp58yPhQ== > 0f && mov.#=zx1L3mWRqDcT7.HasValue) goto proceed;
                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Ldfld, fGaitFactor);
                il.Emit(OpCodes.Ldc_R4, 0f);
                il.Emit(OpCodes.Ble_Un_S, lblCheckJump);

                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Ldflda, fTargetPos);
                il.Emit(OpCodes.Call, mPointFNullableHasValue);
                il.Emit(OpCodes.Brtrue_S, lblProceed);

                // if (!mov.JumpDestiny.HasValue) return;
                il.MarkLabel(lblCheckJump);
                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Callvirt, mGetJumpDestiny);
                il.Emit(OpCodes.Stloc, locJumpOpt);
                il.Emit(OpCodes.Ldloca, locJumpOpt);
                il.Emit(OpCodes.Call, mPointFNullableHasValue);
                il.Emit(OpCodes.Brfalse_S, lblRet);

                // ent = mov.Entity as ZXEntity; if (ent == null) return;
                il.MarkLabel(lblProceed);
                il.Emit(OpCodes.Ldloc, locMov);
                il.Emit(OpCodes.Callvirt, mGetEntity);
                il.Emit(OpCodes.Isinst, tEntity);
                il.Emit(OpCodes.Stloc, locEnt);
                il.Emit(OpCodes.Ldloc, locEnt);
                il.Emit(OpCodes.Brfalse_S, lblRet);

                // sprite = ent.#=zbkLIvkCtqwewx0xa2A==(); if (sprite == null) return;
                il.Emit(OpCodes.Ldloc, locEnt);
                il.Emit(OpCodes.Callvirt, _mGetSprite);
                il.Emit(OpCodes.Stloc, locSprite);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Brfalse_S, lblRet);

                // sprite.#=zbBN4HvXVWuDv = sprite.#=za7pUzvqbWjrs.#=z_y5dBgkM5Q4t(gameTimeInt - sprite.TimeAnimationStart);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Ldflda, _fCurAnim);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloc, locSprite);
                il.Emit(OpCodes.Callvirt, mGetTimeAnimStart);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Call, _mAnimProgress);
                il.Emit(OpCodes.Stfld, _fAnimProgress);

                il.MarkLabel(lblRet);
                il.Emit(OpCodes.Ret);

                var del = (Action<object, int>)dm.CreateDelegate(typeof(Action<object, int>));
                ModLogger.Info("[Perf] Compiled DynamicMethod TABCoop_FastMovableSpriteSync.");
                return del;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[Perf] TryCompileFastMovableSpriteSync failed (falling back to reflection): {ex.Message}");
                return null;
            }
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            EnsureMeta();
            Type tMovable = GameBridge.GameAssembly?.GetType("ZX.Components.CMovable");
            if (tMovable == null) yield break;

            // Only #=zMgpsCOScDEumTw2atg== (jumper height check inside #=z5GPRaBw=) and
            // #=zi4HgedM=(PointF) (active gait speed modulation inside #=zkgQp3Dx6LolarfpKKQ==)
            // actually read CSprite.#=zbBN4HvXVWuDv. Targeting them avoids ~21k Harmony
            // trampolines/tick on stationary units.
            var mJumpCheck = AccessTools.Method(tMovable, "#=zMgpsCOScDEumTw2atg==", Type.EmptyTypes);
            if (mJumpCheck != null) yield return mJumpCheck;

            var mSteerTo = AccessTools.Method(tMovable, "#=zi4HgedM=", new[] { typeof(PointF) });
            if (mSteerTo != null) yield return mSteerTo;
        }

        [HarmonyPrefix]
        public static void Prefix(DXComponent __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive || __instance == null) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var game = DXGame.Current;
            if (game == null) return;

            EnsureMeta();
            int simTimeInt = DXGame.LastGameTimeIAUpdate > 0.0
                ? (int)DXGame.LastGameTimeIAUpdate
                : game.GameTimeInt;
            if (_fastSync != null)
            {
                _fastSync(__instance, simTimeInt);
            }
            else
            {
                var entity = __instance.Entity;
                if (entity == null || _mGetSprite == null || _fCurAnim == null || _pTimeAnimStart == null || _fAnimProgress == null || _mAnimProgress == null)
                    return;
                if (!_mGetSprite.DeclaringType.IsInstanceOfType(entity))
                    return;

                object csprite = _mGetSprite.Invoke(entity, null);
                if (csprite == null) return;

                object animBox = _fCurAnim.GetValue(csprite);
                if (animBox == null) return;

                int animStart = (int)_pTimeAnimStart.GetValue(csprite);
                int elapsed = simTimeInt - animStart;
                float progress = (float)_mAnimProgress.Invoke(animBox, new object[] { elapsed });
                _fAnimProgress.SetValue(csprite, progress);
            }

            System.Threading.Interlocked.Add(ref DeterministicSandboxPatches.DiagTickMovableSyncTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagTickMovableSyncCalls);
        }
    }

    /// <summary>
    /// Redirects calls made directly on the static field DXScene.GlobalRandom (e.g. DXLinq.RandomElement,
    /// DXLinq.Shuffle, and main-thread DXHelper_Random) to a deterministic stream during simulation and
    /// loading, while routing render/UI draws to the isolated render stream.
    /// </summary>
    [HarmonyPatch(typeof(DXRandom), "genrand_uint32")]
    public static class SceneGlobalRandomInstancePatch
    {
        private static readonly DXRandom _sceneGlobalSentinel = DXScene.GlobalRandom;

        [HarmonyPrefix]
        public static bool Prefix(DXRandom __instance, ref uint __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;
            System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagRngGenCalls);
            if (LockstepClock.TryNextEntityBehaviourRandomUInt(out uint entityRand))
            {
                __result = entityRand;
                return false;
            }
            if (!object.ReferenceEquals(__instance, _sceneGlobalSentinel)) return true;

            __result = LockstepClock.ShouldUseSyncedSceneRandom()
                ? LockstepClock.GetSyncedSceneRandom().NextUInt()
                : LockstepClock.GetRenderGlobalRandom().NextUInt();
            return false;
        }
    }

    /// <summary>
    /// Ensures DXHelper_Random.Random follows the same deterministic vs render bifurcation as
    /// DXScene.GlobalRandom even on worker threads where [ThreadStatic] _Random is initially null.
    /// </summary>
    [HarmonyPatch(typeof(DXHelper_Random), "Random", MethodType.Getter)]
    public static class HelperRandomGetterPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(ref DXRandom __result)
        {
            if (!LockstepClock.IsDeterministicModeActive) return true;

            __result = LockstepClock.ShouldUseSyncedSceneRandom()
                ? LockstepClock.GetSyncedSceneRandom()
                : LockstepClock.GetRenderGlobalRandom();
            return false;
        }
    }

    /// <summary>
    /// Isolates synchronous ZXSystemAudio calls from the deterministic PRNG streams. Audio
    /// playback checks local camera frustum and singleton caches (sound-list shuffle dictionary),
    /// so letting it draw from DXRandom.Global or DXScene.GlobalRandom desyncs Host vs Client.
    /// </summary>
    [HarmonyPatch]
    public static class AudioRngIsolationPatch
    {
        private static readonly string[] TargetNames =
        {
            Core.ObfuscatedSymbols.Method_Audio_PickFromList,
            Core.ObfuscatedSymbols.Method_Audio_PickWeightedVoice,
            Core.ObfuscatedSymbols.Method_Audio_StartAmbient,
            Core.ObfuscatedSymbols.Method_Audio_PlayUnique,
            Core.ObfuscatedSymbols.Method_Audio_PlayEntitySound,
            Core.ObfuscatedSymbols.Method_Audio_PlayPosSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayPosSound3D,
            Core.ObfuscatedSymbols.Method_Audio_Play2DSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayNamed2DSound,
            Core.ObfuscatedSymbols.Method_Audio_PlayNotification,
        };

        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var audioType = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXSystemAudio);
            if (audioType == null) yield break;

            var nameSet = new System.Collections.Generic.HashSet<string>(TargetNames, StringComparer.Ordinal);
            foreach (var m in AccessTools.GetDeclaredMethods(audioType))
            {
                if (m != null && !m.IsAbstract && nameSet.Contains(m.Name))
                    yield return m;
            }
        }

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static void Prefix() => LockstepClock.EnterAudioContext();

        [HarmonyFinalizer]
        public static void Finalizer() => LockstepClock.ExitAudioContext();
    }

    /// <summary>
    /// Replaces BHFarmer's farm-cell picker (#=zK2CGzIZdW6uI$Q9jPNxMDPs=) in deterministic mode so
    /// it selects grass cells around the owner Farm from deterministic terrain + building occupancy
    /// (TerrainResourceCells == Grass and !AnyEntityAtCell(..., 2048u)) rather than querying
    /// CFarmer's visual Wheat_A DXEntities (which spawn via InvokeOnStartFrame and fade out over
    /// 1000ms of render-frame animation time before removal from DXWorldGrid).
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicFarmerTargetPatch
    {
        private static bool _metaInit;
        private static MethodInfo _mBDataOwner;
        private static PropertyInfo _pBDataTargetPos;
        private static PropertyInfo _pRefBuildingOwner;
        private static PropertyInfo _pRefEntity;
        private static MethodInfo _mGetParams;
        private static PropertyInfo _pCollectRadius;
        private static MethodInfo _mTerrainGetCurrent;
        private static PropertyInfo _pTerrainCells;

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            var asm = GameBridge.GameAssembly;
            if (asm == null) return;

            var tBData = asm.GetType("ZX.Behaviours.BData");
            if (tBData != null)
            {
                _mBDataOwner = AccessTools.Method(tBData, Core.ObfuscatedSymbols.Method_BData_GetOwner, Type.EmptyTypes);
                _pBDataTargetPos = AccessTools.Property(tBData, "TargetPosition");
            }

            var tColonist = asm.GetType("ZX.Entities.Colonist");
            if (tColonist != null)
            {
                _pRefBuildingOwner = AccessTools.Property(tColonist, "RefBuildingOwner");
                if (_pRefBuildingOwner != null)
                    _pRefEntity = AccessTools.Property(_pRefBuildingOwner.PropertyType, "Entity");
            }

            var tZxEntity = asm.GetType("ZX.Entities.ZXEntity");
            if (tZxEntity != null)
                _mGetParams = AccessTools.Method(tZxEntity, Core.ObfuscatedSymbols.Method_ZXEntity_GetParams, Type.EmptyTypes);

            var tParams = _mGetParams?.ReturnType;
            if (tParams != null)
                _pCollectRadius = AccessTools.Property(tParams, "ResourceCollectionRadius");

            var tTerrain = asm.GetType(Core.ObfuscatedSymbols.Type_TerrainSystem);
            if (tTerrain != null)
            {
                _mTerrainGetCurrent = AccessTools.Method(tTerrain, Core.ObfuscatedSymbols.Method_TerrainSystem_GetCurrent, Type.EmptyTypes);
                _pTerrainCells = AccessTools.Property(tTerrain, Core.ObfuscatedSymbols.Prop_TerrainSystem_Cells);
            }
        }

        public static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var tBeh = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXBehaviour);
            var fNode = tBeh != null
                ? tBeh.GetField(Core.ObfuscatedSymbols.Field_ZXBehaviour_PickFarmCell, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                : null;
            object node = fNode?.GetValue(null);
            if (node == null) yield break;

            var del = AccessTools.Property(node.GetType(), "OnExecute")?.GetValue(node) as Delegate;
            if (del?.Method != null) yield return del.Method;
        }

        [HarmonyPrefix]
        public static bool Prefix(object __0, ref IARes __result)
        {
            if (!LockstepClock.IsDeterministicModeActive || __0 == null) return true;
            EnsureMeta();
            if (_mBDataOwner == null || _pBDataTargetPos == null || _pRefBuildingOwner == null ||
                _pRefEntity == null || _mGetParams == null || _pCollectRadius == null ||
                _mTerrainGetCurrent == null || _pTerrainCells == null)
            {
                return true;
            }

            var colonist = _mBDataOwner.Invoke(__0, null);
            if (colonist == null) return true;

            var ownerRef = _pRefBuildingOwner.GetValue(colonist);
            var building = ownerRef != null ? _pRefEntity.GetValue(ownerRef) as DXEntity : null;
            if (building == null)
            {
                _pBDataTargetPos.SetValue(__0, null);
                __result = IARes.Fail;
                return false;
            }

            object terrainSys = _mTerrainGetCurrent.Invoke(null, null);
            var cells = terrainSys != null ? _pTerrainCells.GetValue(terrainSys) as Array : null;
            object defParams = _mGetParams.Invoke(building, null);
            if (cells == null || defParams == null) return true;

            int radius = (int)_pCollectRadius.GetValue(defParams);
            System.Drawing.Rectangle buildingArea = building.CellArea;
            System.Drawing.Rectangle searchArea = buildingArea.Enlarge(radius);
            int xMin = Math.Max(0, searchArea.X);
            int xMax = Math.Min(DXWorldGrid.NCells - 1, searchArea.Right - 1);
            int yMin = Math.Max(0, searchArea.Y);
            int yMax = Math.Min(DXWorldGrid.NCells - 1, searchArea.Bottom - 1);

            var candidates = new System.Collections.Generic.List<System.Drawing.Point>();
            for (int x = xMin; x <= xMax; x++)
            {
                for (int y = yMin; y <= yMax; y++)
                {
                    if (buildingArea.Contains(x, y)) continue;
                    // ZXTerrainResourceType.Grass == 30
                    if (Convert.ToByte(cells.GetValue(x, y)) != 30) continue;
                    // 2048u == Structure mask (same exclusion check used by CFarmer)
                    if (DXWorldGrid.AnyEntityAtCell(x, y, 2048u)) continue;
                    candidates.Add(new System.Drawing.Point(x, y));
                }
            }

            if (candidates.Count == 0)
            {
                _pBDataTargetPos.SetValue(__0, null);
                __result = IARes.Fail;
                return false;
            }

            System.Drawing.Point chosen = candidates.RandomElement();
            _pBDataTargetPos.SetValue(__0, DXWorldGrid.PointFromCell_Center(chosen));
            __result = IARes.Success;
            return false;
        }
    }

    /// <summary>
    /// Isolates camera-gated visual VFX helpers (#=zg10wm6mM9hhx: footsteps/dust puffs, blood
    /// splatters, hit sparks, explosions, screen shake, bullet tracers), CShotEffect muzzle-flash
    /// visuals (and its one-time static DXProjectLevel asset load), and auxiliary DXProjectLevel.OnLoadData
    /// deserializations from the synced PRNG and entity-ID streams.
    /// </summary>
    [HarmonyPatch]
    public static class VisualVfxIsolationPatch
    {
        public static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var vfxType = AccessTools.TypeByName("#=zg10wm6mM9hhx");
            if (vfxType != null)
            {
                foreach (var m in AccessTools.GetDeclaredMethods(vfxType))
                {
                    if (m != null && !m.IsAbstract && !m.ContainsGenericParameters)
                        yield return m;
                }
            }

            // CShotEffect lazily deserializes a static DXProjectLevel once per process in #=zcv694jLlBMdF()
            // on first OnSceneAdded(), and allocates visual DXProjectObject_Sprite clips.
            var shotType = AccessTools.TypeByName("ZX.Components.CShotEffect");
            if (shotType != null)
            {
                foreach (var m in AccessTools.GetDeclaredMethods(shotType))
                {
                    if (m != null && !m.IsAbstract && !m.ContainsGenericParameters && !m.IsConstructor)
                        yield return m;
                }
            }

            var projLevelLoad = AccessTools.DeclaredMethod(typeof(DXProjectLevel), "OnLoadData");
            if (projLevelLoad != null)
                yield return projLevelLoad;
        }

        [HarmonyPrefix]
        public static void Prefix() => LockstepClock.EnterVisualVfxContext();

        [HarmonyFinalizer]
        public static void Finalizer() => LockstepClock.ExitVisualVfxContext();
    }

    /// <summary>
    /// Propagates deterministic simulation context into deferred callbacks queued via
    /// DXPostMethodManager.Invoke (OnStartFrame, OnFinishFrame, OnSafeLogicAndDrawing,
    /// DXPhysicWorld.PostMethods) when scheduled from a simulation context, so that
    /// callbacks executed later during DXGame.OnRender (e.g. ZXEntity zombie spawns)
    /// stay on the synced RNG stream and synced DXGuid track.
    /// </summary>
    [HarmonyPatch(typeof(DXPostMethodManager), nameof(DXPostMethodManager.Invoke))]
    public static class PostMethodInvokeContextPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DXPostMethodManager __instance, ref Action method)
        {
            if (method == null || !LockstepClock.IsDeterministicModeActive)
                return true;
            if (LockstepClock.LoadingLevel || LockstepClock.InAudioContext || LockstepClock.InVisualVfxContext)
                return true;

            int main = LockstepClock.MainThreadId;
            bool onMain = main != 0 && Thread.CurrentThread.ManagedThreadId == main;
            bool isSimThread = LockstepClock.InDeterministicContext
                || (LockstepClock.LogicTickActive && !onMain)
                || LockstepClock.IsPhysicsThreadActive();
            if (!isSimThread)
                return true;

            Action orig = method;
            System.Threading.Interlocked.Increment(ref DeterministicSandboxPatches.DiagPostWrapCalls);

            // When _TaskLogic runs concurrently with RenderFrame(), defer OnStartFrame/OnFinishFrame/
            // OnSafeLogicAndDrawing callbacks enqueued from the worker thread until WaitForGameLogic
            // finishes so render timing can never change which tick executes a simulation callback.
            var game = DXGame.Current;
            if (DeterministicSandboxPatches.IsAsyncTickInFlight && !onMain && game != null
                && (object.ReferenceEquals(__instance, game.PostMethods_OnStartFrame)
                    || object.ReferenceEquals(__instance, game.PostMethods_OnFinishFrame)
                    || object.ReferenceEquals(__instance, game.PostMethods_OnSafeLogicAndDrawing)))
            {
                DeterministicSandboxPatches.EnqueueDeferredAsyncPostMethod(orig);
                return false;
            }

            method = () => LockstepClock.RunInDeterministicContext(orig);
            return true;
        }
    }

    /// <summary>
    /// Prevents DXGame.OnRender / FinishFrame from draining PostMethods_OnFinishFrame (or
    /// StartFrame/SafeLogic) on the main thread while an async _TaskLogic tick is in flight,
    /// e.g. callbacks queued synchronously by FlushDueTurnCommands just prior to launching _TaskLogic.
    /// WaitForGameLogic_Postfix drains all three queues deterministically once the worker completes.
    /// </summary>
    [HarmonyPatch(typeof(DXPostMethodManager), nameof(DXPostMethodManager.ExecuteAll))]
    public static class PostMethodExecuteAsyncGuardPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(DXPostMethodManager __instance)
        {
            if (!DeterministicSandboxPatches.IsAsyncTickInFlight)
                return true;

            var game = DXGame.Current;
            if (game != null && (object.ReferenceEquals(__instance, game.PostMethods_OnStartFrame)
                || object.ReferenceEquals(__instance, game.PostMethods_OnFinishFrame)
                || object.ReferenceEquals(__instance, game.PostMethods_OnSafeLogicAndDrawing)))
            {
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Marks LoadingLevel = true right when ZXGame.LoadGameAndEnter begins loading a .zxsav file,
    /// allowing menu Co-op sessions to defer LoadingLevel until an actual save load occurs (so
    /// New Survival map generation creates normal tracked entity IDs rather than 0x8000... UI IDs).
    /// </summary>
    [HarmonyPatch]
    public static class LoadGameLoadingFlagPatch
    {
        private static MethodBase TargetMethod() =>
            AccessTools.Method(GameBridge.TypeZXGame, ObfuscatedSymbols.Method_ZXGame_LoadGameAndEnter, new[] { typeof(string) });

        [HarmonyPrefix]
        public static void Prefix()
        {
            if (LockstepClock.IsDeterministicModeActive)
                LockstepClock.LoadingLevel = true;
        }
    }

    /// <summary>
    /// Isolates CSprite.#=zykGuoNdyOv10tsMpy8FzNkE=() (physics-push animation/movement mode sync)
    /// from render-time UpdateSceneObjectParallel, which only runs for entities inside the local
    /// camera viewport when their sprite atlas frame advances. Instead, we run it deterministically
    /// once per physics step for all entities with CanBePulled == true.
    /// </summary>
    [HarmonyPatch]
    public static class CSpritePullIsolationPatch
    {
        private static Type _cspriteType;
        private static int _cspriteTypeId;
        private static MethodInfo _pullMethod;
        private static MethodInfo _isPulledGetter;
        private static PropertyInfo _paramsProp;
        private static PropertyInfo _canBePulledProp;
        private static Action<object> _fastPull;
        private static Func<object, bool> _fastIsPulled;
        private static bool _fastCompiled;
        private static readonly HashSet<DXEntity> _activePullEntities = new HashSet<DXEntity>();
        private static DXLevel _lastColdScanLevel;
        private static readonly System.Collections.Generic.Dictionary<Type, bool> _canPullCache =
            new System.Collections.Generic.Dictionary<Type, bool>();

        private static MethodBase TargetMethod()
        {
            _cspriteType = AccessTools.TypeByName("ZX.Components.CSprite");
            if (_cspriteType == null) return null;
            _cspriteTypeId = DXComponent.GetIDFromType(_cspriteType);
            _paramsProp = AccessTools.Property(_cspriteType, "Params");
            _pullMethod = AccessTools.Method(_cspriteType, "#=zykGuoNdyOv10tsMpy8FzNkE=");
            _isPulledGetter = AccessTools.Property(_cspriteType, "IsPulled")?.GetGetMethod(true);
            return _pullMethod;
        }

        private static void EnsureFastDelegates()
        {
            if (_fastCompiled) return;
            _fastCompiled = true;
            try
            {
                if (_cspriteType == null || _pullMethod == null || _isPulledGetter == null) return;

                var dmPull = new DynamicMethod("TABCoop_FastSpritePull", typeof(void), new[] { typeof(object) },
                    typeof(CSpritePullIsolationPatch).Module, skipVisibility: true);
                var ilP = dmPull.GetILGenerator();
                ilP.Emit(OpCodes.Ldarg_0);
                ilP.Emit(OpCodes.Castclass, _cspriteType);
                ilP.Emit(OpCodes.Callvirt, _pullMethod);
                ilP.Emit(OpCodes.Ret);
                _fastPull = (Action<object>)dmPull.CreateDelegate(typeof(Action<object>));

                var dmIs = new DynamicMethod("TABCoop_FastSpriteIsPulled", typeof(bool), new[] { typeof(object) },
                    typeof(CSpritePullIsolationPatch).Module, skipVisibility: true);
                var ilI = dmIs.GetILGenerator();
                ilI.Emit(OpCodes.Ldarg_0);
                ilI.Emit(OpCodes.Castclass, _cspriteType);
                ilI.Emit(OpCodes.Callvirt, _isPulledGetter);
                ilI.Emit(OpCodes.Ret);
                _fastIsPulled = (Func<object, bool>)dmIs.CreateDelegate(typeof(Func<object, bool>));

                ModLogger.Info("[Perf] Compiled DynamicMethods TABCoop_FastSpritePull / IsPulled.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[Perf] FastSpritePull compile failed (falling back to reflection): {ex.Message}");
                _fastPull = null;
                _fastIsPulled = null;
            }
        }

        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !LockstepClock.IsDeterministicModeActive || LockstepClock.AllowCSpritePullUpdate;
        }

        public static void RunDeterministicPullUpdates(DXGame game)
        {
            var bodies = DXPhysicWorld.Bodies;
            if (bodies == null || _pullMethod == null || _cspriteType == null)
                return;

            EnsureFastDelegates();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int calls = 0;

            var curLevel = DXLevel.Current;
            bool coldScan = !object.ReferenceEquals(_lastColdScanLevel, curLevel);
            if (coldScan)
            {
                _lastColdScanLevel = curLevel;
                _activePullEntities.Clear();
            }
            else if (_activePullEntities.Count > 0)
            {
                _activePullEntities.RemoveWhere(ent => ent == null || ent.Disposed);
            }

            LockstepClock.AllowCSpritePullUpdate = true;
            try
            {
                int bodyCount = bodies.Count;
                for (int i = 0; i < bodyCount; i++)
                {
                    var body = bodies[i];
                    var e = body?.GameEntity;
                    if (e == null || e.Disposed || e.PhysicalType == DXBodyType.Static)
                        continue;

                    if (!coldScan)
                    {
                        var v = body.Velocity;
                        if (v.X * v.X + v.Y * v.Y <= 0.04f && !_activePullEntities.Contains(e))
                            continue;
                    }

                    Type entType = e.GetType();
                    bool knownCanPull;
                    if (_canPullCache.TryGetValue(entType, out knownCanPull) && !knownCanPull)
                        continue;

                    var csprite = e.GetComponent(_cspriteTypeId);
                    if (csprite == null)
                    {
                        _canPullCache[entType] = false;
                        continue;
                    }

                    if (!_canPullCache.ContainsKey(entType))
                    {
                        var p = _paramsProp?.GetValue(csprite);
                        if (p == null) continue;
                        if (_canBePulledProp == null)
                            _canBePulledProp = AccessTools.Property(p.GetType(), "CanBePulled");
                        knownCanPull = (bool)(_canBePulledProp?.GetValue(p) ?? false);
                        _canPullCache[entType] = knownCanPull;
                        if (!knownCanPull) continue;
                    }

                    if (_fastPull != null)
                        _fastPull(csprite);
                    else
                        _pullMethod.Invoke(csprite, null);

                    bool nowPulled = _fastIsPulled != null
                        ? _fastIsPulled(csprite)
                        : (_isPulledGetter != null && (bool)_isPulledGetter.Invoke(csprite, null));
                    if (nowPulled)
                        _activePullEntities.Add(e);
                    else
                        _activePullEntities.Remove(e);

                    calls++;
                }
            }
            finally
            {
                LockstepClock.AllowCSpritePullUpdate = false;
                DeterministicSandboxPatches.DiagTickPullSyncTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                DeterministicSandboxPatches.DiagTickPullSyncCalls += calls;
            }
        }
    }

    /// <summary>
    /// Prevents DXGame._TaskGameTime background thread from mutating DXGame._GameTime and
    /// DXGame.GameTimeInt asynchronously while deterministic mode is active. Since .NET JIT
    /// inlines DXGame.get_GameTimeInt() across assemblies, freezing the backing field itself
    /// ensures CMovable, CSprite, and ZXEntity read identical tick timestamps across peers.
    /// </summary>
    [HarmonyPatch(typeof(DXGame), nameof(DXGame.GameTime), MethodType.Setter)]
    public static class GameTimeIsolationPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            if (!LockstepClock.IsDeterministicModeActive || LockstepClock.LoadingLevel)
                return true;
            return LockstepClock.AllowRawGameTimeSet;
        }
    }

    /// <summary>
    /// Allows explicit engine clock resets via DXGame.SetGameTime(double) — e.g. when starting a
    /// New Survival or Custom Map after returning from a previous match — so GameTime/IAIterationInt
    /// do not inherit the previous match's colony days while GameTimeIsolationPatch is active.
    /// </summary>
    [HarmonyPatch(typeof(DXGame), nameof(DXGame.SetGameTime), new[] { typeof(double) })]
    public static class GameSetGameTimePatch
    {
        [HarmonyPrefix]
        public static void Prefix(out bool __state)
        {
            __state = LockstepClock.AllowRawGameTimeSet;
            LockstepClock.AllowRawGameTimeSet = true;
        }

        [HarmonyPostfix]
        public static void Postfix(double gameTime, bool __state)
        {
            LockstepClock.AllowRawGameTimeSet = __state;
            if (LockstepClock.IsDeterministicModeActive)
            {
                DXGame.LastGameTimeIAUpdate = gameTime;
                DXPhysicWorld.LastGameTimeUpdatePhysics = gameTime;
            }
        }
    }

    /// <summary>
    /// Recreates the native Chipmunk2D cpSpace inside DXPhysicWorld.Clear() when deterministic mode
    /// is active. DXPhysicWorld.Init() normally runs only once per process, so cpSpace->shapeIDCounter,
    /// cpBBTree leaf/node pools, and cached arbiters carry across reloads/Auto-resync and alter
    /// cpBBTree query traversal and Gauss-Seidel arbiter ordering between Host and Client.
    /// </summary>
    [HarmonyPatch(typeof(DXPhysicWorld), nameof(DXPhysicWorld.Clear))]
    public static class DXPhysicWorldClearPatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (!LockstepClock.IsDeterministicModeActive || !DXPhysicWorld.Initialized)
                return;

            lock (DXPhysicWorld.Sync)
            {
                long prevIdCnt = DXPhysicWorld.Handle != IntPtr.Zero
                    ? System.Runtime.InteropServices.Marshal.ReadInt64(DXPhysicWorld.Handle, 0x58)
                    : -1;
                DXPhysicWorld.Init(10);
                DXPool.Clear();
                ModLogger.Info($"[LockstepClock] DXPhysicWorld.Clear -> reset cpSpace and DXPool (prevPhysIdCnt={prevIdCnt}, newSpace=0x{DXPhysicWorld.Handle.ToInt64():X}).");
            }
        }
    }

    /// <summary>
    /// Ensures pooled DXEntity instances (such as Proyectile) acquire a fresh deterministic
    /// DXGuid.New() on pool reuse just like newly constructed instances, eliminating
    /// render-frame pool hit/miss divergence across reloads and differing client framerates.
    /// </summary>
    [HarmonyPatch(typeof(DXEntity), nameof(DXEntity.OnPoolNew))]
    public static class DXEntityOnPoolNewPatch
    {
        [HarmonyPrefix]
        public static void Prefix(DXEntity __instance)
        {
            if (LockstepClock.IsDeterministicModeActive && __instance != null)
                __instance.ID = 0L;
        }
    }

    /// <summary>
    /// Scopes behaviour RNG draws inside ZXBehaviour.Run() to a stateless per-entity SplitMix64
    /// sequence keyed by (SyncedRandomSeed, SimTick, Entity.ID, drawIndex). Because every entity
    /// receives an independent RNG stream, an extra draw by one unit or zombie can never shift
    /// the behaviour random numbers of any other entity within the same simulation tick.
    /// </summary>
    [HarmonyPatch]
    public static class ZXBehaviourRunRngScopePatch
    {
        private static bool _metaInit;
        private static Type _tBehaviour;
        private static Type _tBData;
        private static MethodInfo _mGetData;
        private static MethodInfo _mGetOwner;
        private static Func<object, ulong> _fastGetEntityId;

        private static MethodBase TargetMethod()
        {
            var tBehav = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXBehaviour);
            if (tBehav == null) return null;
            EnsureMeta();
            return AccessTools.Method(tBehav, "Run", Type.EmptyTypes);
        }

        private static void EnsureMeta()
        {
            if (_metaInit) return;
            _metaInit = true;
            try
            {
                var asm = Core.GameBridge.GameAssembly;
                if (asm == null) return;

                _tBehaviour = AccessTools.TypeByName(Core.ObfuscatedSymbols.Type_ZXBehaviour);
                _tBData = asm.GetType("ZX.Behaviours.BData");
                if (_tBehaviour == null || _tBData == null) return;

                _mGetData = _tBehaviour.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy)?.GetGetMethod(true);
                FieldInfo fOwner = _tBData.GetField("#=zIuCqHApzmkOGXXyxMg==", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _mGetOwner = AccessTools.Method(_tBData, Core.ObfuscatedSymbols.Method_BData_GetOwner, Type.EmptyTypes);
                MethodInfo mGetEntityId = typeof(DXEntity).GetProperty("ID", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);

                if (_mGetData != null && fOwner != null && mGetEntityId != null)
                {
                    var dm = new DynamicMethod("TABCoop_GetBehavOwnerEntityId", typeof(ulong), new[] { typeof(object) },
                        typeof(ZXBehaviourRunRngScopePatch).Module, skipVisibility: true);
                    var il = dm.GetILGenerator();
                    var locData = il.DeclareLocal(_tBData);
                    var locOwner = il.DeclareLocal(typeof(DXEntity));
                    var lblZero = il.DefineLabel();

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Brfalse_S, lblZero);

                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _tBehaviour);
                    il.Emit(OpCodes.Callvirt, _mGetData);
                    il.Emit(OpCodes.Stloc, locData);
                    il.Emit(OpCodes.Ldloc, locData);
                    il.Emit(OpCodes.Brfalse_S, lblZero);

                    il.Emit(OpCodes.Ldloc, locData);
                    il.Emit(OpCodes.Ldfld, fOwner);
                    il.Emit(OpCodes.Stloc, locOwner);
                    il.Emit(OpCodes.Ldloc, locOwner);
                    il.Emit(OpCodes.Brfalse_S, lblZero);

                    il.Emit(OpCodes.Ldloc, locOwner);
                    il.Emit(OpCodes.Callvirt, mGetEntityId);
                    il.Emit(OpCodes.Ret);

                    il.MarkLabel(lblZero);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Conv_I8);
                    il.Emit(OpCodes.Ret);

                    _fastGetEntityId = (Func<object, ulong>)dm.CreateDelegate(typeof(Func<object, ulong>));
                    ModLogger.Info("[Perf] Compiled DynamicMethod TABCoop_GetBehavOwnerEntityId.");
                }
            }
            catch (Exception ex)
            {
                _fastGetEntityId = null;
                ModLogger.Warn($"[LockstepClock] ZXBehaviourRunRngScopePatch meta init failed (using reflection): {ex.Message}");
            }
        }

        private static ulong ResolveEntityId(object behaviour)
        {
            if (behaviour == null) return 0UL;
            if (!_metaInit) EnsureMeta();
            if (_fastGetEntityId != null)
                return _fastGetEntityId(behaviour);

            object data = _mGetData?.Invoke(behaviour, null);
            if (data == null) return 0UL;
            var owner = _mGetOwner?.Invoke(data, null) as DXEntity;
            return owner != null ? owner.ID : 0UL;
        }

        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            if (!LockstepClock.IsDeterministicModeActive) return;
            LockstepClock.EnterEntityBehaviourRngScope(ResolveEntityId(__instance));
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            LockstepClock.ExitEntityBehaviourRngScope();
        }
    }

    /// <summary>
    /// Prevents the vanilla 6s New Game intro cinematic (5x camera zoom-out, RootObject blur, and
    /// +300/-300*Unit HUD slide animation) from triggering during active Co-op or deterministic replay
    /// sessions, including during the brief pre-ShowScene window when InitLevel enables CurrentGameSystem.
    /// </summary>
    [HarmonyPatch]
    public static class DisableIntroCinematicInCoopPatch
    {
        public static MethodBase TargetMethod() =>
            AccessTools.Method(Core.GameBridge.TypeZXSystemGameLevel, "OnSetCamera");

        [HarmonyPrefix]
        public static void Prefix(object __instance)
        {
            if (!Sync.CoopSessionManager.IsSessionActive && !LockstepClock.IsDeterministicModeActive)
                return;
            Core.GameBridge.EnforceDisabledCameraIntroForLevel(__instance);
        }
    }
}
