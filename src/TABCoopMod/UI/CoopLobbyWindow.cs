using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using DXVision;
using DXVision.GUI;
using Steamworks;
using TABCoopMod.Core;
using TABCoopMod.Networking;
using TABCoopMod.Sync;

namespace TABCoopMod.UI
{
    /// <summary>
    /// Native ZXWindow-based RTS Waiting Room UI for Archon Mode Co-op.
    /// Supports Entry view (Create Steam/Loopback room, auto-probe & join local 127.0.0.1:27099)
    /// and Room view (3-slot Archon roster with player colors & Ready state, Steam friend invite,
    /// Host single-player menu reuse for Load/New Survival/Custom Map, and Client Ready toggle).
    /// </summary>
    public static class CoopLobbyWindow
    {
        private sealed class PlayerSlotRow
        {
            public DXTextObject Background;
            public DXTextObject Swatch;
            public DXTextObject NameLabel;
            public DXTextObject RoleBadge;
        }

        private sealed class CompactLobbyButton : DXButton
        {
            public DXTextObject Label;
            public Color BaseBg;
            public Color HoverBg;
            public Color BaseBorder;
            public Color HoverBorder;
            public Color TextColor;
            private bool _wasIluminated;

            protected override void OnSetStyle()
            {
                base.OnSetStyle();
                if (Label == null) return;
                bool hover = Enabled && Iluminated;
                if (hover != _wasIluminated)
                {
                    _wasIluminated = hover;
                    SetDynamicText(Label, Label.Text, TextColor, hover ? HoverBorder : BaseBorder, hover ? HoverBg : BaseBg);
                }
            }
        }

        private sealed class ChaosPresetDef
        {
            public string Name;
            public int LatencyMs;
            public int JitterMs;
            public double LossRate;
        }

        private static readonly ChaosPresetDef[] ChaosPresets =
        {
            new ChaosPresetDef { Name = "轻微", LatencyMs = 40, JitterMs = 15, LossRate = 0.01 },
            new ChaosPresetDef { Name = "常规", LatencyMs = 80, JitterMs = 20, LossRate = 0.02 },
            new ChaosPresetDef { Name = "较差", LatencyMs = 140, JitterMs = 40, LossRate = 0.05 },
            new ChaosPresetDef { Name = "恶劣", LatencyMs = 220, JitterMs = 80, LossRate = 0.10 }
        };

        private static readonly Color[] SlotColors =
        {
            Color.DodgerBlue,
            Color.SpringGreen,
            Color.Orange
        };

        private static readonly Color PanelBgColor = Color.FromArgb(205, 14, 19, 24);
        private static readonly Color SlotBgActiveColor = Color.FromArgb(220, 22, 30, 38);
        private static readonly Color SlotBgEmptyColor = Color.FromArgb(150, 14, 18, 22);
        private static readonly Color BorderGoldColor = Color.FromArgb(180, 168, 134, 68);
        private static readonly Color BorderDimColor = Color.FromArgb(130, 72, 86, 98);

        private static DXGUIObject _window;
        private static bool _isClosing;
        private static bool _needsRefresh;
        private static bool _subscribed;
        private static double _lastLobbyStatusRefreshTicks = -100000.0;

        // Reopen tracking when Host closes waiting room temporarily to use singleplayer menus,
        // or when players return to Main Menu from a match.
        private static bool _reopenAfterSingleplayerMenu;
        private static double _reopenCooldownUntilTicks;
        private static double _noModalSinceTicks = -1.0;

        // Non-intrusive local loopback probe state
        private static LocalProbeResult _lastProbe = new LocalProbeResult { IsOnline = false };
        private static volatile bool _probeInFlight;
        private static double _lastProbeSentTicks = -100000.0;
        private static string _entryStatusMessage = string.Empty;

        // Top identity & shared view containers
        private static DXTextObject _identityLabel;
        private static DXObject _entryView;
        private static DXObject _roomView;

        // Entry View widgets
        private static DXButton _btnCreateSteam;
        private static DXButton _btnCreateLoopback;
        private static DXButton _btnCreateLan;
        private static DXTextObject _lanAddressText;
        private static DXTextObject _probeCardText;
        private static DXButton _btnJoinLoopback;
        private static DXButton _btnJoinLan;
        private static DXButton _btnRefreshProbe;
        private static DXTextObject _entryMessageText;

        // Room View widgets
        private static DXTextObject _rosterHeaderText;
        private static readonly PlayerSlotRow[] _slots = new PlayerSlotRow[3];
        private static DXTextObject _roomStatusBanner;
        private static DXButton _btnInviteSteam;
        private static DXButton _btnHostLoadSave;
        private static DXButton _btnHostNewSurvival;
        private static DXButton _btnHostCustomMap;
        private static DXButton _btnBalancePreset;
        private static DXButton _btnDisbandRoom;
        private static DXButton _btnClientReady;
        private static DXTextObject _clientHintText;
        private static DXButton _btnLeaveRoom;

        // Local loopback network chaos simulator panel (inside Room View)
        private static DXObject _chaosPanel;
        private static DXTextObject _chaosPanelBg;
        private static DXTextObject _chaosTitleText;
        private static CompactLobbyButton _btnChaosToggle;
        private static CompactLobbyButton _btnChaosPreset;
        private static DXTextObject _chaosLatencyText;
        private static DXTextObject _chaosJitterText;
        private static DXTextObject _chaosLossText;
        private static DXTextObject _chaosClientNoteText;
        private static readonly List<CompactLobbyButton> _chaosAdjustButtons = new List<CompactLobbyButton>();

        public static bool IsOpen => _window != null && !_isClosing;

        /// <summary>Target used by the "join_lan_direct" test action (automated cross-machine verification).</summary>
        public static string TestLanTarget { get; set; } = string.Empty;

        /// <summary>Executes an equivalent button action for automated UI verification via ControlServer.</summary>
        public static bool InvokeTestAction(string action)
        {
            switch ((action ?? string.Empty).ToLowerInvariant())
            {
                case "host_loopback":
                    CoopSessionManager.StartHostSession(useLoopback: true);
                    RefreshUI();
                    return true;
                case "host_lan":
                    CoopSessionManager.StartHostSessionLan();
                    RefreshUI();
                    return true;
                case "join_lan":
                    JoinLanRoomClicked();
                    return true;
                case "join_lan_direct":
                    // Test hook: connect without the engine input dialog (ControlServer passes
                    // the target through _pendingLanTarget by setting it first).
                    if (!string.IsNullOrEmpty(TestLanTarget))
                    {
                        CoopSessionManager.JoinSessionLan(TestLanTarget.Trim());
                        RefreshUI();
                        return true;
                    }
                    return false;
                case "join_loopback":
                    JoinLocalRoomClicked();
                    return true;
                case "toggle_ready":
                    ClientReadyClicked();
                    return true;
                case "leave":
                    LeaveOrDisbandClicked();
                    return true;
                case "open_load_save":
                    OpenHostSingleplayerMenu("房主正在选择存档...", GameBridge.OpenMainMenuLoadSave, rollSeed: false);
                    return true;
                case "open_new_survival":
                    OpenHostSingleplayerMenu("房主正在配置新生存地图...", GameBridge.OpenMainMenuNewSurvival, rollSeed: true);
                    return true;
                case "open_custom_map":
                    OpenHostSingleplayerMenu("房主正在选择自定义地图...", GameBridge.OpenMainMenuCustomMap, rollSeed: true);
                    return true;
                case "toggle_balance_preset":
                    CoopSessionManager.ToggleBalancePreset();
                    RefreshUI();
                    return true;
                case "toggle_chaos":
                    CoopSessionManager.ToggleLocalChaosEnabled();
                    RefreshUI();
                    return true;
                case "cycle_chaos_preset":
                    CycleChaosPresetClicked();
                    return true;
                case "probe_now":
                    TriggerLocalProbeIfDue(force: true);
                    RefreshUI();
                    return true;
                default:
                    return false;
            }
        }

        public static void ShowLobbyWindow()
        {
            try
            {
                EnsureSubscribed();

                if (_window != null && !_isClosing && GameBridge.IsAnyModalOpen())
                {
                    RefreshUI();
                    return;
                }

                _window = null;
                _isClosing = false;
                _reopenAfterSingleplayerMenu = false;
                _noModalSinceTicks = -1.0;
                _entryStatusMessage = string.Empty;

                var win = GameBridge.CreateZXWindow($"多人合作等候大厅 {ModInfo.Tag} — 执政官模式", bigWindow: true);
                if (win == null)
                {
                    ModLogger.Error("[CoopLobbyWindow] CreateZXWindow returned null.");
                    return;
                }

                BuildWindowContents(win);
                _window = win;

                var closeBtn = GameBridge.AddZXWindowBottomButton(win, 2, "关闭窗口", DXKeys.Escape, -120);
                if (closeBtn != null)
                {
                    closeBtn.Activated += _ => CloseLobbyModalOnly(null);
                }

                RefreshUI();
                TriggerLocalProbeIfDue(force: true);

                GameBridge.ShowZXWindow(win, null, animType: 0);
                GameBridge.ClearWaitCursor();
                ModLogger.Info($"[CoopLobbyWindow] Opened RTS Waiting Room (Active={CoopSessionManager.IsSessionActive}, Phase={CoopSessionManager.RoomPhase}).");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[CoopLobbyWindow] Failed to show waiting room", ex);
            }
        }

        /// <summary>Called on Main Menu completion after boot or ReturnToMainMenu.</summary>
        public static void OnMainMenuLoadComplete()
        {
            if (CoopSessionManager.ShouldOpenLobbyOnMainMenu || CoopSessionManager.IsSessionActive)
            {
                bool shouldOpen = CoopSessionManager.ShouldOpenLobbyOnMainMenu;
                CoopSessionManager.ShouldOpenLobbyOnMainMenu = false;
                if (shouldOpen && !GameBridge.IsAnyModalOpen() && !GameBridge.IsGameLoading())
                {
                    ShowLobbyWindow();
                }
            }
        }

        /// <summary>Called from ReturnToMainMenuPrefix so no dangling modal reference remains.</summary>
        public static void ForceHideWithoutStopping()
        {
            _reopenAfterSingleplayerMenu = false;
            _noModalSinceTicks = -1.0;
            if (_window != null && !_isClosing)
            {
                _isClosing = true;
                var w = _window;
                _window = null;
                GameBridge.CloseZXWindow(w, () => { _isClosing = false; });
            }
        }

        /// <summary>Called globally once per frame from CoopSessionManager.OnFrameTick.</summary>
        public static void Tick()
        {
            try
            {
                if (_window != null && !_isClosing)
                {
                    if (!GameBridge.IsGameLoading() && DXGame.Current != null && DXGame.Current.CursorLocked)
                    {
                        GameBridge.ClearWaitCursor();
                    }
                    if (!CoopSessionManager.IsSessionActive)
                    {
                        TriggerLocalProbeIfDue(force: false);
                    }
                    else
                    {
                        double nowLobby = DXHelper_Environment.Ticks;
                        if (nowLobby - _lastLobbyStatusRefreshTicks >= 700.0 || nowLobby < _lastLobbyStatusRefreshTicks)
                        {
                            _lastLobbyStatusRefreshTicks = nowLobby;
                            _needsRefresh = true;
                        }
                    }
                    if (_needsRefresh)
                    {
                        _needsRefresh = false;
                        RefreshUI();
                    }
                    return;
                }

                if (_reopenAfterSingleplayerMenu)
                {
                    if (GameBridge.IsGameLoading() || GameBridge.CurrentLevelState != null)
                    {
                        _reopenAfterSingleplayerMenu = false;
                        _noModalSinceTicks = -1.0;
                        return;
                    }

                    double now = DXHelper_Environment.Ticks;
                    if (now < _reopenCooldownUntilTicks) return;

                    if (GameBridge.IsAnyModalOpen() || !GameBridge.IsOnMainMenu())
                    {
                        _noModalSinceTicks = -1.0;
                        return;
                    }

                    if (_noModalSinceTicks < 0.0)
                    {
                        _noModalSinceTicks = now;
                        return;
                    }

                    // Wait 950ms with no active modal (filters transition fade gaps between nested menus like SaveName InputBox -> New Survival Config).
                    if (now - _noModalSinceTicks >= 950.0)
                    {
                        _reopenAfterSingleplayerMenu = false;
                        _noModalSinceTicks = -1.0;
                        if (CoopSessionManager.IsSessionActive && CoopSessionManager.Transport.IsHost)
                        {
                            CoopSessionManager.SetHostRoomPhase(LobbyRoomPhase.InLobby, string.Empty);
                        }
                        ShowLobbyWindow();
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[CoopLobbyWindow] Tick warning: {ex.Message}");
            }
        }

        public static void HandleSteamInviteAccepted(string lobbyId)
        {
            if (string.IsNullOrEmpty(lobbyId)) return;
            try
            {
                // Consume the pending invite ID immediately so CoopSessionManager.TryHandlePendingSteamInvite()
                // does not join the exact same lobby a second time on the next frame.
                SteamP2PTransport.ConsumePendingInviteLobbyId();
                ModLogger.Info($"[CoopLobbyWindow] Handling Steam Invite for Lobby {lobbyId}...");
                if (GameBridge.CurrentLevelState != null)
                {
                    CoopSessionManager.StopSession();
                    CoopSessionManager.JoinSession(false, lobbyId);
                    CoopSessionManager.ShouldOpenLobbyOnMainMenu = true;
                    GameBridge.ReturnToMainMenu();
                    return;
                }

                CoopSessionManager.JoinSession(false, lobbyId);
                CoopSessionManager.ShouldOpenLobbyOnMainMenu = true;
                if (GameBridge.IsOnMainMenu())
                {
                    ShowLobbyWindow();
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CoopLobbyWindow] HandleSteamInviteAccepted({lobbyId}) failed", ex);
            }
        }

        private static void EnsureSubscribed()
        {
            if (_subscribed) return;
            _subscribed = true;
            CoopSessionManager.LobbyStateChanged += () => { _needsRefresh = true; };
        }

        private static void CloseLobbyModalOnly(Action onClosed)
        {
            if (_window == null)
            {
                onClosed?.Invoke();
                return;
            }
            if (_isClosing) return;

            _isClosing = true;
            var w = _window;
            _window = null;
            GameBridge.CloseZXWindow(w, () =>
            {
                _isClosing = false;
                onClosed?.Invoke();
            });
        }

        private static void BuildWindowContents(DXGUIObject win)
        {
            var content = GameBridge.GetZXWindowContent(win);
            if (content == null) return;

            float u = DXScene.Unit > 0f ? DXScene.Unit : 1f;
            float cw = content.Width > 50f ? content.Width : 1020f * u;
            float ch = content.Height > 50f ? content.Height : 540f * u;
            ModLogger.Info($"[CoopLobbyWindow] Layout OContent size: {cw:F1}x{ch:F1} (Unit={u:F3})");

            // All children inside OContent use parent-relative coordinates (never AreaInPixels!).
            _identityLabel = CreateTextBox(0f, 0f, cw, 28f * u, 11.5f * u, FontStyle.Bold, Color.Khaki,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            content.AddObject(_identityLabel);

            var divider = CreateSolidBox(0f, 32f * u, cw, Math.Max(1f, 2f * u), BorderGoldColor, Color.Transparent, 0f);
            content.AddObject(divider);

            float bodyY = 44f * u;
            float bodyH = ch - bodyY;
            float gap = 28f * u;
            float leftW = cw * 0.54f;
            float rightX = leftW + gap;
            float rightW = cw - rightX;

            _entryView = new DXObject
            {
                X = 0f,
                Y = bodyY,
                Width = cw,
                Height = bodyH
            };
            content.AddObject(_entryView);

            _roomView = new DXObject
            {
                X = 0f,
                Y = bodyY,
                Width = cw,
                Height = bodyH
            };
            content.AddObject(_roomView);

            BuildEntryView(leftW, rightX, rightW, bodyH, u);
            BuildRoomView(leftW, rightX, rightW, bodyH, u);
        }

        private static void BuildEntryView(float leftW, float rightX, float rightW, float bodyH, float u)
        {
            var leftTitle = CreateTextBox(0f, 0f, leftW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            leftTitle.Text = "创建合作房间（房主）";
            _entryView.AddObject(leftTitle);

            float padLeft = 10f * u;
            _btnCreateSteam = CreateNativeButton("创建 Steam 房间", padLeft, 40f * u, 0, () =>
            {
                _entryStatusMessage = string.Empty;
                try
                {
                    CoopSessionManager.StartHostSession(useLoopback: false);
                    RefreshUI();
                }
                catch (Exception ex)
                {
                    _entryStatusMessage = $"创建 Steam 房间失败: {ex.Message}";
                    CoopSessionManager.StopSession();
                    RefreshUI();
                }
            });
            _entryView.AddObject(_btnCreateSteam);

            var steamDesc = CreateTextBox(padLeft, 40f * u + _btnCreateSteam.Height + 6f * u, leftW - padLeft * 2f, 44f * u,
                10.5f * u, FontStyle.Regular, Color.Silver, ContentAlignment.TopLeft, Color.Transparent, Color.Transparent, 0f);
            steamDesc.Text = "创建仅 Steam 好友可见的 2~3 人合作大厅，\n进入等候房间后可通过 Shift+Tab 邀请好友。";
            _entryView.AddObject(steamDesc);

            float secondBtnY = steamDesc.Y + steamDesc.Height + 18f * u;
            _btnCreateLoopback = CreateNativeButton("创建本地测试房间", padLeft, secondBtnY, 0, () =>
            {
                _entryStatusMessage = string.Empty;
                try
                {
                    CoopSessionManager.StartHostSession(useLoopback: true);
                    RefreshUI();
                }
                catch (Exception ex)
                {
                    _entryStatusMessage = $"端口 27099 启动失败（可能已有房主在运行）: {ex.Message}";
                    CoopSessionManager.StopSession();
                    RefreshUI();
                }
            });
            _entryView.AddObject(_btnCreateLoopback);

            var localDesc = CreateTextBox(padLeft, secondBtnY + _btnCreateLoopback.Height + 6f * u, leftW - padLeft * 2f, 44f * u,
                10.5f * u, FontStyle.Regular, Color.Silver, ContentAlignment.TopLeft, Color.Transparent, Color.Transparent, 0f);
            localDesc.Text = "在 127.0.0.1:27099 开放本地房间，\n支持同机双开及延迟/抖动/丢包网络模拟。";
            _entryView.AddObject(localDesc);

            // Third option: native LAN room (accepts connections from other machines).
            float thirdBtnY = localDesc.Y + localDesc.Height + 18f * u;
            _btnCreateLan = CreateNativeButton("创建局域网房间", padLeft, thirdBtnY, 20, () =>
            {
                _entryStatusMessage = string.Empty;
                try
                {
                    CoopSessionManager.StartHostSessionLan();
                    RefreshUI();
                }
                catch (Exception ex)
                {
                    _entryStatusMessage = $"端口 27099 启动失败（可能已有房主在运行）: {ex.Message}";
                    CoopSessionManager.StopSession();
                    RefreshUI();
                }
            });
            _entryView.AddObject(_btnCreateLan);

            _lanAddressText = CreateTextBox(padLeft, thirdBtnY + _btnCreateLan.Height + 6f * u, leftW - padLeft * 2f, 48f * u,
                10.5f * u, FontStyle.Regular, Color.LightSkyBlue, ContentAlignment.TopLeft,
                PanelBgColor, BorderDimColor, 1f);
            _lanAddressText.Text = BuildLanAddressHint();
            _entryView.AddObject(_lanAddressText);

            // Right column: Probe & Join
            var rightTitle = CreateTextBox(rightX, 0f, rightW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            rightTitle.Text = "加入已有房间（客机）";
            _entryView.AddObject(rightTitle);

            _probeCardText = CreateTextBox(rightX, 40f * u, rightW, 96f * u, 10.5f * u, FontStyle.Regular, Color.WhiteSmoke,
                ContentAlignment.MiddleLeft, PanelBgColor, BorderDimColor, 1.5f);
            _entryView.AddObject(_probeCardText);

            float joinBtnY = _probeCardText.Y + _probeCardText.Height + 14f * u;
            _btnJoinLoopback = CreateNativeButton("加入本地房间", rightX, joinBtnY, -30, JoinLocalRoomClicked);
            _entryView.AddObject(_btnJoinLoopback);

            float lanBtnY = joinBtnY + _btnJoinLoopback.Height + 10f * u;
            _btnJoinLan = CreateNativeButton("加入局域网房间…", rightX, lanBtnY, -10, JoinLanRoomClicked);
            _entryView.AddObject(_btnJoinLan);

            float refreshBtnY = lanBtnY + _btnJoinLan.Height + 10f * u;
            _btnRefreshProbe = CreateNativeButton("刷新本地探测", rightX, refreshBtnY, 0, () =>
            {
                _entryStatusMessage = string.Empty;
                TriggerLocalProbeIfDue(force: true);
                RefreshUI();
            }, smallFont: true);
            _entryView.AddObject(_btnRefreshProbe);

            float tipY = refreshBtnY + _btnRefreshProbe.Height + 14f * u;
            var joinTip = CreateTextBox(rightX, tipY, rightW, 74f * u, 10f * u, FontStyle.Regular, Color.LightSteelBlue,
                ContentAlignment.MiddleLeft, PanelBgColor, BorderDimColor, 1f);
            joinTip.Text = "  Steam 好友联机：\n  直接接受好友发送的 Steam 邀请，\n  游戏将自动连接并进入等候房间。";
            _entryView.AddObject(joinTip);

            _entryMessageText = CreateTextBox(0f, bodyH - 32f * u, rightX + rightW, 28f * u,
                10.5f * u, FontStyle.Bold, Color.OrangeRed, ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _entryView.AddObject(_entryMessageText);
        }

        private static void BuildRoomView(float leftW, float rightX, float rightW, float bodyH, float u)
        {
            _rosterHeaderText = CreateTextBox(0f, 0f, leftW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _roomView.AddObject(_rosterHeaderText);

            float slotY = 34f * u;
            float slotH = 50f * u;
            float slotGap = 8f * u;

            for (int i = 0; i < 3; i++)
            {
                float y = slotY + i * (slotH + slotGap);
                var bg = CreateSolidBox(0f, y, leftW, slotH, SlotBgEmptyColor, BorderDimColor, 1.5f);
                _roomView.AddObject(bg);

                // Child coordinates are relative to bg so swatches/labels render in front of slot card bg.
                var swatch = CreateSolidBox(2f * u, 2f * u, 8f * u, slotH - 4f * u, SlotColors[i], Color.Transparent, 0f);
                bg.AddObject(swatch);

                var nameLbl = CreateTextBox(18f * u, 0f, leftW - 145f * u, slotH, 11.5f * u, FontStyle.Bold, Color.WhiteSmoke,
                    ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
                bg.AddObject(nameLbl);

                var badgeLbl = CreateTextBox(leftW - 125f * u, 0f, 115f * u, slotH, 10.5f * u, FontStyle.Bold, Color.Gray,
                    ContentAlignment.MiddleRight, Color.Transparent, Color.Transparent, 0f);
                bg.AddObject(badgeLbl);

                _slots[i] = new PlayerSlotRow
                {
                    Background = bg,
                    Swatch = swatch,
                    NameLabel = nameLbl,
                    RoleBadge = badgeLbl
                };
            }

            float bannerY = slotY + 3f * slotH + 2f * slotGap + 8f * u;
            float bannerH = 52f * u;
            _roomStatusBanner = CreateTextBox(0f, bannerY, leftW, bannerH,
                10.5f * u, FontStyle.Bold, Color.Gold, ContentAlignment.MiddleCenter, PanelBgColor, BorderGoldColor, 1.5f);
            _roomView.AddObject(_roomStatusBanner);

            BuildLocalChaosPanel(leftW, bannerY + bannerH + 8f * u, bodyH, u);

            // Right column: Host & Client actions using native ZXButtonSteam size
            var rightHeader = CreateTextBox(rightX, 0f, rightW, 28f * u, 13.5f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            rightHeader.Text = "房间操作";
            _roomView.AddObject(rightHeader);

            float ry = 36f * u;
            float gapY = 5f * u;

            _btnInviteSteam = CreateNativeButton("邀请 Steam 好友", rightX, ry, 0, InviteSteamClicked);
            _roomView.AddObject(_btnInviteSteam);

            ry += _btnInviteSteam.Height + gapY;
            _btnHostLoadSave = CreateNativeButton("从存档加载...", rightX, ry, -30, () =>
            {
                OpenHostSingleplayerMenu("房主正在选择存档...", GameBridge.OpenMainMenuLoadSave, rollSeed: false);
            });
            _roomView.AddObject(_btnHostLoadSave);

            ry += _btnHostLoadSave.Height + gapY;
            _btnHostNewSurvival = CreateNativeButton("新建生存游戏...", rightX, ry, -30, () =>
            {
                OpenHostSingleplayerMenu("房主正在配置新生存地图...", GameBridge.OpenMainMenuNewSurvival, rollSeed: true);
            });
            _roomView.AddObject(_btnHostNewSurvival);

            ry += _btnHostNewSurvival.Height + gapY;
            _btnHostCustomMap = CreateNativeButton("自定义地图...", rightX, ry, -30, () =>
            {
                OpenHostSingleplayerMenu("房主正在选择自定义地图...", GameBridge.OpenMainMenuCustomMap, rollSeed: true);
            });
            _roomView.AddObject(_btnHostCustomMap);

            ry += _btnHostCustomMap.Height + gapY;
            _btnBalancePreset = CreateNativeButton("新局预设: 联机均衡", rightX, ry, -30, () =>
            {
                CoopSessionManager.ToggleBalancePreset();
                RefreshUI();
            }, smallFont: true);
            _roomView.AddObject(_btnBalancePreset);

            float leaveY = bodyH - _btnInviteSteam.Height - 4f * u;
            _btnDisbandRoom = CreateNativeButton("解散房间", rightX, leaveY, -120, LeaveOrDisbandClicked);
            _roomView.AddObject(_btnDisbandRoom);

            // Client controls share the right column when !IsHost
            _btnClientReady = CreateNativeButton("进入准备状态", rightX, 40f * u, -30, ClientReadyClicked);
            _roomView.AddObject(_btnClientReady);

            float hintY = 40f * u + _btnClientReady.Height + 16f * u;
            _clientHintText = CreateTextBox(rightX, hintY, rightW, 170f * u, 10.5f * u, FontStyle.Regular, Color.LightGray,
                ContentAlignment.MiddleLeft, PanelBgColor, BorderDimColor, 1f);
            _roomView.AddObject(_clientHintText);

            _btnLeaveRoom = CreateNativeButton("退出房间", rightX, leaveY, -120, LeaveOrDisbandClicked);
            _roomView.AddObject(_btnLeaveRoom);
        }

        private static void BuildLocalChaosPanel(float leftW, float panelY, float bodyH, float u)
        {
            _chaosAdjustButtons.Clear();
            float chaosH = Math.Max(112f * u, bodyH - panelY - 4f * u);
            _chaosPanel = new DXObject
            {
                X = 0f,
                Y = panelY,
                Width = leftW,
                Height = chaosH
            };
            _roomView.AddObject(_chaosPanel);

            _chaosPanelBg = CreateSolidBox(0f, 0f, leftW, chaosH, PanelBgColor, BorderDimColor, 1.5f);
            _chaosPanel.AddObject(_chaosPanelBg);

            _chaosTitleText = CreateTextBox(10f * u, 7f * u, 192f * u, 24f * u, 10f * u, FontStyle.Bold, Color.Gold,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _chaosTitleText.Text = "网络波动模拟 (本地测试)";
            _chaosPanelBg.AddObject(_chaosTitleText);

            _btnChaosToggle = CreateCompactButton("模拟: 已关闭", 206f * u, 7f * u, 90f * u, 24f * u, 9.5f * u,
                Color.FromArgb(220, 32, 38, 46), BorderDimColor, Color.Silver, () =>
                {
                    CoopSessionManager.ToggleLocalChaosEnabled();
                    RefreshUI();
                });
            _chaosPanelBg.AddObject(_btnChaosToggle);

            float presetX = 301f * u;
            _btnChaosPreset = CreateCompactButton("预设: 常规", presetX, 7f * u, leftW - presetX - 9f * u, 24f * u, 9.5f * u,
                Color.FromArgb(220, 26, 36, 48), BorderGoldColor, Color.Khaki, CycleChaosPresetClicked);
            _chaosPanelBg.AddObject(_btnChaosPreset);

            float row1Y = 36f * u;
            float row2Y = 63f * u;
            float row3Y = 90f * u;
            float labelW = 190f * u;
            float rowH = 23f * u;

            _chaosLatencyText = CreateTextBox(12f * u, row1Y, labelW, rowH, 9.5f * u, FontStyle.Regular, Color.WhiteSmoke,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _chaosPanelBg.AddObject(_chaosLatencyText);

            _chaosJitterText = CreateTextBox(12f * u, row2Y, labelW, rowH, 9.5f * u, FontStyle.Regular, Color.WhiteSmoke,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _chaosPanelBg.AddObject(_chaosJitterText);

            _chaosLossText = CreateTextBox(12f * u, row3Y, labelW, rowH, 9.5f * u, FontStyle.Regular, Color.WhiteSmoke,
                ContentAlignment.MiddleLeft, Color.Transparent, Color.Transparent, 0f);
            _chaosPanelBg.AddObject(_chaosLossText);

            float btnStartX = 206f * u;
            float btnW = 44f * u;
            float btnGap = 4f * u;
            float btnH = 22f * u;

            AddChaosStepRow(row1Y, btnStartX, btnW, btnGap, btnH, u,
                new[] { ("-50", -50), ("-10", -10), ("+10", 10), ("+50", 50) },
                delta => AdjustChaosLatency(delta));

            AddChaosStepRow(row2Y, btnStartX, btnW, btnGap, btnH, u,
                new[] { ("-20", -20), ("-5", -5), ("+5", 5), ("+20", 20) },
                delta => AdjustChaosJitter(delta));

            AddChaosStepRow(row3Y, btnStartX, btnW, btnGap, btnH, u,
                new[] { ("-5%", -5), ("-1%", -1), ("+1%", 1), ("+5%", 5) },
                delta => AdjustChaosLossPercent(delta));

            _chaosClientNoteText = CreateTextBox(btnStartX, row1Y, leftW - btnStartX - 10f * u, row3Y + rowH - row1Y,
                9.5f * u, FontStyle.Regular, Color.LightSteelBlue, ContentAlignment.MiddleLeft,
                Color.FromArgb(180, 18, 25, 32), BorderDimColor, 1f);
            _chaosPanelBg.AddObject(_chaosClientNoteText);
        }

        private static void AddChaosStepRow(
            float y, float startX, float w, float gap, float h, float u,
            (string Text, int Delta)[] steps, Action<int> onDelta)
        {
            Color bg = Color.FromArgb(230, 28, 36, 44);
            Color border = Color.FromArgb(180, 110, 130, 150);
            for (int i = 0; i < steps.Length; i++)
            {
                int d = steps[i].Delta;
                var b = CreateCompactButton(steps[i].Text, startX + i * (w + gap), y, w, h, 9f * u,
                    bg, border, Color.WhiteSmoke, () => onDelta(d));
                _chaosAdjustButtons.Add(b);
                _chaosPanelBg.AddObject(b);
            }
        }

        private static void RefreshUI()
        {
            if (_window == null || _isClosing) return;

            bool inSession = CoopSessionManager.IsSessionActive;
            SetDynamicText(_identityLabel, BuildIdentityLine(inSession),
                textColor: inSession ? Color.SpringGreen : Color.Khaki);

            _entryView.Visible = !inSession;
            _roomView.Visible = inSession;

            if (!inSession)
            {
                RefreshEntryView();
            }
            else
            {
                RefreshRoomView();
            }
        }

        private static void RefreshEntryView()
        {
            GameBridge.SetZXButtonSteamText(_btnCreateSteam, "创建 Steam 房间");
            GameBridge.SetZXButtonSteamText(_btnCreateLoopback, "创建本地测试房间");
            GameBridge.SetZXButtonSteamText(_btnCreateLan, "创建局域网房间");
            GameBridge.SetZXButtonSteamText(_btnJoinLan, "加入局域网房间…");
            SetDynamicText(_lanAddressText, BuildLanAddressHint());

            var probe = _lastProbe;
            if (probe != null && probe.IsOnline)
            {
                bool joinable = probe.PlayerCount < probe.MaxPlayers;
                string phaseLabel = DescribePhase((LobbyRoomPhase)probe.RoomPhase);
                SetDynamicText(
                    _probeCardText,
                    $"  检测到本地房主 (127.0.0.1:27099)\n" +
                    $"  房主: {probe.HostName}   |   人数: {probe.PlayerCount} / {probe.MaxPlayers}\n" +
                    $"  状态: {phaseLabel}",
                    textColor: joinable ? Color.LightGreen : Color.Orange,
                    borderColor: joinable ? Color.SpringGreen : Color.OrangeRed);
                _btnJoinLoopback.Enabled = joinable;
                GameBridge.SetZXButtonSteamText(_btnJoinLoopback, joinable ? "加入本地房间" : "本地房间已满");
                GameBridge.SetZXButtonSteamHue(_btnJoinLoopback, joinable ? -30 : 0);
            }
            else
            {
                SetDynamicText(
                    _probeCardText,
                    "  未检测到本地房主 (127.0.0.1:27099)\n" +
                    "  请先在另一窗口点击 [创建本地测试房间]，\n" +
                    "  或等待 1.5 秒自动探测。",
                    textColor: Color.Silver,
                    borderColor: BorderDimColor);
                _btnJoinLoopback.Enabled = false;
                GameBridge.SetZXButtonSteamText(_btnJoinLoopback, "暂未检测到主机");
                GameBridge.SetZXButtonSteamHue(_btnJoinLoopback, 0);
            }

            SetDynamicText(_entryMessageText, _entryStatusMessage ?? string.Empty);
        }

        private static void RefreshRoomView()
        {
            bool isHost = CoopSessionManager.Transport.IsHost;
            byte localId = CoopSessionManager.Transport.LocalPlayerId;
            var peers = CoopSessionManager.GetEffectivePeers();

            SetDynamicText(_rosterHeaderText, $"房间成员 ({peers.Count} / 3) — 执政官共控模式");

            for (int i = 0; i < 3; i++)
            {
                byte slotPlayerId = (byte)(i + 1);
                PeerInfo match = null;
                foreach (var p in peers)
                {
                    if (p.PlayerId == slotPlayerId) { match = p; break; }
                }

                var row = _slots[i];
                if (match != null)
                {
                    bool isMe = match.PlayerId == localId;
                    bool isSlotHost = match.PlayerId == 1;
                    SetSolidBoxColors(row.Background, SlotBgActiveColor, isMe ? SlotColors[i] : BorderGoldColor);
                    SetSolidBoxColors(row.Swatch, SlotColors[i], Color.Transparent);

                    SetDynamicText(row.NameLabel, $"P{slotPlayerId} · {match.PlayerName}{(isMe ? " (我)" : "")}",
                        textColor: Color.WhiteSmoke);

                    if (isSlotHost)
                    {
                        SetDynamicText(row.RoleBadge, "★ 房主", textColor: Color.Gold);
                    }
                    else if (match.IsReady)
                    {
                        SetDynamicText(row.RoleBadge, "✓ 已准备", textColor: Color.SpringGreen);
                    }
                    else
                    {
                        SetDynamicText(row.RoleBadge, "○ 未准备", textColor: Color.Orange);
                    }
                }
                else
                {
                    SetSolidBoxColors(row.Background, SlotBgEmptyColor, BorderDimColor);
                    SetSolidBoxColors(row.Swatch, Color.FromArgb(90, SlotColors[i]), Color.Transparent);

                    SetDynamicText(row.NameLabel, $"P{slotPlayerId} · 空位（等待玩家加入）",
                        textColor: Color.DimGray);
                    SetDynamicText(row.RoleBadge, "空位", textColor: Color.DimGray);
                }
            }

            UpdateRoomBanner(isHost, peers.Count);

            _btnInviteSteam.Visible = isHost;
            _btnHostLoadSave.Visible = isHost;
            _btnHostNewSurvival.Visible = isHost;
            _btnHostCustomMap.Visible = isHost;
            _btnBalancePreset.Visible = isHost;
            _btnDisbandRoom.Visible = isHost;

            _btnClientReady.Visible = !isHost;
            _clientHintText.Visible = !isHost;
            _btnLeaveRoom.Visible = !isHost;

            bool isBalanced = CoopSessionManager.BalancePreset == CoopBalancePreset.Balanced;
            string presetDesc = CoopBalanceManager.DescribePreset(CoopSessionManager.BalancePreset);

            if (isHost)
            {
                var unwrapped = CoopSessionManager.UnwrapTransport();
                bool isSteam = unwrapped is SteamP2PTransport;
                bool lobbyValid = unwrapped is SteamP2PTransport st && st.CurrentLobbyId.IsValid();
                _btnInviteSteam.Enabled = isSteam && lobbyValid;
                GameBridge.SetZXButtonSteamText(_btnInviteSteam,
                    (isSteam && !lobbyValid) ? "正在创建大厅..." : "邀请 Steam 好友");

                bool canStart = CoopSessionManager.CanHostStartGame;
                bool hasSaves = GameBridge.HasAnySaveGames();

                _btnHostLoadSave.Enabled = canStart && hasSaves;
                _btnHostNewSurvival.Enabled = canStart;
                _btnHostCustomMap.Enabled = canStart;
                _btnBalancePreset.Enabled = true;

                GameBridge.SetZXButtonSteamHue(_btnHostLoadSave, (canStart && hasSaves) ? -30 : 0);
                GameBridge.SetZXButtonSteamHue(_btnHostNewSurvival, canStart ? -30 : 0);
                GameBridge.SetZXButtonSteamHue(_btnHostCustomMap, canStart ? -30 : 0);
                GameBridge.SetZXButtonSteamText(_btnBalancePreset, isBalanced ? "新局预设: 联机均衡" : "新局预设: 原版纯净");
                GameBridge.SetZXButtonSteamHue(_btnBalancePreset, isBalanced ? -30 : 0);
            }
            else
            {
                var localPeer = CoopSessionManager.LocalPeer;
                bool ready = localPeer != null && localPeer.IsReady;
                bool joined = CoopSessionManager.Transport.LocalPlayerId != 0;
                _btnClientReady.Enabled = joined;
                GameBridge.SetZXButtonSteamText(_btnClientReady,
                    !joined ? "正在连接房主..." : (ready ? "取消准备" : "进入准备状态"));
                GameBridge.SetZXButtonSteamHue(_btnClientReady, ready ? 0 : -30);

                SetDynamicText(_clientHintText, (CoopSessionManager.RoomPhase == LobbyRoomPhase.InGame
                    ? "  房主当前正在对局中，\n  等待房主返回大厅或同步重连。"
                    : "  点击上方按钮切换准备状态。\n  全员准备后房主即可选图或读档，\n  加载完毕后将自动同步并开始。")
                    + $"\n\n  新生存预设: {presetDesc}\n  (读档与自定义地图始终保持原版)");
            }

            RefreshLocalChaosPanel(isHost);
        }

        private static void RefreshLocalChaosPanel(bool isHost)
        {
            if (_chaosPanel == null) return;
            bool isLocal = CoopSessionManager.IsLocalLoopbackSession;
            _chaosPanel.Visible = isLocal;
            if (!isLocal) return;

            bool enabled = CoopSessionManager.LocalChaosEnabled;
            int lat = CoopSessionManager.LocalChaosLatencyMs;
            int jit = CoopSessionManager.LocalChaosJitterMs;
            double loss = CoopSessionManager.LocalChaosLossRate;
            bool lockedByEnv = CoopSessionManager.IsLocalChaosLockedByEnv;
            bool canEdit = isHost && !lockedByEnv;

            SetSolidBoxColors(_chaosPanelBg, PanelBgColor, enabled ? Color.Orange : BorderDimColor);
            SetDynamicText(_chaosTitleText,
                enabled ? "网络波动模拟 (模拟中)" : "网络波动模拟 (本地测试)",
                textColor: enabled ? Color.Orange : Color.Gold);

            int estRtt = lat * 2 + jit;
            SetDynamicText(_chaosLatencyText, $"单向延迟: {lat} ms (RTT~{estRtt}ms)",
                textColor: enabled ? Color.WhiteSmoke : Color.Silver);
            SetDynamicText(_chaosJitterText, $"随机抖动: 0~{jit} ms",
                textColor: enabled ? Color.WhiteSmoke : Color.Silver);
            SetDynamicText(_chaosLossText, $"随机丢包: {loss * 100.0:0.#} %",
                textColor: enabled ? Color.WhiteSmoke : Color.Silver);

            _btnChaosToggle.Visible = canEdit;
            _btnChaosPreset.Visible = canEdit;
            foreach (var b in _chaosAdjustButtons)
            {
                b.Visible = canEdit;
            }

            if (canEdit)
            {
                _chaosClientNoteText.Visible = false;
                if (enabled)
                {
                    SetCompactButtonStyle(_btnChaosToggle, "模拟: 已开启",
                        Color.FromArgb(220, 140, 74, 18), Color.FromArgb(240, 178, 96, 26),
                        Color.Orange, Color.Gold, Color.White);
                }
                else
                {
                    SetCompactButtonStyle(_btnChaosToggle, "模拟: 已关闭",
                        Color.FromArgb(220, 34, 42, 50), Color.FromArgb(235, 50, 62, 74),
                        BorderDimColor, Color.Silver, Color.Silver);
                }

                string presetName = DetectChaosPresetName(lat, jit, loss);
                SetCompactButtonStyle(_btnChaosPreset, $"预设: {presetName}",
                    Color.FromArgb(220, 26, 40, 54), Color.FromArgb(240, 38, 58, 78),
                    BorderGoldColor, Color.Gold, Color.Khaki);
            }
            else
            {
                _chaosClientNoteText.Visible = true;
                string note = lockedByEnv
                    ? "  已由环境变量 TABCOOP_CHAOS\n  锁定参数，界面不可修改。"
                    : (enabled
                        ? "  当前已开启（由房主同步）\n  双向均已应用相同延迟/丢包，\n  顶部状态栏可查看实时 RTT。"
                        : "  当前未开启（由房主同步）\n  房主可在左侧实时开启并调参。");
                SetDynamicText(_chaosClientNoteText, note,
                    textColor: enabled ? Color.Orange : Color.LightSteelBlue,
                    borderColor: enabled ? Color.Orange : BorderDimColor);
            }
        }

        private static string DetectChaosPresetName(int lat, int jit, double loss)
        {
            foreach (var p in ChaosPresets)
            {
                if (p.LatencyMs == lat && p.JitterMs == jit && Math.Abs(p.LossRate - loss) < 0.0005)
                    return p.Name;
            }
            return "自定义";
        }

        private static void CycleChaosPresetClicked()
        {
            if (!CoopSessionManager.IsLocalLoopbackSession || !CoopSessionManager.Transport.IsHost || CoopSessionManager.IsLocalChaosLockedByEnv)
                return;

            int curIdx = -1;
            for (int i = 0; i < ChaosPresets.Length; i++)
            {
                var p = ChaosPresets[i];
                if (p.LatencyMs == CoopSessionManager.LocalChaosLatencyMs
                    && p.JitterMs == CoopSessionManager.LocalChaosJitterMs
                    && Math.Abs(p.LossRate - CoopSessionManager.LocalChaosLossRate) < 0.0005)
                {
                    curIdx = i;
                    break;
                }
            }
            int nextIdx = (curIdx + 1) % ChaosPresets.Length;
            var next = ChaosPresets[nextIdx];
            CoopSessionManager.SetLocalChaosSettings(true, next.LatencyMs, next.JitterMs, next.LossRate);
            RefreshUI();
        }

        private static void AdjustChaosLatency(int deltaMs)
        {
            CoopSessionManager.SetLocalChaosSettings(
                CoopSessionManager.LocalChaosEnabled,
                CoopSessionManager.LocalChaosLatencyMs + deltaMs,
                CoopSessionManager.LocalChaosJitterMs,
                CoopSessionManager.LocalChaosLossRate);
            RefreshUI();
        }

        private static void AdjustChaosJitter(int deltaMs)
        {
            CoopSessionManager.SetLocalChaosSettings(
                CoopSessionManager.LocalChaosEnabled,
                CoopSessionManager.LocalChaosLatencyMs,
                CoopSessionManager.LocalChaosJitterMs + deltaMs,
                CoopSessionManager.LocalChaosLossRate);
            RefreshUI();
        }

        private static void AdjustChaosLossPercent(int deltaPct)
        {
            double nextLoss = CoopSessionManager.LocalChaosLossRate + deltaPct / 100.0;
            CoopSessionManager.SetLocalChaosSettings(
                CoopSessionManager.LocalChaosEnabled,
                CoopSessionManager.LocalChaosLatencyMs,
                CoopSessionManager.LocalChaosJitterMs,
                nextLoss);
            RefreshUI();
        }

        private static void UpdateRoomBanner(bool isHost, int peerCount)
        {
            if (CoopSessionManager.HasVersionMismatch)
            {
                SetDynamicText(_roomStatusBanner,
                    $"{CoopSessionManager.VersionMismatchSummary}\n请双方更新至完全一致的 Mod 版本后再开始游戏",
                    textColor: Color.OrangeRed, borderColor: Color.OrangeRed);
                return;
            }

            if (isHost)
            {
                if (!CoopSessionManager.HasJoinedClient)
                {
                    SetDynamicText(_roomStatusBanner,
                        "等待至少 1 名客机加入房间后，方可开启或读取游戏",
                        textColor: Color.Orange, borderColor: Color.Orange);
                }
                else if (!CoopSessionManager.AreAllClientsReady)
                {
                    SetDynamicText(_roomStatusBanner,
                        $"已有 {peerCount} 名玩家在房间，等待所有客机进入准备状态...",
                        textColor: Color.Gold, borderColor: Color.Goldenrod);
                }
                else
                {
                    string presetLabel = CoopBalanceManager.DescribePreset(CoopSessionManager.BalancePreset);
                    SetDynamicText(_roomStatusBanner,
                        $"全员已准备就绪！请在右侧开启游戏\n新生存预设: {presetLabel}",
                        textColor: Color.SpringGreen, borderColor: Color.SpringGreen);
                }
                return;
            }

            if (CoopSessionManager.Transport.LocalPlayerId == 0)
            {
                SetDynamicText(_roomStatusBanner,
                    "正在与房主建立联机握手...",
                    textColor: Color.Gold, borderColor: Color.Goldenrod);
                return;
            }

            if (CoopSessionManager.RoomPhase == LobbyRoomPhase.HostSelectingMenu)
            {
                SetDynamicText(_roomStatusBanner,
                    string.IsNullOrEmpty(CoopSessionManager.HostActionHint)
                        ? "房主正在选择地图或存档..."
                        : CoopSessionManager.HostActionHint,
                    textColor: Color.Cyan, borderColor: Color.Cyan);
                return;
            }

            if (CoopSessionManager.RoomPhase == LobbyRoomPhase.HostLoadingLevel)
            {
                SetDynamicText(_roomStatusBanner,
                    "房主正在加载游戏，即将自动接收存档快照并同步进入...",
                    textColor: Color.Cyan, borderColor: Color.Cyan);
                return;
            }

            bool meReady = CoopSessionManager.LocalPeer != null && CoopSessionManager.LocalPeer.IsReady;
            SetDynamicText(_roomStatusBanner,
                meReady ? "您已准备就绪，等待房主开始游戏..." : "请点击右侧 [进入准备状态] 以通知房主开局",
                textColor: meReady ? Color.SpringGreen : Color.Orange,
                borderColor: meReady ? Color.SpringGreen : Color.Orange);
        }

        private static string BuildIdentityLine(bool inSession)
        {
            string persona = "本地玩家";
            string steamTag = "未连接 Steam";
            try
            {
                persona = SteamFriends.GetPersonaName() ?? Environment.UserName ?? "Player";
                var sid = SteamUser.GetSteamID();
                if (sid.IsValid()) steamTag = $"SteamID: {sid.m_SteamID}";
            }
            catch { }

            if (!inSession)
            {
                return $"当前玩家: {persona} ({steamTag})   |   状态: 未加入房间";
            }

            string mode = CoopSessionManager.IsLanSession
                ? "局域网"
                : (CoopSessionManager.IsLocalLoopbackSession ? "本地回环" : "Steam P2P");
            var diag = CoopSessionManager.Transport as ITransportDiagnostics;
            string pathText = diag != null ? diag.ActivePathSummary : mode;
            double showRtt = (diag != null && diag.ActiveDirectRttMs > 0.0)
                ? diag.ActiveDirectRttMs
                : CoopSessionManager.CurrentSmoothedRttMs;
            string rttText = showRtt > 0.0 ? $"{showRtt:F0}ms" : "--";
            string role = CoopSessionManager.Transport.IsHost ? "房主 P1" : $"客机 P{CoopSessionManager.Transport.LocalPlayerId}";
            return $"当前玩家: {persona}  |  角色: {role}  |  链路: {pathText}  |  RTT: {rttText}";
        }

        private static void OpenHostSingleplayerMenu(string hostHint, Func<bool> openMenuAction, bool rollSeed)
        {
            if (!CoopSessionManager.CanHostStartGame) return;

            // Ensure any return-to-menu fade-out object (#=zbvwh$ickzalV) is detached before opening
            // singleplayer menus; otherwise ZXGame.#=zXAMaTPI= silently no-ops on Play if still in Scene.
            GameBridge.ClearLoadingFadeOverlay();

            if (rollSeed)
            {
                CoopSessionManager.RollNewGameSeed();
            }

            CoopSessionManager.SetHostRoomPhase(LobbyRoomPhase.HostSelectingMenu, hostHint);
            _reopenAfterSingleplayerMenu = true;
            _reopenCooldownUntilTicks = DXHelper_Environment.Ticks + 600.0;
            _noModalSinceTicks = -1.0;

            CloseLobbyModalOnly(() =>
            {
                GameBridge.ClearLoadingFadeOverlay();
                bool ok = openMenuAction != null && openMenuAction();
                if (!ok)
                {
                    ModLogger.Warn("[CoopLobbyWindow] Singleplayer menu action returned false; restoring waiting room.");
                    _reopenAfterSingleplayerMenu = false;
                    CoopSessionManager.SetHostRoomPhase(LobbyRoomPhase.InLobby, string.Empty);
                    ShowLobbyWindow();
                }
            });
        }

        /// <summary>Builds the "share this address" hint shown under the LAN host button.</summary>
        private static string BuildLanAddressHint()
        {
            var ips = LocalTcpLoopbackTransport.GetLocalIPv4Addresses();
            string addr = ips.Count > 0 ? ips[0] : "<未检测到局域网 IP>";
            if (ips.Count > 1)
                addr += "（其他网卡: " + string.Join(", ", ips.GetRange(1, ips.Count - 1)) + "）";
            return $"局域网房间监听 0.0.0.0:{LocalTcpLoopbackTransport.DefaultPort}\n" +
                   $"把本机 IP 告诉队友: {addr}";
        }

        private static void JoinLanRoomClicked()
        {
            _entryStatusMessage = string.Empty;
            string suggested = string.Empty;
            var localIps = LocalTcpLoopbackTransport.GetLocalIPv4Addresses();
            if (localIps.Count > 0) suggested = localIps[0];
            // Prefill with the local subnet (host part stripped) so a teammate only types the last octet.
            if (!string.IsNullOrEmpty(suggested))
            {
                int dot = suggested.LastIndexOf('.');
                if (dot > 0) suggested = suggested.Substring(0, dot + 1);
            }

            try
            {
                GameBridge.ShowInputBoxMessageBox(
                    "加入局域网房间",
                    $"请输入房主的局域网 IP（默认端口 {LocalTcpLoopbackTransport.DefaultPort}）\n" +
                    "支持 192.168.1.100 或 192.168.1.100:27099 两种写法。",
                    suggested,
                    64,
                    text =>
                    {
                        string target = (text ?? string.Empty).Trim();
                        if (string.IsNullOrEmpty(target))
                        {
                            _entryStatusMessage = "未输入局域网 IP，已取消。";
                            RefreshUI();
                            return;
                        }
                        try
                        {
                            CoopSessionManager.JoinSessionLan(target);
                            _entryStatusMessage = $"正在连接局域网房主 {target} …";
                        }
                        catch (Exception ex)
                        {
                            _entryStatusMessage = $"连接局域网房主失败: {ex.Message}";
                            CoopSessionManager.StopSession();
                        }
                        RefreshUI();
                    },
                    () => { _entryStatusMessage = "已取消加入局域网房间。"; RefreshUI(); },
                    text => !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 7,
                    "AllCharactersExceptNewLines");
            }
            catch (Exception ex)
            {
                _entryStatusMessage = $"无法打开 IP 输入框: {ex.Message}";
                RefreshUI();
            }
        }

        private static void JoinLocalRoomClicked()
        {
            _entryStatusMessage = string.Empty;
            try
            {
                CoopSessionManager.JoinSession(useLoopback: true, "127.0.0.1");
                RefreshUI();
            }
            catch (Exception ex)
            {
                _entryStatusMessage = $"连接本地房主失败: {ex.Message}";
                CoopSessionManager.StopSession();
                RefreshUI();
            }
        }

        private static void InviteSteamClicked()
        {
            if (CoopSessionManager.UnwrapTransport() is SteamP2PTransport steamTrans && steamTrans.CurrentLobbyId.IsValid())
            {
                steamTrans.OpenSteamInviteOverlay();
            }
        }

        private static void ClientReadyClicked()
        {
            var localPeer = CoopSessionManager.LocalPeer;
            bool curReady = localPeer != null && localPeer.IsReady;
            CoopSessionManager.SetLocalReady(!curReady);
            RefreshUI();
        }

        private static void LeaveOrDisbandClicked()
        {
            _reopenAfterSingleplayerMenu = false;
            _entryStatusMessage = string.Empty;
            CoopSessionManager.StopSession();
            TriggerLocalProbeIfDue(force: true);
            RefreshUI();
        }

        private static void TriggerLocalProbeIfDue(bool force)
        {
            if (_probeInFlight) return;
            double now = DXHelper_Environment.Ticks;
            if (!force && now - _lastProbeSentTicks < 1500.0) return;

            _probeInFlight = true;
            _lastProbeSentTicks = now;

            Task.Run(() =>
            {
                try
                {
                    _lastProbe = LocalTcpLoopbackTransport.ProbeLocalHost("127.0.0.1", LocalTcpLoopbackTransport.DefaultPort, 250);
                }
                catch
                {
                    _lastProbe = new LocalProbeResult { IsOnline = false };
                }
                finally
                {
                    _probeInFlight = false;
                    _needsRefresh = true;
                }
            });
        }

        private static string DescribePhase(LobbyRoomPhase phase)
        {
            switch (phase)
            {
                case LobbyRoomPhase.InLobby: return "等候大厅中";
                case LobbyRoomPhase.HostSelectingMenu: return "房主正在选图";
                case LobbyRoomPhase.HostLoadingLevel: return "正在加载同步关卡";
                case LobbyRoomPhase.InGame: return "游戏中（支持重连/中途加入）";
                default: return "就绪";
            }
        }

        private static DXButton CreateNativeButton(string text, float x, float y, int hueRotation, Action onClick, bool smallFont = false)
        {
            var btn = GameBridge.CreateZXButtonSteam(text, DXKeys.None, hueRotation, smallFont);
            if (btn == null)
            {
                btn = new DXButton();
            }
            btn.X = x;
            btn.Y = y;
            btn.Activated += _ =>
            {
                try
                {
                    onClick?.Invoke();
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[CoopLobbyWindow] Button '{text}' action failed", ex);
                }
            };
            return btn;
        }

        private static CompactLobbyButton CreateCompactButton(
            string text, float x, float y, float w, float h, float fontSize,
            Color bgColor, Color borderColor, Color textColor, Action onClick)
        {
            var lbl = CreateTextBox(0f, 0f, w, h, fontSize, FontStyle.Bold, textColor,
                ContentAlignment.MiddleCenter, bgColor, borderColor, 1f);
            lbl.Text = text;

            var btn = new CompactLobbyButton
            {
                X = x,
                Y = y,
                Width = w,
                Height = h,
                Image = null,
                Border = null,
                Label = lbl,
                BaseBg = bgColor,
                HoverBg = Color.FromArgb(240, Math.Min(255, bgColor.R + 24), Math.Min(255, bgColor.G + 24), Math.Min(255, bgColor.B + 24)),
                BaseBorder = borderColor,
                HoverBorder = Color.Gold,
                TextColor = textColor
            };
            btn.AddObject(lbl);
            btn.Activated += _ =>
            {
                try
                {
                    onClick?.Invoke();
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[CoopLobbyWindow] CompactButton '{text}' action failed", ex);
                }
            };
            return btn;
        }

        private static void SetCompactButtonStyle(
            CompactLobbyButton btn, string text,
            Color baseBg, Color hoverBg, Color baseBorder, Color hoverBorder, Color textColor)
        {
            if (btn == null || btn.Label == null) return;
            btn.BaseBg = baseBg;
            btn.HoverBg = hoverBg;
            btn.BaseBorder = baseBorder;
            btn.HoverBorder = hoverBorder;
            btn.TextColor = textColor;
            bool hover = btn.Enabled && btn.Iluminated;
            SetDynamicText(btn.Label, text, textColor, hover ? hoverBorder : baseBorder, hover ? hoverBg : baseBg);
        }

        private static void ResetTextSurface(DXTextObject obj)
        {
            if (obj == null || obj.Image == null) return;
            try { obj.Image.Dispose(); } catch { }
            obj.Image = null;
        }

        private static void SetSolidBoxColors(DXTextObject box, Color bgColor, Color borderColor)
        {
            if (box == null) return;
            if (box.TextRenderer.ColorBackground == bgColor &&
                box.TextRenderer.ColorAreaBorder == borderColor)
                return;

            box.TextRenderer.ColorBackground = bgColor;
            box.TextRenderer.MakeTransparent = bgColor == Color.Transparent;
            box.TextRenderer.ColorAreaBorder = borderColor;

            // Reset GPU image so DXTextObject creates a fresh surface instead of ReLoadInGPU().
            ResetTextSurface(box);
            box.Text = "  ";
            ResetTextSurface(box);
            box.Text = " ";
        }

        private static void SetDynamicText(
            DXTextObject obj,
            string text,
            Color? textColor = null,
            Color? borderColor = null,
            Color? bgColor = null)
        {
            if (obj == null) return;
            string desired = text ?? string.Empty;
            bool colorChanged =
                (textColor.HasValue && obj.TextRenderer.ColorText != textColor.Value) ||
                (borderColor.HasValue && obj.TextRenderer.ColorAreaBorder != borderColor.Value) ||
                (bgColor.HasValue && obj.TextRenderer.ColorBackground != bgColor.Value);
            bool textChanged = !string.Equals(obj.Text, desired, StringComparison.Ordinal);

            if (!colorChanged && !textChanged) return;

            if (textColor.HasValue) obj.TextRenderer.ColorText = textColor.Value;
            if (borderColor.HasValue) obj.TextRenderer.ColorAreaBorder = borderColor.Value;
            if (bgColor.HasValue)
            {
                obj.TextRenderer.ColorBackground = bgColor.Value;
                obj.TextRenderer.MakeTransparent = bgColor.Value == Color.Transparent;
            }

            if (!textChanged)
            {
                ResetTextSurface(obj);
                obj.Text = desired == " " ? "  " : desired + " ";
            }

            ResetTextSurface(obj);
            obj.Text = desired;
        }

        private static DXTextObject CreateSolidBox(
            float x, float y, float w, float h,
            Color bgColor, Color borderColor, float borderWidth)
        {
            // Render a single space with transparent text so DXTextObject creates a properly
            // sized GPU surface (1x1 EmptyBitmap can streak when stretched across pixels).
            var obj = CreateTextBox(x, y, w, h, 8f, FontStyle.Regular, Color.Transparent,
                ContentAlignment.MiddleCenter, bgColor, borderColor, borderWidth);
            obj.Text = " ";
            return obj;
        }

        private static DXTextObject CreateTextBox(
            float x, float y, float w, float h,
            float fontSize,
            FontStyle style,
            Color textColor,
            ContentAlignment align,
            Color bgColor,
            Color borderColor,
            float borderWidth)
        {
            var obj = new DXTextObject
            {
                X = x,
                Y = y,
                Width = w,
                Height = h
            };
            obj.TextRenderer.Font = new Font("Microsoft YaHei", Math.Max(8f, fontSize), style);
            obj.TextRenderer.ColorText = textColor;
            obj.TextRenderer.TextAlign = align;
            obj.TextRenderer.ColorBackground = bgColor;
            obj.TextRenderer.ColorAreaBorder = borderColor;
            obj.TextRenderer.AreaBorderWidth = borderWidth;
            obj.TextRenderer.MakeTransparent = bgColor == Color.Transparent;
            return obj;
        }
    }
}
