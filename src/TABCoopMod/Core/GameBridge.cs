using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Reflection.Emit;
using DXVision;
using DXVision.GUI;
using HarmonyLib;
using ZX;
using ZX.Commands;
using ZX.Components;
using ZX.Entities;
using ZX.GUI;

namespace TABCoopMod.Core
{
    /// <summary>
    /// Strongly-typed bridge to TheyAreBillions.exe and DXVision.dll internals.
    /// Isolates all obfuscated reflection/delegate lookups in one place.
    /// </summary>
    public static class GameBridge
    {
        public static Assembly GameAssembly { get; private set; }
        public static Assembly EngineAssembly { get; private set; }

        // Resolved Obfuscated System Types
        public static Type TypeZXGame { get; private set; }
        public static Type TypeZXSystem { get; private set; }
        public static Type TypeZXSystemGameLevel { get; private set; }
        public static Type TypeZXSystemMainMenu { get; private set; }
        public static Type TypeZXMessageBoxHelper { get; private set; }
        public static Type TypeZXWindowMessage { get; private set; }
        public static Type TypeZXSteamManager { get; private set; }
        public static Type TypeZXSystemAudio { get; private set; }

        // Cached Reflection Handles
        private static MethodInfo _mZXGameCurrent;
        private static MethodInfo _mLevelSysCurrent;
        private static MethodInfo _mMainMenuCurrent;
        private static MethodInfo _mGameStateCurrent;
        private static MethodInfo _mLevelStateCurrent;
        private static MethodInfo _methodLevelStateAvailFood;
        private static MethodInfo _methodLevelStateAvailWorkers;
        private static MethodInfo _methodLevelStateAvailEnergy;

        private static MethodInfo _mLocalize;
        private static MethodInfo _mGetSavesDir;
        private static MethodInfo _methodWriteSaveFile;
        private static MethodInfo _methodLoadGameAndEnter;
        private static MethodInfo _methodShowTopBanner;
        private static MethodInfo _methodShowGroundIndicator;
        private static MethodInfo _methodAssignGroupMove;
        private static MethodInfo _methodPlaceBuilding;

        private static MethodInfo _mCmdById;
        private static MethodInfo _mCmdByName;
        private static MethodInfo _mCmdExec;
        private static MethodInfo _methodCanExecuteCmd;
        private static MethodInfo _methodIsVisibleCmd;
        private static MethodInfo _methodGetResourcesCost;
        private static Type _typeCreateEntityCommand;
        private static Type _typeStructure;
        private static Type _typeEntityBonusCreation;
        private static Type _typeExtractFromSlot;
        private static PropertyInfo _propBonusCmdNSlot;
        private static PropertyInfo _propBonusCmdTemplate;
        private static PropertyInfo _propBonusCmdEntity;
        private static PropertyInfo _propExtractCmdNSlot;
        private static PropertyInfo _propCmdableAvailableCmds;
        private static FieldInfo _fieldZXGameAllCommands;
        private static MethodInfo _methodLevelStateConsumeBonus;
        private static MethodInfo _methodLevelSysClearPendingMode;
        private static MethodInfo _methodGetTemplateFromEntityType;
        private static MethodInfo _methodEntityCanPlaceAtCell;
        private static MethodInfo _methodEntityUpdateCellPosition;

        private static MethodInfo _mGetEntityCmd;
        private static MethodInfo _mGetEntitySel;
        private static MethodInfo _mGetEntityLife;
        private static MethodInfo _mGetEntityBehaviour;
        private static MethodInfo _mGetEntityMovable;
        private static MethodInfo _methodEntityGetDefaultParams;
        private static Type _typeUnit;

        private static FieldInfo _fieldAudioCurrent;
        private static MethodInfo _methodAudioPickWeightedVoice;
        private static MethodInfo _methodAudioPlayUniqueList;
        private static MethodInfo _methodAudioPlay2DSoundList;
        private static PropertyInfo _propDefParamsSoundsGeneric;
        private static PropertyInfo _propDefParamsSoundsAttack;
        private static PropertyInfo _propDefParamsSoundsCreation;
        private static PropertyInfo _propDefParamsSoundsSelected;

        private static Type _typeZombie;
        private static PropertyInfo _propZombieAttackCC;
        private static PropertyInfo _propMovableMode;
        private static PropertyInfo _propCBehavBehaviour;
        private static PropertyInfo _propBehavData;
        private static FieldInfo _fieldBDataTargetRef;
        private static PropertyInfo _propTargetRefId;
        private static PropertyInfo _propBDataLastEnemy;
        private static FieldInfo _fieldMovableTargetPos;
        private static bool _zombieAggroReflectionInit;

        // 12. Random Survival Map Generator & Balance Scaling Handles
        public static Type TypeZXMapGenerator { get; private set; }
        public static Type TypeCommandCenter { get; private set; }
        public static Type TypeRanger { get; private set; }
        public static Type TypeSoldierRegular { get; private set; }
        private static Type _typeZXRandomLevelParams;
        private static Type _typeZXMapTheme;
        private static Type _typeZXLevelExtension;
        private static Type _typeZXMapDrawer;
        private static Type _typeZXLevelEvent;
        private static MethodInfo _methodMapGeneratorGenerate;
        private static MethodInfo _methodZXMapThemeGetThemes;
        private static MethodInfo _methodLevelEventScalePop;
        private static MethodInfo _methodLevelSysCapturePanorama;
        private static MethodInfo _methodLevelSysEnterGameView;
        private static MethodInfo _mInitLevel;
        private static PropertyInfo _propLevelStateEndStatus;
        private static FieldInfo _fieldLevelSysGameEnded;
        private static FieldInfo _fieldLevelSysScreenAnim;
        private static MethodInfo _methodLevelSysTriggerGameEnd;
        private static MethodInfo _methodLevelSysShowSurvivalEnd;
        private static MethodInfo _methodLevelSysShowCampaignEnd;
        private static MethodInfo _methodGameShowMayorSelection;
        private static Type _typeZXGameState;
        private static PropertyInfo _propRandomParamsNCells;
        private static PropertyInfo _propRandomParamsFactorDuration;
        private static PropertyInfo _propRandomParamsFactorZombiePop;
        private static PropertyInfo _propMapThemeDoomVillagesSize;
        private static PropertyInfo _propMapThemeNumTreasures;
        private static PropertyInfo _propMapThemeNumSuppyFood;
        private static PropertyInfo _propMapThemeNumSuppyEnergy;
        private static PropertyInfo _propMapThemeNumInfectedGiants;
        private static PropertyInfo _propMapThemeNumInfectedMutants;
        private static PropertyInfo _propMapThemeInfectedFactor;
        private static PropertyInfo _propLevelExtMapDrawer;
        private static PropertyInfo _propLevelExtLevelEvents;
        private static PropertyInfo _propMapDrawerExtraEntities;
        private static PropertyInfo _propMapDrawerLayerTerrain;
        private static PropertyInfo _propMapDrawerLayerObjects;
        private static PropertyInfo _propMapDrawerNCells;
        private static PropertyInfo _propLevelEventStartTimeH;
        private static PropertyInfo _propLevelEventGameTimeH;
        private static PropertyInfo _propLevelEventFinalSwarm;
        private static PropertyInfo _propLevelStateExtraWorkers;
        private static PropertyInfo _propLevelStateExtraFood;

        private static MethodInfo _mCurCmd;
        private static MethodInfo _mCancelCur;
        private static MethodInfo _mCancelSlot;

        private static FieldInfo _fieldSelectedList;
        private static MethodInfo _mPrimarySel;
        private static MethodInfo _mUnselectAll;

        private static MethodInfo _methodShowAlert;
        private static MethodInfo _methodShowYesNo;
        private static MethodInfo _methodShowListSelector;
        private static MethodInfo _methodShowInputBox;
        private static Type _typeEditBoxTypeOfInput;
        private static MethodInfo _methodGetPlainTextRenderer;
        private static PropertyInfo _propWindowMsgTitle;
        private static PropertyInfo _propWindowMsgText;

        public static bool IsInitialized { get; private set; }

        public static bool InitializeAndValidate()
        {
            if (IsInitialized) return true;

            try
            {
                GameAssembly = typeof(ZX.Program).Assembly;
                EngineAssembly = typeof(DXGame).Assembly;

                int missingCount = 0;
                void Check(object member, string description)
                {
                    if (member == null)
                    {
                        missingCount++;
                        ModLogger.Error($"[GameBridge] MISSING BINDING: {description}");
                    }
                    else
                    {
                        ModLogger.Info($"[GameBridge] Verified: {description}");
                    }
                }

                // 1. Resolve Obfuscated Core Types
                TypeZXGame = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXGame);
                TypeZXSystem = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystem);
                TypeZXSystemGameLevel = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemGameLevel);
                TypeZXSystemMainMenu = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemMainMenu);
                TypeZXMessageBoxHelper = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXMessageBoxHelper);
                TypeZXWindowMessage = GameAssembly.GetType("ZX.Editor.ZXWindowMessage");
                TypeZXSteamManager = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSteamManager);

                Check(TypeZXGame, $"Type ZXGame ({ObfuscatedSymbols.Type_ZXGame})");
                Check(TypeZXSystemGameLevel, $"Type ZXSystemGameLevel ({ObfuscatedSymbols.Type_ZXSystemGameLevel})");
                Check(TypeZXSystemMainMenu, $"Type ZXSystemMainMenu ({ObfuscatedSymbols.Type_ZXSystemMainMenu})");
                Check(TypeZXMessageBoxHelper, $"Type ZXMessageBoxHelper ({ObfuscatedSymbols.Type_ZXMessageBoxHelper})");
                Check(TypeZXWindowMessage, "Type ZXWindowMessage (ZX.Editor.ZXWindowMessage)");

                // 2. Singleton Getters
                _mZXGameCurrent = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mLevelSysCurrent = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mMainMenuCurrent = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mGameStateCurrent = AccessTools.Method(typeof(ZXGameState), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);
                _mLevelStateCurrent = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes);

                Check(_mZXGameCurrent, "ZXGame.Current getter");
                Check(_mLevelSysCurrent, "ZXSystemGameLevel.Current getter");
                Check(_mMainMenuCurrent, "ZXSystemMainMenu.Current getter");
                Check(_mGameStateCurrent, "ZXGameState.Current getter");
                Check(_mLevelStateCurrent, "ZXLevelState.Current getter");

                // 3. Save / Load & Localization on ZXGame
                _mLocalize = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_Localize, new[] { typeof(string) });
                _mGetSavesDir = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_GetSavesDirectory, Type.EmptyTypes);
                _methodWriteSaveFile = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_WriteSaveFile, new[] { typeof(string) });
                _methodLoadGameAndEnter = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_LoadGameAndEnter, new[] { typeof(string) });

                Check(_mLocalize, "ZXGame.Localize");
                Check(_mGetSavesDir, "ZXGame.GetSavesDirectory");
                Check(_methodWriteSaveFile, "ZXGame.WriteSaveFile (.zxsav)");
                Check(_methodLoadGameAndEnter, "ZXGame.LoadGameAndEnter (.zxsav)");

                // 4. Level System Methods
                _methodShowTopBanner = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowTopBanner,
                    new[] { typeof(string), typeof(Color), typeof(int) });
                _methodShowGroundIndicator = TypeZXSystemGameLevel?.GetMethod(
                    ObfuscatedSymbols.Method_LevelSys_ShowGroundIndicator,
                    BindingFlags.Public | BindingFlags.Instance);
                _methodAssignGroupMove = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_AssignGroupMove,
                    new[] { typeof(List<DXEntity>), typeof(ZXCommand), typeof(PointF) });

                Check(_methodShowTopBanner, "ZXSystemGameLevel.ShowTopBanner");
                Check(_methodAssignGroupMove, "ZXSystemGameLevel.AssignGroupMove");

                // 5. Command Execution & Lookup
                _mCmdById = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetById, new[] { typeof(int) });
                _mCmdByName = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetByName, new[] { typeof(string) });
                _mCmdExec = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_Execute,
                    new[] { typeof(ZXEntity), typeof(ZXCommandTarget) });
                _methodCanExecuteCmd = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_CanExecute,
                    new[] { typeof(ZXEntity) });
                _methodIsVisibleCmd = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_IsVisible,
                    new[] { typeof(ZXEntity) });
                _methodGetResourcesCost = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetResourcesCost,
                    new[] { typeof(ZXEntity) });
                _typeCreateEntityCommand = GameAssembly.GetType("ZX.Commands.CreateEntityCommand");
                _typeStructure = GameAssembly.GetType("ZX.Entities.Structure");
                _typeEntityBonusCreation = GameAssembly.GetType("ZX.Commands.EntityBonusCreation");
                _typeExtractFromSlot = GameAssembly.GetType("ZX.Commands.ExtractFromUnitHolderSlot");
                _propBonusCmdNSlot = _typeEntityBonusCreation?.GetProperty("NSlot", BindingFlags.Public | BindingFlags.Instance);
                _propBonusCmdTemplate = _typeEntityBonusCreation?.GetProperty("EntityTemplate", BindingFlags.Public | BindingFlags.Instance);
                _propBonusCmdEntity = _typeEntityBonusCreation?.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
                _propExtractCmdNSlot = _typeExtractFromSlot?.GetProperty("NSlot", BindingFlags.Public | BindingFlags.Instance);
                _propCmdableAvailableCmds = typeof(CCommandable).GetProperty("AvailableCommands", BindingFlags.Public | BindingFlags.Instance);
                _fieldZXGameAllCommands = TypeZXGame?.GetField(ObfuscatedSymbols.Field_ZXGame_AllCommandsList, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _methodLevelStateConsumeBonus = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_ConsumeBonus, new[] { typeof(DXEntityTemplate) });
                _methodLevelSysClearPendingMode = AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ClearPendingBuildMode, Type.EmptyTypes);
                _methodPlaceBuilding = AccessTools.Method(typeof(Build), ObfuscatedSymbols.Method_Build_PlaceBuilding,
                    new[] { typeof(ZXEntity), typeof(ZXCommandTarget), typeof(Type), typeof(bool), typeof(bool), typeof(DXEntityTemplate) });
                _methodGetTemplateFromEntityType = AccessTools.Method(TypeZXGame, "GetTemplateFromEntityType", new[] { typeof(Type) });
                _methodEntityCanPlaceAtCell = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_CanPlaceAtCell,
                    new[] { typeof(int), typeof(int) });
                _methodEntityUpdateCellPosition = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_UpdateCellPosition,
                    Type.EmptyTypes);

                Check(_mCmdById, "ZXCommand.GetById");
                Check(_mCmdByName, "ZXCommand.GetByName");
                Check(_mCmdExec, "ZXCommand.Execute");
                Check(_methodCanExecuteCmd, "ZXCommand.CanExecute");
                Check(_methodIsVisibleCmd, "ZXCommand.IsVisible");
                Check(_methodGetResourcesCost, "ZXCommand.GetResourcesCost");
                Check(_typeCreateEntityCommand, "Type ZX.Commands.CreateEntityCommand");
                Check(_typeStructure, "Type ZX.Entities.Structure");
                Check(_typeEntityBonusCreation, "Type ZX.Commands.EntityBonusCreation");
                Check(_typeExtractFromSlot, "Type ZX.Commands.ExtractFromUnitHolderSlot");
                Check(_methodLevelStateConsumeBonus, "ZXLevelState.ConsumeBonus");
                Check(_methodPlaceBuilding, "Build.PlaceBuilding");
                Check(_methodGetTemplateFromEntityType, "ZXGame.GetTemplateFromEntityType");
                Check(_methodEntityCanPlaceAtCell, "ZXEntity.CanPlaceAtCell");
                Check(_methodEntityUpdateCellPosition, "ZXEntity.UpdateCellPosition");

                // 6. Entity & Component Helpers
                _mGetEntityCmd = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetCommandable, Type.EmptyTypes);
                _mGetEntitySel = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetSelectable, Type.EmptyTypes);
                _mGetEntityLife = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetLife, Type.EmptyTypes);
                _mGetEntityBehaviour = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetBehaviour, Type.EmptyTypes);
                _mGetEntityMovable = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetMovable, Type.EmptyTypes);
                _typeZombie = GameAssembly.GetType(ObfuscatedSymbols.Type_Zombie);

                Check(_mGetEntityCmd, "ZXEntity.GetCommandable");
                Check(_mGetEntitySel, "ZXEntity.GetSelectable");
                Check(_mGetEntityLife, "ZXEntity.GetLife");
                Check(_mGetEntityBehaviour, "ZXEntity.GetBehaviour");
                Check(_mGetEntityMovable, "ZXEntity.GetMovable");
                Check(_typeZombie, "Type ZX.Entities.Zombie");

                _mCurCmd = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_GetCurrentCmd, Type.EmptyTypes);
                _mCancelCur = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelCurrent, Type.EmptyTypes);
                _mCancelSlot = AccessTools.Method(typeof(CCommandable), ObfuscatedSymbols.Method_CCommandable_CancelQueueAt, new[] { typeof(int) });

                Check(_mCurCmd, "CCommandable.GetCurrentCommand");
                Check(_mCancelCur, "CCommandable.CancelCurrentCommand");
                Check(_mCancelSlot, "CCommandable.CancelQueueSlot");

                // 7. Selection Helpers
                _fieldSelectedList = AccessTools.Field(typeof(CSelectable), ObfuscatedSymbols.Field_CSelectable_SelectedList);
                _mPrimarySel = AccessTools.Method(typeof(CSelectable), ObfuscatedSymbols.Method_CSelectable_GetPrimary, Type.EmptyTypes);
                _mUnselectAll = AccessTools.Method(typeof(CSelectable), ObfuscatedSymbols.Method_CSelectable_UnselectAll, Type.EmptyTypes);

                Check(_fieldSelectedList, "CSelectable.SelectedList field");
                Check(_mPrimarySel, "CSelectable.GetPrimarySelected");
                Check(_mUnselectAll, "CSelectable.UnselectAll");

                // 8. MessageBox & UI Helpers
                _methodShowAlert = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowAlert,
                    new[] { typeof(string), typeof(string), typeof(Action) });
                _methodShowYesNo = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowYesNo,
                    new[] { typeof(string), typeof(string), typeof(Action), typeof(Action) });
                _methodShowListSelector = TypeZXWindowMessage != null
                    ? AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowListSelector,
                        new[] { TypeZXWindowMessage, typeof(Action<int>), typeof(Action), typeof(string[]) })
                    : null;
                _methodGetPlainTextRenderer = AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_GetPlainTextRenderer, Type.EmptyTypes);
                _propWindowMsgTitle = TypeZXWindowMessage != null ? AccessTools.Property(TypeZXWindowMessage, "Title") : null;
                _propWindowMsgText = TypeZXWindowMessage != null ? AccessTools.Property(TypeZXWindowMessage, "Text") : null;

                Check(_methodShowAlert, "ZXMessageBoxHelper.ShowAlert");
                Check(_methodShowYesNo, "ZXMessageBoxHelper.ShowYesNo");
                Check(_methodShowListSelector, "ZXMessageBoxHelper.ShowListSelector");
                Check(_methodGetPlainTextRenderer, "ZXMessageBoxHelper.GetPlainTextRenderer");
                Check(_propWindowMsgTitle, "ZXWindowMessage.Title");
                Check(_propWindowMsgText, "ZXWindowMessage.Text");

                // Optional: engine-native text input box (used by the LAN join dialog). Not part of
                // binding validation - if it is ever missing we simply fall back to an alert.
                _typeEditBoxTypeOfInput = typeof(DXEditBoxObject).GetNestedType("TypeOfInput");
                _methodShowInputBox = _typeEditBoxTypeOfInput != null && TypeZXMessageBoxHelper != null
                    ? AccessTools.Method(TypeZXMessageBoxHelper, ObfuscatedSymbols.Method_MsgBox_ShowInputBox,
                        new[]
                        {
                            typeof(string), typeof(string), typeof(string), _typeEditBoxTypeOfInput,
                            typeof(int), typeof(Action<string>), typeof(Action), typeof(Func<string, bool>)
                        })
                    : null;
                if (_methodShowInputBox != null)
                    ModLogger.Info("[GameBridge] Verified: ZXMessageBoxHelper.ShowInputBox (optional)");
                else
                    ModLogger.Info("[GameBridge] Optional binding missing: ZXMessageBoxHelper.ShowInputBox (LAN join dialog will fall back to alert).");

                // 9. Mayor & Modal Window Helpers
                EnsureMayorMeta();
                Check(_typeZXMayor, "Type ZX.ZXMayor");
                Check(_mayorApplyMethod, "ZXMayor.Apply");
                Check(_levelStateIsChoosingMayorMethod, "ZXLevelState.IsChoosingMayor");
                Check(_levelStateSetChoosingMayorMethod, "ZXLevelState.SetChoosingMayor");
                Check(_typeZXWindow, "Type ZX.GUI.ZXWindow");
                Check(_windowCloseMethod, "ZXWindow.Close");

                // 10. Lobby Window & MainMenu Reuse Helpers
                EnsureLobbyUiMeta();
                Check(_typeZXButtonSteam, "Type ZX.GUI.ZXButtonSteam");
                Check(_windowShowMethod, "ZXWindow.ShowWindow");
                Check(_windowAddBottomBtnMethod, "ZXWindow.AddBottomButton");
                Check(_methodReturnToMainMenu, "ZXGame.ReturnToMainMenu");
                Check(_methodMainMenuLoadSave, "ZXSystemMainMenu.LoadSave");
                Check(_methodMainMenuNewSurvival, "ZXSystemMainMenu.NewSurvival");
                Check(_methodMainMenuCustomMap, "ZXSystemMainMenu.CustomMap");
                Check(_fieldMainMenuContinueBtn, "ZXSystemMainMenu.ContinueButton");
                Check(_typeZXFontCatalogFonts, "Type ZXFontCatalog.Fonts");

                // 11. Pending Build Cost & Immediate Unit Voice Helpers
                TypeZXSystemAudio = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXSystemAudio);
                _typeUnit = GameAssembly.GetType("ZX.Entities.Unit");
                _methodEntityGetDefaultParams = AccessTools.Method(typeof(ZXEntity), ObfuscatedSymbols.Method_ZXEntity_GetDefaultParams, Type.EmptyTypes);
                _fieldAudioCurrent = TypeZXSystemAudio != null ? AccessTools.Field(TypeZXSystemAudio, ObfuscatedSymbols.Field_Audio_Current) : null;
                _methodAudioPickWeightedVoice = TypeZXSystemAudio != null
                    ? AccessTools.Method(TypeZXSystemAudio, ObfuscatedSymbols.Method_Audio_PickWeightedVoice, new[] { typeof(List<string>) })
                    : null;
                _methodAudioPlayUniqueList = TypeZXSystemAudio != null
                    ? AccessTools.Method(TypeZXSystemAudio, ObfuscatedSymbols.Method_Audio_PlayUnique,
                        new[] { typeof(ZXSoundCategory), typeof(ZXUniqueSoundType), typeof(List<string>) })
                    : null;
                _methodAudioPlay2DSoundList = TypeZXSystemAudio != null
                    ? AccessTools.Method(TypeZXSystemAudio, ObfuscatedSymbols.Method_Audio_Play2DSound,
                        new[] { typeof(ZXSoundCategory), typeof(List<string>) })
                    : null;

                _methodLevelStateAvailFood = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_AvailFood, Type.EmptyTypes);
                _methodLevelStateAvailWorkers = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_AvailWorkers, Type.EmptyTypes);
                _methodLevelStateAvailEnergy = AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_AvailEnergy, Type.EmptyTypes);

                Check(TypeZXSystemAudio, $"Type ZXSystemAudio ({ObfuscatedSymbols.Type_ZXSystemAudio})");
                Check(_typeUnit, "Type ZX.Entities.Unit");
                Check(_methodEntityGetDefaultParams, "ZXEntity.GetDefaultParams");
                Check(_fieldAudioCurrent, "ZXSystemAudio.Current");
                Check(_methodAudioPickWeightedVoice, "ZXSystemAudio.PickWeightedVoice");
                Check(_methodAudioPlayUniqueList, "ZXSystemAudio.PlayUnique(List)");
                Check(_methodAudioPlay2DSoundList, "ZXSystemAudio.Play2DSound(List)");
                Check(_methodLevelStateAvailFood, "ZXLevelState.AvailFood");
                Check(_methodLevelStateAvailWorkers, "ZXLevelState.AvailWorkers");
                Check(_methodLevelStateAvailEnergy, "ZXLevelState.AvailEnergy");

                // 12. Random Survival Map Generator & Balance Scaling Handles
                TypeZXMapGenerator = GameAssembly.GetType(ObfuscatedSymbols.Type_MapGenerator);
                TypeCommandCenter = GameAssembly.GetType(ObfuscatedSymbols.Type_CommandCenter);
                TypeRanger = GameAssembly.GetType(ObfuscatedSymbols.Type_Ranger);
                TypeSoldierRegular = GameAssembly.GetType(ObfuscatedSymbols.Type_SoldierRegular);
                _typeZXRandomLevelParams = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXRandomLevelParams);
                _typeZXMapTheme = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXMapTheme);
                _typeZXLevelExtension = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXLevelExtension);
                _typeZXMapDrawer = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXMapDrawer);
                _typeZXLevelEvent = GameAssembly.GetType(ObfuscatedSymbols.Type_ZXLevelEvent);

                _methodMapGeneratorGenerate = (TypeZXMapGenerator != null && _typeZXRandomLevelParams != null)
                    ? AccessTools.Method(TypeZXMapGenerator, ObfuscatedSymbols.Method_MapGenerator_Generate, new[] { _typeZXRandomLevelParams })
                    : null;
                _methodZXMapThemeGetThemes = _typeZXMapTheme != null
                    ? AccessTools.Method(_typeZXMapTheme, ObfuscatedSymbols.Method_ZXMapTheme_GetThemes, Type.EmptyTypes)
                    : null;
                _methodLevelEventScalePop = _typeZXLevelEvent != null
                    ? AccessTools.Method(_typeZXLevelEvent, ObfuscatedSymbols.Method_ZXLevelEvent_ScalePopulation, new[] { typeof(float), typeof(int) })
                    : null;
                _methodLevelSysCapturePanorama = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_CapturePanorama, new[] { typeof(string), typeof(int) })
                    : null;
                _methodLevelSysEnterGameView = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_EnterGameView, Type.EmptyTypes)
                    : null;
                _mInitLevel = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_InitLevel, new[] { typeof(DXLevel) })
                    : null;
                _propLevelStateEndStatus = AccessTools.Property(typeof(ZXLevelState), "EndStatus");
                _fieldLevelSysGameEnded = TypeZXSystemGameLevel != null
                    ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_GameEndedFlag)
                    : null;
                _fieldLevelSysScreenAnim = TypeZXSystemGameLevel != null
                    ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_ScreenAnim)
                    : null;
                _methodLevelSysTriggerGameEnd = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_TriggerGameEnd, new[] { typeof(bool) })
                    : null;
                _methodLevelSysShowSurvivalEnd = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowSurvivalEnd, new[] { typeof(bool), typeof(bool) })
                    : null;
                _methodLevelSysShowCampaignEnd = TypeZXSystemGameLevel != null
                    ? AccessTools.Method(TypeZXSystemGameLevel, ObfuscatedSymbols.Method_LevelSys_ShowCampaignEnd, new[] { typeof(bool), typeof(bool) })
                    : null;
                _methodGameShowMayorSelection = TypeZXGame != null
                    ? AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ShowMayorSelection, Type.EmptyTypes)
                    : null;
                _typeZXGameState = typeof(ZXGameState);

                if (_typeZXRandomLevelParams != null)
                {
                    _propRandomParamsNCells = AccessTools.Property(_typeZXRandomLevelParams, "NCells");
                    _propRandomParamsFactorDuration = AccessTools.Property(_typeZXRandomLevelParams, "FactorGameDuration");
                    _propRandomParamsFactorZombiePop = AccessTools.Property(_typeZXRandomLevelParams, "FactorZombiePopulation");
                }
                if (_typeZXMapTheme != null)
                {
                    _propMapThemeDoomVillagesSize = AccessTools.Property(_typeZXMapTheme, "DoomVillagesSize");
                    _propMapThemeNumTreasures = AccessTools.Property(_typeZXMapTheme, "NumTreasures");
                    _propMapThemeNumSuppyFood = AccessTools.Property(_typeZXMapTheme, "NumSuppyFood");
                    _propMapThemeNumSuppyEnergy = AccessTools.Property(_typeZXMapTheme, "NumSuppyEnergy");
                    _propMapThemeNumInfectedGiants = AccessTools.Property(_typeZXMapTheme, "NumInfectedGiants");
                    _propMapThemeNumInfectedMutants = AccessTools.Property(_typeZXMapTheme, "NumInfectedMutants");
                    _propMapThemeInfectedFactor = AccessTools.Property(_typeZXMapTheme, "InfectedFactor");
                }
                if (_typeZXLevelExtension != null)
                {
                    _propLevelExtMapDrawer = AccessTools.Property(_typeZXLevelExtension, "MapDrawer");
                    _propLevelExtLevelEvents = AccessTools.Property(_typeZXLevelExtension, "LevelEvents");
                }
                if (_typeZXMapDrawer != null)
                {
                    _propMapDrawerExtraEntities = AccessTools.Property(_typeZXMapDrawer, "ExtraEntities");
                    _propMapDrawerLayerTerrain = AccessTools.Property(_typeZXMapDrawer, "LayerTerrain");
                    _propMapDrawerLayerObjects = AccessTools.Property(_typeZXMapDrawer, "LayerObjects");
                    _propMapDrawerNCells = AccessTools.Property(_typeZXMapDrawer, "NCells");
                }
                if (_typeZXLevelEvent != null)
                {
                    _propLevelEventStartTimeH = AccessTools.Property(_typeZXLevelEvent, "StartTimeH");
                    _propLevelEventGameTimeH = AccessTools.Property(_typeZXLevelEvent, "GameTimeH");
                    _propLevelEventFinalSwarm = AccessTools.Property(_typeZXLevelEvent, "FinalSwarm");
                }
                var lsType = LevelStateType ?? GameAssembly.GetType("ZX.ZXLevelState");
                if (lsType != null)
                {
                    _propLevelStateExtraWorkers = AccessTools.Property(lsType, "CommandCenterExtraWorkers");
                    _propLevelStateExtraFood = AccessTools.Property(lsType, "CommandCenterExtraFood");
                }

                Check(TypeZXMapGenerator, "Type RandomMapGenerator");
                Check(_methodMapGeneratorGenerate, "RandomMapGenerator.Generate(ZXRandomLevelParams)");
                Check(TypeCommandCenter, "Type ZX.Entities.CommandCenter");
                Check(TypeRanger, "Type ZX.Entities.Ranger");
                Check(TypeSoldierRegular, "Type ZX.Entities.SoldierRegular");
                Check(_typeZXRandomLevelParams, "Type ZXRandomLevelParams");
                Check(_propRandomParamsNCells, "ZXRandomLevelParams.NCells");
                Check(_propRandomParamsFactorDuration, "ZXRandomLevelParams.FactorGameDuration");
                Check(_propRandomParamsFactorZombiePop, "ZXRandomLevelParams.FactorZombiePopulation");
                Check(_typeZXMapTheme, "Type ZXMapTheme");
                Check(_methodZXMapThemeGetThemes, "ZXMapTheme.GetThemes");
                Check(_propMapThemeDoomVillagesSize, "ZXMapTheme.DoomVillagesSize");
                Check(_propMapThemeNumTreasures, "ZXMapTheme.NumTreasures");
                Check(_propMapThemeNumSuppyFood, "ZXMapTheme.NumSuppyFood");
                Check(_propMapThemeNumSuppyEnergy, "ZXMapTheme.NumSuppyEnergy");
                Check(_propMapThemeNumInfectedGiants, "ZXMapTheme.NumInfectedGiants");
                Check(_propMapThemeNumInfectedMutants, "ZXMapTheme.NumInfectedMutants");
                Check(_propMapThemeInfectedFactor, "ZXMapTheme.InfectedFactor");
                Check(_typeZXLevelExtension, "Type ZXLevelExtension");
                Check(_propLevelExtMapDrawer, "ZXLevelExtension.MapDrawer");
                Check(_propLevelExtLevelEvents, "ZXLevelExtension.LevelEvents");
                Check(_typeZXMapDrawer, "Type ZXMapDrawer");
                Check(_propMapDrawerExtraEntities, "ZXMapDrawer.ExtraEntities");
                Check(_propMapDrawerLayerTerrain, "ZXMapDrawer.LayerTerrain");
                Check(_propMapDrawerLayerObjects, "ZXMapDrawer.LayerObjects");
                Check(_propMapDrawerNCells, "ZXMapDrawer.NCells");
                Check(_typeZXLevelEvent, "Type ZXLevelEvent");
                Check(_methodLevelEventScalePop, "ZXLevelEvent.ScalePopulation");
                Check(_propLevelEventStartTimeH, "ZXLevelEvent.StartTimeH");
                Check(_propLevelEventGameTimeH, "ZXLevelEvent.GameTimeH");
                Check(_propLevelEventFinalSwarm, "ZXLevelEvent.FinalSwarm");
                Check(_propLevelStateExtraWorkers, "ZXLevelState.CommandCenterExtraWorkers");
                Check(_propLevelStateExtraFood, "ZXLevelState.CommandCenterExtraFood");
                Check(_methodLevelSysCapturePanorama, "ZXSystemGameLevel.CapturePanorama");
                Check(_methodLevelSysEnterGameView, "ZXSystemGameLevel.EnterGameView");
                Check(_propLevelStateEndStatus, "ZXLevelState.EndStatus");
                Check(_fieldLevelSysGameEnded, "ZXSystemGameLevel.GameEndedFlag");
                Check(_fieldLevelSysScreenAnim, "ZXSystemGameLevel.ScreenAnim");
                Check(_methodLevelSysTriggerGameEnd, "ZXSystemGameLevel.TriggerGameEnd");
                Check(_methodLevelSysShowSurvivalEnd, "ZXSystemGameLevel.ShowSurvivalEnd");
                Check(_methodLevelSysShowCampaignEnd, "ZXSystemGameLevel.ShowCampaignEnd");
                Check(_methodGameShowMayorSelection, "ZXGame.ShowMayorSelection");
                Check(_typeZXGameState, "Type ZXGameState");

                if (missingCount == 0)
                {
                    TryCompileFastCommandDelegates();
                    ModLogger.Info("[GameBridge] ALL BINDINGS VERIFIED SUCCESSFULLY (0 missing).");
                    IsInitialized = true;
                    return true;
                }
                else
                {
                    ModLogger.Error($"[GameBridge] Binding validation failed with {missingCount} missing symbol(s)!");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] Exception during InitializeAndValidate", ex);
                return false;
            }
        }

        // ====================================================================
        // Public Strongly-Typed Wrapper API
        // ====================================================================

        public static DXGame ZXGameInstance => _mZXGameCurrent?.Invoke(null, null) as DXGame;
        public static DXSystem LevelSystemInstance => _mLevelSysCurrent?.Invoke(null, null) as DXSystem;
        public static DXSystem MainMenuInstance => _mMainMenuCurrent?.Invoke(null, null) as DXSystem;
        /// <summary>ZXGameState is internal; expose it as object so callers can reflect safely.</summary>
        public static object CurrentGameStateObject => _mGameStateCurrent?.Invoke(null, null);

        private static readonly Dictionary<string, PropertyInfo> _gameStatePropCache = new Dictionary<string, PropertyInfo>();

        public static object GetGameStateProperty(string name)
        {
            object gs = CurrentGameStateObject;
            if (gs == null) return null;
            if (!_gameStatePropCache.TryGetValue(name, out PropertyInfo prop))
            {
                prop = gs.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                _gameStatePropCache[name] = prop;
            }
            try { return prop?.GetValue(gs); } catch { return null; }
        }

        /// <summary>
        /// ZXLevelState is an internal type; a direct cast fails at runtime with TypeAccessException.
        /// Everything touching it must go through reflection, so it is returned as object.
        /// </summary>
        public static object CurrentLevelState => _mLevelStateCurrent?.Invoke(null, null);
        public static Type LevelStateType => _mLevelStateCurrent?.ReturnType;

        private static readonly Dictionary<string, PropertyInfo> _levelPropCache = new Dictionary<string, PropertyInfo>();

        public static PropertyInfo GetLevelProperty(string name)
        {
            if (_levelPropCache.TryGetValue(name, out var cached)) return cached;
            PropertyInfo prop = LevelStateType?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            _levelPropCache[name] = prop;
            return prop;
        }

        public static int GetLevelInt(string name)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(ls)); } catch { return 0; }
        }

        public static bool SetLevelProperty(string name, object value)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null || !prop.CanWrite) return false;
            try { prop.SetValue(ls, value); return true; } catch { return false; }
        }

        /// <summary>
        /// Reveals the whole map via ZXLevelState.ShowFullMap, which is OR'ed into every visibility
        /// check in ZXSystemMapLayers. Rendering-only — it does not mutate the simulation state.
        /// </summary>
        public static bool SetShowFullMap(bool on) => SetLevelProperty("ShowFullMap", on);

        private static Type _mapLayersType;
        private static FieldInfo _mapLayersCurrentField;
        private static PropertyInfo _fogEnabledProp;

        /// <summary>Toggles ZXSystemMapLayers.FogEnabled (the global fog-of-war switch).</summary>
        public static bool SetFogEnabled(bool on)
        {
            if (_mapLayersType == null)
                _mapLayersType = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            if (_mapLayersType == null) return false;

            if (_mapLayersCurrentField == null)
                _mapLayersCurrentField = _mapLayersType.GetField(ObfuscatedSymbols.Field_MapLayers_Current,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object inst = _mapLayersCurrentField?.GetValue(null);
            if (inst == null) return false;

            if (_fogEnabledProp == null)
                _fogEnabledProp = _mapLayersType.GetProperty("FogEnabled", BindingFlags.Public | BindingFlags.Instance);
            if (_fogEnabledProp == null || !_fogEnabledProp.CanWrite) return false;

            try { _fogEnabledProp.SetValue(inst, on); return true; } catch { return false; }
        }

        public static string Localize(string key) => _mLocalize != null ? (string)_mLocalize.Invoke(null, new object[] { key }) : key;
        public static string GetSavesDirectory() => (string)_mGetSavesDir?.Invoke(null, null);

        public static void WriteSaveFile(string fullFilePath)
        {
            var game = _mZXGameCurrent.Invoke(null, null);
            var ls = CurrentLevelState;
            if (ls != null)
            {
                AccessTools.Method(typeof(ZXLevelState), ObfuscatedSymbols.Method_ZXLevelState_SaveTimeState)?.Invoke(ls, null);
            }
            _methodWriteSaveFile.Invoke(game, new object[] { fullFilePath });
        }

        public static void LoadSaveFileAndEnter(string fileNameOrPath)
        {
            var game = _mZXGameCurrent.Invoke(null, null);
            _methodLoadGameAndEnter.Invoke(game, new object[] { fileNameOrPath });
        }

        /// <summary>
        /// Checks ZXGame's own save-file validity gate (the private static bool that
        /// LoadGameAndEnter consults at its very top). A false result makes that call return
        /// silently in ~5 ms with no exception and no loading — the exact symptom we chase.
        /// </summary>
        public static bool IsSaveLoadable(string path)
        {
            try
            {
                var m = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_IsSaveLoadable, new[] { typeof(string) });
                if (m == null) return true; // cannot check; let the engine decide
                return (bool)m.Invoke(null, new object[] { path });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] IsSaveLoadable check failed: {ex.Message}");
                return true;
            }
        }

        private static MethodInfo _methodAnimate;
        private static int _kickFadeDiag;
        private static PropertyInfo _propZXGameIsLoading;

        public static bool IsGameLoading()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                if (_propZXGameIsLoading == null)
                    _propZXGameIsLoading = AccessTools.Property(TypeZXGame, "IsLoading");
                return _propZXGameIsLoading != null && (bool)_propZXGameIsLoading.GetValue(game, null);
            }
            catch { return false; }
        }

        /// <summary>
        /// Advances the engine's black loading-fade DXObject by hand. LoadGameAndEnter gates the real
        /// load behind three DXAnimation fades (see ZXGame.#=zXAMaTPI=); if the scene renders the fade
        /// object but never advances its animation (unfocused/background window, no scene frames), the
        /// callback chain never fires and the level never loads. Driving the animation keeps the
        /// engine's own state machine intact instead of skipping callbacks.
        /// </summary>
        public static void KickLoadingFade(double advanceMs = 700.0)
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (!(field?.GetValue(game) is DXObject fade))
                {
                    if (++_kickFadeDiag <= 5) ModLogger.Info("[GameBridge] KickLoadingFade: fade object is null.");
                    return;
                }
                bool inScene = fade.Scene != null;
                bool hasAnim = fade.Animation != null;
                if (++_kickFadeDiag <= 5)
                    ModLogger.Info($"[GameBridge] KickLoadingFade: inScene={inScene} hasAnimation={hasAnim} opacity={fade.Opacity:F2} visible={fade.Visible}.");

                if (!inScene || !hasAnim) return;
                if (_methodAnimate == null)
                    _methodAnimate = AccessTools.Method(typeof(DXObject), "Animate", new[] { typeof(double) });
                _methodAnimate?.Invoke(fade, new object[] { advanceMs });
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] KickLoadingFade failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures ZXGame's fullscreen loading-fade object (#=zbvwh$ickzalV, MouseClickInteractionEnabled=true,
        /// Z=2147483642) is removed from the active scene once loading completes so it cannot swallow mouse
        /// clicks or leave a translucent overlay over the level.
        /// </summary>
        public static void ClearLoadingFadeOverlay()
        {
            try
            {
                if (IsGameLoading()) return;
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (field?.GetValue(game) is DXObject fade && fade.Scene != null)
                {
                    fade.Animation = null;
                    fade.Opacity = 0f;
                    fade.Visible = false;
                    fade.RemoveFromScene();
                    ModLogger.Info("[GameBridge] Cleared lingering loading-fade overlay after level load.");
                }
                // Removing the fade object aborts its fade-out animation callback that would otherwise clear
                // StartupFadeActive (#=zTkvm$fc5hNvM); reset it explicitly so future resync loads are not gated.
                var startupFadeField = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_StartupFadeActive);
                if (startupFadeField?.GetValue(game) is bool sfa && sfa)
                    startupFadeField.SetValue(game, false);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ClearLoadingFadeOverlay failed: {ex.Message}");
            }
        }

        public static bool IsLoadingFadeInScene()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                return field?.GetValue(game) is DXObject fade && fade.Scene != null;
            }
            catch { return false; }
        }

        public static DXSystem CurrentGameSystem
        {
            get
            {
                try
                {
                    var game = _mZXGameCurrent?.Invoke(null, null);
                    if (game == null || TypeZXGame == null) return null;
                    return AccessTools.Property(TypeZXGame, "CurrentGameSystem")?.GetValue(game) as DXSystem;
                }
                catch { return null; }
            }
        }

        public static bool IsMainMenuReadyForInput()
        {
            try
            {
                var menu = MainMenuInstance;
                if (menu == null) return false;
                var field = AccessTools.Field(TypeZXSystemMainMenu, ObfuscatedSymbols.Field_MainMenu_ReadyForInput);
                return field != null && (bool)field.GetValue(menu);
            }
            catch { return false; }
        }

        public static bool IsStartupFadeActive()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null) return false;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_StartupFadeActive);
                return field != null && (bool)field.GetValue(game);
            }
            catch { return false; }
        }

        /// <summary>
        /// ZXGame.#=z9DPDdq9qP9lZ silently returns when its black loading-fade object is already in
        /// the scene (e.g. a main-menu transition still animating), so a programmatic load during a
        /// snapshot arrival becomes a no-op. Also works around vanilla LoadGameAndEnter line 229's
        /// inverted null check (`if (CurrentGameSystem == null) CurrentGameSystem.Enabled = false;`),
        /// which otherwise leaves an active ZXSystemGameLevel updating disposed CShadow scene objects
        /// after Scene.Init(Black) and throws NullReferenceException on in-level reloads.
        /// </summary>
        public static void PrepareForProgrammaticLoad()
        {
            try
            {
                DisableCameraIntroAnimation();
                var game = _mZXGameCurrent?.Invoke(null, null) as DXGame;
                if (game == null) return;
                var field = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_LoadingFadeObj);
                if (field?.GetValue(game) is DXObject fade && fade.Scene != null)
                {
                    fade.RemoveFromScene();
                    ModLogger.Info("[GameBridge] Detached stale loading-fade object before programmatic load.");
                }

                var sys = AccessTools.Property(TypeZXGame, "CurrentGameSystem")?.GetValue(game) as DXSystem;
                if (sys != null)
                {
                    bool wasInLevel = TypeZXSystemGameLevel != null && TypeZXSystemGameLevel.IsInstanceOfType(sys);
                    sys.Enabled = false;
                    game.Paused = true;
                    game.PostMethods_OnStartFrame?.Clear();
                    game.PostMethods_OnFinishFrame?.Clear();
                    game.PostMethods_OnSafeLogicAndDrawing?.Clear();
                    if (wasInLevel)
                    {
                        game.ClearEntities();
                        ModLogger.Info("[GameBridge] Disabled active level system and cleared entities before in-level reload.");
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] PrepareForProgrammaticLoad failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears any stuck Wait/Hourglass cursor lock left behind by interrupted async level loads
        /// or programmatic snapshot transitions so hover/click cursor updates resume normally.
        /// </summary>
        public static void ClearWaitCursor()
        {
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null) as DXGame;
                if (game == null) return;

                var fWait = AccessTools.Field(TypeZXGame, ObfuscatedSymbols.Field_ZXGame_WaitCursorCount);
                int prevCount = 0;
                if (fWait != null)
                {
                    prevCount = (int)fWait.GetValue(game);
                    fWait.SetValue(game, 0);
                }

                bool wasLocked = game.CursorLocked;
                var mEnd = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_EndWaitCursor, Type.EmptyTypes);
                if (mEnd != null)
                {
                    mEnd.Invoke(game, null);
                }
                else
                {
                    game.CursorLocked = false;
                }

                game.CursorVisible = true;
                if (wasLocked || prevCount > 0)
                    ModLogger.Info($"[GameBridge] Cleared stuck WaitCursor (prevCount={prevCount}, wasLocked={wasLocked}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ClearWaitCursor failed: {ex.Message}");
            }
        }

        public static void ShowTopBanner(string message, Color color, int durationMs = 3000)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                _methodShowTopBanner.Invoke(levelSys, new object[] { message, color, durationMs });
            }
        }

        // NOTE: ZXCommand and ZXCommandTarget are internal types. Never cast to / `is`-check them here —
        // the Desktop CLR throws TypeAccessException on castclass/isinst of an inaccessible type
        // (铁律#3). All command execution paths therefore use object + reflection.
        public static object GetCommandById(int id) => _mCmdById.Invoke(null, new object[] { id });
        public static object GetCommandByName(string typeName) => _mCmdByName.Invoke(null, new object[] { typeName });

        private static Action<object, object, object> _fastCmdExec;
        private static Action<object, List<DXEntity>, object, PointF> _fastAssignGroupMove;
        private static Func<object, object> _fastGetCmdable;
        private static Type _fastGetCmdTargetDeclType;
        private static Func<object, object> _fastGetCmdTarget;
        private static Func<DXGame, List<DXEntity>> _fastGetGameEntities;
        private static Func<object, object> _fastGetLifeObj;
        private static Type _fastGetLifeIntDeclType;
        private static Func<object, int> _fastGetLifeInt;
        private static Type _fastGetOrdersQueuedDeclType;
        private static Func<object, IList> _fastGetOrdersQueued;

        private static void TryCompileFastCommandDelegates()
        {
            try
            {
                if (_mCmdExec != null)
                {
                    var dmExec = new DynamicMethod("TABCoop_FastCmdExec", typeof(void),
                        new[] { typeof(object), typeof(object), typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmExec.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, typeof(ZXCommand));
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Castclass, typeof(ZXEntity));
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(typeof(ZXCommandTarget).IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, typeof(ZXCommandTarget));
                    il.Emit(OpCodes.Callvirt, _mCmdExec);
                    il.Emit(OpCodes.Ret);
                    _fastCmdExec = (Action<object, object, object>)dmExec.CreateDelegate(typeof(Action<object, object, object>));
                }

                if (_methodAssignGroupMove != null && TypeZXSystemGameLevel != null)
                {
                    var dmGroup = new DynamicMethod("TABCoop_FastAssignGroupMove", typeof(void),
                        new[] { typeof(object), typeof(List<DXEntity>), typeof(object), typeof(PointF) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmGroup.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, TypeZXSystemGameLevel);
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Castclass, typeof(ZXCommand));
                    il.Emit(OpCodes.Ldarg_3);
                    il.Emit(OpCodes.Callvirt, _methodAssignGroupMove);
                    il.Emit(OpCodes.Ret);
                    _fastAssignGroupMove = (Action<object, List<DXEntity>, object, PointF>)dmGroup.CreateDelegate(
                        typeof(Action<object, List<DXEntity>, object, PointF>));
                }

                if (_mGetEntityCmd != null)
                {
                    var dmCmdable = new DynamicMethod("TABCoop_FastGetCommandable", typeof(object),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmCmdable.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _mGetEntityCmd.DeclaringType);
                    il.Emit(OpCodes.Callvirt, _mGetEntityCmd);
                    il.Emit(OpCodes.Ret);
                    _fastGetCmdable = (Func<object, object>)dmCmdable.CreateDelegate(typeof(Func<object, object>));
                }

                Type ccmd = TypeCCommandable;
                var targetProp = ccmd?.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
                var targetGetter = targetProp?.GetGetMethod(true);
                if (targetGetter != null)
                {
                    _fastGetCmdTargetDeclType = targetGetter.DeclaringType;
                    var dmTarget = new DynamicMethod("TABCoop_FastGetCmdTarget", typeof(object),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmTarget.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _fastGetCmdTargetDeclType);
                    il.Emit(OpCodes.Callvirt, targetGetter);
                    if (targetGetter.ReturnType.IsValueType)
                        il.Emit(OpCodes.Box, targetGetter.ReturnType);
                    il.Emit(OpCodes.Ret);
                    _fastGetCmdTarget = (Func<object, object>)dmTarget.CreateDelegate(typeof(Func<object, object>));
                }

                var fEnt = typeof(DXGame).GetField("_Entities", BindingFlags.NonPublic | BindingFlags.Instance);
                if (fEnt != null && fEnt.FieldType == typeof(List<DXEntity>))
                {
                    var dmEnt = new DynamicMethod("TABCoop_FastGetGameEntities", typeof(List<DXEntity>),
                        new[] { typeof(DXGame) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmEnt.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Ldfld, fEnt);
                    il.Emit(OpCodes.Ret);
                    _fastGetGameEntities = (Func<DXGame, List<DXEntity>>)dmEnt.CreateDelegate(typeof(Func<DXGame, List<DXEntity>>));
                }

                if (_mGetEntityLife != null)
                {
                    var dmLifeObj = new DynamicMethod("TABCoop_FastGetLifeObj", typeof(object),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmLifeObj.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _mGetEntityLife.DeclaringType);
                    il.Emit(OpCodes.Callvirt, _mGetEntityLife);
                    il.Emit(OpCodes.Ret);
                    _fastGetLifeObj = (Func<object, object>)dmLifeObj.CreateDelegate(typeof(Func<object, object>));

                    var lifeGetter = _mGetEntityLife.ReturnType?.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
                    if (lifeGetter != null && lifeGetter.ReturnType == typeof(int))
                    {
                        _fastGetLifeIntDeclType = lifeGetter.DeclaringType;
                        var dmLifeInt = new DynamicMethod("TABCoop_FastGetLifeInt", typeof(int),
                            new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                        var ilI = dmLifeInt.GetILGenerator();
                        ilI.Emit(OpCodes.Ldarg_0);
                        ilI.Emit(OpCodes.Castclass, _fastGetLifeIntDeclType);
                        ilI.Emit(OpCodes.Callvirt, lifeGetter);
                        ilI.Emit(OpCodes.Ret);
                        _fastGetLifeInt = (Func<object, int>)dmLifeInt.CreateDelegate(typeof(Func<object, int>));
                    }
                }

                var ordersGetter = ccmd?.GetProperty("OrdersQueued", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod(true);
                if (ordersGetter != null && typeof(IList).IsAssignableFrom(ordersGetter.ReturnType))
                {
                    _fastGetOrdersQueuedDeclType = ordersGetter.DeclaringType;
                    var dmOrders = new DynamicMethod("TABCoop_FastGetOrdersQueued", typeof(IList),
                        new[] { typeof(object) }, typeof(GameBridge).Module, skipVisibility: true);
                    var il = dmOrders.GetILGenerator();
                    il.Emit(OpCodes.Ldarg_0);
                    il.Emit(OpCodes.Castclass, _fastGetOrdersQueuedDeclType);
                    il.Emit(OpCodes.Callvirt, ordersGetter);
                    il.Emit(OpCodes.Ret);
                    _fastGetOrdersQueued = (Func<object, IList>)dmOrders.CreateDelegate(typeof(Func<object, IList>));
                }

                ModLogger.Info("[Perf] Compiled DynamicMethods for fast command replay & state hashing.");
            }
            catch (Exception ex)
            {
                _fastCmdExec = null;
                _fastAssignGroupMove = null;
                _fastGetCmdable = null;
                _fastGetCmdTarget = null;
                _fastGetCmdTargetDeclType = null;
                _fastGetGameEntities = null;
                _fastGetLifeObj = null;
                _fastGetLifeIntDeclType = null;
                _fastGetLifeInt = null;
                _fastGetOrdersQueuedDeclType = null;
                _fastGetOrdersQueued = null;
                ModLogger.Warn($"[Perf] TryCompileFastCommandDelegates failed (falling back to reflection): {ex.Message}");
            }
        }

        public static List<DXEntity> GetEntitiesList(DXGame game)
        {
            if (game == null || _fastGetGameEntities == null) return null;
            try { return _fastGetGameEntities(game); } catch { return null; }
        }

        public static bool IsZombieType(Type type) => type != null && _typeZombie != null && _typeZombie.IsAssignableFrom(type);

        public static void ExecuteCommandOnEntity(object cmd, object executor, object target)
        {
            if (_fastCmdExec != null)
            {
                _fastCmdExec(cmd, executor, target);
                return;
            }
            _mCmdExec.Invoke(cmd, new object[] { executor, target });
        }

        public static void AssignGroupMoveTargets(List<DXEntity> entities, object cmd, PointF destination)
        {
            var levelSys = _mLevelSysCurrent.Invoke(null, null);
            if (levelSys != null)
            {
                if (_fastAssignGroupMove != null)
                {
                    _fastAssignGroupMove(levelSys, entities, cmd, destination);
                    return;
                }
                _methodAssignGroupMove.Invoke(levelSys, new object[] { entities, cmd, destination });
            }
        }

        public static object PlaceBuilding(object builder, object target, Type buildingType, bool isBonus = false, bool instant = false, DXEntityTemplate template = null)
        {
            return _methodPlaceBuilding.Invoke(null, new object[] { builder, target, buildingType, isBonus, instant, template });
        }

        public static CSelectable GetSelectable(this ZXEntity entity) => (CSelectable)_mGetEntitySel.Invoke(entity, null);
        public static CLife GetLife(this ZXEntity entity) => (CLife)_mGetEntityLife.Invoke(entity, null);

        public static object GetCurrentCommandObject(object commandable) => _mCurCmd?.Invoke(commandable, null);
        public static void CancelCurrentOrderObject(object commandable) => _mCancelCur?.Invoke(commandable, null);
        public static void CancelQueueSlotObject(object commandable, int slotIndex) => _mCancelSlot?.Invoke(commandable, new object[] { slotIndex });

        public static DXConcurrentList<CSelectable> GetLocallySelectedList()
        {
            return (DXConcurrentList<CSelectable>)_fieldSelectedList.GetValue(null);
        }

        public static ZXEntity GetPrimarySelectedEntity() => (ZXEntity)_mPrimarySel.Invoke(null, null);
        public static void UnselectAllLocal() => _mUnselectAll.Invoke(null, null);

        public static DXTextRenderer GetPlainTextRenderer()
        {
            return (DXTextRenderer)_methodGetPlainTextRenderer.Invoke(null, null);
        }

        public static object ShowAlertMessageBox(string title, string message, Action onOk = null)
        {
            return _methodShowAlert.Invoke(null, new object[] { title, message, onOk });
        }

        public static object ShowYesNoMessageBox(string title, string message, Action onYes, Action onNo = null)
        {
            return _methodShowYesNo.Invoke(null, new object[] { title, message, onYes, onNo });
        }

        public static object ShowListSelectorMessageBox(
            string title,
            string message,
            string[] options,
            Action<int> onOk,
            Action onCancel = null)
        {
            if (TypeZXWindowMessage == null || _methodShowListSelector == null)
                return ShowAlertMessageBox(title, message, onCancel);

            object msgObj = Activator.CreateInstance(TypeZXWindowMessage);
            _propWindowMsgTitle?.SetValue(msgObj, title, null);
            _propWindowMsgText?.SetValue(msgObj, message, null);
            return _methodShowListSelector.Invoke(null, new object[] { msgObj, onOk, onCancel, options });
        }

        /// <summary>True when the engine-native input box is available.</summary>
        public static bool HasInputBoxSupport => _methodShowInputBox != null && _typeEditBoxTypeOfInput != null;

        /// <summary>
        /// Shows the engine-native text input box (with the game's own cursor / backspace handling).
        /// <paramref name="inputTypeName"/> is a <c>DXEditBoxObject.TypeOfInput</c> member name, e.g.
        /// <c>AllCharactersExceptNewLines</c>, <c>Numeric</c>, <c>Alphanumeric</c>.
        /// Falls back to a plain alert when the binding is unavailable.
        /// </summary>
        public static object ShowInputBoxMessageBox(
            string title,
            string message,
            string initialValue,
            int maxLength,
            Action<string> onOk,
            Action onCancel = null,
            Func<string, bool> validator = null,
            string inputTypeName = "AllCharactersExceptNewLines")
        {
            if (!HasInputBoxSupport)
            {
                ShowAlertMessageBox(title, message, onCancel);
                return null;
            }

            object inputType;
            try { inputType = Enum.Parse(_typeEditBoxTypeOfInput, inputTypeName, true); }
            catch { inputType = Activator.CreateInstance(_typeEditBoxTypeOfInput); }

            return _methodShowInputBox.Invoke(null, new object[]
            {
                title, message, initialValue ?? string.Empty, inputType, maxLength,
                onOk, onCancel, validator
            });
        }

        // ====================================================================
        // Deterministic test-harness helpers.
        // NOTE: all game entity/component classes are internal, so these return base/public
        // types (DXEntity) and use reflection internally. Never cast to ZXEntity here.
        // ====================================================================

        private static readonly Dictionary<Type, PropertyInfo> _teamPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _lifePropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _ordersPropCache = new Dictionary<Type, PropertyInfo>();
        private static PropertyInfo _ccProp;

        private static int CompareEntitiesDeterministically(DXEntity a, DXEntity b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            bool aHigh = (a.ID & 0x8000000000000000UL) != 0UL;
            bool bHigh = (b.ID & 0x8000000000000000UL) != 0UL;
            if (aHigh != bHigh) return aHigh ? 1 : -1;

            if (!aHigh)
                return a.ID.CompareTo(b.ID);

            int cmp = a.Cell.X.CompareTo(b.Cell.X);
            if (cmp != 0) return cmp;
            cmp = a.Cell.Y.CompareTo(b.Cell.Y);
            if (cmp != 0) return cmp;
            cmp = a.Position.X.CompareTo(b.Position.X);
            if (cmp != 0) return cmp;
            cmp = a.Position.Y.CompareTo(b.Position.Y);
            if (cmp != 0) return cmp;
            cmp = (a.Template?.ID ?? 0UL).CompareTo(b.Template?.ID ?? 0UL);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
            if (cmp != 0) return cmp;
            return a.ID.CompareTo(b.ID);
        }

        private static int CompareBodiesDeterministically(DXPhysicBody a, DXPhysicBody b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            int cmp = CompareEntitiesDeterministically(a.GameEntity, b.GameEntity);
            if (cmp != 0) return cmp;

            cmp = a.PositionComputed.X.CompareTo(b.PositionComputed.X);
            if (cmp != 0) return cmp;
            cmp = a.PositionComputed.Y.CompareTo(b.PositionComputed.Y);
            if (cmp != 0) return cmp;
            return ((int)a.Shape).CompareTo((int)b.Shape);
        }

        public static void SortEntitiesById()
        {
            try
            {
                var game = DXGame.Current;
                if (game == null) return;

                var entitiesField = AccessTools.Field(typeof(DXGame), "_Entities");
                if (entitiesField?.GetValue(game) is List<DXEntity> list && list.Count > 1)
                {
                    list.Sort(CompareEntitiesDeterministically);
                    var indexField = AccessTools.Field(typeof(DXEntity), "_Index");
                    if (indexField != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i] != null)
                                indexField.SetValue(list[i], i);
                        }
                    }
                    ModLogger.Info($"[GameBridge] Re-ordered {list.Count} entities deterministically.");

                    // ClearEntities() clears HashSets in place without resetting bucket capacities, so an
                    // in-level reload leaves larger _buckets arrays than a fresh MainMenu load and makes
                    // EntitiesOfType<T>() iterate in a different hash-collision order (e.g. SysColonists).
                    // Rebuild _EntitiesPerType and _DicBaseTypesPerEntity from fresh HashSets in sorted order.
                    var perTypeField = AccessTools.Field(typeof(DXGame), "_EntitiesPerType");
                    var baseTypesField = AccessTools.Field(typeof(DXGame), "_DicBaseTypesPerEntity");
                    if (perTypeField?.GetValue(game) is Dictionary<Type, HashSet<DXEntity>> perType &&
                        baseTypesField?.GetValue(null) is Dictionary<Type, List<HashSet<DXEntity>>> baseTypes)
                    {
                        int rebuiltSets = 0;
                        lock (DXGame.SyncLogic)
                        {
                            perType.Clear();
                            baseTypes.Clear();
                            for (int i = 0; i < list.Count; i++)
                            {
                                var e = list[i];
                                if (e == null) continue;
                                Type et = e.GetType();
                                if (!baseTypes.TryGetValue(et, out var setList))
                                {
                                    setList = new List<HashSet<DXEntity>>();
                                    baseTypes.Add(et, setList);
                                    Type t = et;
                                    while (t != null && t != typeof(DXEntity))
                                    {
                                        if (!perType.TryGetValue(t, out var hs))
                                        {
                                            hs = new HashSet<DXEntity>();
                                            perType.Add(t, hs);
                                            rebuiltSets++;
                                        }
                                        setList.Add(hs);
                                        t = t.BaseType;
                                    }
                                }
                                for (int j = 0; j < setList.Count; j++)
                                    setList[j].Add(e);
                            }
                        }
                        ModLogger.Info($"[GameBridge] Rebuilt {rebuiltSets} _EntitiesPerType sets deterministically.");
                    }
                }

                // Flush deferred DXEntity.CreateBody() calls queued during async level load, then sort
                // DXPhysicWorld.Bodies in place WITHOUT calling Clear() — InitLevel also registers ~358
                // static terrain/gate colliders directly via DXPhysicBody.CreateRectangle(userData:null
                // or gate sub-bodies) that CreateBody() does not recreate.
                AccessTools.Method(typeof(DXPhysicWorld), "FinishPhysics")?.Invoke(null, null);
                var bodies = DXPhysicWorld.Bodies;
                if (bodies != null && bodies.Count > 1)
                {
                    bodies.RemoveAll(b => b == null || b.Handle == IntPtr.Zero);
                    bodies.Sort(CompareBodiesDeterministically);
                    var bodyIndexField = AccessTools.Field(typeof(DXPhysicBody), "_Index");
                    if (bodyIndexField != null)
                    {
                        for (int i = 0; i < bodies.Count; i++)
                        {
                            if (bodies[i] != null)
                                bodyIndexField.SetValue(bodies[i], i);
                        }
                    }
                    ModLogger.Info($"[GameBridge] Re-ordered {bodies.Count} physics bodies deterministically (preserved terrain/gate colliders).");
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SortEntitiesById failed: {ex.InnerException ?? ex}");
            }
        }

        // ====================================================================
        // Deterministic clock / RNG / fog / camera helpers (P0 & P1 fixes).
        // ====================================================================

        /// <summary>
        /// Re-seats the 30Hz logic clock and the 12Hz physics clock on the current GameTime so both
        /// peers start a run with an identical zero-phase boundary. NEVER set these to 0.0: on a
        /// late-game save (GameTime ≈ 5.6e6 ms) that makes UpdatePhysics run tens of thousands of
        /// steps in one frame and desyncs the two processes.
        /// </summary>
        public static void AlignSimulationClocks()
        {
            var game = DXGame.Current;
            if (game == null) return;
            try
            {
                double gt = game.GameTime;
                DXGame.LastGameTimeIAUpdate = gt;
                DXPhysicWorld.LastGameTimeUpdatePhysics = gt;
                DXPhysicWorld.TimeBodyPosition = gt;
                DXPhysicWorld.TimeLastBodyPosition = gt;
                ModLogger.Info($"[GameBridge] Simulation clocks aligned at GameTime={gt:F1}ms.");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] AlignSimulationClocks failed: {ex.Message}");
            }
        }

        /// <summary>Re-seeds DXLevel.Current.Random (used by mayor selection / random events).</summary>
        public static void SeedLevelRandom(int seed)
        {
            try
            {
                var level = DXLevel.Current;
                if (level == null) return;
                level.Random = new DXRandom(seed);
                ModLogger.Info($"[GameBridge] Re-seeded DXLevel.Current.Random (seed={seed}).");
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] SeedLevelRandom failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Makes every entity visible immediately. Beyond flipping ShowFullMap/FogEnabled, this clears
        /// the three ZXSystemMapLayers watch arrays and forces SceneObject.Opacity=1/Visible=true:
        /// CInvisibleWhenNotWatched uses the Point overload that ignores FogEnabled and only fades in
        /// by +0.09 every 4 logic ticks, so without this reveal_map shows an empty map for ~45 ticks.
        /// </summary>
        public static bool RevealAll(bool reveal)
        {
            bool ok = SetShowFullMap(reveal);
            SetFogEnabled(!reveal);

            try
            {
                if (!reveal) return ok;

                object inst = GetMapLayersInstance();
                if (inst != null)
                {
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_VisibleCells);
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_VisibleBackBuf);
                    ClearIntArray(inst, ObfuscatedSymbols.Field_MapLayers_ExploredCells);
                    SetInstanceBool(inst, "LayerFogUpdated", true);
                    SetInstanceBool(inst, "LayerShadowUpdated", true);
                }

                var game = DXGame.Current;
                if (game?.Entities != null)
                {
                    foreach (var e in game.Entities)
                    {
                        var so = e?.SceneObject;
                        if (so == null) continue;
                        so.Opacity = 1f;
                        so.Visible = true;
                    }
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] RevealAll failed: {ex.Message}");
            }
            return ok;
        }

        private static object GetMapLayersInstance()
        {
            if (_mapLayersType == null)
                _mapLayersType = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXSystemMapLayers);
            if (_mapLayersType == null) return null;
            if (_mapLayersCurrentField == null)
                _mapLayersCurrentField = _mapLayersType.GetField(ObfuscatedSymbols.Field_MapLayers_Current,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            return _mapLayersCurrentField?.GetValue(null);
        }

        private static void ClearIntArray(object instance, string fieldName)
        {
            var f = _mapLayersType?.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(instance) is int[] arr) Array.Clear(arr, 0, arr.Length);
        }

        private static void SetInstanceBool(object instance, string propName, bool value)
        {
            var p = _mapLayersType?.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) p.SetValue(instance, value);
        }

        /// <summary>
        /// Centers the camera on a WORLD point. entity.Position is world-grid; CameraArea is isometric
        /// scene space, so the point must go through DXWorldGrid.ScenePointFromWorldPoint. Also clears
        /// the opening zoom animation flag which otherwise overwrites CameraArea every frame.
        /// </summary>
        public static bool SetCameraWorld(PointF worldCenter, float width = 0f, float height = 0f)
        {
            var scene = DXScene.Current;
            if (scene == null) return false;

            DisableCameraIntroAnimation();

            RectangleF cur = scene.CameraArea;
            if (cur.Width <= 0f || cur.Height <= 0f) return false;
            float aspect = cur.Width / cur.Height;

            float w = width;
            if (w <= 0f && height > 0f) w = height * aspect;
            if (w <= 0f) w = cur.Width;
            if (w <= 0f) return false;

            float maxW = Math.Max(15f, 45f * aspect);
            w = Math.Max(15f, Math.Min(w, maxW));

            var scenePt = DXWorldGrid.ScenePointFromWorldPoint(worldCenter);
            scene.SetCameraCenter(scenePt, w);
            ModLogger.Info($"[GameBridge] Camera world=({worldCenter.X:F1},{worldCenter.Y:F1}) -> scene=({scenePt.X:F1},{scenePt.Y:F1}) width={w:F1}");
            return true;
        }

        private static FieldInfo _fLevelSysHudRoot;
        private static FieldInfo _fLevelSysIntroCinematic;
        private static FieldInfo _fLevelSysScreenAnim;

        private static void EnsureCameraIntroFields()
        {
            if (TypeZXSystemGameLevel == null) return;
            if (_fLevelSysHudRoot == null)
                _fLevelSysHudRoot = AccessTools.Field(TypeZXSystemGameLevel, "#=zNjT9FdpMLDa2");
            if (_fLevelSysIntroCinematic == null)
                _fLevelSysIntroCinematic = AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_CameraIntroAnim);
            if (_fLevelSysScreenAnim == null)
                _fLevelSysScreenAnim = AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_ScreenAnim);
        }

        public static void EnforceDisabledCameraIntroForLevel(object levelSys)
        {
            try
            {
                EnsureCameraIntroFields();
                if (_fLevelSysIntroCinematic != null && _fLevelSysIntroCinematic.GetValue(null) is bool intro && intro)
                {
                    _fLevelSysIntroCinematic.SetValue(null, false);
                    _fLevelSysScreenAnim?.SetValue(null, null);
                    var rootObj = DXScene.Current?.RootObject;
                    if (rootObj != null)
                    {
                        rootObj.Blur = 0f;
                        if (rootObj.State != null) rootObj.State.Blur = 0f;
                    }
                }

                if (levelSys != null && _fLevelSysHudRoot != null && _fLevelSysHudRoot.GetValue(levelSys) is DXObject hudObj)
                {
                    bool clearedAnim = false;
                    if (hudObj.Animation != null && hudObj.Animation.Animating)
                    {
                        hudObj.Animation.Clear();
                        clearedAnim = true;
                    }
                    var sp = DXScreenParameters.Current;
                    if (sp != null && !sp.ViewArea.IsEmpty && (clearedAnim || Math.Abs(hudObj.Y - sp.ViewArea.Y) > 0.5f))
                    {
                        hudObj.Area = sp.ViewArea;
                        hudObj.Opacity = 1f;
                    }
                }
            }
            catch { }
        }

        public static void DisableCameraIntroAnimation()
        {
            try
            {
                EnsureCameraIntroFields();
                _fLevelSysIntroCinematic?.SetValue(null, false);
                _fLevelSysScreenAnim?.SetValue(null, null);

                var rootObj = DXScene.Current?.RootObject;
                if (rootObj != null)
                {
                    rootObj.Blur = 0f;
                    if (rootObj.State != null) rootObj.State.Blur = 0f;
                }

                var levelSys = LevelSystemInstance;
                if (levelSys != null && _fLevelSysHudRoot != null && _fLevelSysHudRoot.GetValue(levelSys) is DXObject hudObj)
                {
                    if (hudObj.Animation != null && hudObj.Animation.Animating)
                        hudObj.Animation.Clear();
                    var sp = DXScreenParameters.Current;
                    if (sp != null && !sp.ViewArea.IsEmpty && Math.Abs(hudObj.Y - sp.ViewArea.Y) > 0.5f)
                    {
                        hudObj.Area = sp.ViewArea;
                    }
                    hudObj.Opacity = 1f;
                }
            }
            catch { }
        }

        public static string GetHudDiagSummary()
        {
            try
            {
                var levelSys = LevelSystemInstance;
                var fHud = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, "#=zNjT9FdpMLDa2") : null;
                var fIntro = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_CameraIntroAnim) : null;
                var fIntroTime = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, "#=zIOUagVzqxvgnQeV3rA==") : null;
                var fIntroBlur = TypeZXSystemGameLevel != null ? AccessTools.Field(TypeZXSystemGameLevel, "#=z40N_lqqB8daJ") : null;
                var fIsLoadSave = TypeZXGame != null ? AccessTools.Field(TypeZXGame, "#=zAVyuAmKGGVLXLOjjSQ==") : null;

                var hudObj = levelSys != null && fHud != null ? fHud.GetValue(levelSys) as DXObject : null;
                bool introFlag = fIntro?.GetValue(null) is bool b && b;
                int introStart = fIntroTime?.GetValue(null) is int t ? t : -1;
                var introBlur = fIntroBlur?.GetValue(null) as DXAnimation;
                var gx = _mZXGameCurrent?.Invoke(null, null);
                bool isLoadSave = gx != null && fIsLoadSave?.GetValue(gx) is bool ls && ls;

                var scene = DXScene.Current;
                var viewArea = DXScreenParameters.Current?.ViewArea ?? Rectangle.Empty;
                var cam = scene?.CameraArea ?? RectangleF.Empty;
                var screen = scene != null ? DXScene.ScreenArea : Rectangle.Empty;
                float unit = DXScene.Unit;
                float rootBlur = scene?.RootObject?.Blur ?? -1f;
                int vtime = scene?.VirtualTimeInt ?? -1;

                string clipDesc = "";
                if (hudObj?.Animation != null && hudObj.Animation.Clips != null && hudObj.Animation.Clips.Count > 0)
                {
                    var parts = new System.Collections.Generic.List<string>();
                    foreach (var cl in hudObj.Animation.Clips)
                    {
                        foreach (var tr in cl.Transitions)
                        {
                            parts.Add($"[dur={tr.Duration},loc={tr.RangeLocation},op={tr.RangeOpacity}]");
                        }
                    }
                    clipDesc = ",clips=" + string.Join(";", parts);
                }

                string hudStr = hudObj != null
                    ? $"({hudObj.X:F1},{hudObj.Y:F1},{hudObj.Width:F1}x{hudObj.Height:F1},anim={hudObj.Animation?.Animating}{clipDesc},vis={hudObj.Visible},style={hudObj.State?.AreaStyle})"
                    : "null";

                return $"hud={hudStr} intro={introFlag}(t0={introStart},vt={vtime}) blurAnim={(introBlur != null ? (introBlur.Finished ? "done" : "running") : "null")} rootBlur={rootBlur:F3} isLoadSave={isLoadSave} unit={unit:F3} view={viewArea} screen={screen} cam=({cam.X:F1},{cam.Y:F1},{cam.Width:F1}x{cam.Height:F1})";
            }
            catch (Exception ex)
            {
                return $"err:{ex.Message}";
            }
        }

        /// <summary>
        /// Returns the entity Type produced by a Build / EntityBonusCreation command (both internal,
        /// so the caller passes them as object). Used to capture And replay building placements.
        /// </summary>
        public static Type GetBuildingTypeFromCommand(object command)
        {
            if (command == null) return null;
            Type t = command.GetType();
            var entityTypeProp = t.GetProperty("EntityType", BindingFlags.Public | BindingFlags.Instance);
            if (entityTypeProp != null)
            {
                try { if (entityTypeProp.GetValue(command) is Type et) return et; } catch { }
            }
            var entityProp = t.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);
            if (entityProp != null)
            {
                try { return (entityProp.GetValue(command) as DXEntity)?.GetType(); } catch { }
            }
            return null;
        }


        public static List<DXEntity> GetPlayerEntities()
        {
            var result = new List<DXEntity>();
            var game = DXGame.Current;
            if (game?.Entities == null) return result;

            foreach (var entity in game.Entities)
            {
                if (entity == null || entity.Disposed) continue;
                if ((entity.ID & 0x8000000000000000UL) != 0) continue;
                if (IsPlayerTeam(entity)) result.Add(entity);
            }
            result.Sort((a, b) => a.ID.CompareTo(b.ID));
            return result;
        }

        public static List<DXEntity> GetPlayerEntitiesByTypeName(string typeNameSubstring, int maxCount = int.MaxValue)
        {
            var result = new List<DXEntity>();
            if (string.IsNullOrEmpty(typeNameSubstring)) return result;

            foreach (var entity in GetPlayerEntities())
            {
                if (entity.GetType().Name.IndexOf(typeNameSubstring, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    result.Add(entity);
                    if (result.Count >= maxCount) break;
                }
            }
            return result;
        }

        public static bool IsPlayerEntity(object entity) => IsPlayerTeam(entity);

        private static bool IsPlayerTeam(object entity)
        {
            Type t = entity.GetType();
            if (!_teamPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                _teamPropCache[t] = prop;
            }
            if (prop == null) return false;
            try { return string.Equals(prop.GetValue(entity)?.ToString(), "Player", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static DXEntity GetCommandCenterEntity()
        {
            var ls = CurrentLevelState;
            if (ls == null) return null;
            if (_ccProp == null)
                _ccProp = LevelStateType?.GetProperty("CurrentCommandCenter", BindingFlags.Public | BindingFlags.Instance);
            if (_ccProp == null) return null;
            try { return _ccProp.GetValue(ls) as DXEntity; } catch { return null; }
        }

        public static object GetLifeObject(object entity)
        {
            if (entity == null || _mGetEntityLife == null) return null;
            if (!_mGetEntityLife.DeclaringType.IsInstanceOfType(entity)) return null;
            if (_fastGetLifeObj != null)
            {
                try { return _fastGetLifeObj(entity); } catch { return null; }
            }
            try { return _mGetEntityLife.Invoke(entity, null); } catch { return null; }
        }

        public static int GetLifeValue(object entity)
        {
            object life = GetLifeObject(entity);
            if (life == null) return 0;
            if (_fastGetLifeInt != null && _fastGetLifeIntDeclType != null && _fastGetLifeIntDeclType.IsInstanceOfType(life))
            {
                try { return _fastGetLifeInt(life); } catch { return 0; }
            }
            Type t = life.GetType();
            if (!_lifePropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance);
                _lifePropCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(life)); } catch { return 0; }
        }

        private static readonly Dictionary<Type, PropertyInfo> _isAlivePropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _deathProcessedPropCache = new Dictionary<Type, PropertyInfo>();

        /// <summary>
        /// Host-authoritative HP correction for async alignment. Applies lifeDelta to the current
        /// CLife.Life (or forces 0 when forceKill) only while the entity is still alive locally; the
        /// engine's own CLife update then runs normal death processing on the next tick.
        /// </summary>
        public static bool TryCorrectLife(object entity, int lifeDelta, bool forceKill)
        {
            object life = GetLifeObject(entity);
            if (life == null) return false;
            Type t = life.GetType();
            try
            {
                if (!_lifePropCache.TryGetValue(t, out PropertyInfo lifeProp))
                {
                    lifeProp = t.GetProperty("Life", BindingFlags.Public | BindingFlags.Instance);
                    _lifePropCache[t] = lifeProp;
                }
                if (lifeProp == null || !lifeProp.CanWrite) return false;

                if (!_isAlivePropCache.TryGetValue(t, out PropertyInfo aliveProp))
                {
                    aliveProp = t.GetProperty("IsAlive", BindingFlags.Public | BindingFlags.Instance);
                    _isAlivePropCache[t] = aliveProp;
                }
                if (!_deathProcessedPropCache.TryGetValue(t, out PropertyInfo deadProp))
                {
                    deadProp = t.GetProperty("DeathProccessed", BindingFlags.Public | BindingFlags.Instance);
                    _deathProcessedPropCache[t] = deadProp;
                }
                if (aliveProp != null && !(bool)aliveProp.GetValue(life)) return false;
                if (deadProp != null && (bool)deadProp.GetValue(life)) return false;

                int cur = Convert.ToInt32(lifeProp.GetValue(life));
                int next = forceKill ? 0 : cur + lifeDelta;
                if (next < 0) next = 0;
                if (next == cur) return false;
                lifeProp.SetValue(life, next);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Computes a deterministic aggro/alert signature for a Zombie entity without touching internal
        /// types at compile time. Returns 0 when idle/roaming so pure micro-physics nudges never trip aggro alerts.
        /// </summary>
        public static long GetZombieAggroSignature(DXEntity entity, out bool isAlerted)
        {
            isAlerted = false;
            if (entity == null || _typeZombie == null || !_typeZombie.IsInstanceOfType(entity))
                return 0L;

            try
            {
                if (!_zombieAggroReflectionInit)
                {
                    _zombieAggroReflectionInit = true;
                    _propZombieAttackCC = _typeZombie.GetProperty("AttackCommandCenter", BindingFlags.Public | BindingFlags.Instance);
                }

                bool attackCC = _propZombieAttackCC != null && (bool)_propZombieAttackCC.GetValue(entity);

                int moveMode = 0;
                if (_mGetEntityMovable != null)
                {
                    object movable = _mGetEntityMovable.Invoke(entity, null);
                    if (movable != null)
                    {
                        if (_propMovableMode == null)
                            _propMovableMode = movable.GetType().GetProperty("MovementMode", BindingFlags.Public | BindingFlags.Instance);
                        if (_propMovableMode != null)
                            moveMode = Convert.ToInt32(_propMovableMode.GetValue(movable));
                    }
                }

                ulong targetId = 0;
                bool hasLastEnemy = false;
                if (_mGetEntityBehaviour != null)
                {
                    object cbehav = _mGetEntityBehaviour.Invoke(entity, null);
                    if (cbehav != null)
                    {
                        if (_propCBehavBehaviour == null)
                            _propCBehavBehaviour = cbehav.GetType().GetProperty("Behaviour", BindingFlags.Public | BindingFlags.Instance);
                        object behav = _propCBehavBehaviour?.GetValue(cbehav);
                        if (behav != null)
                        {
                            if (_propBehavData == null)
                                _propBehavData = behav.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                            object bdata = _propBehavData?.GetValue(behav);
                            if (bdata != null)
                            {
                                Type bType = bdata.GetType();
                                if (_fieldBDataTargetRef == null)
                                    _fieldBDataTargetRef = bType.GetField(ObfuscatedSymbols.Field_BData_TargetRef, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (_fieldBDataTargetRef != null)
                                {
                                    object boxedRef = _fieldBDataTargetRef.GetValue(bdata);
                                    if (boxedRef != null)
                                    {
                                        if (_propTargetRefId == null)
                                            _propTargetRefId = boxedRef.GetType().GetProperty("IDEntity", BindingFlags.Public | BindingFlags.Instance);
                                        if (_propTargetRefId != null)
                                            targetId = Convert.ToUInt64(_propTargetRefId.GetValue(boxedRef));
                                    }
                                }

                                if (_propBDataLastEnemy == null)
                                    _propBDataLastEnemy = bType.GetProperty("LastEnemyPosition", BindingFlags.Public | BindingFlags.Instance);
                                if (_propBDataLastEnemy != null)
                                {
                                    object lastEnemyObj = _propBDataLastEnemy.GetValue(bdata);
                                    hasLastEnemy = lastEnemyObj != null;
                                }
                            }
                        }
                    }
                }

                // TMovementMode: Walk=0, WalkFast=1, Run=2, Jump=3
                bool chasingMode = moveMode >= 2;
                if (targetId == 0 && !hasLastEnemy && !chasingMode && !attackCC)
                    return 0L;

                isAlerted = true;
                long sig = (long)entity.ID
                    ^ ((long)targetId * 73856093L)
                    ^ ((long)moveMode * 19349663L)
                    ^ (hasLastEnemy ? 83492791L : 0L)
                    ^ (attackCC ? 2654435761L : 0L);
                return sig != 0L ? sig : 1L;
            }
            catch
            {
                return 0L;
            }
        }

        /// <summary>
        /// Returns true if the entity has significant physics velocity or an active CMovable
        /// movement target, so mid-march player units are not nudged unnecessarily on sub-threshold drift.
        /// </summary>
        public static bool IsEntityActivelyMoving(DXEntity entity)
        {
            if (entity == null || entity.Disposed) return false;
            try
            {
                var body = entity.Body;
                if (body != null && body.Handle != IntPtr.Zero)
                {
                    var v = body.Velocity;
                    if (v.X * v.X + v.Y * v.Y > 0.04f)
                        return true;
                }

                if (_mGetEntityMovable != null && _mGetEntityMovable.DeclaringType.IsInstanceOfType(entity))
                {
                    object movable = _mGetEntityMovable.Invoke(entity, null);
                    if (movable != null)
                    {
                        if (_fieldMovableTargetPos == null)
                            _fieldMovableTargetPos = movable.GetType().GetField("#=zx1L3mWRqDcT7", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_fieldMovableTargetPos != null && _fieldMovableTargetPos.GetValue(movable) != null)
                            return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        /// <summary>
        /// Returns the minimum positional drift (in cells) required before async correction snaps an
        /// entity, along with the damping factor applied to sub-2.2-cell nudges.
        /// </summary>
        public static float GetEntityPositionSnapThreshold(DXEntity entity, out float dampingFactor)
        {
            if (entity == null)
            {
                dampingFactor = 0.65f;
                return 1.0f;
            }

            if (IsPlayerEntity(entity))
            {
                if (IsEntityActivelyMoving(entity))
                {
                    dampingFactor = 0.60f;
                    return 1.80f;
                }
                dampingFactor = 0.75f;
                return 0.50f;
            }

            if (IsZombieType(entity.GetType()))
            {
                GetZombieAggroSignature(entity, out bool isAlerted);
                if (isAlerted)
                {
                    dampingFactor = 0.70f;
                    return IsEntityActivelyMoving(entity) ? 0.85f : 0.55f;
                }
                dampingFactor = 0.60f;
                return 1.25f;
            }

            dampingFactor = 0.65f;
            return 1.00f;
        }

        public static object GetCommandableObject(object entity)
        {
            if (entity == null || _mGetEntityCmd == null) return null;
            if (!_mGetEntityCmd.DeclaringType.IsInstanceOfType(entity)) return null;
            if (_fastGetCmdable != null)
            {
                try { return _fastGetCmdable(entity); } catch { return null; }
            }
            try { return _mGetEntityCmd.Invoke(entity, null); } catch { return null; }
        }

        public static IList GetOrdersQueued(object cmdComp)
        {
            if (cmdComp == null) return null;
            if (_fastGetOrdersQueued != null && _fastGetOrdersQueuedDeclType != null && _fastGetOrdersQueuedDeclType.IsInstanceOfType(cmdComp))
            {
                try { return _fastGetOrdersQueued(cmdComp); } catch { return null; }
            }
            Type t = cmdComp.GetType();
            if (!_ordersPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("OrdersQueued", BindingFlags.Public | BindingFlags.Instance);
                _ordersPropCache[t] = prop;
            }
            if (prop == null) return null;
            try { return prop.GetValue(cmdComp) as IList; } catch { return null; }
        }

        public static int GetOrderCommandId(object order)
        {
            if (order == null) return 0;
            Type t = order.GetType();
            if (!_idCommandCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("IDCommand", BindingFlags.Public | BindingFlags.Instance);
                _idCommandCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(order)); } catch { return 0; }
        }

        private static readonly Dictionary<Type, PropertyInfo> _idCommandCache = new Dictionary<Type, PropertyInfo>();

        public static object GetCommandGeneric(Type commandType)
        {
            var generic = AccessTools.Method(typeof(ZXCommand), ObfuscatedSymbols.Method_ZXCommand_GetGeneric);
            if (generic == null || !generic.IsGenericMethodDefinition) return null;
            return generic.MakeGenericMethod(commandType).Invoke(null, null);
        }

        // ====================================================================
        // Command execution internals (all object + reflection; ZXCommand,
        // ZXCommandTarget, Build, Train, CCommandable and CMovable are internal).
        // ====================================================================

        private static readonly Dictionary<Type, PropertyInfo> _cmdIdPropCache = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _cmdableTargetPropCache = new Dictionary<Type, PropertyInfo>();
        private static MethodInfo _mBuildGetForType;
        private static MethodInfo _mTrainGetForType;

        private static Type _typeZXCommand;
        private static Type _typeCMovable;
        private static Type _typeCCommandable;
        private static Type[] _groupRoutedCommandTypes;
        private static readonly Dictionary<Type, PropertyInfo> _cmdIndicatorColorCache = new Dictionary<Type, PropertyInfo>();

        public static Type TypeZXCommand
        {
            get
            {
                if (_typeZXCommand == null) _typeZXCommand = GameAssembly?.GetType("ZX.Commands.ZXCommand");
                return _typeZXCommand;
            }
        }

        public static Type TypeZXEntity => _mCmdExec?.GetParameters()[0].ParameterType;
        public static Type TypeZXCommandTarget => _mCmdExec?.GetParameters()[1].ParameterType;

        public static Type TypeCMovable
        {
            get
            {
                if (_typeCMovable == null) _typeCMovable = GameAssembly?.GetType("ZX.Components.CMovable");
                return _typeCMovable;
            }
        }

        public static Type TypeCCommandable
        {
            get
            {
                if (_typeCCommandable == null) _typeCCommandable = _mGetEntityCmd?.ReturnType ?? GameAssembly?.GetType("ZX.Components.CCommandable");
                return _typeCCommandable;
            }
        }

        public static int GetCommandId(object command)
        {
            if (command == null) return 0;
            Type t = command.GetType();
            if (!_cmdIdPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("ID", BindingFlags.Public | BindingFlags.Instance);
                _cmdIdPropCache[t] = prop;
            }
            if (prop == null) return 0;
            try { return Convert.ToInt32(prop.GetValue(command)); } catch { return 0; }
        }

        public static ulong[] GetSelectedPlayerEntityIds()
        {
            var listObj = _fieldSelectedList?.GetValue(null) as IEnumerable;
            if (listObj == null) return Array.Empty<ulong>();

            var ids = new List<ulong>();
            foreach (object item in listObj)
            {
                var comp = item as DXComponent;
                var ent = comp?.Entity;
                if (ent == null || ent.Disposed) continue;
                if (HasComponent(ent, TypeCMovable) && HasComponent(ent, TypeCCommandable) && IsPlayerEntity(ent))
                    ids.Add(ent.ID);
            }
            ids.Sort();
            return ids.ToArray();
        }

        public static bool IsGroupRoutedCommand(object command)
        {
            if (command == null) return false;
            if (_groupRoutedCommandTypes == null && GameAssembly != null)
            {
                _groupRoutedCommandTypes = new[]
                {
                    GameAssembly.GetType("ZX.Commands.Travel"),
                    GameAssembly.GetType("ZX.Commands.Patrol"),
                    GameAssembly.GetType("ZX.Commands.Chase"),
                    GameAssembly.GetType("ZX.Commands.UseItem_Grenade")
                };
            }
            if (_groupRoutedCommandTypes == null) return false;
            for (int i = 0; i < _groupRoutedCommandTypes.Length; i++)
            {
                var t = _groupRoutedCommandTypes[i];
                if (t != null && t.IsInstanceOfType(command)) return true;
            }
            return false;
        }

        /// <summary>
        /// Inspects a ZX.Commands.EntityBonusCreation command and extracts its slot, entity type,
        /// whether the rewarded entity is a Structure (placed on map) vs Unit (spawned at CommandCenter),
        /// and its DXEntityTemplate.
        /// </summary>
        public static bool TryGetEntityBonusInfo(object command, out int slot, out Type entityType, out bool isStructure, out DXEntityTemplate template)
        {
            slot = 0;
            entityType = null;
            isStructure = false;
            template = null;
            if (command == null || _typeEntityBonusCreation == null || !_typeEntityBonusCreation.IsInstanceOfType(command))
                return false;

            try
            {
                if (_propBonusCmdNSlot != null)
                    slot = Convert.ToInt32(_propBonusCmdNSlot.GetValue(command));
                template = _propBonusCmdTemplate?.GetValue(command) as DXEntityTemplate;
                DXEntity entity = template?.Entity ?? (_propBonusCmdEntity?.GetValue(command) as DXEntity);
                if (entity != null)
                {
                    entityType = entity.GetType();
                    isStructure = _typeStructure != null && _typeStructure.IsInstanceOfType(entity);
                }
            }
            catch { }
            return true;
        }

        public static bool TryGetExtractSlotInfo(object command, out int slot)
        {
            slot = 0;
            if (command == null || _typeExtractFromSlot == null || !_typeExtractFromSlot.IsInstanceOfType(command))
                return false;
            try
            {
                if (_propExtractCmdNSlot != null)
                    slot = Convert.ToInt32(_propExtractCmdNSlot.GetValue(command));
            }
            catch { }
            return true;
        }

        public static bool IsBuildOrBonusCommand(object command, out bool isBonus)
        {
            isBonus = false;
            if (command == null) return false;
            if (TryGetEntityBonusInfo(command, out _, out Type entityType, out bool isStructure, out _))
            {
                if (isStructure && entityType != null)
                {
                    isBonus = true;
                    return true;
                }
                // Bonus Units (TargetType.None) spawn next to the CommandCenter; they are NOT placed via PlaceBuilding.
                return false;
            }
            return string.Equals(command.GetType().Name, "Build", StringComparison.Ordinal);
        }

        public static int GetBonusEntityCount(Type entityType, out DXEntityTemplate matchedTemplate)
        {
            matchedTemplate = null;
            if (entityType == null) return 0;
            object ls = CurrentLevelState;
            var bonusDict = ls != null ? GetLevelProperty("BonusEntityTemplates")?.GetValue(ls) as Dictionary<ulong, int> : null;
            if (bonusDict == null || bonusDict.Count == 0) return 0;

            DXEntityTemplate directTpl = GetEntityTemplateByType(entityType);
            if (directTpl != null && bonusDict.TryGetValue(directTpl.ID, out int directCount) && directCount > 0)
            {
                matchedTemplate = directTpl;
                return directCount;
            }

            var proj = DXProject.Current;
            if (proj == null) return 0;
            foreach (var kv in bonusDict)
            {
                if (kv.Value <= 0) continue;
                var tpl = proj.EntityTemplateFromID(kv.Key);
                if (tpl?.Entity != null && tpl.Entity.GetType() == entityType)
                {
                    matchedTemplate = tpl;
                    return kv.Value;
                }
            }
            return 0;
        }

        public static void ConsumeBonusEntityByType(Type entityType)
        {
            if (entityType == null || _methodLevelStateConsumeBonus == null) return;
            object ls = CurrentLevelState;
            if (ls == null) return;
            if (GetBonusEntityCount(entityType, out DXEntityTemplate tpl) > 0 && tpl != null)
            {
                try { _methodLevelStateConsumeBonus.Invoke(ls, new object[] { tpl }); }
                catch (Exception ex) { ModLogger.Warn($"[GameBridge] ConsumeBonusEntityByType({entityType.Name}) failed: {ex.Message}"); }
            }
        }

        public static void CancelPendingBonusCommandIfType(Type buildingType)
        {
            if (buildingType == null) return;
            object pending = GetPendingCommandOnLevelSys();
            if (pending == null) return;
            if (!TryGetEntityBonusInfo(pending, out _, out Type pendingEntityType, out _, out _))
                return;
            // After ConsumeBonusEntityByType removes the last charge, pendingEntityType may already be null or still point at a different shifted slot.
            if (pendingEntityType == null || pendingEntityType == buildingType)
            {
                object levelSys = LevelSystemInstance;
                if (levelSys != null && _methodLevelSysClearPendingMode != null)
                {
                    try { _methodLevelSysClearPendingMode.Invoke(levelSys, null); } catch { }
                }
            }
        }

        public static IList GetAvailableCommandsForEntity(object entity) => GetAvailableCommandsForExecutor(entity);

        private static IList GetAvailableCommandsForExecutor(object executor)
        {
            if (executor == null || _propCmdableAvailableCmds == null) return null;
            object cmdable = GetCommandableObject(executor);
            if (cmdable == null) return null;
            try { return _propCmdableAvailableCmds.GetValue(cmdable, null) as IList; }
            catch { return null; }
        }

        /// <summary>
        /// Resolves the command object for ExecuteSingleOrBatchCommand replay.
        /// Handles slot-indexed commands (EntityBonusCreation and ExtractFromUnitHolderSlot) whose base
        /// ZXCommand.ID collides across all slots, matching bonus commands by rewarded entity type name.
        /// </summary>
        public static object ResolveReplayCommand(int commandId, string typeOrSlotTag, byte slotIndex, object executor, out bool isSlotSensitive)
        {
            isSlotSensitive = !string.IsNullOrEmpty(typeOrSlotTag);
            if (!isSlotSensitive)
                return GetCommandById(commandId);

            if (string.Equals(typeOrSlotTag, "ZX.Commands.ExtractFromUnitHolderSlot", StringComparison.Ordinal))
            {
                IList avail = GetAvailableCommandsForExecutor(executor);
                if (avail != null)
                {
                    foreach (object c in avail)
                    {
                        if (TryGetExtractSlotInfo(c, out int s) && s == slotIndex)
                            return c;
                    }
                }
                object game = ZXGameInstance;
                var allCmds = game != null ? _fieldZXGameAllCommands?.GetValue(game) as IList : null;
                if (allCmds != null)
                {
                    foreach (object c in allCmds)
                    {
                        if (TryGetExtractSlotInfo(c, out int s) && s == slotIndex)
                            return c;
                    }
                }
                return null;
            }

            // EntityBonusCreation for a Unit (or any non-structure bonus): CommandCenter.#=z0rkMLsM_Q666() populates slots 1..12.
            IList ccAvail = GetAvailableCommandsForExecutor(executor)
                ?? GetAvailableCommandsForExecutor(GetCommandCenterEntity());
            if (ccAvail == null) return null;

            object fallbackMatch = null;
            foreach (object c in ccAvail)
            {
                if (!TryGetEntityBonusInfo(c, out int slot, out Type entityType, out bool isStructure, out _))
                    continue;
                if (isStructure || entityType == null)
                    continue;
                if (!string.Equals(entityType.FullName, typeOrSlotTag, StringComparison.Ordinal))
                    continue;
                if (slot == slotIndex)
                    return c;
                if (fallbackMatch == null)
                    fallbackMatch = c;
            }
            return fallbackMatch;
        }

        public static bool CanExecuteCommandOnEntity(object command, object executor)
        {
            if (command == null || executor == null) return true;
            try
            {
                if (_methodIsVisibleCmd != null && !(bool)_methodIsVisibleCmd.Invoke(command, new[] { executor }))
                    return false;
                if (_methodCanExecuteCmd != null && !(bool)_methodCanExecuteCmd.Invoke(command, new[] { executor }))
                    return false;
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] CanExecuteCommandOnEntity failed: {ex.Message}");
                return true;
            }
        }

        public static bool IsResourceSpendingCommand(object command, object executor)
        {
            if (command == null) return false;
            if (_typeCreateEntityCommand != null && _typeCreateEntityCommand.IsInstanceOfType(command))
                return true;
            if (executor != null && _methodGetResourcesCost != null)
            {
                try
                {
                    if (_methodGetResourcesCost.Invoke(command, new[] { executor }) != null)
                        return true;
                }
                catch { }
            }
            return false;
        }

        public static Rectangle ResolveBuildCellArea(DXEntity templateEntity, PointF targetPos, SizeF targetSize, PointF targetOrigin)
        {
            PointF effectivePos = targetPos != PointF.Empty ? targetPos : targetOrigin;
            int tw = (int)Math.Round(targetSize.Width);
            int th = (int)Math.Round(targetSize.Height);
            if (tw <= 0 || th <= 0)
            {
                if (templateEntity == null) return Rectangle.Empty;
                tw = Math.Max(1, templateEntity.CellArea.Width);
                th = Math.Max(1, templateEntity.CellArea.Height);
                int xFallback = (int)Math.Floor(effectivePos.X);
                int yFallback = (int)Math.Floor(effectivePos.Y);
                return new Rectangle(xFallback, yFallback, tw, th);
            }

            Rectangle targetRect = Rectangle.Truncate(new RectangleF(
                effectivePos.X - targetSize.Width * 0.5f,
                effectivePos.Y - targetSize.Height * 0.5f,
                targetSize.Width,
                targetSize.Height));

            if (templateEntity != null)
            {
                int tmplArea = templateEntity.CellArea.Width * templateEntity.CellArea.Height;
                int targetArea = targetRect.Width * targetRect.Height;
                if (tmplArea != targetArea)
                {
                    return new Rectangle(
                        targetRect.X,
                        targetRect.Y,
                        Math.Max(1, templateEntity.CellArea.Width),
                        Math.Max(1, templateEntity.CellArea.Height));
                }
            }
            return targetRect;
        }

        public static bool CanPlaceBuildingAtTarget(Type buildingType, PointF targetPos, SizeF targetSize, PointF targetOrigin)
        {
            if (buildingType == null) return false;
            if (string.Equals(buildingType.Name, "TheGreatTelescope", StringComparison.Ordinal))
                return true;

            try
            {
                DXGame zxGame = ZXGameInstance;
                if (zxGame == null || _methodGetTemplateFromEntityType == null || _methodEntityCanPlaceAtCell == null)
                    return true;

                var template = _methodGetTemplateFromEntityType.Invoke(zxGame, new object[] { buildingType }) as DXEntityTemplate;
                DXEntity templateEntity = template?.Entity;
                if (templateEntity == null) return true;

                _methodEntityUpdateCellPosition?.Invoke(templateEntity, null);

                Rectangle rect = ResolveBuildCellArea(templateEntity, targetPos, targetSize, targetOrigin);
                if (rect.Width <= 0 || rect.Height <= 0)
                    return true;

                PointF origPos = templateEntity.Position;
                SizeF origSize = templateEntity.Size;
                try
                {
                    templateEntity.CellArea = rect;
                    for (int x = rect.Left; x < rect.Right; x++)
                    {
                        for (int y = rect.Top; y < rect.Bottom; y++)
                        {
                            if (!(bool)_methodEntityCanPlaceAtCell.Invoke(templateEntity, new object[] { x, y }))
                                return false;
                        }
                    }
                    return true;
                }
                finally
                {
                    templateEntity.Size = origSize;
                    templateEntity.Position = origPos;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] CanPlaceBuildingAtTarget({buildingType.Name}) failed: {ex.Message}");
                return true;
            }
        }

        public static void FlushStartFramePostMethods()
        {
            try
            {
                DXGame.Current?.PostMethods_OnStartFrame?.ExecuteAll();
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] FlushStartFramePostMethods failed: {ex.Message}");
            }
        }

        public static void ShowGroundIndicatorForCommand(object command, PointF worldPos, float scale = 1f)
        {
            if (command == null || _methodShowGroundIndicator == null) return;
            object levelSys = _mLevelSysCurrent?.Invoke(null, null);
            if (levelSys == null) return;

            Type t = command.GetType();
            if (!_cmdIndicatorColorCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("TargetIndicatorColor", BindingFlags.Public | BindingFlags.Instance);
                _cmdIndicatorColorCache[t] = prop;
            }
            if (prop == null) return;

            try
            {
                object colorVal = prop.GetValue(command);
                if (colorVal != null)
                    _methodShowGroundIndicator.Invoke(levelSys, new[] { worldPos, colorVal, scale });
            }
            catch { }
        }

        /// <summary>
        /// Scan the entity's public component list instead of HasComponent&lt;T&gt;() — instantiating a
        /// generic method with an internal type argument trips the CLR's type-access check.
        /// </summary>
        public static bool HasComponent(DXEntity entity, Type componentType)
        {
            if (entity == null || componentType == null) return false;
            var comps = entity.Components;
            if (comps == null) return false;
            for (int i = 0; i < comps.Count; i++)
            {
                if (IsComponentOfType(comps[i], componentType)) return true;
            }
            return false;
        }

        private static bool IsComponentOfType(object component, Type componentType) => componentType.IsInstanceOfType(component);

        public static object GetCommandableTargetObject(object commandable)
        {
            if (commandable == null) return null;
            if (_fastGetCmdTarget != null && _fastGetCmdTargetDeclType != null && _fastGetCmdTargetDeclType.IsInstanceOfType(commandable))
            {
                try { return _fastGetCmdTarget(commandable); } catch { return null; }
            }
            Type t = commandable.GetType();
            if (!_cmdableTargetPropCache.TryGetValue(t, out PropertyInfo prop))
            {
                prop = t.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
                _cmdableTargetPropCache[t] = prop;
            }
            if (prop == null) return null;
            try { return prop.GetValue(commandable); } catch { return null; }
        }

        public static object GetBuildCommandForType(Type entityType) => GetCreateEntityCommandForType(ref _mBuildGetForType, "ZX.Commands.Build", entityType);
        public static object GetTrainCommandForType(Type entityType) => GetCreateEntityCommandForType(ref _mTrainGetForType, "ZX.Commands.Train", entityType);

        private static object GetCreateEntityCommandForType(ref MethodInfo cache, string typeName, Type entityType)
        {
            if (cache == null)
            {
                Type cmdType = GameAssembly?.GetType(typeName);
                if (cmdType == null) return null;
                cache = AccessTools.Method(cmdType, ObfuscatedSymbols.Method_BuildOrTrain_GetForType, new[] { typeof(Type) });
            }
            return cache?.Invoke(null, new object[] { entityType });
        }

        private static Type _typeZXCommandTargetRef;
        private static PropertyInfo _pSerPosition, _pSerSize, _pSerOrigin, _pSerEntityRef;
        private static Type _typeEntityRefClosed;
        private static PropertyInfo _pEntityRefEntity;
        private static PropertyInfo _pEntityRefId;
        private static FieldInfo _fEntityRefId;

        private static void EnsureCommandTargetMeta()
        {
            if (_typeZXCommandTargetRef != null) return;
            _typeZXCommandTargetRef = TypeZXCommandTarget;
            if (_typeZXCommandTargetRef == null) return;

            const BindingFlags F = BindingFlags.Public | BindingFlags.Instance;
            _pSerPosition = _typeZXCommandTargetRef.GetProperty("SerPosition", F);
            _pSerSize = _typeZXCommandTargetRef.GetProperty("SerSize", F);
            _pSerOrigin = _typeZXCommandTargetRef.GetProperty("SerOrigin", F);
            _pSerEntityRef = _typeZXCommandTargetRef.GetProperty("SerEntityRef", F);

            Type zxEntity = TypeZXEntity;
            if (zxEntity != null)
            {
                _typeEntityRefClosed = typeof(DXEntityRef<>).MakeGenericType(zxEntity);
                _pEntityRefEntity = _typeEntityRefClosed.GetProperty("Entity", F);
                _pEntityRefId = _typeEntityRefClosed.GetProperty("IDEntity", F);
                if (_pEntityRefId == null)
                    _fEntityRefId = _typeEntityRefClosed.GetField("IDEntity", F);
            }
        }

        public static void UnpackCommandTarget(object target, out ulong targetEntityId, out PointF pos, out SizeF size, out PointF origin)
        {
            targetEntityId = 0UL;
            pos = PointF.Empty;
            size = SizeF.Empty;
            origin = PointF.Empty;
            if (target == null) return;

            EnsureCommandTargetMeta();
            if (_typeZXCommandTargetRef == null) return;

            try
            {
                if (_pSerPosition?.GetValue(target) is PointF p) pos = p;
                if (_pSerSize?.GetValue(target) is SizeF s) size = s;
                if (_pSerOrigin?.GetValue(target) is PointF o) origin = o;
                object refObj = _pSerEntityRef?.GetValue(target);
                if (refObj != null)
                {
                    if (_pEntityRefId != null)
                        targetEntityId = Convert.ToUInt64(_pEntityRefId.GetValue(refObj));
                    else if (_fEntityRefId != null)
                        targetEntityId = Convert.ToUInt64(_fEntityRefId.GetValue(refObj));
                    else if (_pEntityRefEntity?.GetValue(refObj) is DXEntity refEnt)
                        targetEntityId = refEnt.ID;
                }
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] UnpackCommandTarget failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Builds a boxed ZXCommandTarget struct via reflection. Setting properties on the boxed
        /// value mutates the box in place, so the resulting object is a complete target.
        /// </summary>
        public static object CreateCommandTarget(PointF position, SizeF size, PointF origin, ulong targetEntityId)
        {
            EnsureCommandTargetMeta();
            if (_typeZXCommandTargetRef == null) return null;

            object box = Activator.CreateInstance(_typeZXCommandTargetRef);
            _pSerPosition?.SetValue(box, position);
            _pSerSize?.SetValue(box, size);
            _pSerOrigin?.SetValue(box, origin);

            if (targetEntityId != 0UL && _typeEntityRefClosed != null && _pEntityRefEntity != null && _pSerEntityRef != null)
            {
                var ent = DXGame.Current?.GetEntityByID(targetEntityId);
                if (ent != null)
                {
                    object refBox = Activator.CreateInstance(_typeEntityRefClosed);
                    _pEntityRefEntity.SetValue(refBox, ent);
                    _pSerEntityRef.SetValue(box, refBox);
                }
            }
            return box;
        }

        // ====================================================================
        // Mayor selection (ZX.ZXMayor). Identify the chosen mayor by its string ID and apply that
        // exact mayor on every peer — avoids re-running the male/female random pick per machine.
        // ====================================================================
        private static Type _typeZXMayor;
        private static FieldInfo _mayorRegistryField;
        private static PropertyInfo _mayorIdProp, _mayorNameProp, _mayorLevelProp, _mayorBonusEntitiesProp;
        private static MethodInfo _mayorApplyMethod;
        private static MethodInfo _levelStateIsChoosingMayorMethod;
        private static MethodInfo _levelStateSetChoosingMayorMethod;
        private static Type _typeZXWindow;
        private static MethodInfo _windowCloseMethod;

        private static void EnsureMayorMeta()
        {
            if (_typeZXMayor != null) return;
            _typeZXMayor = GameAssembly?.GetType("ZX.ZXMayor");
            if (_typeZXMayor != null)
            {
                const BindingFlags SF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                const BindingFlags IF = BindingFlags.Public | BindingFlags.Instance;
                _mayorRegistryField = _typeZXMayor.GetField(ObfuscatedSymbols.Field_ZXMayor_Registry, SF);
                _mayorIdProp = _typeZXMayor.GetProperty("ID", IF);
                _mayorNameProp = _typeZXMayor.GetProperty("Name", IF);
                _mayorLevelProp = _typeZXMayor.GetProperty("Level", IF);
                _mayorBonusEntitiesProp = _typeZXMayor.GetProperty("IDBonusEntities", IF);
                _mayorApplyMethod = AccessTools.Method(_typeZXMayor, ObfuscatedSymbols.Method_ZXMayor_Apply, new[] { LevelStateType });
                if (_mayorApplyMethod == null)
                {
                    foreach (var m in AccessTools.GetDeclaredMethods(_typeZXMayor))
                    {
                        if (m.Name == ObfuscatedSymbols.Method_ZXMayor_Apply && m.GetParameters().Length == 1)
                        { _mayorApplyMethod = m; break; }
                    }
                }
            }

            if (LevelStateType != null)
            {
                _levelStateIsChoosingMayorMethod = AccessTools.Method(LevelStateType, ObfuscatedSymbols.Method_ZXLevelState_IsChoosingMayor, Type.EmptyTypes);
                _levelStateSetChoosingMayorMethod = AccessTools.Method(LevelStateType, ObfuscatedSymbols.Method_ZXLevelState_SetChoosingMayor, new[] { typeof(bool) });
            }

            _typeZXWindow = GameAssembly?.GetType("ZX.GUI.ZXWindow");
            if (_typeZXWindow != null)
            {
                _windowCloseMethod = AccessTools.Method(_typeZXWindow, ObfuscatedSymbols.Method_ZXWindow_CloseWindow, new[] { typeof(Action) });
            }
        }

        public static List<Dictionary<string, object>> GetMayorList(int levelFilter)
        {
            EnsureMayorMeta();
            var result = new List<Dictionary<string, object>>();
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null) return result;
            foreach (DictionaryEntry kv in dict)
            {
                object mayor = kv.Value;
                if (mayor == null) continue;
                int lvl = _mayorLevelProp != null ? Convert.ToInt32(_mayorLevelProp.GetValue(mayor)) : 0;
                if (levelFilter > 0 && lvl != levelFilter) continue;
                result.Add(new Dictionary<string, object>
                {
                    ["id"] = _mayorIdProp != null ? Convert.ToString(_mayorIdProp.GetValue(mayor)) : Convert.ToString(kv.Key),
                    ["name"] = _mayorNameProp != null ? Convert.ToString(_mayorNameProp.GetValue(mayor)) : "?",
                    ["level"] = lvl,
                    ["bonusEntities"] = _mayorBonusEntitiesProp != null ? (Convert.ToString(_mayorBonusEntitiesProp.GetValue(mayor)) ?? string.Empty) : string.Empty
                });
            }
            return result;
        }

        public static int GetMayorLevel(object mayorObj)
        {
            EnsureMayorMeta();
            if (mayorObj == null || _mayorLevelProp == null) return 0;
            try { return Convert.ToInt32(_mayorLevelProp.GetValue(mayorObj)); }
            catch { return 0; }
        }

        public static string GetMayorNameById(string mayorId)
        {
            EnsureMayorMeta();
            if (string.IsNullOrEmpty(mayorId) || _mayorNameProp == null) return null;
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(mayorId)) return null;
            try { return _mayorNameProp.GetValue(dict[mayorId]) as string; }
            catch { return null; }
        }

        public static bool IsChoosingMayor()
        {
            EnsureMayorMeta();
            object ls = CurrentLevelState;
            if (ls == null || _levelStateIsChoosingMayorMethod == null) return false;
            try { return (bool)_levelStateIsChoosingMayorMethod.Invoke(ls, null); }
            catch { return false; }
        }

        public static void SetChoosingMayor(bool choosing)
        {
            EnsureMayorMeta();
            object ls = CurrentLevelState;
            if (ls == null || _levelStateSetChoosingMayorMethod == null) return;
            try { _levelStateSetChoosingMayorMethod.Invoke(ls, new object[] { choosing }); }
            catch (Exception ex) { ModLogger.Warn($"[GameBridge] SetChoosingMayor({choosing}) failed: {ex.Message}"); }
        }

        public static bool IsZXWindow(object obj)
        {
            EnsureMayorMeta();
            return obj != null && _typeZXWindow != null && _typeZXWindow.IsInstanceOfType(obj);
        }

        public static bool CloseZXWindow(object window, Action onClosed = null)
        {
            EnsureMayorMeta();
            if (window == null || _windowCloseMethod == null) return false;
            try
            {
                _windowCloseMethod.Invoke(window, new object[] { onClosed });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] CloseZXWindow failed: {ex.Message}");
                return false;
            }
        }

        // ====================================================================
        // Lobby Window & MainMenu Reuse Reflection Bridges (Iron Law #3 Safe)
        // ====================================================================
        private static Type _typeZXButtonSteam;
        private static Type _typeZXWindowTypeEnum;
        private static Type _typeZXWindowBtnPosEnum;
        private static Type _typeZXWindowAnimEnum;
        private static Type _typeZXFontCatalogFonts;
        private static PropertyInfo _windowContentProp;
        private static PropertyInfo _windowTitleProp;
        private static MethodInfo _windowShowMethod;
        private static MethodInfo _windowAddBottomBtnMethod;
        private static PropertyInfo _btnSteamTextProp;
        private static PropertyInfo _btnSteamOBarProp;
        private static PropertyInfo _btnSteamKeysProp;
        private static PropertyInfo _btnSteamSmallFontProp;
        private static FieldInfo _btnSteamLabelField;
        private static ConstructorInfo _btnSteamTextCtor;
        private static MethodInfo _methodReturnToMainMenu;
        private static MethodInfo _methodMainMenuLoadSave;
        private static MethodInfo _methodMainMenuNewSurvival;
        private static MethodInfo _methodMainMenuCustomMap;
        private static FieldInfo _fieldMainMenuContinueBtn;
        private static FieldInfo _fieldMainMenuReadyForInput;

        private static void EnsureLobbyUiMeta()
        {
            if (_typeZXButtonSteam != null) return;
            EnsureMayorMeta();

            _typeZXButtonSteam = GameAssembly?.GetType("ZX.GUI.ZXButtonSteam");
            if (_typeZXButtonSteam != null)
            {
                _btnSteamTextProp = AccessTools.Property(_typeZXButtonSteam, "Text");
                _btnSteamOBarProp = AccessTools.Property(_typeZXButtonSteam, "OBar");
                _btnSteamKeysProp = AccessTools.Property(_typeZXButtonSteam, "Keys");
                _btnSteamSmallFontProp = AccessTools.Property(_typeZXButtonSteam, "UseSmallFontStyle");
                _btnSteamLabelField = AccessTools.Field(_typeZXButtonSteam, "#=zMUT$V2s=");
                foreach (var c in _typeZXButtonSteam.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                    {
                        _btnSteamTextCtor = c;
                        break;
                    }
                }
            }

            if (_typeZXWindow != null)
            {
                _windowContentProp = AccessTools.Property(_typeZXWindow, "OContent");
                _windowTitleProp = AccessTools.Property(_typeZXWindow, "Title");
                _windowShowMethod = AccessTools.Method(_typeZXWindow, ObfuscatedSymbols.Method_ZXWindow_ShowWindow);
                _windowAddBottomBtnMethod = AccessTools.Method(_typeZXWindow, ObfuscatedSymbols.Method_ZXWindow_AddBottomButton);
                _typeZXWindowTypeEnum = _typeZXWindow.GetNestedType("TWindowType", BindingFlags.Public | BindingFlags.NonPublic);
                _typeZXWindowBtnPosEnum = _typeZXWindow.GetNestedType("TButtonPosition", BindingFlags.Public | BindingFlags.NonPublic);
                if (_windowShowMethod != null)
                {
                    var ps = _windowShowMethod.GetParameters();
                    if (ps.Length >= 2) _typeZXWindowAnimEnum = ps[1].ParameterType;
                }
            }

            if (TypeZXGame != null)
            {
                _methodReturnToMainMenu = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ReturnToMainMenu, new[] { typeof(Action), typeof(bool) });
            }

            if (TypeZXSystemMainMenu != null)
            {
                _methodMainMenuLoadSave = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnContinueLoad, new[] { typeof(DXButton) });
                _methodMainMenuNewSurvival = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnNewSurvival, new[] { typeof(DXButton) });
                _methodMainMenuCustomMap = AccessTools.Method(TypeZXSystemMainMenu, ObfuscatedSymbols.Method_MainMenu_OnCustomMap, new[] { typeof(DXButton) });
                _fieldMainMenuContinueBtn = AccessTools.Field(TypeZXSystemMainMenu, ObfuscatedSymbols.Field_MainMenu_ContinueButton);
                _fieldMainMenuReadyForInput = AccessTools.Field(TypeZXSystemMainMenu, ObfuscatedSymbols.Field_MainMenu_ReadyForInput);
            }

            _typeZXFontCatalogFonts = GameAssembly?.GetType(ObfuscatedSymbols.Type_ZXFontCatalogFonts);
        }

        public static bool IsOnMainMenu()
        {
            return MainMenuInstance != null;
        }

        public static bool IsAnyModalOpen()
        {
            try { return DXModalScreen.IsInModal; }
            catch { return false; }
        }

        public static bool HasAnySaveGames()
        {
            EnsureLobbyUiMeta();
            try
            {
                var menu = MainMenuInstance;
                if (menu != null && _fieldMainMenuContinueBtn != null)
                {
                    var btn = _fieldMainMenuContinueBtn.GetValue(menu) as DXButton;
                    if (btn != null) return btn.Enabled;
                }
                string savesDir = GetSavesDirectory();
                return !string.IsNullOrEmpty(savesDir) && System.IO.Directory.Exists(savesDir) && System.IO.Directory.GetFiles(savesDir, "*.zxsav").Length > 0;
            }
            catch { return false; }
        }

        public static bool OpenMainMenuLoadSave()
        {
            return InvokeMainMenuAction(_methodMainMenuLoadSave, "OpenMainMenuLoadSave");
        }

        public static bool OpenMainMenuNewSurvival()
        {
            return InvokeMainMenuAction(_methodMainMenuNewSurvival, "OpenMainMenuNewSurvival");
        }

        public static bool OpenMainMenuCustomMap()
        {
            return InvokeMainMenuAction(_methodMainMenuCustomMap, "OpenMainMenuCustomMap");
        }

        private static bool InvokeMainMenuAction(MethodInfo method, string label)
        {
            EnsureLobbyUiMeta();
            try
            {
                var menu = MainMenuInstance;
                if (menu == null || method == null)
                {
                    ModLogger.Warn($"[GameBridge] {label}: menu or method unavailable.");
                    return false;
                }
                if (_fieldMainMenuReadyForInput != null)
                    _fieldMainMenuReadyForInput.SetValue(menu, true);
                method.Invoke(menu, new object[] { null });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[GameBridge] {label} failed", ex);
                return false;
            }
        }

        public static bool ReturnToMainMenu()
        {
            EnsureLobbyUiMeta();
            try
            {
                var game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null || _methodReturnToMainMenu == null) return false;
                _methodReturnToMainMenu.Invoke(game, new object[] { null, true });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] ReturnToMainMenu failed", ex);
                return false;
            }
        }

        /// <summary>Returns ZXLevelState.EndStatus as integer (0=Playing, 1=Won, 2=Lost).</summary>
        public static int GetLevelEndStatus()
        {
            try
            {
                object ls = CurrentLevelState;
                if (ls == null || _propLevelStateEndStatus == null) return 0;
                object val = _propLevelStateEndStatus.GetValue(ls);
                return val != null ? Convert.ToInt32(val) : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>True when either ZXLevelState.EndStatus != Playing or ZXSystemGameLevel.GameEndedFlag is set.</summary>
        public static bool IsLevelEnded()
        {
            if (CurrentLevelState == null) return false;
            if (GetLevelEndStatus() != 0) return true;
            try
            {
                object lvlSys = LevelSystemInstance;
                if (lvlSys != null && _fieldLevelSysGameEnded != null)
                {
                    object f = _fieldLevelSysGameEnded.GetValue(lvlSys);
                    if (f is bool b && b) return true;
                }
            }
            catch { }
            return false;
        }

        public static bool IsCurrentLevelSystemEnabled()
        {
            try
            {
                var sys = CurrentGameSystem as DXSystem;
                return sys != null && sys.Enabled;
            }
            catch
            {
                return false;
            }
        }

        public static void ClearScreenTransitionEffect()
        {
            try
            {
                _fieldLevelSysScreenAnim?.SetValue(null, null);
            }
            catch { }
        }

        public static bool TriggerGameEnd(bool won)
        {
            try
            {
                object lvlSys = LevelSystemInstance;
                if (lvlSys == null || _methodLevelSysTriggerGameEnd == null) return false;
                _methodLevelSysTriggerGameEnd.Invoke(lvlSys, new object[] { won });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] TriggerGameEnd failed", ex);
                return false;
            }
        }

        public static bool ShowSyncedGameEnd(bool won)
        {
            try
            {
                object ls = CurrentLevelState;
                object lvlSys = LevelSystemInstance;
                if (ls == null || lvlSys == null) return false;
                if (IsLevelEnded()) return false;

                if (_propLevelStateEndStatus != null)
                {
                    object endVal = Enum.ToObject(_propLevelStateEndStatus.PropertyType, won ? 1 : 2);
                    _propLevelStateEndStatus.SetValue(ls, endVal);
                }
                _fieldLevelSysGameEnded?.SetValue(lvlSys, true);

                bool isCampaign = false;
                try
                {
                    var campaignCur = AccessTools.Method(typeof(ZXCampaign), ObfuscatedSymbols.Method_GetCurrentSingleton, Type.EmptyTypes)?.Invoke(null, null);
                    isCampaign = campaignCur != null;
                }
                catch { }

                MethodInfo showMethod = isCampaign ? _methodLevelSysShowCampaignEnd : _methodLevelSysShowSurvivalEnd;
                if (showMethod == null) return false;
                showMethod.Invoke(lvlSys, new object[] { won, true });
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] ShowSyncedGameEnd failed", ex);
                return false;
            }
        }

        public static bool TriggerMayorPopup()
        {
            try
            {
                object game = _mZXGameCurrent?.Invoke(null, null);
                if (game == null || CurrentLevelState == null || _methodGameShowMayorSelection == null) return false;
                SetChoosingMayor(true);
                _methodGameShowMayorSelection.Invoke(game, null);
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Error("[GameBridge] TriggerMayorPopup failed", ex);
                return false;
            }
        }

        public static DXGUIObject CreateZXWindow(string title, bool bigWindow = true)
        {
            EnsureLobbyUiMeta();
            if (_typeZXWindow == null || _typeZXWindowTypeEnum == null) return null;
            object wtype = Enum.ToObject(_typeZXWindowTypeEnum, bigWindow ? 0 : 1);
            return Activator.CreateInstance(
                _typeZXWindow,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new object[] { title ?? string.Empty, wtype },
                null) as DXGUIObject;
        }

        public static DXObject GetZXWindowContent(DXGUIObject window)
        {
            EnsureLobbyUiMeta();
            return window != null ? _windowContentProp?.GetValue(window) as DXObject : null;
        }

        public static void SetZXWindowTitle(DXGUIObject window, string title)
        {
            EnsureLobbyUiMeta();
            if (window != null) _windowTitleProp?.SetValue(window, title ?? string.Empty);
        }

        public static DXButton AddZXWindowBottomButton(DXGUIObject window, int position, string text, DXKeys key = DXKeys.None, int hueRotation = 0)
        {
            EnsureLobbyUiMeta();
            if (window == null || _windowAddBottomBtnMethod == null || _typeZXWindowBtnPosEnum == null) return null;
            object posEnum = Enum.ToObject(_typeZXWindowBtnPosEnum, position);
            return _windowAddBottomBtnMethod.Invoke(window, new object[] { posEnum, text ?? string.Empty, null, key, hueRotation }) as DXButton;
        }

        public static void ShowZXWindow(DXGUIObject window, Action onShown = null, int animType = 2)
        {
            EnsureLobbyUiMeta();
            if (window == null || _windowShowMethod == null || _typeZXWindowAnimEnum == null) return;
            object anim = Enum.ToObject(_typeZXWindowAnimEnum, animType);
            _windowShowMethod.Invoke(window, new object[] { onShown, anim });
        }

        public static DXButton CreateZXButtonSteam(string text, DXKeys key = DXKeys.None, int hueRotation = 0, bool smallFont = false)
        {
            EnsureLobbyUiMeta();
            if (_typeZXButtonSteam == null || _btnSteamTextCtor == null) return null;
            var btn = _btnSteamTextCtor.Invoke(new object[] { text ?? string.Empty, null }) as DXButton;
            if (btn == null) return null;
            if (smallFont && _btnSteamSmallFontProp != null)
                _btnSteamSmallFontProp.SetValue(btn, true);
            if (key != DXKeys.None && _btnSteamKeysProp?.GetValue(btn) is IList<DXKeys> keys)
                keys.Add(key);
            if (hueRotation != 0)
                SetZXButtonSteamHue(btn, hueRotation);
            return btn;
        }

        public static void SetZXButtonSteamText(DXButton btn, string text)
        {
            EnsureLobbyUiMeta();
            if (btn == null || _btnSteamTextProp == null) return;
            string desired = text ?? string.Empty;
            string current = _btnSteamTextProp.GetValue(btn) as string;
            if (string.Equals(current, desired, StringComparison.Ordinal)) return;

            // Dispose and clear cached DXTextObject GPU texture before text mutation
            // so DXVision creates a fresh texture instead of ReLoadInGPU() (which skews on Direct3DEx).
            if (_btnSteamLabelField?.GetValue(btn) is DXObject labelObj && labelObj.Image != null)
            {
                try { labelObj.Image.Dispose(); } catch { }
                labelObj.Image = null;
            }
            _btnSteamTextProp.SetValue(btn, desired);
        }

        public static void SetZXButtonSteamHue(DXButton btn, int hueRotation)
        {
            EnsureLobbyUiMeta();
            if (btn == null || _btnSteamOBarProp == null) return;
            if (_btnSteamOBarProp.GetValue(btn) is DXObject obar)
                obar.HueRotation = hueRotation;
        }

        public static DXTextRenderer GetFontRenderer(string fontMethodName)
        {
            EnsureLobbyUiMeta();
            try
            {
                if (_typeZXFontCatalogFonts != null && !string.IsNullOrEmpty(fontMethodName))
                {
                    var m = AccessTools.Method(_typeZXFontCatalogFonts, fontMethodName, Type.EmptyTypes);
                    if (m?.Invoke(null, null) is DXTextRenderer tr) return tr;
                }
            }
            catch { }
            return GetPlainTextRenderer();
        }

        public static bool ApplyMayor(string mayorId)
        {
            EnsureMayorMeta();
            if (string.IsNullOrEmpty(mayorId) || _mayorApplyMethod == null) return false;
            var dict = _mayorRegistryField?.GetValue(null) as IDictionary;
            if (dict == null || !dict.Contains(mayorId)) return false;
            object mayor = dict[mayorId];
            object ls = CurrentLevelState;
            if (mayor == null || ls == null) return false;

            int mayorLevel = GetMayorLevel(mayor);
            int currentCount = GetLevelCollectionCount("MayorStates");
            if (mayorLevel > 0 && currentCount >= mayorLevel)
            {
                ModLogger.Info($"[GameBridge] Ignored duplicate mayor selection '{mayorId}' (level={mayorLevel}, alreadySelected={currentCount}).");
                SetChoosingMayor(false);
                Patches.MayorSyncPatch.CloseActiveMayorWindow();
                return false;
            }

            try
            {
                _mayorApplyMethod.Invoke(mayor, new[] { ls });
                SetChoosingMayor(false);
                Patches.MayorSyncPatch.CloseActiveMayorWindow();
                return true;
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] ApplyMayor failed: {ex.Message}");
                return false;
            }
        }

        public static int GetLevelCollectionCount(string name)
        {
            var prop = GetLevelProperty(name);
            var ls = CurrentLevelState;
            if (prop == null || ls == null) return 0;
            try
            {
                object v = prop.GetValue(ls);
                if (v is ICollection col) return col.Count;
                if (v is IEnumerable en) { int n = 0; foreach (var _ in en) n++; return n; }
            }
            catch { }
            return 0;
        }

        // ====================================================================
        // Right-click interaction, CCommandable snapshot & unit wake-up helpers
        // ====================================================================

        public struct CommandableSnapshot
        {
            public object Commandable;
            public object Cmd;
            public object Target;
        }

        private static bool _interactionMetaInit;
        private static FieldInfo _fLevelPendingCmd;
        private static FieldInfo _fLevelMouseEntity;
        private static MethodInfo _mIsOppositeNature;
        private static PropertyInfo _pCapsuleBroken;
        private static FieldInfo _fCmdableCurCmd;
        private static FieldInfo _fCmdableCurTarget;
        private static MethodInfo _mBehavSetResetState;
        private static MethodInfo _mBehavSetResetMem;
        private static MethodInfo _mBehavTick;
        private static MethodInfo _mMovableTick;

        private static void EnsureInteractionMeta()
        {
            if (_interactionMetaInit) return;
            _interactionMetaInit = true;
            try
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _fLevelPendingCmd = TypeZXSystemGameLevel?.GetField(ObfuscatedSymbols.Field_LevelSys_PendingCommand, F);
                _fLevelMouseEntity = TypeZXSystemGameLevel?.GetField(ObfuscatedSymbols.Field_LevelSys_MouseEntity, F);

                Type zxEntity = TypeZXEntity;
                if (zxEntity != null)
                    _mIsOppositeNature = AccessTools.Method(zxEntity, "#=zyqIcAEeeAjQu", new[] { zxEntity });

                Type tCapsule = GameAssembly?.GetType("ZX.Entities.InfectedCapsule");
                if (tCapsule != null)
                    _pCapsuleBroken = tCapsule.GetProperty("Broken", BindingFlags.Public | BindingFlags.Instance);

                Type tCmdable = TypeCCommandable;
                if (tCmdable != null)
                {
                    _fCmdableCurCmd = tCmdable.GetField("#=z8dbvOmM=", F);
                    _fCmdableCurTarget = tCmdable.GetField("#=zyCiF7Lo=", F);
                }

                Type tCBehav = _mGetEntityBehaviour?.ReturnType ?? GameAssembly?.GetType("ZX.Components.CBehaviour");
                if (tCBehav != null)
                {
                    _mBehavSetResetState = AccessTools.Method(tCBehav, "#=zhPQK_m4yjl9wB_DGFw==", new[] { typeof(bool) });
                    _mBehavSetResetMem = AccessTools.Method(tCBehav, "#=zXeRMQqKCGSFidmsp4w==", new[] { typeof(bool) });
                    _mBehavTick = AccessTools.Method(tCBehav, "#=z5GPRaBw=", Type.EmptyTypes);
                }

                Type tCMov = TypeCMovable;
                if (tCMov != null)
                    _mMovableTick = AccessTools.Method(tCMov, "#=z5GPRaBw=", Type.EmptyTypes);
            }
            catch (Exception ex)
            {
                ModLogger.Warn($"[GameBridge] EnsureInteractionMeta failed: {ex.Message}");
            }
        }

        public static object GetPendingCommandOnLevelSys()
        {
            EnsureInteractionMeta();
            object levelSys = LevelSystemInstance;
            if (levelSys == null || _fLevelPendingCmd == null) return null;
            try { return _fLevelPendingCmd.GetValue(levelSys); } catch { return null; }
        }

        public static DXEntity GetMouseEntityOnLevelSys()
        {
            EnsureInteractionMeta();
            object levelSys = LevelSystemInstance;
            if (levelSys == null || _fLevelMouseEntity == null) return null;
            try { return _fLevelMouseEntity.GetValue(levelSys) as DXEntity; } catch { return null; }
        }

        public static object GetAttackCommandObject() => GetCommandByName("Attack");

        public static bool IsEnemyOrAttackableCapsule(DXEntity targetEntity, DXEntity referencePlayerUnit)
        {
            if (targetEntity == null || targetEntity.Disposed || referencePlayerUnit == null)
                return false;
            EnsureInteractionMeta();
            try
            {
                if (_mIsOppositeNature != null && (bool)_mIsOppositeNature.Invoke(targetEntity, new object[] { referencePlayerUnit }))
                    return true;
                if (string.Equals(targetEntity.GetType().Name, "InfectedCapsule", StringComparison.Ordinal))
                    return _pCapsuleBroken == null || !(bool)_pCapsuleBroken.GetValue(targetEntity);
            }
            catch { }
            return false;
        }

        public static List<CommandableSnapshot> SnapshotSelectedCommandables()
        {
            EnsureInteractionMeta();
            if (_fCmdableCurCmd == null || _fCmdableCurTarget == null) return null;
            var listObj = _fieldSelectedList?.GetValue(null) as IEnumerable;
            if (listObj == null) return null;

            List<CommandableSnapshot> snaps = null;
            foreach (object item in listObj)
            {
                var ent = (item as DXComponent)?.Entity;
                if (ent == null || ent.Disposed || !IsPlayerEntity(ent)) continue;
                object cmdable = GetCommandableObject(ent);
                if (cmdable == null) continue;
                if (snaps == null) snaps = new List<CommandableSnapshot>();
                snaps.Add(new CommandableSnapshot
                {
                    Commandable = cmdable,
                    Cmd = _fCmdableCurCmd.GetValue(cmdable),
                    Target = _fCmdableCurTarget.GetValue(cmdable)
                });
            }
            return snaps;
        }

        public static void RestoreSelectedCommandables(List<CommandableSnapshot> snaps)
        {
            if (snaps == null || _fCmdableCurCmd == null || _fCmdableCurTarget == null) return;
            for (int i = 0; i < snaps.Count; i++)
            {
                var s = snaps[i];
                if (s.Commandable == null) continue;
                try
                {
                    _fCmdableCurCmd.SetValue(s.Commandable, s.Cmd);
                    _fCmdableCurTarget.SetValue(s.Commandable, s.Target);
                }
                catch { }
            }
        }

        public static bool IsWeaponSubCommand(object cmdObj)
        {
            if (cmdObj == null) return false;
            string name = cmdObj.GetType().Name;
            if (string.Equals(name, "Attack", StringComparison.Ordinal))
                return false;
            return name.EndsWith("Attack", StringComparison.Ordinal)
                || string.Equals(name, "ArtilleryManExplosive", StringComparison.Ordinal);
        }

        public struct PendingResourceCost
        {
            public int Gold;
            public int Wood;
            public int Stone;
            public int Iron;
            public int Oil;
            public int Energy;
            public int Food;
            public int Workers;

            public static PendingResourceCost operator +(PendingResourceCost a, PendingResourceCost b)
            {
                return new PendingResourceCost
                {
                    Gold = a.Gold + b.Gold,
                    Wood = a.Wood + b.Wood,
                    Stone = a.Stone + b.Stone,
                    Iron = a.Iron + b.Iron,
                    Oil = a.Oil + b.Oil,
                    Energy = a.Energy + b.Energy,
                    Food = a.Food + b.Food,
                    Workers = a.Workers + b.Workers
                };
            }
        }

        private static bool _costPropsInit;
        private static PropertyInfo _propCostGold, _propCostWood, _propCostStone, _propCostIron, _propCostOil, _propCostEnergy, _propCostFood, _propCostWorkers;

        public static bool TryGetCommandResourceCost(object command, object executor, out PendingResourceCost cost)
        {
            cost = default;
            if (command == null || executor == null || _methodGetResourcesCost == null)
                return false;

            try
            {
                object boxedCost = _methodGetResourcesCost.Invoke(command, new[] { executor });
                if (boxedCost == null)
                    return false;

                if (!_costPropsInit)
                {
                    Type tCost = boxedCost.GetType();
                    _propCostGold = tCost.GetProperty("Gold", BindingFlags.Public | BindingFlags.Instance);
                    _propCostWood = tCost.GetProperty("Wood", BindingFlags.Public | BindingFlags.Instance);
                    _propCostStone = tCost.GetProperty("Stone", BindingFlags.Public | BindingFlags.Instance);
                    _propCostIron = tCost.GetProperty("Iron", BindingFlags.Public | BindingFlags.Instance);
                    _propCostOil = tCost.GetProperty("Oil", BindingFlags.Public | BindingFlags.Instance);
                    _propCostEnergy = tCost.GetProperty("Energy", BindingFlags.Public | BindingFlags.Instance);
                    _propCostFood = tCost.GetProperty("Food", BindingFlags.Public | BindingFlags.Instance);
                    _propCostWorkers = tCost.GetProperty("Workers", BindingFlags.Public | BindingFlags.Instance);
                    _costPropsInit = true;
                }

                int ReadPosInt(PropertyInfo p) => p != null ? Math.Max(0, Convert.ToInt32(p.GetValue(boxedCost, null))) : 0;
                cost = new PendingResourceCost
                {
                    Gold = ReadPosInt(_propCostGold),
                    Wood = ReadPosInt(_propCostWood),
                    Stone = ReadPosInt(_propCostStone),
                    Iron = ReadPosInt(_propCostIron),
                    Oil = ReadPosInt(_propCostOil),
                    Energy = ReadPosInt(_propCostEnergy),
                    Food = ReadPosInt(_propCostFood),
                    Workers = ReadPosInt(_propCostWorkers)
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool CanAffordWithPending(PendingResourceCost candidate, PendingResourceCost pendingTotal)
        {
            object ls = CurrentLevelState;
            if (ls == null) return true;

            PendingResourceCost total = candidate + pendingTotal;
            try
            {
                if (total.Gold > 0 && GetLevelInt("Gold") < total.Gold) return false;
                if (total.Wood > 0 && GetLevelInt("Wood") < total.Wood) return false;
                if (total.Stone > 0 && GetLevelInt("Stone") < total.Stone) return false;
                if (total.Iron > 0 && GetLevelInt("Iron") < total.Iron) return false;
                if (total.Oil > 0 && GetLevelInt("Oil") < total.Oil) return false;

                if (total.Energy > 0 && _methodLevelStateAvailEnergy != null
                    && Convert.ToInt32(_methodLevelStateAvailEnergy.Invoke(ls, null)) < total.Energy)
                    return false;
                if (total.Food > 0 && _methodLevelStateAvailFood != null
                    && Convert.ToInt32(_methodLevelStateAvailFood.Invoke(ls, null)) < total.Food)
                    return false;
                if (total.Workers > 0 && _methodLevelStateAvailWorkers != null
                    && Convert.ToInt32(_methodLevelStateAvailWorkers.Invoke(ls, null)) < total.Workers)
                    return false;

                return true;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsAttackCommand(object cmdObj)
        {
            if (cmdObj == null) return false;
            string name = cmdObj.GetType().Name;
            return name.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(name, "UseItem_Grenade", StringComparison.Ordinal);
        }

        public static void PlayUnitCommandVoice(DXEntity entity, bool isAttack)
        {
            if (entity == null || entity.Disposed || DXScene.Current == null || _methodEntityGetDefaultParams == null)
                return;
            if (TypeZXEntity != null && !TypeZXEntity.IsInstanceOfType(entity))
                return;

            Determinism.LockstepClock.EnterAudioContext();
            try
            {
                object audio = _fieldAudioCurrent?.GetValue(null);
                if (audio == null) return;

                object defParams = _methodEntityGetDefaultParams.Invoke(entity, null);
                if (defParams == null) return;

                if (_propDefParamsSoundsGeneric == null || _propDefParamsSoundsAttack == null)
                {
                    Type dpType = defParams.GetType();
                    _propDefParamsSoundsGeneric = dpType.GetProperty("SoundsOnCommandGeneric", BindingFlags.Public | BindingFlags.Instance);
                    _propDefParamsSoundsAttack = dpType.GetProperty("SoundsOnCommandAttack", BindingFlags.Public | BindingFlags.Instance);
                }

                var list = (isAttack
                    ? _propDefParamsSoundsAttack?.GetValue(defParams, null) as List<string>
                    : _propDefParamsSoundsGeneric?.GetValue(defParams, null) as List<string>);

                if ((list == null || list.Count == 0) && isAttack)
                    list = _propDefParamsSoundsGeneric?.GetValue(defParams, null) as List<string>;

                if (list == null || list.Count == 0) return;

                if (!isAttack && _typeUnit != null && _typeUnit.IsInstanceOfType(entity) && _methodAudioPickWeightedVoice != null)
                {
                    _methodAudioPickWeightedVoice.Invoke(audio, new object[] { list });
                }
                else if (_methodAudioPlayUniqueList != null)
                {
                    _methodAudioPlayUniqueList.Invoke(audio, new object[] { ZXSoundCategory.UnitVoices, ZXUniqueSoundType.EntityVoice, list });
                }
            }
            catch { }
            finally
            {
                Determinism.LockstepClock.ExitAudioContext();
            }
        }

        public static void PlayLocalBuildOrOrderFeedback(Type targetEntityType, bool preferCreationSound, DXEntity fallbackExecutor = null)
        {
            if (DXScene.Current == null || _methodAudioPlay2DSoundList == null || _methodEntityGetDefaultParams == null)
                return;

            Determinism.LockstepClock.EnterAudioContext();
            try
            {
                object audio = _fieldAudioCurrent?.GetValue(null);
                if (audio == null) return;

                DXEntity sampleEntity = targetEntityType != null ? GetEntityTemplateByType(targetEntityType)?.Entity : null;
                if (sampleEntity == null) sampleEntity = fallbackExecutor;
                if (sampleEntity == null) return;

                object defParams = _methodEntityGetDefaultParams.Invoke(sampleEntity, null);
                if (defParams == null) return;

                if (_propDefParamsSoundsCreation == null || _propDefParamsSoundsSelected == null)
                {
                    Type dpType = defParams.GetType();
                    _propDefParamsSoundsCreation = dpType.GetProperty("SoundsOnCreation", BindingFlags.Public | BindingFlags.Instance);
                    _propDefParamsSoundsSelected = dpType.GetProperty("SoundsOnSelected", BindingFlags.Public | BindingFlags.Instance);
                }

                var list = (preferCreationSound
                    ? _propDefParamsSoundsCreation?.GetValue(defParams, null) as List<string>
                    : _propDefParamsSoundsSelected?.GetValue(defParams, null) as List<string>);
                if ((list == null || list.Count == 0))
                    list = _propDefParamsSoundsSelected?.GetValue(defParams, null) as List<string>;

                if (list != null && list.Count > 0)
                {
                    _methodAudioPlay2DSoundList.Invoke(audio, new object[] { ZXSoundCategory.Effects, list });
                }
            }
            catch { }
            finally
            {
                Determinism.LockstepClock.ExitAudioContext();
            }
        }

        public static void NotifyUnitCommandExecuted(object executorObj, object cmdObj)
        {
            if (IsWeaponSubCommand(cmdObj)) return;
            var ent = executorObj as DXEntity;
            if (ent == null || ent.Disposed) return;
            EnsureInteractionMeta();

            object cbehav = null;
            if (_mGetEntityBehaviour != null && _mGetEntityBehaviour.DeclaringType.IsInstanceOfType(ent))
            {
                try { cbehav = _mGetEntityBehaviour.Invoke(ent, null); } catch { cbehav = null; }
            }

            if (cbehav != null)
            {
                try
                {
                    _mBehavSetResetMem?.Invoke(cbehav, new object[] { true });
                    string cmdName = cmdObj?.GetType().Name;
                    if (string.Equals(cmdName, "Interact", StringComparison.Ordinal)
                        || string.Equals(cmdName, "Travel", StringComparison.Ordinal))
                    {
                        _mBehavSetResetState?.Invoke(cbehav, new object[] { true });
                    }
                }
                catch { }
            }

            // Wake odd-parity units on the exact command tick so an entire squad reacts together
            // without waiting up to 33-100ms staggered across IsIAFrameModule(4) parity boundaries.
            if (Determinism.LockstepClock.IsDeterministicModeActive
                && !Determinism.LockstepClock.PausedBySession
                && !ent.IsIAFrameModule(4))
            {
                int origHash = ent.HashCode;
                ent.HashCode = origHash ^ 1;
                try
                {
                    if (cbehav != null) _mBehavTick?.Invoke(cbehav, null);
                    if (_mGetEntityMovable != null && _mGetEntityMovable.DeclaringType.IsInstanceOfType(ent))
                    {
                        object cmov = _mGetEntityMovable.Invoke(ent, null);
                        if (cmov != null) _mMovableTick?.Invoke(cmov, null);
                    }
                }
                catch { }
                finally
                {
                    ent.HashCode = origHash;
                }
            }
        }

        // ====================================================================
        // Random Survival Map Generator & Balance Scaling Helpers
        // ====================================================================

        public static MethodInfo GetMapGeneratorMethod() => _methodMapGeneratorGenerate;

        public static int GetRandomParamsNCells(object randomParams)
        {
            if (randomParams == null || _propRandomParamsNCells == null) return 256;
            return (int)_propRandomParamsNCells.GetValue(randomParams);
        }

        public static void SetRandomParamsNCells(object randomParams, int nCells)
        {
            if (randomParams != null && _propRandomParamsNCells != null)
                _propRandomParamsNCells.SetValue(randomParams, nCells);
        }

        public static float GetRandomParamsFactorGameDuration(object randomParams)
        {
            if (randomParams == null || _propRandomParamsFactorDuration == null) return 1f;
            return (float)_propRandomParamsFactorDuration.GetValue(randomParams);
        }

        public static IDictionary GetMapThemesDictionary()
        {
            if (_methodZXMapThemeGetThemes == null) return null;
            return _methodZXMapThemeGetThemes.Invoke(null, null) as IDictionary;
        }

        public struct MapThemeBalanceSnapshot
        {
            public object ThemeObj;
            public DXRange<int> DoomVillagesSize;
            public DXRange<int> NumTreasures;
            public DXRange<int> NumSuppyFood;
            public DXRange<int> NumSuppyEnergy;
            public int NumInfectedGiants;
            public int NumInfectedMutants;
            public double InfectedFactor;
        }

        public static MapThemeBalanceSnapshot CaptureMapThemeSnapshot(object themeObj)
        {
            return new MapThemeBalanceSnapshot
            {
                ThemeObj = themeObj,
                DoomVillagesSize = (DXRange<int>)_propMapThemeDoomVillagesSize.GetValue(themeObj),
                NumTreasures = (DXRange<int>)_propMapThemeNumTreasures.GetValue(themeObj),
                NumSuppyFood = (DXRange<int>)_propMapThemeNumSuppyFood.GetValue(themeObj),
                NumSuppyEnergy = (DXRange<int>)_propMapThemeNumSuppyEnergy.GetValue(themeObj),
                NumInfectedGiants = (int)_propMapThemeNumInfectedGiants.GetValue(themeObj),
                NumInfectedMutants = (int)_propMapThemeNumInfectedMutants.GetValue(themeObj),
                InfectedFactor = (double)_propMapThemeInfectedFactor.GetValue(themeObj)
            };
        }

        public static void ApplyMapThemeValues(
            object themeObj,
            DXRange<int> doomVillagesSize,
            DXRange<int> numTreasures,
            DXRange<int> numSuppyFood,
            DXRange<int> numSuppyEnergy,
            int numInfectedGiants,
            int numInfectedMutants,
            double infectedFactor)
        {
            if (themeObj == null) return;
            _propMapThemeDoomVillagesSize.SetValue(themeObj, doomVillagesSize);
            _propMapThemeNumTreasures.SetValue(themeObj, numTreasures);
            _propMapThemeNumSuppyFood.SetValue(themeObj, numSuppyFood);
            _propMapThemeNumSuppyEnergy.SetValue(themeObj, numSuppyEnergy);
            _propMapThemeNumInfectedGiants.SetValue(themeObj, numInfectedGiants);
            _propMapThemeNumInfectedMutants.SetValue(themeObj, numInfectedMutants);
            _propMapThemeInfectedFactor.SetValue(themeObj, infectedFactor);
        }

        public static void RestoreMapThemeSnapshot(MapThemeBalanceSnapshot snap)
        {
            ApplyMapThemeValues(
                snap.ThemeObj,
                snap.DoomVillagesSize,
                snap.NumTreasures,
                snap.NumSuppyFood,
                snap.NumSuppyEnergy,
                snap.NumInfectedGiants,
                snap.NumInfectedMutants,
                snap.InfectedFactor);
        }

        public static bool TryGetGeneratedLevelContext(
            DXLevel level,
            out object mapDrawer,
            out List<DXEntity> extraEntities,
            out DXGridLayerEnum<ZXMapLayerTerrainType> layerTerrain,
            out DXGridLayerEnum<ZXMapLayerObjectType> layerObjects,
            out int gridNCells,
            out IList levelEvents)
        {
            mapDrawer = null;
            extraEntities = null;
            layerTerrain = null;
            layerObjects = null;
            gridNCells = 0;
            levelEvents = null;

            object ext = level?.Extension;
            if (ext == null || (_typeZXLevelExtension != null && !_typeZXLevelExtension.IsInstanceOfType(ext)))
                return false;

            mapDrawer = _propLevelExtMapDrawer?.GetValue(ext);
            levelEvents = _propLevelExtLevelEvents?.GetValue(ext) as IList;
            if (mapDrawer == null) return false;

            extraEntities = _propMapDrawerExtraEntities?.GetValue(mapDrawer) as List<DXEntity>;
            layerTerrain = _propMapDrawerLayerTerrain?.GetValue(mapDrawer) as DXGridLayerEnum<ZXMapLayerTerrainType>;
            layerObjects = _propMapDrawerLayerObjects?.GetValue(mapDrawer) as DXGridLayerEnum<ZXMapLayerObjectType>;
            gridNCells = _propMapDrawerNCells != null ? (int)_propMapDrawerNCells.GetValue(mapDrawer) : level.NCells;
            return extraEntities != null;
        }

        public static void ScaleLevelEventPopulation(object levelEvent, float factor, int maxPower)
        {
            if (levelEvent == null || _methodLevelEventScalePop == null) return;
            _methodLevelEventScalePop.Invoke(levelEvent, new object[] { factor, maxPower });
        }

        public static void GetLevelEventTiming(object levelEvent, out int startTimeH, out int gameTimeH, out bool isFinalSwarm)
        {
            startTimeH = _propLevelEventStartTimeH != null ? (int)_propLevelEventStartTimeH.GetValue(levelEvent) : 0;
            gameTimeH = _propLevelEventGameTimeH != null ? (int)_propLevelEventGameTimeH.GetValue(levelEvent) : 0;
            isFinalSwarm = _propLevelEventFinalSwarm != null && (bool)_propLevelEventFinalSwarm.GetValue(levelEvent);
        }

        public static DXEntityTemplate GetEntityTemplateByType(Type entityType)
        {
            if (entityType == null || _methodGetTemplateFromEntityType == null) return null;
            DXGame zxGame = ZXGameInstance;
            if (zxGame == null) return null;
            return _methodGetTemplateFromEntityType.Invoke(zxGame, new object[] { entityType }) as DXEntityTemplate;
        }

        public static bool AddLevelStateCommandCenterExtras(int addWorkers, int addFood)
        {
            object ls = CurrentLevelState;
            if (ls == null || _propLevelStateExtraWorkers == null || _propLevelStateExtraFood == null) return false;

            if (addWorkers != 0)
            {
                int curW = (int)_propLevelStateExtraWorkers.GetValue(ls);
                _propLevelStateExtraWorkers.SetValue(ls, curW + addWorkers);
            }
            if (addFood != 0)
            {
                int curF = (int)_propLevelStateExtraFood.GetValue(ls);
                _propLevelStateExtraFood.SetValue(ls, curF + addFood);
            }
            return true;
        }

        public static void GetLevelStateCommandCenterExtras(out int extraWorkers, out int extraFood)
        {
            extraWorkers = 0;
            extraFood = 0;
            object ls = CurrentLevelState;
            if (ls == null) return;
            if (_propLevelStateExtraWorkers != null) extraWorkers = (int)_propLevelStateExtraWorkers.GetValue(ls);
            if (_propLevelStateExtraFood != null) extraFood = (int)_propLevelStateExtraFood.GetValue(ls);
        }

        public static bool CaptureMapPanorama(string outputPath, int resolution = 2048, bool revealFog = true)
        {
            var lvlSys = LevelSystemInstance;
            if (lvlSys == null || _methodLevelSysCapturePanorama == null || DXLevel.Current == null)
                return false;

            FieldInfo progressWinField = null;
            object prevProgressWin = null;
            object tempProgressWin = null;
            try
            {
                string dir = System.IO.Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

                ClearLoadingFadeOverlay();
                ClearWaitCursor();

                // Cancel the 6-second New Game intro blur/cinematic so Paused frames are crisp.
                AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_IntroCinematic)?.SetValue(null, false);
                AccessTools.Field(TypeZXSystemGameLevel, ObfuscatedSymbols.Field_LevelSys_ScreenAnim)?.SetValue(null, null);
                var rootObj = DXScene.Current?.RootObject;
                if (rootObj != null)
                {
                    rootObj.Blur = 0f;
                    if (rootObj.State != null) rootObj.State.Blur = 0f;
                }

                if (revealFog)
                {
                    RevealAll(true);
                    object ml = GetMapLayersInstance();
                    if (ml != null)
                    {
                        SetInstanceBool(ml, "ShadowEnabled", false);
                        AccessTools.Method(ml.GetType(), "OnFinishUpdate")?.Invoke(ml, null);
                    }
                    if (DXScene.Current != null && DXLevel.Current != null)
                    {
                        DXScene.Current.GlobalLightColor = DXLevel.Current.GlobalLightColor;
                    }
                }

                var helperType = GameAssembly?.GetType(ObfuscatedSymbols.Type_ProgressWindowHelper);
                progressWinField = helperType != null
                    ? AccessTools.Field(helperType, ObfuscatedSymbols.Field_ProgressWindow_Current)
                    : null;
                if (progressWinField != null)
                {
                    prevProgressWin = progressWinField.GetValue(null);
                    if (prevProgressWin == null)
                    {
                        var winType = GameAssembly?.GetType("ZX.GUI.ZXWindow");
                        var ctors = winType?.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (ctors != null && ctors.Length > 0)
                        {
                            var ctor = ctors[0];
                            var ps = ctor.GetParameters();
                            var cArgs = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                            {
                                var pt = ps[i].ParameterType;
                                if (pt == typeof(string)) cArgs[i] = "";
                                else if (pt.IsEnum) cArgs[i] = Enum.ToObject(pt, 0);
                                else if (ps[i].HasDefaultValue) cArgs[i] = ps[i].DefaultValue;
                                else if (pt.IsValueType) cArgs[i] = Activator.CreateInstance(pt);
                            }
                            tempProgressWin = ctor.Invoke(cArgs);
                            if (tempProgressWin is DXObject dxObj) dxObj.Visible = false;
                            progressWinField.SetValue(null, tempProgressWin);
                        }
                    }
                }

                _methodLevelSysCapturePanorama.Invoke(lvlSys, new object[] { outputPath, resolution });
                return System.IO.File.Exists(outputPath) || System.IO.File.Exists(System.IO.Path.ChangeExtension(outputPath, ".png"));
            }
            catch (Exception ex)
            {
                ModLogger.Error($"[GameBridge] CaptureMapPanorama({outputPath}) failed", ex);
                return false;
            }
            finally
            {
                if (progressWinField != null && tempProgressWin != null)
                {
                    try { progressWinField.SetValue(null, prevProgressWin); } catch { }
                    try { (tempProgressWin as IDisposable)?.Dispose(); } catch { }
                }
            }
        }

        public static volatile bool SurvivalGenInProgress;
        public static volatile bool SurvivalGenLastOk;
        public static volatile string SurvivalGenLastError;

        public static bool StartSurvivalAsync(
            int seed,
            string themeName,
            float factorZombiePop = 1f,
            float factorDuration = 1f,
            Sync.CoopBalancePreset? overridePreset = null,
            int? overridePlayerCount = null)
        {
            if (SurvivalGenInProgress)
            {
                ModLogger.Warn("[GameBridge] StartSurvivalAsync: generation already in progress.");
                return false;
            }

            try
            {
                if (TypeZXGame == null || TypeZXSystemGameLevel == null || _typeZXGameState == null ||
                    _typeZXRandomLevelParams == null || _methodMapGeneratorGenerate == null || _mInitLevel == null)
                {
                    SurvivalGenLastError = "missing required bindings";
                    ModLogger.Warn("[GameBridge] StartSurvivalAsync: missing required bindings.");
                    return false;
                }

                SurvivalGenInProgress = true;
                SurvivalGenLastOk = false;
                SurvivalGenLastError = null;

                if (DXModalScreen.IsInModal && DXModalScreen.CurrentModalObject != null)
                {
                    DXModalScreen.RemoveModal(DXModalScreen.CurrentModalObject);
                }
                PrepareForProgrammaticLoad();

                var game = ZXGameInstance;
                var asyncTransitionMethod = AccessTools.Method(
                    TypeZXGame,
                    ObfuscatedSymbols.Method_ZXGame_AsyncTransition,
                    new[] { typeof(int), typeof(Action), typeof(Action), typeof(List<string>), typeof(bool) });
                if (asyncTransitionMethod == null)
                {
                    SurvivalGenLastError = "missing ZXGame.AsyncTransition method";
                    SurvivalGenInProgress = false;
                    return false;
                }

                if (!Enum.TryParse<ZXMapThemeType>(themeName ?? "BR", true, out var themeType))
                    themeType = ZXMapThemeType.BR;

                var resetMethod = AccessTools.Method(TypeZXGame, ObfuscatedSymbols.Method_ZXGame_ResetWorldState, Type.EmptyTypes);
                var lsType = LevelStateType ?? GameAssembly.GetType("ZX.ZXLevelState");

                MethodInfo loadGeneric = null;
                foreach (var m in typeof(DXSystem).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "Load" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)
                    {
                        loadGeneric = m.MakeGenericMethod(TypeZXSystemGameLevel);
                        break;
                    }
                }

                object newSys = null;
                DXLevel generated = null;
                string workerErr = null;

                Action workerAction = () =>
                {
                    try
                    {
                        ModLogger.Info($"[GameBridge] StartSurvivalAsync worker begin: theme={themeType}, seed={seed}, preset={overridePreset}, players={overridePlayerCount}");
                        resetMethod?.Invoke(game, null);

                        object gameState = Activator.CreateInstance(_typeZXGameState, $"Panorama_{themeType}_{seed}");
                        AccessTools.Property(_typeZXGameState, "GameMode")?.SetValue(gameState, ZXGameModeType.Survival);
                        AccessTools.Property(_typeZXGameState, "ChallengeType")?.SetValue(gameState, ZXGameChallengeType.Default);

                        object randParams = Activator.CreateInstance(_typeZXRandomLevelParams);
                        AccessTools.Property(_typeZXRandomLevelParams, "Seed")?.SetValue(randParams, seed);
                        AccessTools.Property(_typeZXRandomLevelParams, "NCells")?.SetValue(randParams, 256);
                        AccessTools.Property(_typeZXRandomLevelParams, "ThemeType")?.SetValue(randParams, themeType);
                        AccessTools.Property(_typeZXRandomLevelParams, "FactorZombiePopulation")?.SetValue(randParams, factorZombiePop);
                        AccessTools.Property(_typeZXRandomLevelParams, "FactorGameDuration")?.SetValue(randParams, factorDuration);
                        AccessTools.Property(_typeZXRandomLevelParams, "Name")?.SetValue(randParams, $"Panorama_{themeType}_{seed}");
                        AccessTools.Property(_typeZXGameState, "SurvivalModeParams")?.SetValue(gameState, randParams);

                        AccessTools.Method(_typeZXGameState, ObfuscatedSymbols.Method_ZXGameState_SetCurrent, new[] { _typeZXGameState })
                            ?.Invoke(null, new[] { gameState });

                        object levelState = Activator.CreateInstance(lsType);
                        AccessTools.Method(lsType, ObfuscatedSymbols.Method_ZXLevelState_SetCurrent, new[] { lsType })
                            ?.Invoke(null, new[] { levelState });
                        AccessTools.Method(lsType, ObfuscatedSymbols.Method_ZXLevelState_PreInit, Type.EmptyTypes)
                            ?.Invoke(levelState, null);

                        newSys = loadGeneric?.Invoke(null, new object[] { false });
                        AccessTools.Property(TypeZXGame, "CurrentGameSystem")?.SetValue(game, newSys);

                        Sync.CoopBalanceManager.TestOverridePreset = overridePreset;
                        Sync.CoopBalanceManager.TestOverridePlayerCount = overridePlayerCount;

                        ModLogger.Info("[GameBridge] StartSurvivalAsync worker calling MapGenerator.Generate...");
                        generated = _methodMapGeneratorGenerate.Invoke(null, new[] { randParams }) as DXLevel;
                        if (generated == null)
                        {
                            workerErr = "MapGenerator returned null";
                        }
                        else
                        {
                            ModLogger.Info($"[GameBridge] StartSurvivalAsync worker generated NCells={generated.NCells}, calling InitLevel...");
                            _mInitLevel.Invoke(newSys, new object[] { generated });
                            ModLogger.Info("[GameBridge] StartSurvivalAsync worker InitLevel completed.");
                        }
                    }
                    catch (Exception ex)
                    {
                        workerErr = ex.InnerException?.Message ?? ex.Message;
                        ModLogger.Error("[GameBridge] StartSurvivalAsync worker failed", ex);
                    }
                    finally
                    {
                        Sync.CoopBalanceManager.TestOverridePreset = null;
                        Sync.CoopBalanceManager.TestOverridePlayerCount = null;
                    }
                };

                Action finishAction = () =>
                {
                    try
                    {
                        ModLogger.Info($"[GameBridge] StartSurvivalAsync finishAction: err={workerErr ?? "none"}, hasLevel={generated != null}");
                        if (workerErr == null && generated != null && newSys != null)
                        {
                            _methodLevelSysEnterGameView?.Invoke(newSys, Type.EmptyTypes);
                            ClearLoadingFadeOverlay();
                            ClearWaitCursor();
                            if (game != null) game.Paused = true;
                            SurvivalGenLastOk = DXLevel.Current != null && CurrentLevelState != null;
                            if (!SurvivalGenLastOk) SurvivalGenLastError = "DXLevel or LevelState null after EnterGameView";
                        }
                        else
                        {
                            SurvivalGenLastOk = false;
                            SurvivalGenLastError = workerErr ?? "unknown worker failure";
                        }
                    }
                    catch (Exception ex2)
                    {
                        SurvivalGenLastOk = false;
                        SurvivalGenLastError = ex2.Message;
                        ModLogger.Error("[GameBridge] StartSurvivalAsync completion failed", ex2);
                    }
                    finally
                    {
                        SurvivalGenInProgress = false;
                    }
                };

                if (_propZXGameIsLoading == null)
                    _propZXGameIsLoading = AccessTools.Property(TypeZXGame, "IsLoading");

                asyncTransitionMethod.Invoke(game, new object[]
                {
                    20,
                    workerAction,
                    finishAction,
                    new List<string> { "Generating panorama comparison map..." },
                    true
                });

                return true;
            }
            catch (Exception ex)
            {
                SurvivalGenLastOk = false;
                SurvivalGenLastError = ex.Message;
                SurvivalGenInProgress = false;
                ModLogger.Error("[GameBridge] StartSurvivalAsync failed", ex);
                return false;
            }
        }

        public static Dictionary<string, object> CollectCurrentMapMetrics()
        {
            var res = new Dictionary<string, object>();
            object survivalParams = GetGameStateProperty("SurvivalModeParams");
            int survivalNCells = survivalParams != null ? GetRandomParamsNCells(survivalParams) : (DXLevel.Current?.NCells ?? 0);
            res["survivalNCells"] = survivalNCells;
            res["gridNCells"] = DXLevel.Current?.NCells ?? 0;
            res["playableAreaWidth"] = DXLevel.Current?.PlayableArea.Width ?? 0f;
            res["theme"] = survivalParams != null ? AccessTools.Property(survivalParams.GetType(), "ThemeType")?.GetValue(survivalParams)?.ToString() : "";
            res["seed"] = survivalParams != null ? AccessTools.Property(survivalParams.GetType(), "Seed")?.GetValue(survivalParams) : 0;

            GetLevelStateCommandCenterExtras(out int ccW, out int ccF);
            res["ccExtraWorkers"] = ccW;
            res["ccExtraFood"] = ccF;

            int rangers = 0, soldiers = 0, zombies = 0, giants = 0, mutants = 0;
            int doomVillages = 0, oilSources = 0, pickables = 0, trees = 0, minerals = 0;
            var nestType = GameAssembly?.GetType("ZX.Components.CInfectionNest");

            if (DXGame.Current?.Entities != null)
            {
                foreach (var e in DXGame.Current.Entities)
                {
                    if (e == null || e.Disposed) continue;
                    string tn = e.GetType().Name;
                    if (tn == "Ranger") rangers++;
                    else if (tn == "SoldierRegular") soldiers++;
                    else if (tn == "OilSource") oilSources++;
                    else if (tn.Contains("Giant") && _typeZombie != null && _typeZombie.IsInstanceOfType(e)) { giants++; zombies++; }
                    else if (tn.Contains("Mutant") && _typeZombie != null && _typeZombie.IsInstanceOfType(e)) { mutants++; zombies++; }
                    else if (_typeZombie != null && _typeZombie.IsInstanceOfType(e)) zombies++;
                    else if (tn.Contains("Pickable") || tn.Contains("Item") || tn.Contains("Gold") && !tn.Contains("Mineral")) pickables++;
                    else if (tn.Contains("Forest") || tn.Contains("Tree")) trees++;
                    else if (tn.Contains("Mineral")) minerals++;

                    if (nestType != null && e.Components != null && e.Components.Exists(c => c != null && nestType.IsInstanceOfType(c)))
                        doomVillages++;
                }
            }

            // Also count static map-layer mineral/forest cells from MapDrawer
            int terrainTrees = 0, terrainGold = 0, terrainStone = 0, terrainIron = 0;
            if (DXLevel.Current != null && TryGetGeneratedLevelContext(DXLevel.Current, out _, out _, out _, out var layerObj, out int gN, out _))
            {
                if (layerObj != null && gN > 0)
                {
                    for (int x = 0; x < gN; x++)
                    {
                        for (int y = 0; y < gN; y++)
                        {
                            var o = layerObj[x, y];
                            if (o == ZXMapLayerObjectType.Tree) terrainTrees++;
                            else if (o == ZXMapLayerObjectType.MineralGold) terrainGold++;
                            else if (o == ZXMapLayerObjectType.MineralStone) terrainStone++;
                            else if (o == ZXMapLayerObjectType.MineralIron) terrainIron++;
                        }
                    }
                }
            }

            int levelEventsCount = GetLevelCollectionCount("LevelEvents");
            res["rangers"] = rangers;
            res["soldiers"] = soldiers;
            res["zombiesTotal"] = zombies;
            res["giants"] = giants;
            res["mutants"] = mutants;
            res["doomVillages"] = doomVillages;
            res["oilSources"] = oilSources;
            res["pickables"] = pickables;
            res["terrainTreeCells"] = terrainTrees;
            res["terrainGoldCells"] = terrainGold;
            res["terrainStoneCells"] = terrainStone;
            res["terrainIronCells"] = terrainIron;
            res["levelEventsCount"] = levelEventsCount;
            return res;
        }
    }
}
