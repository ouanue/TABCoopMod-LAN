using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using DXVision;
using HarmonyLib;
using TABCoopMod.Commands;
using TABCoopMod.Core;
using TABCoopMod.Determinism;
using TABCoopMod.Patches;
using TABCoopMod.Sync;

namespace TABCoopMod.Testing
{
    /// <summary>
    /// Wires the test harness into the game loop: optional auto-level-load, F8 single-player
    /// interception toggle, and per-tick hooks for the deterministic replay verifier.
    /// All behaviour is gated on TestHarnessConfig / explicit key press; normal play is unaffected.
    /// </summary>
    [HarmonyPatch]
    public static class TestingLifecyclePatches
    {
        private static bool _autoLoadIssued;
        private static bool _autoLoadKickFade;
        private static double _autoLoadStartTicks = -1.0;
        private static bool _f8Latch;
        private static bool _armIssued;
        private static bool _roleApplied;
        private static bool _menuReady;
        [ThreadStatic] private static Stopwatch _initLevelSw;
        [ThreadStatic] private static Stopwatch _readSaveSw;

        [HarmonyPatch(typeof(DXGame), "StartFrame")]
        [HarmonyPostfix]
        public static void StartFrame_Postfix()
        {
            try
            {
                TestHarnessConfig.Initialize();

                if (TestHarnessConfig.Enabled && !_armIssued)
                {
                    _armIssued = true;
                    DeterministicReplayVerifier.Begin();
                }

                DeterministicReplayVerifier.OnRenderFrame();
                ControlServer.Tick();
                PauseSyncController.Tick();
                HandleSinglePlayerToggle();
                TryApplyRole();
                TryAutoLoad();
                TickAutoLoadFade();
            }
            catch (Exception ex)
            {
                ModLogger.Error("[TestingLifecyclePatches] StartFrame hook failed", ex);
            }
        }

        [HarmonyPatch]
        public static class Patch_LevelSys_InitLevel_TestHooks
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_InitLevel);
            }

            [HarmonyPrefix]
            public static void Prefix()
            {
                _initLevelSw = Stopwatch.StartNew();
                ModLogger.Info("[LoadTiming] InitLevel started...");
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                try
                {
                    if (_initLevelSw != null)
                    {
                        _initLevelSw.Stop();
                        ModLogger.Info($"[LoadTiming] InitLevel finished in {_initLevelSw.ElapsedMilliseconds} ms.");
                        _initLevelSw = null;
                    }

                    if (TestHarnessConfig.Intercept)
                    {
                        if (CommandInterceptor.OnLocalCommandCaptured == null)
                            CommandInterceptor.OnLocalCommandCaptured = cmd => CommandExecutor.Execute(cmd);
                        CommandInterceptor.IsInterceptionEnabled = true;
                        ModLogger.Info("[TestHarness] Interception auto-enabled at level init.");
                    }

                    if (TestHarnessConfig.Enabled)
                        DeterministicReplayVerifier.OnLevelInitialized();
                }
                catch (Exception ex)
                {
                    ModLogger.Error("[TestingLifecyclePatches] InitLevel hook failed", ex);
                }
            }
        }

        [HarmonyPatch]
        public static class Patch_ZXGame_ReadSaveFile_Timing
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ReadSaveFile, new[] { typeof(string) });
            }

            [HarmonyPrefix]
            public static void Prefix(string __0)
            {
                _readSaveSw = Stopwatch.StartNew();
                ModLogger.Info($"[LoadTiming] ReadSaveFile started: {__0}");
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                if (_readSaveSw != null)
                {
                    _readSaveSw.Stop();
                    ModLogger.Info($"[LoadTiming] ReadSaveFile finished in {_readSaveSw.ElapsedMilliseconds} ms.");
                    _readSaveSw = null;
                }
            }
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPrefix]
        public static void UpdateGameLogic_Prefix_Verifier(DXGame __instance)
        {
            if (!TestHarnessConfig.Enabled) return;
            DeterministicReplayVerifier.OnBeforeLogicTick(__instance?.IAIterationInt ?? 0);
        }

        [HarmonyPatch(typeof(DXGame), "UpdateGameLogic")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        public static void UpdateGameLogic_Postfix_Verifier(DXGame __instance)
        {
            if (!TestHarnessConfig.Enabled) return;
            DeterministicReplayVerifier.OnAfterLogicTick(__instance?.IAIterationInt ?? 0);
        }

        private static void HandleSinglePlayerToggle()
        {
            if (!TestHarnessConfig.Enabled) return; // avoid surprising normal players with a hotkey
            var input = DXInputState.Current;
            if (input == null) return;

            bool pressed = input.IsKeyPressed(DXKeys.F8, false);
            if (pressed && !_f8Latch)
            {
                // Never fight a live network session: only toggle the single-player loop.
                if (!CoopSessionManager.IsSessionActive)
                {
                    if (CommandInterceptor.OnLocalCommandCaptured == null)
                        CommandInterceptor.OnLocalCommandCaptured = cmd => CommandExecutor.Execute(cmd);

                    CommandInterceptor.IsInterceptionEnabled = !CommandInterceptor.IsInterceptionEnabled;
                    ModLogger.Info($"[TestHarness] F8 -> single-player interception = {CommandInterceptor.IsInterceptionEnabled}");
                }
            }
            _f8Latch = pressed;
        }

        private static void TryApplyRole()
        {
            if (_roleApplied) return;
            string role = TestHarnessConfig.Role;
            if (string.IsNullOrEmpty(role)) return;
            if (CoopSessionManager.IsSessionActive) return;

            if (string.Equals(role, "host", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (TestHarnessConfig.LanMode)
                    {
                        ModLogger.Info("[TestHarness] Auto-starting LAN HOST session.");
                        CoopSessionManager.StartHostSessionLan();
                    }
                    else
                    {
                        ModLogger.Info("[TestHarness] Auto-starting loopback HOST session.");
                        CoopSessionManager.StartHostSession(useLoopback: true);
                    }
                    _roleApplied = true;
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[TestHarness] Host start failed, will retry: {ex.Message}");
                }
            }
            else if (string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
            {
                // Host may not be listening yet; retry each frame until the blocking connect succeeds.
                try
                {
                    if (TestHarnessConfig.LanMode)
                    {
                        string target = string.IsNullOrEmpty(TestHarnessConfig.LanHost)
                            ? "127.0.0.1"
                            : TestHarnessConfig.LanHost;
                        ModLogger.Info($"[TestHarness] Auto-joining LAN session as CLIENT -> {target}.");
                        CoopSessionManager.JoinSessionLan(target);
                    }
                    else
                    {
                        ModLogger.Info("[TestHarness] Auto-joining loopback session as CLIENT.");
                        CoopSessionManager.JoinSession(useLoopback: true, targetAddressOrSteamId: "127.0.0.1:27099");
                    }
                    _roleApplied = true;
                }
                catch (Exception ex)
                {
                    ModLogger.Warn($"[TestHarness] Client join failed, will retry: {ex.Message}");
                }
            }
        }

        private static void TryAutoLoad()
        {
            if (_autoLoadIssued) return;
            // Auto-load is used both by the replay verifier (TABCOOP_TEST) and by the MCP control
            // server (TABCOOP_CONTROL_PORT). In control-only mode the verifier stays disabled.
            if (!TestHarnessConfig.Enabled && !TestHarnessConfig.ControlEnabled) return;
            if (string.IsNullOrEmpty(TestHarnessConfig.AutoLoadSave)) return;
            // The Host must be able to load the level after the session started: its load triggers
            // the snapshot broadcast to peers. Clients never auto-load (they receive the snapshot).
            if (CoopSessionManager.IsSessionActive &&
                CoopSessionManager.Transport != null && !CoopSessionManager.Transport.IsHost)
                return;
            if (!_menuReady) return; // wait until MainMenu.#=zwK_pUIeRBpbp (main-thread init) completes
            if (!GameBridge.IsMainMenuReadyForInput() || GameBridge.IsGameLoading() || GameBridge.IsStartupFadeActive())
                return;

            if (GameBridge.CurrentLevelState != null) return;
            if (DXGame.Current == null) return;

            _autoLoadIssued = true;
            _autoLoadKickFade = true;
            _autoLoadStartTicks = DXHelper_Environment.Ticks;
            string save = TestHarnessConfig.AutoLoadSave;
            ModLogger.Info($"[TestHarness] Auto-loading save: {save}");
            DXGame.Current.InvokeOnStartFrame(() =>
            {
                try
                {
                    DeterministicReplayVerifier.PrepareForLoad();
                    GameBridge.PrepareForProgrammaticLoad();
                    GameBridge.LoadSaveFileAndEnter(save);
                }
                catch (Exception ex)
                {
                    ModLogger.Error($"[TestHarness] Auto-load failed: {save}", ex);
                    _autoLoadIssued = false; // allow a retry next frame
                    _autoLoadKickFade = false;
                }
            });
        }

        private static void TickAutoLoadFade()
        {
            if (!_autoLoadKickFade) return;
            if (GameBridge.CurrentLevelState != null)
            {
                _autoLoadKickFade = false;
                return;
            }
            GameBridge.KickLoadingFade(700.0);
            if (_autoLoadStartTicks > 0 && DXHelper_Environment.Ticks - _autoLoadStartTicks > 60000.0)
                _autoLoadKickFade = false;
        }

        [HarmonyPatch]
        public static class Patch_MainMenu_OnLoad_TestHook
        {
            public static MethodBase TargetMethod()
            {
                return AccessTools.Method(GameBridge.TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnLoadComplete, Type.EmptyTypes);
            }

            [HarmonyPostfix]
            public static void Postfix()
            {
                _menuReady = true;
                ModLogger.Info("[TestHarness] MainMenu main-thread setup completed (ready for auto-load).");
            }
        }
    }

    /// <summary>
    /// Allows the game to initialize and run cleanly on virtual display adapters (e.g., GameViewer IDDCx / Parsec)
    /// when no physical monitor is attached to the primary GPU, by falling back from legacy Direct3D9 to Direct3DEx
    /// and mapping Pool.Managed textures to Pool.Default + Usage.Dynamic when the fallback is active.
    /// </summary>
    [HarmonyPatch]
    internal static class Patch_DXRender_VirtualDisplayFallback
    {
        public static bool IsDirect3DExFallback;

        private static readonly ConstructorInfo Direct3DCtor =
            AccessTools.Constructor(typeof(SlimDX.Direct3D9.Direct3D), Type.EmptyTypes);

        private static readonly ConstructorInfo TextureCtor7 =
            AccessTools.Constructor(typeof(SlimDX.Direct3D9.Texture), new[]
            {
                typeof(SlimDX.Direct3D9.Device),
                typeof(int),
                typeof(int),
                typeof(int),
                typeof(SlimDX.Direct3D9.Usage),
                typeof(SlimDX.Direct3D9.Format),
                typeof(SlimDX.Direct3D9.Pool)
            });

        private static readonly MethodInfo TextureFromFile2 =
            AccessTools.Method(typeof(SlimDX.Direct3D9.Texture), nameof(SlimDX.Direct3D9.Texture.FromFile), new[]
            {
                typeof(SlimDX.Direct3D9.Device),
                typeof(string)
            });

        private static readonly MethodInfo HelperCreateDirect3D =
            AccessTools.Method(typeof(Patch_DXRender_VirtualDisplayFallback), nameof(CreateDirect3DWithFallback));

        private static readonly MethodInfo HelperCreateTextureCompat =
            AccessTools.Method(typeof(Patch_DXRender_VirtualDisplayFallback), nameof(CreateTextureCompat));

        private static readonly MethodInfo HelperTextureFromFileCompat =
            AccessTools.Method(typeof(Patch_DXRender_VirtualDisplayFallback), nameof(TextureFromFileCompat));

        [HarmonyTargetMethods]
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var renderType = typeof(DXPlatform_Desktop.DXRender_SlimDX);
            string[] methodNames =
            {
                "D3DCreateDevice",
                "TexturaFromImage",
                "TexturaDeBitmapARGB4",
                "TexturaDeBitmapARGB4Old",
                "TexturaDeBitmap8BitsAlpha",
                "TexturaDeBitmap8BitsAlphaOld",
                "TexturaDeBitmapA1RGB5",
                "TexturaDeBitmapA4L4",
                "TexturaDeBitmapA4L4Old"
            };

            foreach (string name in methodNames)
            {
                var m = AccessTools.Method(renderType, name);
                if (m != null)
                    yield return m;
                else
                    ModLogger.Warn($"[D3D9Fallback] Could not find method DXRender_SlimDX.{name}");
            }
        }

        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                if (inst.opcode == OpCodes.Newobj && Direct3DCtor != null && Equals(inst.operand, Direct3DCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, HelperCreateDirect3D)
                    {
                        labels = inst.labels,
                        blocks = inst.blocks
                    };
                }
                else if (inst.opcode == OpCodes.Newobj && TextureCtor7 != null && Equals(inst.operand, TextureCtor7))
                {
                    yield return new CodeInstruction(OpCodes.Call, HelperCreateTextureCompat)
                    {
                        labels = inst.labels,
                        blocks = inst.blocks
                    };
                }
                else if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) && TextureFromFile2 != null && Equals(inst.operand, TextureFromFile2))
                {
                    yield return new CodeInstruction(OpCodes.Call, HelperTextureFromFileCompat)
                    {
                        labels = inst.labels,
                        blocks = inst.blocks
                    };
                }
                else
                {
                    yield return inst;
                }
            }
        }

        public static SlimDX.Direct3D9.Direct3D CreateDirect3DWithFallback()
        {
            var d3d = new SlimDX.Direct3D9.Direct3D();
            try
            {
                d3d.GetDeviceCaps(0, SlimDX.Direct3D9.DeviceType.Hardware);
                IsDirect3DExFallback = false;
                return d3d;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[D3D9Fallback] Legacy Direct3D.GetDeviceCaps(0, Hardware) failed ({ex.Message}); falling back to Direct3DEx for virtual display adapter.");
                try { d3d.Dispose(); } catch { }
                IsDirect3DExFallback = true;
                return new SlimDX.Direct3D9.Direct3DEx();
            }
        }

        public static SlimDX.Direct3D9.Texture CreateTextureCompat(
            SlimDX.Direct3D9.Device device, int width, int height, int levelCount,
            SlimDX.Direct3D9.Usage usage, SlimDX.Direct3D9.Format format, SlimDX.Direct3D9.Pool pool)
        {
            if (IsDirect3DExFallback && pool == SlimDX.Direct3D9.Pool.Managed)
            {
                pool = SlimDX.Direct3D9.Pool.Default;
                usage |= SlimDX.Direct3D9.Usage.Dynamic;
            }
            return new SlimDX.Direct3D9.Texture(device, width, height, levelCount, usage, format, pool);
        }

        public static SlimDX.Direct3D9.Texture TextureFromFileCompat(SlimDX.Direct3D9.Device device, string fileName)
        {
            if (IsDirect3DExFallback)
            {
                return SlimDX.Direct3D9.Texture.FromFile(
                    device, fileName, -1, -1, -1,
                    SlimDX.Direct3D9.Usage.None, SlimDX.Direct3D9.Format.Unknown,
                    SlimDX.Direct3D9.Pool.Default, SlimDX.Direct3D9.Filter.Default,
                    SlimDX.Direct3D9.Filter.Default, 0);
            }
            return SlimDX.Direct3D9.Texture.FromFile(device, fileName);
        }
    }
}
