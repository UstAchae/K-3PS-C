using EFT;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// What the player looks at to interact with (loot, doors, switches, tripwires): the game casts it from a point on the
// first-person rig (PlayerBones.LootRaycastOrigin) along its forward, where the game's own camera is. This mod puts the
// camera elsewhere (at the eyes of the third-person skeleton, at the aiming eye), and the screen's centre dot no longer
// marked what was picked. While this mod places the camera, the ray is the camera's: from it, through the centre of
// the screen, started as far behind as the game starts it (EFTHardSettings.BEHIND_CAST).
internal static class InteractionRay
{
    private static bool _patched;

    internal static void Install()
    {
        if (_patched)
            return;
        _patched = true;
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".interaction");
        var getter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.InteractionRay));
        if (getter == null)
        {
            NewCameraPlugin.Log?.LogWarning("Interaction ray: Player.InteractionRay not found; it stays the game's");
            return;
        }
        harmony.Patch(getter, prefix: new HarmonyMethod(AccessTools.Method(typeof(InteractionRay), nameof(Prefix))));
    }

    private static bool Prefix(Player __instance, ref Ray __result)
    {
        if (!NewCameraSwitch.Enabled || !NewCameraSwitch.CameraOf(__instance, out Vector3 position, out Quaternion rotation))
            return true;
        Vector3 forward = rotation * Vector3.forward;
        float behind = EFTHardSettings.Instance != null ? EFTHardSettings.Instance.BEHIND_CAST : 0f;
        __result = new Ray(position - forward * behind, forward);
        return false;
    }
}
