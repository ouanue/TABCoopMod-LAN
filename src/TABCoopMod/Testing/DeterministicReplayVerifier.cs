using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Loads a save, runs a scripted command scenario for N ticks while recording a per-tick
    /// StateHash timeline, then reloads the same save and repeats. If both timelines are identical
    /// the simulation is deterministic for that scenario. Supports an in-process double run and a
    /// two-process fallback (TABCOOP_REPLAY_RUN=1|2) when the engine cannot reload in place.
    /// </summary>
    public static class DeterministicReplayVerifier
    {
        private const int ReplaySeed = 20261109;       // identical for both runs
        private const int ReloadWatchdogFrames = 300;

        private enum Phase { Idle, AwaitRun1, Running, AwaitReload2, AwaitRun2, Done }

        private static Phase _phase = Phase.Idle;
        private static int _currentRun;
        private static int _baselineTick;
        private static int _ticks;
        private static string _savePath;
        private static string _scenarioPath;
        private static string _run1Path;
        private static string _run2Path;
        private static string _resultPath;

        private static ScriptedCommandDriver _driver;
        private static readonly List<string> _records = new List<string>();
        private static int _reloadWatchFrames;
        private static bool _reloadPending;
        private static int _shotDueCount = -1;
        private static int _armTick = -1;
        private static int _lastEntityCount = -1;
        private static int _lastRecordedTick = -1;
        private static int _settleStreak;
        private const int SettleStreakTicks = 10;
        private const int SettleGiveUpTicks = 600;

        public static bool IsRunning => _phase == Phase.Running;

        public static void Begin()
        {
            if (!TestHarnessConfig.Enabled || string.IsNullOrEmpty(TestHarnessConfig.ReplaySave) || _phase != Phase.Idle)
                return;

            // Co-op host/client sessions also auto-load a save, but must not run the single-process
            // replay verifier (it would reload the level for its second pass mid-session).
            if (string.Equals(TestHarnessConfig.Role, "host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(TestHarnessConfig.Role, "client", StringComparison.OrdinalIgnoreCase))
                return;

            _savePath = TestHarnessConfig.ReplaySave;
            _ticks = TestHarnessConfig.ReplayTicks;
            _scenarioPath = string.Equals(TestHarnessConfig.Scenario, "default", StringComparison.OrdinalIgnoreCase)
                ? null : TestHarnessConfig.Scenario;

            string dir = TestHarnessConfig.OutputDirectory;
            _run1Path = Path.Combine(dir, "coop_replay_run1.timeline");
            _run2Path = Path.Combine(dir, "coop_replay_run2.timeline");
            _resultPath = Path.Combine(dir, "coop_replay_result.txt");

            _phase = TestHarnessConfig.ReplayRun == 2 ? Phase.AwaitRun2 : Phase.AwaitRun1;
            ModLogger.Info($"[ReplayVerifier] ARMED. Waiting for level init. save={_savePath}, ticks={_ticks}, runMode={TestHarnessConfig.ReplayRun}");
        }

        /// <summary>
        /// Must be called BEFORE LoadSaveFileAndEnter: entities/random draws produced during the
        /// save load itself must already use the synced GUID counter and seeded RNG, otherwise the
        /// loaded initial state differs between processes and the very first hash diverges.
        /// </summary>
        public static void PrepareForLoad()
        {
            if (!TestHarnessConfig.Enabled) return;
            try
            {
                if (!Sync.CoopSessionManager.IsSessionActive)
                    LockstepClock.TickGate = new AlwaysReadyTickGate();
                LockstepClock.EnableDeterministicSandbox(ReplaySeed);
                // Defer ID calibration until the async loader reports completion: at this point the
                // old level's entities are still live and the save's GameTime is not restored yet.
                LockstepClock.RequestPostLoadCalibration(ReplaySeed);
                LockstepClock.ResetBehaviourRandom(ReplaySeed);
                ModLogger.Info("[ReplayVerifier] Deterministic sandbox enabled BEFORE load.");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] PrepareForLoad failed", ex);
            }
        }

        /// <summary>Called from the level InitLevel postfix.</summary>
        public static void OnLevelInitialized()
        {
            if (_phase == Phase.AwaitRun1) StartRun(1);
            else if (_phase == Phase.AwaitRun2) StartRun(2);
        }

        private static void StartRun(int run)
        {
            try
            {
                _currentRun = run;
                ResetStaticsForRun();

                // Baseline tick is captured lazily on the first real logic tick, because the save's
                // IAIterationInt is restored only after InitLevel returns. LoadingLevel stays true
                // until LevelLoadCompletePatch fires.
                _baselineTick = -1;
                _driver = null;
                _armTick = -1;
                _lastEntityCount = -1;
                _lastRecordedTick = -1;
                _settleStreak = 0;
                _reloadPending = false;
                _shotDueCount = -1;

                // NOTE: do NOT re-enable the sandbox or reseed RNG here. InitLevel runs on the
                // loader's background thread while the save is still being deserialized; resetting
                // the DXRandom counter at this nondeterministic moment makes entities constructed
                // afterwards draw different seeds in each process. PrepareForLoad() already enabled
                // the sandbox and LevelLoadCompletePatch re-seeds at a deterministic point.
                LockstepClock.TickGate = new AlwaysReadyTickGate();

                _records.Clear();
                _phase = Phase.Running;

                ModLogger.Info($"[ReplayVerifier] RUN {run} armed; waiting for first logic tick (target {_ticks} ticks).");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ReplayVerifier] StartRun({run}) failed", ex);
                _phase = Phase.Done;
            }
        }

        public static void OnBeforeLogicTick(int nextSimTick)
        {
            if (_phase != Phase.Running) return;
            // Never capture a baseline (or record) against a level that is still being loaded.
            if (LockstepClock.LoadingLevel) return;

            if (_baselineTick < 0)
            {
                if (_armTick < 0) _armTick = nextSimTick;

                int total = CountEntities();
                if (total > 0 && total == _lastEntityCount) _settleStreak++;
                else { _settleStreak = 0; _lastEntityCount = total; }

                int sinceArm = _armTick >= 0 ? nextSimTick - _armTick : 0;
                // Align both processes on the same ABSOLUTE tick: first multiple of 100 at least
                // 50 ticks after level init. Slightly different load timing still lands on the same
                // tick, so the two timelines are directly comparable.
                bool aligned = total > 0 && sinceArm >= 50 && (nextSimTick % 100 == 0);
                bool giveUp = sinceArm >= SettleGiveUpTicks;
                if (!aligned && !giveUp) return;

                EstablishBaseline(nextSimTick, aligned);
            }

            _driver?.OnBeforeLogicTick(nextSimTick);
        }

        /// <summary>
        /// Locks in the run's start tick. Prefer calling this from the level-load-completion postfix:
        /// right then the entity set/positions are byte-identical on both processes (loadPosHash
        /// matched in testing). Waiting for a "settled" absolute tick instead lets a different number
        /// of physics frames run during the scene-initialization window, which moved zombies apart.
        /// </summary>
        private static void EstablishBaseline(int tick, bool aligned)
        {
            _baselineTick = tick;
            // Everything (entities, GameTime, IAIterationInt) is restored by now: recalibrate the
            // synced ID counter above the save's max ID, fix update order and zero-phase both
            // clocks so both runs start from an identical state.
            LockstepClock.CalibrateEntityIdCounterFromLoadedLevel(ReplaySeed);
            GameBridge.SortEntitiesById();
            GameBridge.SeedLevelRandom(ReplaySeed);
            LockstepClock.ResetBehaviourRandom(ReplaySeed);
            DXWorldGrid.ResetPathsCached();
            GameBridge.AlignSimulationClocks();
            _driver = string.IsNullOrEmpty(_scenarioPath)
                ? ScriptedCommandDriver.CreateDefault(_baselineTick)
                : ScriptedCommandDriver.FromFile(_scenarioPath);

            int players = GameBridge.GetPlayerEntities().Count;
            var cc = GameBridge.GetCommandCenterEntity();
            ulong baseHash = ComputePositionHash();
            try { File.WriteAllText(Path.Combine(TestHarnessConfig.OutputDirectory, $"coop_baselinepos_run{_currentRun}.txt"), $"0x{baseHash:X16}"); } catch { }
            ModLogger.Info($"[ReplayVerifier] RUN {_currentRun} baseline tick {_baselineTick}: entities={CountEntities()}, players={players}, cc={(cc != null ? cc.ID.ToString() : "none")}, ops={_driver.Count}, aligned={aligned}, basePosHash=0x{baseHash:X16}");
        }

        private static int CountEntities()
        {
            int total = 0;
            if (DXGame.Current?.Entities != null)
                foreach (var _ in DXGame.Current.Entities) total++;
            return total;
        }

        private static void DumpPositions(string suffix = null)
        {
            try
            {
                var sb = new StringBuilder(1 << 20);
                int n = 0;
                if (DXGame.Current?.Entities != null)
                {
                    foreach (var e in DXGame.Current.Entities)
                    {
                        if (e == null || e.Disposed) continue;
                        if ((e.ID & 0x8000000000000000UL) != 0) continue;
                        if (e.GetType() == typeof(DXEntity)) continue;
                        sb.Append(e.ID).Append('|')
                          .Append(e.Position.X.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
                          .Append(e.Position.Y.ToString("F4", CultureInfo.InvariantCulture)).Append('|')
                          .Append(e.GetType().Name).Append('\n');
                        n++;
                    }
                }
                string name = suffix == null ? $"coop_pos_run{_currentRun}.txt" : $"coop_pos_run{_currentRun}_{suffix}.txt";
                string path = Path.Combine(TestHarnessConfig.OutputDirectory, name);
                File.WriteAllText(path, sb.ToString());
                ModLogger.Info($"[ReplayVerifier] Dumped {n} entity positions -> {path}");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] DumpPositions failed", ex);
            }
        }
        /// <summary>
        /// At the first recorded tick, dump positions plus a screenshot and the camera transform so a
        /// report can box divergent entities directly on the image (see scripts/build_divergence_report.py).
        /// </summary>
        private static void DumpSnapshot()
        {
            DumpPositions();

            if (TestHarnessConfig.RevealMap)
            {
                bool ok = GameBridge.RevealAll(true);
                ModLogger.Info($"[ReplayVerifier] reveal map (instant): {ok}");
            }

            // Optional camera override (TABCOOP_SHOT_CAM) lets the report look at the interaction
            // area instead of the save's restored camera. Apply now; capture a couple of ticks later
            // so the render target has been redrawn with the new camera.
            if (!string.IsNullOrEmpty(TestHarnessConfig.ShotCamera) && CameraProbe.ApplySpec(TestHarnessConfig.ShotCamera))
            {
                _shotDueCount = _records.Count + 2;
                ModLogger.Info($"[ReplayVerifier] camera override '{TestHarnessConfig.ShotCamera}' applied; screenshot due at record {_shotDueCount}.");
                return;
            }

            CaptureShot();
        }

        private static void MaybeCaptureDeferredShot()
        {
            if (_shotDueCount < 0 || _records.Count < _shotDueCount) return;
            _shotDueCount = -1;
            CaptureShot();
        }

        private static void CaptureShot()
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;

                string dir = !string.IsNullOrEmpty(TestHarnessConfig.ShotDir)
                    ? TestHarnessConfig.ShotDir
                    : Path.Combine(TestHarnessConfig.OutputDirectory, "tabcoop_shots");
                System.IO.Directory.CreateDirectory(dir);

                int tick = game.IAIterationInt;
                string baseName = $"replay_run{_currentRun}_t{tick}_{DateTime.Now:yyyyMMdd_HHmmss_fff}";
                string shotPath = Path.Combine(dir, baseName + ".png");
                string camPath = Path.Combine(dir, baseName + ".cam.json");

                using (var bmp = game.CaptureScreen())
                {
                    if (bmp == null) return;
                    bmp.Save(shotPath, ImageFormat.Png);
                    CameraProbe.WriteJson(camPath, bmp.Width, bmp.Height);
                    ModLogger.Info($"[ReplayVerifier] Saved run{_currentRun} screenshot {bmp.Width}x{bmp.Height} -> {shotPath}; camera -> {camPath}");
                }

                UpdateShotManifest(dir, tick, shotPath, camPath);
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] CaptureShot failed", ex);
            }
        }

        /// <summary>Writes/merges tabcoop_shots/latest_replay_shots.json so the report can auto-discover
        /// the uniquely-named shots of each pass (no fixed-name overwrites, no stale browser cache).</summary>
        private static void UpdateShotManifest(string dir, int tick, string shotPath, string camPath)
        {
            try
            {
                string path = Path.Combine(dir, "latest_replay_shots.json");
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                Dictionary<string, object> root = null;
                // Start a fresh manifest on run1 so two-pass pairs never mix with a prior session;
                // run2 merges into the run1 entry of the same session.
                if (_currentRun != 1 && File.Exists(path))
                {
                    try { root = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path)); } catch { }
                }
                if (root == null) root = new Dictionary<string, object>();
                root["tick"] = tick;
                root["run" + _currentRun] = new Dictionary<string, object>
                {
                    ["shot"] = shotPath,
                    ["cam"] = camPath
                };
                File.WriteAllText(path, ser.Serialize(root));
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[ReplayVerifier] manifest update failed: {ex.Message}");
            }
        }

        public static void OnAfterLogicTick(int tick)
        {
            if (_phase != Phase.Running || _baselineTick < 0) return;
            if (LockstepClock.LoadingLevel) return;

            // Guarantee the logic Task has finished regardless of patch ordering vs the clock patch.
            try { AccessTools.Method(typeof(DXGame), "WaitForGameLogic")?.Invoke(DXGame.Current, null); } catch { }

            int curTick = DXGame.Current?.IAIterationInt ?? tick;
            if (curTick <= _lastRecordedTick) return;
            _lastRecordedTick = curTick;

            try
            {
                var d = StateHasher.ComputeDigest();
                var rec = new ReplayTickRecord
                {
                    Run = _currentRun,
                    Tick = d.SimTick,
                    Hash = d.FullHash,
                    Gold = d.Gold, Wood = d.Wood, Stone = d.Stone, Iron = d.Iron, Oil = d.Oil,
                    EntityCount = d.EntityCount,
                    PosChecksum = d.PosChecksum,
                    LifeChecksum = d.LifeChecksum,
                    ProjectileChecksum = d.ProjectileChecksum,
                    ProductionChecksum = d.ProductionChecksum,
                    ReservationChecksum = d.ReservationChecksum
                };
                _records.Add(rec.ToJson());

                if (TestHarnessConfig.PosDump && _records.Count == 1)
                    DumpSnapshot();

                MaybeCaptureDeferredShot();

                if (d.SimTick >= _baselineTick + _ticks)
                    FinishRun();
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] hash/record failed", ex);
            }
        }

        private static void FinishRun()
        {
            try
            {
                string path = _currentRun == 1 ? _run1Path : _run2Path;
                File.WriteAllLines(path, _records);
                ModLogger.Info($"[ReplayVerifier] RUN {_currentRun} finished: {_records.Count} records -> {path}");

                if (TestHarnessConfig.PosDump)
                    DumpPositions("end");

                if (TestHarnessConfig.ReplayRun == 1 || TestHarnessConfig.ReplayRun == 2)
                {
                    _phase = Phase.Done;
                    ModLogger.Info($"[ReplayVerifier] SINGLE-PASS RUN {_currentRun} DONE. Compare externally: {path}");
                    return;
                }

                if (_currentRun == 1)
                {
                    _phase = Phase.AwaitRun2;
                    _reloadPending = true;
                    _reloadWatchFrames = 0;
                    ModLogger.Info("[ReplayVerifier] Reloading save for RUN 2...");
                    DXGame.Current.InvokeOnStartFrame(() =>
                    {
                        try
                        {
                            PrepareForLoad(); // re-arm deterministic sandbox + defer ID calibration
                            GameBridge.PrepareForProgrammaticLoad();
                            GameBridge.LoadSaveFileAndEnter(_savePath);
                        }
                        catch (Exception ex) { ModLogger.Error("[ReplayVerifier] Reload for RUN 2 failed", ex); FallbackRequired(); }
                    });
                }
                else
                {
                    Compare();
                    _phase = Phase.Done;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReplayVerifier] FinishRun failed", ex);
                _phase = Phase.Done;
            }
        }

        private static void Compare()
        {
            var a = ReplayTimeline.ParseFile(_run1Path);
            var b = ReplayTimeline.ParseFile(_run2Path);
            var result = ReplayTimeline.Compare(a, b);

            string line = $"ReplayVerifier {(result.Pass ? "PASS" : "FAIL")} :: {result}";
            try { File.WriteAllText(_resultPath, line + Environment.NewLine); } catch { }

            if (result.Pass) ModLogger.Info($"[ReplayVerifier] {line}");
            else ModLogger.Warn($"[ReplayVerifier] {line}");
        }

        private static void FallbackRequired()
        {
            _phase = Phase.Done;
            string line = "ReplayVerifier FALLBACK_REQUIRED :: in-process reload did not re-enter the level. " +
                          "Re-run with TABCOOP_REPLAY_RUN=1 then TABCOOP_REPLAY_RUN=2 in separate processes and compare the two timeline files.";
            try { File.WriteAllText(_resultPath, line + Environment.NewLine); } catch { }
            ModLogger.Warn($"[ReplayVerifier] {line}");
        }

        /// <summary>Per-render-frame hook used only to watchdog the reload.</summary>
        public static void OnRenderFrame()
        {
            if (!_reloadPending || _phase != Phase.AwaitRun2) return;
            _reloadWatchFrames++;
            if (_reloadWatchFrames > ReloadWatchdogFrames)
                FallbackRequired();
        }

        private static void ResetStaticsForRun()
        {
            CommandInterceptor.IsInterceptionEnabled = false;
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            CommandExecutor.IsReplayingCommand = false;
            LockstepClock.LogicTickActive = false;
            LockstepClock.InDeterministicContext = false;

            // NEVER reset LastGameTimeIAUpdate / LastGameTimeUpdatePhysics to 0.0: on a late-game
            // save this makes UpdatePhysics run tens of thousands of steps in a single frame.
            // LevelLoadCompletePatch and the baseline capture align them to GameTime instead.
            try { if (DXGame.Current != null) DXGame.Current.Paused = false; } catch { }
        }

        /// <summary>Called from LevelLoadCompletePatch after the async loader restored the level.</summary>
        public static void OnLevelLoadComplete()
        {
            if (!TestHarnessConfig.Enabled) return;
            if (_phase != Phase.Running && _phase != Phase.AwaitRun2) return;
            ulong loadHash = ComputePositionHash();
            try
            {
                File.WriteAllText(Path.Combine(TestHarnessConfig.OutputDirectory, $"coop_loadpos_run{_currentRun}.txt"),
                    $"0x{loadHash:X16} gt={DXGame.Current?.GameTime:F3}");
            }
            catch { }
            ModLogger.Info($"[ReplayVerifier] Level load complete notified (run={_currentRun}, phase={_phase}); loadPosHash=0x{loadHash:X16}.");

            // Capture the baseline NOW: positions are identical across processes at this instant.
            if (_phase == Phase.Running && _baselineTick < 0)
                EstablishBaseline(LockstepClock.CurrentSimTick, aligned: true);
        }

        private static ulong ComputePositionHash()
        {
            ulong h = 1469598103934665603UL;
            var game = DXGame.Current;
            if (game?.Entities == null) return h;
            foreach (var e in game.Entities)
            {
                if (e == null || e.Disposed) continue;
                if ((e.ID & 0x8000000000000000UL) != 0) continue;
                if (e.GetType() == typeof(DXEntity) || string.Equals(e.GetType().Name, "Raven", StringComparison.Ordinal)) continue;
                ulong id = e.ID;
                ulong x = unchecked((ulong)BitConverter.DoubleToInt64Bits(e.Position.X));
                ulong y = unchecked((ulong)BitConverter.DoubleToInt64Bits(e.Position.Y));
                h = (h ^ id) * 1099511628211UL;
                h = (h ^ x) * 1099511628211UL;
                h = (h ^ y) * 1099511628211UL;
            }
            return h;
        }
    }
}
