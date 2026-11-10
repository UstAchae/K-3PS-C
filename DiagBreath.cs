using EFT;
using UnityEngine;

namespace EFTNewCamera;

// TEMPORARY: aimed, how much each layer moves over half a second (peak to peak), with the breath held or not.
internal static class DiagBreath
{
    private static float _start = -1f;
    private static int _frames;
    private static Vector2 _headMin, _headMax, _camMin, _camMax, _lineMin, _lineMax, _placeMin, _placeMax;

    private static void Take(Vector2 v, ref Vector2 min, ref Vector2 max, bool first)
    {
        if (first) { min = max = v; return; }
        min = Vector2.Min(min, v);
        max = Vector2.Max(max, v);
    }

    private static Vector2 Angles(Quaternion q)
    {
        Vector3 e = q.eulerAngles;
        return new Vector2(Mathf.DeltaAngle(0f, e.x), Mathf.DeltaAngle(0f, e.y));
    }

    internal static void Sample(Player player, Quaternion headRotation, Vector3 cameraPosition, Quaternion cameraRotation)
    {
        if (player.HandsController == null || !player.HandsController.IsAiming)
        {
            _start = -1f;
            return;
        }
        Quaternion look = AimArms.DiagLook;
        bool first = _start < 0f;
        if (first)
        {
            _start = Time.time;
            _frames = 0;
        }
        _frames++;
        Take(Angles(Quaternion.Inverse(look) * headRotation), ref _headMin, ref _headMax, first);
        Take(Angles(Quaternion.Inverse(look) * cameraRotation), ref _camMin, ref _camMax, first);
        Transform? bone = Scopes.Current(player.ProceduralWeaponAnimation)?.Bone;
        if (bone != null)
        {
            Vector3 line = Quaternion.Inverse(cameraRotation) * -bone.forward;
            Take(new Vector2(Mathf.Atan2(line.y, line.z), Mathf.Atan2(line.x, line.z)) * Mathf.Rad2Deg, ref _lineMin, ref _lineMax, first);
            Vector3 at = Quaternion.Inverse(cameraRotation) * (bone.position - cameraPosition);
            Take(new Vector2(at.x, at.y) * 1000f, ref _placeMin, ref _placeMax, first);
        }
        if (Time.time - _start < 0.5f)
            return;
        bool holding = player.Physical != null && player.Physical.HoldingBreath;
        NewCameraPlugin.Log?.LogInfo($"DiagBreath holding={holding} frames={_frames} sway={AimArms.DiagSway}"
            + $" head(p,y)={_headMax - _headMin:F3} cam={_camMax - _camMin:F3} line={_lineMax - _lineMin:F3} deg place(x,y)={_placeMax - _placeMin:F2} mm"
            + $" camMean={(_camMax + _camMin) * 0.5f:F2}");
        _start = -1f;
    }
}
