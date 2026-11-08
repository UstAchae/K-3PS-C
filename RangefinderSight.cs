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
    // The optic camera's near plane is 0.05 m, and nothing of the device lies in front of that.
    private const float ReadingDistance = 0.1f;

    private static float _aimedAt = -1f;
    private static bool _logged;
    private static Vector3 _readingOff;

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

    // The reading is a canvas 2 m in front of the optic camera, depth tested against the scene, so anything nearer than
    // that hides it: nearer to the camera, it shows whatever is aimed at.
    // The item's icon or preview leaves its pooled model on the Weapon Preview layer, and taking it in hand only brings the
    // meshes back, not the reading, which the optic camera (seeing no Weapon Preview) then never draws.
    internal static void ReadingPostfix(TacticalRangeFinderController __instance)
    {
        if (!NewCameraSwitch.Enabled)
            return;
        TransformTools.SetLayersRecursively(__instance.gameObject, LayerMask.NameToLayer("Player"));
        if (__instance._canvas != null)
            __instance._canvas.planeDistance = ReadingDistance;
    }

    // The canvas follows the optic camera before our pose moves that camera with the device (AimArms.CarryOptic), and the
    // window of a 3.2 degree view is far narrower than the move: the reading is put back in front of the camera as it is.
    internal static void PlaceReading(Player player, Camera camera)
    {
        if (player.HandsController is not PortableRangeFinderController controller || !controller.IsAiming)
            return;
        TacticalRangeFinderController display = Display(controller);
        Canvas? canvas = display != null ? display._canvas : null;
        if (canvas == null)
            return;
        Transform view = camera.transform;
        Transform placed = canvas.transform;
        _readingOff = view.InverseTransformPoint(placed.position);
        placed.SetPositionAndRotation(view.position + view.forward * canvas.planeDistance, view.rotation);
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
        NewCameraPlugin.Log?.LogInfo($"Rangefinder reading: active {display.gameObject.activeInHierarchy}, text '{display._textOnDisplay?.text}', canvas on {display._canvas?.enabled}, plane {display._canvas?.planeDistance:F2}, canvas camera '{display._canvas?.worldCamera?.name}', layer {display._textOnDisplay?.gameObject.layer}, optic camera on {optic?.enabled}, mask {optic?.cullingMask}, canvas was at {_readingOff.ToString("F3")} from the camera, now {(optic != null && display._canvas != null ? optic.transform.InverseTransformPoint(display._canvas.transform.position).ToString("F3") : "?")}");
    }
}
