using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using DXVision;
using Steamworks;
using TABCoopMod.Commands;
using TABCoopMod.Core;

namespace TABCoopMod.Networking
{
    public enum SendChannel : byte
    {
        ReliableOrdered = 0,     // Bulk reliable (snapshots, PosDriftSync, checksums)
        UnreliableSequenced = 1, // Unreliable low-latency (ally cursor/camera, RTT probe)
        ReliableCommand = 2      // High-priority reliable commands (inputs, turns, lobby, session control)
    }

    public enum PacketType : byte
    {
        HandshakeRequest = 0x01,
        HandshakeAccept = 0x02,
        LobbyProbe = 0x06,
        LobbyProbeAck = 0x07,
        LobbyRosterSync = 0x08,
        ClientReadyState = 0x09,
        LobbyHostNotice = 0x0A,
        DirectEndpointExchange = 0x0B,
        DirectCutoverSync = 0x0C,
        DirectFallbackBatch = 0x0D,
        SnapshotBegin = 0x10,
        SnapshotChunk = 0x11,
        SnapshotLoadedAck = 0x12,
        SessionStart = 0x13,
        ClientInputReport = 0x20,
        TurnInputBroadcast = 0x21,
        HostPauseKeepalive = 0x22,
        LevelEnded = 0x23,
        StateChecksumReport = 0x30,
        DesyncDetectedAlert = 0x31,
        PosDriftSyncChunk = 0x32,
        PosDriftStatsReport = 0x33,
        AllyCursorAndCamera = 0x40,
        TacticalPing = 0x41,
        RttPing = 0x42,
        RttPong = 0x43
    }

    public interface ITransportDiagnostics
    {
        bool IsDirectConnected { get; }
        string ActivePathSummary { get; }
        double ActiveDirectRttMs { get; }
        string GetDebugStatsLine();
    }

    public sealed class PeerInfo
    {
        public byte PlayerId { get; set; }
        public string PlayerName { get; set; } = string.Empty;
        public ulong SteamId64 { get; set; }
        public bool IsReady { get; set; }
    }

    public sealed class LocalProbeResult
    {
        public bool IsOnline { get; set; }
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public byte RoomPhase { get; set; }
        public string HostName { get; set; } = string.Empty;
    }

    public interface INetworkTransport : IDisposable
    {
        bool IsActive { get; }
        bool IsHost { get; }
        byte LocalPlayerId { get; }
        IReadOnlyList<PeerInfo> ConnectedPeers { get; }

        event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        event Action<PeerInfo> OnPeerConnected;
        event Action<byte> OnPeerDisconnected;
        event Action<string, string, string> OnVersionMismatch;

        void StartHost(int maxPlayers = 3);
        void ConnectToHost(string targetAddressOrSteamId);
        void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload);
        void PollEvents();
        void SyncPeers(IEnumerable<PeerInfo> peers);
    }

    /// <summary>
    /// Local TCP Loopback Transport (127.0.0.1:27099) for AI Agent automated dual-instance testing
    /// and local multi-window debugging without Steam Lobby overhead.
    /// </summary>
    public sealed class LocalTcpLoopbackTransport : INetworkTransport, ITransportDiagnostics
    {
        public const int DefaultPort = 27099;

        /// <summary>How long a client waits for the TCP handshake before reporting a clear error.</summary>
        public const int ConnectTimeoutMs = 5000;

        private readonly IPAddress _bindAddress;
        private readonly int _port;
        private TcpListener _listener;
        private readonly List<(TcpClient Client, NetworkStream Stream, long AcceptedAtTicks)> _pendingAccepts = new List<(TcpClient, NetworkStream, long)>();
        private readonly List<(byte PlayerId, TcpClient Client, NetworkStream Stream)> _clients = new List<(byte, TcpClient, NetworkStream)>();
        private TcpClient _clientToHost;
        private NetworkStream _streamToHost;
        private string _lastRemoteEndpoint = "127.0.0.1";
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private int _maxPlayers = 3;

        /// <summary>
        /// Optional callback returning current Host lobby phase byte for non-intrusive LobbyProbeAck replies.
        /// </summary>
        public static Func<byte> HostRoomPhaseProvider { get; set; }

        /// <summary>Loopback-only transport (legacy local dual-instance mode).</summary>
        public LocalTcpLoopbackTransport()
            : this(IPAddress.Loopback, DefaultPort)
        {
        }

        /// <summary>
        /// Explicit bind address/port transport. Pass <see cref="IPAddress.Any"/> to accept
        /// connections from other machines on the LAN (native LAN co-op mode).
        /// </summary>
        public LocalTcpLoopbackTransport(IPAddress bindAddress, int port)
        {
            _bindAddress = bindAddress ?? IPAddress.Loopback;
            _port = port > 0 && port <= 65535 ? port : DefaultPort;
        }

        /// <summary>True when this transport is bound to a non-loopback (LAN) endpoint.</summary>
        public bool IsLanBound => _bindAddress != null && !IPAddress.IsLoopback(_bindAddress);

        /// <summary>The port this transport binds/listens or connects to.</summary>
        public int Port => _port;

        /// <summary>The bind address used when hosting.</summary>
        public IPAddress BindAddress => _bindAddress;

        /// <summary>
        /// One locally assigned IPv4 address together with the adapter that owns it, so the UI can
        /// tell a real Wi-Fi/Ethernet address apart from a virtual-adapter (Radmin / ZeroTier /
        /// VMware / Hyper-V) or APIPA (169.254.x.x) address.
        /// </summary>
        public sealed class LocalAddressInfo
        {
            public string Address;
            public string AdapterName;
            /// <summary>0 = physical LAN/Wi-Fi, 1 = virtual adapter, 2 = APIPA / link-local.</summary>
            public int Rank;
            public bool IsPhysical { get { return Rank == 0; } }

            /// <summary>Short label used in the lobby, e.g. "192.168.3.10 (WLAN)".</summary>
            public string Display
            {
                get
                {
                    string tag = Rank == 0 ? AdapterName
                               : Rank == 2 ? "无效地址" + (string.IsNullOrEmpty(AdapterName) ? "" : "/" + AdapterName)
                               : "虚拟网卡" + (string.IsNullOrEmpty(AdapterName) ? "" : "/" + AdapterName);
                    return string.IsNullOrEmpty(tag) ? Address : Address + " (" + tag + ")";
                }
            }
        }

        // Adapter-name / description fragments that identify virtual or non-LAN adapters.
        private static readonly string[] VirtualAdapterMarkers =
        {
            "radmin", "zerotier", "tailscale", "hamachi", "wg", "wireguard", "openvpn", "tap-",
            "tap ", "tun", "vmware", "virtualbox", "vbox", "hyper-v", "vethernet", "loopback",
            "npcap", "wintun", "softether", "pgyvpn", "pugongying", "蒲公英", "parallels"
        };

        private static bool IsApipa(string ip)
        {
            return ip != null && ip.StartsWith("169.254.", StringComparison.Ordinal);
        }

        private static bool LooksVirtual(string name, string description)
        {
            string hay = ((name ?? string.Empty) + " " + (description ?? string.Empty)).ToLowerInvariant();
            foreach (var marker in VirtualAdapterMarkers)
            {
                if (hay.Contains(marker)) return true;
            }
            return false;
        }

        /// <summary>
        /// Enumerates usable IPv4 addresses of this machine, ordered so that the address a teammate
        /// should actually type comes first: physical Wi-Fi/Ethernet, then virtual adapters
        /// (Radmin / ZeroTier / VPN), then APIPA 169.254.x.x addresses. Loopback is excluded.
        /// </summary>
        public static List<LocalAddressInfo> GetLocalAddressInfos()
        {
            var list = new List<LocalAddressInfo>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni == null) continue;
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props;
                    try { props = ni.GetIPProperties(); }
                    catch { continue; }
                    if (props == null) continue;

                    string adapterName = ni.Name ?? string.Empty;
                    string description = string.Empty;
                    try { description = ni.Description ?? string.Empty; } catch { }
                    bool virtualAdapter = LooksVirtual(adapterName, description)
                                          || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel;

                    foreach (var ua in props.UnicastAddresses)
                    {
                        if (ua == null || ua.Address == null) continue;
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string s = ua.Address.ToString();
                        if (string.IsNullOrEmpty(s) || s.StartsWith("127.", StringComparison.Ordinal)) continue;

                        int rank = IsApipa(s) ? 2 : (virtualAdapter ? 1 : 0);
                        bool dup = false;
                        foreach (var existing in list)
                        {
                            if (existing.Address == s) { dup = true; break; }
                        }
                        if (dup) continue;

                        list.Add(new LocalAddressInfo
                        {
                            Address = s,
                            AdapterName = adapterName,
                            Rank = rank
                        });
                    }
                }
            }
            catch
            {
            }

            // Stable sort by rank (physical first); within the same rank keep enumeration order.
            var ordered = new List<LocalAddressInfo>(list.Count);
            for (int rank = 0; rank <= 2; rank++)
            {
                foreach (var info in list)
                {
                    if (info.Rank == rank) ordered.Add(info);
                }
            }
            return ordered;
        }

        /// <summary>
        /// Convenience wrapper returning bare address strings, best candidate first.
        /// Virtual adapters and APIPA addresses are still included (they may be the only route
        /// between two machines on a Radmin / ZeroTier network) but always after physical ones.
        /// </summary>
        public static List<string> GetLocalIPv4Addresses()
        {
            var result = new List<string>();
            foreach (var info in GetLocalAddressInfos())
            {
                result.Add(info.Address);
            }
            return result;
        }

        /// <summary>
        /// Best single address to hand to a teammate: prefers a physical LAN/Wi-Fi address,
        /// falls back to a virtual adapter, and finally to an APIPA address. Empty if none.
        /// </summary>
        public static string GetPreferredLanAddress()
        {
            var infos = GetLocalAddressInfos();
            return infos.Count > 0 ? infos[0].Address : string.Empty;
        }

        /// <summary>Result of an attempt to open the inbound firewall for the LAN port.</summary>
        public enum FirewallOpenResult
        {
            AlreadyPresent = 0,
            Created = 1,
            Failed = 2,
            NotAttempted = 3
        }

        private static volatile string _firewallStatus = "未检测";

        /// <summary>Human readable status of the last firewall attempt (shown in the lobby).</summary>
        public static string FirewallStatus
        {
            get { return _firewallStatus ?? "未检测"; }
        }

        /// <summary>
        /// Attempts to add an inbound allow rule for this port so other machines can reach the host.
        /// Windows Firewall blocks inbound TCP by default, which looks exactly like "nothing is
        /// listening" to the joining player, so the host tries to open the port automatically.
        /// Elevation is required to change firewall state; without it this silently reports Failed
        /// and the UI shows the manual command instead.
        /// </summary>
        public static FirewallOpenResult EnsureFirewallOpen(int port)
        {
            try
            {
                string ruleName = "TABCoopMod LAN " + port;
                string check = "netsh advfirewall firewall show rule name=\"" + ruleName + "\"";
                if (RunHidden(check, 4000) == 0)
                {
                    _firewallStatus = "已放行 (规则已存在)";
                    return FirewallOpenResult.AlreadyPresent;
                }

                string add = "netsh advfirewall firewall add rule name=\"" + ruleName + "\""
                           + " dir=in action=allow protocol=TCP localport=" + port;
                int rc = RunHidden(add, 6000);
                if (rc == 0)
                {
                    _firewallStatus = "已自动放行 TCP " + port;
                    ModLogger.Info("[LAN] Opened Windows Firewall inbound TCP " + port + " (rule: " + ruleName + ")");
                    return FirewallOpenResult.Created;
                }

                _firewallStatus = "未能自动放行（需要管理员权限）";
                ModLogger.Warn("[LAN] Could not open firewall for TCP " + port + " (exit " + rc + "). Ask the host to run the netsh command as administrator.");
                return FirewallOpenResult.Failed;
            }
            catch (Exception ex)
            {
                _firewallStatus = "放行检查失败: " + ex.Message;
                ModLogger.Warn("[LAN] Firewall helper failed: " + ex.Message);
                return FirewallOpenResult.Failed;
            }
        }

        /// <summary>The exact command a host must run once, as administrator, if auto-open failed.</summary>
        public static string BuildFirewallCommand(int port)
        {
            return "netsh advfirewall firewall add rule name=\"TABCoopMod LAN " + port + "\""
                 + " dir=in action=allow protocol=TCP localport=" + port;
        }

        /// <summary>
        /// Second-chance firewall open that raises a UAC prompt. The game normally runs unelevated,
        /// so <see cref="EnsureFirewallOpen"/> cannot change firewall state; this shows the standard
        /// Windows consent dialog and only proceeds if the player approves it.
        /// Returns true when the rule is present afterwards (with or without elevation needed).
        /// </summary>
        public static bool EnsureFirewallOpenElevated(int port)
        {
            if (FirewallRuleExists(port))
            {
                _firewallStatus = "已放行 (规则已存在)";
                return true;
            }

            try
            {
                string ruleName = "TABCoopMod LAN " + port;
                var psi = new ProcessStartInfo("netsh",
                    "advfirewall firewall add rule name=\"" + ruleName + "\""
                    + " dir=in action=allow protocol=TCP localport=" + port)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                ModLogger.Info("[LAN] Requesting elevation (UAC) to open inbound TCP " + port + " ...");
                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                    {
                        _firewallStatus = "未能自动放行（提权进程未启动）";
                        return false;
                    }

                    if (!proc.WaitForExit(20000))
                    {
                        try { proc.Kill(); } catch { }
                        _firewallStatus = "未能自动放行（等待管理员确认超时）";
                        ModLogger.Warn("[LAN] Elevated netsh timed out waiting for the UAC prompt.");
                        return false;
                    }

                    if (proc.ExitCode == 0 && FirewallRuleExists(port))
                    {
                        _firewallStatus = "已自动放行 TCP " + port + "（管理员授权）";
                        ModLogger.Info("[LAN] Opened Windows Firewall inbound TCP " + port + " via UAC elevation.");
                        return true;
                    }

                    _firewallStatus = "未能自动放行（管理员授权被拒绝）";
                    ModLogger.Warn("[LAN] Elevated netsh exited with " + proc.ExitCode + " for TCP " + port + ".");
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User pressed "No" on the UAC dialog (ERROR_CANCELLED).
                _firewallStatus = "未能自动放行（你取消了管理员授权）";
                ModLogger.Warn("[LAN] UAC prompt was cancelled while opening TCP " + port + ".");
                return false;
            }
            catch (Exception ex)
            {
                _firewallStatus = "未能自动放行（提权失败: " + ex.Message + "）";
                ModLogger.Warn("[LAN] Elevation helper failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>True when an inbound allow rule for this port already exists.</summary>
        public static bool FirewallRuleExists(int port)
        {
            try
            {
                string check = "advfirewall firewall show rule name=\"TABCoopMod LAN " + port + "\"";
                return RunHidden(check, 4000) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static int RunHidden(string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var proc = Process.Start(psi))
            {
                if (proc == null) return -1;
                try { proc.StandardOutput.ReadToEnd(); } catch { }
                try { proc.StandardError.ReadToEnd(); } catch { }
                if (!proc.WaitForExit(timeoutMs))
                {
                    try { proc.Kill(); } catch { }
                    return -2;
                }
                return proc.ExitCode;
            }
        }

        public bool IsActive { get; private set; }
        public bool IsHost { get; private set; }
        public byte LocalPlayerId { get; private set; } = 1;
        public IReadOnlyList<PeerInfo> ConnectedPeers => _peers;

        public bool IsDirectConnected => IsActive;
        public string ActivePathSummary =>
            IsLanBound ? (IsHost ? $"LAN ({_bindAddress}:{_port})" : $"LAN ({_lastRemoteEndpoint})")
                       : "Local Loopback (127.0.0.1)";
        public double ActiveDirectRttMs => 0.0;
        public string GetDebugStatsLine() =>
            $"LocalTcpLoopback(Active={IsActive}, Peers={_peers.Count}, Bind={_bindAddress}:{_port}, Lan={IsLanBound})";

        public event Action<byte, PacketType, BinaryReader> OnPacketReceived;
        public event Action<PeerInfo> OnPeerConnected;
        public event Action<byte> OnPeerDisconnected;
        public event Action<string, string, string> OnVersionMismatch;

        public void StartHost(int maxPlayers = 3)
        {
            Dispose();
            IsHost = true;
            LocalPlayerId = 1;
            _maxPlayers = Math.Max(2, maxPlayers);
            _peers.Clear();
            _peers.Add(new PeerInfo { PlayerId = 1, PlayerName = "HostPlayer", IsReady = true });

            _listener = new TcpListener(_bindAddress, _port);
            _listener.Start();
            IsActive = true;
            ModLogger.Info($"[LocalTcpLoopbackTransport] Host listening on {_bindAddress}:{_port} (Lan={IsLanBound})");

            if (IsLanBound)
            {
                // Windows Firewall drops inbound TCP by default, which is indistinguishable from
                // "no one is listening" for the joining player. Try to open it once per host start.
                EnsureFirewallOpen(_port);
            }
        }

        public void ConnectToHost(string targetAddressOrSteamId)
        {
            Dispose();
            IsHost = false;
            LocalPlayerId = 0; // Assigned by Host upon HandshakeAccept
            _peers.Clear();

            string host = "127.0.0.1";
            int port = _port;
            if (!string.IsNullOrEmpty(targetAddressOrSteamId) && targetAddressOrSteamId.Contains(":"))
            {
                var parts = targetAddressOrSteamId.Split(':');
                host = parts[0];
                int parsed;
                if (int.TryParse(parts[1], out parsed) && parsed > 0 && parsed <= 65535) port = parsed;
            }
            else if (!string.IsNullOrEmpty(targetAddressOrSteamId))
            {
                host = targetAddressOrSteamId.Trim();
            }

            _lastRemoteEndpoint = $"{host}:{port}";
            _clientToHost = new TcpClient { NoDelay = true };
            try
            {
                // Bounded connect: a wrong IP or a firewalled host must surface as a clear error
                // within a few seconds instead of blocking the render thread on the OS default
                // (~21s SYN retries) or throwing a raw SocketException.
                IAsyncResult ar = _clientToHost.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                {
                    try { _clientToHost.Close(); } catch { }
                    _clientToHost = null;
                    throw new TimeoutException(
                        $"连接 {host}:{port} 超时（{ConnectTimeoutMs} ms 无响应）。" +
                        "请确认房主已点【创建局域网房间】、双方在同一网段、" +
                        "且房主放行了 TCP 27099 防火墙。");
                }
                _clientToHost.EndConnect(ar);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try { if (_clientToHost != null) _clientToHost.Close(); } catch { }
                _clientToHost = null;
                throw new InvalidOperationException(
                    $"无法连接 {host}:{port} — {ex.Message}。" +
                    "请确认房主已创建局域网房间、IP 填写正确、且未被防火墙拦截。", ex);
            }

            _streamToHost = _clientToHost.GetStream();
            IsActive = true;

            ModLogger.Info($"[LocalTcpLoopbackTransport] Connected to Host at {host}:{port}, sending HandshakeRequest...");
            SendToPeer(1, SendChannel.ReliableCommand, PacketType.HandshakeRequest, w =>
            {
                w.Write(Environment.UserName ?? "ClientPlayer");
                w.Write(ModInfo.Version);
            });
        }

        public void SendToPeer(byte targetPlayerId, SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            byte[] frame = BuildFrame(LocalPlayerId, type, writePayload);

            if (IsHost)
            {
                foreach (var c in _clients)
                {
                    if (c.PlayerId == targetPlayerId)
                    {
                        TryWriteFrame(c.Stream, frame);
                        break;
                    }
                }
            }
            else if (_streamToHost != null)
            {
                TryWriteFrame(_streamToHost, frame);
            }
        }

        public void Broadcast(SendChannel channel, PacketType type, Action<BinaryWriter> writePayload)
        {
            if (!IsActive) return;
            byte[] frame = BuildFrame(LocalPlayerId, type, writePayload);

            if (IsHost)
            {
                foreach (var c in _clients)
                {
                    TryWriteFrame(c.Stream, frame);
                }
            }
            else if (_streamToHost != null)
            {
                TryWriteFrame(_streamToHost, frame);
            }
        }

        public void PollEvents()
        {
            if (!IsActive) return;

            try
            {
                if (IsHost)
                {
                    while (_listener != null && _listener.Pending())
                    {
                        var tcp = _listener.AcceptTcpClient();
                        tcp.NoDelay = true;
                        _pendingAccepts.Add((tcp, tcp.GetStream(), DateTime.UtcNow.Ticks));
                    }

                    long nowTicks = DateTime.UtcNow.Ticks;
                    for (int i = _pendingAccepts.Count - 1; i >= 0; i--)
                    {
                        var pending = _pendingAccepts[i];
                        if (pending.Client == null || pending.Client.Client == null || !pending.Client.Connected)
                        {
                            ClosePending(i);
                            continue;
                        }

                        if (!pending.Stream.DataAvailable)
                        {
                            if (nowTicks - pending.AcceptedAtTicks > TimeSpan.FromSeconds(3).Ticks || IsSocketDisconnected(pending.Client))
                                ClosePending(i);
                            continue;
                        }

                        if (!TryReadSingleFrame(pending.Stream, out var pktType, out byte senderId, out byte[] payload))
                        {
                            ClosePending(i);
                            continue;
                        }

                        if (pktType == PacketType.LobbyProbe)
                        {
                            byte phase = HostRoomPhaseProvider != null ? HostRoomPhaseProvider() : (byte)1;
                            string hostName = _peers.Count > 0 ? (_peers[0].PlayerName ?? "HostPlayer") : "HostPlayer";
                            byte[] ack = BuildFrame(1, PacketType.LobbyProbeAck, w =>
                            {
                                w.Write((byte)_peers.Count);
                                w.Write((byte)_maxPlayers);
                                w.Write(phase);
                                w.Write(hostName);
                            });
                            TryWriteFrame(pending.Stream, ack);
                            ClosePending(i);
                            continue;
                        }

                        if (pktType == PacketType.HandshakeRequest)
                        {
                            byte assignedId = AllocateLowestFreePlayerId();
                            if (_peers.Count >= _maxPlayers || assignedId == 0)
                            {
                                ClosePending(i);
                                continue;
                            }

                            _pendingAccepts.RemoveAt(i);
                            _clients.Add((assignedId, pending.Client, pending.Stream));
                            ModLogger.Info($"[LocalTcpLoopbackTransport] Accepted client connection -> Assigned PlayerId={assignedId}");

                            using (var ms = new MemoryStream(payload, false))
                            using (var reader = new BinaryReader(ms))
                            {
                                HandleInternalOrDispatch(assignedId, pktType, reader);
                            }

                            if (!ReadAvailableFrames(pending.Stream, assignedId))
                            {
                                RemoveClientById(assignedId);
                            }
                            continue;
                        }

                        ClosePending(i);
                    }

                    for (int i = _clients.Count - 1; i >= 0; i--)
                    {
                        var entry = _clients[i];
                        if (!ReadAvailableFrames(entry.Stream, entry.PlayerId) || IsSocketDisconnected(entry.Client))
                        {
                            byte deadId = entry.PlayerId;
                            try { entry.Stream?.Close(); entry.Client?.Close(); } catch { }
                            _clients.RemoveAt(i);
                            _peers.RemoveAll(p => p.PlayerId == deadId);
                            OnPeerDisconnected?.Invoke(deadId);
                        }
                    }
                }
                else if (_streamToHost != null)
                {
                    if (!ReadAvailableFrames(_streamToHost, 1) || IsSocketDisconnected(_clientToHost))
                    {
                        ModLogger.Warn("[LocalTcpLoopbackTransport] Disconnected from Host.");
                        Dispose();
                        OnPeerDisconnected?.Invoke(1);
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[LocalTcpLoopbackTransport] Exception in PollEvents", ex);
            }
        }

        private byte AllocateLowestFreePlayerId()
        {
            int limit = Math.Min(255, Math.Max(2, _maxPlayers));
            for (int candidate = 2; candidate <= limit; candidate++)
            {
                byte id = (byte)candidate;
                bool used = false;
                for (int i = 0; i < _clients.Count; i++)
                {
                    if (_clients[i].PlayerId == id) { used = true; break; }
                }
                if (!used)
                {
                    for (int i = 0; i < _peers.Count; i++)
                    {
                        if (_peers[i].PlayerId == id) { used = true; break; }
                    }
                }
                if (!used) return id;
            }
            return 0;
        }

        private void ClosePending(int index)
        {
            var p = _pendingAccepts[index];
            _pendingAccepts.RemoveAt(index);
            try { p.Stream?.Close(); p.Client?.Close(); } catch { }
        }

        private void RemoveClientById(byte playerId)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (_clients[i].PlayerId == playerId)
                {
                    var entry = _clients[i];
                    try { entry.Stream?.Close(); entry.Client?.Close(); } catch { }
                    _clients.RemoveAt(i);
                }
            }
            _peers.RemoveAll(p => p.PlayerId == playerId);
            OnPeerDisconnected?.Invoke(playerId);
        }

        private static bool IsSocketDisconnected(TcpClient client)
        {
            try
            {
                if (client == null || client.Client == null || !client.Connected) return true;
                return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0;
            }
            catch
            {
                return true;
            }
        }

        public static LocalProbeResult ProbeLocalHost(string host = "127.0.0.1", int port = DefaultPort, int timeoutMs = 250)
        {
            var result = new LocalProbeResult { IsOnline = false };
            TcpClient tcp = null;
            try
            {
                tcp = new TcpClient { NoDelay = true, ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
                var iar = tcp.BeginConnect(host, port, null, null);
                if (!iar.AsyncWaitHandle.WaitOne(timeoutMs))
                    return result;
                tcp.EndConnect(iar);

                using (var stream = tcp.GetStream())
                {
                    stream.ReadTimeout = timeoutMs;
                    stream.WriteTimeout = timeoutMs;
                    byte[] probe = BuildFrame(0, PacketType.LobbyProbe, null);
                    stream.Write(probe, 0, probe.Length);
                    stream.Flush();

                    if (TryReadSingleFrame(stream, out var pktType, out _, out byte[] payload) && pktType == PacketType.LobbyProbeAck)
                    {
                        using (var ms = new MemoryStream(payload, false))
                        using (var reader = new BinaryReader(ms))
                        {
                            result.IsOnline = true;
                            result.PlayerCount = reader.ReadByte();
                            result.MaxPlayers = reader.ReadByte();
                            result.RoomPhase = reader.ReadByte();
                            result.HostName = reader.ReadString();
                        }
                    }
                }
            }
            catch
            {
                // Host not listening or timed out; keep IsOnline = false quietly.
            }
            finally
            {
                try { tcp?.Close(); } catch { }
            }
            return result;
        }

        /// <summary>
        /// Probes an arbitrary LAN host and reports *why* it failed instead of swallowing the
        /// error. A closed/blocked port and a wrong address both looked identical ("no host") in
        /// <see cref="ProbeLocalHost"/>, which made remote troubleshooting impossible.
        /// </summary>
        public static bool ProbeLanHost(string host, int port, int timeoutMs, out LocalProbeResult result, out string failure)
        {
            result = new LocalProbeResult { IsOnline = false };
            failure = string.Empty;
            TcpClient tcp = null;

            if (string.IsNullOrEmpty(host))
            {
                failure = "未填写 IP 地址";
                return false;
            }

            IPAddress parsedIp;
            if (!IPAddress.TryParse(host, out parsedIp))
            {
                // Allow a plain hostname too, but surface DNS failures clearly.
                try
                {
                    var addresses = Dns.GetHostAddresses(host);
                    if (addresses == null || addresses.Length == 0)
                    {
                        failure = "无法解析主机名（DNS 无结果）";
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    failure = "无法解析主机名: " + ex.Message;
                    return false;
                }
            }

            if (IsApipa(host))
            {
                failure = "169.254.x.x 是无效地址（对方网卡没拿到 IP），请让对方运行 ipconfig 查看 192.168.x.x 地址";
                return false;
            }

            try
            {
                tcp = new TcpClient { NoDelay = true, ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
                IAsyncResult iar = tcp.BeginConnect(host, port, null, null);
                if (!iar.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    failure = $"连接超时（{timeoutMs} ms 内没有任何响应），通常是对方防火墙拦掉了 TCP {port}，或该 IP 不是房主";
                    return false;
                }

                try
                {
                    tcp.EndConnect(iar);
                }
                catch (SocketException se)
                {
                    switch (se.SocketErrorCode)
                    {
                        case SocketError.ConnectionRefused:
                            failure = "连接被拒绝（对方主机在线但 27099 端口没有监听，可能房主还没点【创建局域网房间】）";
                            break;
                        case SocketError.HostUnreachable:
                        case SocketError.NetworkUnreachable:
                            failure = "网络不可达（该 IP 与本机不在同一网络，检查网段或 VPN）";
                            break;
                        case SocketError.TimedOut:
                            failure = "连接超时（多半是防火墙丢弃了数据包）";
                            break;
                        default:
                            failure = "连接失败: " + se.SocketErrorCode;
                            break;
                    }
                    return false;
                }

                using (var stream = tcp.GetStream())
                {
                    stream.ReadTimeout = timeoutMs;
                    stream.WriteTimeout = timeoutMs;
                    byte[] probe = BuildFrame(0, PacketType.LobbyProbe, null);
                    stream.Write(probe, 0, probe.Length);
                    stream.Flush();

                    PacketType pktType;
                    byte senderId;
                    byte[] payload;
                    if (!TryReadSingleFrame(stream, out pktType, out senderId, out payload))
                    {
                        failure = $"TCP 已连上 {host}:{port}，但对方没有回应探测包（端口被别的程序占用，或对方版本不是 LAN 版）";
                        return false;
                    }

                    if (pktType != PacketType.LobbyProbeAck)
                    {
                        failure = $"对方端口有响应，但不是本 mod 的房主（返回包类型={pktType}）";
                        return false;
                    }

                    using (var ms = new MemoryStream(payload, false))
                    using (var reader = new BinaryReader(ms))
                    {
                        result.IsOnline = true;
                        result.PlayerCount = reader.ReadByte();
                        result.MaxPlayers = reader.ReadByte();
                        result.RoomPhase = reader.ReadByte();
                        result.HostName = reader.ReadString();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                failure = "探测异常: " + ex.Message;
                return false;
            }
            finally
            {
                try { tcp?.Close(); } catch { }
            }
        }

        private static bool TryReadSingleFrame(NetworkStream stream, out PacketType pktType, out byte senderId, out byte[] payload)
        {
            pktType = 0;
            senderId = 0;
            payload = null;
            try
            {
                byte[] header = new byte[8];
                int read = 0;
                while (read < 8)
                {
                    int n = stream.Read(header, read, 8 - read);
                    if (n <= 0) return false;
                    read += n;
                }
                if (header[0] != 0x54 || header[1] != 0x42) return false;
                pktType = (PacketType)header[2];
                senderId = header[3];
                int payloadLen = BitConverter.ToInt32(header, 4);
                if (payloadLen < 0 || payloadLen > 4 * 1024 * 1024) return false;
                payload = new byte[payloadLen];
                int offset = 0;
                while (offset < payloadLen)
                {
                    int n = stream.Read(payload, offset, payloadLen - offset);
                    if (n <= 0) return false;
                    offset += n;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ReadAvailableFrames(NetworkStream stream, byte defaultSenderId)
        {
            try
            {
                while (stream.DataAvailable)
                {
                    byte[] header = new byte[8];
                    int read = 0;
                    while (read < 8)
                    {
                        int n = stream.Read(header, read, 8 - read);
                        if (n <= 0) return false;
                        read += n;
                    }

                    if (header[0] != 0x54 || header[1] != 0x42)
                    {
                        ModLogger.Warn("[LocalTcpLoopbackTransport] Invalid frame magic!");
                        return false;
                    }

                    var pktType = (PacketType)header[2];
                    byte senderId = header[3] != 0 ? header[3] : defaultSenderId;
                    int payloadLen = BitConverter.ToInt32(header, 4);

                    byte[] payload = new byte[payloadLen];
                    int offset = 0;
                    while (offset < payloadLen)
                    {
                        int n = stream.Read(payload, offset, payloadLen - offset);
                        if (n <= 0) return false;
                        offset += n;
                    }

                    using (var ms = new MemoryStream(payload, false))
                    using (var reader = new BinaryReader(ms))
                    {
                        HandleInternalOrDispatch(senderId, pktType, reader);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void HandleInternalOrDispatch(byte senderId, PacketType pktType, BinaryReader reader)
        {
            if (IsHost && pktType == PacketType.HandshakeRequest)
            {
                string clientName = reader.ReadString();
                string version = reader.ReadString();
                bool versionMatched = string.Equals(version, ModInfo.Version, StringComparison.Ordinal);
                if (!versionMatched)
                    ModLogger.Warn($"[LocalTcpLoopbackTransport] Version mismatch! Host={ModInfo.Version}, Client({clientName})={version}");
                else
                    ModLogger.Info($"[LocalTcpLoopbackTransport] Handshake from {clientName} (ver={version}).");
                var peer = new PeerInfo { PlayerId = senderId, PlayerName = $"{clientName}#{senderId}", IsReady = false };
                _peers.RemoveAll(p => p.PlayerId == senderId);
                _peers.Add(peer);

                SendToPeer(senderId, SendChannel.ReliableCommand, PacketType.HandshakeAccept, w =>
                {
                    w.Write(senderId);
                    w.Write((byte)_peers.Count);
                    foreach (var p in _peers)
                    {
                        w.Write(p.PlayerId);
                        w.Write(p.PlayerName);
                        w.Write(p.IsReady);
                    }
                    w.Write(ModInfo.Version);
                });

                OnPeerConnected?.Invoke(peer);
                if (!versionMatched)
                    OnVersionMismatch?.Invoke(clientName, ModInfo.Version, version);
                return;
            }

            if (!IsHost && pktType == PacketType.HandshakeAccept)
            {
                LocalPlayerId = reader.ReadByte();
                byte count = reader.ReadByte();
                _peers.Clear();
                for (int i = 0; i < count; i++)
                {
                    _peers.Add(new PeerInfo
                    {
                        PlayerId = reader.ReadByte(),
                        PlayerName = reader.ReadString(),
                        IsReady = reader.ReadBoolean()
                    });
                }
                string hostVersion = reader.BaseStream.Position < reader.BaseStream.Length
                    ? reader.ReadString()
                    : "<=0.1.2";
                ModLogger.Info($"[LocalTcpLoopbackTransport] HandshakeAccepted! LocalPlayerId={LocalPlayerId}, Peers={_peers.Count}, HostVer={hostVersion}");
                OnPeerConnected?.Invoke(_peers.Find(p => p.PlayerId == LocalPlayerId));
                if (!string.Equals(hostVersion, ModInfo.Version, StringComparison.Ordinal))
                {
                    string hostName = _peers.Find(p => p.PlayerId == 1)?.PlayerName ?? "Host";
                    ModLogger.Warn($"[LocalTcpLoopbackTransport] Version mismatch! Client={ModInfo.Version}, Host({hostName})={hostVersion}");
                    OnVersionMismatch?.Invoke(hostName, ModInfo.Version, hostVersion);
                }
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
            }
        }

        internal static byte[] BuildFrame(byte senderId, PacketType type, Action<BinaryWriter> writePayload)
        {
            using (var payloadMs = new MemoryStream())
            using (var payloadWriter = new BinaryWriter(payloadMs))
            {
                writePayload?.Invoke(payloadWriter);
                payloadWriter.Flush();
                byte[] payloadBytes = payloadMs.ToArray();

                using (var frameMs = new MemoryStream(8 + payloadBytes.Length))
                using (var frameWriter = new BinaryWriter(frameMs))
                {
                    frameWriter.Write((byte)0x54); // 'T'
                    frameWriter.Write((byte)0x42); // 'B'
                    frameWriter.Write((byte)type);
                    frameWriter.Write(senderId);
                    frameWriter.Write(payloadBytes.Length);
                    frameWriter.Write(payloadBytes);
                    frameWriter.Flush();
                    return frameMs.ToArray();
                }
            }
        }

        private static void TryWriteFrame(NetworkStream stream, byte[] frame)
        {
            try
            {
                stream.Write(frame, 0, frame.Length);
                stream.Flush();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            IsActive = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;

            foreach (var p in _pendingAccepts)
            {
                try { p.Stream?.Close(); p.Client?.Close(); } catch { }
            }
            _pendingAccepts.Clear();

            foreach (var c in _clients)
            {
                try { c.Stream?.Close(); c.Client?.Close(); } catch { }
            }
            _clients.Clear();

            try { _streamToHost?.Close(); _clientToHost?.Close(); } catch { }
            _streamToHost = null;
            _clientToHost = null;
        }
    }
}
