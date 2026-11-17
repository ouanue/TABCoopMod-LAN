using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using DXVision;
using TABCoopMod.Commands;
using TABCoopMod.Core;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Drives gameplay commands from a declarative script instead of human clicks.
    /// Every operation resolves entity IDs by scanning the level (sorted by ID) and is replayed
    /// through CommandExecutor — it never reads or mutates the local CSelectable selection list.
    /// </summary>
    public sealed class ScriptedCommandDriver
    {
        public sealed class Operation
        {
            public int SimTick;
            public string Label;
            public Func<NetCommand> Build;
        }

        private readonly List<Operation> _ops = new List<Operation>();
        private int _nextIndex;

        public int Count => _ops.Count;
        public int ExecutedCount { get; private set; }

        public void Add(Operation op)
        {
            if (op != null) _ops.Add(op);
        }

        public void Sort() => _ops.Sort((a, b) => a.SimTick.CompareTo(b.SimTick));

        /// <summary>Fires every operation whose tick is due (called from the logic-tick prefix).</summary>
        public void OnBeforeLogicTick(int nextSimTick)
        {
            while (_nextIndex < _ops.Count && _ops[_nextIndex].SimTick <= nextSimTick)
            {
                var op = _ops[_nextIndex++];
                NetCommand cmd = null;
                try { cmd = op.Build?.Invoke(); }
                catch (Exception ex) { ModLogger.Error($"[ScriptDriver] build failed for '{op.Label}'", ex); }

                if (cmd == null)
                {
                    ModLogger.Warn($"[ScriptDriver] SKIP '{op.Label}' at tick {nextSimTick} (could not resolve entities)");
                    continue;
                }

                cmd.TargetSimTick = nextSimTick;
                try
                {
                    CommandExecutor.Execute(cmd);
                    ExecutedCount++;
                    ModLogger.Info($"[ScriptDriver] tick {nextSimTick} inject '{op.Label}': {cmd}");
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[ScriptDriver] inject failed for '{op.Label}'", ex);
                }
            }
        }

        // ====================================================================
        // Scenario construction
        // ====================================================================

        /// <summary>
        /// Default scenario relative to the level's first logic tick:
        /// +10 place a TentHouse, +30 move up to 4 army units, +60 train a Ranger.
        /// Operations that cannot resolve their entities are logged SKIP and safely ignored.
        /// </summary>
        public static ScriptedCommandDriver CreateDefault(int baseTick)
        {
            var driver = new ScriptedCommandDriver();
            driver.Add(new Operation { SimTick = baseTick + 10, Label = "build_tent", Build = () => BuildTent() });
            driver.Add(new Operation { SimTick = baseTick + 30, Label = "move_units", Build = () => MoveUnits(4) });
            driver.Add(new Operation { SimTick = baseTick + 60, Label = "train_ranger", Build = () => TrainUnit("ZX.Entities.Ranger") });
            return driver;
        }

        public static ScriptedCommandDriver FromFile(string path)
        {
            var driver = new ScriptedCommandDriver();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return driver;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                var kv = ParseKeyValues(line);
                if (!kv.TryGetValue("tick", out string tickStr) || !int.TryParse(tickStr, out int tick)) continue;
                kv.TryGetValue("op", out string opKind);

                switch ((opKind ?? string.Empty).ToLowerInvariant())
                {
                    case "build_tent":
                        driver.Add(new Operation { SimTick = tick, Label = line, Build = () => BuildTent() });
                        break;
                    case "move":
                        int count = kv.TryGetValue("count", out string c) && int.TryParse(c, out int n) ? n : 4;
                        driver.Add(new Operation { SimTick = tick, Label = line, Build = () => MoveUnits(count) });
                        break;
                    case "train":
                        string typeName = kv.TryGetValue("type", out string t) ? t : "ZX.Entities.Ranger";
                        driver.Add(new Operation { SimTick = tick, Label = line, Build = () => TrainUnit(typeName) });
                        break;
                    case "cancel":
                        driver.Add(new Operation { SimTick = tick, Label = line, Build = () => CancelFirstUnit() });
                        break;
                    case "pause":
                        bool state = kv.TryGetValue("state", out string s) && s == "1";
                        driver.Add(new Operation { SimTick = tick, Label = line, Build = () => SetPaused(state) });
                        break;
                }
            }
            driver.Sort();
            return driver;
        }

        // ====================================================================
        // Command builders (deterministic: IDs resolved by sorted scan)
        // ====================================================================

        private static NetCommand BuildTent()
        {
            var cc = GameBridge.GetCommandCenterEntity();
            if (cc == null) return null;

            Point cell = DXWorldGrid.CellFromPoint(cc.Position);
            Point targetCell = new Point(cell.X + 3, cell.Y + 3);
            PointF center = DXWorldGrid.PointFromCell_Center(targetCell);

            return new NetCommand
            {
                Type = NetCommandType.PlaceBuilding,
                PlayerId = 1,
                BuildingTypeName = "ZX.Entities.TentHouse",
                ExecutorEntityIds = new[] { cc.ID },
                TargetPosition = center,
                TargetSize = new SizeF(1f, 1f),
                TargetOrigin = center
            };
        }

        private static NetCommand MoveUnits(int count)
        {
            var units = GameBridge.GetPlayerEntitiesByTypeName("Ranger", count);
            if (units.Count == 0) units = GameBridge.GetPlayerEntitiesByTypeName("HumanArmyUnit", count);
            if (units.Count == 0)
            {
                // Fallback: any player entity that can move and be commanded.
                units = GameBridge.GetPlayerEntities()
                    .Where(e => GameBridge.HasComponent(e, GameBridge.TypeCMovable) &&
                                GameBridge.HasComponent(e, GameBridge.TypeCCommandable))
                    .Take(Math.Max(1, count))
                    .ToList();
            }
            if (units.Count == 0) return null;

            Type travelType = GameBridge.GameAssembly?.GetType("ZX.Commands.Travel");
            object travel = travelType != null ? GameBridge.GetCommandGeneric(travelType) : null;
            if (travel == null)
            {
                ModLogger.Warn("[ScriptDriver] Travel command singleton unresolved");
                return null;
            }

            float sx = 0f, sy = 0f;
            foreach (var u in units) { sx += u.Position.X; sy += u.Position.Y; }
            var destination = new PointF(sx / units.Count + 4f, sy / units.Count);

            return new NetCommand
            {
                Type = NetCommandType.GroupMoveOrPatrol,
                PlayerId = 1,
                CommandId = GameBridge.GetCommandId(travel),
                ExecutorEntityIds = units.Select(u => u.ID).ToArray(),
                TargetPosition = destination,
                IsShiftQueued = false
            };
        }

        private static NetCommand TrainUnit(string unitTypeName)
        {
            var barracks = GameBridge.GetPlayerEntitiesByTypeName("SoldiersCenter", 1);
            if (barracks.Count == 0) barracks = GameBridge.GetPlayerEntitiesByTypeName("UnitCenter", 1);
            if (barracks.Count == 0) return null;

            Type unitType = GameBridge.GameAssembly?.GetType(unitTypeName);
            if (unitType == null) return null;

            object trainCmd = GameBridge.GetTrainCommandForType(unitType);
            if (trainCmd == null) return null;

            return new NetCommand
            {
                Type = NetCommandType.ExecuteCommand,
                PlayerId = 1,
                CommandId = GameBridge.GetCommandId(trainCmd),
                ExecutorEntityIds = new[] { barracks[0].ID }
            };
        }

        private static NetCommand CancelFirstUnit()
        {
            var unit = GameBridge.GetPlayerEntities().FirstOrDefault(e => GameBridge.HasComponent(e, GameBridge.TypeCCommandable));
            if (unit == null) return null;
            return new NetCommand
            {
                Type = NetCommandType.CancelCurrentOrder,
                PlayerId = 1,
                ExecutorEntityIds = new[] { unit.ID }
            };
        }

        private static NetCommand SetPaused(bool paused)
        {
            return new NetCommand
            {
                Type = NetCommandType.SetPaused,
                PlayerId = 1,
                TargetPausedState = paused
            };
        }

        private static Dictionary<string, string> ParseKeyValues(string line)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string part in line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq > 0) d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return d;
        }
    }
}
