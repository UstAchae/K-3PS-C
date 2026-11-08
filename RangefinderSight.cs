using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

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

    private static CommandBuffer? _reading;
    private static Camera? _readingCamera;

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

    // Each optic camera frame, once our pose has carried that camera: the game's own text (its mesh, its material) drawn
    // into the picture where its canvas has it. Unity takes canvases for drawing as it updates them, before the camera's
    // pre-cull in which this mod moves the device and the camera, so the canvas itself does not show.
    internal static void DrawReading(Player player, Camera camera)
    {
        TacticalRangeFinderController? display = player.HandsController is PortableRangeFinderController controller && controller.IsAiming
            ? Display(controller)
            : null;
        TMPro.TMP_Text? text = display != null && display.isActiveAndEnabled ? display._textOnDisplay : null;
        if (text == null || text.textInfo == null)
        {
            ClearReading();
            return;
        }
        if (_reading == null)
            _reading = new CommandBuffer { name = "Rangefinder reading" };
        if (_readingCamera != camera)
        {
            if (_readingCamera != null)
                _readingCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _reading);
            camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _reading);
            _readingCamera = camera;
        }
        _reading.Clear();
        // Each of its materials has its own mesh, and the characters may well all be in another than the first.
        Matrix4x4 place = text.rectTransform.localToWorldMatrix;
        TMPro.TMP_TextInfo info = text.textInfo;
        for (int i = 0; i < info.materialCount && i < info.meshInfo.Length; i++)
        {
            Mesh? mesh = info.meshInfo[i].mesh;
            Material? material = info.meshInfo[i].material;
            if (mesh == null || material == null || info.meshInfo[i].vertexCount == 0)
                continue;
            _reading.DrawMesh(mesh, place, material, 0, 0);
        }
    }

    internal static void ClearReading()
    {
        _reading?.Clear();
    }
}
