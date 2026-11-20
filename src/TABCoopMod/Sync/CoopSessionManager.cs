using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Networking;
using TABCoopMod.Patches;
using TABCoopMod.Testing;
using TABCoopMod.UI;
using ZX.GUI;

namespace TABCoopMod.Sync
{
    public enum LobbyRoomPhase : byte
    {
        Idle = 0,
        InLobby = 1,
        HostSelectingMenu = 2,
        HostLoadingLevel = 3,
        InGame = 4
    }

    public enum LobbyHostNoticeType : byte
    {
        PhaseChanged = 1,
        ReturnToLobby = 2
    }

    public enum CoopTransportMode
    {
        /// <summary>Steam matchmaking + P2P (requires Steam lobby signalling).</summary>
        Steam = 0,
        /// <summary>127.0.0.1 loopback, for two instances on one machine (with chaos simulation).</summary>
        LocalLoopback = 1,
        /// <summary>Native TCP over the local network; accepts connections from other machines.</summary>
        Lan = 2
    }

    /// <summary>
    /// Orchestrates the Archon Mode Co-op Session:
    /// 1. Initial & Resync Save-Stream Transfer (.zxsav + .zxcheck chunked over Reliable channel)
    /// 2. 30Hz Lockstep Command Turn Synchronization (works both when Running and when Paused!)
    /// 3. Periodic 60-Tick StateHash verification & Auto-Resync trigger
    /// 4. 20Hz Unreliable Ally Cursor & Minimap Camera Viewport broadcast
    /// </summary>
    public static class CoopSessionManager
    {
        public const int ChunkSizeBytes = 32768; // 32 KB per reliable packet
        public const int SyncSeed = 20261109;

        public static INetworkTransport Transport { get; private set; }
        public static bool IsSessionActive => Transport != null && Transport.IsActive;
        public static bool IsInSyncedLevel { get; private set; }
        public static bool IsInSyncBarrier =>
            IsSessionActive && !IsInSyncedLevel &&
            (_hostSnapshotReloadPending || _awaitingSnapshotLoad || _awaitingClientSnapshotAck
             || _pendingClientSnapshotLoadPath != null || _pendingHostSnapshotExport
             || GameBridge.CurrentLevelState != null);
        public static bool IsGameplayInputLocked => IsInSyncBarrier;
        public static int CurrentSessionSeed { get; private set; } = SyncSeed;
        public static LobbyRoomPhase RoomPhase { get; private set; } = LobbyRoomPhase.Idle;
        public static CoopBalancePreset BalancePreset { get; private set; } = CoopBalancePreset.Balanced;
        public static string HostActionHint { get; private set; } = string.Empty;
        public static bool ShouldOpenLobbyOnMainMenu { get; set; }

        // Local loopback network fluctuation simulation (only applies to LocalTcpLoopbackTransport).
        public static bool LocalChaosEnabled { get; private set; }
        public static int LocalChaosLatencyMs { get; private set; } = 80;
        public static int LocalChaosJitterMs { get; private set; } = 20;
        public static double LocalChaosLossRate { get; private set; } = 0.02;
        public static bool IsLocalChaosLockedByEnv => TestHarnessConfig.HasChaos;

        /// <summary>True when the active session runs on a real LAN socket (accepting other machines).</summary>
        public static bool IsLanSession =>
            UnwrapTransport() is LocalTcpLoopbackTransport lan && lan.IsLanBound;

        /// <summary>True for the same-machine 127.0.0.1 dual-instance mode (not LAN).</summary>
        public static bool IsLocalLoopbackSession =>
            UnwrapTransport() is LocalTcpLoopbackTransport lb && !lb.IsLanBound;

        public static INetworkTransport UnwrapTransport(INetworkTransport transport = null)
        {
            var t = transport ?? Transport;
            while (t is ChaosTransport chaos)
                t = chaos.InnerTransport;
            return t;
        }
        private static volatile bool _pendingClientReturnToMainMenu;
        private static volatile bool _suppressReturnNotice;
        private static readonly List<PeerInfo> _lobbyRoster = new List<PeerInfo>();

        public static IReadOnlyList<PeerInfo> LobbyRoster => _lobbyRoster;
        public static event Action LobbyStateChanged;

        public static bool EnableAutoResync = true;
        // Host-authoritative async correction radius. Host positions are valid on the client because
        // static terrain/buildings are identical, so diverged units up to this distance are snapped back.
        public const float MinAsyncPosSnapCells = 0.50f;
        public const float MaxAsyncNudgeCells = 8.0f;
        public const float MaxAsyncTeleportCells = 8.0f;
        public const double AsyncPosAlignCooldownMs = 2000.0;
        public const double AsyncSoftPosAlignCooldownMs = 8000.0;
        public const int EntityPosRealignCooldownTicks = 180;

        // Sub-precision alignment (docs/11). Drift smaller than MinAsyncPosSnapCells is float/physics
        // integration noise rather than steering divergence: the per-type thresholds in GameBridge
        // (0.50-1.80 cells) never fire for it, so nothing corrects it and it accumulates unchecked into
        // CoarseDiff, then MissingOnClient, then a snapshot rebuild every 1-4 minutes. The micro band
        // snaps exactly to the Host sample (no damping) because one write closes a sub-cell gap; the
        // visual ripple is absorbed by RenderVisualInterpolationPatch's snap smoothing.
        public const int MicroAlignCooldownTicks = 24;

        /// <summary>Per-report cap on sub-precision snaps so a huge late-game save cannot stall a frame.</summary>
        public const int MicroAlignMaxUnitsPerReport = 2048;

        internal static bool MicroAlignEnabled => TestHarnessConfig.PrecisionAlign;

        internal static float MicroAlignFloorCells
        {
            get
            {
                float configured = TestHarnessConfig.PrecisionAlignCells;
                if (configured > 0f) return configured;
                int scale = TestHarnessConfig.PhysicsQuantScale;
                return scale > 0 ? 2f / scale : 0.0078f;
            }
        }

        /// <summary>
        /// Drops cooldown entries older than the given window so the per-entity tables cannot grow
        /// without bound on a long session. Cheap: it only runs when the table is large.
        /// </summary>
        private static void PruneCorrectionCooldowns(Dictionary<ulong, int> table, int tick, int windowTicks)
        {
            if (table.Count <= 512) return;
            List<ulong> expired = null;
            foreach (var kv in table)
            {
                int age = tick - kv.Value;
                if (age < 0 || age >= windowTicks)
                {
                    if (expired == null) expired = new List<ulong>(table.Count / 4 + 8);
                    expired.Add(kv.Key);
                }
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
                table.Remove(expired[i]);
        }

        // Per-owner differential alignment (docs/11 Section 1)
        public const int OwnershipLeaseTicks = 300;               // 10s max lease
        public const int OwnershipIdleGraceTicks = 90;            // 3s idle grace after last order
        public const float SelfOwnedHostOverrideThreshold = 3.5f; // Client skips Host soft nudges < 3.5 cells for own units
        public const float ReverseAlignMinThreshold = 0.25f;      // Keep Host/Client owned squad within ~0.2-0.3 cells
        public const int MaxReverseAlignUnitsPerReport = 64;

        public static bool HasVersionMismatch { get; private set; }
        public static string VersionMismatchSummary { get; private set; } = string.Empty;
        private static readonly HashSet<string> _versionMismatchAlertedKeys = new HashSet<string>();
        private static string _pendingVersionMismatchAlert;

        // Tiered reload policy (async_align). Positions / HP / aggro / projectiles are corrected or
        // re-derived continuously without pausing; only irreversible divergence forces a full reload.
        private const int EconReloadStreak = 3;              // ~6s persistent resource/production/time mismatch
        private const int StructuralReloadStreak = 6;        // ~12s entity-count delta beyond tolerance
        private const int MissingOnClientReloadStreak = 4;   // ~8s sustained unspawned/missing Host entities on Client
        private const int WidePosReloadStreak = 10;          // ~15-20s wide residual drift after corrections
        private const double StructuralResyncCooldownMs = 30000.0;
        private const double LongPeriodRealignMs = 300000.0; // 5 min continuous residual divergence
        private static int _consecutiveEconMismatch;
        private static int _consecutiveStructuralMismatch;
        private static int _consecutiveMissingOnClient;
        private static int _consecutiveSubCoarsePosDrift;
        private static double _divergedSinceTicks = -1.0;

        private static void ResetTieredResyncState()
        {
            _consecutiveEconMismatch = 0;
            _consecutiveStructuralMismatch = 0;
            _consecutiveMissingOnClient = 0;
            _consecutiveSubCoarsePosDrift = 0;
            _divergedSinceTicks = -1.0;
        }
        private static double _lastAutoResyncTicks = -100000.0;
        private static double _lastPosSyncSentTicks = -100000.0;
        private static double _lastFullPosAlignSentTicks = -100000.0;
        private static readonly Dictionary<ulong, int> _lastEntityPosCorrectedTick = new Dictionary<ulong, int>();
        // Separate cooldown table for the sub-precision band: those snaps are cheap and frequent, so
        // keeping them out of the normal table is what lets a unit re-drifted past 0.5 cells still be
        // handled by the normal path instead of inheriting a 3 s lockout it never earned.
        private static readonly Dictionary<ulong, int> _lastEntityMicroCorrectedTick = new Dictionary<ulong, int>();
        private static bool _desyncDumped;

        private static string EffectiveResyncMode =>
            string.IsNullOrEmpty(TestHarnessConfig.ResyncMode) ? "async_align" : TestHarnessConfig.ResyncMode.ToLowerInvariant();
        private static bool IsStrictMode => EffectiveResyncMode == "strict";
        private static bool EnableAsyncPosAlign => EffectiveResyncMode == "async_align";
        private static double AutoResyncCooldownMs =>
            TestHarnessConfig.ResyncCooldownMs > 0
                ? TestHarnessConfig.ResyncCooldownMs
                : (IsStrictMode ? 15000.0 : 120000.0);

        private static readonly List<NetCommand> _localOutgoingQueue = new List<NetCommand>();
        private static readonly List<NetCommand> _hostTurnBuffer = new List<NetCommand>();

        // True network lockstep: Host stamps every command with TargetSimTick = simTick + InputDelayTicks
        // and broadcasts a heartbeat for EVERY tick (even empty), so all peers execute the same
        // commands on the same tick regardless of network latency. InputDelayTicks adapts cleanly
        // between 1..4 ticks based on smoothed peer RTT.
        public const int MinInputDelayTicks = 2;
        public const int MaxInputDelayTicks = 4;
        public static int InputDelayTicks { get; private set; } = 2;
        public static double CurrentSmoothedRttMs { get; private set; }
        private static readonly SortedDictionary<int, List<NetCommand>> _pendingTurnCommands =
            new SortedDictionary<int, List<NetCommand>>();
        private static TurnInputTickGate _networkGate = new TurnInputTickGate();
        private static int _hostLastBroadcastSimTick = -1;
        private static int _hostLastBroadcastTargetTick = -1;
        private static uint _rttPingSeq;
        private static double _lastRttProbeTicks = -100000.0;
        private static double _lastDelayAdjustTicks = -100000.0;
        private static readonly Dictionary<byte, double> _peerSmoothedRttMs = new Dictionary<byte, double>();
        private static readonly Dictionary<byte, double> _peerRttSampleTicks = new Dictionary<byte, double>();
        private static readonly Dictionary<byte, int> _peerLastSimTick = new Dictionary<byte, int>();
        private static readonly Dictionary<byte, double> _peerLastSimTickWallMs = new Dictionary<byte, double>();
        private const double RttProbeIntervalMs = 500.0;
        private const double RttStalePeerMs = 5000.0;
        private const double ClientLagStaleMs = 3500.0;
        private const int HostBackpressureSlowLagTicks = 16;
        private const int HostBackpressureHoldLagTicks = 45;
        private const double DelayAdjustCooldownMs = 1500.0;
        private static int _gateLastObservedTick = -1;
        private static double _gateWaitStartTicks = -1.0;
        private static double _gateLastWarnTicks = -1.0;
        private const double GateTimeoutMs = 3000.0;

        private static bool _awaitingSnapshotLoad;
        private static bool _awaitingClientSnapshotAck;
        private static double _awaitingClientAckStartTicks = -1.0;
        private static bool _hostSnapshotReloadPending;
        private static bool _hostWaitingOwnReload;
        private static bool _clientSnapshotAckReceived;
        public static bool IsHostSnapshotReloadPending => _hostSnapshotReloadPending;

        // Deferred work: never export/load from inside a deep engine callback (LevelLoadComplete
        // postfix / network PollEvents). Doing the ZIP save from that stack exhausted the 1 MB main
        // thread stack (0xc00000fd) and re-entered the session callbacks.
        private static bool _pendingHostSnapshotExport;
        private static string _pendingClientSnapshotLoadPath;
        private static byte[] _pendingBroadcastSavBytes;
        private static string _pendingBroadcastChkText;
        private static int _pendingBroadcastSeed;
        private static long _pendingBroadcastIdCounter;
        // The simulation-clock origin the Host used for this snapshot. The Client adopts it verbatim
        // so both peers run the same number of Chipmunk steps (see LockstepClock.SharedClockOriginMs).
        private static double _incomingClockOriginMs = double.NaN;
        private static bool _kickingLoadFade;
        private static int _loadFadeKickFrames;
        private static double _loadFadeKickStartTicks = -1.0;
        private const double LoadFadeTimeoutMs = 30000.0;

        // Snapshot receive buffer on Client. Chaos/jitter can deliver chunks out of order (even
        // before SnapshotBegin), so buffer by index and assemble in order once all are present.
        private static readonly Dictionary<int, byte[]> _incomingChunks = new Dictionary<int, byte[]>();
        private static int _expectedChunks;
        private static int _expectedSaveBytes;
        private static string _incomingCheckContent = string.Empty;
        private static int _incomingRandomSeed = 1337;
        private static long _incomingEntityIdCounter;

        // Ally Cursor & Camera State
        public sealed class AllyVisualState
        {
            public byte PlayerId;
            public PointF MouseWorldPosition;
            public RectangleF CameraWorldArea;
            public DateTime LastUpdatedUtc;
        }

        public sealed class TickDigestRecord
        {
            public StateHasher.LevelStateSnapshotDigest Digest;
            public string Summary;
            public List<OwnedUnitPosSample> OwnedUnits;
        }

        private struct UnitOwnershipEntry
        {
            public byte PlayerId;
            public int LastOrderTick;
        }

        public struct OwnedUnitPosSample
        {
            public ulong Id;
            public float X;
            public float Y;
        }

        private sealed class PendingReverseAlignApply
        {
            public int Generation;
            public byte SenderPlayerId;
            public int Tick;
            public List<OwnedUnitPosSample> Units;
        }

        private sealed class PosSyncReceiveBuffer
        {
            public int SimTick;
            public bool ApplyCorrection;
            public int TotalChunks;
            public int CompressedTotalLen;
            public readonly Dictionary<int, byte[]> Chunks = new Dictionary<int, byte[]>();
        }

        private struct EntityDeltaFix
        {
            public ulong Id;
            public float Dx;
            public float Dy;
        }

        private struct EntityLifeFix
        {
            public ulong Id;
            public int Delta;
            public bool Kill;
        }

        private sealed class OutgoingPosSyncBatch
        {
            public int Generation;
            public byte TargetPlayerId;
            public int Seq;
            public int Tick;
            public bool ApplyCorrection;
            public int TotalCompressedLen;
            public List<byte[]> Chunks;
        }

        private sealed class PendingPosAlignApply
        {
            public int Generation;
            public int Tick;
            public bool ApplyCorrection;
            public bool ApplyPosCorrection;
            public int ComparedCount;
            public int Drifted001, Drifted01, Drifted1, Drifted5;
            public float MaxDelta;
            public float RmsDelta;
            public ulong MaxDeltaEntityId;
            public bool ExceededThreshold;
            public List<EntityDeltaFix> Deltas;
            public List<EntityLifeFix> LifeFixes;
            public int LifeDiffCount, MissingOnHost, MissingOnClient;
        }

        public static readonly Dictionary<byte, AllyVisualState> AllyStates = new Dictionary<byte, AllyVisualState>();
        private static readonly StateHasher.IncrementalEpochHasher _epochHasher = new StateHasher.IncrementalEpochHasher();
        private static readonly ConcurrentQueue<OutgoingPosSyncBatch> _outgoingPosSyncQueue =
            new ConcurrentQueue<OutgoingPosSyncBatch>();
        private static readonly ConcurrentQueue<PendingPosAlignApply> _incomingPosAlignQueue =
            new ConcurrentQueue<PendingPosAlignApply>();
        private static readonly ConcurrentQueue<PendingReverseAlignApply> _incomingReverseAlignQueue =
            new ConcurrentQueue<PendingReverseAlignApply>();
        private static readonly object _unitOwnershipSync = new object();
        private static readonly Dictionary<ulong, UnitOwnershipEntry> _unitOwnership =
            new Dictionary<ulong, UnitOwnershipEntry>();
        private static volatile int _asyncGeneration;
        private static int _posSyncBuildInFlight;

        private static double _lastCursorBroadcastTicks;
        private static int _lastCheckedSimTick = -1;
        private static bool _firstChecksumPosDumped;
        private static bool _bucketDiffLogged;
        private static int _segmentStartTick = -1;
        private static int _consecutiveProjOnlyMismatch;
        private static int _consecutiveHardMismatch;
        private static int _consecutivePosExceeded;
        private static int _verifiedChecksumCycles;
        private static bool _econEarlyBypassUsed;
        private static bool _driftOnsetLogged;
        private static int _posSyncSeq;

        // Modal / engine pause keepalive & E2E command latency telemetry
        private static double _hostLastPauseKeepaliveSentTicks = -100000.0;
        private static double _clientHostPauseKeepaliveUntilTicks = -1.0;
        private static int _clientHostPausedTick = -1;
        private static bool _clientHostInModalPause;
        private static bool _levelEndedSynced;
        public static bool IsReceivingRemoteLevelEnd { get; private set; }
        public static bool IsLevelEndedSynced => _levelEndedSynced;
        private static uint _nextTraceSeq;
        private static readonly Dictionary<uint, long> _localCmdSentTs = new Dictionary<uint, long>();
        private static readonly List<double> _cmdE2ELatencySamplesMs = new List<double>();
        private static double _lastLatencyStatsLogTicks = -100000.0;

        private static readonly Dictionary<int, TickDigestRecord> _hostTickDigests = new Dictionary<int, TickDigestRecord>();
        private static readonly Dictionary<int, Dictionary<byte, TickDigestRecord>> _clientTickDigests =
            new Dictionary<int, Dictionary<byte, TickDigestRecord>>();
        private static readonly Dictionary<int, List<StateHasher.EntityPosSample>> _tickPosCache =
            new Dictionary<int, List<StateHasher.EntityPosSample>>();
        private static readonly Queue<int> _tickPosCacheOrder = new Queue<int>();
        private static readonly Dictionary<int, PosSyncReceiveBuffer> _posSyncIncoming =
            new Dictionary<int, PosSyncReceiveBuffer>();

        private static void ResetSyncTrackingState()
        {
            _asyncGeneration++;
            Interlocked.Exchange(ref _posSyncBuildInFlight, 0);
            _lastCheckedSimTick = -1;
            _lastFullPosAlignSentTicks = -100000.0;
            _lastEntityPosCorrectedTick.Clear();
            _lastEntityMicroCorrectedTick.Clear();
            _consecutiveProjOnlyMismatch = 0;
            _consecutiveHardMismatch = 0;
            _consecutivePosExceeded = 0;
            _verifiedChecksumCycles = 0;
            _econEarlyBypassUsed = false;
            _driftOnsetLogged = false;
            _hostLastPauseKeepaliveSentTicks = -100000.0;
            _clientHostPauseKeepaliveUntilTicks = -1.0;
            _clientHostPausedTick = -1;
            _clientHostInModalPause = false;
            _levelEndedSynced = false;
            IsReceivingRemoteLevelEnd = false;
            lock (_localCmdSentTs) _localCmdSentTs.Clear();
            lock (_cmdE2ELatencySamplesMs) _cmdE2ELatencySamplesMs.Clear();
            _lastLatencyStatsLogTicks = -100000.0;
            ResetTieredResyncState();
            _epochHasher.Reset();
            _hostTickDigests.Clear();
            _clientTickDigests.Clear();
            _tickPosCache.Clear();
            _tickPosCacheOrder.Clear();
            _posSyncIncoming.Clear();
            while (_outgoingPosSyncQueue.TryDequeue(out _)) { }
            while (_incomingPosAlignQueue.TryDequeue(out _)) { }
            while (_incomingReverseAlignQueue.TryDequeue(out _)) { }
            lock (_unitOwnershipSync) _unitOwnership.Clear();
            _peerLastSimTick.Clear();
            _peerLastSimTickWallMs.Clear();
            CommandInterceptor.ClearPendingLocalBuilds();
            Patches.MayorSyncPatch.ResetTracking();
        }

        public static int RollNewGameSeed()
        {
            if (TestHarnessConfig.Enabled)
            {
                CurrentSessionSeed = SyncSeed;
                return CurrentSessionSeed;
            }
            int seed = unchecked((int)(DateTime.UtcNow.Ticks ^ Guid.NewGuid().GetHashCode()));
            if (seed == 0 || seed == 1337) seed = 20261110;
            CurrentSessionSeed = seed;
            LockstepClock.EnableDeterministicSandbox(CurrentSessionSeed);
            LockstepClock.RequestPostLoadCalibration(CurrentSessionSeed, markLoading: false);
            LockstepClock.ResetBehaviourRandom(CurrentSessionSeed);
            ModLogger.Info($"[CoopSessionManager] Rolled dynamic session seed: {CurrentSessionSeed}");
            return CurrentSessionSeed;
        }

        public static bool HasJoinedClient
        {
            get
            {
                if (Transport == null) return false;
                foreach (var p in GetEffectivePeers())
                {
                    if (p.PlayerId != 1) return true;
                }
                return false;
            }
        }

        public static bool AreAllClientsReady
        {
            get
            {
                bool hasClient = false;
                foreach (var p in GetEffectivePeers())
                {
                    if (p.PlayerId == 1) continue;
                    hasClient = true;
                    if (!p.IsReady) return false;
                }
                return hasClient;
            }
        }

        public static bool CanHostStartGame =>
            IsSessionActive && Transport.IsHost && AreAllClientsReady;

        public static PeerInfo LocalPeer
        {
            get
            {
                if (Transport == null) return null;
                foreach (var p in GetEffectivePeers())
                {
                    if (p.PlayerId == Transport.LocalPlayerId) return p;
                }
                return null;
            }
        }

        public static IReadOnlyList<PeerInfo> GetEffectivePeers()
        {
            if (_lobbyRoster.Count > 0) return _lobbyRoster;
            if (Transport?.ConnectedPeers != null) return Transport.ConnectedPeers;
            return Array.Empty<PeerInfo>();
        }

        public static void SetLocalReady(bool ready)
        {
            if (!IsSessionActive || Transport.IsHost) return;
            byte localId = Transport.LocalPlayerId;
            if (localId == 0) return;

            foreach (var p in _lobbyRoster)
            {
                if (p.PlayerId == localId)
                {
                    p.IsReady = ready;
                    break;
                }
            }
            Transport.SyncPeers(_lobbyRoster);
            Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.ClientReadyState, w =>
            {
                w.Write(ready);
            });
            LobbyStateChanged?.Invoke();
        }

        public static void SetHostRoomPhase(LobbyRoomPhase phase, string hint = "")
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            RoomPhase = phase;
            HostActionHint = hint ?? string.Empty;
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static void SetBalancePreset(CoopBalancePreset preset)
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            if (BalancePreset == preset) return;
            BalancePreset = preset;
            ModLogger.Info($"[CoopSessionManager] BalancePreset changed to {preset}.");
            BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();
        }

        public static void ToggleBalancePreset()
        {
            if (!IsSessionActive || !Transport.IsHost) return;
            SetBalancePreset(BalancePreset == CoopBalancePreset.Balanced
                ? CoopBalancePreset.Vanilla
                : CoopBalancePreset.Balanced);
        }

        private static ChaosConfig BuildLocalChaosConfig()
        {
            return new ChaosConfig
            {
                Enabled = LocalChaosEnabled,
                LatencyMs = LocalChaosLatencyMs,
                JitterMs = LocalChaosJitterMs,
                LossRate = LocalChaosLossRate,
                Seed = 12345
            };
        }

        public static void SetLocalChaosSettings(bool enabled, int latencyMs, int jitterMs, double lossRate)
        {
            if (TestHarnessConfig.HasChaos) return;
            if (IsSessionActive && (!Transport.IsHost || !IsLocalLoopbackSession)) return;

            LocalChaosEnabled = enabled;
            LocalChaosLatencyMs = Math.Max(0, Math.Min(500, latencyMs));
            LocalChaosJitterMs = Math.Max(0, Math.Min(300, jitterMs));
            LocalChaosLossRate = Math.Round(Math.Max(0.0, Math.Min(0.30, lossRate)), 3);

            if (IsLocalLoopbackSession && Transport is ChaosTransport chaos)
            {
                chaos.UpdateConfig(BuildLocalChaosConfig());
            }

            if (IsSessionActive && Transport.IsHost && IsLocalLoopbackSession)
            {
                BroadcastLobbyRoster();
            }
            LobbyStateChanged?.Invoke();
        }

        public static void ToggleLocalChaosEnabled()
        {
            SetLocalChaosSettings(!LocalChaosEnabled, LocalChaosLatencyMs, LocalChaosJitterMs, LocalChaosLossRate);
        }

        public static void BroadcastLobbyRoster()
        {
            if (!IsSessionActive || !Transport.IsHost) return;

            if (_lobbyRoster.Count == 0 && Transport.ConnectedPeers != null)
            {
                foreach (var p in Transport.ConnectedPeers)
                {
                    _lobbyRoster.Add(new PeerInfo
                    {
                        PlayerId = p.PlayerId,
                        PlayerName = p.PlayerName,
                        SteamId64 = p.SteamId64,
                        IsReady = p.PlayerId == 1 || p.IsReady
                    });
                }
            }

            Transport.SyncPeers(_lobbyRoster);
            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.LobbyRosterSync, w =>
            {
                w.Write((byte)RoomPhase);
                w.Write(HostActionHint ?? string.Empty);
                w.Write((byte)_lobbyRoster.Count);
                foreach (var p in _lobbyRoster)
                {
                    w.Write(p.PlayerId);
                    w.Write(p.PlayerName ?? string.Empty);
                    w.Write(p.SteamId64);
                    w.Write(p.IsReady);
                }
                w.Write((byte)BalancePreset);
                if (IsLocalLoopbackSession && !TestHarnessConfig.HasChaos)
                {
                    w.Write(LocalChaosEnabled);
                    w.Write(LocalChaosLatencyMs);
                    w.Write(LocalChaosJitterMs);
                    w.Write((float)LocalChaosLossRate);
                }
            });
        }

        public static void StartHostSession(bool useLoopback)
        {
            StartHostSession(useLoopback ? CoopTransportMode.LocalLoopback : CoopTransportMode.Steam);
        }

        /// <summary>
        /// Hosts a native LAN session: binds the TCP listener to <see cref="IPAddress.Any"/> on the
        /// LAN port so players on other machines in the same network can join directly.
        /// </summary>
        public static void StartHostSessionLan(int port = LocalTcpLoopbackTransport.DefaultPort)
        {
            StartHostSession(CoopTransportMode.Lan, port);
        }

        public static void StartHostSession(CoopTransportMode mode, int port = LocalTcpLoopbackTransport.DefaultPort)
        {
            StopSession();
            int seed = RollNewGameSeed();
            LockstepClock.EnableDeterministicSandbox(seed);
            LockstepClock.RequestPostLoadCalibration(seed, markLoading: false);
            LockstepClock.ResetBehaviourRandom(seed);

            LocalTcpLoopbackTransport.HostRoomPhaseProvider = () => (byte)RoomPhase;
            Transport = CreateTransport(mode, port);
            BindTransportEvents();
            Transport.StartHost(3);

            _lobbyRoster.Clear();
            if (Transport.ConnectedPeers != null)
            {
                foreach (var p in Transport.ConnectedPeers)
                {
                    _lobbyRoster.Add(new PeerInfo
                    {
                        PlayerId = p.PlayerId,
                        PlayerName = p.PlayerName,
                        SteamId64 = p.SteamId64,
                        IsReady = true
                    });
                }
            }
            RoomPhase = LobbyRoomPhase.InLobby;
            BalancePreset = CoopBalancePreset.Balanced;
            HostActionHint = string.Empty;

            CommandInterceptor.LocalPlayerId = 1;
            CommandInterceptor.OnLocalCommandCaptured = OnLocalCommandCaptured;
            ModLogger.Info($"[CoopSessionManager] Started Host Session (Mode={mode}, port={port}, seed={seed}).");
            if (mode == CoopTransportMode.Lan)
            {
                var ips = LocalTcpLoopbackTransport.GetLocalIPv4Addresses();
                var v6 = LocalTcpLoopbackTransport.GetGlobalIPv6AddressInfos();
                string v6Text = "<none>";
                if (v6.Count > 0)
                {
                    var v6List = new List<string>();
                    foreach (var info in v6) v6List.Add(info.Address);
                    v6Text = string.Join(", ", v6List.ToArray());
                }
                ModLogger.Info($"[CoopSessionManager] LAN host ready (dual-stack). IPv4: " +
                               $"{(ips.Count > 0 ? string.Join(", ", ips) : "<no LAN IPv4 found>")}:{port} | IPv6: {v6Text}");
                if (v6.Count == 0)
                {
                    ModLogger.Warn("[CoopSessionManager] No global IPv6 address found. A remote teammate behind " +
                                   "carrier-grade NAT will NOT be able to connect; only players on the same LAN/VPN can.");
                }
            }
            LobbyStateChanged?.Invoke();
        }

        public static void JoinSession(bool useLoopback, string targetAddressOrSteamId)
        {
            JoinSession(useLoopback ? CoopTransportMode.LocalLoopback : CoopTransportMode.Steam, targetAddressOrSteamId);
        }

        /// <summary>Joins a native LAN session hosted by another machine ("192.168.1.100" or "192.168.1.100:27099").</summary>
        public static void JoinSessionLan(string hostOrEndpoint)
        {
            JoinSession(CoopTransportMode.Lan, hostOrEndpoint);
        }

        public static void JoinSession(CoopTransportMode mode, string targetAddressOrSteamId)
        {
            StopSession();
            CurrentSessionSeed = SyncSeed;
            LockstepClock.EnableDeterministicSandbox(CurrentSessionSeed);
            LockstepClock.RequestPostLoadCalibration(CurrentSessionSeed, markLoading: false);
            LockstepClock.ResetBehaviourRandom(CurrentSessionSeed);

            Transport = CreateTransport(mode, LocalTcpLoopbackTransport.DefaultPort);
            BindTransportEvents();
            Transport.ConnectToHost(targetAddressOrSteamId);

            RoomPhase = LobbyRoomPhase.InLobby;
            BalancePreset = CoopBalancePreset.Balanced;
            HostActionHint = string.Empty;
            CommandInterceptor.OnLocalCommandCaptured = OnLocalCommandCaptured;
            ModLogger.Info($"[CoopSessionManager] Joining Session at '{targetAddressOrSteamId}' (Mode={mode}).");
            LobbyStateChanged?.Invoke();
        }

        public static void StopSession()
        {
            CommandInterceptor.IsInterceptionEnabled = false;
            CommandInterceptor.OnLocalCommandCaptured = null;
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            CommandExecutor.IsReplayingCommand = false;
            IsInSyncedLevel = false;
            RoomPhase = LobbyRoomPhase.Idle;
            BalancePreset = CoopBalancePreset.Balanced;
            HostActionHint = string.Empty;
            HasVersionMismatch = false;
            VersionMismatchSummary = string.Empty;
            _versionMismatchAlertedKeys.Clear();
            _pendingVersionMismatchAlert = null;
            ShouldOpenLobbyOnMainMenu = false;
            _pendingClientReturnToMainMenu = false;
            _suppressReturnNotice = false;
            _lobbyRoster.Clear();
            LocalTcpLoopbackTransport.HostRoomPhaseProvider = null;
            ResetSyncTrackingState();
            LockstepClock.DisableDeterministicSandbox();
            AllyStates.Clear();
            _localOutgoingQueue.Clear();
            _hostTurnBuffer.Clear();
            _pendingTurnCommands.Clear();
            _hostLastBroadcastSimTick = -1;
            _hostLastBroadcastTargetTick = -1;
            InputDelayTicks = MinInputDelayTicks;
            CurrentSmoothedRttMs = 0.0;
            _lastRttProbeTicks = -100000.0;
            _lastDelayAdjustTicks = -100000.0;
            _peerSmoothedRttMs.Clear();
            _peerRttSampleTicks.Clear();
            _peerLastSimTick.Clear();
            _peerLastSimTickWallMs.Clear();
            _gateLastObservedTick = -1;
            _gateWaitStartTicks = -1.0;
            _awaitingSnapshotLoad = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _hostSnapshotReloadPending = false;
            _hostWaitingOwnReload = false;
            _clientSnapshotAckReceived = false;
            _lastAutoResyncTicks = -100000.0;
            _lastPosSyncSentTicks = -100000.0;
            _lastFullPosAlignSentTicks = -100000.0;
            _lastEntityPosCorrectedTick.Clear();
            _lastEntityMicroCorrectedTick.Clear();
            _pendingHostSnapshotExport = false;
            _pendingClientSnapshotLoadPath = null;
            _pendingBroadcastSavBytes = null;
            _pendingBroadcastChkText = null;
            _kickingLoadFade = false;
            _loadFadeKickStartTicks = -1.0;
            _incomingChunks.Clear();
            _expectedChunks = 0;
            _networkGate = new TurnInputTickGate();
            DXGame.LastGameTimeIAUpdate = 0.0;
            DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0;

            if (Transport != null)
            {
                Transport.Dispose();
                Transport = null;
            }
            try
            {
                if (DXGame.Current != null && !DXGame.Current.InputEnabled)
                    DXGame.Current.InputEnabled = true;
            }
            catch { }
            LobbyStateChanged?.Invoke();
        }

        public static void OnReturningToMainMenu()
        {
            if (!IsSessionActive) return;

            bool isHost = Transport.IsHost;
            bool suppressBroadcast = _suppressReturnNotice;
            _suppressReturnNotice = false;

            IsInSyncedLevel = false;
            CommandInterceptor.IsInterceptionEnabled = false;
            try
            {
                if (DXGame.Current != null && !DXGame.Current.InputEnabled)
                    DXGame.Current.InputEnabled = true;
            }
            catch { }
            CommandInterceptor.SuppressIndividualMoveExecution = false;
            CommandInterceptor.InQueueAutoAdvance = false;
            CommandExecutor.IsReplayingCommand = false;
            PauseSyncController.CancelCountdown();
            LockstepClock.SetSessionPause(false);
            RenderVisualInterpolationPatch.ExitVisualSceneUpdate(DXGame.Current);
            RenderVisualInterpolationPatch.Reset();

            ResetSyncTrackingState();
            AllyStates.Clear();
            _localOutgoingQueue.Clear();
            _hostTurnBuffer.Clear();
            _pendingTurnCommands.Clear();
            _hostLastBroadcastSimTick = -1;
            _hostLastBroadcastTargetTick = -1;
            InputDelayTicks = MinInputDelayTicks;
            CurrentSmoothedRttMs = 0.0;
            _lastRttProbeTicks = -100000.0;
            _lastDelayAdjustTicks = -100000.0;
            _peerSmoothedRttMs.Clear();
            _peerRttSampleTicks.Clear();
            _peerLastSimTick.Clear();
            _peerLastSimTickWallMs.Clear();
            _gateLastObservedTick = -1;
            _gateWaitStartTicks = -1.0;
            _awaitingSnapshotLoad = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _hostSnapshotReloadPending = false;
            _hostWaitingOwnReload = false;
            _clientSnapshotAckReceived = false;
            _pendingHostSnapshotExport = false;
            _pendingClientSnapshotLoadPath = null;
            _pendingBroadcastSavBytes = null;
            _pendingBroadcastChkText = null;
            _kickingLoadFade = false;
            _loadFadeKickStartTicks = -1.0;
            _incomingChunks.Clear();
            _expectedChunks = 0;
            _networkGate = new TurnInputTickGate();
            DXGame.LastGameTimeIAUpdate = 0.0;
            DXPhysicWorld.LastGameTimeUpdatePhysics = 0.0;
            LockstepClock.TickGate = null;

            ShouldOpenLobbyOnMainMenu = true;

            if (isHost)
            {
                foreach (var p in _lobbyRoster)
                {
                    if (p.PlayerId != 1) p.IsReady = false;
                }
                RoomPhase = LobbyRoomPhase.InLobby;
                HostActionHint = string.Empty;
                RollNewGameSeed();

                if (!suppressBroadcast)
                {
                    Transport.Broadcast(SendChannel.ReliableCommand, PacketType.LobbyHostNotice, w =>
                    {
                        w.Write((byte)LobbyHostNoticeType.ReturnToLobby);
                    });
                }
                BroadcastLobbyRoster();
                ModLogger.Info("[CoopSessionManager] Host returning to Main Menu -> preserved connection and returned room to Lobby.");
            }
            else
            {
                if (!suppressBroadcast)
                {
                    SetLocalReady(false);
                    ModLogger.Info("[CoopSessionManager] Client returning to Main Menu -> preserved connection and notified Host Unready.");
                }
                else
                {
                    foreach (var p in _lobbyRoster)
                    {
                        if (p.PlayerId != 1) p.IsReady = false;
                    }
                    RoomPhase = LobbyRoomPhase.InLobby;
                    HostActionHint = string.Empty;
                    Transport.SyncPeers(_lobbyRoster);
                    ModLogger.Info("[CoopSessionManager] Client followed Host back to Lobby.");
                }
            }
            LobbyStateChanged?.Invoke();
        }

        private static INetworkTransport CreateTransport(CoopTransportMode mode, int port)
        {
            INetworkTransport transport;
            switch (mode)
            {
                case CoopTransportMode.LocalLoopback:
                    transport = new LocalTcpLoopbackTransport(IPAddress.Loopback, port);
                    break;
                case CoopTransportMode.Lan:
                    // IPAddress.Any: accept connections from other machines. Chaos simulation (latency
                    // jitter/loss) is deliberately NOT applied to LAN sessions - it would sabotage a
                    // link that is already fast, and the roster would carry 13 extra bytes.
                    transport = new LocalTcpLoopbackTransport(IPAddress.Any, port);
                    break;
                default:
                    transport = new SteamP2PTransport();
                    break;
            }

            if (TestHarnessConfig.HasChaos && mode != CoopTransportMode.Lan)
            {
                var envCfg = ChaosConfig.FromString(TestHarnessConfig.ChaosSpec);
                envCfg.Enabled = true;
                LocalChaosEnabled = true;
                LocalChaosLatencyMs = envCfg.LatencyMs;
                LocalChaosJitterMs = envCfg.JitterMs;
                LocalChaosLossRate = envCfg.LossRate;
                transport = new ChaosTransport(transport, envCfg);
                ModLogger.Info($"[CoopSessionManager] Chaos injection active: {TestHarnessConfig.ChaosSpec}");
            }
            else if (mode == CoopTransportMode.LocalLoopback)
            {
                transport = new ChaosTransport(transport, BuildLocalChaosConfig());
            }
            return transport;
        }

        private static void BindTransportEvents()
        {
            Transport.OnPeerConnected += peer =>
            {
                CommandInterceptor.LocalPlayerId = Transport.LocalPlayerId;
                ModLogger.Info($"[CoopSessionManager] Peer connected: {peer?.PlayerName} (PlayerId={peer?.PlayerId}, LocalId={Transport.LocalPlayerId})");
                GameBridge.ShowTopBanner($"Co-op Peer Connected: {peer?.PlayerName}", Color.SpringGreen, 3000);

                if (Transport.IsHost)
                {
                    if (peer != null)
                    {
                        _lobbyRoster.RemoveAll(p =>
                            p.PlayerId == peer.PlayerId
                            || (peer.SteamId64 != 0 && p.SteamId64 == peer.SteamId64 && p.PlayerId != 1));
                        _lobbyRoster.Add(new PeerInfo
                        {
                            PlayerId = peer.PlayerId,
                            PlayerName = peer.PlayerName,
                            SteamId64 = peer.SteamId64,
                            IsReady = peer.PlayerId == 1 || peer.IsReady
                        });
                        _lobbyRoster.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));
                    }
                    BroadcastLobbyRoster();

                    // If Host is already inside a level when a peer connects, push snapshot immediately!
                    if (GameBridge.CurrentLevelState != null && !_hostSnapshotReloadPending)
                    {
                        RequestSnapshotBroadcast();
                    }
                }
                else
                {
                    if (Transport.ConnectedPeers != null && Transport.ConnectedPeers.Count > 0)
                    {
                        _lobbyRoster.Clear();
                        foreach (var p in Transport.ConnectedPeers)
                        {
                            _lobbyRoster.Add(new PeerInfo
                            {
                                PlayerId = p.PlayerId,
                                PlayerName = p.PlayerName,
                                SteamId64 = p.SteamId64,
                                IsReady = p.IsReady
                            });
                        }
                    }
                }
                LobbyStateChanged?.Invoke();
            };

            Transport.OnPeerDisconnected += playerId =>
            {
                AllyStates.Remove(playerId);
                _peerSmoothedRttMs.Remove(playerId);
                _peerRttSampleTicks.Remove(playerId);
                _peerLastSimTick.Remove(playerId);
                _peerLastSimTickWallMs.Remove(playerId);
                _lobbyRoster.RemoveAll(p => p.PlayerId == playerId);
                if (Transport != null && Transport.IsHost)
                    UpdateAdaptiveInputDelay();

                if (Transport != null && Transport.IsHost)
                {
                    BroadcastLobbyRoster();
                    GameBridge.ShowTopBanner($"Player {playerId} Disconnected", Color.OrangeRed, 3000);
                    LobbyStateChanged?.Invoke();
                }
                else if (Transport != null && !Transport.IsHost)
                {
                    _networkGate.MarkReady(int.MaxValue);
                    IsInSyncedLevel = false;
                    if (playerId == 1)
                    {
                        GameBridge.ShowTopBanner("Host disconnected / Room disbanded", Color.OrangeRed, 3500);
                        StopSession();
                    }
                    else
                    {
                        GameBridge.ShowTopBanner($"Player {playerId} Disconnected", Color.OrangeRed, 3000);
                        LobbyStateChanged?.Invoke();
                    }
                }
            };

            Transport.OnPacketReceived += OnPacketReceived;
            Transport.OnVersionMismatch += OnTransportVersionMismatch;
        }

        private static void OnTransportVersionMismatch(string peerName, string localVer, string remoteVer)
        {
            string peerLabel = string.IsNullOrEmpty(peerName)
                ? (Transport != null && Transport.IsHost ? "客机" : "房主")
                : peerName;
            HasVersionMismatch = true;
            VersionMismatchSummary = $"⚠ 版本不一致：本机 v{localVer} ≠ {peerLabel} (v{remoteVer})";

            string key = $"{peerLabel}|{localVer}|{remoteVer}";
            if (_versionMismatchAlertedKeys.Add(key))
            {
                ModLogger.Warn($"[CoopSessionManager] {VersionMismatchSummary}");
                _pendingVersionMismatchAlert =
                    $"检测到联机 Mod 版本不一致！\n\n" +
                    $"本机版本：v{localVer}\n" +
                    $"对端 ({peerLabel}) 版本：v{remoteVer}\n\n" +
                    $"版本不一致会导致市长候选不同、重开天数异常、物理/尸潮不同步等问题。\n" +
                    $"请所有联机玩家安装完全相同的 TABCoopMod 版本后再开始游戏！";
                GameBridge.ShowTopBanner($"Version Mismatch: v{localVer} != v{remoteVer}", Color.OrangeRed, 5000);
            }
            LobbyStateChanged?.Invoke();
        }

        /// <summary>
        /// Defers a Host snapshot export to the next OnFrameTick top-level. Callers may sit on a deep
        /// engine/network stack (LevelLoadComplete postfix, packet delivery), where the recursive ZIP
        /// serializer would blow the main-thread stack.
        /// </summary>
        public static void RequestSnapshotBroadcast()
        {
            if (Transport == null || !Transport.IsHost || GameBridge.CurrentLevelState == null)
                return;
            _pendingHostSnapshotExport = true;
        }

        /// <summary>
        /// Exports the Host's current level state into a temporary .zxsav + .zxcheck pair
        /// and streams it to all connected peers in 32KB chunks.
        /// </summary>
        public static void BroadcastCurrentLevelSnapshot()
        {
            if (Transport == null || !Transport.IsHost || GameBridge.CurrentLevelState == null)
                return;

            double wasGameSpeed = DXGame.Current.GameSpeed;
            try
            {
                DXGame.Current.Paused = true;

                string savesDir = GameBridge.GetSavesDirectory();
                string tempBaseName = "coop_host_snapshot";
                string savPath = Path.Combine(savesDir, tempBaseName + ".zxsav");
                string chkPath = Path.Combine(savesDir, tempBaseName + ".zxcheck");

                ModLogger.Info($"[CoopSessionManager] Exporting Host snapshot to {savPath}...");
                GameBridge.WriteSaveFile(savPath);

                // Freeze the simulation-clock origin BEFORE exporting so the snapshot and the
                // broadcast carry the exact same origin. The Client adopts it, which is what keeps
                // "GameTime - LastGameTimeUpdatePhysics" identical on both peers.
                double clockOrigin = DXGame.Current != null ? DXGame.Current.GameTime : double.NaN;
                if (!double.IsNaN(clockOrigin))
                    LockstepClock.ApplySharedClockOrigin(clockOrigin);

                byte[] savBytes = File.ReadAllBytes(savPath);
                string chkText = File.Exists(chkPath) ? File.ReadAllText(chkPath) : string.Empty;
                int syncSeed = CurrentSessionSeed;
                long idCounter = LockstepClock.CurrentSyncedEntityIdCounter;

                _hostTickDigests.Clear();
                _clientTickDigests.Clear();
                _tickPosCache.Clear();
                _tickPosCacheOrder.Clear();
                _posSyncIncoming.Clear();
                _consecutiveProjOnlyMismatch = 0;
                _consecutiveHardMismatch = 0;
                _consecutivePosExceeded = 0;
                ResetTieredResyncState();
                _lastCheckedSimTick = -1;
                _pendingTurnCommands.Clear();
                _hostTurnBuffer.Clear();
                _localOutgoingQueue.Clear();

                bool hasRemotePeers = Transport.ConnectedPeers != null && Transport.ConnectedPeers.Count > 1;
                if (!hasRemotePeers)
                {
                    LockstepClock.ApplyDeterministicLevelBaseline(syncSeed, idCounter);
                    EnterSyncedLevel();
                    _awaitingClientSnapshotAck = false;
                    _awaitingClientAckStartTicks = -1.0;
                    _hostSnapshotReloadPending = false;
                    _hostWaitingOwnReload = false;
                    _clientSnapshotAckReceived = false;
                    return;
                }

                // Exit synced-level state during symmetric reload so neither peer runs ticks/inputs early.
                IsInSyncedLevel = false;
                CommandInterceptor.IsInterceptionEnabled = false;
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null) DXGame.Current.InputEnabled = false;

                _hostSnapshotReloadPending = true;
                _hostWaitingOwnReload = true;
                _clientSnapshotAckReceived = false;
                _awaitingClientSnapshotAck = false;
                _awaitingClientAckStartTicks = -1.0;

                _pendingBroadcastSavBytes = savBytes;
                _pendingBroadcastChkText = chkText;
                _pendingBroadcastSeed = syncSeed;
                _pendingBroadcastIdCounter = idCounter;

                // Host reloads the exact coop_host_snapshot.zxsav first before streaming it to peers.
                // This avoids writing a second save file (coop_host_self.zxsav) and avoids loopback file lock collisions.
                ModLogger.Info($"[CoopSessionManager] Host starting symmetric reload of {savPath} ({savBytes.Length:N0} bytes) before broadcasting...");
                LockstepClock.EnableDeterministicSandbox(syncSeed);
                LockstepClock.RequestPostLoadCalibration(syncSeed, idCounter);
                if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false;
                GameBridge.PrepareForProgrammaticLoad();
                GameBridge.LoadSaveFileAndEnter(savPath);
                _kickingLoadFade = true;
                _loadFadeKickFrames = 0;
                _loadFadeKickStartTicks = -1.0;

                GameBridge.ShowTopBanner($"Reloading Host Snapshot ({savBytes.Length / 1024} KB)...", Color.Cyan, 2500);
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to broadcast level snapshot", ex);
            }
            finally
            {
                // Never restore a zero speed: that would look like an engine pause and stall the
                // host heartbeat. Session pauses are handled separately via LockstepClock.PausedBySession.
                try { DXGame.Current.GameSpeed = wasGameSpeed > 0.0 ? wasGameSpeed : 1.0; } catch { }
                try { if (DXGame.Current != null && DXGame.Current.Paused) DXGame.Current.Paused = false; } catch { }
                if (_awaitingClientSnapshotAck)
                {
                    LockstepClock.SetSessionPause(true);
                }
            }
        }

        private static void TryCompleteHostSyncBarrier()
        {
            if (Transport == null || !Transport.IsHost) return;
            if (_hostWaitingOwnReload || !_clientSnapshotAckReceived) return;

            _hostSnapshotReloadPending = false;
            _awaitingClientSnapshotAck = false;
            _awaitingClientAckStartTicks = -1.0;
            _clientSnapshotAckReceived = false;

            ResetSyncTrackingState();

            EnterSyncedLevel();

            int startTick = DXGame.Current?.IAIterationInt ?? 0;
            long idCounter = LockstepClock.CurrentSyncedEntityIdCounter;
            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.SessionStart, w =>
            {
                w.Write(startTick);
                w.Write(CurrentSessionSeed);
                w.Write(idCounter);
                w.Write((byte)InputDelayTicks);
            });
            ModLogger.Info($"[CoopSessionManager] Host+Client barrier satisfied; entered synced level and sent SessionStart at tick={startTick}, seed={CurrentSessionSeed}, inputDelay={InputDelayTicks}!");
        }

        private static void OnLocalCommandCaptured(NetCommand cmd)
        {
            if (IsGameplayInputLocked)
            {
                ModLogger.Warn($"[CoopSessionManager] Dropped local command {cmd?.Type} during sync barrier.");
                return;
            }

            if (!IsSessionActive || !IsInSyncedLevel)
            {
                // Single-player deterministic test mode: execute immediately
                CommandExecutor.Execute(cmd);
                return;
            }

            if (cmd != null && cmd.TraceId == 0)
            {
                cmd.TraceId = ++_nextTraceSeq;
                if (cmd.TraceId == 0) cmd.TraceId = ++_nextTraceSeq;
                lock (_localCmdSentTs)
                {
                    if (_localCmdSentTs.Count > 512) _localCmdSentTs.Clear();
                    _localCmdSentTs[cmd.TraceId] = Stopwatch.GetTimestamp();
                }
            }

            if (Transport.IsHost)
            {
                lock (_hostTurnBuffer)
                {
                    if (!TryMergeIntoTailCommand(_hostTurnBuffer, cmd))
                        _hostTurnBuffer.Add(cmd);
                }
            }
            else
            {
                lock (_localOutgoingQueue)
                {
                    if (!TryMergeIntoTailCommand(_localOutgoingQueue, cmd))
                        _localOutgoingQueue.Add(cmd);
                }
                if (Transport != null && Transport.IsActive)
                    FlushOutgoingClientCommands();
            }
        }

        private static bool TryMergeIntoTailCommand(List<NetCommand> list, NetCommand incoming)
        {
            if (list == null || list.Count == 0 || incoming == null) return false;
            if (incoming.Type != NetCommandType.ExecuteCommand) return false;
            if (incoming.ExecutorEntityIds == null || incoming.ExecutorEntityIds.Length == 0) return false;

            var prev = list[list.Count - 1];
            if (prev == null || prev.Type != NetCommandType.ExecuteCommand) return false;
            if (prev.PlayerId != incoming.PlayerId
                || prev.CommandId != incoming.CommandId
                || prev.IsShiftQueued != incoming.IsShiftQueued
                || prev.TargetEntityId != incoming.TargetEntityId
                || prev.TargetPosition != incoming.TargetPosition
                || prev.TargetSize != incoming.TargetSize
                || prev.TargetOrigin != incoming.TargetOrigin
                || prev.ExecutorEntityIds == null
                || prev.ExecutorEntityIds.Length == 0)
            {
                return false;
            }

            var merged = new List<ulong>(prev.ExecutorEntityIds.Length + incoming.ExecutorEntityIds.Length);
            merged.AddRange(prev.ExecutorEntityIds);
            for (int i = 0; i < incoming.ExecutorEntityIds.Length; i++)
            {
                ulong id = incoming.ExecutorEntityIds[i];
                if (!merged.Contains(id))
                    merged.Add(id);
            }
            prev.ExecutorEntityIds = merged.ToArray();
            return true;
        }

        /// <summary>
        /// Call once both peers are inside the same synced level. Installs the turn-input gate and
        /// lets the simulation run the first InputDelayTicks immediately while broadcasts catch up.
        /// </summary>
        public static void EnterSyncedLevel()
        {
            if (!IsSessionActive) return;

            _pendingTurnCommands.Clear();
            CommandInterceptor.ClearPendingLocalBuilds();
            _hostLastBroadcastSimTick = -1;
            _hostLastBroadcastTargetTick = -1;
            _gateWaitStartTicks = -1.0;
            _desyncDumped = false;
            _firstChecksumPosDumped = false;
            _bucketDiffLogged = false;
            _networkGate = new TurnInputTickGate();
            LockstepClock.TickGate = _networkGate;

            int cur = DXGame.Current?.IAIterationInt ?? 0;
            int initialDelay = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, InputDelayTicks));
            _networkGate.MarkReady(cur + initialDelay);
            _hostLastBroadcastSimTick = cur;
            _hostLastBroadcastTargetTick = cur + initialDelay;
            IsInSyncedLevel = true;
            CommandInterceptor.IsInterceptionEnabled = true;
            RoomPhase = LobbyRoomPhase.InGame;
            HostActionHint = string.Empty;
            if (Transport.IsHost)
                BroadcastLobbyRoster();
            LobbyStateChanged?.Invoke();

            // The save loader leaves DXGame.Paused/GameSpeed at 0; clear it so engine animations and
            // the fixed-step clock start running. Session pauses are tracked separately.
            LockstepClock.SetSessionPause(false);
            LockstepClock.ResetHostTickPacing();
            if (DXGame.Current != null)
            {
                if (DXGame.Current.Paused) DXGame.Current.Paused = false;
                if (DXGame.Current.GameSpeed <= 0.0) DXGame.Current.GameSpeed = 1.0;
                if (!DXGame.Current.InputEnabled)
                {
                    DXGame.Current.InputEnabled = true;
                    ModLogger.Info("[CoopSessionManager] Restored DXGame.InputEnabled=true on synced level entry.");
                }
            }
            if (GameBridge.CurrentGameSystem is DXSystem activeSys && !activeSys.Enabled)
            {
                activeSys.Enabled = true;
                ModLogger.Info("[CoopSessionManager] Re-enabled CurrentGameSystem on synced level entry.");
            }
            GameBridge.ClearWaitCursor();
            GameBridge.ClearLoadingFadeOverlay();

            ModLogger.Info($"[CoopSessionManager] Entered synced level at tick {cur} (inputDelay={InputDelayTicks}, role={(Transport.IsHost ? "Host" : "Client")}).");
            _segmentStartTick = cur;
            LockstepClock.LogBaselineFingerprint($"enter-synced/{(Transport.IsHost ? "Host" : "Client")}");
        }

        /// <summary>
        /// Executes every buffered turn command for ticks &lt;= nextSimTick, in ascending tick order.
        /// Called from the deterministic clock prefix at the moment a tick is allowed to advance.
        /// Must not touch local selection state (see CommandExecutor).
        /// </summary>
        public static void FlushDueTurnCommands(int nextSimTick)
        {
            while (true)
            {
                List<NetCommand> due = null;
                lock (_pendingTurnCommands)
                {
                    if (_pendingTurnCommands.Count == 0) return;
                    var first = _pendingTurnCommands.GetEnumerator();
                    if (!first.MoveNext() || first.Current.Key > nextSimTick) return;
                    due = first.Current.Value;
                    _pendingTurnCommands.Remove(first.Current.Key);
                }

                foreach (var cmd in due)
                {
                    if (cmd != null && cmd.TraceId != 0 && Transport != null && cmd.PlayerId == Transport.LocalPlayerId)
                    {
                        long sentTs = 0;
                        bool found = false;
                        lock (_localCmdSentTs)
                        {
                            if (_localCmdSentTs.TryGetValue(cmd.TraceId, out sentTs))
                            {
                                _localCmdSentTs.Remove(cmd.TraceId);
                                found = true;
                            }
                        }
                        if (found && sentTs > 0)
                        {
                            double e2eMs = (Stopwatch.GetTimestamp() - sentTs) * 1000.0 / Stopwatch.Frequency;
                            if (e2eMs >= 0.0 && e2eMs < 30000.0)
                            {
                                lock (_cmdE2ELatencySamplesMs)
                                {
                                    if (_cmdE2ELatencySamplesMs.Count >= 256)
                                        _cmdE2ELatencySamplesMs.RemoveAt(0);
                                    _cmdE2ELatencySamplesMs.Add(e2eMs);
                                }
                            }
                        }
                    }
                    RecordDeterministicCommandOwnership(cmd, nextSimTick);
                    CommandExecutor.Execute(cmd);
                }
            }
        }

        private static void RecordDeterministicCommandOwnership(NetCommand cmd, int simTick)
        {
            if (cmd == null || cmd.PlayerId == 0 || cmd.ExecutorEntityIds == null || cmd.ExecutorEntityIds.Length == 0)
                return;
            if (cmd.Type != NetCommandType.GroupMoveOrPatrol
                && cmd.Type != NetCommandType.ExecuteCommand
                && cmd.Type != NetCommandType.CancelCurrentOrder)
                return;

            var game = DXGame.Current;
            if (game == null) return;
            Type movableType = GameBridge.TypeCMovable;

            lock (_unitOwnershipSync)
            {
                if (_unitOwnership.Count > 512)
                {
                    var stale = new List<ulong>();
                    foreach (var kv in _unitOwnership)
                    {
                        int age = simTick - kv.Value.LastOrderTick;
                        if (age < 0 || age > OwnershipLeaseTicks)
                            stale.Add(kv.Key);
                    }
                    for (int i = 0; i < stale.Count; i++)
                        _unitOwnership.Remove(stale[i]);
                }

                for (int i = 0; i < cmd.ExecutorEntityIds.Length; i++)
                {
                    ulong id = cmd.ExecutorEntityIds[i];
                    DXEntity e = game.GetEntityByID(id);
                    if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic) continue;
                    if (!GameBridge.IsPlayerEntity(e)) continue;
                    if (movableType != null && !GameBridge.HasComponent(e, movableType)) continue;
                    _unitOwnership[id] = new UnitOwnershipEntry { PlayerId = cmd.PlayerId, LastOrderTick = simTick };
                }
            }
        }

        private static bool TryGetActiveUnitOwner(ulong entityId, int curTick, DXEntity entity, out byte ownerPlayerId)
        {
            ownerPlayerId = 0;
            if (entity == null || entity.Disposed) return false;

            UnitOwnershipEntry entry;
            lock (_unitOwnershipSync)
            {
                if (!_unitOwnership.TryGetValue(entityId, out entry))
                    return false;
            }

            int age = curTick - entry.LastOrderTick;
            if (age < 0 || age > OwnershipLeaseTicks)
            {
                lock (_unitOwnershipSync) _unitOwnership.Remove(entityId);
                return false;
            }

            if (age > OwnershipIdleGraceTicks && !GameBridge.IsEntityActivelyMoving(entity))
            {
                lock (_unitOwnershipSync) _unitOwnership.Remove(entityId);
                return false;
            }

            ownerPlayerId = entry.PlayerId;
            return ownerPlayerId > 0;
        }

        private static List<OwnedUnitPosSample> CollectClientOwnedActiveUnits(int curTick, List<StateHasher.EntityPosSample> dynamicPositions)
        {
            if (Transport == null || Transport.IsHost || !EnableAsyncPosAlign || dynamicPositions == null || dynamicPositions.Count == 0)
                return null;
            var game = DXGame.Current;
            if (game == null) return null;

            byte localPid = Transport.LocalPlayerId;
            if (localPid <= 1) return null;

            Dictionary<ulong, UnitOwnershipEntry> ownedSnapshot = null;
            List<ulong> expired = null;

            lock (_unitOwnershipSync)
            {
                if (_unitOwnership.Count == 0) return null;
                foreach (var kv in _unitOwnership)
                {
                    int age = curTick - kv.Value.LastOrderTick;
                    if (age < 0 || age > OwnershipLeaseTicks)
                    {
                        if (expired == null) expired = new List<ulong>();
                        expired.Add(kv.Key);
                        continue;
                    }
                    if (kv.Value.PlayerId != localPid) continue;

                    DXEntity e = game.GetEntityByID(kv.Key);
                    if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic)
                    {
                        if (expired == null) expired = new List<ulong>();
                        expired.Add(kv.Key);
                        continue;
                    }
                    if (age > OwnershipIdleGraceTicks && !GameBridge.IsEntityActivelyMoving(e))
                    {
                        if (expired == null) expired = new List<ulong>();
                        expired.Add(kv.Key);
                        continue;
                    }

                    if (ownedSnapshot == null) ownedSnapshot = new Dictionary<ulong, UnitOwnershipEntry>(16);
                    ownedSnapshot[kv.Key] = kv.Value;
                }

                if (expired != null)
                {
                    for (int i = 0; i < expired.Count; i++)
                        _unitOwnership.Remove(expired[i]);
                }
            }

            if (ownedSnapshot == null || ownedSnapshot.Count == 0)
                return null;

            var result = new List<OwnedUnitPosSample>(Math.Min(ownedSnapshot.Count, MaxReverseAlignUnitsPerReport));
            for (int i = 0; i < dynamicPositions.Count && result.Count < MaxReverseAlignUnitsPerReport; i++)
            {
                var s = dynamicPositions[i];
                if (!s.IsDynamic || !ownedSnapshot.ContainsKey(s.Id)) continue;
                result.Add(new OwnedUnitPosSample
                {
                    Id = s.Id,
                    X = s.X,
                    Y = s.Y
                });
            }

            return result.Count > 0 ? result : null;
        }

        private static void MonitorGateWait()
        {
            if (_levelEndedSynced || GameBridge.IsLevelEnded())
            {
                _gateWaitStartTicks = -1.0;
                return;
            }

            int cur = DXGame.Current?.IAIterationInt ?? 0;
            double now = DXHelper_Environment.Ticks;

            if (now <= _clientHostPauseKeepaliveUntilTicks)
            {
                _gateWaitStartTicks = -1.0;
                if (now - _gateLastWarnTicks >= 1500.0)
                {
                    _gateLastWarnTicks = now;
                    if (_clientHostInModalPause)
                        GameBridge.ShowTopBanner("Host paused (dialog/pause open)...", Color.LightSkyBlue, 900);
                }
                return;
            }

            if (cur != _gateLastObservedTick)
            {
                _gateLastObservedTick = cur;
                _gateWaitStartTicks = -1.0;
            }

            if (_networkGate.IsTickReady(cur + 1))
            {
                _gateWaitStartTicks = -1.0;
                return;
            }

            if (_gateWaitStartTicks < 0.0)
            {
                _gateWaitStartTicks = now;
                return;
            }

            if (now - _gateWaitStartTicks >= GateTimeoutMs && now - _gateLastWarnTicks >= 1000.0)
            {
                _gateLastWarnTicks = now;
                double waited = (now - _gateWaitStartTicks) / 1000.0;
                ModLogger.Warn($"[CoopSessionManager] Tick gate waiting {waited:F1}s at tick {cur} — packets from Host are delayed or lost.");
                if (waited >= GateTimeoutMs / 1000.0)
                    GameBridge.ShowTopBanner("Waiting for Host network...", Color.Orange, 1200);
            }
        }

        /// <summary>Called from LevelLoadCompletePatch on the main thread after a level fully loads.</summary>
        public static void OnLevelLoadComplete()
        {
            if (!IsSessionActive) return;

            if (Transport.IsHost)
            {
                // Freeze immediately at 0 elapsed ticks until snapshot export and client handshake complete.
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null) DXGame.Current.InputEnabled = false;
                if (_hostSnapshotReloadPending && _hostWaitingOwnReload)
                {
                    _hostWaitingOwnReload = false;
                    ModLogger.Info($"[CoopSessionManager] Host symmetric snapshot reload finished at tick={DXGame.Current?.IAIterationInt ?? 0} (clientAck={_clientSnapshotAckReceived}).");
                    byte[] savBytes = _pendingBroadcastSavBytes;
                    _pendingBroadcastSavBytes = null;
                    if (savBytes != null && Transport != null && Transport.IsHost)
                    {
                        string chkText = _pendingBroadcastChkText ?? string.Empty;
                        _pendingBroadcastChkText = null;
                        int syncSeed = _pendingBroadcastSeed;
                        long idCounter = _pendingBroadcastIdCounter;
                        int totalChunks = (savBytes.Length + ChunkSizeBytes - 1) / ChunkSizeBytes;
                        _awaitingClientSnapshotAck = true;
                        _awaitingClientAckStartTicks = DXHelper_Environment.Ticks;
                        ModLogger.Info($"[CoopSessionManager] Broadcasting Snapshot ({savBytes.Length:N0} bytes in {totalChunks} chunks, seed={syncSeed}, idCounter={idCounter})...");
                        GameBridge.ShowTopBanner("Waiting for Client to finish loading...", Color.Cyan, 4000);
                        Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotBegin, w =>
                        {
                            w.Write(savBytes.Length);
                            w.Write(totalChunks);
                            w.Write(chkText);
                            w.Write(syncSeed);
                            w.Write(idCounter);
                            // Append-only: peers on an older build stop reading here, newer peers
                            // adopt the Host's clock origin so both sides run identical physics steps.
                            if (w.BaseStream.CanWrite)
                                w.Write(LockstepClock.SharedClockOriginMs);
                        });
                        for (int i = 0; i < totalChunks; i++)
                        {
                            int offset = i * ChunkSizeBytes;
                            int len = Math.Min(ChunkSizeBytes, savBytes.Length - offset);
                            int chunkIndex = i;
                            Transport.Broadcast(SendChannel.ReliableOrdered, PacketType.SnapshotChunk, w =>
                            {
                                w.Write(chunkIndex);
                                w.Write(len);
                                w.Write(savBytes, offset, len);
                            });
                        }
                    }
                    TryCompleteHostSyncBarrier();
                    return;
                }
                if (!IsInSyncedLevel && !_hostSnapshotReloadPending)
                {
                    SetHostRoomPhase(LobbyRoomPhase.HostLoadingLevel, "正在同步关卡...");
                    RequestSnapshotBroadcast();
                }
                return;
            }

            if (!_awaitingSnapshotLoad) return;

            _awaitingSnapshotLoad = false;
            IsInSyncedLevel = false;
            LockstepClock.SetSessionPause(true);
            if (DXGame.Current != null) DXGame.Current.InputEnabled = false;
            int clientTick = DXGame.Current?.IAIterationInt ?? 0;
            Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.SnapshotLoadedAck, w =>
            {
                w.Write(clientTick);
            });
            ModLogger.Info($"[CoopSessionManager] Client finished loading snapshot at tick={clientTick}; sent SnapshotLoadedAck and waiting for SessionStart.");
        }

        /// <summary>Records the snapshot seed so the post-load calibration uses the Host's seed.</summary>
        public static void RequestSnapshotCalibration(int seed, long entityIdCounter = 0)
        {
            _awaitingSnapshotLoad = true;
            LockstepClock.EnableDeterministicSandbox(seed);
            LockstepClock.RequestPostLoadCalibration(seed, entityIdCounter);
        }

        private static void TryHandlePendingSteamInvite()
        {
            if (GameBridge.CurrentLevelState != null || GameBridge.IsGameLoading()) return;
            string inviteLobbyId = SteamP2PTransport.ConsumePendingInviteLobbyId();
            if (string.IsNullOrEmpty(inviteLobbyId)) return;

            ModLogger.Info($"[CoopSessionManager] Auto-joining invited Steam Lobby {inviteLobbyId} on Main Menu...");
            try
            {
                JoinSession(false, inviteLobbyId);
                ShouldOpenLobbyOnMainMenu = true;
                CoopLobbyWindow.ShowLobbyWindow();
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CoopSessionManager] Failed to auto-join invited Steam Lobby {inviteLobbyId}", ex);
                StopSession();
            }
        }

        public static void OnFrameTick()
        {
            var frameGame = DXGame.Current;
            if (frameGame == null || frameGame.Disposing)
                return;

            if (!TestHarnessConfig.Enabled && !TestHarnessConfig.ControlEnabled)
            {
                SteamP2PTransport.PumpSteamCallbacksIfNeeded(Transport);
                TryHandlePendingSteamInvite();
            }

            if (_pendingClientReturnToMainMenu)
            {
                _pendingClientReturnToMainMenu = false;
                if (GameBridge.CurrentLevelState != null)
                {
                    ModLogger.Info("[CoopSessionManager] Host returned to lobby -> returning Client to Main Menu...");
                    _suppressReturnNotice = true;
                    IsInSyncedLevel = false;
                    CommandInterceptor.IsInterceptionEnabled = false;
                    CommandInterceptor.ClearPendingLocalBuilds();
                    LockstepClock.SetSessionPause(false);
                    GameBridge.ReturnToMainMenu();
                    return;
                }
            }

            CoopLobbyWindow.Tick();

            if (!string.IsNullOrEmpty(_pendingVersionMismatchAlert)
                && !TestHarnessConfig.Enabled
                && !TestHarnessConfig.ControlEnabled
                && !GameBridge.IsGameLoading())
            {
                string alertMsg = _pendingVersionMismatchAlert;
                _pendingVersionMismatchAlert = null;
                try
                {
                    GameBridge.ShowAlertMessageBox("TABCoopMod 版本不一致警告", alertMsg);
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[CoopSessionManager] Failed to show version mismatch alert modal: {ex.Message}");
                }
            }

            if (!IsSessionActive || Transport == null) return;

            // Deferred, top-of-frame (shallow stack) work: export never runs inside the loader
            // postfix; client load never runs inside packet delivery.
            if (_pendingHostSnapshotExport)
            {
                _pendingHostSnapshotExport = false;
                BroadcastCurrentLevelSnapshot();
            }
            if (_pendingClientSnapshotLoadPath != null
                && GameBridge.CurrentGameSystem != null
                && !GameBridge.IsGameLoading()
                && (GameBridge.CurrentLevelState != null || !GameBridge.IsStartupFadeActive()))
            {
                string path = _pendingClientSnapshotLoadPath;
                _pendingClientSnapshotLoadPath = null;
                BeginClientSnapshotLoad(path);
            }

            Transport?.PollEvents();
            ProbeNetworkRtt();

            // Keep the engine's loader fades advancing while the Client waits to enter the level.
            TickLoadingFade();

            if (!IsSessionActive || Transport == null || DXGame.Current == null || DXGame.Current.Disposing)
                return;

            // Keep Host paused and hold turn broadcasts while waiting for Host reload + Client ack.
            if (Transport.IsHost && (_hostWaitingOwnReload || _awaitingClientSnapshotAck))
            {
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null && DXGame.Current.InputEnabled) DXGame.Current.InputEnabled = false;
                if (!_hostWaitingOwnReload && _awaitingClientAckStartTicks > 0.0 && DXHelper_Environment.Ticks - _awaitingClientAckStartTicks > 60000.0)
                {
                    ModLogger.Warn("[CoopSessionManager] Timed out (60s) waiting for Client SnapshotLoadedAck; resuming Host.");
                    _hostSnapshotReloadPending = false;
                    _awaitingClientSnapshotAck = false;
                    _awaitingClientAckStartTicks = -1.0;
                    _clientSnapshotAckReceived = false;
                    EnterSyncedLevel();
                }
                else
                {
                    return;
                }
            }

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || GameBridge.CurrentLevelState == null)
                return;

            DrainAsyncQueues();

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || GameBridge.CurrentLevelState == null)
                return;

            // 1. Flush Client outgoing commands to Host
            if (!Transport.IsHost)
                FlushOutgoingClientCommands();

            // 2. Host broadcasts TurnInputBroadcast while paused (session or engine/modal pause) or before
            //    first tick from OnFrameTick; during active sim turns are sealed in OnSimulationTickStarting.
            if (Transport.IsHost)
            {
                if (!_levelEndedSynced && GameBridge.IsLevelEnded())
                    NotifyHostLevelEnded(GameBridge.GetLevelEndStatus() == 1);

                var gx = DXGame.Current;
                bool hostEnginePaused = gx != null && gx.Paused;
                if (!_levelEndedSynced && (hostEnginePaused || LockstepClock.PausedBySession || _hostLastBroadcastSimTick < 0))
                {
                    int cur = gx?.IAIterationInt ?? 0;
                    EmitHostTurns(cur);
                    if (hostEnginePaused || LockstepClock.PausedBySession)
                    {
                        double nowKeep = DXHelper_Environment.Ticks;
                        if (nowKeep - _hostLastPauseKeepaliveSentTicks >= 250.0 || nowKeep < _hostLastPauseKeepaliveSentTicks)
                        {
                            _hostLastPauseKeepaliveSentTicks = nowKeep;
                            bool inModal = GameBridge.IsAnyModalOpen();
                            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.HostPauseKeepalive, w =>
                            {
                                w.Write(cur);
                                w.Write(inModal);
                                w.Write(LockstepClock.PausedBySession);
                            });
                        }
                    }
                }
            }
            else
            {
                MonitorGateWait();
            }

            LogPeriodicLatencyStats();

            // While paused (by session or local modal pause such as Mayor selection), UpdateGameLogic
            // is frozen, so flush any authorized turn commands stamped <= pausedTick on the main thread
            // so a peer's Mayor pick can close this machine's open Mayor modal immediately.
            bool isLocalEnginePaused = DXGame.Current != null && DXGame.Current.Paused;
            if ((LockstepClock.PausedBySession || isLocalEnginePaused)
                && !LockstepClock.LoadingLevel
                && !GameBridge.IsLevelEnded()
                && GameBridge.IsCurrentLevelSystemEnabled())
            {
                int pausedTick = DXGame.Current?.IAIterationInt ?? 0;
                if (_networkGate == null || _networkGate.IsTickReady(pausedTick))
                    LockstepClock.RunInDeterministicContext(() => FlushDueTurnCommands(pausedTick));
            }

            // 3. Broadcast Ally Cursor & Camera Viewport at 20Hz (every 50ms)
            double nowTicks = DXHelper_Environment.Ticks;
            if (nowTicks - _lastCursorBroadcastTicks >= 50.0 && DXScene.Current != null)
            {
                _lastCursorBroadcastTicks = nowTicks;
                var camArea = DXScene.Current.CameraArea;
                var mouseWorldPos = GetLocalMouseWorldPos();

                Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.AllyCursorAndCamera, w =>
                {
                    w.Write(Transport.LocalPlayerId);
                    w.Write(mouseWorldPos.X);
                    w.Write(mouseWorldPos.Y);
                    w.Write(camArea.X);
                    w.Write(camArea.Y);
                    w.Write(camArea.Width);
                    w.Write(camArea.Height);
                });
            }

            // 4. Initial StateHash baseline on session entry (per-tick epoch steps are driven
            //    sequentially by OnSimulationTickCompleted so multi-tick catchup never skips a tick).
            int entryTick = DXGame.Current?.IAIterationInt ?? 0;
            if (entryTick > 0 && _lastCheckedSimTick < 0)
                RunEpochHashAndChecksum(entryTick);
        }

        public static void NotifyHostLevelEnded(bool won)
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || !Transport.IsHost)
                return;
            if (_levelEndedSynced)
                return;

            _levelEndedSynced = true;
            _clientHostPauseKeepaliveUntilTicks = -1.0;
            _clientHostInModalPause = false;
            LockstepClock.SetSessionPause(false);
            PauseSyncController.CancelCountdown();

            int tick = DXGame.Current?.IAIterationInt ?? 0;
            ModLogger.Info($"[CoopSessionManager] Host level ended (won={won}, tick={tick}) -> broadcasting PacketType.LevelEnded to clients.");
            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.LevelEnded, w =>
            {
                w.Write(won);
                w.Write(tick);
            });
        }

        private static void OnClientLevelEndedReceived(bool won, int endTick)
        {
            if (_levelEndedSynced || GameBridge.IsLevelEnded())
                return;

            _levelEndedSynced = true;
            _clientHostPauseKeepaliveUntilTicks = -1.0;
            _clientHostInModalPause = false;
            _gateWaitStartTicks = -1.0;
            LockstepClock.SetSessionPause(false);
            PauseSyncController.CancelCountdown();

            ModLogger.Info($"[CoopSessionManager] Client received LevelEnded(won={won}, endTick={endTick}) from Host -> showing GameEnd modal.");
            IsReceivingRemoteLevelEnd = true;
            try
            {
                if (DXVision.GUI.DXModalScreen.IsInModal)
                    DXVision.GUI.DXModalScreen.CloseAll();
                GameBridge.ShowSyncedGameEnd(won);
            }
            finally
            {
                IsReceivingRemoteLevelEnd = false;
            }
        }

        /// <summary>
        /// Polls transport and flushes pending Client commands right before UpdateGameLogic chooses
        /// how many ticks to advance this frame, reducing Client->Host->Client input roundtrip lag.
        /// </summary>
        public static void PumpNetworkBeforeLogic()
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;
            if (!Transport.IsHost)
                FlushOutgoingClientCommands();
            Transport.PollEvents();
            ProbeNetworkRtt();
        }

        /// <summary>
        /// Called synchronously right before a simulation tick starts advancing, so Host seals the
        /// turn at the exact tick boundary instead of 17ms early on a render frame.
        /// </summary>
        public static void OnSimulationTickStarting(int nextTick)
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;
            if (Transport.IsHost)
                EmitHostTurns(nextTick);
        }

        /// <summary>
        /// Called synchronously after each single simulation tick finishes during UpdateGameLogic.
        /// Keeps incremental epoch hashing lockstep-aligned even when a slow render frame runs 2..8
        /// ticks to maintain 30Hz game speed.
        /// </summary>
        public static void OnSimulationTickCompleted(int completedTick)
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;

            if (Transport.IsHost && (LockstepClock.PausedBySession || Testing.DeterministicReplayVerifier.IsRunning))
                EmitHostTurns(completedTick);

            if (completedTick > 0)
            {
                TryLogTickTrace(completedTick);
                RunEpochHashAndChecksum(completedTick);
            }
        }

        /// <summary>
        /// DIAGNOSTIC ONLY (TABCOOP_TRACE_FIRST_TICKS=N). Logs a full digest for every tick within the
        /// first N ticks of a synced level, so the two peers' logs reveal the exact tick on which the
        /// position checksum first diverges. Nothing is sent over the wire.
        /// </summary>
        private static void TryLogTickTrace(int completedTick)
        {
            int n = Testing.TestHarnessConfig.TraceFirstTicks;
            if (n <= 0 || _segmentStartTick < 0) return;
            int rel = completedTick - _segmentStartTick;
            if (rel < 0 || rel > n) return;
            try
            {
                var d = StateHasher.ComputeDigest();
                ModLogger.Info($"[TickTrace] rel={rel} tick={d.SimTick} Pos={d.PosChecksum:X16} Coarse={d.CoarsePosChecksum:X16} " +
                               $"Life={d.LifeChecksum:X16} Aggro={d.ZombieAggroChecksum:X16}({d.AggroZombieCount}) Proj={d.ProjectileChecksum:X16} " +
                               $"Prod={d.ProductionChecksum:X16} Resv={d.ReservationChecksum:X16} entities={d.EntityCount} proj={d.ProjectileCount} " +
                               $"time={d.GameTimeMs} phys={d.PhysicsUpdates}");
            }
            catch (Exception ex) { ModLogger.Warn($"[TickTrace] failed: {ex.Message}"); }
        }

        private static void FlushOutgoingClientCommands()
        {
            List<NetCommand> toSend = null;
            lock (_localOutgoingQueue)
            {
                if (_localOutgoingQueue.Count > 0)
                {
                    toSend = new List<NetCommand>(_localOutgoingQueue);
                    _localOutgoingQueue.Clear();
                }
            }

            if (toSend != null)
            {
                Transport.SendToPeer(1, SendChannel.ReliableCommand, PacketType.ClientInputReport, w =>
                {
                    w.Write((ushort)toSend.Count);
                    foreach (var c in toSend) c.WriteTo(w);
                });
            }
        }

        private static void EmitHostTurns(int cur)
        {
            if (!Transport.IsHost || _levelEndedSynced || GameBridge.IsLevelEnded()) return;

            // Use the synchronized pause flag, or engine/modal pause when a game modal opens:
            // during modal pause UpdateGameLogic is frozen so we must not keep advancing targetTick
            // into the future, while still flushing buffered commands and sending HostPauseKeepalive.
            bool paused = LockstepClock.PausedBySession || (DXGame.Current != null && DXGame.Current.Paused);
            bool hasTurn = false;
            lock (_hostTurnBuffer) hasTurn = _hostTurnBuffer.Count > 0;
            if (paused)
            {
                if (!hasTurn) return;
                BroadcastHostTurn(cur, drainBuffer: true);
                return;
            }

            if (cur <= _hostLastBroadcastSimTick && !hasTurn)
                return;
            if (cur > _hostLastBroadcastSimTick)
                _hostLastBroadcastSimTick = cur;

            int delay = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, InputDelayTicks));
            int desiredTarget = cur + delay;
            if (!hasTurn && desiredTarget <= _hostLastBroadcastTargetTick)
                return;

            int targetTick = desiredTarget > _hostLastBroadcastTargetTick
                ? desiredTarget
                : _hostLastBroadcastTargetTick + 1;

            if (_hostLastBroadcastTargetTick >= 0)
            {
                for (int fillTick = _hostLastBroadcastTargetTick + 1; fillTick < targetTick; fillTick++)
                {
                    BroadcastHostTurn(fillTick, drainBuffer: false);
                }
            }
            if (targetTick > _hostLastBroadcastTargetTick)
                _hostLastBroadcastTargetTick = targetTick;

            BroadcastHostTurn(targetTick, drainBuffer: true);
        }

        private static void BroadcastHostTurn(int targetTick, bool drainBuffer)
        {
            List<NetCommand> turnCmds = null;
            if (drainBuffer)
            {
                lock (_hostTurnBuffer)
                {
                    if (_hostTurnBuffer.Count > 0)
                    {
                        turnCmds = new List<NetCommand>(_hostTurnBuffer);
                        _hostTurnBuffer.Clear();
                    }
                }
            }

            if (turnCmds != null)
            {
                foreach (var c in turnCmds) c.TargetSimTick = targetTick;
                lock (_pendingTurnCommands)
                {
                    if (!_pendingTurnCommands.TryGetValue(targetTick, out var slot))
                        _pendingTurnCommands[targetTick] = slot = new List<NetCommand>();
                    slot.AddRange(turnCmds);
                }
            }

            _networkGate.MarkReady(targetTick);

            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.TurnInputBroadcast, w =>
            {
                w.Write(targetTick);
                w.Write((ushort)(turnCmds?.Count ?? 0));
                if (turnCmds != null)
                    foreach (var c in turnCmds) c.WriteTo(w);
            });
        }

        private static void ProbeNetworkRtt()
        {
            if (Transport == null || !Transport.IsHost || !HasJoinedClient) return;
            if (LockstepClock.LoadingLevel) return;
            bool inSyncedGame = IsInSyncedLevel && GameBridge.CurrentLevelState != null;
            bool inLobby = RoomPhase == LobbyRoomPhase.InLobby || RoomPhase == LobbyRoomPhase.HostSelectingMenu;
            if (!inSyncedGame && !inLobby) return;

            double now = DXHelper_Environment.Ticks;
            if (now - _lastRttProbeTicks < RttProbeIntervalMs && now >= _lastRttProbeTicks)
                return;

            _lastRttProbeTicks = now;
            uint seq = ++_rttPingSeq;
            long timestamp = Stopwatch.GetTimestamp();
            float hostSmoothedRtt = (float)CurrentSmoothedRttMs;
            Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.RttPing, w =>
            {
                w.Write(seq);
                w.Write(timestamp);
                w.Write(hostSmoothedRtt);
            });
        }

        private static void UpdateAdaptiveInputDelay()
        {
            if (Transport == null || !Transport.IsHost) return;

            double now = DXHelper_Environment.Ticks;
            double maxRtt = -1.0;
            foreach (var p in GetEffectivePeers())
            {
                if (p.PlayerId == 1) continue;
                if (_peerSmoothedRttMs.TryGetValue(p.PlayerId, out double rtt)
                    && _peerRttSampleTicks.TryGetValue(p.PlayerId, out double sampleTs)
                    && (now - sampleTs <= RttStalePeerMs || now < sampleTs))
                {
                    if (rtt > maxRtt) maxRtt = rtt;
                }
            }

            if (maxRtt < 0.0)
            {
                CurrentSmoothedRttMs = 0.0;
                return;
            }

            CurrentSmoothedRttMs = maxRtt;

            int cur = InputDelayTicks;
            int desired = cur;
            if (maxRtt > 135.0) desired = 4;
            else if (maxRtt > 92.0) desired = (cur == 4 && maxRtt >= 115.0) ? 4 : 3;
            else if (maxRtt > 52.0) desired = (cur >= 3 && maxRtt >= 75.0) ? 3 : 2;
            else if (cur == 2 && maxRtt >= 44.0) desired = 2;
            else desired = 1;

            desired = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, desired));
            if (desired == cur) return;
            if (now - _lastDelayAdjustTicks < DelayAdjustCooldownMs && now >= _lastDelayAdjustTicks)
                return;

            _lastDelayAdjustTicks = now;
            InputDelayTicks = desired;
            ModLogger.Info($"[AdaptiveDelay] InputDelayTicks {cur} -> {desired} (smoothedRtt={maxRtt:F1}ms)");
        }

        private static void UpdatePeerSimTick(byte peerId, int simTick)
        {
            if (peerId == 0 || peerId == 1 || simTick < 0) return;
            if (_peerLastSimTick.TryGetValue(peerId, out int prevTick) && simTick < prevTick)
            {
                // Ignore stale out-of-order UDP RttPong samples, unless a snapshot reload reset tick downward.
                if (prevTick - simTick < 300) return;
            }
            _peerLastSimTick[peerId] = simTick;
            _peerLastSimTickWallMs[peerId] = DXHelper_Environment.Ticks;
        }

        private static bool TryGetSlowestClientLagTicks(out int maxLagTicks)
        {
            maxLagTicks = 0;
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || !Transport.IsHost)
                return false;

            int hostTick = DXGame.Current?.IAIterationInt ?? 0;
            double now = DXHelper_Environment.Ticks;
            bool found = false;

            foreach (var p in GetEffectivePeers())
            {
                if (p.PlayerId == 1) continue;
                if (_peerLastSimTick.TryGetValue(p.PlayerId, out int peerTick)
                    && _peerLastSimTickWallMs.TryGetValue(p.PlayerId, out double sampleTs)
                    && now >= sampleTs
                    && (now - sampleTs) <= ClientLagStaleMs)
                {
                    int lag = Math.Max(0, hostTick - peerTick);
                    if (!found || lag > maxLagTicks)
                    {
                        maxLagTicks = lag;
                        found = true;
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Backpressure for Host tick pacing so a slower Client cannot fall seconds behind Host
        /// and accumulate multi-second input/pause latency. Bounded by ClientLagStaleMs (3.5s)
        /// so a frozen/disconnected Client can never deadlock Host.
        /// </summary>
        internal static void GetHostPaceAdjustment(out double paceMultiplier, out int maxTicks)
        {
            paceMultiplier = 1.0;
            maxTicks = 3;

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null || !Transport.IsHost)
                return;
            if (LockstepClock.PausedBySession || _awaitingClientSnapshotAck || _hostWaitingOwnReload
                || Testing.DeterministicReplayVerifier.IsRunning)
                return;
            if (!TryGetSlowestClientLagTicks(out int maxLagTicks))
                return;

            if (maxLagTicks >= HostBackpressureHoldLagTicks)
            {
                maxTicks = 0;
            }
            else if (maxLagTicks >= HostBackpressureSlowLagTicks)
            {
                maxTicks = 1;
                paceMultiplier = 1.35;
            }
        }

        private static void LogPeriodicLatencyStats()
        {
            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;
            double now = DXHelper_Environment.Ticks;
            if (now - _lastLatencyStatsLogTicks < 10000.0 && now >= _lastLatencyStatsLogTicks)
                return;
            _lastLatencyStatsLogTicks = now;

            double avgMs = 0.0, p95Ms = 0.0, maxMs = 0.0;
            int count;
            lock (_cmdE2ELatencySamplesMs)
            {
                count = _cmdE2ELatencySamplesMs.Count;
                if (count > 0)
                {
                    var copy = new List<double>(_cmdE2ELatencySamplesMs);
                    _cmdE2ELatencySamplesMs.Clear();
                    copy.Sort();
                    double sum = 0.0;
                    for (int i = 0; i < copy.Count; i++) sum += copy[i];
                    avgMs = sum / count;
                    int p95Idx = Math.Min(count - 1, (int)Math.Floor((count - 1) * 0.95));
                    p95Ms = copy[p95Idx];
                    maxMs = copy[count - 1];
                }
            }

            var diag = Transport as ITransportDiagnostics;
            string path = diag != null ? diag.ActivePathSummary : Transport.GetType().Name;
            double directRtt = diag != null ? diag.ActiveDirectRttMs : -1.0;
            string statsLine = diag != null ? diag.GetDebugStatsLine() : string.Empty;
            string role = Transport.IsHost ? "Host" : "Client";

            string lagPart = string.Empty;
            if (Transport.IsHost)
            {
                if (TryGetSlowestClientLagTicks(out int cLag))
                    lagPart = $" clientLag={cLag}t({cLag * 33.3:F0}ms)";
            }
            else
            {
                int curTick = DXGame.Current?.IAIterationInt ?? 0;
                int cLag = Math.Max(0, _networkGate.ReadyThrough - curTick);
                lagPart = $" tickLag={cLag}t({cLag * 33.3:F0}ms)";
            }

            ModLogger.Info(
                $"[LatencyStats] role={role} path=\"{path}\" rtt={CurrentSmoothedRttMs:F1}ms directRtt={directRtt:F1}ms " +
                $"delay={InputDelayTicks}t({InputDelayTicks * 33.3:F0}ms){lagPart} cmdN={count} cmdAvg={avgMs:F1}ms cmdP95={p95Ms:F1}ms cmdMax={maxMs:F1}ms" +
                (string.IsNullOrEmpty(statsLine) ? string.Empty : $" | {statsLine}"));
        }

        private static void RunEpochHashAndChecksum(int curTick)
        {
            bool hasDigest = false;
            StateHasher.LevelStateSnapshotDigest digest = default;
            List<StateHasher.EntityPosSample> dynamicPositions = null;

            if (_lastCheckedSimTick < 0)
            {
                dynamicPositions = new List<StateHasher.EntityPosSample>(2048);
                digest = StateHasher.ComputeDigest(dynamicPositions);
                _epochHasher.Reset();
                hasDigest = true;
            }
            else if (_epochHasher.Step(curTick, out digest, out dynamicPositions))
            {
                hasDigest = true;
            }

            if (!hasDigest) return;

            _lastCheckedSimTick = digest.SimTick;

            // Prune stale digests (keep ~40s of history) so dictionaries cannot grow unbounded.
            if (Transport.IsHost)
            {
                if (_hostTickDigests.Count > 0)
                {
                    var stale = new List<int>();
                    foreach (var k in _hostTickDigests.Keys)
                        if (k < curTick - 1200) stale.Add(k);
                    foreach (var k in stale) _hostTickDigests.Remove(k);
                }
                if (_clientTickDigests.Count > 0)
                {
                    var stale = new List<int>();
                    foreach (var k in _clientTickDigests.Keys)
                        if (k < curTick - 1200) stale.Add(k);
                    foreach (var k in stale) _clientTickDigests.Remove(k);
                }
            }

            CacheTickPositions(digest.SimTick, dynamicPositions);

            string digestSummary =
                $"Pos={digest.PosChecksum:X16} Coarse={digest.CoarsePosChecksum:X16} Life={digest.LifeChecksum:X16} " +
                $"Aggro={digest.ZombieAggroChecksum:X16}({digest.AggroZombieCount}) Proj={digest.ProjectileChecksum:X16} " +
                $"Prod={digest.ProductionChecksum:X16} Resv={digest.ReservationChecksum:X16} " +
                $"(entities={digest.EntityCount} proj={digest.ProjectileCount} queued={digest.QueuedOrderCount} " +
                $"res={digest.Gold}/{digest.Wood}/{digest.Stone}/{digest.Iron}/{digest.Oil} time={digest.GameTimeMs} " +
                $"phys={digest.PhysicsUpdates})";

            // Diagnostic (TABCOOP_PROJ_DUMP=1): both peers log the exact projectile ID set hashed at
            // this epoch's SimTick, so a host/client diff names the diverging arrows directly.
            if (Testing.TestHarnessConfig.ProjDump && digest.ProjectileIds != null)
            {
                var ids = new List<ulong>(digest.ProjectileIds);
                ids.Sort();
                var sb = new System.Text.StringBuilder(ids.Count * 21 + 64);
                sb.Append("[ProjDump] Tick=").Append(digest.SimTick).Append(" role=")
                  .Append(Transport.IsHost ? "host" : "client")
                  .Append(" count=").Append(ids.Count).Append(" ids=");
                for (int i = 0; i < ids.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ids[i]);
                }
                ModLogger.Info(sb.ToString());
            }

            if (Transport.IsHost)
            {
                _hostTickDigests[digest.SimTick] = new TickDigestRecord { Digest = digest, Summary = digestSummary };
                VerifyTickChecksum(digest.SimTick);
            }
            else
            {
                List<OwnedUnitPosSample> ownedUnits = CollectClientOwnedActiveUnits(digest.SimTick, dynamicPositions);
                Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.StateChecksumReport, w =>
                {
                    WriteDigestReport(w, in digest, digestSummary, ownedUnits);
                });
            }
        }

        private static PointF GetLocalMouseWorldPos()
        {
            var levelSys = GameBridge.LevelSystemInstance;
            if (levelSys == null) return PointF.Empty;
            var field = AccessTools.Field(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_MouseWorldPos);
            return field != null ? (PointF)field.GetValue(levelSys) : PointF.Empty;
        }

        /// <summary>
        /// Triggers a tactical ping locally and broadcasts it to all session peers.
        /// Pings are purely visual UI signals (not deterministic world mutations) so they travel over
        /// PacketType.TacticalPing immediately without consuming a lockstep command slot.
        /// </summary>
        public static void SendTacticalPing(Point cell, string label = null)
        {
            byte pid = Transport != null ? Transport.LocalPlayerId : CommandInterceptor.LocalPlayerId;
            string effectiveLabel = string.IsNullOrEmpty(label) ? $"Player {pid}" : label;

            AllyOverlayRenderer.TriggerMinimapAndGroundPing(pid, cell, effectiveLabel);

            if (!IsSessionActive || !IsInSyncedLevel || Transport == null) return;

            Transport.Broadcast(SendChannel.ReliableCommand, PacketType.TacticalPing, w =>
            {
                w.Write(pid);
                w.Write(cell.X);
                w.Write(cell.Y);
                w.Write(effectiveLabel);
            });
        }

        private static void OnPacketReceived(byte senderId, PacketType pktType, BinaryReader r)
        {
            switch (pktType)
            {
                case PacketType.LobbyRosterSync:
                {
                    if (Transport.IsHost) break;
                    var phase = (LobbyRoomPhase)r.ReadByte();
                    string hint = r.ReadString();
                    byte count = r.ReadByte();
                    _lobbyRoster.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        _lobbyRoster.Add(new PeerInfo
                        {
                            PlayerId = r.ReadByte(),
                            PlayerName = r.ReadString(),
                            SteamId64 = r.ReadUInt64(),
                            IsReady = r.ReadBoolean()
                        });
                    }
                    RoomPhase = phase;
                    HostActionHint = hint;
                    if (r.BaseStream.Position < r.BaseStream.Length)
                    {
                        BalancePreset = (CoopBalancePreset)r.ReadByte();
                    }
                    if (r.BaseStream.Position + 13 <= r.BaseStream.Length && !TestHarnessConfig.HasChaos && IsLocalLoopbackSession)
                    {
                        bool chaosOn = r.ReadBoolean();
                        int lat = r.ReadInt32();
                        int jit = r.ReadInt32();
                        float loss = r.ReadSingle();
                        LocalChaosEnabled = chaosOn;
                        LocalChaosLatencyMs = Math.Max(0, Math.Min(500, lat));
                        LocalChaosJitterMs = Math.Max(0, Math.Min(300, jit));
                        LocalChaosLossRate = Math.Round(Math.Max(0.0, Math.Min(0.30, (double)loss)), 3);
                        if (Transport is ChaosTransport chaos)
                        {
                            chaos.UpdateConfig(BuildLocalChaosConfig());
                        }
                    }
                    Transport.SyncPeers(_lobbyRoster);
                    LobbyStateChanged?.Invoke();
                    break;
                }

                case PacketType.ClientReadyState:
                {
                    if (!Transport.IsHost) break;
                    bool ready = r.ReadBoolean();
                    foreach (var p in _lobbyRoster)
                    {
                        if (p.PlayerId == senderId)
                        {
                            p.IsReady = ready;
                            break;
                        }
                    }
                    BroadcastLobbyRoster();
                    LobbyStateChanged?.Invoke();
                    break;
                }

                case PacketType.LobbyHostNotice:
                {
                    if (Transport.IsHost) break;
                    var noticeType = (LobbyHostNoticeType)r.ReadByte();
                    if (noticeType == LobbyHostNoticeType.ReturnToLobby)
                    {
                        foreach (var p in _lobbyRoster)
                        {
                            if (p.PlayerId != 1) p.IsReady = false;
                        }
                        RoomPhase = LobbyRoomPhase.InLobby;
                        HostActionHint = string.Empty;
                        ShouldOpenLobbyOnMainMenu = true;
                        Transport.SyncPeers(_lobbyRoster);
                        if (GameBridge.CurrentLevelState != null)
                        {
                            _pendingClientReturnToMainMenu = true;
                        }
                        LobbyStateChanged?.Invoke();
                    }
                    break;
                }

                case PacketType.SnapshotBegin:
                {
                    _expectedSaveBytes = r.ReadInt32();
                    _expectedChunks = r.ReadInt32();
                    _incomingCheckContent = r.ReadString();
                    _incomingRandomSeed = r.ReadInt32();
                    _incomingEntityIdCounter = r.ReadInt64();
                    // Append-only field: present only when the Host runs a newer build.
                    _incomingClockOriginMs = r.BaseStream.Position + 8 <= r.BaseStream.Length ? r.ReadDouble() : double.NaN;
                    if (!double.IsNaN(_incomingClockOriginMs))
                    {
                        LockstepClock.ApplySharedClockOrigin(_incomingClockOriginMs);
                        ModLogger.Info($"[CoopSessionManager] Received Snapshot from Host ({_expectedSaveBytes:N0} bytes, {_expectedChunks} chunks, seed={_incomingRandomSeed}, idCounter={_incomingEntityIdCounter}, clockOrigin={_incomingClockOriginMs:F1}ms); buffered={_incomingChunks.Count}.");
                    }
                    else
                    {
                        ModLogger.Info($"[CoopSessionManager] Receiving Snapshot from Host ({_expectedSaveBytes:N0} bytes, {_expectedChunks} chunks, seed={_incomingRandomSeed}, idCounter={_incomingEntityIdCounter}); buffered={_incomingChunks.Count}.");
                    }
                    GameBridge.ShowTopBanner("Receiving Map Snapshot from Host...", Color.Cyan, 3000);
                    TryAssembleSnapshot();
                    break;
                }

                case PacketType.SnapshotChunk:
                {
                    int chunkIdx = r.ReadInt32();
                    int len = r.ReadInt32();
                    byte[] data = r.ReadBytes(len);
                    // Chunks may arrive before SnapshotBegin and out of order (ChaosTransport jitter
                    // reorders even ReliableOrdered). Buffer by index; assemble once complete.
                    if (chunkIdx >= 0) _incomingChunks[chunkIdx] = data;
                    ModLogger.Info($"[CoopSessionManager] Snapshot chunk {chunkIdx} received ({len:N0} bytes, buffered={_incomingChunks.Count}/{_expectedChunks}).");
                    TryAssembleSnapshot();
                    break;
                }

                case PacketType.SnapshotLoadedAck:
                {
                    if (!Transport.IsHost) break;
                    int clientTick = r.ReadInt32();
                    _clientSnapshotAckReceived = true;
                    ModLogger.Info($"[CoopSessionManager] Received SnapshotLoadedAck from Client({senderId}) at tick={clientTick} (hostWaitingOwnReload={_hostWaitingOwnReload}).");
                    TryCompleteHostSyncBarrier();
                    break;
                }

                case PacketType.SessionStart:
                {
                    if (Transport.IsHost) break;
                    int startTick = r.ReadInt32();
                    int seed = r.ReadInt32();
                    long idCounter = r.ReadInt64();
                    byte hostDelay = r.BaseStream.Position < r.BaseStream.Length ? r.ReadByte() : (byte)MinInputDelayTicks;
                    ResetSyncTrackingState();
                    InputDelayTicks = Math.Max(MinInputDelayTicks, Math.Min(MaxInputDelayTicks, (int)hostDelay));
                    EnterSyncedLevel();
                    ModLogger.Info($"[CoopSessionManager] Received SessionStart from Host (tick={startTick}, localTick={DXGame.Current?.IAIterationInt ?? 0}, seed={seed}, idCounter={idCounter}, inputDelay={InputDelayTicks}, mode={EffectiveResyncMode}). Unpausing in lockstep!");
                    break;
                }

                case PacketType.ClientInputReport:
                {
                    if (!Transport.IsHost) break;
                    ushort count = r.ReadUInt16();
                    lock (_hostTurnBuffer)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var cmd = NetCommand.ReadFrom(r);
                            cmd.PlayerId = senderId;
                            _hostTurnBuffer.Add(cmd);
                        }
                    }
                    break;
                }

                case PacketType.TurnInputBroadcast:
                {
                    if (Transport.IsHost) break;
                    int targetTick = r.ReadInt32();
                    ushort count = r.ReadUInt16();
                    List<NetCommand> cmds = null;
                    for (int i = 0; i < count; i++)
                    {
                        var cmd = NetCommand.ReadFrom(r);
                        (cmds ?? (cmds = new List<NetCommand>())).Add(cmd);
                    }

                    if (cmds != null)
                    {
                        lock (_pendingTurnCommands)
                        {
                            if (!_pendingTurnCommands.TryGetValue(targetTick, out var slot))
                                _pendingTurnCommands[targetTick] = slot = new List<NetCommand>();
                            slot.AddRange(cmds);
                        }
                    }

                    // The heartbeat arrived: allow the simulation to advance up to this tick. The
                    // commands themselves are executed by FlushDueTurnCommands at the tick boundary.
                    _networkGate.MarkReady(targetTick);
                    if (targetTick > _clientHostPausedTick)
                        _clientHostPauseKeepaliveUntilTicks = -1.0;

                    if (cmds != null && cmds.Exists(c => c.Type == NetCommandType.MayorVoteOrSelect))
                    {
                        Patches.MayorSyncPatch.NotifyRemoteMayorCommitted(GameBridge.GetLevelCollectionCount("MayorStates"));
                        Patches.MayorSyncPatch.CloseActiveMayorWindow();
                    }
                    break;
                }

                case PacketType.HostPauseKeepalive:
                {
                    if (Transport.IsHost || _levelEndedSynced || GameBridge.IsLevelEnded()) break;
                    int hostTick = r.ReadInt32();
                    bool hostInModal = r.ReadBoolean();
                    bool hostSessionPaused = r.ReadBoolean();
                    _clientHostPausedTick = hostTick;
                    _clientHostInModalPause = hostInModal || hostSessionPaused;
                    _clientHostPauseKeepaliveUntilTicks = DXHelper_Environment.Ticks + 1500.0;
                    int curClientTick = DXGame.Current?.IAIterationInt ?? 0;
                    if (hostSessionPaused && !LockstepClock.PausedBySession && curClientTick < hostTick)
                    {
                        GameBridge.ShowTopBanner($"Pausing (syncing {hostTick - curClientTick}t)...", Color.LightSkyBlue, 600);
                    }
                    break;
                }

                case PacketType.LevelEnded:
                {
                    if (Transport.IsHost) break;
                    bool won = r.ReadBoolean();
                    int endTick = r.ReadInt32();
                    OnClientLevelEndedReceived(won, endTick);
                    break;
                }

                case PacketType.StateChecksumReport:
                {
                    if (!Transport.IsHost) break;
                    TickDigestRecord rec = ReadDigestReport(r);
                    int clientTick = rec.Digest.SimTick;
                    UpdatePeerSimTick(senderId, clientTick);
                    if (!_clientTickDigests.TryGetValue(clientTick, out var byPeer))
                        _clientTickDigests[clientTick] = byPeer = new Dictionary<byte, TickDigestRecord>();
                    byPeer[senderId] = rec;
                    if (EnableAsyncPosAlign && rec.OwnedUnits != null && rec.OwnedUnits.Count > 0)
                    {
                        _incomingReverseAlignQueue.Enqueue(new PendingReverseAlignApply
                        {
                            Generation = _asyncGeneration,
                            SenderPlayerId = senderId,
                            Tick = clientTick,
                            Units = rec.OwnedUnits
                        });
                    }
                    VerifyTickChecksum(clientTick);
                    break;
                }

                case PacketType.PosDriftSyncChunk:
                {
                    if (Transport.IsHost) break;
                    OnPosDriftSyncChunkReceived(r);
                    break;
                }

                case PacketType.PosDriftStatsReport:
                {
                    if (!Transport.IsHost) break;
                    int pTick = r.ReadInt32();
                    bool wasApplied = r.ReadBoolean();
                    int appliedCount = r.ReadInt32();
                    int comparedCount = r.ReadInt32();
                    int drifted001 = r.ReadInt32();
                    int drifted01 = r.ReadInt32();
                    int drifted1 = r.ReadInt32();
                    int drifted5 = r.ReadInt32();
                    float maxDelta = r.ReadSingle();
                    float rmsDelta = r.ReadSingle();
                    bool exceededThreshold = r.ReadBoolean();
                    string maxType = r.ReadString();
                    int lifeDiffCount = r.ReadInt32();
                    int lifeApplied = r.ReadInt32();
                    int killsApplied = r.ReadInt32();
                    int missingOnHost = r.ReadInt32();
                    int missingOnClient = r.ReadInt32();

                    // After host-authoritative snapping (<= MaxAsyncNudgeCells) a residual this wide means
                    // many units are on entirely different paths; only that, sustained, warrants a reload.
                    bool widePosDrift = rmsDelta >= 2.5f && drifted5 >= 25;
                    bool significantPosExceed = exceededThreshold && (drifted5 >= 5 || drifted1 >= 15);
                    bool countsAsPosExceed = IsStrictMode ? (significantPosExceed || widePosDrift) : widePosDrift;
                    if (countsAsPosExceed)
                        _consecutivePosExceeded++;
                    else
                        _consecutivePosExceeded = 0;

                    if (missingOnClient >= 10)
                        _consecutiveMissingOnClient++;
                    else
                        _consecutiveMissingOnClient = 0;

                    ModLogger.Info(
                        $"[PosDriftStats] Client({senderId}) Tick={pTick} Applied={wasApplied}({appliedCount}/{comparedCount}) " +
                        $"Drifted(>0.001={drifted001}, >0.01={drifted01}, >0.1={drifted1}, >0.5={drifted5}) " +
                        $"MaxDelta={maxDelta:F4} RmsDelta={rmsDelta:F4} Type={maxType} Exceeded={exceededThreshold} Streak={_consecutivePosExceeded} " +
                        $"LifeDiff={lifeDiffCount} LifeFix={lifeApplied} Kills={killsApplied} MissingOnHost={missingOnHost} MissingOnClient={missingOnClient} MissingStreak={_consecutiveMissingOnClient}");

                    bool triggerMissingResync = !IsStrictMode && _consecutiveMissingOnClient >= MissingOnClientReloadStreak;
                    bool triggerPosResync = IsStrictMode
                        ? (widePosDrift || _consecutivePosExceeded >= 3)
                        : (_consecutivePosExceeded >= WidePosReloadStreak);
                    if ((triggerPosResync || triggerMissingResync) && EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport)
                    {
                        double now = DXHelper_Environment.Ticks;
                        double requiredCooldown = triggerMissingResync ? StructuralResyncCooldownMs : AutoResyncCooldownMs;
                        if (now - _lastAutoResyncTicks >= requiredCooldown)
                        {
                            _lastAutoResyncTicks = now;
                            _consecutivePosExceeded = 0;
                            _consecutiveMissingOnClient = 0;
                            if (triggerMissingResync)
                                ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after MissingOnClient at Tick {pTick} (MissingOnClient={missingOnClient}, MissingOnHost={missingOnHost}).");
                            else
                                ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after PosDrift at Tick {pTick} (MaxDelta={maxDelta:F4}, RmsDelta={rmsDelta:F4}, Drifted5={drifted5}).");
                            RequestSnapshotBroadcast();
                        }
                    }
                    break;
                }

                case PacketType.AllyCursorAndCamera:
                {
                    byte pid = r.ReadByte();
                    if (pid == Transport.LocalPlayerId) break;

                    var state = new AllyVisualState
                    {
                        PlayerId = pid,
                        MouseWorldPosition = new PointF(r.ReadSingle(), r.ReadSingle()),
                        CameraWorldArea = new RectangleF(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                        LastUpdatedUtc = DateTime.UtcNow
                    };
                    AllyStates[pid] = state;

                    // If Host, relay to other clients in 3-player session
                    if (Transport.IsHost && Transport.ConnectedPeers.Count > 2)
                    {
                        Transport.Broadcast(SendChannel.UnreliableSequenced, PacketType.AllyCursorAndCamera, w =>
                        {
                            w.Write(state.PlayerId);
                            w.Write(state.MouseWorldPosition.X);
                            w.Write(state.MouseWorldPosition.Y);
                            w.Write(state.CameraWorldArea.X);
                            w.Write(state.CameraWorldArea.Y);
                            w.Write(state.CameraWorldArea.Width);
                            w.Write(state.CameraWorldArea.Height);
                        });
                    }
                    break;
                }

                case PacketType.TacticalPing:
                {
                    byte pid = r.ReadByte();
                    int cellX = r.ReadInt32();
                    int cellY = r.ReadInt32();
                    string label = r.ReadString();
                    if (Transport != null && pid == Transport.LocalPlayerId) break;

                    AllyOverlayRenderer.TriggerMinimapAndGroundPing(pid, new Point(cellX, cellY), label);

                    // If Host, relay to other clients in 3-player session
                    if (Transport != null && Transport.IsHost && Transport.ConnectedPeers.Count > 2)
                    {
                        Transport.Broadcast(SendChannel.ReliableCommand, PacketType.TacticalPing, w =>
                        {
                            w.Write(pid);
                            w.Write(cellX);
                            w.Write(cellY);
                            w.Write(label);
                        });
                    }
                    break;
                }

                case PacketType.RttPing:
                {
                    if (Transport == null || Transport.IsHost) break;
                    uint seq = r.ReadUInt32();
                    long hostTs = r.ReadInt64();
                    if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                    {
                        float hostRtt = r.ReadSingle();
                        if (hostRtt >= 0f && hostRtt < 5000f)
                            CurrentSmoothedRttMs = hostRtt;
                    }
                    Transport.SendToPeer(1, SendChannel.UnreliableSequenced, PacketType.RttPong, w =>
                    {
                        w.Write(seq);
                        w.Write(hostTs);
                        w.Write(DXGame.Current?.IAIterationInt ?? 0);
                    });
                    break;
                }

                case PacketType.RttPong:
                {
                    if (Transport == null || !Transport.IsHost) break;
                    uint seq = r.ReadUInt32();
                    long hostTs = r.ReadInt64();
                    if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                    {
                        int clientTick = r.ReadInt32();
                        UpdatePeerSimTick(senderId, clientTick);
                    }
                    double rttMs = (Stopwatch.GetTimestamp() - hostTs) * 1000.0 / Stopwatch.Frequency;
                    if (rttMs < 0.0 || rttMs > 2000.0 || double.IsNaN(rttMs) || double.IsInfinity(rttMs))
                        break;

                    double sample = Math.Min(rttMs, 1500.0);
                    if (_peerSmoothedRttMs.TryGetValue(senderId, out double prev))
                        _peerSmoothedRttMs[senderId] = 0.70 * prev + 0.30 * sample;
                    else
                        _peerSmoothedRttMs[senderId] = sample;

                    _peerRttSampleTicks[senderId] = DXHelper_Environment.Ticks;
                    UpdateAdaptiveInputDelay();
                    break;
                }
            }
        }

        private static void CacheTickPositions(int tick, List<StateHasher.EntityPosSample> samples)
        {
            if (samples == null) return;
            _tickPosCache[tick] = samples;
            _tickPosCacheOrder.Enqueue(tick);
            while (_tickPosCacheOrder.Count > 16)
            {
                int old = _tickPosCacheOrder.Dequeue();
                _tickPosCache.Remove(old);
            }
        }

        private static void WriteDigestReport(BinaryWriter w, in StateHasher.LevelStateSnapshotDigest d, string summary, List<OwnedUnitPosSample> ownedUnits = null)
        {
            w.Write(d.SimTick);
            w.Write(d.GameTimeMs);
            w.Write(d.Gold);
            w.Write(d.Wood);
            w.Write(d.Stone);
            w.Write(d.Iron);
            w.Write(d.Oil);
            w.Write(d.EntityCount);
            w.Write(d.ProjectileCount);
            w.Write(d.QueuedOrderCount);
            w.Write(d.AggroZombieCount);
            w.Write(d.PosChecksum);
            w.Write(d.CoarsePosChecksum);
            w.Write(d.LifeChecksum);
            w.Write(d.ZombieAggroChecksum);
            w.Write(d.ProjectileChecksum);
            w.Write(d.ProductionChecksum);
            w.Write(d.ReservationChecksum);
            w.Write(d.HardHash);
            w.Write(d.FullHash);
            w.Write(summary ?? string.Empty);

            int bucketLen = d.PosBuckets?.Length ?? 0;
            w.Write((ushort)bucketLen);
            for (int i = 0; i < bucketLen; i++)
                w.Write(d.PosBuckets[i]);

            int lifeLen = d.LifeBuckets?.Length ?? 0;
            w.Write((ushort)lifeLen);
            for (int i = 0; i < lifeLen; i++)
                w.Write(d.LifeBuckets[i]);

            int entLen = d.EntityBuckets?.Length ?? 0;
            w.Write((ushort)entLen);
            for (int i = 0; i < entLen; i++)
                w.Write(d.EntityBuckets[i]);

            int ownedCount = ownedUnits != null ? Math.Min(ownedUnits.Count, MaxReverseAlignUnitsPerReport) : 0;
            w.Write((ushort)ownedCount);
            for (int i = 0; i < ownedCount; i++)
            {
                w.Write(ownedUnits[i].Id);
                w.Write(ownedUnits[i].X);
                w.Write(ownedUnits[i].Y);
            }

            // Appended after every pre-existing field so a peer on an older build still parses the
            // report correctly (the reader guards on remaining length). Diagnostic only; never hashed.
            w.Write(d.PhysicsUpdates);
        }

        private static TickDigestRecord ReadDigestReport(BinaryReader r)
        {
            var d = new StateHasher.LevelStateSnapshotDigest
            {
                SimTick = r.ReadInt32(),
                GameTimeMs = r.ReadInt32(),
                Gold = r.ReadInt32(),
                Wood = r.ReadInt32(),
                Stone = r.ReadInt32(),
                Iron = r.ReadInt32(),
                Oil = r.ReadInt32(),
                EntityCount = r.ReadInt32(),
                ProjectileCount = r.ReadInt32(),
                QueuedOrderCount = r.ReadInt32(),
                AggroZombieCount = r.ReadInt32(),
                PosChecksum = r.ReadInt64(),
                CoarsePosChecksum = r.ReadInt64(),
                LifeChecksum = r.ReadInt64(),
                ZombieAggroChecksum = r.ReadInt64(),
                ProjectileChecksum = r.ReadInt64(),
                ProductionChecksum = r.ReadInt64(),
                ReservationChecksum = r.ReadInt64(),
                HardHash = r.ReadUInt64(),
                FullHash = r.ReadUInt64(),
            };
            string summary = r.BaseStream.Position < r.BaseStream.Length ? r.ReadString() : string.Empty;
            d.PosBuckets = ReadBucketArray(r);
            d.LifeBuckets = ReadBucketArray(r);
            d.EntityBuckets = ReadBucketArray(r);

            List<OwnedUnitPosSample> ownedUnits = null;
            if (r.BaseStream.Position + 2 <= r.BaseStream.Length)
            {
                ushort ownedCount = r.ReadUInt16();
                if (ownedCount > 0 && r.BaseStream.Position + ownedCount * 16L <= r.BaseStream.Length)
                {
                    ownedUnits = new List<OwnedUnitPosSample>(ownedCount);
                    for (int i = 0; i < ownedCount; i++)
                    {
                        ownedUnits.Add(new OwnedUnitPosSample
                        {
                            Id = r.ReadUInt64(),
                            X = r.ReadSingle(),
                            Y = r.ReadSingle()
                        });
                    }
                }
            }
            // Optional trailer (present only from builds that append it).
            if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                d.PhysicsUpdates = r.ReadUInt32();
            return new TickDigestRecord { Digest = d, Summary = summary, OwnedUnits = ownedUnits };
        }

        private static ulong[] ReadBucketArray(BinaryReader r)
        {
            if (r.BaseStream.Position + 2 > r.BaseStream.Length) return null;
            ushort bucketLen = r.ReadUInt16();
            if (bucketLen > 0 && r.BaseStream.Position + bucketLen * 8L <= r.BaseStream.Length)
            {
                var arr = new ulong[bucketLen];
                for (int i = 0; i < bucketLen; i++)
                    arr[i] = r.ReadUInt64();
                return arr;
            }
            return null;
        }

        private static ushort ComputeDiffBucketMask(
            in StateHasher.LevelStateSnapshotDigest host,
            in StateHasher.LevelStateSnapshotDigest client)
        {
            return DiffMask(host.PosBuckets, client.PosBuckets);
        }

        private static ushort ComputeLifeOrEntityDiffBucketMask(
            in StateHasher.LevelStateSnapshotDigest host,
            in StateHasher.LevelStateSnapshotDigest client)
        {
            return (ushort)(DiffMask(host.LifeBuckets, client.LifeBuckets)
                | DiffMask(host.EntityBuckets, client.EntityBuckets));
        }

        private static ushort DiffMask(ulong[] a, ulong[] b)
        {
            if (a == null || b == null ||
                a.Length != StateHasher.BucketCount ||
                b.Length != StateHasher.BucketCount)
            {
                return 0xFFFF;
            }

            ushort mask = 0;
            for (int i = 0; i < StateHasher.BucketCount; i++)
            {
                if (a[i] != b[i])
                    mask |= (ushort)(1 << i);
            }
            return mask;
        }

        private static void VerifyTickChecksum(int tick)
        {
            if (!_hostTickDigests.TryGetValue(tick, out TickDigestRecord hostRec)) return;
            if (!_clientTickDigests.TryGetValue(tick, out var byPeer) || byPeer.Count == 0) return;

            var host = hostRec.Digest;
            string hostSummary = hostRec.Summary;
            var resolvedPeers = new List<byte>();

            foreach (var kv in byPeer)
            {
                byte senderId = kv.Key;
                var client = kv.Value.Digest;
                string clientSummary = kv.Value.Summary;
                resolvedPeers.Add(senderId);
                _verifiedChecksumCycles++;

                if (host.FullHash == client.FullHash)
                {
                    _consecutiveProjOnlyMismatch = 0;
                    _consecutiveHardMismatch = 0;
                    _consecutivePosExceeded = 0;
                    ResetTieredResyncState();
                    // phys= is diagnostic-only and NOT part of FullHash, so a mismatch here on an
                    // otherwise perfect MATCH epoch proves the two peers ran a different number of
                    // Chipmunk steps while every hashed channel still agreed -- the classic signature
                    // of a sub-pixel position divergence that has not yet reached the coarse grid.
                    string physNote = host.PhysicsUpdates == client.PhysicsUpdates
                        ? string.Empty
                        : $" PHYS-UPDATES-MISMATCH host={host.PhysicsUpdates} client={client.PhysicsUpdates} delta={host.PhysicsUpdates - client.PhysicsUpdates}";
                    ModLogger.Info($"[StateChecksum] Tick {tick} MATCH (0x{host.FullHash:X16}) with Client({senderId}) phys={host.PhysicsUpdates}/{client.PhysicsUpdates}.{physNote}");
                    continue;
                }

                bool lifeDiff = host.LifeChecksum != client.LifeChecksum;
                bool aggroDiff = host.ZombieAggroChecksum != client.ZombieAggroChecksum || host.AggroZombieCount != client.AggroZombieCount;
                bool aggroCountDiff = host.AggroZombieCount != client.AggroZombieCount;
                bool entityDiff = host.EntityCount != client.EntityCount;
                bool econCoreDiff = host.Gold != client.Gold || host.Wood != client.Wood || host.Stone != client.Stone
                    || host.Iron != client.Iron || host.Oil != client.Oil
                    || host.ReservationChecksum != client.ReservationChecksum
                    || host.ProductionChecksum != client.ProductionChecksum
                    || host.GameTimeMs != client.GameTimeMs;
                bool econDiff = econCoreDiff || host.QueuedOrderCount != client.QueuedOrderCount;
                bool projDiff = host.ProjectileCount != client.ProjectileCount || host.ProjectileChecksum != client.ProjectileChecksum;
                bool posDiff = host.PosChecksum != client.PosChecksum;
                bool coarsePosDiff = host.CoarsePosChecksum != client.CoarsePosChecksum;

                int entityTol = Math.Min(30, Math.Max(12, host.EntityCount / 1000));
                int aggroTol = Math.Max(15, host.AggroZombieCount / 10);
                int absEntityDelta = Math.Abs(host.EntityCount - client.EntityCount);
                int absAggroDelta = Math.Abs(host.AggroZombieCount - client.AggroZombieCount);

                // In async_align mode, sub-tile solver nudges or minor combat timing can briefly
                // shift alerted zombie states within aggroTol without altering HP, entity counts,
                // coarse grid cells, or economy. Treat that as soft drift for async pos alignment
                // instead of stalling both players with a 6.5s full-game snapshot reload.
                bool softAggroDrift = !IsStrictMode
                    && aggroDiff && absAggroDelta <= aggroTol
                    && !lifeDiff && !entityDiff && !econDiff && !coarsePosDiff;

                bool hardCoreDiff = lifeDiff || entityDiff || econDiff || (aggroDiff && !softAggroDrift);
                bool majorStateDiff = econCoreDiff
                    || absEntityDelta > entityTol
                    || absAggroDelta > aggroTol;
                double now = DXHelper_Environment.Ticks;

                // Tiered policy (async_align):
                //  - Pos / HP / aggro / projectile / small entity-count deltas are corrected continuously by
                //    host-authoritative AsyncPosAlign (positions + HP + host-side kills) with no pause.
                //  - Economy (resources, production, reservations, game time) is irreversible -> reload
                //    after a short persistent streak.
                //  - Entity-count delta beyond tolerance (spawns/deaths that cannot be corrected) -> reload
                //    only when sustained for ~30s.
                //  - Any residual divergence is realigned by a long-period reload (LongPeriodRealignMs).
                bool structuralDiff = absEntityDelta > entityTol;
                bool longPeriodDue = false;
                if (!IsStrictMode)
                {
                    _consecutiveEconMismatch = econCoreDiff ? _consecutiveEconMismatch + 1 : 0;
                    _consecutiveStructuralMismatch = structuralDiff ? _consecutiveStructuralMismatch + 1 : 0;
                    // Ported from upstream v0.1.9: a small entity-count delta (a couple of transient
                    // spawns/deaths) must not start the long-period realign timer — that used to fire a
                    // full-game reload every 5 minutes even when nothing structural had changed.
                    bool substantiveDiff = econCoreDiff || lifeDiff
                        || _consecutiveEconMismatch >= 3
                        || absEntityDelta > entityTol;
                    if (substantiveDiff)
                    {
                        if (_divergedSinceTicks < 0) _divergedSinceTicks = now;
                        longPeriodDue = now - _divergedSinceTicks >= LongPeriodRealignMs;
                    }
                    else
                    {
                        _divergedSinceTicks = -1.0;
                    }
                }
                bool earlyEconBypass = !IsStrictMode && !_econEarlyBypassUsed
                    && _verifiedChecksumCycles <= 3 && _consecutiveEconMismatch >= EconReloadStreak;
                bool structuralCooldownElapsed = !IsStrictMode
                    && _consecutiveStructuralMismatch >= StructuralReloadStreak
                    && (now - _lastAutoResyncTicks >= StructuralResyncCooldownMs);
                bool cooldownElapsed = (now - _lastAutoResyncTicks >= AutoResyncCooldownMs) || earlyEconBypass || structuralCooldownElapsed;
                bool canReloadNow = EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport
                    && cooldownElapsed;

                bool triggerHardDesync = IsStrictMode
                    ? (hardCoreDiff || projDiff)
                    : canReloadNow && (_consecutiveEconMismatch >= EconReloadStreak
                        || _consecutiveStructuralMismatch >= StructuralReloadStreak
                        || longPeriodDue);

                if (triggerHardDesync)
                {
                    if (projDiff && !hardCoreDiff) _consecutiveProjOnlyMismatch++;
                    else _consecutiveProjOnlyMismatch = 0;
                    _consecutiveHardMismatch = 0;

                    string reason = IsStrictMode ? "strict"
                        : _consecutiveEconMismatch >= EconReloadStreak ? $"econ x{_consecutiveEconMismatch}"
                        : _consecutiveStructuralMismatch >= StructuralReloadStreak ? $"entityDelta={absEntityDelta} x{_consecutiveStructuralMismatch}"
                        : $"long-period realign after {(now - _divergedSinceTicks) / 1000.0:F0}s";
                    ModLogger.Error($"[StateChecksum] HARD DESYNC DETECTED at Tick {tick} ({reason})! Life={lifeDiff} Aggro={aggroDiff} Entity={entityDiff} Econ={econDiff} Proj={projDiff} Pos={posDiff} (Host=0x{host.FullHash:X16}, Client({senderId})=0x{client.FullHash:X16})");
                    ModLogger.Error($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                    ModLogger.Error($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                    if (!_desyncDumped)
                    {
                        _desyncDumped = true;
                        DumpHostEntitiesOnDesync(tick);
                    }
                    TrySendPosDriftSync(senderId, tick, applyLifeCorrection: false, applyPosCorrection: false, bucketMask: ComputeDiffBucketMask(in host, in client), includeStatic: false);
                    GameBridge.ShowTopBanner("State Drift Detected (see log)", Color.Orange, 2500);

                    if (EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport && cooldownElapsed)
                    {
                        if (earlyEconBypass)
                        {
                            _econEarlyBypassUsed = true;
                            ModLogger.Warn($"[CoopSessionManager] Early-epoch Econ divergence (cycle={_verifiedChecksumCycles}): bypassing resync cooldown once.");
                        }
                        _lastAutoResyncTicks = now;
                        ResetTieredResyncState();
                        ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after HARD DESYNC at Tick {tick} (Life={lifeDiff}, Aggro={aggroDiff}, Entity={entityDiff}, Econ={econDiff}, Proj={projDiff}).");
                        RequestSnapshotBroadcast();
                    }
                    continue;
                }

                if (hardCoreDiff)
                {
                    // Combat-level drift (HP / aggro / small entity delta): correct host-authoritatively
                    // in the background; the tiered policy above decides if a reload is ever needed.
                    // Decouple fast HP/kill sync (2s) from slow position realignments (8s).
                    _consecutiveHardMismatch++;
                    _consecutiveProjOnlyMismatch = 0;
                    ModLogger.Warn($"[StateChecksum] TRANSIENT STATE DRIFT at Tick {tick} (Life={lifeDiff} Aggro={aggroDiff} Entity={entityDiff}(d={absEntityDelta}) Econ={econDiff} CoarseDiff={coarsePosDiff} streak={_consecutiveHardMismatch} econStreak={_consecutiveEconMismatch} entityStreak={_consecutiveStructuralMismatch}).");
                    ModLogger.Info($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                    ModLogger.Info($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                    if ((posDiff || lifeDiff || entityDiff) && (now - _lastPosSyncSentTicks >= AsyncPosAlignCooldownMs))
                    {
                        _lastPosSyncSentTicks = now;
                        bool applyLife = EnableAsyncPosAlign || !IsStrictMode;
                        bool applyPos = applyLife && (now - _lastFullPosAlignSentTicks >= AsyncSoftPosAlignCooldownMs);
                        if (applyPos)
                            _lastFullPosAlignSentTicks = now;
                        ushort lifeEntMask = (lifeDiff || entityDiff) ? ComputeLifeOrEntityDiffBucketMask(in host, in client) : (ushort)0;
                        ushort posMask = (posDiff && applyPos) ? ComputeDiffBucketMask(in host, in client) : (ushort)0;
                        ushort mask = (ushort)(lifeEntMask | posMask);
                        if (mask == 0 && (lifeDiff || entityDiff)) mask = 0xFFFF;
                        TrySendPosDriftSync(senderId, tick, applyLifeCorrection: applyLife, applyPosCorrection: applyPos, bucketMask: mask, includeStatic: (lifeDiff || entityDiff));
                    }
                    continue;
                }

                if (projDiff)
                {
                    // Projectiles are short-lived; their effect shows up as HP, which is corrected above.
                    _consecutiveProjOnlyMismatch++;
                    _consecutiveHardMismatch = 0;
                    ModLogger.Warn($"[StateChecksum] TRANSIENT PROJ DIFF at Tick {tick} (Life/Aggro/Entity/Econ MATCH; PosDiff={posDiff} CoarseDiff={coarsePosDiff}, streak={_consecutiveProjOnlyMismatch}).");
                    if (coarsePosDiff && (now - _lastFullPosAlignSentTicks >= AsyncSoftPosAlignCooldownMs))
                    {
                        _lastFullPosAlignSentTicks = now;
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(senderId, tick, applyLifeCorrection: EnableAsyncPosAlign, applyPosCorrection: EnableAsyncPosAlign, bucketMask: ComputeDiffBucketMask(in host, in client), includeStatic: false);
                    }
                    continue;
                }

                // Pure position drift (or same-count sub-tile aggro oscillation): NO real combat/economy divergence!
                _consecutiveProjOnlyMismatch = 0;
                _consecutiveHardMismatch = 0;
                if (softAggroDrift)
                    ModLogger.Warn($"[StateChecksum] SOFT AGGRO/POS DRIFT at Tick {tick} (AggroCount={host.AggroZombieCount}/{client.AggroZombieCount}; Life/Entity/Proj/Econ/Coarse MATCH, mode={EffectiveResyncMode}).");
                else
                    ModLogger.Warn($"[StateChecksum] SOFT POS DRIFT at Tick {tick} (Life/Aggro/Entity/Proj/Econ MATCH! CoarseDiff={coarsePosDiff}, mode={EffectiveResyncMode}).");
                ModLogger.Info($"[StateChecksum]   Host   : {hostSummary ?? "(none)"}");
                ModLogger.Info($"[StateChecksum]   Client : {clientSummary ?? "(none)"}");
                if (!_bucketDiffLogged)
                {
                    _bucketDiffLogged = true;
                    LogPosBucketDiff(tick, in host, in client);
                }
                if (IsStrictMode)
                {
                    if (now - _lastPosSyncSentTicks >= 2000.0)
                    {
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(senderId, tick, applyLifeCorrection: false, applyPosCorrection: false, bucketMask: ComputeDiffBucketMask(in host, in client), includeStatic: false);
                    }
                    if (EnableAutoResync && !_awaitingClientSnapshotAck && !_pendingHostSnapshotExport && (now - _lastAutoResyncTicks >= AutoResyncCooldownMs))
                    {
                        _lastAutoResyncTicks = now;
                        ModLogger.Warn($"[CoopSessionManager] Auto-resync triggered after DESYNC (strict pos) at Tick {tick}.");
                        RequestSnapshotBroadcast();
                    }
                }
                else
                {
                    bool shouldSendSoftSync;
                    if (!coarsePosDiff && !softAggroDrift)
                    {
                        _consecutiveSubCoarsePosDrift++;
                        shouldSendSoftSync = _consecutiveSubCoarsePosDrift >= 3;
                    }
                    else
                    {
                        _consecutiveSubCoarsePosDrift = 0;
                        shouldSendSoftSync = true;
                    }

                    if (shouldSendSoftSync && (now - _lastFullPosAlignSentTicks >= AsyncSoftPosAlignCooldownMs))
                    {
                        _lastFullPosAlignSentTicks = now;
                        _lastPosSyncSentTicks = now;
                        TrySendPosDriftSync(senderId, tick, applyLifeCorrection: EnableAsyncPosAlign, applyPosCorrection: EnableAsyncPosAlign, bucketMask: ComputeDiffBucketMask(in host, in client), includeStatic: false);
                    }
                }
            }

            foreach (var pid in resolvedPeers)
                byPeer.Remove(pid);
            if (byPeer.Count == 0)
                _clientTickDigests.Remove(tick);
        }

        private static void DrainAsyncQueues()
        {
            int curGen = _asyncGeneration;

            while (_outgoingPosSyncQueue.TryDequeue(out var batch))
            {
                if (batch.Generation != curGen || Transport == null) continue;
                try
                {
                    int totalChunks = batch.Chunks.Count;
                    for (int idx = 0; idx < totalChunks; idx++)
                    {
                        byte[] chunk = batch.Chunks[idx];
                        int len = chunk.Length;
                        Transport.SendToPeer(batch.TargetPlayerId, SendChannel.ReliableOrdered, PacketType.PosDriftSyncChunk, w =>
                        {
                            w.Write(batch.Seq);
                            w.Write(batch.Tick);
                            w.Write(batch.ApplyCorrection);
                            w.Write(totalChunks);
                            w.Write(idx);
                            w.Write(batch.TotalCompressedLen);
                            w.Write(len);
                            w.Write(chunk, 0, len);
                        });
                    }
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[AsyncPosAlign] Drain outgoing failed: {ex.Message}");
                }
            }

            while (_incomingPosAlignQueue.TryDequeue(out var item))
            {
                if (item.Generation != curGen || Transport == null) continue;

                var game = DXGame.Current;
                int nCells = DXWorldGrid.NCells;
                float maxCoord = nCells > 1 ? nCells - 0.5f : 10000f;

                int appliedCount = 0;
                int microApplied = 0;
                int protectedSelfOwned = 0;
                bool microAlign = item.ApplyPosCorrection && MicroAlignEnabled;
                float microFloor = microAlign ? MicroAlignFloorCells : 0f;
                int microBudget = MicroAlignMaxUnitsPerReport;
                if (item.ApplyPosCorrection && item.Deltas != null && game != null)
                {
                    PruneCorrectionCooldowns(_lastEntityPosCorrectedTick, item.Tick, EntityPosRealignCooldownTicks);
                    PruneCorrectionCooldowns(_lastEntityMicroCorrectedTick, item.Tick, MicroAlignCooldownTicks);

                    byte localPid = Transport.LocalPlayerId;
                    for (int i = 0; i < item.Deltas.Count; i++)
                    {
                        var d = item.Deltas[i];
                        DXEntity e = game.GetEntityByID(d.Id);
                        if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic) continue;

                        float dist = (float)Math.Sqrt(d.Dx * d.Dx + d.Dy * d.Dy);

                        // Sub-precision band: drift below the per-type snap threshold (0.50-1.80 cells)
                        // is never corrected by the normal path, so it accumulates until CoarseDiff and
                        // then a full snapshot rebuild. These are snapped exactly onto the Host sample
                        // (no damping) on a short cooldown, because one write closes a sub-cell gap while
                        // damping would leave a residue that physics diverges again immediately.
                        bool microBand = microAlign && dist < MinAsyncPosSnapCells && dist >= microFloor;

                        // Hard cap on sub-precision work per report: a late-game save has ~17k dynamic
                        // bodies, and letting every sub-cell gap rewrite its body in one tick would
                        // cost more than the divergence it fixes.
                        if (microBand && microBudget <= 0) continue;

                        int cooldown = microBand ? MicroAlignCooldownTicks : EntityPosRealignCooldownTicks;
                        var lastCorrections = microBand ? _lastEntityMicroCorrectedTick : _lastEntityPosCorrectedTick;
                        if (lastCorrections.TryGetValue(d.Id, out int prevTick))
                        {
                            int elapsedTicks = item.Tick - prevTick;
                            if (elapsedTicks >= 0 && elapsedTicks < cooldown) continue;
                        }

                        bool hasOwner = TryGetActiveUnitOwner(e.ID, item.Tick, e, out byte ownerId);
                        if (hasOwner && ownerId == localPid)
                        {
                            // A unit the local player is steering is always left alone: the Host sample
                            // is already stale by the RTT and the local input has not reached the Host.
                            if (microBand || dist < SelfOwnedHostOverrideThreshold)
                            {
                                protectedSelfOwned++;
                                continue;
                            }
                        }

                        float applyDx, applyDy;
                        if (microBand)
                        {
                            applyDx = d.Dx;
                            applyDy = d.Dy;
                        }
                        else
                        {
                            float minThreshold = GameBridge.GetEntityPositionSnapThreshold(e, out float damping);
                            if (dist < minThreshold) continue;
                            applyDx = dist < 2.2f ? d.Dx * damping : d.Dx;
                            applyDy = dist < 2.2f ? d.Dy * damping : d.Dy;
                        }

                        float nx = LockstepClock.QuantizeCoord(e.Position.X + applyDx);
                        float ny = LockstepClock.QuantizeCoord(e.Position.Y + applyDy);
                        if (float.IsNaN(nx) || float.IsInfinity(nx) || float.IsNaN(ny) || float.IsInfinity(ny)) continue;

                        nx = Math.Max(0.5f, Math.Min(maxCoord, nx));
                        ny = Math.Max(0.5f, Math.Min(maxCoord, ny));
                        float actualDx = nx - e.Position.X;
                        float actualDy = ny - e.Position.Y;
                        if (actualDx == 0f && actualDy == 0f) continue;
                        var newPos = new PointF(nx, ny);
                        e.LastPosition = newPos;
                        e.Position = newPos;
                        var body = e.Body;
                        if (body != null && body.Handle != IntPtr.Zero)
                        {
                            body.LastPositionComputed = newPos;
                            body.PositionComputed = newPos;
                            body.PhysicPosition = newPos;
                        }
                        lastCorrections[e.ID] = item.Tick;
                        if (dist <= 3.5f)
                            RenderVisualInterpolationPatch.RecordVisualSnapOffset(e.ID, -actualDx, -actualDy);
                        appliedCount++;
                        if (microBand)
                        {
                            microApplied++;
                            microBudget--;
                        }
                    }
                }

                int lifeApplied = 0, killsApplied = 0;
                if (item.ApplyCorrection && item.LifeFixes != null && game != null)
                {
                    for (int i = 0; i < item.LifeFixes.Count; i++)
                    {
                        var lf = item.LifeFixes[i];
                        DXEntity e = game.GetEntityByID(lf.Id);
                        if (e == null || e.Disposed) continue;
                        // Missing-on-Host kills (Delta==0) are limited to zombies so an ID mismatch can
                        // never delete a player unit or building.
                        if (lf.Kill && lf.Delta == 0 && !GameBridge.IsZombieType(e.GetType())) continue;
                        if (GameBridge.TryCorrectLife(e, lf.Delta, lf.Kill))
                        {
                            if (lf.Kill) killsApplied++;
                            else lifeApplied++;
                        }
                    }
                }

                DXEntity maxEnt = item.MaxDeltaEntityId != 0 ? game?.GetEntityByID(item.MaxDeltaEntityId) : null;
                string maxType = maxEnt != null ? maxEnt.GetType().Name : "None";
                if (!_driftOnsetLogged && item.MaxDelta >= 0.05f && maxEnt != null)
                {
                    _driftOnsetLogged = true;
                    ModLogger.Info($"[DriftOnset] First observed pos drift at Tick={item.Tick}: EntityId={item.MaxDeltaEntityId}, Type={maxType}, Pos=({maxEnt.Position.X:F3},{maxEnt.Position.Y:F3}), MaxDelta={item.MaxDelta:F4}, Drifted001={item.Drifted001}, Rms={item.RmsDelta:F4}");
                }

                ModLogger.Info(
                    $"[AsyncPosAlign] Tick={item.Tick} Apply={item.ApplyCorrection}(pos={(item.ApplyPosCorrection ? 1 : 0)}:{appliedCount}/{item.ComparedCount}, micro={microApplied}, selfProt={protectedSelfOwned}) " +
                    $"Drifted(>0.001={item.Drifted001}, >0.01={item.Drifted01}, >0.1={item.Drifted1}, >0.5={item.Drifted5}) " +
                    $"MaxDelta={item.MaxDelta:F4} Rms={item.RmsDelta:F4} MaxType={maxType} Exceeded={item.ExceededThreshold} " +
                    $"LifeDiff={item.LifeDiffCount} LifeFix={lifeApplied} Kills={killsApplied} MissingOnHost={item.MissingOnHost} MissingOnClient={item.MissingOnClient}");

                Transport.SendToPeer(1, SendChannel.ReliableOrdered, PacketType.PosDriftStatsReport, w =>
                {
                    w.Write(item.Tick);
                    w.Write(item.ApplyCorrection);
                    w.Write(appliedCount + microApplied);
                    w.Write(item.ComparedCount);
                    w.Write(item.Drifted001);
                    w.Write(item.Drifted01);
                    w.Write(item.Drifted1);
                    w.Write(item.Drifted5);
                    w.Write(item.MaxDelta);
                    w.Write(item.RmsDelta);
                    w.Write(item.ExceededThreshold);
                    w.Write(maxType ?? "Unknown");
                    w.Write(item.LifeDiffCount);
                    w.Write(lifeApplied);
                    w.Write(killsApplied);
                    w.Write(item.MissingOnHost);
                    w.Write(item.MissingOnClient);
                });
            }

            while (_incomingReverseAlignQueue.TryDequeue(out var rev))
            {
                if (rev.Generation != curGen || Transport == null || !Transport.IsHost) continue;
                if (!EnableAsyncPosAlign || rev.Units == null || rev.Units.Count == 0) continue;
                if (!_tickPosCache.TryGetValue(rev.Tick, out var hostSamples) || hostSamples == null || hostSamples.Count == 0)
                    continue;

                var game = DXGame.Current;
                if (game == null) continue;
                int curTick = game.IAIterationInt;
                int nCells = DXWorldGrid.NCells;
                float maxCoord = nCells > 1 ? nCells - 0.5f : 10000f;
                int applied = 0;

                var revMap = new Dictionary<ulong, OwnedUnitPosSample>(rev.Units.Count);
                for (int i = 0; i < rev.Units.Count; i++)
                    revMap[rev.Units[i].Id] = rev.Units[i];

                for (int i = 0; i < hostSamples.Count; i++)
                {
                    var hs = hostSamples[i];
                    if (!hs.IsDynamic || !revMap.TryGetValue(hs.Id, out var u))
                        continue;

                    if (_lastEntityPosCorrectedTick.TryGetValue(u.Id, out int prevTick))
                    {
                        int elapsed = curTick - prevTick;
                        if (elapsed >= 0 && elapsed < EntityPosRealignCooldownTicks)
                            continue;
                    }

                    DXEntity e = game.GetEntityByID(u.Id);
                    if (e == null || e.Disposed || e.PhysicalType != DXBodyType.Dynamic) continue;
                    if (!TryGetActiveUnitOwner(e.ID, curTick, e, out byte ownerId) || ownerId != rev.SenderPlayerId)
                        continue;

                    // Compare same-tick epoch samples (u@rev.Tick vs hs@rev.Tick) to eliminate tick-phase skew.
                    float dx = u.X - hs.X;
                    float dy = u.Y - hs.Y;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist < ReverseAlignMinThreshold || dist > MaxAsyncTeleportCells) continue;

                    float damping = dist < 2.2f ? 0.65f : 1.0f;
                    float nx = LockstepClock.QuantizeCoord(e.Position.X + dx * damping);
                    float ny = LockstepClock.QuantizeCoord(e.Position.Y + dy * damping);
                    if (float.IsNaN(nx) || float.IsInfinity(nx) || float.IsNaN(ny) || float.IsInfinity(ny)) continue;

                    nx = Math.Max(0.5f, Math.Min(maxCoord, nx));
                    ny = Math.Max(0.5f, Math.Min(maxCoord, ny));
                    float actualDx = nx - e.Position.X;
                    float actualDy = ny - e.Position.Y;
                    var newPos = new PointF(nx, ny);
                    e.LastPosition = newPos;
                    e.Position = newPos;
                    var body = e.Body;
                    if (body != null && body.Handle != IntPtr.Zero)
                    {
                        body.LastPositionComputed = newPos;
                        body.PositionComputed = newPos;
                        body.PhysicPosition = newPos;
                    }
                    _lastEntityPosCorrectedTick[e.ID] = curTick;
                    if (dist <= 3.5f)
                        RenderVisualInterpolationPatch.RecordVisualSnapOffset(e.ID, -actualDx, -actualDy);
                    applied++;
                }

                if (applied > 0)
                    ModLogger.Info($"[AsyncReverseAlign] Client({rev.SenderPlayerId}) Tick={rev.Tick} applied={applied}/{rev.Units.Count}");
            }
        }

        private static void TrySendPosDriftSync(byte targetPlayerId, int tick, bool applyLifeCorrection, bool applyPosCorrection, ushort bucketMask, bool includeStatic = true)
        {
            if (Transport == null || !Transport.IsHost) return;
            if (!_tickPosCache.TryGetValue(tick, out var samples) || samples == null || samples.Count == 0)
                return;

            ushort effectiveMask = bucketMask == 0 ? (ushort)0xFFFF : bucketMask;
            int gen = _asyncGeneration;
            if (Interlocked.CompareExchange(ref _posSyncBuildInFlight, 1, 0) != 0)
                return;

            List<StateHasher.EntityPosSample> filtered = new List<StateHasher.EntityPosSample>(
                effectiveMask == 0xFFFF ? samples.Count : (samples.Count / 4 + 16));
            for (int i = 0; i < samples.Count; i++)
            {
                var s = samples[i];
                if (!includeStatic && !s.IsDynamic) continue;
                if (effectiveMask == 0xFFFF || (effectiveMask & (1 << (int)(s.Id & 0xFUL))) != 0)
                    filtered.Add(s);
            }
            if (filtered.Count == 0)
            {
                Interlocked.Exchange(ref _posSyncBuildInFlight, 0);
                return;
            }

            ushort sentMask = effectiveMask;
            byte flags = (byte)((applyLifeCorrection ? 1 : 0) | (applyPosCorrection ? 2 : 0) | (includeStatic ? 4 : 0));
            int seq = ++_posSyncSeq;
            Task.Run(() =>
            {
                try
                {
                    if (gen != _asyncGeneration) return;

                    byte[] compressed;
                    using (var rawMs = new MemoryStream(filtered.Count * 20 + 12))
                    using (var bw = new BinaryWriter(rawMs))
                    {
                        bw.Write(sentMask);
                        bw.Write(flags);
                        bw.Write(filtered.Count);
                        for (int i = 0; i < filtered.Count; i++)
                        {
                            bw.Write(filtered[i].Id);
                            bw.Write(filtered[i].X);
                            bw.Write(filtered[i].Y);
                            bw.Write(filtered[i].Life);
                        }
                        bw.Flush();
                        byte[] rawBytes = rawMs.ToArray();
                        using (var outMs = new MemoryStream(Math.Max(64, rawBytes.Length / 2)))
                        {
                            using (var ds = new DeflateStream(outMs, CompressionLevel.Fastest, leaveOpen: true))
                                ds.Write(rawBytes, 0, rawBytes.Length);
                            compressed = outMs.ToArray();
                        }
                    }

                    if (gen != _asyncGeneration) return;

                    int totalLen = compressed.Length;
                    int totalChunks = Math.Max(1, (totalLen + ChunkSizeBytes - 1) / ChunkSizeBytes);
                    var chunks = new List<byte[]>(totalChunks);
                    for (int idx = 0; idx < totalChunks; idx++)
                    {
                        int offset = idx * ChunkSizeBytes;
                        int len = Math.Min(ChunkSizeBytes, totalLen - offset);
                        var chunk = new byte[len];
                        Buffer.BlockCopy(compressed, offset, chunk, 0, len);
                        chunks.Add(chunk);
                    }

                    _outgoingPosSyncQueue.Enqueue(new OutgoingPosSyncBatch
                    {
                        Generation = gen,
                        TargetPlayerId = targetPlayerId,
                        Seq = seq,
                        Tick = tick,
                        ApplyCorrection = applyLifeCorrection,
                        TotalCompressedLen = totalLen,
                        Chunks = chunks
                    });
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[AsyncPosAlign] Async compress failed: {ex.Message}");
                }
                finally
                {
                    if (gen == _asyncGeneration)
                        Interlocked.Exchange(ref _posSyncBuildInFlight, 0);
                }
            });
        }

        private static void OnPosDriftSyncChunkReceived(BinaryReader r)
        {
            int seq = r.ReadInt32();
            int tick = r.ReadInt32();
            bool applyCorrection = r.ReadBoolean();
            int totalChunks = r.ReadInt32();
            int chunkIdx = r.ReadInt32();
            int compressedTotalLen = r.ReadInt32();
            int len = r.ReadInt32();
            byte[] part = r.ReadBytes(len);

            if (!_posSyncIncoming.TryGetValue(seq, out var buf))
            {
                buf = new PosSyncReceiveBuffer
                {
                    SimTick = tick,
                    ApplyCorrection = applyCorrection,
                    TotalChunks = totalChunks,
                    CompressedTotalLen = compressedTotalLen
                };
                _posSyncIncoming[seq] = buf;
            }
            buf.Chunks[chunkIdx] = part;
            if (buf.Chunks.Count < buf.TotalChunks) return;

            _posSyncIncoming.Remove(seq);
            // Prune stale incomplete sequences if any
            if (_posSyncIncoming.Count > 4) _posSyncIncoming.Clear();

            byte[] compressed = new byte[buf.CompressedTotalLen];
            int offset = 0;
            for (int i = 0; i < buf.TotalChunks; i++)
            {
                if (!buf.Chunks.TryGetValue(i, out byte[] p)) return;
                Buffer.BlockCopy(p, 0, compressed, offset, p.Length);
                offset += p.Length;
            }

            if (!_tickPosCache.TryGetValue(buf.SimTick, out var clientSamples) || clientSamples == null)
            {
                ModLogger.Warn($"[AsyncPosAlign] No cached client positions for Tick {buf.SimTick}; cannot measure/apply delta.");
                return;
            }

            var clientSnapshot = clientSamples.ToArray();
            int gen = _asyncGeneration;
            int simTick = buf.SimTick;
            bool apply = buf.ApplyCorrection;

            Task.Run(() =>
            {
                try
                {
                    if (gen != _asyncGeneration) return;

                    var clientMap = new Dictionary<ulong, StateHasher.EntityPosSample>(clientSnapshot.Length);
                    for (int i = 0; i < clientSnapshot.Length; i++)
                        clientMap[clientSnapshot[i].Id] = clientSnapshot[i];

                    int count;
                    ushort hostMask;
                    bool flagApplyLife;
                    bool flagApplyPos;
                    List<StateHasher.EntityPosSample> hostSamples;
                    using (var inMs = new MemoryStream(compressed))
                    using (var ds = new DeflateStream(inMs, CompressionMode.Decompress))
                    using (var br = new BinaryReader(ds))
                    {
                        hostMask = br.ReadUInt16();
                        byte flags = br.ReadByte();
                        flagApplyLife = (flags & 1) != 0;
                        flagApplyPos = (flags & 2) != 0;
                        count = br.ReadInt32();
                        hostSamples = new List<StateHasher.EntityPosSample>(count);
                        for (int i = 0; i < count; i++)
                        {
                            hostSamples.Add(new StateHasher.EntityPosSample
                            {
                                Id = br.ReadUInt64(),
                                X = br.ReadSingle(),
                                Y = br.ReadSingle(),
                                Life = br.ReadInt32()
                            });
                        }
                    }

                    if (gen != _asyncGeneration) return;

                    bool effectiveApplyLife = apply && flagApplyLife;
                    int comparedCount = 0;
                    int drifted001 = 0, drifted01 = 0, drifted1 = 0, drifted5 = 0;
                    int lifeDiffCount = 0, missingOnHost = 0, missingOnClient = 0;
                    float maxDelta = 0f;
                    ulong maxDeltaId = 0;
                    ulong hostMaxId = 0;
                    double sumSq = 0.0;
                    List<EntityDeltaFix> deltas = apply ? new List<EntityDeltaFix>(64) : null;
                    // Sub-precision band: drift below MinAsyncPosSnapCells is never corrected by the
                    // normal path, so it accumulates into CoarseDiff and then a snapshot rebuild.
                    // Collected into the same list; the apply side classifies each delta by its own
                    // measured gap and uses the matching cooldown/threshold.
                    bool microAlign = apply && flagApplyPos && MicroAlignEnabled;
                    float microFloor = microAlign ? MicroAlignFloorCells : 0f;
                    List<EntityLifeFix> lifeFixes = effectiveApplyLife ? new List<EntityLifeFix>(64) : null;
                    var hostIds = new HashSet<ulong>();

                    for (int i = 0; i < hostSamples.Count; i++)
                    {
                        var hs = hostSamples[i];
                        hostIds.Add(hs.Id);
                        if (hs.Id > hostMaxId) hostMaxId = hs.Id;
                        if (!clientMap.TryGetValue(hs.Id, out StateHasher.EntityPosSample cs))
                        {
                            missingOnClient++;
                            continue;
                        }

                        // Host-authoritative HP: apply the delta measured at the same epoch tick so
                        // damage taken locally since then is preserved.
                        if (hs.Life != cs.Life)
                        {
                            lifeDiffCount++;
                            if (effectiveApplyLife)
                                lifeFixes.Add(new EntityLifeFix { Id = hs.Id, Delta = hs.Life - cs.Life, Kill = hs.Life <= 0 });
                        }

                        if (!cs.IsDynamic) continue;
                        comparedCount++;

                        float dx = hs.X - cs.X;
                        float dy = hs.Y - cs.Y;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (dist > 0.001f)
                        {
                            drifted001++;
                            sumSq += dist * dist;
                        }
                        if (dist > 0.01f) drifted01++;
                        if (dist > 0.1f) drifted1++;
                        if (dist > 0.5f) drifted5++;

                        if (dist > maxDelta)
                        {
                            maxDelta = dist;
                            maxDeltaId = hs.Id;
                        }

                        // Normal band >= MinAsyncPosSnapCells, plus the sub-precision band below it
                        // (PrecisionAlign). Both go into one list: the apply side re-measures the gap
                        // per entity and picks the matching cooldown/threshold from that measurement,
                        // so a single list keeps the two paths from disagreeing with each other.
                        if (apply && dist <= MaxAsyncNudgeCells
                            && (dist >= MinAsyncPosSnapCells || (microAlign && dist >= microFloor)))
                            deltas.Add(new EntityDeltaFix { Id = hs.Id, Dx = dx, Dy = dy });
                    }

                    // Dynamic entities alive on the client but absent on the Host at the same epoch tick, whose
                    // ID the Host has already allocated past, are candidates for host-killed stragglers.
                    // Never kill a large batch when MissingOnClient is also non-trivial: that signals a
                    // DXGuid ID-shift or unspawned wave where mass-killing would wipe out Client's own horde.
                    var missingOnHostDynamicIds = effectiveApplyLife ? new List<ulong>(16) : null;
                    for (int i = 0; i < clientSnapshot.Length; i++)
                    {
                        var cs = clientSnapshot[i];
                        if (!cs.IsDynamic) continue;
                        if ((hostMask & (1 << (int)(cs.Id & 0xFUL))) == 0) continue;
                        if (hostIds.Contains(cs.Id) || cs.Id > hostMaxId) continue;
                        missingOnHost++;
                        missingOnHostDynamicIds?.Add(cs.Id);
                    }
                    if (effectiveApplyLife && missingOnHostDynamicIds != null
                        && missingOnHost > 0 && missingOnHost <= 8 && missingOnClient <= 4)
                    {
                        for (int i = 0; i < missingOnHostDynamicIds.Count; i++)
                            lifeFixes.Add(new EntityLifeFix { Id = missingOnHostDynamicIds[i], Delta = 0, Kill = true });
                    }

                    float rmsDelta = drifted001 > 0 ? (float)Math.Sqrt(sumSq / drifted001) : 0f;
                    bool exceededThreshold = maxDelta >= MaxAsyncTeleportCells;
                    bool emergencyPosOverride = maxDelta >= 3.5f || rmsDelta >= 1.2f;
                    bool effectiveApplyPos = apply && (flagApplyPos || emergencyPosOverride);

                    if (gen != _asyncGeneration) return;

                    _incomingPosAlignQueue.Enqueue(new PendingPosAlignApply
                    {
                        Generation = gen,
                        Tick = simTick,
                        ApplyCorrection = effectiveApplyLife,
                        ApplyPosCorrection = effectiveApplyPos,
                        ComparedCount = comparedCount,
                        Drifted001 = drifted001,
                        Drifted01 = drifted01,
                        Drifted1 = drifted1,
                        Drifted5 = drifted5,
                        MaxDelta = maxDelta,
                        RmsDelta = rmsDelta,
                        MaxDeltaEntityId = maxDeltaId,
                        ExceededThreshold = exceededThreshold,
                        Deltas = effectiveApplyPos ? deltas : null,
                        LifeFixes = lifeFixes,
                        LifeDiffCount = lifeDiffCount,
                        MissingOnHost = missingOnHost,
                        MissingOnClient = missingOnClient
                    });
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[AsyncPosAlign] Async decompress/scan failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// DIAGNOSTIC ONLY. On the first soft position drift of a segment, reports which position
        /// buckets differ. Bucket index == (entity.ID &amp; (bucketCount-1)), so the numbers point at
        /// the entity ID ranges that diverged first without dumping every entity.
        /// </summary>
        private static void LogPosBucketDiff(int tick, in StateHasher.LevelStateSnapshotDigest host, in StateHasher.LevelStateSnapshotDigest client)
        {
            try
            {
                var hb = host.PosBuckets;
                var cb = client.PosBuckets;
                if (hb == null || cb == null)
                {
                    ModLogger.Info($"[BucketDiff] Tick {tick}: buckets unavailable (host={hb?.Length ?? 0}, client={cb?.Length ?? 0}).");
                    return;
                }
                int n = Math.Min(hb.Length, cb.Length);
                int diff = 0;
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    if (hb[i] == cb[i]) continue;
                    diff++;
                    if (diff <= 8) sb.Append(' ').Append(i);
                }
                string tail = diff > 8 ? " ..." : string.Empty;
                ModLogger.Info($"[BucketDiff] Tick {tick}: {diff}/{n} pos buckets differ; first differing buckets:{sb}{tail} (bucket index == entity.ID & (n-1)).");
            }
            catch (Exception ex) { ModLogger.Warn($"[BucketDiff] failed: {ex.Message}"); }
        }

        private static void DumpHostEntitiesOnDesync(int tick)
        {
            if (_firstChecksumPosDumped) return;
            _firstChecksumPosDumped = true;
            DumpPeerEntities(tick, "coop_desync_pos_host.txt");
        }

        private static void DumpPeerEntities(int tick, string fileName)
        {
            try
            {
                var game = DXGame.Current;
                if (game?.Entities == null) return;
                string savesDir = GameBridge.GetSavesDirectory();
                if (string.IsNullOrEmpty(savesDir)) return;
                string path = Path.Combine(savesDir, fileName);
                var sb = new System.Text.StringBuilder(1 << 18);
                sb.Append("# tick=").Append(tick).Append('\n');
                int count = 0;
                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    if ((e.ID & 0x8000000000000000UL) != 0) continue;
                    if (e.GetType() == typeof(DXEntity) || string.Equals(e.GetType().Name, "Raven", StringComparison.Ordinal)) continue;
                    var b = e.Body;
                    float bx = b != null ? b.PositionComputed.X : float.NaN;
                    float by = b != null ? b.PositionComputed.Y : float.NaN;
                    sb.Append(e.ID).Append('|')
                      .Append(e.Position.X.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(e.Position.Y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(bx.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(by.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
                      .Append(e.GetType().Name).Append('\n');
                    count++;
                }
                File.WriteAllText(path, sb.ToString());
                ModLogger.Info($"[StateChecksum] Dumped {count} entity positions (tick {tick}) -> {path}");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[StateChecksum] DumpPeerEntities failed: {ex.Message}");
            }
        }

        private static void TryAssembleSnapshot()
        {
            if (_expectedChunks <= 0 || _incomingChunks.Count < _expectedChunks) return;

            for (int i = 0; i < _expectedChunks; i++)
                if (!_incomingChunks.ContainsKey(i)) return;

            int total = 0;
            for (int i = 0; i < _expectedChunks; i++) total += _incomingChunks[i].Length;
            byte[] savBytes = new byte[total];
            int offset = 0;
            for (int i = 0; i < _expectedChunks; i++)
            {
                byte[] part = _incomingChunks[i];
                Buffer.BlockCopy(part, 0, savBytes, offset, part.Length);
                offset += part.Length;
            }
            _incomingChunks.Clear();
            _expectedChunks = 0;

            ApplyReceivedSnapshotOnClient(savBytes, _incomingCheckContent, _incomingRandomSeed, _incomingEntityIdCounter);
        }

        private static void ApplyReceivedSnapshotOnClient(byte[] savBytes, string chkContent, int randomSeed,
            long hostEntityIdCounter)
        {
            try
            {
                string savesDir = GameBridge.GetSavesDirectory();
                // Reuse the Host's exact file stem: the engine's .zxcheck sidecar appears to be bound
                // to the save name (renaming it made ReadSaveFile fail with "error while loading").
                string clientSaveName = "coop_host_snapshot";
                string savPath = Path.Combine(savesDir, clientSaveName + ".zxsav");
                string chkPath = Path.Combine(savesDir, clientSaveName + ".zxcheck");

                File.WriteAllBytes(savPath, savBytes);
                // ZXGame validates saves via Path.ChangeExtension(path, ".zxcheck") and the sidecar
                // is bound to the save name, hence we reuse the Host's file stem above.
                File.WriteAllText(chkPath, chkContent);

                ModLogger.Info($"[CoopSessionManager] Saved received snapshot to {savPath} ({savBytes.Length:N0} bytes, check={chkContent.Length} chars). Deferring load to next frame...");

                // Defer: this runs inside network packet delivery. Loading here re-enters the engine
                // and previously blew the main-thread stack under Chaos.
                _incomingRandomSeed = randomSeed;
                _incomingEntityIdCounter = hostEntityIdCounter;
                _pendingClientSnapshotLoadPath = savPath;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Failed to apply received snapshot on Client", ex);
            }
        }

        /// <summary>Client-side snapshot load, executed from OnFrameTick before packet polling.</summary>
        private static void BeginClientSnapshotLoad(string savPath)
        {
            try
            {
                IsInSyncedLevel = false;
                LockstepClock.SetSessionPause(true);
                if (DXGame.Current != null) DXGame.Current.InputEnabled = false;

                if (!GameBridge.IsSaveLoadable(savPath))
                {
                    ModLogger.Error($"[CoopSessionManager] ZXGame rejected the received snapshot as unloadable: {savPath}");
                    GameBridge.ShowTopBanner("Received snapshot failed the save-file validity check", Color.OrangeRed, 4000);
                    return;
                }

                // Enable the deterministic sandbox and defer ID calibration to the loader completion
                // postfix — the level is not restored when LoadSaveFileAndEnter returns.
                RequestSnapshotCalibration(_incomingRandomSeed, _incomingEntityIdCounter);
                GameBridge.PrepareForProgrammaticLoad();

                ModLogger.Info($"[CoopSessionManager] Client calling LoadSaveFileAndEnter('{savPath}')...");
                GameBridge.LoadSaveFileAndEnter(savPath);
                ModLogger.Info("[CoopSessionManager] Client LoadSaveFileAndEnter returned; driving loader fades.");

                _kickingLoadFade = true;
                _loadFadeKickFrames = 0;
                _loadFadeKickStartTicks = -1.0;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopSessionManager] Client snapshot load threw", ex);
            }
        }

        /// <summary>
        /// Advances the engine's black loading-fade animation while the Client waits to enter the
        /// level. LoadGameAndEnter gates the real load behind a DXAnimation chain that never runs if
        /// the background window's scene does not advance it.
        /// </summary>
        private static void TickLoadingFade()
        {
            if (!_kickingLoadFade) return;

            if (!_awaitingSnapshotLoad && !_hostWaitingOwnReload && !LockstepClock.LoadingLevel)
            {
                _kickingLoadFade = false;
                return;
            }

            double now = DXHelper_Environment.Ticks;
            if (_loadFadeKickStartTicks < 0.0) _loadFadeKickStartTicks = now;
            else if (now - _loadFadeKickStartTicks >= LoadFadeTimeoutMs)
            {
                _kickingLoadFade = false;
                ModLogger.Error("[CoopSessionManager] Client snapshot load did not complete within 30s of driving the loader fades.");
                return;
            }

            GameBridge.KickLoadingFade(700.0);

            if ((++_loadFadeKickFrames % 60) == 0)
                ModLogger.Info($"[LoadFade] frames={_loadFadeKickFrames} awaited={_awaitingSnapshotLoad} loading={LockstepClock.LoadingLevel} zxLoading={GameBridge.IsGameLoading()} tick={DXGame.Current?.IAIterationInt ?? -1}");
        }
    }

    /// <summary>
    /// Hooks ZXGame.OnStartFrame and ZXSystemGameLevel.#=zmTU4kueQctVr (InitLevel)
    /// to drive CoopSessionManager every frame and auto-sync when Host enters a level.
    /// </summary>
    [HarmonyPatch]
    public static class SessionLifecyclePatches
    {
        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPostfix]
        public static void DXGame_StartFrame_Postfix()
        {
            CoopSessionManager.OnFrameTick();
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_InitLevel
        {
            public static System.Reflection.MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_InitLevel);
            }

            [HarmonyPrefix]
            public static void Prefix()
            {
                if (Determinism.LockstepClock.IsDeterministicModeActive)
                    Determinism.LockstepClock.ResetSyncedRandomStreamsForInitLevel();
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                if (!CoopSessionManager.IsSessionActive) return;

                if (CoopSessionManager.Transport.IsHost)
                {
                    // Snapshot export is triggered from CoopSessionManager.OnLevelLoadComplete, not
                    // here: InitLevel fires while the save is still deserializing.
                    ModLogger.Info("[SessionLifecyclePatches] Host level initialized; snapshot will be sent after load completes.");
                }
                else
                {
                    // Do NOT calibrate here: InitLevel runs on the loader's background thread and
                    // the level is not fully restored yet (see LevelLoadCompletePatch).
                    ModLogger.Info("[SessionLifecyclePatches] Client InitLevel fired; waiting for loader completion.");
                }
            }
        }
    }
}
