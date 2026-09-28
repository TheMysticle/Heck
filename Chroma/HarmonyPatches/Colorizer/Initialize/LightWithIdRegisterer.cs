using System.Collections.Generic;
using Chroma.Colorizer;
using Chroma.Lighting;
using SiraUtil.Affinity;
using UnityEngine;

namespace Chroma.HarmonyPatches.Colorizer.Initialize;

internal class LightWithIdRegisterer : IAffinity
{
    private readonly LightColorizerManager _colorizerManager;
    private readonly LightWithIdManager _lightWithIdManager;
    private readonly HashSet<ILightWithId> _needToRegister = [];
    private readonly Dictionary<ILightWithId, int> _requestedIds = new();
    private readonly LightIDTableManager _tableManager;

#if !PRE_V1_45_1
    // As of 1.45.1, LightWithIdManager no longer has a flat "_lights"/"_colors" array to bind to by name
    // (Harmony's "____fieldname" convention) -- its internal storage moved to a groupId/elementId-based
    // mapping. Since the patches below already fully replace the game's own RegisterLight/UnregisterLight/
    // SetColorForId (__runOriginal = false), they never needed to mirror the game's internal layout in the
    // first place -- Chroma just owns this state itself now, keyed the same way (by lightId, 0-550).
    private readonly List<ILightWithId>?[] _lights = new List<ILightWithId>?[LightWithIdManager.kMaxLightId + 1];
    private readonly Color?[] _colors = new Color?[LightWithIdManager.kMaxLightId + 1];
#endif

    private LightWithIdRegisterer(
        LightColorizerManager colorizerManager,
        LightWithIdManager lightWithIdManager,
        LightIDTableManager tableManager)
    {
        _colorizerManager = colorizerManager;
        _lightWithIdManager = lightWithIdManager;
        _tableManager = tableManager;
    }

#if !PRE_V1_45_1
    // Used by LightColorizer's constructor instead of reaching into the (now nonexistent) private
    // "_lights" field directly.
    internal List<ILightWithId> GetOrCreateLights(int lightId)
    {
        List<ILightWithId>? lights = _lights[lightId];
        if (lights == null)
        {
            lights = new List<ILightWithId>(10);
            _lights[lightId] = lights;
        }

        return lights;
    }
#endif

    internal void ForceUnregister(ILightWithId lightWithId)
    {
        int lightId = lightWithId.lightId;
#if !PRE_V1_45_1
        List<ILightWithId> lights = _lights[lightId]!;
#else
        List<ILightWithId> lights = _lightWithIdManager._lights[lightId];
#endif
        int index = lights.FindIndex(n => n == lightWithId);
        lights[index] = null!; // TODO: handle null
        _tableManager.UnregisterIndex(lightId, index);
        _colorizerManager.CreateLightColorizerContractByLightID(
            lightId,
            n => n.ChromaLightSwitchEventEffect.UnregisterLight(lightWithId));
        lightWithId.__SetIsUnRegistered();
    }

    internal void MarkForTableRegister(ILightWithId lightWithId)
    {
        _needToRegister.Add(lightWithId);
    }

    internal void SetRequestedId(ILightWithId lightWithId, int id)
    {
        _requestedIds[lightWithId] = id;
    }

    // too lazy to make a transpiler
    [AffinityPrefix]
    [AffinityPatch(typeof(LightWithIdManager), nameof(LightWithIdManager.SetColorForId))]
#if !PRE_V1_45_1
    private bool AllowNull(
        int lightId,
        Color color,
        ref bool ____didChangeSomeColorsThisFrame)
    {
        _colors[lightId] = color;
        ____didChangeSomeColorsThisFrame = true;
        _lights[lightId]
            ?.ForEach(
                n =>
                {
                    if (n is { isRegistered: true })
                    {
                        n.ColorWasSet(color);
                    }
                });
        return false;
    }
#else
    private bool AllowNull(
        int lightId,
        Color color,
        List<ILightWithId?>?[] ____lights,
        Color?[] ____colors,
        ref bool ____didChangeSomeColorsThisFrame)
    {
        ____colors[lightId] = color;
        ____didChangeSomeColorsThisFrame = true;
        ____lights[lightId]
            ?.ForEach(
                n =>
                {
                    if (n is { isRegistered: true })
                    {
                        n.ColorWasSet(color);
                    }
                });
        return false;
    }
#endif

    [AffinityPrefix]
    [AffinityPatch(typeof(LightWithIdManager), nameof(LightWithIdManager.UnregisterLight))]
    private bool DontClearList(ILightWithId lightWithId)
    {
        lightWithId.__SetIsUnRegistered();
        return false;
    }

    [AffinityPrefix]
    [AffinityPatch(typeof(LightWithIdManager), nameof(LightWithIdManager.RegisterLight))]
    private void Prefix(
        ref bool __runOriginal,
        LightWithIdManager __instance,
        ILightWithId lightWithId
#if PRE_V1_45_1
        ,
        List<ILightWithId>?[] ____lights,
        List<ILightWithId> ____lightsToUnregister,
        Color?[] ____colors
#endif
    )
    {
        // TODO: figure this shit out
        // for some reason, despite being an affinity patch bound to player, this still runs in the menu scene
        // so quick and dirty fix
        if (__instance.gameObject.scene.name.Contains("Menu"))
        {
            return;
        }

        __runOriginal = false;

        if (lightWithId.isRegistered)
        {
            return;
        }

        int lightId = lightWithId.lightId;
        if (lightId == -1)
        {
            return;
        }

#if !PRE_V1_45_1
        List<ILightWithId> lights = GetOrCreateLights(lightId);
#else
        List<ILightWithId>? lights = ____lights[lightId];
        if (lights == null)
        {
            ____lights[lightId] = lights = new List<ILightWithId>(10);
        }
#endif

        lightWithId.__SetIsRegistered();

        if (lights.Contains(lightWithId))
        {
            return;
        }

        // TODO: find a better way to register "new" lights to table
        int index = lights.Count;
        if (_needToRegister.Remove(lightWithId))
        {
            int? tableId = _requestedIds.TryGetValue(lightWithId, out int value) ? value : null;
            _tableManager.RegisterIndex(lightId, index, tableId);
        }

        // this also colors the light
        _colorizerManager.CreateLightColorizerContractByLightID(
            lightId,
            n => n.ChromaLightSwitchEventEffect.RegisterLight(lightWithId, index));

        lights.Add(lightWithId);
#if !PRE_V1_45_1
        Color? color = _colors[lightId];
#else
        ____lightsToUnregister.Remove(lightWithId);
        Color? color = ____colors[lightId];
#endif
        lightWithId.ColorWasSet(color ?? Color.clear);
    }
}
