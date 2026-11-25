using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// The player's mounts as they are set, on the player's models shown elsewhere: the inventory's overview, the loading
// screen's. A mount is set as it is worn (NightVisionMount: its carriage, arm and slide, split from the model), by
// the settings kept for its item; a model is given one when the game puts a slot's model on it (a model of the gear
// with a mount whose item has settings: not another's, nor one never set) and lets it go before the model goes back to
// the game's pool. The worn mount's own model, the player's in a raid or the hideout, is the worn mount's alone.
// Set every frame as the worn one is (the device flipping moves what it hangs from); nothing searched per frame. The
// helmet on such a model is tilted as kept for it too (HeadwearTilt).
internal static partial class NightVisionEyepieces
{
    private static readonly List<MountRig> _previewMounts = new List<MountRig>();
    private static readonly Dictionary<GameObject, MountRig> _previewMountOf = new Dictionary<GameObject, MountRig>();
    private static bool _previewInstalled;
    // A model with a mount whose item had no settings yet when it was made: given one once some are saved for it.
    private static readonly Dictionary<GameObject, (Transform device, PlayerBody body)> _previewWaiting = new Dictionary<GameObject, (Transform, PlayerBody)>();
    private static int _previewSettingsRead;

    private static void InstallPreviewMounts(Harmony harmony)
    {
        if (_previewInstalled)
            return;
        _previewInstalled = true;
        MethodInfo? create = AccessTools.Method(typeof(PlayerBody.SlotView), nameof(PlayerBody.SlotView.CreateAndParent));
        MethodInfo? destroy = AccessTools.Method(typeof(PlayerBody.SlotView), nameof(PlayerBody.SlotView.DestroyCurrentModel));
        if (create == null || destroy == null)
        {
            NewCameraPlugin.Log?.LogWarning("Mounts on the player's other models: the slot views' model methods not found; those models show the mount as the game has it");
            return;
        }
        harmony.Patch(create, postfix: new HarmonyMethod(AccessTools.Method(typeof(NightVisionEyepieces), nameof(SlotModelPostfix))));
        harmony.Patch(destroy, prefix: new HarmonyMethod(AccessTools.Method(typeof(NightVisionEyepieces), nameof(SlotModelGoingPrefix))));
    }

    private static void SlotModelPostfix(PlayerBody.SlotView __instance, PlayerBody playerBody, GameObject model)
    {
        try
        {
            if (!NewCameraSwitch.Enabled || playerBody == null || model == null || _previewMountOf.ContainsKey(model))
                return;
            // Only a model of the player shown apart: not anyone's in a raid (the worn one's is its own).
            if (playerBody.GetComponentInParent<Player>() != null)
                return;
            if (__instance.EquipmentSlot == EquipmentSlot.Headwear)
                HeadwearTilt.Show(playerBody, model.transform, __instance.ContainedItem.Value);
            Transform? device = DeviceNode(model, out Device? kind);
            if (device == null || kind == null)
                return;
            ShowGlow(model.transform, device, kind);
            if (!MakePreviewMount(model, device, playerBody))
                _previewWaiting[model] = (device, playerBody);
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("Mount on a model of the player not set: " + e.Message);
        }
    }

    // A slot's model going (to the pool, or another in its place): its mount whole again first, the worn one too.
    private static void SlotModelGoingPrefix(PlayerBody.SlotView __instance)
    {
        GameObject? model = __instance._model;
        if (model == null)
            return;
        DropDestroyedRenderers(__instance, model);
        HeadwearTilt.Forget(model.transform);
        ForgetGlow(model.transform);
        _previewWaiting.Remove(model);
        if (_previewMountOf.TryGetValue(model, out MountRig rig))
        {
            rig.Release();
            _previewMountOf.Remove(model);
            _previewMounts.Remove(rig);
        }
        if (_mountOf != null && _mountOf.IsChildOf(model.transform))
            ReleaseMount();
    }

    // The game keeps the renderers a slot's model had when it was hung on the body (CreateAndParent), and switches each
    // of them on again as the model goes, your own player's: one of them destroyed since, that throws, the model is
    // never given back to the pool and the move of the item is left half done (the user's, 2026-11-23, a goggle changed
    // in a raid: "原先的夜视仪换下后建模还在头上", then the item flickering in the rig; Player.log: NullReferenceException
    // in Renderer.set_enabled under SlotView.DestroyCurrentModel). Renderers made under a model and destroyed again are
    // this mod's own (the mount's pieces, the lenses) and other mods'; whoever's, the destroyed ones are taken out of
    // the game's list before it goes through it. (A new array only then: when a model goes and has one.)
    private static void DropDestroyedRenderers(PlayerBody.SlotView view, GameObject model)
    {
        Renderer[] renderers = view._renderers;
        if (renderers == null)
            return;
        int gone = 0;
        foreach (Renderer r in renderers)
        {
            if (r == null)
                gone++;
        }
        if (gone == 0)
            return;
        var kept = new Renderer[renderers.Length - gone];
        int at = 0;
        foreach (Renderer r in renderers)
        {
            if (r != null)
                kept[at++] = r;
        }
        view._renderers = kept;
        NewCameraPlugin.Log?.LogInfo($"Slot model going ({model.name}): {gone} destroyed renderer(s) of {renderers.Length} taken out of the game's list, which would have stopped it there.");
    }

    private static bool MakePreviewMount(GameObject model, Transform device, PlayerBody playerBody)
    {
        MountRig? rig = MountRig.Make(device, playerBody.Equipment, playerBody.transform, worn: false, onlyIfSet: true);
        if (rig == null)
            return false;
        _previewMounts.Add(rig);
        _previewMountOf[model] = rig;
        rig.Adjust();
        return true;
    }

    // The mounts' settings saved since (the worn one adjusted): the models' mounts set by them from now on, and those
    // made before their item had any given a mount now. Only when something was saved: nothing searched per frame.
    private static void ReadPreviewSettingsAgain()
    {
        if (_previewSettingsRead == MountSettingsVersion)
            return;
        _previewSettingsRead = MountSettingsVersion;
        if (!NewCameraSwitch.Enabled)
            return;
        foreach (MountRig rig in _previewMounts)
            rig.Reload();
        int made = 0;
        if (_previewWaiting.Count > 0)
        {
            List<GameObject>? done = null;
            foreach (KeyValuePair<GameObject, (Transform device, PlayerBody body)> e in _previewWaiting)
            {
                if (e.Key == null || e.Value.device == null || e.Value.body == null)
                    (done ??= new List<GameObject>()).Add(e.Key!);
                else if (!_previewMountOf.ContainsKey(e.Key) && MakePreviewMount(e.Key, e.Value.device, e.Value.body))
                {
                    (done ??= new List<GameObject>()).Add(e.Key);
                    made++;
                }
            }
            if (done != null)
                foreach (GameObject g in done)
                    _previewWaiting.Remove(g);
        }
        NewCameraPlugin.Log?.LogInfo($"Mount settings saved: {_previewMounts.Count - made} model(s) of the player set again, {made} given a mount now.");
    }

    // Each frame, the models' mounts set again; all whole again with this mod off.
    internal static void UpdatePreviewMounts()
    {
        UpdateShownGlows();
        ReadPreviewSettingsAgain();
        for (int i = _previewMounts.Count - 1; i >= 0; i--)
        {
            MountRig rig = _previewMounts[i];
            if (rig.Device == null || !NewCameraSwitch.Enabled)
            {
                if (rig.Device != null)
                    rig.Release();
                _previewMounts.RemoveAt(i);
                continue;
            }
            if (rig.Device.gameObject.activeInHierarchy)
                rig.Adjust();
        }
        if (_previewMounts.Count == 0 && _previewMountOf.Count > 0)
            _previewMountOf.Clear();
        else if (_previewMountOf.Count != _previewMounts.Count)
        {
            // A model gone without being let go (destroyed with its scene): forgotten.
            List<GameObject>? gone = null;
            foreach (KeyValuePair<GameObject, MountRig> e in _previewMountOf)
            {
                if (e.Key == null || !_previewMounts.Contains(e.Value))
                    (gone ??= new List<GameObject>()).Add(e.Key!);
            }
            if (gone != null)
                foreach (GameObject g in gone)
                    _previewMountOf.Remove(g);
        }
    }

    // The goggle in a slot's model, as the worn one is found (FindDevice), without taking it for the worn one.
    private static Transform? DeviceNode(GameObject model, out Device? kind)
    {
        kind = null;
        foreach (NightVisionDevice device in model.GetComponentsInChildren<NightVisionDevice>(true))
        {
            foreach (Transform node in device.GetComponentsInChildren<Transform>(true))
            {
                foreach (Device known in Devices)
                {
                    if (node.name != known.Node)
                        continue;
                    kind = known;
                    return node;
                }
            }
        }
        foreach (Transform node in model.GetComponentsInChildren<Transform>(true))
        {
            foreach (Device known in Devices)
            {
                if (!known.Thermal || node.name != known.Node)
                    continue;
                kind = known;
                return node;
            }
        }
        return null;
    }

    // The goggle's own light on such a model (the game's, lit while the goggle is switched on) in the colour of the
    // picture: the one the player last saw through this goggle (BRNVG's tint, or the game's filter's: PhosphorColor,
    // kept in NightVisionEyepieces), else the colour the game draws this item's picture in. Its own colour back when the
    // model goes or the mod is switched off. (No light on the face of our own on this branch: only the game's is tinted.)
    private sealed class ShownGlow
    {
        internal Transform Model = null!;
        internal Light Light = null!;
        internal NightVisionDevice Switch = null!;
        internal Device Kind = null!;
        internal Color Own;
        internal bool Noted;
    }

    private static readonly List<ShownGlow> _shownGlows = new List<ShownGlow>();
    private static readonly Dictionary<string, Color> _phosphorOf = new Dictionary<string, Color>();

    private static void ShowGlow(Transform model, Transform body, Device kind)
    {
        if (kind.Thermal)
            return;
        ForgetGlow(model);
        NightVisionDevice? device = body.GetComponentInParent<NightVisionDevice>(true);
        if (device == null || device._light == null)
            return;
        _shownGlows.Add(new ShownGlow { Model = model, Light = device._light, Switch = device, Kind = kind, Own = device._light.color });
    }

    private static void ForgetGlow(Transform model)
    {
        for (int i = _shownGlows.Count - 1; i >= 0; i--)
        {
            if (_shownGlows[i].Model == model)
                DropGlow(i);
        }
    }

    private static void DropGlow(int i)
    {
        ShownGlow glow = _shownGlows[i];
        if (glow.Light != null)
            glow.Light.color = glow.Own;
        _shownGlows.RemoveAt(i);
    }

    private static void UpdateShownGlows()
    {
        for (int i = _shownGlows.Count - 1; i >= 0; i--)
        {
            ShownGlow glow = _shownGlows[i];
            if (glow.Model == null || glow.Light == null || glow.Switch == null || !NewCameraSwitch.Enabled)
            {
                DropGlow(i);
                continue;
            }
            if (!glow.Light.enabled)
                continue;
            if (!_phosphorOf.TryGetValue(glow.Kind.Node, out Color color))
            {
                if (DeviceComponent?.GetValue(glow.Switch) is not NightVisionComponent component)
                    continue;
                Color own = component.Template.Color;
                float brightest = Mathf.Max(own.r, Mathf.Max(own.g, own.b));
                color = brightest > 1e-4f ? own / brightest : glow.Own;
            }
            if (glow.Light.color != color)
                glow.Light.color = color;
            if (!glow.Noted)
            {
                glow.Noted = true;
                NewCameraPlugin.Log?.LogInfo($"Goggle light on a model of the player ({glow.Kind.Node}): {glow.Own} -> {color} ({(_phosphorOf.ContainsKey(glow.Kind.Node) ? "the picture last seen" : "the item's picture colour")})");
            }
        }
    }
}
