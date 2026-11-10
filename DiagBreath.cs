using EFT;
using UnityEngine;

namespace EFTNewCamera;

// TEMPORARY: aimed, how much each layer moves over half a second (peak to peak), with the breath held or not.
internal static class DiagBreath
{
    private static float _start = -1f;
    private static int _frames;
    private static Vector2 _rawHeadMin, _rawHeadMax, _headMin, _headMax, _camMin, _camMax, _lineMin, _lineMax, _placeMin, _placeMax;

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

    // From AimArms.Sway, each frame aimed: how much each part of the gun's placement moves.
    private static bool _partsFirst = true;
    private static Vector2 _armMin, _armMax, _rawMin, _rawMax, _lineTurnMin, _lineTurnMax, _keptMin, _keptMax, _kickMin, _kickMax;
    private static float _laterMax;
    private static int _chasing, _catching, _acting, _parts;
    internal static void Parts(Quaternion arm, Quaternion raw, Quaternion line, Quaternion kept, Quaternion kick, Vector3 later, bool chasing, bool catching, bool acting)
    {
        bool first = _partsFirst;
        _partsFirst = false;
        Take(Angles(arm), ref _armMin, ref _armMax, first);
        Take(Angles(raw), ref _rawMin, ref _rawMax, first);
        Take(Angles(line), ref _lineTurnMin, ref _lineTurnMax, first);
        Take(Angles(kept), ref _keptMin, ref _keptMax, first);
        Take(Angles(kick), ref _kickMin, ref _kickMax, first);
        if (first) { _laterMax = 0f; _chasing = _catching = _acting = _parts = 0; }
        _laterMax = Mathf.Max(_laterMax, later.magnitude * 1000f);
        _parts++;
        if (chasing) _chasing++;
        if (catching) _catching++;
        if (acting) _acting++;
    }

    private static string PartsText()
    {
        if (_partsFirst)
            return " parts none";
        _partsFirst = true;
        return $" parts n={_parts} arm={_armMax - _armMin:F3} rawGun={_rawMax - _rawMin:F3} lineFix={_lineTurnMax - _lineTurnMin:F3} kept={_keptMax - _keptMin:F3}"
            + $" kick={_kickMax - _kickMin:F3} kickMean={(_kickMax + _kickMin) * 0.5f:F3} laterMax={_laterMax:F2}mm chasing={_chasing} catching={_catching} acting={_acting}";
    }

    private static int _bolt, _mode, _check;
    internal static void Started(string why)
    {
        if (why == "bolt") _bolt++; else if (why == "mode") _mode++; else _check++;
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
        Take(Angles(AimArms.DiagRawHead), ref _rawHeadMin, ref _rawHeadMax, first);
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
        string body = "";
        try
        {
            Physical? physical = player.Physical as Physical;
            if (physical != null)
                body = $" arms={physical.HandsStamina?.Current:F1} exhausted={physical.HandsStamina?.Exhausted} oxygen={physical.Oxygen?.Current:F1} legs={physical.Stamina?.Current:F1}"
                    + $" overweight={physical.Overweight:F2}";
        }
        catch (System.Exception e) { body = " body? " + e.Message; }
        NewCameraPlugin.Log?.LogInfo($"DiagBreath holding={holding} frames={_frames} {AimArms.DiagState} sway={AimArms.DiagSway} rawHead={_rawHeadMax - _rawHeadMin:F3}"
            + $" head(p,y)={_headMax - _headMin:F3} cam={_camMax - _camMin:F3} line={_lineMax - _lineMin:F3} deg place(x,y)={_placeMax - _placeMin:F2} mm"
            + $" camMean={(_camMax + _camMin) * 0.5f:F2}" + body + PartsText() + $" started bolt={_bolt} mode={_mode} check={_check}");
        _bolt = _mode = _check = 0;
        _start = -1f;
    }
}
