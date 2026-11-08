using Comfort.Common;
using EFT;
using EFT.Settings;
using UnityEngine;

namespace EFTNewCamera;

// The gym seen from the character's eyes. The game shows a workout from a fixed camera of its own above the bench (the
// area's SpecialActionCamera, through the hideout's Cinemachine brain, which also sets the lens every frame). While the
// character works out, the camera is put on the head just before it renders, as when walking about, and with its lens.
internal static class HideoutWorkout
{
    private const float Near = 0.01f;

    // Placed: nothing else of this mod's applies during a workout.
    internal static bool Place(Player? player, Camera camera)
    {
        if (player is not HideoutPlayer hideout || !hideout.CustomAnimationsAreProcessing)
            return false;
        Transform? head = hideout.PlayerBones?.Head?.Original;
        if (head == null)
            return false;
        Quaternion rotation = CameraDirection.Resolve(head.rotation);
        camera.transform.SetPositionAndRotation(CameraPosition.Resolve(head.position, rotation), rotation);
        camera.fieldOfView = Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView;
        camera.nearClipPlane = Near;
        return true;
    }
}
