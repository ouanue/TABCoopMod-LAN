using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Sync;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Hooks the survival random map generator to apply Co-op Archon balance scaling
    /// cleanly on Host creation without touching Singleplayer, Load Save, or Custom Maps,
    /// and fixes vanilla non-power-of-two distance clamps (Doom Villages / Ravens).
    /// </summary>
    [HarmonyPatch]
    public static class SurvivalBalancePatch
    {
        private static readonly MethodInfo MethodDistanceMaxAxis = AccessTools.Method(
            typeof(DXExtensions_Point),
            "DistanceMaxAxis",
            new[] { typeof(Point), typeof(Point) });

        private static readonly MethodInfo HelperDoomVillageMaxBase = AccessTools.Method(
            typeof(CoopBalanceManager),
            nameof(CoopBalanceManager.GetEffectiveDoomVillageMaxDistBase),
            new[] { typeof(int) });

        private static readonly MethodInfo HelperQuarterCells = AccessTools.Method(
            typeof(CoopBalanceManager),
            nameof(CoopBalanceManager.GetEffectiveQuarterNCells),
            new[] { typeof(int) });

        public static bool Prepare()
        {
            var m = GameBridge.GetMapGeneratorMethod();
            if (m == null)
            {
                ModLogger.Warn("[SurvivalBalancePatch] Random map generator method missing; patch skipped.");
                return false;
            }
            return true;
        }

        public static MethodBase TargetMethod()
        {
            return GameBridge.GetMapGeneratorMethod();
        }

        [HarmonyPrefix]
        public static void Prefix(object __0)
        {
            CoopBalanceManager.OnGeneratePrefix(__0);
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int site1Count = 0;
            int site2Count = 0;

            if (MethodDistanceMaxAxis != null && HelperDoomVillageMaxBase != null && HelperQuarterCells != null)
            {
                for (int i = 0; i < codes.Count; i++)
                {
                    if (!IsCallTo(codes[i], MethodDistanceMaxAxis))
                        continue;

                    if (i + 5 < codes.Count &&
                        codes[i + 1].IsLdloc() &&
                        codes[i + 2].opcode == OpCodes.Ldc_I4_2 &&
                        codes[i + 3].opcode == OpCodes.Div &&
                        IsLdcI4Value(codes[i + 4], 60) &&
                        codes[i + 5].opcode == OpCodes.Sub)
                    {
                        codes[i + 2] = ReplaceOpKeepingMeta(codes[i + 2], OpCodes.Call, HelperDoomVillageMaxBase);
                        codes[i + 3] = ReplaceOpKeepingMeta(codes[i + 3], OpCodes.Nop, null);
                        codes[i + 4] = ReplaceOpKeepingMeta(codes[i + 4], OpCodes.Nop, null);
                        codes[i + 5] = ReplaceOpKeepingMeta(codes[i + 5], OpCodes.Nop, null);
                        site1Count++;
                    }
                    else if (i + 3 < codes.Count &&
                             codes[i + 1].IsLdloc() &&
                             codes[i + 2].opcode == OpCodes.Ldc_I4_4 &&
                             codes[i + 3].opcode == OpCodes.Div)
                    {
                        codes[i + 2] = ReplaceOpKeepingMeta(codes[i + 2], OpCodes.Call, HelperQuarterCells);
                        codes[i + 3] = ReplaceOpKeepingMeta(codes[i + 3], OpCodes.Nop, null);
                        site2Count++;
                    }
                }
            }

            if (site1Count != 1 || site2Count != 1)
                ModLogger.Warn($"[SurvivalBalancePatch] MapGenerator transpiler matched unexpected sites: doom={site1Count}, raven={site2Count}");
            else
                ModLogger.Info("[SurvivalBalancePatch] Transpiled MapGenerator non-power-of-two bounds cleanly (doom=1, raven=1).");

            return codes;
        }

        [HarmonyPostfix]
        public static void Postfix(DXLevel __result, object __0)
        {
            CoopBalanceManager.OnGeneratePostfix(__result, __0);
        }

        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception)
        {
            CoopBalanceManager.OnGenerateFinalizer();
            return __exception;
        }

        private static bool IsCallTo(CodeInstruction inst, MethodInfo target)
        {
            return (inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) &&
                   Equals(inst.operand, target);
        }

        private static bool IsLdcI4Value(CodeInstruction inst, int expected)
        {
            if (inst.opcode == OpCodes.Ldc_I4_S && inst.operand is sbyte sb)
                return sb == expected;
            if (inst.opcode == OpCodes.Ldc_I4 && inst.operand is int v)
                return v == expected;
            return false;
        }

        private static CodeInstruction ReplaceOpKeepingMeta(CodeInstruction orig, OpCode opcode, object operand)
        {
            return new CodeInstruction(opcode, operand)
            {
                labels = orig.labels,
                blocks = orig.blocks
            };
        }
    }
}
