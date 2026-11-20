using System;
using System.IO;
using TABCoopMod.Core;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Reads test-harness switches from environment variables (set by the PowerShell launchers).
    /// Everything is opt-in; when disabled the mod behaves exactly as before.
    /// </summary>
    public static class TestHarnessConfig
    {
        public static bool Enabled { get; private set; }
        public static bool Initialized { get; private set; }

        // Level entry / replay
        public static string AutoLoadSave { get; private set; }
        public static string ReplaySave { get; private set; }
        public static int ReplayTicks { get; private set; }
        public static string ReplayOut { get; private set; }
        public static int ReplayRun { get; private set; }       // 0 = both passes in-process, 1/2 = single pass
        public static string TickGate { get; private set; }     // "always" | "manual"
        public static bool Intercept { get; private set; }
        public static string Scenario { get; private set; }     // "default" or path to a script file
        public static string Role { get; private set; }         // "host" | "client" | ""
        public static string ChaosSpec { get; private set; }
        public static bool PosDump { get; private set; }

        /// <summary>When true (TABCOOP_LAN=1), the auto-started host/client session uses the native LAN transport.</summary>
        public static bool LanMode { get; private set; }

        /// <summary>Target host for a LAN client (TABCOOP_LAN_HOST), e.g. "192.168.1.100" or "192.168.1.100:27099".</summary>
        public static string LanHost { get; private set; }

        /// <summary>When > 0, the in-process MCP control server listens on 127.0.0.1:ControlPort.</summary>
        public static int ControlPort { get; private set; }

        /// <summary>Auto-save a PNG after every mutating control tool (default on; TABCOOP_AUTO_SHOT=0 disables).</summary>
        public static bool AutoShot { get; private set; }

        /// <summary>Directory screenshots are written to (TABCOOP_SHOT_DIR). Set by the launcher to the workspace folder.</summary>
        public static string ShotDir { get; private set; }

        /// <summary>Camera override for replay screenshots: "cx,cy" or "cx,cy,w,h" (TABCOOP_SHOT_CAM).</summary>
        public static string ShotCamera { get; private set; }

        /// <summary>Reveal the whole map before replay screenshots (TABCOOP_REVEAL_MAP=1).</summary>
        public static bool RevealMap { get; private set; }

        /// <summary>Optional resync policy override: "strict" | "tolerant" | "async_align" (default: "async_align").</summary>
        public static string ResyncMode { get; private set; }

        /// <summary>Optional resync cooldown override in ms (-1 uses policy default).</summary>
        public static int ResyncCooldownMs { get; private set; }

        public const int DefaultPhysicsQuantScale = 256;

        /// <summary>When true (TABCOOP_DISABLE_ASYNC_TICK=1), forces synchronous single-thread simulation ticks even in co-op.</summary>
        public static bool DisableAsyncTick { get; private set; }

        /// <summary>
        /// When true (TABCOOP_ENABLE_ASYNC_TICK=1), allows the logic tick to be pipelined on the
        /// _TaskLogic continuation thread while RenderFrame/Present runs on MainThread.
        ///
        /// DEFAULT IS FALSE. The overlapped tick runs the engine's UpdateGameLogic body on a
        /// thread-pool continuation that interleaves with the render thread's DXScene pass, so
        /// per-machine thread scheduling decides when entity positions are sampled. Live co-op
        /// sessions then show sub-pixel position divergence within ~60-200 ticks of every segment
        /// start, which snowballs into MissingOnClient and a snapshot rebuild every 1-4 minutes.
        /// Running the tick synchronously keeps physics on the main thread and matches the
        /// replay-deterministic path, at the cost of capping throughput near 12-30 fps on a
        /// late-game 17k-entity save.
        /// </summary>
        public static bool EnableAsyncTick { get; private set; }

        /// <summary>Physics step-end quantization grid inverse step (e.g. 256 => 1/256 cell). 0 disables.</summary>
        public static int PhysicsQuantScale { get; private set; } = DefaultPhysicsQuantScale;

        /// <summary>Optional per-frame floor in ms (TABCOOP_SIM_MIN_FRAME_MS) to simulate lower FPS peers in tests.</summary>
        public static int SimMinFrameMs { get; private set; }

        /// <summary>Optional per-tick delay in ms (TABCOOP_SIM_EXTRA_TICK_MS) to simulate slower CPU tick rate in tests.</summary>
        public static int SimExtraTickMs { get; private set; }

        /// <summary>
        /// When true (TABCOOP_PROJ_DUMP=1), every epoch digest also logs the exact projectile entity-ID
        /// set it hashed, tagged with the epoch's SimTick. Both peers log at the same tick, so the two
        /// lines can be diffed directly to see which projectiles exist on one side only.
        /// </summary>
        public static bool ProjDump { get; private set; }

        /// <summary>
        /// When true (default), DXHelper_Task.For loops that run inside a logic tick are executed
        /// serially so the tick cannot depend on core count / scheduler timing. Set
        /// TABCOOP_SERIALIZE_LOGIC=0 to restore the engine's native parallelism in the logic tick
        /// (higher FPS on very large saves, at the cost of occasional position drift).
        /// Physics loops (start-update and readback) are always serialized regardless of this flag.
        /// </summary>
        public static bool SerializeLogic { get; private set; } = true;

        /// <summary>
        /// DIAGNOSTIC. When greater than zero (TABCOOP_TRACE_FIRST_TICKS=N), each peer computes and
        /// logs a full [TickTrace] digest for the first N ticks after every synced-level entry
        /// instead of only every 60th tick. The two logs then show the exact tick on which the
        /// position checksum first diverges — an immediate difference points at the math/integration
        /// path, a slow accumulation points at the simulation logic. Local logging only; nothing
        /// extra is sent to the peer.
        /// </summary>
        public static int TraceFirstTicks { get; private set; }

        /// <summary>True when the MCP control server is enabled (TABCOOP_CONTROL_PORT set).</summary>
        public static bool ControlEnabled => ControlPort > 0;

        /// <summary>
        /// Sub-precision position alignment (default ON). Drift below the per-type snap thresholds in
        /// GameBridge.GetEntityPositionSnapThreshold (0.50-1.80 cells) is never corrected by the normal
        /// path, so it accumulates until CoarseDiff, then MissingOnClient, then a snapshot rebuild.
        /// Enabling this makes the two peers snap that band exactly (no damping) on a short cooldown.
        /// Set TABCOOP_PRECISION_ALIGN=0 to restore the pre-6.4 behaviour for A/B diagnosis.
        /// </summary>
        public static bool PrecisionAlign { get; private set; } = true;

        /// <summary>
        /// Explicit lower bound in cells for sub-precision alignment (TABCOOP_PRECISION_ALIGN_CELLS).
        /// When unset the floor is derived from PhysicsQuantScale as 2/scale (0.0078 cells at 256).
        /// </summary>
        public static float PrecisionAlignCells { get; private set; }

        public static string OutputDirectory { get; private set; }

        public static void Initialize()
        {
            if (Initialized) return;
            Initialized = true;

            Enabled = Env("TABCOOP_TEST") == "1";

            AutoLoadSave = Env("TABCOOP_AUTO_LOAD_SAVE");
            ReplaySave = Env("TABCOOP_REPLAY_SAVE");
            if (string.IsNullOrEmpty(ReplaySave)) ReplaySave = AutoLoadSave;

            ReplayTicks = ParseInt(Env("TABCOOP_REPLAY_TICKS"), 600);
            ReplayRun = ParseInt(Env("TABCOOP_REPLAY_RUN"), 0);
            TickGate = Env("TABCOOP_TICK_GATE") ?? "always";
            Intercept = Env("TABCOOP_INTERCEPT") == "1";
            Scenario = Env("TABCOOP_SCENARIO") ?? "default";
            Role = Env("TABCOOP_ROLE") ?? string.Empty;
            ChaosSpec = Env("TABCOOP_CHAOS");
            PosDump = Env("TABCOOP_POS_DUMP") == "1";
            LanMode = Env("TABCOOP_LAN") == "1";
            LanHost = Env("TABCOOP_LAN_HOST");
            ControlPort = ParseInt(Env("TABCOOP_CONTROL_PORT"), 0);
            AutoShot = Env("TABCOOP_AUTO_SHOT") != "0";
            ShotDir = Env("TABCOOP_SHOT_DIR");
            ShotCamera = Env("TABCOOP_SHOT_CAM");
            RevealMap = Env("TABCOOP_REVEAL_MAP") == "1";
            ResyncMode = Env("TABCOOP_RESYNC_MODE") ?? "async_align";
            ResyncCooldownMs = ParseInt(Env("TABCOOP_RESYNC_COOLDOWN_MS"), -1);
            DisableAsyncTick = Env("TABCOOP_DISABLE_ASYNC_TICK") == "1";
            EnableAsyncTick = Env("TABCOOP_ENABLE_ASYNC_TICK") == "1";
            PhysicsQuantScale = Math.Max(0, ParseInt(Env("TABCOOP_PHYSICS_QUANT"), DefaultPhysicsQuantScale));
            SimMinFrameMs = Math.Max(0, ParseInt(Env("TABCOOP_SIM_MIN_FRAME_MS"), 0));
            SimExtraTickMs = Math.Max(0, ParseInt(Env("TABCOOP_SIM_EXTRA_TICK_MS"), 0));
            ProjDump = Env("TABCOOP_PROJ_DUMP") == "1";
            SerializeLogic = Env("TABCOOP_SERIALIZE_LOGIC") != "0";
            TraceFirstTicks = Math.Max(0, Math.Min(2000, ParseInt(Env("TABCOOP_TRACE_FIRST_TICKS"), 0)));
            PrecisionAlign = Env("TABCOOP_PRECISION_ALIGN") != "0";
            PrecisionAlignCells = ParseFloat(Env("TABCOOP_PRECISION_ALIGN_CELLS"), 0f);

            string logDir = null;
            try { logDir = Path.GetDirectoryName(ModLogger.LogFilePath); } catch { }
            OutputDirectory = !string.IsNullOrEmpty(logDir) ? logDir : AppDomain.CurrentDomain.BaseDirectory;

            ReplayOut = Env("TABCOOP_REPLAY_OUT");
            if (string.IsNullOrEmpty(ReplayOut))
                ReplayOut = Path.Combine(OutputDirectory, "coop_replay_run" + (ReplayRun > 0 ? ReplayRun.ToString() : "0") + ".timeline");

            if (Enabled)
            {
                ModLogger.Info($"[TestHarness] enabled: replaySave={ReplaySave ?? "(none)"}, ticks={ReplayTicks}, run={ReplayRun}, gate={TickGate}, intercept={Intercept}, scenario={Scenario}, role={Role}, chaos={ChaosSpec ?? "(none)"}, physQuant={PhysicsQuantScale}");
            }
        }

        public static bool HasChaos => !string.IsNullOrEmpty(ChaosSpec);

        private static string Env(string name)
        {
            string v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        private static int ParseInt(string s, int fallback)
        {
            return int.TryParse(s, out int v) ? v : fallback;
        }

        private static float ParseFloat(string s, float fallback)
        {
            return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }
    }
}
