using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using DXVision;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Patches;
using TABCoopMod.Sync;
using TABCoopMod.UI;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// In-process JSON control channel for automated testing (MCP server connects over loopback TCP).
    /// Requests are queued from the socket thread and executed on the game's main/render thread from
    /// StartFrame, because issuing a command mutates live game state and is not thread-safe.
    ///
    /// Protocol (newline-delimited JSON, one request/response per line):
    ///   request : {"id":123,"tool":"move_units","args":{"ids":[...],"x":10,"y":20}}
    ///   response: {"id":123,"ok":true,"result":{...}}  or  {"id":123,"ok":false,"error":"..."}
    /// All game commands go through CommandExecutor/NetCommand so they follow the same path as the
    /// multiplayer replay (never touches local CSelectable).
    /// </summary>
    public static class ControlServer
    {
        private const int MaxListEntities = 5000;

        private sealed class PendingRequest
        {
            public string RequestJson;
            public string ResponseJson;
            public ManualResetEventSlim Done;
        }

        private static readonly ConcurrentQueue<PendingRequest> _queue = new ConcurrentQueue<PendingRequest>();
        private static readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };

        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static int _port;

        public static bool IsRunning => _running;
        public static int Port => _port;

        /// <summary>Called every render frame from StartFrame. Starts the listener once and drains requests.</summary>
        public static void Tick()
        {
            int port = TestHarnessConfig.ControlPort;
            if (port <= 0) return;
            if (!_running) Start(port);
            ScreenshotService.Tick();
            Drain();
        }

        private static void Start(int port)
        {
            try
            {
                _port = port;
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TABCoopControlAccept" };
                _acceptThread.Start();
                ModLogger.Info($"[ControlServer] listening on 127.0.0.1:{port}");
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[ControlServer] failed to start on port {port}", ex);
                _running = false;
            }
        }

        private static void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private static void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch { if (!_running) break; else continue; }

                var t = new Thread(() => ClientLoop(client)) { IsBackground = true, Name = "TABCoopControlClient" };
                t.Start();
            }
        }

        private static void ClientLoop(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true })
                {
                    string line;
                    while (_running && (line = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        var pending = new PendingRequest { RequestJson = line, Done = new ManualResetEventSlim(false) };
                        _queue.Enqueue(pending);
                        int waitMs = (line.IndexOf("start_survival_gen", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                      line.IndexOf("capture_panorama", StringComparison.OrdinalIgnoreCase) >= 0)
                            ? 120000
                            : 10000;
                        if (!pending.Done.Wait(waitMs))
                        {
                            writer.WriteLine(ErrorResponse(0, "timeout waiting for game main thread (is the game paused/loading?)"));
                            continue;
                        }
                        writer.WriteLine(pending.ResponseJson ?? ErrorResponse(0, "no response"));
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[ControlServer] client loop ended: {ex.Message}");
            }
        }

        private static void Drain()
        {
            while (_queue.TryDequeue(out var pending))
            {
                Determinism.DeterministicSandboxPatches.FlushPendingAsyncTick(DXGame.Current);
                try { pending.ResponseJson = HandleRequest(pending.RequestJson); }
                catch (Exception ex)
                {
                    ModLogger.Error("[ControlServer] handler failed", ex);
                    pending.ResponseJson = ErrorResponse(0, ex.Message);
                }
                finally { pending.Done.Set(); }
            }
        }

        // ====================================================================
        // Request dispatch (runs on the game main thread)
        // ====================================================================

        private static string HandleRequest(string requestJson)
        {
            long id = 0;
            string tool = null;
            try
            {
                Dictionary<string, object> req = _json.Deserialize<Dictionary<string, object>>(requestJson);
                if (req == null) return ErrorResponse(0, "empty request");

                id = GetLong(req, "id", 0);
                tool = GetStr(req, "tool");
                var args = req.TryGetValue("args", out object a) ? a as Dictionary<string, object> : null;
                if (args == null) args = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                object result = Dispatch(tool, args);
                AttachScreenshot(tool, args, result);
                return OkResponse(id, result);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[ControlServer] tool '{tool}' failed: {ex.Message}");
                return ErrorResponse(id, ex.Message);
            }
        }

        private static object Dispatch(string tool, Dictionary<string, object> args)
        {
            switch ((tool ?? string.Empty).ToLowerInvariant())
            {
                case "ping": return Ping();
                case "get_state":
                case "game_state": return GetState();
                case "list_entities": return ListEntities(args);
                case "move_units": return MoveUnits(args);
                case "place_building": return PlaceBuilding(args);
                case "train_unit": return TrainUnit(args);
                case "cancel_current_order": return CancelCurrent(args);
                case "cancel_queued_order": return CancelQueued(args);
                case "set_paused": return SetPaused(args);
                case "set_game_speed": return SetGameSpeed(args);
                case "request_pause": return RequestPauseTool(args);
                case "list_mayors": return ListMayors(args);
                case "mayor_select": return SelectMayor(args);
                case "issue_command": return IssueRawCommand(args);
                case "screenshot": return TakeScreenshot(args);
                case "get_camera": return GetCamera();
                case "set_camera": return SetCamera(args);
                case "reveal_map": return RevealMap(args);
                case "open_lobby":
                    CoopLobbyWindow.ShowLobbyWindow();
                    return new Dictionary<string, object> { ["opened"] = true };
                case "lobby_state":
                    return GetLobbyState();
                case "lobby_action":
                    return RunLobbyAction(args);
                case "start_survival_gen":
                    return StartSurvivalGen(args);
                case "capture_panorama":
                    return CapturePanoramaTool(args);
                case "map_metrics":
                    return GameBridge.CollectCurrentMapMetrics();
                default: throw new InvalidOperationException($"unknown tool '{tool}'");
            }
        }

        // ====================================================================
        // Tools
        // ====================================================================

        private static object Ping()
        {
            return new Dictionary<string, object>
            {
                ["pong"] = true,
                ["tick"] = CurrentTick(),
                ["port"] = _port
            };
        }

        private static object GetState()
        {
            var game = DXGame.Current;
            object ls = GameBridge.CurrentLevelState;

            int entityCount = 0, playerCount = 0;
            if (game?.Entities != null)
            {
                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    if ((e.ID & 0x8000000000000000UL) != 0) continue;
                    entityCount++;
                    if (GameBridge.IsPlayerEntity(e)) playerCount++;
                }
            }

            object gameMode = GameBridge.GetGameStateProperty("GameMode");
            object inCampaign = GameBridge.GetGameStateProperty("InCampaign");
            object difficulty = GameBridge.GetGameStateProperty("Difficulty");
            object survivalParams = GameBridge.GetGameStateProperty("SurvivalModeParams");
            var curSys = GameBridge.CurrentGameSystem as DXSystem;

            GameBridge.GetLevelStateCommandCenterExtras(out int ccExtraWorkers, out int ccExtraFood);

            return new Dictionary<string, object>
            {
                ["tick"] = game?.IAIterationInt ?? 0,
                ["gameTimeMs"] = game?.GameTimeInt ?? 0,
                ["paused"] = game?.Paused ?? false,
                ["pausedBySession"] = Determinism.LockstepClock.PausedBySession,
                ["inputEnabled"] = game?.InputEnabled ?? false,
                ["focused"] = game?.Focused ?? false,
                ["visible"] = game?.Visible ?? false,
                ["systemEnabled"] = curSys?.Enabled ?? false,
                ["inModal"] = DXVision.GUI.DXModalScreen.IsInModal,
                ["fadeInScene"] = GameBridge.IsLoadingFadeInScene(),
                ["avgFrameMs"] = Math.Round(Determinism.DeterministicSandboxPatches.DiagAvgFrameMs, 1),
                ["avgLogicMs"] = Math.Round(Determinism.DeterministicSandboxPatches.DiagAvgTickLogicMs, 1),
                ["avgPhysMs"] = Math.Round(Determinism.DeterministicSandboxPatches.DiagAvgPhysicsMs, 1),
                ["lastWantedTicks"] = Determinism.DeterministicSandboxPatches.DiagLastWantedTicks,
                ["lastRanTicks"] = Determinism.DeterministicSandboxPatches.DiagLastRanTicks,
                ["cursorLocked"] = game?.CursorLocked ?? false,
                ["gameSpeed"] = game?.GameSpeed ?? 0,
                ["countdownActive"] = PauseSyncController.CountdownActive,
                ["mayorCount"] = GameBridge.GetLevelCollectionCount("MayorStates"),
                ["levelLoaded"] = ls != null,
                ["endStatus"] = GameBridge.GetLevelEndStatus(),
                ["levelEnded"] = GameBridge.IsLevelEnded(),
                ["gameMode"] = gameMode?.ToString(),
                ["inCampaign"] = inCampaign is bool ic && ic,
                ["difficulty"] = difficulty?.ToString(),
                ["survivalMode"] = survivalParams != null,
                ["survivalNCells"] = survivalParams != null ? GameBridge.GetRandomParamsNCells(survivalParams) : 0,
                ["levelNCells"] = DXLevel.Current?.NCells ?? 0,
                ["commandCenterExtraWorkers"] = ccExtraWorkers,
                ["commandCenterExtraFood"] = ccExtraFood,
                ["balancePreset"] = Sync.CoopSessionManager.BalancePreset.ToString(),
                ["hudDiag"] = GameBridge.GetHudDiagSummary(),
                ["gold"] = GameBridge.GetLevelInt("Gold"),
                ["wood"] = GameBridge.GetLevelInt("Wood"),
                ["stone"] = GameBridge.GetLevelInt("Stone"),
                ["iron"] = GameBridge.GetLevelInt("Iron"),
                ["oil"] = GameBridge.GetLevelInt("Oil"),
                ["entityCount"] = entityCount,
                ["playerEntityCount"] = playerCount
            };
        }

        private static object ListEntities(Dictionary<string, object> args)
        {
            string typeContains = GetStr(args, "typeContains");
            bool onlyPlayer = GetBool(args, "onlyPlayer", false);
            bool movable = GetBool(args, "movable", false);
            bool includeProjectiles = GetBool(args, "includeProjectiles", false);
            int limit = GetInt(args, "limit", 50);
            if (limit <= 0 || limit > MaxListEntities) limit = MaxListEntities;

            var list = new List<Dictionary<string, object>>();
            var game = DXGame.Current;
            if (game?.Entities != null)
            {
                Type movableType = movable ? GameBridge.TypeCMovable : null;

                // Sorted by ID so output is stable across calls (agents diff the result).
                var matched = new List<DXEntity>();
                foreach (var e in game.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    if ((e.ID & 0x8000000000000000UL) != 0) continue;
                    string tn = e.GetType().Name;
                    bool isProj = tn.IndexOf("Projectile", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  tn.IndexOf("Proyectile", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!includeProjectiles && isProj) continue;
                    if (onlyPlayer && !GameBridge.IsPlayerEntity(e)) continue;
                    if (!string.IsNullOrEmpty(typeContains) && tn.IndexOf(typeContains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (movableType != null && !GameBridge.HasComponent(e, movableType)) continue;
                    matched.Add(e);
                }
                matched.Sort((a, b) => a.ID.CompareTo(b.ID));

                int count = 0;
                foreach (var e in matched)
                {
                    if (count++ >= limit) break;
                    list.Add(new Dictionary<string, object>
                    {
                        // IDs are 64-bit and exceed JS safe-integer range → always render as strings.
                        ["id"] = e.ID.ToString(CultureInfo.InvariantCulture),
                        ["type"] = e.GetType().Name,
                        ["x"] = Math.Round(e.Position.X, 2),
                        ["y"] = Math.Round(e.Position.Y, 2),
                        ["hp"] = GameBridge.GetLifeValue(e),
                        ["player"] = GameBridge.IsPlayerEntity(e),
                        ["movable"] = GameBridge.HasComponent(e, GameBridge.TypeCMovable)
                    });
                }
            }

            return new Dictionary<string, object> { ["count"] = list.Count, ["entities"] = list };
        }

        private static object MoveUnits(Dictionary<string, object> args)
        {
            ulong[] ids = GetULongArray(args, "ids");
            if (ids.Length == 0) throw new ArgumentException("ids is required");
            float x = GetFloat(args, "x");
            float y = GetFloat(args, "y");

            Type travelType = GameBridge.GameAssembly?.GetType("ZX.Commands.Travel");
            object travel = travelType != null ? GameBridge.GetCommandGeneric(travelType) : null;
            if (travel == null) throw new InvalidOperationException("Travel command singleton unresolved");

            var cmd = new NetCommand
            {
                Type = NetCommandType.GroupMoveOrPatrol,
                PlayerId = CommandInterceptor.LocalPlayerId,
                CommandId = GameBridge.GetCommandId(travel),
                ExecutorEntityIds = ids,
                TargetPosition = new PointF(x, y),
                IsShiftQueued = GetBool(args, "shift", false)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object PlaceBuilding(Dictionary<string, object> args)
        {
            string typeName = GetStr(args, "buildingType") ?? "ZX.Entities.TentHouse";
            float x = GetFloat(args, "x");
            float y = GetFloat(args, "y");
            float w = GetFloat(args, "sizeW", 1f);
            float h = GetFloat(args, "sizeH", 1f);

            ulong builderId = GetULong(args, "builderId", 0);
            var ids = builderId != 0UL ? new[] { builderId } : Array.Empty<ulong>();

            var cmd = new NetCommand
            {
                Type = NetCommandType.PlaceBuilding,
                PlayerId = CommandInterceptor.LocalPlayerId,
                BuildingTypeName = typeName,
                ExecutorEntityIds = ids,
                TargetPosition = new PointF(x, y),
                TargetSize = new SizeF(w, h),
                TargetOrigin = new PointF(x, y),
                IsBonusBuilding = GetBool(args, "isBonus", false),
                InstantBuild = GetBool(args, "instant", false),
                IsShiftQueued = GetBool(args, "shift", false)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object TrainUnit(Dictionary<string, object> args)
        {
            ulong barracksId = GetULong(args, "barracksId", 0);
            if (barracksId == 0UL) throw new ArgumentException("barracksId is required");
            string unitType = GetStr(args, "unitType") ?? "ZX.Entities.Ranger";

            Type ut = GameBridge.GameAssembly?.GetType(unitType);
            if (ut == null) throw new ArgumentException($"unknown unit type '{unitType}'");

            object trainCmd = GameBridge.GetTrainCommandForType(ut);
            if (trainCmd == null) throw new InvalidOperationException($"no Train command for '{unitType}'");

            var cmd = new NetCommand
            {
                Type = NetCommandType.ExecuteCommand,
                PlayerId = CommandInterceptor.LocalPlayerId,
                CommandId = GameBridge.GetCommandId(trainCmd),
                ExecutorEntityIds = new[] { barracksId }
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object CancelCurrent(Dictionary<string, object> args)
        {
            ulong[] ids = GetULongArray(args, "ids");
            if (ids.Length == 0) throw new ArgumentException("ids is required");

            var cmd = new NetCommand { Type = NetCommandType.CancelCurrentOrder, PlayerId = CommandInterceptor.LocalPlayerId, ExecutorEntityIds = ids };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object CancelQueued(Dictionary<string, object> args)
        {
            ulong[] ids = GetULongArray(args, "ids");
            if (ids.Length == 0) throw new ArgumentException("ids is required");

            var cmd = new NetCommand
            {
                Type = NetCommandType.CancelQueuedOrder,
                PlayerId = CommandInterceptor.LocalPlayerId,
                ExecutorEntityIds = ids,
                QueueSlotIndex = (byte)GetInt(args, "slot", 0)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object SetPaused(Dictionary<string, object> args)
        {
            bool paused = GetBool(args, "paused", true);
            var cmd = new NetCommand { Type = NetCommandType.SetPaused, PlayerId = CommandInterceptor.LocalPlayerId, TargetPausedState = paused };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["paused"] = DXGame.Current?.Paused ?? false };
        }

        private static object IssueRawCommand(Dictionary<string, object> args)
        {
            var cmd = new NetCommand
            {
                Type = (NetCommandType)GetInt(args, "type", 1),
                PlayerId = (byte)GetInt(args, "playerId", CommandInterceptor.LocalPlayerId),
                CommandId = GetInt(args, "commandId", 0),
                BuildingTypeName = GetStr(args, "buildingType") ?? string.Empty,
                ExecutorEntityIds = GetULongArray(args, "ids"),
                TargetEntityId = GetULong(args, "targetEntityId", 0),
                TargetPosition = new PointF(GetFloat(args, "x"), GetFloat(args, "y")),
                TargetSize = new SizeF(GetFloat(args, "sizeW", 0f), GetFloat(args, "sizeH", 0f)),
                TargetOrigin = new PointF(GetFloat(args, "originX"), GetFloat(args, "originY")),
                IsShiftQueued = GetBool(args, "shift", false),
                QueueSlotIndex = (byte)GetInt(args, "slot", 0),
                TargetPausedState = GetBool(args, "paused", false)
            };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object> { ["dispatched"] = true, ["command"] = cmd.ToString() };
        }

        private static object TakeScreenshot(Dictionary<string, object> args)
        {
            string tag = GetStr(args, "tag") ?? "manual";
            string path = ScreenshotService.CaptureNow(tag);
            return new Dictionary<string, object>
            {
                ["saved"] = path != null,
                ["path"] = path ?? string.Empty
            };
        }

        private static object GetCamera()
        {
            var c = CameraProbe.Capture();
            return new Dictionary<string, object>
            {
                ["valid"] = c.Valid,
                ["x"] = c.X,
                ["y"] = c.Y,
                ["w"] = c.W,
                ["h"] = c.H,
                ["screenW"] = c.ScreenW,
                ["screenH"] = c.ScreenH,
                ["windowW"] = c.WindowW,
                ["windowH"] = c.WindowH
            };
        }

        private static object SetCamera(Dictionary<string, object> args)
        {
            var scene = DXScene.Current;
            if (scene == null) throw new InvalidOperationException("no active scene");

            RectangleF cur = scene.CameraArea;
            float w = GetFloat(args, "w", cur.Width > 0f ? cur.Width : 0f);
            float h = GetFloat(args, "h", 0f);
            float zoom = GetFloat(args, "zoom", 1f);
            if (zoom > 0f && Math.Abs(zoom - 1f) > 1e-4f) { w /= zoom; h /= zoom; }

            // x/y and entity positions are WORLD-grid coordinates; GameBridge.SetCameraWorld converts
            // them to isometric scene space before centering the camera.
            float wx, wy;
            ulong eid = GetULong(args, "entityId", 0);
            if (eid != 0UL)
            {
                var ent = DXGame.Current?.GetEntityByID(eid);
                if (ent == null) throw new ArgumentException($"entity {eid} not found");
                wx = ent.Position.X; wy = ent.Position.Y;
            }
            else
            {
                var worldCenter = DXWorldGrid.WorldPointFromScenePoint(
                    new PointF(cur.X + cur.Width / 2f, cur.Y + cur.Height / 2f));
                wx = GetFloat(args, "x", worldCenter.X);
                wy = GetFloat(args, "y", worldCenter.Y);
            }

            if (!GameBridge.SetCameraWorld(new PointF(wx, wy), w, h))
                throw new InvalidOperationException("failed to set camera");

            RectangleF applied = scene.CameraArea;
            return new Dictionary<string, object>
            {
                ["worldX"] = wx, ["worldY"] = wy,
                ["x"] = applied.X, ["y"] = applied.Y, ["w"] = applied.Width, ["h"] = applied.Height,
                ["centerX"] = applied.X + applied.Width / 2f,
                ["centerY"] = applied.Y + applied.Height / 2f
            };
        }

        private static object RequestPauseTool(Dictionary<string, object> args)
        {
            bool paused = GetBool(args, "paused", true);
            PauseSyncController.RequestPause(paused);
            return new Dictionary<string, object>
            {
                ["requested"] = paused,
                ["countdownActive"] = PauseSyncController.CountdownActive,
                ["secondsRemaining"] = PauseSyncController.SecondsRemaining,
                ["paused"] = DXGame.Current?.Paused ?? false
            };
        }

        private static object ListMayors(Dictionary<string, object> args)
        {
            int level = GetInt(args, "level", 0);
            var list = GameBridge.GetMayorList(level);
            return new Dictionary<string, object> { ["count"] = list.Count, ["mayors"] = list };
        }

        private static object SelectMayor(Dictionary<string, object> args)
        {
            string id = GetStr(args, "mayorId");
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("mayorId is required");

            var cmd = new NetCommand { Type = NetCommandType.MayorVoteOrSelect, PlayerId = CommandInterceptor.LocalPlayerId, MayorId = id };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object>
            {
                ["dispatched"] = true,
                ["mayorId"] = id,
                ["mayorCount"] = GameBridge.GetLevelCollectionCount("MayorStates")
            };
        }

        private static object SetGameSpeed(Dictionary<string, object> args)
        {
            float speed = GetFloat(args, "speed", 1f);
            speed = Math.Max(0f, Math.Min(20f, speed));
            var cmd = new NetCommand { Type = NetCommandType.SetGameSpeed, PlayerId = CommandInterceptor.LocalPlayerId, GameSpeed = speed };
            CommandInterceptor.Dispatch(cmd);
            return new Dictionary<string, object>
            {
                ["dispatched"] = true,
                ["gameSpeed"] = DXGame.Current?.GameSpeed ?? 0
            };
        }

        private static object RevealMap(Dictionary<string, object> args)
        {
            bool reveal = GetBool(args, "enabled", true);
            bool ok = GameBridge.RevealAll(reveal);
            return new Dictionary<string, object>
            {
                ["reveal"] = reveal,
                ["applied"] = ok
            };
        }

        private static void AttachScreenshot(string tool, Dictionary<string, object> args, object result)
        {
            if (!(result is Dictionary<string, object> dict)) return;
            if (!TestHarnessConfig.AutoShot) return;
            if (!GetBool(args, "screenshot", true)) return; // per-call opt-out
            if (!IsMutatingTool(tool)) return;

            // Deferred capture: the file appears a couple of frames after the operation is applied.
            dict["screenshot"] = ScreenshotService.RequestAfterFrames(tool);
        }

        private static bool IsMutatingTool(string tool)
        {
            switch ((tool ?? string.Empty).ToLowerInvariant())
            {
                case "move_units":
                case "place_building":
                case "train_unit":
                case "cancel_current_order":
                case "cancel_queued_order":
                case "set_paused":
                case "set_game_speed":
                case "request_pause":
                case "mayor_select":
                case "issue_command":
                case "set_camera":
                case "reveal_map":
                    return true;
                default:
                    return false;
            }
        }

        private static object GetLobbyState()
        {
            var peers = new List<Dictionary<string, object>>();
            foreach (var p in Sync.CoopSessionManager.GetEffectivePeers())
            {
                peers.Add(new Dictionary<string, object>
                {
                    ["id"] = p.PlayerId,
                    ["name"] = p.PlayerName,
                    ["ready"] = p.IsReady
                });
            }

            return new Dictionary<string, object>
            {
                ["onMainMenu"] = GameBridge.IsOnMainMenu(),
                ["mainMenuReady"] = GameBridge.IsMainMenuReadyForInput(),
                ["inLevel"] = GameBridge.CurrentLevelState != null,
                ["loading"] = GameBridge.IsGameLoading(),
                ["anyModalOpen"] = GameBridge.IsAnyModalOpen(),
                ["modalHasEditBox"] = DXVision.GUI.DXModalScreen.IsInModal && HasModalEditBox(DXVision.GUI.DXModalScreen.CurrentModalObject),
                ["lobbyWindowOpen"] = CoopLobbyWindow.IsOpen,
                ["sessionActive"] = Sync.CoopSessionManager.IsSessionActive,
                ["isHost"] = Sync.CoopSessionManager.Transport?.IsHost ?? false,
                ["localPlayerId"] = Sync.CoopSessionManager.Transport?.LocalPlayerId ?? 0,
                ["roomPhase"] = Sync.CoopSessionManager.RoomPhase.ToString(),
                ["canStart"] = Sync.CoopSessionManager.CanHostStartGame,
                ["balancePreset"] = Sync.CoopSessionManager.BalancePreset.ToString(),
                ["sessionSeed"] = Sync.CoopSessionManager.CurrentSessionSeed,
                ["survivalGenBusy"] = GameBridge.SurvivalGenInProgress,
                ["survivalGenOk"] = GameBridge.SurvivalGenLastOk,
                ["survivalGenError"] = GameBridge.SurvivalGenLastError ?? "",
                ["isLocalLoopback"] = Sync.CoopSessionManager.IsLocalLoopbackSession,
                ["isLanSession"] = Sync.CoopSessionManager.IsLanSession,
                ["localIps"] = Networking.LocalTcpLoopbackTransport.GetLocalIPv4Addresses(),
                ["chaosEnabled"] = Sync.CoopSessionManager.LocalChaosEnabled,
                ["chaosLatencyMs"] = Sync.CoopSessionManager.LocalChaosLatencyMs,
                ["chaosJitterMs"] = Sync.CoopSessionManager.LocalChaosJitterMs,
                ["chaosLossRate"] = Sync.CoopSessionManager.LocalChaosLossRate,
                ["smoothedRttMs"] = Sync.CoopSessionManager.CurrentSmoothedRttMs,
                ["peers"] = peers
            };
        }

        private static object RunLobbyAction(Dictionary<string, object> args)
        {
            string act = GetStr(args, "action") ?? "";
            switch (act.ToLowerInvariant())
            {
                case "close_top_modal":
                    var modal = DXVision.GUI.DXModalScreen.CurrentModalObject;
                    if (modal != null) GameBridge.CloseZXWindow(modal);
                    return new Dictionary<string, object> { ["ok"] = modal != null };
                case "confirm_modal":
                {
                    var topModal = DXVision.GUI.DXModalScreen.CurrentModalObject;
                    bool hasEdit = HasModalEditBox(topModal);
                    string inputText = GetStr(args, "inputText");
                    if (!string.IsNullOrEmpty(inputText) && topModal != null)
                        SetModalEditBoxText(topModal, inputText);

                    DXVision.GUI.DXButton confirmBtn = FindRightmostEnabledButton(topModal);
                    ModLogger.Info($"[ControlServer] confirm_modal: topModal={topModal?.GetType().Name ?? "null"} hasEdit={hasEdit} inputText='{inputText ?? ""}' btn={(confirmBtn != null ? confirmBtn.X + "," + confirmBtn.Y : "null")}");
                    if (confirmBtn != null)
                    {
                        confirmBtn.Activate();
                        return new Dictionary<string, object> { ["ok"] = true, ["hasEdit"] = hasEdit, ["x"] = confirmBtn.X, ["y"] = confirmBtn.Y };
                    }
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "no enabled confirm button found on modal" };
                }
                case "kick_fade":
                    GameBridge.KickLoadingFade(700.0);
                    return new Dictionary<string, object> { ["ok"] = true };
                case "return_to_menu":
                    bool r = GameBridge.ReturnToMainMenu();
                    return new Dictionary<string, object> { ["ok"] = r };
                case "trigger_game_end":
                    bool won = GetBool(args, "won", false);
                    bool endOk = GameBridge.TriggerGameEnd(won);
                    return new Dictionary<string, object> { ["ok"] = endOk, ["won"] = won, ["endStatus"] = GameBridge.GetLevelEndStatus() };
                case "trigger_mayor_popup":
                    bool popOk = GameBridge.TriggerMayorPopup();
                    return new Dictionary<string, object> { ["ok"] = popOk, ["inModal"] = DXVision.GUI.DXModalScreen.IsInModal };
                case "load_save":
                    if (!Sync.CoopSessionManager.CanHostStartGame)
                        return new Dictionary<string, object> { ["ok"] = false, ["error"] = "cannot start before clients are ready" };
                    string save = GetStr(args, "save") ?? "1_Backup";
                    CoopLobbyWindow.ForceHideWithoutStopping();
                    Sync.CoopSessionManager.SetHostRoomPhase(Sync.LobbyRoomPhase.HostLoadingLevel, "房主正在加载存档...");
                    GameBridge.LoadSaveFileAndEnter(save);
                    return new Dictionary<string, object> { ["ok"] = true, ["save"] = save };
                case "probe":
                    var res = Networking.LocalTcpLoopbackTransport.ProbeLocalHost();
                    return new Dictionary<string, object>
                    {
                        ["online"] = res.IsOnline,
                        ["players"] = res.PlayerCount,
                        ["max"] = res.MaxPlayers,
                        ["phase"] = res.RoomPhase,
                        ["hostName"] = res.HostName
                    };
                case "set_chaos":
                    Sync.CoopSessionManager.SetLocalChaosSettings(
                        GetBool(args, "enabled", Sync.CoopSessionManager.LocalChaosEnabled),
                        GetInt(args, "latencyMs", Sync.CoopSessionManager.LocalChaosLatencyMs),
                        GetInt(args, "jitterMs", Sync.CoopSessionManager.LocalChaosJitterMs),
                        GetFloat(args, "lossRate", (float)Sync.CoopSessionManager.LocalChaosLossRate));
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["enabled"] = Sync.CoopSessionManager.LocalChaosEnabled,
                        ["latencyMs"] = Sync.CoopSessionManager.LocalChaosLatencyMs,
                        ["jitterMs"] = Sync.CoopSessionManager.LocalChaosJitterMs,
                        ["lossRate"] = Sync.CoopSessionManager.LocalChaosLossRate
                    };
                case "bonus_slots":
                    return GetBonusSlotsInfo();
                case "invoke_bonus_slot":
                    return InvokeBonusSlot(args);
                case "set_lan_target":
                    CoopLobbyWindow.TestLanTarget = GetStr(args, "target") ?? "";
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["target"] = CoopLobbyWindow.TestLanTarget,
                        ["localIps"] = Networking.LocalTcpLoopbackTransport.GetLocalIPv4Addresses()
                    };
                default:
                    bool ok = CoopLobbyWindow.InvokeTestAction(act);
                    return new Dictionary<string, object> { ["ok"] = ok, ["action"] = act };
            }
        }

        private static object StartSurvivalGen(Dictionary<string, object> args)
        {
            int seed = GetInt(args, "seed", 12345);
            string theme = GetStr(args, "theme") ?? "BR";
            float factorPop = GetFloat(args, "factorPop", 1.0f);
            float factorDuration = GetFloat(args, "factorDuration", 1.0f);
            string mode = (GetStr(args, "mode") ?? "vanilla").ToLowerInvariant();

            Sync.CoopBalancePreset preset = Sync.CoopBalancePreset.Vanilla;
            int playerCount = 1;
            if (mode == "coop2p")
            {
                preset = Sync.CoopBalancePreset.Balanced;
                playerCount = 2;
            }
            else if (mode == "coop3p")
            {
                preset = Sync.CoopBalancePreset.Balanced;
                playerCount = 3;
            }

            bool started = GameBridge.StartSurvivalAsync(seed, theme, factorPop, factorDuration, preset, playerCount);
            return new Dictionary<string, object>
            {
                ["ok"] = started,
                ["started"] = started,
                ["mode"] = mode
            };
        }

        private static object CapturePanoramaTool(Dictionary<string, object> args)
        {
            string path = GetStr(args, "path");
            if (string.IsNullOrEmpty(path))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "path is required" };
            int res = GetInt(args, "resolution", 2048);
            bool reveal = GetBool(args, "reveal", true);

            bool ok = GameBridge.CaptureMapPanorama(path, res, reveal);
            return new Dictionary<string, object>
            {
                ["ok"] = ok,
                ["path"] = path,
                ["resolution"] = res
            };
        }

        private static object GetBonusSlotsInfo()
        {
            var cc = GameBridge.GetCommandCenterEntity();
            var slots = new List<Dictionary<string, object>>();
            var cmds = GameBridge.GetAvailableCommandsForEntity(cc);
            if (cmds != null)
            {
                foreach (object cmd in cmds)
                {
                    if (!GameBridge.TryGetEntityBonusInfo(cmd, out int slot, out Type entType, out bool isStructure, out _))
                        continue;
                    if (entType == null) continue;
                    int count = GameBridge.GetBonusEntityCount(entType, out _);
                    slots.Add(new Dictionary<string, object>
                    {
                        ["slot"] = slot,
                        ["entityType"] = entType.FullName,
                        ["isStructure"] = isStructure,
                        ["count"] = count
                    });
                }
            }
            return new Dictionary<string, object>
            {
                ["ok"] = cc != null,
                ["ccId"] = cc?.ID ?? 0UL,
                ["slots"] = slots
            };
        }

        private static object InvokeBonusSlot(Dictionary<string, object> args)
        {
            int targetSlot = GetInt(args, "slot", 1);
            var cc = GameBridge.GetCommandCenterEntity();
            if (cc == null)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "CommandCenter not found" };

            var cmds = GameBridge.GetAvailableCommandsForEntity(cc);
            object matched = null;
            if (cmds != null)
            {
                foreach (object cmd in cmds)
                {
                    if (GameBridge.TryGetEntityBonusInfo(cmd, out int slot, out _, out _, out _) && slot == targetSlot)
                    {
                        matched = cmd;
                        break;
                    }
                }
            }
            if (matched == null)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = $"Bonus slot {targetSlot} not found" };

            float x = GetFloat(args, "x", 0f);
            float y = GetFloat(args, "y", 0f);
            float w = GetFloat(args, "sizeW", 0f);
            float h = GetFloat(args, "sizeH", 0f);
            object cmdTarget = GameBridge.CreateCommandTarget(
                (x != 0f || y != 0f) ? new PointF(x, y) : PointF.Empty,
                (w > 0f && h > 0f) ? new SizeF(w, h) : SizeF.Empty,
                (x != 0f || y != 0f) ? new PointF(x, y) : PointF.Empty,
                0UL);

            // Invokes ZXCommand.Execute cleanly through Harmony Patch_ZXCommand_Execute just like a real UI click.
            GameBridge.ExecuteCommandOnEntity(matched, cc, cmdTarget);
            GameBridge.TryGetEntityBonusInfo(matched, out _, out Type entType, out bool isStructure, out _);
            return new Dictionary<string, object>
            {
                ["ok"] = entType != null,
                ["slot"] = targetSlot,
                ["entityType"] = entType?.FullName ?? string.Empty,
                ["isStructure"] = isStructure
            };
        }

        private static bool HasModalEditBox(DXVision.DXObject root)
        {
            if (root == null) return false;
            var stack = new Stack<DXVision.DXObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                if (cur is DXVision.DXEditBoxObject) return true;
                foreach (var child in cur.Children)
                {
                    if (child != null) stack.Push(child);
                }
            }
            return false;
        }

        private static void SetModalEditBoxText(DXVision.DXObject root, string text)
        {
            if (root == null) return;
            var stack = new Stack<DXVision.DXObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null) continue;
                if (cur is DXVision.DXEditBoxObject editBox)
                {
                    editBox.Text = text;
                }
                foreach (var child in cur.Children)
                {
                    if (child != null) stack.Push(child);
                }
            }
        }

        private static DXVision.GUI.DXButton FindRightmostEnabledButton(DXVision.DXObject root)
        {
            if (root == null) return null;
            DXVision.GUI.DXButton best = null;
            var stack = new Stack<DXVision.DXObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (cur == null || !cur.Visible) continue;
                if (cur is DXVision.GUI.DXButton btn && btn.Enabled)
                {
                    // Prefer the bottom-right confirmation button (higher Y, then higher X).
                    if (best == null || btn.Y > best.Y + 5f || (Math.Abs(btn.Y - best.Y) <= 5f && btn.X > best.X))
                        best = btn;
                }
                foreach (var child in cur.Children)
                {
                    if (child != null) stack.Push(child);
                }
            }
            return best;
        }

        private static int CurrentTick() => DXGame.Current?.IAIterationInt ?? 0;

        // ====================================================================
        // JSON helpers
        // ====================================================================

        private static string OkResponse(long id, object result)
        {
            var payload = new Dictionary<string, object> { ["id"] = id, ["ok"] = true, ["result"] = result };
            return _json.Serialize(payload);
        }

        private static string ErrorResponse(long id, string message)
        {
            var payload = new Dictionary<string, object> { ["id"] = id, ["ok"] = false, ["error"] = message ?? "unknown error" };
            return _json.Serialize(payload);
        }

        private static string GetStr(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToBoolean(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static int GetInt(Dictionary<string, object> d, string key, int fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static long GetLong(Dictionary<string, object> d, string key, long fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static float GetFloat(Dictionary<string, object> d, string key, float fallback = 0f)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static ulong GetULong(Dictionary<string, object> d, string key, ulong fallback)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            try { return Convert.ToUInt64(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }

        private static ulong[] GetULongArray(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null)
                return Array.Empty<ulong>();

            if (v is IEnumerable en && !(v is string))
            {
                var list = new List<ulong>();
                foreach (object item in en)
                {
                    if (item == null) continue;
                    try { list.Add(Convert.ToUInt64(item, CultureInfo.InvariantCulture)); } catch { }
                }
                return list.ToArray();
            }

            try { return new[] { Convert.ToUInt64(v, CultureInfo.InvariantCulture) }; } catch { return Array.Empty<ulong>(); }
        }
    }
}
