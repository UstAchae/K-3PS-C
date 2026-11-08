using GPUInstancer;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// The grass flickered under the mouse while this mod has the camera, and no resolution helped. GPU Instancer culls the
// grass, records the matrices for its motion vectors and submits it in its Update, from the camera as it stands then: but
// this mod widens the field in LateUpdate and places the camera at pre-cull (a field of 50 degrees against the 92 drawn).
// For that Update the camera is what this mod last drew with: the field as it is, and the place and turn a frame old.
internal static class GrassCamera
{
    private struct Kept
    {
        internal Camera? Camera;
        internal float Field;
        internal Vector3 Position;
        internal Quaternion Rotation;
    }

    internal static void Install()
    {
        new Harmony(NewCameraPlugin.PluginGuid + ".grasscamera").Patch(
            AccessTools.Method(typeof(GPUInstancerManager), nameof(GPUInstancerManager.Update)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(GrassCamera), nameof(Prefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(GrassCamera), nameof(Postfix))));
    }

    private static void Prefix(GPUInstancerManager __instance, out Kept __state)
    {
        __state = default;
        if (!NewCameraSwitch.Enabled || __instance.IsOptic || !NewCameraSwitch.LastPose(out Vector3 position, out Quaternion rotation))
            return;
        Camera? camera = __instance.Camera;
        if (camera == null)
            return;
        Transform moved = camera.transform;
        __state = new Kept { Camera = camera, Field = camera.fieldOfView, Position = moved.position, Rotation = moved.rotation };
        if (Fisheye.Enabled && Fisheye.LastWideField > 0f)
            camera.fieldOfView = Fisheye.LastWideField;
        moved.SetPositionAndRotation(position, rotation);
    }

    private static void Postfix(Kept __state)
    {
        if (__state.Camera == null)
            return;
        __state.Camera.fieldOfView = __state.Field;
        __state.Camera.transform.SetPositionAndRotation(__state.Position, __state.Rotation);
    }
}
