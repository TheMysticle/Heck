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
    // LoadTransformedBeatmapDataAsync records which Hecked instance is about to load, keyed by its
    // beatmapKey, and a prefix on CreateTransformedBeatmapData (which CreateTransformedBeatmapDataAsync
    // wraps via Task.Run, so potentially on a different thread) captures its raw beatmapData for the
    // matching entry.
    //
    // This used to key by a single static "pending" instance instead of by beatmapKey, on the
    // assumption that only one load is ever in flight at a time -- confirmed wrong on real hardware:
    // loading a second song crashed with "[_untransformedBeatmapData] was null", because some other
    // load (a menu-level preview, a non-Hecked vanilla GameplayCoreSceneSetupData, or just the next
    // song's own load starting before the previous one's transform callback fired) overwrote or cleared
    // the single pending slot before the real capture happened. Keying by beatmapKey instead means each
    // load's capture is independent of whatever else happens to be loading at the same time.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<BeatmapKey, HeckGameplayCoreSceneSetupData> _pendingCaptures = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameplayCoreSceneSetupData), nameof(GameplayCoreSceneSetupData.LoadTransformedBeatmapDataAsync))]
    private static void MarkPendingCapture(GameplayCoreSceneSetupData __instance)
    {
        if (__instance is HeckGameplayCoreSceneSetupData hecked)
        {
            _pendingCaptures[hecked.beatmapKey] = hecked;
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BeatmapDataTransformHelper), nameof(BeatmapDataTransformHelper.CreateTransformedBeatmapData))]
    private static void CaptureUntransformedBeatmapData(IReadonlyBeatmapData beatmapData, BeatmapKey beatmapKey)
    {
        if (_pendingCaptures.TryRemove(beatmapKey, out HeckGameplayCoreSceneSetupData? hecked))
        {
            hecked._untransformedBeatmapData = beatmapData;
        }
    }
#endif
}
#endif
