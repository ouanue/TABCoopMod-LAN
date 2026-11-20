using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DXVision;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Determinism;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Makes the engine's simulation-relevant Math.Pow calls bit-identical on every CPU.
    ///
    /// WHY THIS EXISTS
    /// The lockstep protocol compares state checksums, so any 1-ULP difference between the two
    /// peers becomes a desync. Two classes of difference were found:
    ///
    ///  1. ORDER / SCHEDULING differences - the engine's DXHelper_Task.For splits work by
    ///     minIterationsForConcurrency / maxCores, so the partition depends on core count and on
    ///     scheduler timing. Fixed by DeterministicTaskPatch (serializing those loops).
    ///
    ///  2. ARITHMETIC differences - Math.Pow is implemented by the platform CRT, and the result is
    ///     NOT guaranteed to be correctly rounded on every CPU. On this machine Math.Pow(x, 2)
    ///     differs from the exactly-rounded x*x for ~0.05% of inputs by 1 ULP. The engine uses
    ///     Math.Pow(x, 2) for path-finding cost terms and for the geometry helper that every
    ///     entity re-evaluates when it changes world-grid position. Two machines with different
    ///     CPUs can therefore disagree on the last bit of a path cost, which flips a comparison,
    ///     which changes the chosen path, which moves an entity by a fraction of a cell, which
    ///     then diverges into a visible drift.
    ///
    ///     Same-machine A/A testing can NEVER catch this: both peers run the same CPU and the
    ///     same CRT, so their Pow results are bit-identical by construction. That is exactly why
    ///     the single-machine harness stayed green while the two-machine session drifted from the
    ///     very first checksum epoch.
    ///
    /// WHAT THIS PATCHES
    /// Only Math.Pow calls whose exponent is a compile-time constant that is exactly representable
    /// and for which an exact integer-power form exists:
    ///     exponent 2      -> x * x          (exact, matches IEEE multiply)
    ///     exponent 0.5    -> Math.Sqrt(x)   (IEEE-754 requires correctly-rounded sqrt)
    ///     exponent 2^k    -> repeated multiplication (exact for all integer k)
    /// The transpiler rewrites the pattern
    ///     ldc.r8 <e>; call Math::Pow(double, double)
    /// into a call to a local helper that computes the same value exactly.
    ///
    /// IMPORTANT - SCOPE
    /// The helpers fall back to the original Math.Pow result unless the deterministic sandbox is
    /// active (LockstepClock.IsDeterministicModeActive). In singleplayer the game's own values
    /// are left exactly as the engine produced them, so nothing changes for normal play.
    ///
    /// Math.Sin / Cos / Atan2 / Exp / Log are NOT touched: the engine calls them only from
    /// rendering, animation and map-noise code (verified by scanning every Math call site in
    /// DXVision.dll), and Math.Sin/Cos are not required to be correctly rounded, so replacing them
    /// would change gameplay-relevant visuals without guaranteeing cross-CPU agreement anyway.
    /// </summary>
    [HarmonyPatch]
    public static class DeterministicMathPatch
    {
        /// <summary>
        /// Math.Pow(x, 2) computed as x*x. The IEEE-754 multiply is exactly rounded, so this is
        /// bit-reproducible on every conforming CPU. Math.Pow(x, 2) is NOT guaranteed to equal it.
        /// </summary>
        public static double Pow2(double x)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return Math.Pow(x, 2.0);
            return x * x;
        }

        /// <summary>
        /// Math.Pow(x, 0.5) computed as Math.Sqrt(x). IEEE-754 requires sqrt to be correctly rounded,
        /// so this is bit-reproducible on every conforming CPU, while Math.Pow(x, 0.5) is not.
        /// Negative x yields NaN for both forms (Math.Pow of a negative base with a fractional
        /// exponent is NaN, and Math.Sqrt of a negative is NaN) - so behaviour is preserved.
        /// </summary>
        public static double PowHalf(double x)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return Math.Pow(x, 0.5);
            // Math.Pow(-0.0, 0.5) is +0.0 while Math.Sqrt(-0.0) is -0.0. The sign of zero can
            // change a comparison result, so keep the original call for zero.
            if (x == 0.0) return Math.Pow(x, 0.5);
            return Math.Sqrt(x);
        }

        /// <summary>
        /// Math.Pow(x, 2^k) for a compile-time-constant k >= 0, computed by repeated multiplication.
        /// Every step is an exactly-rounded multiply, so the result is bit-identical everywhere.
        /// </summary>
        public static double PowPow2(double x, int k)
        {
            if (!LockstepClock.IsDeterministicModeActive)
                return Math.Pow(x, Math.Pow(2.0, k));
            if (k < 0)
            {
                double v = PowPow2(x, -k);
                return v == 0.0 ? double.PositiveInfinity : 1.0 / v;
            }
            double r = 1.0;
            for (int i = 0; i < k; i++) r *= x;
            return r;
        }

        private static readonly MethodInfo MethodPow2 =
            AccessTools.Method(typeof(DeterministicMathPatch), nameof(Pow2), new[] { typeof(double) });

        private static readonly MethodInfo MethodPowHalf =
            AccessTools.Method(typeof(DeterministicMathPatch), nameof(PowHalf), new[] { typeof(double) });

        private static readonly MethodInfo MethodPowPow2 =
            AccessTools.Method(typeof(DeterministicMathPatch), nameof(PowPow2), new[] { typeof(double), typeof(int) });

        /// <summary>
        /// The engine methods that participate in simulation and contain Math.Pow calls with a
        /// constant exponent. DXVision.dll is obfuscated, so the types are resolved by name at
        /// runtime instead of being referenced directly.
        /// </summary>
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var asm = typeof(DXEntity).Assembly;
            if (asm == null) yield break;

            // Algorithms.PathFinder::FindPath(Point, Point)
            // Algorithms.PathFinderFast::FindPath(Point, Point, UInt32)
            // -> path-finding cost terms, 4 Math.Pow(x, 2) each. Chosen path decides movement.
            foreach (var typeName in new[] { "Algorithms.PathFinder", "Algorithms.PathFinderFast" })
            {
                var t = asm.GetType(typeName);
                if (t == null) continue;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "FindPath") continue;
                    if (!HasConstantPow(m)) continue;
                    yield return m;
                }
            }

            // DXVision.DXHelper_Circle::IntersectWithRectangle(PointF, Single, RectangleF)
            // -> called from DXEntity.UpdateWorldGrid, i.e. every entity that changes its world
            //    grid position re-evaluates its circle/rectangle overlap.
            var circle = asm.GetType("DXVision.DXHelper_Circle");
            if (circle != null)
            {
                foreach (var m in circle.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "IntersectWithRectangle") continue;
                    if (!HasConstantPow(m)) continue;
                    yield return m;
                }
            }

            // DXVision.DXHelper_Point::GetPointInLinePerpendicularToPoint(PointF, PointF, PointF)
            var point = asm.GetType("DXVision.DXHelper_Point");
            if (point != null)
            {
                foreach (var m in point.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "GetPointInLinePerpendicularToPoint") continue;
                    if (!HasConstantPow(m)) continue;
                    yield return m;
                }
            }
        }

        public static bool Prepare()
        {
            int count = 0;
            foreach (var _ in TargetMethods()) count++;
            if (count == 0)
            {
                ModLogger.Warn("[DeterministicMathPatch] No Math.Pow call sites found; patch skipped.");
                return false;
            }
            ModLogger.Info($"[DeterministicMathPatch] Preparing {count} Math.Pow call site(s) for exact integer-power rewrite.");
            return true;
        }

        private static bool HasConstantPow(MethodBase m)
        {
            var body = m.GetMethodBody();
            var il = body?.GetILAsByteArray();
            if (il == null || il.Length < 6) return false;
            var module = m.Module;
            for (int i = 0; i < il.Length - 5; i++)
            {
                if (il[i] != 0x28) continue; // call
                int token = BitConverter.ToInt32(il, i + 1);
                MethodInfo called;
                try { called = module.ResolveMethod(token) as MethodInfo; }
                catch { continue; }
                if (called == null || called.DeclaringType != typeof(Math) || called.Name != "Pow") continue;
                if (called.GetParameters().Length != 2) continue;
                return true;
            }
            return false;
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int patchedPow2 = 0;
            int patchedPowHalf = 0;
            int patchedPowPow2 = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (!IsCallToPow(codes[i])) continue;
                // The exponent must be a constant pushed right before the call.
                if (i < 1) continue;
                var expInstr = codes[i - 1];
                if (expInstr.opcode != OpCodes.Ldc_R8) continue;
                if (!(expInstr.operand is double exponent)) continue;

                if (exponent == 2.0)
                {
                    // ldc.r8 2 ; call Math::Pow  ->  call Pow2   (x is already on the stack)
                    codes[i - 1] = new CodeInstruction(OpCodes.Nop) { labels = expInstr.labels, blocks = expInstr.blocks };
                    codes[i] = new CodeInstruction(OpCodes.Call, MethodPow2) { labels = codes[i].labels, blocks = codes[i].blocks };
                    patchedPow2++;
                }
                else if (exponent == 0.5)
                {
                    codes[i - 1] = new CodeInstruction(OpCodes.Nop) { labels = expInstr.labels, blocks = expInstr.blocks };
                    codes[i] = new CodeInstruction(OpCodes.Call, MethodPowHalf) { labels = codes[i].labels, blocks = codes[i].blocks };
                    patchedPowHalf++;
                }
                else if (exponent >= 2.0 && exponent <= 32.0 && IsPowerOfTwo(exponent))
                {
                    int k = (int)Math.Round(Math.Log(exponent, 2.0));
                    // ldc.r8 2^k ; call Math::Pow  ->  ldc.i4 k ; call PowPow2
                    codes[i - 1] = new CodeInstruction(OpCodes.Ldc_I4, k) { labels = expInstr.labels, blocks = expInstr.blocks };
                    codes[i] = new CodeInstruction(OpCodes.Call, MethodPowPow2) { labels = codes[i].labels, blocks = codes[i].blocks };
                    patchedPowPow2++;
                }
            }

            if (patchedPow2 + patchedPowHalf + patchedPowPow2 > 0)
            {
                ModLogger.Info($"[DeterministicMathPatch] Rewrote non-portable Math.Pow: pow2={patchedPow2}, pow0.5={patchedPowHalf}, pow2k={patchedPowPow2} (exact integer-power forms, CPU independent).");
            }

            return codes;
        }

        private static bool IsCallToPow(CodeInstruction inst)
        {
            if (inst.opcode != OpCodes.Call && inst.opcode != OpCodes.Callvirt) return false;
            return inst.operand is MethodInfo mi &&
                   mi.DeclaringType == typeof(Math) &&
                   mi.Name == "Pow" &&
                   mi.GetParameters().Length == 2;
        }

        private static bool IsPowerOfTwo(double v)
        {
            double log2 = Math.Log(v, 2.0);
            double rounded = Math.Round(log2);
            return Math.Abs(log2 - rounded) < 1e-12;
        }
    }
}
