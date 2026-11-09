using EFT;
using UnityEngine;

namespace EFTNewCamera;

internal static class CameraPosition
{
    // Across, as it is now: the set place (Across), or, aimed, the aiming eye's (AimEye).
    internal static float Horizontal;
    internal static float Across;
    // Aimed, the camera goes over to the eye the gun is shouldered for: the right one, or the left one with the gun on
    // the left shoulder (the game's left stance), the gun with it (AimArms places it from the camera). At the chest
    // (ChestStance) the camera stays, and only the gun is placed as from that eye (PoseAcross).
    internal static bool AimEyeEnabled = true;
    internal static float AimEye = 0.03f;            // the eye from the head's middle (m)
    internal static float Vertical;
    internal static float Forward;
    // How far further ahead the camera is while a magnified sight is aimed through the goggle (NightVisionScope.cs):
    // part of the camera's place from the head like the rest, so that what works from that place (the gun's carry,
    // which takes it off and puts it back at the body's own turn) moves it as it does them.
    internal static float ScopeAhead;

    private const float AimInSeconds = 0.2f;
    private const float AimOutSeconds = 0.35f;       // as long as the gun takes back to the hip (AimArms.LeaveSeconds)
    private const float SideSeconds = 0.3f;
    private static float _aimed, _viewAimed;
    // Across from the camera to where the gun is placed from (the aiming eye's place as the pose has it), camera's right (m).
    internal static float PoseAcross;
    private static float _side = 1f;
    // How far over to the aiming eye the camera is, 0 to 1, as it moves.
    internal static float ViewAimed => Smooth(_viewAimed);

    // Each frame, before the camera is placed: how far over to the aiming eye the pose is (aimed, also at the chest)
    // and the camera (aimed, not at the chest).
    internal static void UpdateAimEye(Player player)
    {
        bool aimed = AimEyeEnabled && player.HandsController != null && player.HandsController.IsAiming;
        _aimed = Ease(_aimed, aimed);
        _viewAimed = Ease(_viewAimed, aimed && !ChestStance.Active);
        bool left = false;
        try { left = player.MovementContext?.LeftStanceController?.LeftStance == true; } catch { }
        _side = Mathf.MoveTowards(_side, left ? -1f : 1f, 2f * Time.deltaTime / SideSeconds);
        float u = 0.5f * (_side + 1f);
        float side = 2f * u * u * (3f - 2f * u) - 1f;
        float pose = Mathf.Lerp(Across, side * AimEye, Smooth(_aimed));
        Horizontal = Mathf.Lerp(Across, side * AimEye, Smooth(_viewAimed));
        PoseAcross = pose - Horizontal;
    }

    private static float Ease(float at, bool on)
    {
        float target = on ? 1f : 0f;
        return Mathf.MoveTowards(at, target, Time.deltaTime / (target > at ? AimInSeconds : AimOutSeconds));
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    internal static void ResetAimEye()
    {
        _aimed = _viewAimed = 0f;
        Horizontal = Across;
        PoseAcross = 0f;
    }

    internal static Vector3 Resolve(Vector3 headPosition, Quaternion cameraRotation)
    {
        return headPosition + Offset(cameraRotation);
    }

    // Where the camera stands from the head, along the world's axes.
    internal static Vector3 Offset(Quaternion cameraRotation) => cameraRotation * new Vector3(Horizontal, Vertical, Forward + ScopeAhead);
}
