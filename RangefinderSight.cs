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
        relief = optic.DistanceToCamera;
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
    }
}
