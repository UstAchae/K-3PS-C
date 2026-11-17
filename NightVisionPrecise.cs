using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Far out in a map a place in the world is kept only to a few tens of micrometres, each point rounded to that on its own: the
// lenses are placed from the camera by where they truly stand from it (from the head down the bones by their small local
// offsets, less where the camera was put from the head), never by the difference of two rounded places in the world.
internal static partial class NightVisionEyepieces
{
    private static readonly Dictionary<Transform, Matrix4x4> _rootsFromCamera = new Dictionary<Transform, Matrix4x4>();
    private static bool _precise;

    // The device as it stands from the camera this frame; false away from the player's eyes, where the camera's place from the
    // head is not known.
    private static bool PrepareRelative(Camera camera)
    {
        _precise = false;
        _rootsFromCamera.Clear();
        Transform? head = NewCameraSwitch.PoseHead;
        if (head == null || !NewCameraSwitch.AtEyes(camera) || _body == null)
            return false;
        Transform root = DeviceSlot();
        Vector3? fromHead = FromHead(head, root);
        if (fromHead == null)
            return false;
        _rootsFromCamera[root] = Matrix4x4.TRS(fromHead.Value - NewCameraSwitch.PoseFromHead, root.rotation, root.lossyScale);
        _precise = true;
        return true;
    }

    // Where a transform under the device stands from the camera (turned with the world); for one elsewhere, from its place in the
    // world (as coarse as that is).
    private static Matrix4x4 FromCamera(Transform t, Vector3 cameraPlace)
    {
        foreach (KeyValuePair<Transform, Matrix4x4> root in _rootsFromCamera)
        {
            if (t.IsChildOf(root.Key))
                return root.Value * Chain(root.Key, t);
        }
        return Matrix4x4.Translate(-cameraPlace) * t.localToWorldMatrix;
    }
}
