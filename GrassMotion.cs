using GPUInstancer;
using HarmonyLib;

namespace EFTNewCamera;

// The grass flickered under the mouse while this mod has the camera, and no resolution helped; without its motion vectors
// it does not. GPU Instancer records the matrices for them in its Update, before this mod widens the field (LateUpdate)
// and places the camera (pre-cull): with the fisheye, a field of 50 degrees against the 92 drawn. So its grass goes
// through its depth pass alone, as it does in the game when no effect asks for motion vectors.
internal static class GrassMotion
{
    internal static void Install()
    {
        new Harmony(NewCameraPlugin.PluginGuid + ".grassmotion").Patch(
            AccessTools.PropertyGetter(typeof(GPUInstancerManager), nameof(GPUInstancerManager.bGenerateMotionVectors)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(GrassMotion), nameof(Postfix))));
    }

    private static void Postfix(ref bool __result)
    {
        if (NewCameraSwitch.Enabled)
            __result = false;
    }
}
