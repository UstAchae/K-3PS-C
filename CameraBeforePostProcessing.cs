using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// The FPS camera placed before its post-processing makes its frame's commands. PostProcessing builds them in its own
// OnPreCull, with the camera's view as it stands then (the screen-space reflections' view matrix among them), and
// that came before the cameras' pre-cull callback in which this mod places the camera: the reflections were worked out
// from the game's own camera place and turn while the frame was drawn from this mod's, and laid a dark band over the
// ground that followed the view (the more, the more the head was turned by the middle button). This mod's placing
// is done first, from the post-processing's OnPreCull, once a frame (NewCameraSwitch.OnCameraPreCull).
internal static class CameraBeforePostProcessing
{
    private static bool _patched;

    internal static void Install()
    {
        if (_patched)
            return;
        _patched = true;
        MethodInfo? preCull = AccessTools.Method(typeof(PostProcessLayer), "OnPreCull");
        if (preCull == null)
        {
            NewCameraPlugin.Log?.LogWarning("Camera before post-processing: PostProcessLayer.OnPreCull not found");
            return;
        }
        new Harmony(NewCameraPlugin.PluginGuid + ".postfx").Patch(preCull,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CameraBeforePostProcessing), nameof(Prefix))));
    }

    private static void Prefix(PostProcessLayer __instance)
    {
        Camera? camera = __instance != null ? __instance.GetComponent<Camera>() : null;
        if (camera == null || camera != EFT.CameraControl.CameraManager.Instance?.Camera)
            return;
        NewCameraPlugin.GuardedPreCull(camera);
    }
}
