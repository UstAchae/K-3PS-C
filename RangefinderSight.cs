using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// The handheld rangefinder aimed like a gun's scope: its aim point is the lens itself, at the eye relief the item's own
// data gives, on the axis of its optic. The game's aim_camera bone is not on the device: the first-person camera only
// meets the eyepiece through the aim animation, and our camera does not go to that bone but brings the bone to itself.
internal static class RangefinderSight
{
    private static readonly AccessTools.FieldRef<PortableRangeFinderController, OpticSight> Optic =
        AccessTools.FieldRefAccess<PortableRangeFinderController, OpticSight>("_opticSight");

    private static readonly AccessTools.FieldRef<PortableRangeFinderController, TacticalRangeFinderController> Display =
        AccessTools.FieldRefAccess<PortableRangeFinderController, TacticalRangeFinderController>("_rangeFinderController");

    // A little further off the eye than the item's own relief.
    private const float ExtraRelief = 0.015f;

    private static float _aimedAt = -1f;
    private static bool _logged;

    internal static bool TryGet(Player player, out Transform lens, out Transform device, out float relief)
    {
        lens = null!;
        device = null!;
        relief = 0f;
        if (player.HandsController is not PortableRangeFinderController controller)
            return false;
        OpticSight optic = Optic(controller);
        if (optic == null || optic.LensRenderer == null)
            return false;
        lens = optic.LensRenderer.transform;
        device = optic.transform;
        relief = optic.DistanceToCamera + ExtraRelief;
        return true;
    }

    // The device's forward: its optic camera looks along the item's -Y, with +X to its right.
    internal static Vector3 Forward(Transform device)
    {
        return -device.up;
    }

    // The turn, with the device's roll taken out, so that it lies level in the view whatever the hold pose has it at.
    internal static Quaternion Level(Quaternion turn, Transform device, Quaternion view)
    {
        Vector3 axis = view * Vector3.forward;
        Vector3 right = Vector3.ProjectOnPlane(turn * device.right, axis);
        if (right.sqrMagnitude < 0.000001f)
            return turn;
        return Quaternion.AngleAxis(Vector3.SignedAngle(right, view * Vector3.right, axis), axis) * turn;
    }

    // The item's icon or preview leaves its pooled model on the Weapon Preview layer, and taking it in hand only brings the
    // meshes back, not the reading, which the optic camera (seeing no Weapon Preview) then never draws.
    internal static void ReadingPostfix(TacticalRangeFinderController __instance)
    {
        if (NewCameraSwitch.Enabled)
            TransformTools.SetLayersRecursively(__instance.gameObject, LayerMask.NameToLayer("Player"));
    }

    // Temporary: where the lens ended up, once per aim, a second in.
    internal static void Report(Player player, bool aiming, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        if (!aiming || !TryGet(player, out Transform lens, out Transform device, out float relief))
        {
            _aimedAt = -1f;
            _logged = false;
            return;
        }
        if (_aimedAt < 0f)
            _aimedAt = Time.time;
        if (_logged || Time.time - _aimedAt < 1f)
            return;
        _logged = true;
        Quaternion toView = Quaternion.Inverse(cameraRotation);
        NewCameraPlugin.Log?.LogInfo($"Rangefinder: lens '{lens.name}' in view {(toView * (lens.position - cameraPosition)).ToString("F3")}, relief {relief:F3}, forward in view {(toView * Forward(device)).ToString("F3")}, right in view {(toView * device.right).ToString("F3")}");
        TacticalRangeFinderController? display = player.HandsController is PortableRangeFinderController controller ? Display(controller) : null;
        if (display == null)
            return;
        Camera? optic = CameraManager.Instance?.OpticCameraManager?.Camera;
        Canvas? canvas = display._canvas;
        TMPro.TMP_Text? text = display._textOnDisplay;
        NewCameraPlugin.Log?.LogInfo($"Rangefinder reading: active {display.gameObject.activeInHierarchy}, text '{text?.text}', characters {text?.textInfo?.characterCount}, text on {text?.enabled}, alpha {text?.color.a:F2}, culled {text?.canvasRenderer?.cull}, text layer {text?.gameObject.layer}, canvas on {canvas?.enabled}, canvas layer {canvas?.gameObject.layer}, mode {canvas?.renderMode}, plane {canvas?.planeDistance:F2}, scale {canvas?.transform.lossyScale.x:F7}, size {(canvas?.transform as RectTransform)?.rect.size.ToString("F0")}, camera '{canvas?.worldCamera?.name}', optic on {optic?.enabled}, near {optic?.nearClipPlane:F3}, far {optic?.farClipPlane:F0}, fov {optic?.fieldOfView:F2}, mask {optic?.cullingMask}, pixels {optic?.pixelWidth}x{optic?.pixelHeight}");
    }
}
