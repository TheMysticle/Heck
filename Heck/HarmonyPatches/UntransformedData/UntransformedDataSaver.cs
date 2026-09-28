#if !PRE_V1_37_1
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IPA.Utilities;

namespace Heck.HarmonyPatches.UntransformedData;

[HeckPatch]
public class HeckGameplayCoreSceneSetupData : GameplayCoreSceneSetupData
{
    private static readonly MethodInfo _heckType = AccessTools.Method(
        typeof(HeckGameplayCoreSceneSetupData),
        nameof(HeckGetType));

    private static readonly FieldAccessor<GameplayCoreSceneSetupData, BeatmapLevelsModel>.Accessor
        _beatmapLevelsModelAccessor =
            FieldAccessor<GameplayCoreSceneSetupData, BeatmapLevelsModel>.GetAccessor(nameof(_beatmapLevelsModel));

    private IReadonlyBeatmapData? _untransformedBeatmapData;

    public HeckGameplayCoreSceneSetupData(
        GameplayCoreSceneSetupData original)
        : base(
            original.beatmapKey,
            original.beatmapLevel,
            original.gameplayModifiers,
            original.playerSpecificSettings,
            original.practiceSettings,
#if !LATEST
            original.useTestNoteCutSoundEffects,
#endif
#if !PRE_V1_40_8
            original.targetEnvironmentInfo,
            original.originalEnvironmentInfo,
#else
            original.environmentInfo,
#endif
            original.colorScheme,
#if !PRE_V1_40_8
            original._settingsManager,
#elif V1_37_1
            original._performancePreset,
#endif
            original._audioClipAsyncLoader,
            original._beatmapDataLoader,
            original._beatmapLevelsEntitlementModel,
            original._enableBeatmapDataCaching,
#if LATEST
            original.environmentsListModel,
            original._allowNullBeatmapLevelData,
            original._beatmapLevelsModel,
            original.beatmapLevelData)
#else
            original._allowNullBeatmapLevelData,
    #if !PRE_V1_40_8
            original.environmentsListModel,
    #endif
            original.recordingToolData)
#endif
    {
        GameplayCoreSceneSetupData @this = this;
        _beatmapLevelsModelAccessor(ref @this) = original._beatmapLevelsModel;
        beatmapLevelData = original.beatmapLevelData;
    }

    public IReadonlyBeatmapData UntransformedBeatmapData =>
        _untransformedBeatmapData ??
        throw new InvalidOperationException($"[{nameof(_untransformedBeatmapData)}] was null.");

    private static Type HeckGetType(Type original)
    {
        return original == typeof(HeckGameplayCoreSceneSetupData) ? typeof(GameplayCoreSceneSetupData) : original;
    }

    // i hate gettype i hate gettype i hate gettype
    [HarmonyTranspiler]
    [HarmonyPatch(typeof(ScenesTransitionSetupData), nameof(ScenesTransitionSetupData.InstallBindings))]
    private static IEnumerable<CodeInstruction> HeckOff(IEnumerable<CodeInstruction> instructions)
    {
        return new CodeMatcher(instructions)
            /*
             * -- Type type = sceneSetupData.GetType();
             * ++ Type type = HeckGetType(sceneSetupData.GetType());
             */
            .MatchForward(false, new CodeMatch(OpCodes.Stloc_3))
            .InsertAndAdvance(new CodeInstruction(OpCodes.Call, _heckType))
            .InstructionEnumeration();
    }

#if !LATEST
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameplayCoreSceneSetupData), nameof(TransformBeatmapData))]
    private static void OverrideGetTransformedBeatmapDataAsync(
        GameplayCoreSceneSetupData __instance,
        IReadonlyBeatmapData beatmapData)
    {
        if (__instance is HeckGameplayCoreSceneSetupData hecked)
        {
            hecked._untransformedBeatmapData = beatmapData;
        }
    }
#else
    // TransformBeatmapData no longer exists as of 1.45.1 -- GameplayCoreSceneSetupData now loads and
    // transforms in one shot via LoadTransformedBeatmapDataAsync, which internally calls into
    // BeatmapDataTransformHelper.CreateTransformedBeatmapData(Async) to do the actual transform. Neither
    // of those receives the setup-data instance, so we bridge them: a prefix on
    // LoadTransformedBeatmapDataAsync records which Hecked instance is about to load, and a prefix on
    // CreateTransformedBeatmapData (which CreateTransformedBeatmapDataAsync wraps via Task.Run) captures
    // its raw beatmapData for that instance. Both patches only ever fire for the single active gameplay
    // scene setup, matching the single-instance-at-a-time assumption this class already made.
    private static HeckGameplayCoreSceneSetupData? _pendingCapture;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameplayCoreSceneSetupData), nameof(GameplayCoreSceneSetupData.LoadTransformedBeatmapDataAsync))]
    private static void MarkPendingCapture(GameplayCoreSceneSetupData __instance)
    {
        _pendingCapture = __instance as HeckGameplayCoreSceneSetupData;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BeatmapDataTransformHelper), nameof(BeatmapDataTransformHelper.CreateTransformedBeatmapData))]
    private static void CaptureUntransformedBeatmapData(IReadonlyBeatmapData beatmapData)
    {
        if (_pendingCapture != null)
        {
            _pendingCapture._untransformedBeatmapData = beatmapData;
            _pendingCapture = null;
        }
    }
#endif
}
#endif
