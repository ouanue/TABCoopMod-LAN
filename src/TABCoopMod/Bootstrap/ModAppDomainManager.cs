using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using TABCoopMod.Core;

namespace TABCoopMod.Bootstrap
{
    /// <summary>
    /// Non-invasive .NET AppDomainManager bootstrap.
    /// Loaded automatically by the CLR via TheyAreBillions.exe.config without touching TheyAreBillions.exe bytes!
    /// </summary>
    public sealed class ModAppDomainManager : AppDomainManager
    {
        private static bool _bootstrapped;

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            base.InitializeNewDomain(appDomainInfo);

            if (_bootstrapped) return;
            _bootstrapped = true;

            try
            {
                ModLogger.Initialize();
                ModLogger.Info("ModAppDomainManager.InitializeNewDomain triggered.");

                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    ModLogger.Error($"[AppDomain.UnhandledException] IsTerminating={args.IsTerminating}", args.ExceptionObject as Exception);
                };

                // Subscribe to AssemblyLoad so we initialize ModEntry AFTER the CLR loads TheyAreBillions.exe as the EntryAssembly!
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoaded;
            }
            catch (Exception ex)
            {
                ModLogger.Error("Fatal error in ModAppDomainManager.InitializeNewDomain", ex);
            }
        }

        private static void OnAssemblyLoaded(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                var loaded = args.LoadedAssembly;
                if (loaded != null && loaded.GetName().Name == "TheyAreBillions")
                {
                    AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoaded;
                    ModLogger.Info($"[AssemblyLoad] TheyAreBillions loaded: {loaded.FullName}");
                    ModLogger.LogGameVersionInfo(loaded);

                    // Ensure <Module>..cctor runs first so embedded DXVision / Steamworks.NET resolvers are active
                    RuntimeHelpers.RunModuleConstructor(loaded.ManifestModule.ModuleHandle);
                    ModLogger.Info("Executed <Module>..cctor on TheyAreBillions.exe.");

                    ModEntry.Initialize();
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("Exception in OnAssemblyLoaded", ex);
            }
        }
    }

    public static class ModEntry
    {
        private static bool _initialized;
        public static Harmony HarmonyInstance { get; private set; }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                ModLogger.Info("Initializing GameBridge bindings...");
                if (!GameBridge.InitializeAndValidate())
                {
                    ModLogger.Error("GameBridge validation failed! Aborting Harmony patching.");
                    return;
                }

                ModLogger.Info("Applying Harmony patches...");
                HarmonyInstance = new Harmony("com.tabcoopmod.archon");

                // Per-class patching: a single unpatcheable method (e.g. Eazfuscator-generated
                // exception blocks that break MonoMod's IL rewriter) must NOT abort the whole mod.
                int applied = 0, failed = 0;
                foreach (var type in typeof(ModEntry).Assembly.GetTypes())
                {
                    if (type.GetCustomAttributes(typeof(HarmonyLib.HarmonyPatch), true).Length == 0)
                        continue;

                    try
                    {
                        var processor = new HarmonyLib.PatchClassProcessor(HarmonyInstance, type);
                        var patchedMethods = processor.Patch();
                        applied++;
                        ModLogger.Info($"[Harmony] {type.Name}: patched {patchedMethods?.Count ?? 0} method(s).");
                    }
                    catch (Exception patchEx)
                    {
                        failed++;
                        ModLogger.Warn($"[Harmony] Skipped patch class {type.Name}: {patchEx.GetType().Name}: {patchEx.Message}");
                    }
                }

                ModLogger.Info($"All Harmony patches applied. Classes OK={applied}, failed-tolerant={failed}.");
            }
            catch (Exception ex)
            {
                ModLogger.Error("Exception in ModEntry.Initialize", ex);
            }
        }
    }
}
