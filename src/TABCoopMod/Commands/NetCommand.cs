using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using DXVision;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;

namespace TABCoopMod.Commands
{
    public enum NetCommandType : byte
    {
        None = 0,
        ExecuteCommand = 1,      // Generic ZXCommand.#=zfmgzkuw= (Attack, Train, Tech, Destroy, Repair, Market, RallyPoint, etc.)
        GroupMoveOrPatrol = 2,   // Group distributed move/patrol/grenade (#=zgn6bDkSd9MGS3$WvAQ==)
        PlaceBuilding = 3,       // Building placement (Build.#=zBTwSMLo=)
        CancelCurrentOrder = 4,  // Cancel active command/construction (CCommandable.#=zWGiN6nrTv8Dp)
        CancelQueuedOrder = 5,   // Cancel production queue slot (CCommandable.#=z8g7GC7xLrr8j)
        SetPaused = 6,           // Pause / Resume toggle
        SetGameSpeed = 7,        // GameSpeed multiplier change
        MayorVoteOrSelect = 8    // Mayor selection (0 = left male, 1 = right female)
    }

    /// <summary>
    /// Serializable deterministic player command payload.
    /// Carries explicit ExecutorEntityIds so replay NEVER touches or depends on local CSelectable!
    /// </summary>
    public sealed class NetCommand
    {
        public NetCommandType Type { get; set; }
        public byte PlayerId { get; set; } = 1;
        public int TargetSimTick { get; set; }
        public bool IsShiftQueued { get; set; }

        // Command & Entity Identifiers
        public int CommandId { get; set; }
        public string BuildingTypeName { get; set; } = string.Empty;
        public ulong[] ExecutorEntityIds { get; set; } = Array.Empty<ulong>();

        // ZXCommandTarget Payload
        public ulong TargetEntityId { get; set; }
        public PointF TargetPosition { get; set; }
        public SizeF TargetSize { get; set; }
        public PointF TargetOrigin { get; set; }

        // Building / Queue / State Extras
        public Rectangle BuildingCellArea { get; set; }
        public bool IsBonusBuilding { get; set; }
        public bool InstantBuild { get; set; }
        public byte QueueSlotIndex { get; set; }
        public bool TargetPausedState { get; set; }
        public float GameSpeed { get; set; } = 1f;
        public string MayorId { get; set; } = string.Empty;
        public byte MayorOptionIndex { get; set; }
        public bool IsMayorFinalDecision { get; set; }
        public uint TraceId { get; set; }

        public void WriteTo(BinaryWriter w)
        {
            w.Write((byte)Type);
            w.Write(PlayerId);
            w.Write(TargetSimTick);
            w.Write(IsShiftQueued);
            w.Write(CommandId);
            w.Write(BuildingTypeName ?? string.Empty);

            ushort count = (ushort)(ExecutorEntityIds?.Length ?? 0);
            w.Write(count);
            for (int i = 0; i < count; i++)
            {
                w.Write(ExecutorEntityIds[i]);
            }

            w.Write(TargetEntityId);
            w.Write(TargetPosition.X);
            w.Write(TargetPosition.Y);
            w.Write(TargetSize.Width);
            w.Write(TargetSize.Height);
            w.Write(TargetOrigin.X);
            w.Write(TargetOrigin.Y);

            w.Write(BuildingCellArea.X);
            w.Write(BuildingCellArea.Y);
            w.Write(BuildingCellArea.Width);
            w.Write(BuildingCellArea.Height);
            w.Write(IsBonusBuilding);
            w.Write(InstantBuild);
            w.Write(QueueSlotIndex);
            w.Write(TargetPausedState);
            w.Write(MayorOptionIndex);
            w.Write(IsMayorFinalDecision);
            w.Write(GameSpeed);
            w.Write(MayorId ?? string.Empty);
            w.Write(TraceId);
        }

        public static NetCommand ReadFrom(BinaryReader r)
        {
            var cmd = new NetCommand
            {
                Type = (NetCommandType)r.ReadByte(),
                PlayerId = r.ReadByte(),
                TargetSimTick = r.ReadInt32(),
                IsShiftQueued = r.ReadBoolean(),
                CommandId = r.ReadInt32(),
                BuildingTypeName = r.ReadString()
            };

            ushort count = r.ReadUInt16();
            cmd.ExecutorEntityIds = new ulong[count];
            for (int i = 0; i < count; i++)
            {
                cmd.ExecutorEntityIds[i] = r.ReadUInt64();
            }

            cmd.TargetEntityId = r.ReadUInt64();
            cmd.TargetPosition = new PointF(r.ReadSingle(), r.ReadSingle());
            cmd.TargetSize = new SizeF(r.ReadSingle(), r.ReadSingle());
            cmd.TargetOrigin = new PointF(r.ReadSingle(), r.ReadSingle());

            cmd.BuildingCellArea = new Rectangle(r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            cmd.IsBonusBuilding = r.ReadBoolean();
            cmd.InstantBuild = r.ReadBoolean();
            cmd.QueueSlotIndex = r.ReadByte();
            cmd.TargetPausedState = r.ReadBoolean();
            cmd.MayorOptionIndex = r.ReadByte();
            cmd.IsMayorFinalDecision = r.ReadBoolean();
            cmd.GameSpeed = r.ReadSingle();
            cmd.MayorId = r.ReadString();
            if (r.BaseStream.Position + 4 <= r.BaseStream.Length)
                cmd.TraceId = r.ReadUInt32();

            return cmd;
        }

        public override string ToString()
        {
            return $"[NetCmd P{PlayerId} T={TargetSimTick} {Type} CmdId={CommandId} Entities={ExecutorEntityIds?.Length ?? 0} Pos=({TargetPosition.X:F1},{TargetPosition.Y:F1})]";
        }
    }

    /// <summary>
    /// Executes NetCommands deterministically on the local game state without modifying local player's CSelectable.
    /// </summary>
    public static class CommandExecutor
    {
        /// <summary>
        /// Reentrancy guard: when true, CommandInterceptorPatch lets original game methods run without re-capturing.
        /// </summary>
        [ThreadStatic]
        public static bool IsReplayingCommand;

        /// <summary>
        /// True while replaying a command originally issued by the local player in a live co-op session,
        /// allowing 0ms-predicted build/order SFX to suppress duplicate replay Effects audio.
        /// </summary>
        [ThreadStatic]
        public static bool IsReplayingLocalBuild;

        public static void Execute(NetCommand netCmd)
        {
            if (netCmd == null || netCmd.Type == NetCommandType.None)
                return;

            bool prevReplaying = IsReplayingCommand;
            bool prevLocalBuild = IsReplayingLocalBuild;
            bool prevDet = LockstepClock.InDeterministicContext;
            IsReplayingCommand = true;
            IsReplayingLocalBuild = TABCoopMod.Sync.CoopSessionManager.IsSessionActive
                && netCmd.PlayerId == TABCoopMod.Patches.CommandInterceptor.LocalPlayerId
                && (netCmd.Type == NetCommandType.PlaceBuilding
                    || netCmd.Type == NetCommandType.ExecuteCommand
                    || netCmd.Type == NetCommandType.CancelCurrentOrder
                    || netCmd.Type == NetCommandType.CancelQueuedOrder);
            LockstepClock.InDeterministicContext = true;

            // Save local Shift key state so we can simulate the sender's Shift modifier accurately
            bool hadShiftKey = false;
            var inputKeys = DXInputState.Current?.Keys;
            if (inputKeys != null)
            {
                hadShiftKey = inputKeys.Contains(DXKeys.Shift);
                if (netCmd.IsShiftQueued && !hadShiftKey)
                    inputKeys.Add(DXKeys.Shift);
                else if (!netCmd.IsShiftQueued && hadShiftKey)
                    inputKeys.Remove(DXKeys.Shift);
            }

            try
            {
                switch (netCmd.Type)
                {
                    case NetCommandType.ExecuteCommand:
                        ExecuteSingleOrBatchCommand(netCmd);
                        break;

                    case NetCommandType.GroupMoveOrPatrol:
                        ExecuteGroupMoveCommand(netCmd);
                        break;

                    case NetCommandType.PlaceBuilding:
                        ExecutePlaceBuilding(netCmd);
                        break;

                    case NetCommandType.CancelCurrentOrder:
                        ExecuteCancelCurrentOrder(netCmd);
                        break;

                    case NetCommandType.CancelQueuedOrder:
                        ExecuteCancelQueuedOrder(netCmd);
                        break;

                    case NetCommandType.SetPaused:
                        if (DXGame.Current != null)
                        {
                            // Drive the fixed-step clock's freeze off this session flag, not the
                            // engine's DXGame.Paused (which the save loader leaves set).
                            TABCoopMod.Determinism.LockstepClock.SetSessionPause(netCmd.TargetPausedState);
                            DXGame.Current.Paused = netCmd.TargetPausedState;
                            if (!TABCoopMod.Sync.PauseSyncController.CountdownActive)
                            {
                                // durationMs=0 avoids stalled fade-in clips while Paused is true.
                                GameBridge.ShowTopBanner(
                                    netCmd.TargetPausedState ? $"Player {netCmd.PlayerId} Paused Game" : $"Player {netCmd.PlayerId} Resumed Game",
                                    Color.Gold, netCmd.TargetPausedState ? 0 : 1500);
                            }
                        }
                        break;

                    case NetCommandType.SetGameSpeed:
                        if (DXGame.Current != null)
                        {
                            DXGame.Current.GameSpeed = Math.Max(0.0, Math.Min(20.0, (double)netCmd.GameSpeed));
                            GameBridge.ShowTopBanner($"Player {netCmd.PlayerId} set speed {DXGame.Current.GameSpeed:0.##}x", Color.Gold, 1200);
                        }
                        break;

                    case NetCommandType.MayorVoteOrSelect:
                        if (!string.IsNullOrEmpty(netCmd.MayorId) && GameBridge.ApplyMayor(netCmd.MayorId))
                        {
                            string mayorName = GameBridge.GetMayorNameById(netCmd.MayorId);
                            GameBridge.ShowTopBanner(
                                string.IsNullOrEmpty(mayorName)
                                    ? $"Player {netCmd.PlayerId} selected a mayor"
                                    : $"Player {netCmd.PlayerId} selected mayor: {mayorName}",
                                Color.Gold, 2500);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[CommandExecutor] Failed to execute {netCmd}", ex);
            }
            finally
            {
                // Restore local Shift key state
                if (inputKeys != null)
                {
                    if (hadShiftKey && !inputKeys.Contains(DXKeys.Shift))
                        inputKeys.Add(DXKeys.Shift);
                    else if (!hadShiftKey && inputKeys.Contains(DXKeys.Shift))
                        inputKeys.Remove(DXKeys.Shift);
                }

                LockstepClock.InDeterministicContext = prevDet;
                IsReplayingLocalBuild = prevLocalBuild;
                IsReplayingCommand = prevReplaying;
            }
        }

        private static void ExecuteSingleOrBatchCommand(NetCommand netCmd)
        {
            bool slotSensitive = !string.IsNullOrEmpty(netCmd.BuildingTypeName);
            object sharedCmd = slotSensitive ? null : GameBridge.GetCommandById(netCmd.CommandId);
            if (!slotSensitive && sharedCmd == null)
            {
                ModLogger.Warn($"[CommandExecutor] Unknown CommandId: {netCmd.CommandId}");
                return;
            }

            object target = BuildCommandTarget(netCmd);

            foreach (ulong entityId in netCmd.ExecutorEntityIds)
            {
                object executor = DXGame.Current?.GetEntityByID(entityId);
                if (!IsLiveExecutableEntity(executor))
                    continue;

                object cmd = slotSensitive
                    ? GameBridge.ResolveReplayCommand(netCmd.CommandId, netCmd.BuildingTypeName, netCmd.QueueSlotIndex, executor, out _)
                    : sharedCmd;
                if (cmd == null)
                {
                    ModLogger.Info($"[CommandExecutor] Rejected stale/unavailable slot command {netCmd.BuildingTypeName} (slot={netCmd.QueueSlotIndex}) on {entityId}.");
                    continue;
                }

                if (GameBridge.IsResourceSpendingCommand(cmd, executor) && !GameBridge.CanExecuteCommandOnEntity(cmd, executor))
                {
                    ModLogger.Info($"[CommandExecutor] Rejected {cmd.GetType().Name} on {entityId}: CanExecute=false.");
                    continue;
                }

                GameBridge.ExecuteCommandOnEntity(cmd, executor, target);
                GameBridge.NotifyUnitCommandExecuted(executor, cmd);
                if (slotSensitive)
                    GameBridge.FlushStartFramePostMethods();
            }
        }

        private static void ExecuteGroupMoveCommand(NetCommand netCmd)
        {
            object cmd = GameBridge.GetCommandById(netCmd.CommandId);
            if (cmd == null) return;

            Type movable = GameBridge.TypeCMovable;
            Type commandable = GameBridge.TypeCCommandable;

            var entities = new List<DXEntity>(netCmd.ExecutorEntityIds.Length);
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                var ent = DXGame.Current?.GetEntityByID(id);
                if (ent == null || ent.Disposed) continue;
                if (!GameBridge.HasComponent(ent, movable) || !GameBridge.HasComponent(ent, commandable)) continue;
                entities.Add(ent);
            }

            if (entities.Count == 0) return;

            // Make a copy because #=zgn6bDkSd9MGS3$WvAQ== mutates the passed list via Remove()!
            var entitiesForExecution = new List<DXEntity>(entities);

            // 1. Assign distributed target positions to each unit's CCommandable.Target
            GameBridge.AssignGroupMoveTargets(entities, cmd, netCmd.TargetPosition);

            // 2. Trigger command execution on each unit using its assigned CCommandable.Target
            foreach (var entity in entitiesForExecution)
            {
                object cmdComp = GameBridge.GetCommandableObject(entity);
                if (cmdComp == null) continue;

                object unitTarget = GameBridge.GetCommandableTargetObject(cmdComp);
                if (unitTarget == null) continue;

                GameBridge.ExecuteCommandOnEntity(cmd, entity, unitTarget);
                GameBridge.NotifyUnitCommandExecuted(entity, cmd);
            }
        }

        private static void ExecutePlaceBuilding(NetCommand netCmd)
        {
            if (netCmd.PlayerId == CommandInterceptor.LocalPlayerId)
            {
                Rectangle fp = GameBridge.ResolveBuildCellArea(null, netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin);
                CommandInterceptor.ConfirmLocalPendingBuild(netCmd.BuildingTypeName, fp);
            }

            if (string.IsNullOrEmpty(netCmd.BuildingTypeName)) return;

            Type buildingType = GameBridge.GameAssembly.GetType(netCmd.BuildingTypeName);
            if (buildingType == null)
            {
                ModLogger.Warn($"[CommandExecutor] Unknown BuildingTypeName: {netCmd.BuildingTypeName}");
                return;
            }

            ulong builderId = netCmd.ExecutorEntityIds.Length > 0 ? netCmd.ExecutorEntityIds[0] : 0UL;
            object builder = builderId != 0UL ? DXGame.Current?.GetEntityByID(builderId) : null;
            if (!IsLiveExecutableEntity(builder))
                builder = GameBridge.GetCommandCenterEntity();

            if (!IsLiveExecutableEntity(builder)) return;

            // Mayor-reward buildings come from EntityBonusCreation, which internally calls
            // Build.PlaceBuilding(isBonus:true) AND consumes one entry from BonusEntityTemplates.
            if (netCmd.IsBonusBuilding)
            {
                int prevBonusCount = GameBridge.GetBonusEntityCount(buildingType, out _);
                if (prevBonusCount <= 0)
                {
                    ModLogger.Info($"[CommandExecutor] Rejected bonus PlaceBuilding {netCmd.BuildingTypeName}: no remaining bonus charge.");
                    return;
                }
                if (!GameBridge.CanPlaceBuildingAtTarget(buildingType, netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin))
                {
                    ModLogger.Info($"[CommandExecutor] Rejected bonus PlaceBuilding {netCmd.BuildingTypeName}: target cells occupied or invalid.");
                    return;
                }
                object bonusTarget = BuildCommandTarget(netCmd);
                object placed = GameBridge.PlaceBuilding(builder, bonusTarget, buildingType, isBonus: true, instant: netCmd.InstantBuild, template: null);
                GameBridge.FlushStartFramePostMethods();
                ModLogger.Info($"[CommandExecutor] Bonus PlaceBuilding {netCmd.BuildingTypeName} at ({netCmd.TargetPosition.X:F1},{netCmd.TargetPosition.Y:F1}) -> {(placed != null ? "OK" : "NULL")}");
                GameBridge.ConsumeBonusEntityByType(buildingType);
                if (prevBonusCount == 1 && netCmd.PlayerId == CommandInterceptor.LocalPlayerId)
                    GameBridge.CancelPendingBonusCommandIfType(buildingType);
                return;
            }

            object buildCmd = GameBridge.GetBuildCommandForType(buildingType);
            if (buildCmd != null && !GameBridge.CanExecuteCommandOnEntity(buildCmd, builder))
            {
                ModLogger.Info($"[CommandExecutor] Rejected PlaceBuilding {netCmd.BuildingTypeName}: CanExecute=false (insufficient resources or prerequisites).");
                return;
            }

            if (!GameBridge.CanPlaceBuildingAtTarget(buildingType, netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin))
            {
                ModLogger.Info($"[CommandExecutor] Rejected PlaceBuilding {netCmd.BuildingTypeName} at ({netCmd.TargetPosition.X:F1},{netCmd.TargetPosition.Y:F1}): target cells occupied or invalid.");
                return;
            }

            object target = BuildCommandTarget(netCmd);
            if (buildCmd != null)
            {
                // Execute through Build.#=zfmgzkuw= so resource deduction and statistics are properly applied!
                GameBridge.ExecuteCommandOnEntity(buildCmd, builder, target);
            }
            else
            {
                GameBridge.PlaceBuilding(builder, target, buildingType, netCmd.IsBonusBuilding, netCmd.InstantBuild, null);
            }
            // Commit AddToScene() / replacement Destroy immediately so subsequent PlaceBuilding
            // commands in the same tick see occupied cells on DXWorldGrid.
            GameBridge.FlushStartFramePostMethods();
        }

        private static void ExecuteCancelCurrentOrder(NetCommand netCmd)
        {
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                object cmdComp = GameBridge.GetCommandableObject(DXGame.Current?.GetEntityByID(id));
                if (cmdComp != null) GameBridge.CancelCurrentOrderObject(cmdComp);
            }
        }

        private static void ExecuteCancelQueuedOrder(NetCommand netCmd)
        {
            foreach (ulong id in netCmd.ExecutorEntityIds)
            {
                object cmdComp = GameBridge.GetCommandableObject(DXGame.Current?.GetEntityByID(id));
                if (cmdComp != null) GameBridge.CancelQueueSlotObject(cmdComp, netCmd.QueueSlotIndex);
            }
        }

        private static bool IsLiveExecutableEntity(object entity)
        {
            var ent = entity as DXEntity; // DXEntity is public; safe to cast
            if (ent == null || ent.Disposed) return false;
            Type zxEntity = GameBridge.TypeZXEntity;
            return zxEntity == null || zxEntity.IsInstanceOfType(ent);
        }

        public static object BuildCommandTarget(NetCommand netCmd)
        {
            return GameBridge.CreateCommandTarget(
                netCmd.TargetPosition, netCmd.TargetSize, netCmd.TargetOrigin, netCmd.TargetEntityId);
        }
    }
}
