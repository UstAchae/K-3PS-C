using Comfort.Common;
using EFT;
using EFT.Settings;
using UnityEngine;

namespace EFTNewCamera;

// The gym seen from the character's eyes. The game shows a workout from a fixed camera of its own above the bench (the
// area's SpecialActionCamera, through the hideout's Cinemachine brain, which also sets the lens every frame). While the
// character works out, the camera is put on the head just before it renders, as when walking about, and with its lens.
// The eyes look down to the hands: the head looks ahead through the exercise, and the arms were out of sight below.
internal static class HideoutWorkout
{
    private const float Near = 0.01f;
    private const float LookDownSeconds = 1.5f;   // how slowly the eyes follow the hands (a curl does not bob the view)
    private const float MaxLookDown = 50f;        // degrees
    private static float _lookDown;
    private static int _lookFrame = -1;
    private static bool _working;

    // Placed: nothing else of this mod's applies during a workout.
    internal static bool Place(Player? player, Camera camera)
    {
        if (player is not HideoutPlayer hideout || !hideout.CustomAnimationsAreProcessing)
        {
            _working = false;
            return false;
        }
        Transform? head = hideout.PlayerBones?.Head?.Original;
        if (head == null)
            return false;
        Quaternion rotation = CameraDirection.Resolve(head.rotation);
        Vector3 position = CameraPosition.Resolve(head.position, rotation);
        if (_lookFrame != Time.frameCount)
        {
            _lookFrame = Time.frameCount;
            Transform? left = hideout.PlayerBones?.LeftPalm, right = hideout.PlayerBones?.RightPalm;
            if (left != null && right != null)
            {
                // How far below the head's own forward the hands are, seen from the eyes.
                Vector3 toHands = Quaternion.Inverse(rotation) * ((left.position + right.position) * 0.5f - position);
                float below = Mathf.Clamp(Mathf.Atan2(-toHands.y, Mathf.Max(toHands.z, 0.01f)) * Mathf.Rad2Deg, 0f, MaxLookDown);
                _lookDown = _working ? Mathf.Lerp(_lookDown, below, 1f - Mathf.Exp(-Time.deltaTime / LookDownSeconds)) : below;
                _working = true;
            }
        }
        rotation *= Quaternion.AngleAxis(_lookDown, Vector3.right);
        camera.transform.SetPositionAndRotation(position, rotation);
        camera.fieldOfView = Singleton<SettingsManager>.Instance.Game.Settings.FieldOfView;
        camera.nearClipPlane = Near;
        return true;
    }
}
