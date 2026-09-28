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
    // BeatmapDataLoader.CreateOrGetTransformedBeatmapDataAsync, which -- decompiled and confirmed on real
    // hardware -- has its OWN single-slot cache (_lastUsedBeatmapDataCache) keyed roughly by beatmapKey.
    // On a cache hit it returns the previously-computed Task directly and never calls
    // LoadAndTransformAsync/BeatmapDataTransformHelper.CreateTransformedBeatmapData(Async) again.
    //
    // This used to key a "pending instance" dictionary by beatmapKey and have
    // CreateTransformedBeatmapData's prefix consume (TryRemove) the matching entry. That broke on real
    // hardware the moment a song got loaded twice for the same beatmapKey (e.g. once for a menu-level
    // preview, once for actually pressing Play) -- the second load hits the loader's cache, so
    // CreateTransformedBeatmapData never runs for it, so nothing ever populates that instance's
    // _untransformedBeatmapData, crashing with "[_untransformedBeatmapData] was null" in
    // PatchedPlayerInstaller.BindHeckSinglePlayer right after a suspiciously fast (cache-hit) transform.
    //
    // Fix: capture the untransformed data into a cache of our own, keyed by beatmapKey and never
    // removed (mirroring the game's own cache's lifetime/granularity). Then, once
    // LoadTransformedBeatmapDataAsync has fully completed -- real transform or cache hit, doesn't matter
    // -- look the data up by the *completed* instance's own beatmapKey. A cache hit on the game's side
    // means our earlier capture (from whichever load actually ran the transform for that beatmapKey) is
    // still valid and correct to reuse.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<BeatmapKey, IReadonlyBeatmapData> _capturedUntransformedData = new();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BeatmapDataTransformHelper), nameof(BeatmapDataTransformHelper.CreateTransformedBeatmapData))]
    private static void CaptureUntransformedBeatmapData(IReadonlyBeatmapData beatmapData, BeatmapKey beatmapKey)
    {
        _capturedUntransformedData[beatmapKey] = beatmapData;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameplayCoreSceneSetupData), nameof(GameplayCoreSceneSetupData.LoadTransformedBeatmapDataAsync))]
    private static void ApplyCapturedUntransformedData(GameplayCoreSceneSetupData __instance, System.Threading.Tasks.Task __result)
    {
        if (__instance is not HeckGameplayCoreSceneSetupData hecked)
        {
            return;
        }

        // __result is the Task returned by the (still in-flight) async method, not its completion --
        // hook the actual completion so this runs whether the load was a real transform or a cache hit.
        __result.ContinueWith(
            _ =>
            {
                if (_capturedUntransformedData.TryGetValue(hecked.beatmapKey, out IReadonlyBeatmapData? data))
                {
                    hecked._untransformedBeatmapData = data;
                }
            },
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion |
            System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
    }
#endif
}
#endif
