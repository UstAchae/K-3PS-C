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
            MountRig? rig = MountRig.Make(device, playerBody.Equipment, playerBody.transform, worn: false, onlyIfSet: true);
            if (rig == null)
                return;
            _previewMounts.Add(rig);
            _previewMountOf[model] = rig;
            rig.Adjust();
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
        HeadwearTilt.Forget(model.transform);
        ForgetGlow(model.transform);
        if (_previewMountOf.TryGetValue(model, out MountRig rig))
        {
            rig.Release();
            _previewMountOf.Remove(model);
            _previewMounts.Remove(rig);
        }
        if (_mountOf != null && _mountOf.IsChildOf(model.transform))
            ReleaseMount();
    }

    // Each frame, the models' mounts set again; all whole again with this mod off.
    internal static void UpdatePreviewMounts()
    {
        UpdateShownGlows();
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

    // The eyepieces' light on the face of such a model, as on the player's own (NightVisionFaceLight): a spot light
    // behind each lens of its own, its cookie the lens's disc spread by the field, lit while the goggle is switched on;
    // in the phosphor's colour the player last saw it in (BRNVG's, or the game's filter's), else the goggle's own.
    private sealed class ShownGlow
    {
        internal Transform Model = null!, Body = null!;
        internal NightVisionDevice? Switch;
        internal Device Kind = null!;
        internal readonly List<(Light light, float behind)> Lights = new List<(Light, float)>();
    }

    private static readonly List<ShownGlow> _shownGlows = new List<ShownGlow>();
    private static readonly Dictionary<string, Color> _phosphorOf = new Dictionary<string, Color>();

    private static void ShowGlow(Transform model, Transform body, Device kind)
    {
        if (kind.Thermal)
            return;
        ForgetGlow(model);
        var glow = new ShownGlow { Model = model, Body = body, Kind = kind, Switch = body.GetComponentInParent<NightVisionDevice>(true) };
        float half = FieldOfView * 0.5f * Mathf.Deg2Rad;
        for (int i = 0; i < kind.Tubes.Length; i++)
        {
            bool repeated = false;
            for (int j = 0; j < i; j++)
                repeated |= kind.Tubes[j].Centre == kind.Tubes[i].Centre;
            if (repeated)
                continue;
            Tube tube = kind.Tubes[i];
            Vector3 normal = tube.Normal.normalized;
            Quaternion lensTurn = Quaternion.LookRotation(normal, Vector3.forward);
            Vector3 floor = tube.LensRadius > 0f ? tube.Lens : tube.Centre - normal * kind.FloorDepth;
            Vector3 local = Quaternion.Inverse(lensTurn) * (floor - tube.Centre);
            float radius = tube.LensRadius > 0f ? tube.LensRadius : kind.LensRadius;
            float spread = EyeRelief * Mathf.Tan(half), behind = radius / Mathf.Tan(half);
            var holder = new GameObject("Justachae NVG face light") { layer = body.gameObject.layer };
            holder.transform.SetParent(body, false);
            holder.transform.SetLocalPositionAndRotation(tube.Centre + lensTurn * new Vector3(local.x, local.y, local.z - behind), lensTurn);
            Light light = holder.AddComponent<Light>();
            light.type = LightType.Spot;
            light.spotAngle = 2f * Mathf.Atan((radius + spread) / (behind + EyeRelief)) * Mathf.Rad2Deg;
            light.cookie = FaceCookie(radius, spread);
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            light.enabled = false;
            glow.Lights.Add((light, behind));
        }
        _shownGlows.Add(glow);
    }

    private static void ForgetGlow(Transform model)
    {
        for (int i = _shownGlows.Count - 1; i >= 0; i--)
        {
            if (_shownGlows[i].Model != model)
                continue;
            DropGlow(i);
        }
    }

    private static void DropGlow(int i)
    {
        // The game's own glow back as the game has it (lit while switched on).
        NightVisionDevice? device = _shownGlows[i].Switch;
        if (device != null && device._light != null && DeviceComponent?.GetValue(device) != null)
            device.CG_Init();
        foreach ((Light light, float _) in _shownGlows[i].Lights)
        {
            if (light != null)
                Object.Destroy(light.gameObject);
        }
        _shownGlows.RemoveAt(i);
    }

    private static void UpdateShownGlows()
    {
        for (int i = _shownGlows.Count - 1; i >= 0; i--)
        {
            ShownGlow glow = _shownGlows[i];
            if (glow.Model == null || glow.Body == null || !NewCameraSwitch.Enabled)
            {
                DropGlow(i);
                continue;
            }
            NightVisionComponent? component = glow.Switch != null ? DeviceComponent?.GetValue(glow.Switch) as NightVisionComponent : null;
            bool lit = component != null && component.Togglable != null && component.Togglable.On && glow.Body.gameObject.activeInHierarchy;
            Color color = Color.white;
            if (lit && !_phosphorOf.TryGetValue(glow.Kind.Node, out color))
            {
                Color own = component!.Template.Color;
                float brightest = Mathf.Max(own.r, Mathf.Max(own.g, own.b));
                color = brightest > 1e-4f ? own / brightest : Color.white;
            }
            // The game's own green glow off: the eyepieces' light is ours (as on the worn goggle: KeepGlow).
            if (glow.Switch != null && glow.Switch._light != null && glow.Switch._light.enabled)
                glow.Switch._light.enabled = false;
            float scale = glow.Body.lossyScale.x;
            foreach ((Light light, float behind) in glow.Lights)
            {
                if (light == null)
                    continue;
                light.enabled = lit;
                if (!lit)
                    continue;
                light.color = color;
                light.intensity = FaceLightIntensity;
                light.range = (behind + FaceLightRange) * scale;
            }
        }
    }
}
