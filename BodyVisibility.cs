using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

internal static class BodyVisibility
{
    internal static bool ShowHead;
    internal static bool ShowHeadwear = true;
    internal static bool ShowFaceCover = true;
    internal static bool ShowEyewear = true;

    private readonly struct RendererState
    {
        internal readonly Renderer Renderer;
        internal readonly bool Enabled;
        internal readonly ShadowCastingMode Shadows;

        internal RendererState(Renderer renderer)
        {
            Renderer = renderer;
            Enabled = renderer.enabled;
            Shadows = renderer.shadowCastingMode;
        }
    }

    private static readonly Dictionary<int, RendererState> OriginalRenderers = new();
    // The head skin as the game had it before it was shown: active, and each renderer's layer and off-screen update.
    private static LoddedSkin? _headSkin;
    private static bool _headActive;
    private static int _headLayer;
    private static readonly List<(Renderer Renderer, int Layer, bool Offscreen)> HeadRenderers = new();
    private static readonly Dictionary<int, Renderer[]> EquipmentRenderers = new();
    private static readonly Dictionary<int, bool> HeadDevices = new();   // by renderer: on a helmet's night-vision or thermal device
    private static Player? _trackedPlayer;
    private static int _nextCachePruneFrame;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".body");
        MethodInfo? updateRenders = AccessTools.Method(typeof(PlayerBody), nameof(PlayerBody.UpdatePlayerRenders));
        MethodInfo? updatePostfix = typeof(BodyVisibility).GetMethod(nameof(UpdatePlayerRendersPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (updateRenders != null && updatePostfix != null)
            harmony.Patch(updateRenders, postfix: new HarmonyMethod(updatePostfix) { priority = Priority.Last });

        MethodInfo? parentModel = AccessTools.Method(typeof(PlayerBody.SlotView), "CreateAndParent", new[] { typeof(PlayerBody), typeof(GameObject) });
        MethodInfo? parentPostfix = typeof(BodyVisibility).GetMethod(nameof(ParentEquipmentModelPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (parentModel != null && parentPostfix != null)
            harmony.Patch(parentModel, postfix: new HarmonyMethod(parentPostfix) { priority = Priority.Last });

        _installed = true;
    }

    internal static void Apply(Player player)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        PlayerBody? body = player.PlayerBody;
        if (body == null || !player.IsYourPlayer)
            return;

        if (!ReferenceEquals(_trackedPlayer, player))
        {
            Restore();
            _trackedPlayer = player;
        }

        Prune();
        SetHead(body, ShowHead);
        foreach (EquipmentSlot slot in PlayerBody.SlotNames)
        {
            if (body.SlotViews.TryGetByKey(slot, out PlayerBody.SlotView? view) && view != null)
                ApplySlot(player, view);
        }
    }

    internal static void Restore()
    {
        RestoreHead();
        foreach (RendererState state in OriginalRenderers.Values)
        {
            if (state.Renderer == null)
                continue;

            state.Renderer.enabled = state.Enabled;
            state.Renderer.shadowCastingMode = state.Shadows;
        }

        OriginalRenderers.Clear();
        EquipmentRenderers.Clear();
        HeadDevices.Clear();
        _trackedPlayer = null;
        _nextCachePruneFrame = 0;
    }

    private static void UpdatePlayerRendersPostfix(PlayerBody __instance)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.PlayerBody, __instance))
            return;

        Apply(player);
    }

    private static void ParentEquipmentModelPostfix(PlayerBody.SlotView __instance)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || __instance._playerBody == null || !ReferenceEquals(player.PlayerBody, __instance._playerBody))
            return;

        ApplySlot(player, __instance);
    }

    private static void SetHead(PlayerBody body, bool visible)
    {
        if (!body.BodySkins.TryGetValue(EBodyModelPart.Head, out LoddedSkin? skin) || skin == null)
            return;

        IEnumerable<Renderer> renderers = skin.GetRenderers();
        Snapshot(renderers);
        if (!ReferenceEquals(_headSkin, skin))
        {
            RestoreHead();
            _headSkin = skin;
            _headActive = skin.gameObject.activeSelf;
            _headLayer = skin.gameObject.layer;
            foreach (Renderer renderer in renderers)
            {
                if (renderer != null)
                    HeadRenderers.Add((renderer, renderer.gameObject.layer, renderer is SkinnedMeshRenderer s && s.updateWhenOffscreen));
            }
        }
        if (!skin.gameObject.activeSelf)
            skin.gameObject.SetActive(true);

        skin.EnableRenderers(true);
        skin.SetShadowCastingMode(visible ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly);
        if (visible)
        {
            foreach (Renderer renderer in renderers)
            {
                if (renderer is SkinnedMeshRenderer skinned)
                    skinned.updateWhenOffscreen = true;
            }
        }

        int playerLayer = LayerMask.NameToLayer("Player");
        Camera? camera = Camera.main;
        bool cameraSeesPlayer = playerLayer < 0 || camera == null || (camera.cullingMask & (1 << playerLayer)) != 0;
        if (visible && !cameraSeesPlayer)
            skin.SetLayer(0);
        else if (!visible && playerLayer >= 0)
            skin.SetLayer(playerLayer);
    }

    private static void ApplySlot(Player player, PlayerBody.SlotView slotView)
    {
        if (slotView.EquipmentSlot != EquipmentSlot.Headwear
            && slotView.EquipmentSlot != EquipmentSlot.FaceCover
            && slotView.EquipmentSlot != EquipmentSlot.Eyewear)
            return;

        Snapshot(slotView.Renderers);
        Renderer[] renderers = SlotRenderers(slotView);
        Snapshot(renderers);
        if (!ShownSlot(slotView.EquipmentSlot))
        {
            // A helmet hidden leaves the night vision or thermal goggles on it, with the mount they hang from: the
            // player looks through them.
            Transform? keep = slotView.EquipmentSlot == EquipmentSlot.Headwear && slotView.Model != null ? slotView.Model.transform : null;
            Hide(slotView.Renderers, keep);
            Hide(renderers, keep);
            return;
        }

        Show(slotView.Renderers);
        Show(renderers);
    }

    // Under the helmet's night-vision slot (its mount and the device on it), or part of a night-vision or thermal device.
    private static bool OnHeadDevice(Transform t, Transform? model)
    {
        for (Transform? at = t; at != null && at != model; at = at.parent)
        {
            if (at.name.StartsWith("mod_nvg", StringComparison.Ordinal)
                || at.GetComponent<NightVisionDevice>() != null || at.GetComponent<ThermalVisionDevice>() != null)
                return true;
        }
        return false;
    }

    private static bool ShownSlot(EquipmentSlot slot)
    {
        switch (slot)
        {
            case EquipmentSlot.Headwear:
                return ShowHeadwear;
            case EquipmentSlot.FaceCover:
                return ShowFaceCover;
            case EquipmentSlot.Eyewear:
                return ShowEyewear;
            default:
                return true;
        }
    }

    private static Renderer[] SlotRenderers(PlayerBody.SlotView slotView)
    {
        if (slotView.Model == null)
            return Array.Empty<Renderer>();

        int modelId = slotView.Model.GetInstanceID();
        if (!EquipmentRenderers.TryGetValue(modelId, out Renderer[] renderers) || Destroyed(renderers))
        {
            renderers = slotView.Model.GetComponentsInChildren<Renderer>(true);
            EquipmentRenderers[modelId] = renderers;
        }

        return renderers;
    }

    private static readonly FieldInfo? CameraManagerInstance = AccessTools.Field(typeof(CameraManager), "instance");
    private static Type? _nightRendererType;
    private static MethodInfo? _usesCustomPipeline;
    private static bool _nightResolved;
    private static readonly object?[] PipelineArgument = new object?[1];

    internal static CameraManager? Cameras => CameraManagerInstance?.GetValue(null) as CameraManager;

    internal static Type? NightRendererType
    {
        get
        {
            EnsureNightTypes();
            return _nightRendererType;
        }
    }

    internal static bool UsesNightPipeline(BSG.CameraEffects.NightVision nightVision)
    {
        EnsureNightTypes();
        if (_usesCustomPipeline == null)
            return false;
        PipelineArgument[0] = nightVision;
        return _usesCustomPipeline.Invoke(null, PipelineArgument) is true;
    }

    private static void EnsureNightTypes()
    {
        if (_nightResolved)
            return;

        _nightResolved = true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "BorkelRNVG", StringComparison.Ordinal))
                continue;

            _nightRendererType = assembly.GetType("BorkelRNVG.Controllers.RealisticNightVisionRenderer");
            Type? helper = assembly.GetType("BorkelRNVG.Helpers.NvgHelper");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _usesCustomPipeline = helper?.GetMethod("UsesCustomPipeline", BindingFlags.Public | BindingFlags.Static);
            return;
        }
    }

    private static void Prune()
    {
        if (Time.frameCount < _nextCachePruneFrame)
            return;

        _nextCachePruneFrame = Time.frameCount + 600;
        List<int>? dead = null;
        foreach (KeyValuePair<int, RendererState> pair in OriginalRenderers)
        {
            if (pair.Value.Renderer != null)
                continue;

            dead ??= new List<int>();
            dead.Add(pair.Key);
        }

        if (dead != null)
        {
            foreach (int key in dead)
            {
                OriginalRenderers.Remove(key);
                HeadDevices.Remove(key);
            }
        }

        dead = null;
        foreach (KeyValuePair<int, Renderer[]> pair in EquipmentRenderers)
        {
            if (!Destroyed(pair.Value))
                continue;

            dead ??= new List<int>();
            dead.Add(pair.Key);
        }

        if (dead == null)
            return;

        foreach (int key in dead)
            EquipmentRenderers.Remove(key);
    }

    private static bool Destroyed(Renderer[] renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                return true;
        }

        return false;
    }

    private static void RestoreHead()
    {
        foreach ((Renderer renderer, int layer, bool offscreen) in HeadRenderers)
        {
            if (renderer == null)
                continue;
            renderer.gameObject.layer = layer;
            if (renderer is SkinnedMeshRenderer skinned)
                skinned.updateWhenOffscreen = offscreen;
        }
        HeadRenderers.Clear();
        if (_headSkin != null)
        {
            _headSkin.gameObject.layer = _headLayer;
            if (_headSkin.gameObject.activeSelf != _headActive)
                _headSkin.gameObject.SetActive(_headActive);
        }
        _headSkin = null;
    }

    private static void Snapshot(IEnumerable<Renderer> renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null && !OriginalRenderers.ContainsKey(renderer.GetInstanceID()))
                OriginalRenderers.Add(renderer.GetInstanceID(), new RendererState(renderer));
        }
    }

    private static void Show(IEnumerable<Renderer> renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null)
                Show(renderer);
        }
    }

    private static void Show(Renderer renderer)
    {
        renderer.enabled = true;
        if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
            renderer.shadowCastingMode = ShadowCastingMode.On;
    }

    // keep: the helmet whose head-mounted devices stay shown.
    private static void Hide(IEnumerable<Renderer> renderers, Transform? keep)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;
            bool device = false;
            if (keep != null && !HeadDevices.TryGetValue(renderer.GetInstanceID(), out device))
                HeadDevices[renderer.GetInstanceID()] = device = OnHeadDevice(renderer.transform, keep);
            if (device)
                Show(renderer);
            else
                Hide(renderer);
        }
    }

    private static void Hide(Renderer renderer)
    {
        renderer.enabled = true;
        renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
    }
}
