using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using Steamworks;
using TABCoopMod.Core;

namespace TABCoopMod.Networking
{
    /// <summary>
    /// Steamworks P2P Networking & Lobby Transport built on top of the game's embedded Steamworks.NET (v10.0.0.0).
    /// Supports Steam Lobby creation, Friend Invite overlay, isolated P2P channels (ch2 ReliableCommand,
    /// ch0 ReliableOrdered, ch1 UnreliableSequenced), and automatic Direct UDP Fast-Path upgrade.
    /// </summary>
    public sealed class SteamP2PTransport : INetworkTransport, ITransportDiagnostics
    {
        private struct HeldCommand
        {
            public PacketType Type;
            public byte[] Payload;
        }
        private static Callback<GameLobbyJoinRequested_t> _globalCbLobbyJoinRequested;
        private static Action<string> _globalInviteHandler;
        private static bool _startupLobbyChecked;
        private static string _pendingInviteLobbyId;

        private Callback<LobbyCreated_t> _cbLobbyCreated;
        private Callback<LobbyEnter_t> _cbLobbyEnter;
        private Callback<LobbyChatUpdate_t> _cbLobbyChatUpdate;
        private Callback<P2PSessionRequest_t> _cbP2PSessionRequest;
        private Callback<P2PSessionConnectFail_t> _cbP2PConnectFail;

        public static void RegisterGlobalInviteListener(Action<string> onInviteAccepted)
        {
            _globalInviteHandler = onInviteAccepted;
            try
            {
                if (_globalCbLobbyJoinRequested == null)
                {
                    _globalCbLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(param =>
                    {
                        string lobbyId = param.m_steamIDLobby.m_SteamID.ToString();
                        ModLogger.Info($"[SteamP2PTransport] Global Steam Invite accepted! LobbyID={lobbyId}");
                        _pendingInviteLobbyId = lobbyId;
                        _globalInviteHandler?.Invoke(lobbyId);
                    });
                    ModLogger.Info("[SteamP2PTransport] Global GameLobbyJoinRequested_t listener registered.");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[SteamP2PTransport] Failed to register global invite listener: {ex.Message}");
            }

            if (!_startupLobbyChecked)
            {
                _startupLobbyChecked = true;
                try
                {
                    string[] args = Environment.GetCommandLineArgs();
                    for (int i = 0; i < args.Length - 1; i++)
                    {
                        if (string.Equals(args[i], "+connect_lobby", StringComparison.OrdinalIgnoreCase) &&
                            ulong.TryParse(args[i + 1], out ulong lobbyId) && lobbyId != 0)
                        {
                            ModLogger.Info($"[SteamP2PTransport] Detected startup +connect_lobby {lobbyId}");
                            _pendingInviteLobbyId = lobbyId.ToString();
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        public static string ConsumePendingInviteLobbyId()
        {
            string id = _pendingInviteLobbyId;
            _pendingInviteLobbyId = null;
            return id;
        }

        public static void PumpSteamCallbacksIfNeeded(INetworkTransport activeTransport)
        {
            while (activeTransport is ChaosTransport ct)
                activeTransport = ct.InnerTransport;
            if (activeTransport is SteamP2PTransport) return;
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch { }
        }

        private CSteamID _currentLobbyId = CSteamID.Nil;
        private CSteamID _expectedLobbyId = CSteamID.Nil;
        private CSteamID _hostSteamId = CSteamID.Nil;
        private volatile bool _disposed;
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private readonly Dictionary<byte, CSteamID> _playerToSteamId = new Dictionary<byte, CSteamID>();
        private int _maxPlayers = 3;

        private DirectFastPathManager _direct;
        private readonly ConcurrentQueue<Action> _mainThreadActions = new ConcurrentQueue<Action>();
        private readonly Dictionary<byte, long> _cutoverPendingTs = new Dictionary<byte, long>();
        private readonly Dictionary<byte, List<HeldCommand>> _cutoverHoldQueue = new Dictionary<byte, List<HeldCommand>>();

        public bool IsActive { get; private set; }
        public bool IsHost { get; private set; }
        public byte LocalPlayerId { get; private set; } = 1;
        public CSteamID CurrentLobbyId => _currentLobbyId;
        public IReadOnlyList<PeerInfo> ConnectedPeers => _peers;

        public string ActivePathSummary
        {
            get
            {
                if (_direct != null && _direct.TryGetActiveSummary(out string directSummary, out _, out bool isDirect) && isDirect)
                    return directSummary;

                try
                {
                    foreach (var kv in _playerToSteamId)
                    {
                        if (kv.Key == LocalPlayerId || !kv.Value.IsValid()) continue;
                        if (SteamNetworking.GetP2PSessionState(kv.Value, out P2PSessionState_t state))
                        {
                            if (state.m_bConnectionActive != 0)
                                return state.m_bUsingRelay != 0 ? "Steam Relay" : "Steam Direct";
                            if (state.m_bConnecting != 0)
                                return "Steam Connecting";
                        }
                    }
                }
                catch { }
                return IsActive ? "Steam Idle" : "Offline";
            }
        }

        public bool IsDirectConnected => _direct != null && _direct.TryGetActiveSummary(out _, out _, out bool isDirect) && isDirect;

        public double ActiveDirectRttMs => _direct != null && _direct.TryGetActiveSummary(out _, out double rtt, out bool isDirect) && isDirect ? rtt : -1.0;

        public string GetDebugStatsLine()
        {
            string directStats = _direct != null ? _direct.GetDebugStatsLine() : "DirectUDP(off)";
            string steamStats = GetP2PDebugStats();
            return $"{directStats} | {steamStats}";
        }

        public string GetP2PDebugStats()
        {
            try
            {
                var parts = new List<string>();
                foreach (var kv in _playerToSteamId)
                {
                    if (kv.Key == LocalPlayerId || !kv.Value.IsValid()) continue;
                    if (SteamNetworking.GetP2PSessionState(kv.Value, out P2PSessionState_t st))
                    {
                        parts.Add($"P{kv.Key}(relay={st.m_bUsingRelay},act={st.m_bConnectionActive},qBytes={st.m_nBytesQueuedForSend},qPkt={st.m_nPacketsQueuedForSend},err={st.m_eP2PSessionError})");
                    }
                }
                return parts.Count > 0 ? "SteamP2P[" + string.Join("; ", parts) + "]" : "SteamP2P[no-remote]";
            }
            catch
            {
                return "SteamP2P[err]";
            }
        }

        public event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        public event Action<PeerInfo> OnPeerConnected;
        public event Action<byte> OnPeerDisconnected;
        public event Action<string, string, string> OnVersionMismatch;

        public void EnsureCallbacksRegistered()
        {
            if (_disposed || _cbLobbyCreated != null) return;

            _cbLobbyCreated = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _cbLobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _cbLobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
            _cbP2PSessionRequest = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
            _cbP2PConnectFail = Callback<P2PSessionConnectFail_t>.Create(OnP2PConnectFail);
        }

        private void DisposeCallbacks()
        {
            try { _cbLobbyCreated?.Dispose(); } catch { }
            try { _cbLobbyEnter?.Dispose(); } catch { }
            try { _cbLobbyChatUpdate?.Dispose(); } catch { }
            try { _cbP2PSessionRequest?.Dispose(); } catch { }
            try { _cbP2PConnectFail?.Dispose(); } catch { }
            _cbLobbyCreated = null;
            _cbLobbyEnter = null;
            _cbLobbyChatUpdate = null;
            _cbP2PSessionRequest = null;
            _cbP2PConnectFail = null;
        }

        public void StartHost(int maxPlayers = 3)
        {
            if (_disposed) return;
            EnsureCallbacksRegistered();
            IsHost = true;
            LocalPlayerId = 1;
            _maxPlayers = Math.Max(2, maxPlayers);
            _expectedLobbyId = CSteamID.Nil;
            _peers.Clear();
            _playerToSteamId.Clear();

            var mySteamId = SteamUser.GetSteamID();
            string myName = SteamFriends.GetPersonaName() ?? "Host";
            _hostSteamId = mySteamId;
            _playerToSteamId[1] = mySteamId;
            _peers.Add(new PeerInfo { PlayerId = 1, PlayerName = myName, SteamId64 = mySteamId.m_SteamID, IsReady = true });

            InitializeDirectFastPath(true);

            SteamNetworking.AllowP2PPacketRelay(true);
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, maxPlayers);
            IsActive = true;
            ModLogger.Info($"[SteamP2PTransport] Creating Steam Friends-Only Lobby for up to {maxPlayers} players...");
        }

        public void ConnectToHost(string targetAddressOrSteamId)
        {
            if (_disposed) return;
            EnsureCallbacksRegistered();
            IsHost = false;
            LocalPlayerId = 0;
            _expectedLobbyId = CSteamID.Nil;
            _peers.Clear();
            _playerToSteamId.Clear();
            InitializeDirectFastPath(false);
            SteamNetworking.AllowP2PPacketRelay(true);

            if (ulong.TryParse(targetAddressOrSteamId, out ulong steamOrLobbyId))
            {
                var targetId = new CSteamID(steamOrLobbyId);
                if (targetId.IsLobby())
                {
                    _expectedLobbyId = targetId;
                    ModLogger.Info($"[SteamP2PTransport] Joining Steam Lobby {steamOrLobbyId}...");
                    SteamMatchmaking.JoinLobby(targetId);
                    IsActive = true;
                }
                else
                {
                    _hostSteamId = targetId;
                    _playerToSteamId[1] = _hostSteamId;
                    IsActive = true;
                    SendHandshakeToHost();
                }
            }
        }

        public void OpenSteamInviteOverlay()
        {
            if (_currentLobbyId.IsValid())
            {
                SteamFriends.ActivateGameOverlayInviteDialog(_currentLobbyId);
            }
        }

        private void OnLobbyCreated(LobbyCreated_t param)
        {
            if (_disposed || !IsActive || !IsHost) return;
            if (param.m_eResult != EResult.k_EResultOK)
            {
                ModLogger.Error($"[SteamP2PTransport] CreateLobby failed: {param.m_eResult}");
                return;
            }
            _currentLobbyId = new CSteamID(param.m_ulSteamIDLobby);
            SteamMatchmaking.SetLobbyData(_currentLobbyId, "tab_coop_host", SteamUser.GetSteamID().m_SteamID.ToString());
            SteamMatchmaking.SetLobbyData(_currentLobbyId, "tab_coop_ver", ModInfo.Version);
            ModLogger.Info($"[SteamP2PTransport] Steam Lobby created! LobbyID={_currentLobbyId.m_SteamID} (ver={ModInfo.Version})");
        }

        private void OnLobbyEntered(LobbyEnter_t param)
        {
            if (_disposed || !IsActive) return;
            if (IsHost)
            {
                _currentLobbyId = new CSteamID(param.m_ulSteamIDLobby);
                return;
            }
            if (_expectedLobbyId.IsValid() && param.m_ulSteamIDLobby != _expectedLobbyId.m_SteamID) return;

            _currentLobbyId = new CSteamID(param.m_ulSteamIDLobby);
            var ownerId = SteamMatchmaking.GetLobbyOwner(_currentLobbyId);
            if (!ownerId.IsValid()) return;
            _hostSteamId = ownerId;
            _playerToSteamId[1] = ownerId;
            string lobbyVer = SteamMatchmaking.GetLobbyData(_currentLobbyId, "tab_coop_ver");
            ModLogger.Info($"[SteamP2PTransport] Entered Lobby {_currentLobbyId.m_SteamID}, Host SteamID={ownerId.m_SteamID} (lobbyVer={(string.IsNullOrEmpty(lobbyVer) ? "unknown" : lobbyVer)}). Sending Handshake...");
            if (!string.IsNullOrEmpty(lobbyVer) && !string.Equals(lobbyVer, ModInfo.Version, StringComparison.Ordinal))
            {
                string hostName = SteamFriends.GetFriendPersonaName(ownerId);
                if (string.IsNullOrEmpty(hostName)) hostName = "Host";
                ModLogger.Warn($"[SteamP2PTransport] Version mismatch detected from lobby metadata! Local={ModInfo.Version}, Host({hostName})={lobbyVer}");
                OnVersionMismatch?.Invoke(hostName, ModInfo.Version, lobbyVer);
            }
            SendHandshakeToHost();
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t param)
        {
            if (_disposed || !IsActive) return;
            if (!_currentLobbyId.IsValid() || param.m_ulSteamIDLobby != _currentLobbyId.m_SteamID) return;
            uint change = param.m_rgfChatMemberStateChange;
            uint leftFlags = (uint)(EChatMemberStateChange.k_EChatMemberStateChangeLeft |
                                    EChatMemberStateChange.k_EChatMemberStateChangeDisconnected |
                                    EChatMemberStateChange.k_EChatMemberStateChangeKicked |
                                    EChatMemberStateChange.k_EChatMemberStateChangeBanned);
            if ((change & leftFlags) == 0) return;

            var leftSteamId = new CSteamID(param.m_ulSteamIDUserChanged);
            byte disconnectedPlayerId = 0;
            foreach (var kv in _playerToSteamId)
            {
                if (kv.Value == leftSteamId)
                {
                    disconnectedPlayerId = kv.Key;
                    break;
                }
            }

            if (disconnectedPlayerId != 0)
            {
                _playerToSteamId.Remove(disconnectedPlayerId);
                _peers.RemoveAll(p => p.PlayerId == disconnectedPlayerId || (leftSteamId.IsValid() && p.SteamId64 == leftSteamId.m_SteamID));
                _cutoverPendingTs.Remove(disconnectedPlayerId);
                _cutoverHoldQueue.Remove(disconnectedPlayerId);
                _direct?.RemovePeer(disconnectedPlayerId);
                ModLogger.Info($"[SteamP2PTransport] Steam lobby member left: SteamID={leftSteamId.m_SteamID} -> PlayerId={disconnectedPlayerId}");
                OnPeerDisconnected?.Invoke(disconnectedPlayerId);
            }
        }

        private void SendHandshakeToHost()
        {
            if (_disposed || !IsActive || !_hostSteamId.IsValid()) return;
            SendToSteamUser(_hostSteamId, SendChannel.ReliableCommand, PacketType.HandshakeRequest, w =>
            {
                w.Write(SteamFriends.GetPersonaName() ?? "Client");
                w.Write(ModInfo.Version);
            });
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t param)
        {
            if (_disposed || !IsActive) return;
            ModLogger.Info($"[SteamP2PTransport] Accepting P2P session from {param.m_steamIDRemote.m_SteamID}");
            SteamNetworking.AcceptP2PSessionWithUser(param.m_steamIDRemote);
        }

        private void OnP2PConnectFail(P2PSessionConnectFail_t param)
        {
            if (_disposed || !IsActive) return;
            ModLogger.Warn($"[SteamP2PTransport] P2P session connect fail with {param.m_steamIDRemote.m_SteamID}: Error={param.m_eP2PSessionError}");
            foreach (var kv in _playerToSteamId)
            {
                if (kv.Value == param.m_steamIDRemote)
                {
                    byte deadId = kv.Key;
                    _playerToSteamId.Remove(deadId);
                    _peers.RemoveAll(p => p.PlayerId == deadId || (param.m_steamIDRemote.IsValid() && p.SteamId64 == param.m_steamIDRemote.m_SteamID));
                    _cutoverPendingTs.Remove(deadId);
                    _cutoverHoldQueue.Remove(deadId);
                    _direct?.RemovePeer(deadId);
                    OnPeerDisconnected?.Invoke(deadId);
                    break;
                }
            }
        }

        public void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            if (_playerToSteamId.TryGetValue(targetPlayerId, out var remoteSteamId))
            {
                byte[] payload = SerializePayload(writePayload);
                SendToPlayerWithRouting(targetPlayerId, remoteSteamId, channel, type, payload);
            }
        }

        public void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            byte[] payload = SerializePayload(writePayload);
            if (IsHost)
            {
                foreach (var kv in _playerToSteamId)
                {
                    if (kv.Key != LocalPlayerId)
                    {
                        SendToPlayerWithRouting(kv.Key, kv.Value, channel, type, payload);
                    }
                }
            }
            else if (_hostSteamId.IsValid())
            {
                SendToPlayerWithRouting(1, _hostSteamId, channel, type, payload);
            }
        }

        private static byte[] SerializePayload(Action<BinaryWriter> writePayload)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                writePayload?.Invoke(w);
                return ms.ToArray();
            }
        }

        private byte[] BuildFrameFromPayload(PacketType type, byte[] payload)
        {
            int plen = payload?.Length ?? 0;
            byte[] frame = new byte[8 + plen];
            frame[0] = 0x54; // 'T'
            frame[1] = 0x42; // 'B'
            frame[2] = (byte)type;
            frame[3] = LocalPlayerId;
            Buffer.BlockCopy(BitConverter.GetBytes(plen), 0, frame, 4, 4);
            if (plen > 0)
                Buffer.BlockCopy(payload, 0, frame, 8, plen);
            return frame;
        }

        private void SendToPlayerWithRouting(byte targetPlayerId, CSteamID targetSteamId, SendChannel channel, PacketType type, byte[] payload)
        {
            // Transport signaling/fallback packets always travel on Steam control plane.
            bool isSignaling =
                type == PacketType.HandshakeRequest ||
                type == PacketType.HandshakeAccept ||
                type == PacketType.DirectEndpointExchange ||
                type == PacketType.DirectCutoverSync ||
                type == PacketType.DirectFallbackBatch;

            if (!isSignaling && _direct != null && _direct.TrySendPacket(targetPlayerId, channel, type, payload))
                return;

            if (!isSignaling && channel == SendChannel.ReliableCommand && _cutoverPendingTs.TryGetValue(targetPlayerId, out long sentTs))
            {
                double waitMs = (Stopwatch.GetTimestamp() - sentTs) * 1000.0 / Stopwatch.Frequency;
                if (waitMs < 1500.0)
                {
                    if (!_cutoverHoldQueue.TryGetValue(targetPlayerId, out var q))
                    {
                        q = new List<HeldCommand>();
                        _cutoverHoldQueue[targetPlayerId] = q;
                    }
                    q.Add(new HeldCommand { Type = type, Payload = payload });
                    return;
                }

                // Cutover barrier timed out; release held commands cleanly over Steam.
                FlushCutoverHoldQueueOverSteam(targetPlayerId, targetSteamId);
            }

            SendToSteamUserPayload(targetSteamId, channel, type, payload);
        }

        private void SendToSteamUser(CSteamID targetSteamId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            byte[] payload = SerializePayload(writePayload);
            SendToSteamUserPayload(targetSteamId, channel, type, payload);
        }

        private void SendToSteamUserPayload(CSteamID targetSteamId, SendChannel channel, PacketType type, byte[] payload)
        {
            byte[] frame = BuildFrameFromPayload(type, payload);
            var sendType = channel != SendChannel.UnreliableSequenced
                ? EP2PSend.k_EP2PSendReliable
                : EP2PSend.k_EP2PSendUnreliableNoDelay;

            SteamNetworking.SendP2PPacket(targetSteamId, frame, (uint)frame.Length, sendType, (int)channel);
        }

        public void PollEvents()
        {
            if (!IsActive) return;

            try
            {
                SteamAPI.RunCallbacks();

                while (_mainThreadActions.TryDequeue(out var act))
                {
                    try { act(); }
                    catch (Exception ex) { ModLogger.Warn($"[SteamP2PTransport] Main-thread action failed: {ex.Message}"); }
                }

                CheckExpiredCutoverBarriers();

                if (_direct != null)
                {
                    while (_direct.TryDequeueIncoming(out var directPkt))
                    {
                        using (var ms = new MemoryStream(directPkt.Payload, false))
                        using (var reader = new BinaryReader(ms))
                        {
                            OnPacketReceived?.Invoke(directPkt.SenderId, directPkt.Type, reader);
                        }
                    }
                }

                int[] pollChannels =
                {
                    (int)SendChannel.ReliableCommand,
                    (int)SendChannel.UnreliableSequenced,
                    (int)SendChannel.ReliableOrdered
                };

                for (int ci = 0; ci < pollChannels.Length; ci++)
                {
                    int ch = pollChannels[ci];
                    while (SteamNetworking.IsP2PPacketAvailable(out uint msgSize, ch))
                    {
                        byte[] buf = new byte[msgSize];
                        if (SteamNetworking.ReadP2PPacket(buf, msgSize, out uint bytesRead, out CSteamID remoteId, ch) && bytesRead >= 8)
                        {
                            if (buf[0] != 0x54 || buf[1] != 0x42) continue;

                            var pktType = (PacketType)buf[2];
                            byte senderId = buf[3];
                            int payloadLen = BitConverter.ToInt32(buf, 4);
                            if (8 + payloadLen > bytesRead) continue;

                            using (var ms = new MemoryStream(buf, 8, payloadLen, false))
                            using (var reader = new BinaryReader(ms))
                            {
                                HandleSteamPacket(remoteId, senderId, pktType, reader);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[SteamP2PTransport] Exception in PollEvents", ex);
            }
        }

        private void HandleSteamPacket(CSteamID remoteSteamId, byte senderId, PacketType pktType, BinaryReader reader)
        {
            if (IsHost && pktType == PacketType.HandshakeRequest)
            {
                string clientName = reader.ReadString();
                string ver = reader.ReadString();
                bool versionMatched = string.Equals(ver, ModInfo.Version, StringComparison.Ordinal);
                if (!versionMatched)
                    ModLogger.Warn($"[SteamP2PTransport] Version mismatch! Host={ModInfo.Version}, Client({clientName})={ver}");
                else
                    ModLogger.Info($"[SteamP2PTransport] Handshake from {clientName} (ver={ver}).");

                byte existingId = ResolvePlayerIdBySteamId(remoteSteamId);
                var existingPeer = _peers.Find(p =>
                    (existingId != 0 && p.PlayerId == existingId)
                    || (remoteSteamId.m_SteamID != 0 && p.SteamId64 == remoteSteamId.m_SteamID && p.PlayerId != LocalPlayerId));
                if (existingId == 0 && existingPeer != null)
                    existingId = existingPeer.PlayerId;

                bool isDuplicate = existingId != 0 && existingId != LocalPlayerId;
                byte assignedId = isDuplicate ? existingId : AllocateLowestFreePlayerId();
                if (assignedId == 0)
                {
                    PruneStaleLobbyMembers();
                    assignedId = AllocateLowestFreePlayerId();
                }
                if (assignedId == 0)
                {
                    ModLogger.Warn($"[SteamP2PTransport] Rejected Handshake from {clientName} ({remoteSteamId.m_SteamID}): room full (maxPlayers={_maxPlayers}).");
                    return;
                }
                _playerToSteamId[assignedId] = remoteSteamId;

                var peer = new PeerInfo
                {
                    PlayerId = assignedId,
                    PlayerName = clientName,
                    SteamId64 = remoteSteamId.m_SteamID,
                    IsReady = isDuplicate && (existingPeer?.IsReady ?? false)
                };
                _peers.RemoveAll(p => p.PlayerId == assignedId || (remoteSteamId.m_SteamID != 0 && p.SteamId64 == remoteSteamId.m_SteamID && p.PlayerId != LocalPlayerId));
                _peers.Add(peer);
                _peers.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

                SendToSteamUser(remoteSteamId, SendChannel.ReliableCommand, PacketType.HandshakeAccept, w =>
                {
                    w.Write(assignedId);
                    w.Write((byte)_peers.Count);
                    foreach (var p in _peers)
                    {
                        w.Write(p.PlayerId);
                        w.Write(p.PlayerName);
                        w.Write(p.SteamId64);
                        w.Write(p.IsReady);
                    }
                    w.Write(ModInfo.Version);
                });

                if (isDuplicate)
                    ModLogger.Info($"[SteamP2PTransport] Deduplicated repeat Handshake from {clientName} ({remoteSteamId.m_SteamID}) -> keeping PlayerId={assignedId}");
                else
                    ModLogger.Info($"[SteamP2PTransport] Accepted Steam peer {clientName} ({remoteSteamId.m_SteamID}) -> PlayerId={assignedId}");

                _direct?.SetLocalPlayerId(LocalPlayerId);
                SendDirectCandidatesToPeer(assignedId);
                OnPeerConnected?.Invoke(peer);
                if (!versionMatched)
                    OnVersionMismatch?.Invoke(clientName, ModInfo.Version, ver);
                return;
            }

            if (!IsHost && pktType == PacketType.HandshakeAccept)
            {
                LocalPlayerId = reader.ReadByte();
                byte count = reader.ReadByte();
                _peers.Clear();
                _playerToSteamId.Clear();
                for (int i = 0; i < count; i++)
                {
                    var p = new PeerInfo
                    {
                        PlayerId = reader.ReadByte(),
                        PlayerName = reader.ReadString(),
                        SteamId64 = reader.ReadUInt64(),
                        IsReady = reader.ReadBoolean()
                    };
                    _peers.RemoveAll(existing => existing.PlayerId == p.PlayerId || (p.SteamId64 != 0 && existing.SteamId64 == p.SteamId64 && existing.PlayerId != 1));
                    _peers.Add(p);
                    if (p.SteamId64 != 0)
                        _playerToSteamId[p.PlayerId] = new CSteamID(p.SteamId64);
                }
                string hostVer = reader.BaseStream.Position < reader.BaseStream.Length
                    ? reader.ReadString()
                    : (_currentLobbyId.IsValid() ? SteamMatchmaking.GetLobbyData(_currentLobbyId, "tab_coop_ver") : string.Empty);
                if (string.IsNullOrEmpty(hostVer))
                    hostVer = "<=0.1.1 (未知旧版本)";

                _direct?.SetLocalPlayerId(LocalPlayerId);
                SendDirectCandidatesToPeer(1);
                ModLogger.Info($"[SteamP2PTransport] Joined Host Lobby! Assigned LocalPlayerId={LocalPlayerId}, HostVer={hostVer}");
                OnPeerConnected?.Invoke(_peers.Find(p => p.PlayerId == LocalPlayerId));
                if (!string.Equals(hostVer, ModInfo.Version, StringComparison.Ordinal))
                {
                    string hostName = _peers.Find(p => p.PlayerId == 1)?.PlayerName ?? "Host";
                    ModLogger.Warn($"[SteamP2PTransport] Version mismatch! Client={ModInfo.Version}, Host({hostName})={hostVer}");
                    OnVersionMismatch?.Invoke(hostName, ModInfo.Version, hostVer);
                }
                return;
            }

            byte resolvedSender = senderId != 0 ? senderId : ResolvePlayerIdBySteamId(remoteSteamId);
            if (pktType == PacketType.DirectEndpointExchange)
            {
                HandleDirectEndpointExchange(resolvedSender, reader);
                return;
            }
            if (pktType == PacketType.DirectCutoverSync)
            {
                HandleDirectCutoverSync(resolvedSender, reader);
                return;
            }
            if (pktType == PacketType.DirectFallbackBatch)
            {
                HandleDirectFallbackBatch(resolvedSender, reader);
                return;
            }

            OnPacketReceived?.Invoke(senderId, pktType, reader);
        }

        public void SyncPeers(IEnumerable<PeerInfo> peers)
        {
            _peers.Clear();
            if (peers == null) return;
            foreach (var p in peers)
            {
                if (p == null) continue;
                _peers.Add(new PeerInfo
                {
                    PlayerId = p.PlayerId,
                    PlayerName = p.PlayerName ?? string.Empty,
                    SteamId64 = p.SteamId64,
                    IsReady = p.IsReady
                });
                if (p.SteamId64 != 0)
                    _playerToSteamId[p.PlayerId] = new CSteamID(p.SteamId64);
            }
        }

        private void InitializeDirectFastPath(bool isHost)
        {
            DisposeDirectOnly();
            try
            {
                _direct = new DirectFastPathManager(isHost);
                _direct.SetLocalPlayerId(LocalPlayerId);
                _direct.LocalCandidatesUpdated = (cands, token) =>
                    _mainThreadActions.Enqueue(() => BroadcastDirectCandidatesToAll(cands, token));
                _direct.PeerDirectCutoverNeeded = (peerId, _) =>
                    _mainThreadActions.Enqueue(() => SendCutoverBarrierToPeer(peerId));
                _direct.PeerFallbackTriggered = (peerId, msgs) =>
                    _mainThreadActions.Enqueue(() => SendFallbackBatchViaSteam(peerId, msgs));
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[SteamP2PTransport] DirectFastPath init skipped (Steam P2P fallback active): {ex.Message}");
                _direct = null;
            }
        }

        private void DisposeDirectOnly()
        {
            _cutoverPendingTs.Clear();
            _cutoverHoldQueue.Clear();
            while (_mainThreadActions.TryDequeue(out _)) { }
            try { _direct?.Dispose(); } catch { }
            _direct = null;
        }

        private byte ResolvePlayerIdBySteamId(CSteamID remoteSteamId)
        {
            foreach (var kv in _playerToSteamId)
            {
                if (kv.Value == remoteSteamId) return kv.Key;
            }
            return 0;
        }

        private byte AllocateLowestFreePlayerId()
        {
            int limit = Math.Min(255, Math.Max(2, _maxPlayers));
            for (int candidate = 2; candidate <= limit; candidate++)
            {
                byte id = (byte)candidate;
                if (_playerToSteamId.ContainsKey(id)) continue;
                bool inPeers = false;
                for (int i = 0; i < _peers.Count; i++)
                {
                    if (_peers[i].PlayerId == id) { inPeers = true; break; }
                }
                if (!inPeers) return id;
            }
            return 0;
        }

        private void PruneStaleLobbyMembers()
        {
            if (!_currentLobbyId.IsValid()) return;
            try
            {
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(_currentLobbyId);
                var activeMembers = new HashSet<ulong>();
                for (int i = 0; i < memberCount; i++)
                {
                    var m = SteamMatchmaking.GetLobbyMemberByIndex(_currentLobbyId, i);
                    if (m.IsValid()) activeMembers.Add(m.m_SteamID);
                }

                var staleIds = new List<byte>();
                foreach (var kv in _playerToSteamId)
                {
                    if (kv.Key != LocalPlayerId && kv.Value.IsValid() && !activeMembers.Contains(kv.Value.m_SteamID))
                        staleIds.Add(kv.Key);
                }
                for (int i = 0; i < staleIds.Count; i++)
                {
                    byte pid = staleIds[i];
                    _playerToSteamId.Remove(pid);
                    _peers.RemoveAll(p => p.PlayerId == pid);
                    _cutoverPendingTs.Remove(pid);
                    _cutoverHoldQueue.Remove(pid);
                    _direct?.RemovePeer(pid);
                    ModLogger.Info($"[SteamP2PTransport] Pruned stale lobby slot PlayerId={pid} before assigning new joiner.");
                    OnPeerDisconnected?.Invoke(pid);
                }
            }
            catch { }
        }

        private void SendDirectCandidatesToPeer(byte targetPlayerId)
        {
            if (_direct == null || targetPlayerId == 0 || targetPlayerId == LocalPlayerId) return;
            if (!_playerToSteamId.TryGetValue(targetPlayerId, out var steamId) || !steamId.IsValid()) return;
            var cands = _direct.GetLocalCandidatesSnapshot();
            SendDirectCandidatesPayload(steamId, cands, _direct.SessionToken);
        }

        private void BroadcastDirectCandidatesToAll(IReadOnlyList<DirectEndpointCandidate> cands, ulong token)
        {
            if (!IsActive || _direct == null || cands == null) return;
            if (IsHost)
            {
                foreach (var kv in _playerToSteamId)
                {
                    if (kv.Key != LocalPlayerId && kv.Value.IsValid())
                        SendDirectCandidatesPayload(kv.Value, cands, token);
                }
            }
            else if (LocalPlayerId != 0 && _hostSteamId.IsValid())
            {
                SendDirectCandidatesPayload(_hostSteamId, cands, token);
            }
        }

        private void SendDirectCandidatesPayload(CSteamID targetSteamId, IReadOnlyList<DirectEndpointCandidate> cands, ulong token)
        {
            if (cands == null || cands.Count == 0) return;
            SendToSteamUser(targetSteamId, SendChannel.ReliableCommand, PacketType.DirectEndpointExchange, w =>
            {
                w.Write(token);
                int maxCount = Math.Min(cands.Count, 24);
                w.Write((byte)maxCount);
                for (int i = 0; i < maxCount; i++)
                {
                    var c = cands[i];
                    byte[] ipBytes = c.Address.GetAddressBytes();
                    w.Write((byte)c.Kind);
                    w.Write((byte)ipBytes.Length);
                    w.Write(ipBytes);
                    w.Write((ushort)c.Port);
                }
            });
        }

        private void HandleDirectEndpointExchange(byte senderId, BinaryReader reader)
        {
            if (_direct == null || senderId == 0) return;
            ulong token = reader.ReadUInt64();
            byte count = reader.ReadByte();
            var list = new List<DirectEndpointCandidate>(count);
            for (int i = 0; i < count; i++)
            {
                var kind = (DirectCandidateKind)reader.ReadByte();
                byte ipLen = reader.ReadByte();
                byte[] ipBytes = reader.ReadBytes(ipLen);
                ushort port = reader.ReadUInt16();
                if (ipBytes.Length == 4 || ipBytes.Length == 16)
                {
                    list.Add(new DirectEndpointCandidate
                    {
                        Kind = kind,
                        Address = new IPAddress(ipBytes),
                        Port = port
                    });
                }
            }
            _direct.SetRemotePeerCandidates(senderId, token, list);
        }

        private void SendCutoverBarrierToPeer(byte peerId)
        {
            if (!IsActive || _direct == null || peerId == 0 || peerId == LocalPlayerId) return;
            if (_cutoverPendingTs.ContainsKey(peerId) || _direct.IsPeerDirectActive(peerId)) return;
            if (!_playerToSteamId.TryGetValue(peerId, out var steamId) || !steamId.IsValid()) return;

            _cutoverPendingTs[peerId] = Stopwatch.GetTimestamp();
            if (!_cutoverHoldQueue.ContainsKey(peerId))
                _cutoverHoldQueue[peerId] = new List<HeldCommand>();

            SendToSteamUser(steamId, SendChannel.ReliableCommand, PacketType.DirectCutoverSync, w => w.Write((byte)0));
        }

        private void HandleDirectCutoverSync(byte senderId, BinaryReader reader)
        {
            if (senderId == 0) return;
            byte phase = reader.ReadByte();
            if (phase == 0)
            {
                if (_playerToSteamId.TryGetValue(senderId, out var steamId) && steamId.IsValid())
                {
                    SendToSteamUser(steamId, SendChannel.ReliableCommand, PacketType.DirectCutoverSync, w => w.Write((byte)1));
                }
                return;
            }

            // Phase 1: Remote peer ACKed our barrier; all earlier Steam ReliableCommand packets have arrived.
            _cutoverPendingTs.Remove(senderId);
            _direct?.EnableDirectTxAfterBarrier(senderId, true);

            if (_cutoverHoldQueue.TryGetValue(senderId, out var held) && held.Count > 0)
            {
                _playerToSteamId.TryGetValue(senderId, out var steamId);
                for (int i = 0; i < held.Count; i++)
                {
                    var item = held[i];
                    if (_direct == null || !_direct.TrySendPacket(senderId, SendChannel.ReliableCommand, item.Type, item.Payload))
                    {
                        if (steamId.IsValid())
                            SendToSteamUserPayload(steamId, SendChannel.ReliableCommand, item.Type, item.Payload);
                    }
                }
                held.Clear();
            }
        }

        private void CheckExpiredCutoverBarriers()
        {
            if (_cutoverPendingTs.Count == 0) return;
            long now = Stopwatch.GetTimestamp();
            List<byte> expired = null;
            foreach (var kv in _cutoverPendingTs)
            {
                double waitMs = (now - kv.Value) * 1000.0 / Stopwatch.Frequency;
                if (waitMs >= 1500.0)
                {
                    if (expired == null) expired = new List<byte>();
                    expired.Add(kv.Key);
                }
            }
            if (expired == null) return;
            for (int i = 0; i < expired.Count; i++)
            {
                byte peerId = expired[i];
                if (_playerToSteamId.TryGetValue(peerId, out var steamId) && steamId.IsValid())
                    FlushCutoverHoldQueueOverSteam(peerId, steamId);
                else
                {
                    _cutoverPendingTs.Remove(peerId);
                    _cutoverHoldQueue.Remove(peerId);
                }
            }
        }

        private void FlushCutoverHoldQueueOverSteam(byte peerId, CSteamID steamId)
        {
            _cutoverPendingTs.Remove(peerId);
            if (_cutoverHoldQueue.TryGetValue(peerId, out var held) && held.Count > 0)
            {
                for (int i = 0; i < held.Count; i++)
                    SendToSteamUserPayload(steamId, SendChannel.ReliableCommand, held[i].Type, held[i].Payload);
                held.Clear();
            }
        }

        private void SendFallbackBatchViaSteam(byte peerId, List<DirectFastPathManager.FallbackMessage> msgs)
        {
            if (!IsActive || msgs == null || msgs.Count == 0) return;
            if (!_playerToSteamId.TryGetValue(peerId, out var steamId) || !steamId.IsValid()) return;

            var cmdMsgs = new List<DirectFastPathManager.FallbackMessage>();
            var bulkMsgs = new List<DirectFastPathManager.FallbackMessage>();
            for (int i = 0; i < msgs.Count; i++)
            {
                if (msgs[i].Channel == SendChannel.ReliableCommand) cmdMsgs.Add(msgs[i]);
                else bulkMsgs.Add(msgs[i]);
            }

            if (cmdMsgs.Count > 0)
                SendFallbackSlice(steamId, SendChannel.ReliableCommand, cmdMsgs);
            if (bulkMsgs.Count > 0)
                SendFallbackSlice(steamId, SendChannel.ReliableOrdered, bulkMsgs);
        }

        private void SendFallbackSlice(CSteamID steamId, SendChannel steamChannel, List<DirectFastPathManager.FallbackMessage> msgs)
        {
            SendToSteamUser(steamId, steamChannel, PacketType.DirectFallbackBatch, w =>
            {
                w.Write((ushort)msgs.Count);
                for (int i = 0; i < msgs.Count; i++)
                {
                    var m = msgs[i];
                    w.Write(m.MsgId);
                    w.Write((byte)m.Channel);
                    w.Write((byte)m.Type);
                    byte[] payload = m.Payload ?? Array.Empty<byte>();
                    w.Write(payload.Length);
                    if (payload.Length > 0) w.Write(payload);
                }
            });
        }

        private void HandleDirectFallbackBatch(byte senderId, BinaryReader reader)
        {
            ushort count = reader.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                uint msgId = reader.ReadUInt32();
                var channel = (SendChannel)reader.ReadByte();
                var innerType = (PacketType)reader.ReadByte();
                int len = reader.ReadInt32();
                byte[] payload = len > 0 ? reader.ReadBytes(len) : Array.Empty<byte>();

                if (_direct != null && _direct.WasMessageDeliveredViaDirect(senderId, channel, msgId))
                    continue;

                using (var ms = new MemoryStream(payload, false))
                using (var innerReader = new BinaryReader(ms))
                {
                    OnPacketReceived?.Invoke(senderId, innerType, innerReader);
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            IsActive = false;
            DisposeCallbacks();
            DisposeDirectOnly();
            if (_currentLobbyId.IsValid())
            {
                try { SteamMatchmaking.LeaveLobby(_currentLobbyId); } catch { }
                _currentLobbyId = CSteamID.Nil;
            }
            _expectedLobbyId = CSteamID.Nil;
            _peers.Clear();
            _playerToSteamId.Clear();
        }
    }
}
