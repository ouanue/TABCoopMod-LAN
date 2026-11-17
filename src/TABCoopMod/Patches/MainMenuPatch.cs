using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using TABCoopMod.Core;
using TABCoopMod.Networking;
using TABCoopMod.Sync;
using TABCoopMod.Testing;
using TABCoopMod.UI;
using ZX.GUI;

namespace TABCoopMod.Patches
{
    /// <summary>
    /// Hooks ZXSystemMainMenu.OnLoad (#=zLs$VX1eoAapVflmW_w==.OnLoad) to inject the
    /// "CO-OP MULTIPLAYER" button into the main menu button layout.
    /// </summary>
    [HarmonyPatch]
    public static class MainMenuPatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXSystemMainMenu, "OnLoad", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void OnLoad_Postfix(object __instance)
        {
            try
            {
                ModLogger.Info("[MainMenuPatch] ZXSystemMainMenu.OnLoad Postfix triggered.");

                var mainMenuType = __instance.GetType();
                var dictField = AccessTools.Field(mainMenuType, ObfuscatedSymbols.Field_MainMenu_CategoryButtonsDict);
                var gridField = AccessTools.Field(mainMenuType, ObfuscatedSymbols.Field_MainMenu_GridLayout);
                var switchCatMethod = AccessTools.Method(mainMenuType, ObfuscatedSymbols.Method_MainMenu_SwitchCategory);
                var catEnumType = mainMenuType.GetNestedType(ObfuscatedSymbols.NestedEnum_MainMenu_TMenuCategory, BindingFlags.NonPublic | BindingFlags.Public);

                if (dictField == null || gridField == null || switchCatMethod == null || catEnumType == null)
                {
                    ModLogger.Error("[MainMenuPatch] Could not resolve main menu internal fields.");
                    return;
                }

                // Create the native-styled ZXButtonLink via reflection (since ZXButtonLink is internal in TheyAreBillions.exe)
                var coopButton = (DXTextButton)Activator.CreateInstance(
                    typeof(ZXButtonLink),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new object[] { "多人合作模式", new DXKeys[] { DXKeys.K } },
                    null);

                coopButton.Activated += _ =>
                {
                    try
                    {
                        ModLogger.Info("[MainMenuPatch] CO-OP MULTIPLAYER button clicked!");
                        CoopLobbyWindow.ShowLobbyWindow();
                    }
                    catch (Exception clickEx)
                    {
                        ModLogger.Error("[MainMenuPatch] Error opening CoopLobbyWindow", clickEx);
                    }
                };

                var dict = dictField.GetValue(__instance) as IDictionary;
                var mainCategoryKey = Enum.ToObject(catEnumType, 0); // TMenuCategory.Main = 0

                if (dict != null && dict.Contains(mainCategoryKey))
                {
                    var mainList = dict[mainCategoryKey] as IList;
                    if (mainList != null && mainList.Count > 0)
                    {
                        // Inherit the exact scaled TextRenderer from the existing New Game / Continue buttons
                        var sampleBtn = mainList[0] as DXTextButton;
                        if (sampleBtn != null && sampleBtn.TextRenderer != null)
                        {
                            coopButton.TextRenderer = sampleBtn.TextRenderer;
                        }

                        // Insert right after Continue button (index 2)
                        int insertIdx = Math.Min(2, mainList.Count);
                        mainList.Insert(insertIdx, coopButton);

                        // Refresh the main category rows and layout
                        switchCatMethod.Invoke(__instance, new object[] { mainCategoryKey });
                        var grid = gridField.GetValue(__instance) as DXGridLayout;
                        grid?.Update();

                        ModLogger.Info("[MainMenuPatch] Injected 'CO-OP MULTIPLAYER' button into TMenuCategory.Main successfully!");
                        return;
                    }
                }

                // Fallback when campaign mode is not active (flat menu layout)
                var fallbackGrid = gridField.GetValue(__instance) as DXGridLayout;
                if (fallbackGrid != null)
                {
                    fallbackGrid.AddRow(coopButton);
                    fallbackGrid.Update();
                    ModLogger.Info("[MainMenuPatch] Injected 'CO-OP MULTIPLAYER' button into fallback DXGridLayout.");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[MainMenuPatch] Exception in OnLoad_Postfix", ex);
            }
        }
    }

    /// <summary>
    /// Hooks ZXSystemMainMenu.OnLoadComplete (#=zwK_pUIeRBpbp) to initialize Steam invite listener
    /// and automatically reopen the RTS Waiting Room when returning to Main Menu from an active session.
    /// </summary>
    [HarmonyPatch]
    public static class MainMenuOnLoadCompletePatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnLoadComplete, Type.EmptyTypes);
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            try
            {
                if (!TestHarnessConfig.Enabled && !TestHarnessConfig.ControlEnabled)
                {
                    SteamP2PTransport.RegisterGlobalInviteListener(CoopLobbyWindow.HandleSteamInviteAccepted);
                }
                if (GameBridge.CurrentLevelState == null && DXGame.Current != null)
                {
                    DXGame.Current.SetGameTime(0.0);
                }
                CoopLobbyWindow.OnMainMenuLoadComplete();
                GameBridge.ClearWaitCursor();
            }
            catch (Exception ex)
            {
                ModLogger.Error("[MainMenuOnLoadCompletePatch] Exception in Postfix", ex);
            }
        }
    }

    /// <summary>
    /// Hooks ZXGame.ReturnToMainMenu (#=zqF2tlklT$8RE) so exiting a match preserves the Co-op
    /// connection and returns players back into the RTS Waiting Room instead of destroying the session.
    /// </summary>
    [HarmonyPatch]
    public static class ReturnToMainMenuPatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(GameBridge.TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ReturnToMainMenu, new[] { typeof(Action), typeof(bool) });
        }

        [HarmonyPrefix]
        public static void Prefix()
        {
            try
            {
                ModLogger.Info($"[ReturnToMainMenuPatch] Prefix start (IsHost={CoopSessionManager.Transport?.IsHost}, EndStatus={GameBridge.GetLevelEndStatus()}, InModal={DXVision.GUI.DXModalScreen.IsInModal}).");
                GameBridge.ClearScreenTransitionEffect();
                if (!GameBridge.IsGameLoading())
                    GameBridge.ClearLoadingFadeOverlay();
                GameBridge.ClearWaitCursor();
                CoopLobbyWindow.ForceHideWithoutStopping();
                CoopSessionManager.OnReturningToMainMenu();
                ModLogger.Info("[ReturnToMainMenuPatch] Prefix complete.");
            }
            catch (Exception ex)
            {
                ModLogger.Error("[ReturnToMainMenuPatch] Exception in Prefix", ex);
            }
        }
    }
}
