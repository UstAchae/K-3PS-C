using System.Collections.Generic;
using EFT;
using EFT.Animations;
using EFT.InputSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// Sights set by range on weapons that lob (the M32A1's M2A1): as the game does in first person, the sight is turned for
// the range set, the gun is raised for it, and the eye looks along the line from the lens to the point the range gives.
// The game calibrates only in its first-person and mounted strategies. In the third-person one, which this mod keeps the
// player in, the point stays at zero, which is the muzzle: the sight did not turn and the gun rose by the slope from the
// lens down to the muzzle (some 25 degrees on the M32A1). Its shift of the camera onto that line is first person only too.
//
// The range goes in steps in the game (its points on the trajectory, its table of the sight's angle). Here PgUp and PgDn
// set it anywhere between the first and the last: the point and the angle laid between the two either side of it.
internal static class CalibratedSight
{
    private const float TapMetres = 5f;
    private const float HeldMetresPerSecond = 40f;
    private const float HeldAfterSeconds = 0.15f;

    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, bool> Adjusts =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, bool>("_adjustCollimatorsToTrajectory");
    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, Vector3> Point =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, Vector3>("_localCalibratePoint");
    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> Slope =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_lineOfSightDeltaAngle");

    private static readonly Dictionary<SightComponent, float> Ranges = new();
    private static Vector3 _appliedPoint;
    private static SightComponent? _appliedSight;
    private static int _shownMetres = -1;
    private static int _heldSign;
    private static float _heldFrom;
    private static float _tapped;

    internal static void EveryFrame(ProceduralWeaponAnimation animation)
    {
        if (!Adjusts(animation) || Scopes.Current(animation) == null)
            return;
        animation.PreCalibrate();
        ContinuousRange(animation);
        animation.Calibrate();

    }

    // The sight aimed through, if it is one the range turns, with what the game has for it.
    private static bool RangedSight(ProceduralWeaponAnimation animation, out SightComponent mod, out ScopePrefabCache cache, out int[] distances, out Vector3[] points)
    {
        mod = null!;
        cache = null!;
        distances = null!;
        points = null!;
        ProceduralWeaponAnimation.SightNBone? scope = Scopes.Current(animation);
        if (scope?.Mod == null || scope.ScopePrefabCache == null || !scope.ScopePrefabCache.CanChangeAngleByDistance
            || scope.ScopePrefabCache.WeaponScopeAxis == null || scope.ScopePrefabCache.AngleByRange == null || scope.ScopePrefabCache.AngleByRange.Length == 0
            || !scope.ScopePrefabCache.HasCollimators || !scope.Mod.HasOpticCalibrationPoints(scope.Mod.SelectedScope))
            return false;
        int[]? table = scope.Mod.GetScopeCalibrationDistances(scope.Mod.SelectedScope);
        Vector3[] onPath = scope.Mod.OpticCalibrationPoints[scope.Mod.SelectedScope];
        if (table == null || table.Length == 0 || onPath.Length != table.Length)
            return false;
        mod = scope.Mod;
        cache = scope.ScopePrefabCache;
        distances = table;
        points = onPath;
        return true;
    }

    private static void ContinuousRange(ProceduralWeaponAnimation animation)
    {
        if (!RangedSight(animation, out SightComponent mod, out ScopePrefabCache cache, out int[] distances, out Vector3[] points))
            return;
        if (!Ranges.TryGetValue(mod, out float range))
            range = mod.GetCurrentOpticCalibrationDistance();
        float wanted = Mathf.Clamp(range + Moved(animation), distances[0], distances[distances.Length - 1]);
        // Set by the game since (it does as the aim begins): the range here goes on.
        if (Mathf.Approximately(wanted, range) && ReferenceEquals(mod, _appliedSight) && Point(animation) == _appliedPoint)
            return;
        bool setByGame = !ReferenceEquals(mod, _appliedSight) || Point(animation) != _appliedPoint;
        Ranges[mod] = wanted;
        Apply(animation, mod, cache, distances, points, wanted);
        int metres = Mathf.RoundToInt(wanted);
        if (metres != _shownMetres || setByGame)
        {
            _shownMetres = metres;
            GamePlayerOwner.MyPlayer?.ShowAmmoCountZeroingPanel(metres.ToString());
        }
    }

    // How far the keys have moved the range this frame, while aiming.
    private static float Moved(ProceduralWeaponAnimation animation)
    {
        float tapped = _tapped;
        _tapped = 0f;
        if (!animation.IsAiming)
        {
            _heldSign = 0;
            return 0f;
        }
        int sign = (Input.GetKey(KeyCode.PageUp) ? 1 : 0) - (Input.GetKey(KeyCode.PageDown) ? 1 : 0);
        if (sign != _heldSign)
        {
            _heldSign = sign;
            _heldFrom = Time.unscaledTime + HeldAfterSeconds;
            return tapped + sign * TapMetres;
        }
        if (sign != 0 && Time.unscaledTime >= _heldFrom)
            return tapped + sign * HeldMetresPerSecond * Time.unscaledDeltaTime;
        return tapped;
    }

    private static void Apply(ProceduralWeaponAnimation animation, SightComponent mod, ScopePrefabCache cache, int[] distances, Vector3[] points, float range)
    {
        int next = 1;
        while (next < distances.Length - 1 && distances[next] < range)
            next++;
        Vector3 point = points[0];
        if (distances.Length > 1)
            point = Vector3.Lerp(points[next - 1], points[next], Mathf.InverseLerp(distances[next - 1], distances[next], range));
        Point(animation) = point;
        _appliedPoint = point;
        _appliedSight = mod;

        // The game's table, by the same reckoning.
        ScopePrefabCache.DistaneAngle[] angles = cache.AngleByRange;
        int after = 1;
        while (after < angles.Length - 1 && angles[after].Distance < range)
            after++;
        float angle = angles[0].Angle;
        if (angles.Length > 1)
            angle = Mathf.Lerp(angles[after - 1].Angle, angles[after].Angle, Mathf.InverseLerp(angles[after - 1].Distance, angles[after].Distance, range));
        cache.WeaponScopeAxis.localRotation = Quaternion.Euler(angle, 0f, 0f);

        // The slope the game raises the gun by, as it reckons it, from the lens as it now lies.
        Transform? weapon = animation.HandsContainer?.Weapon;
        if (weapon != null)
        {
            Vector3 lens = weapon.InverseTransformPoint(cache.GetLensCenter().position);
            Vector3 target = weapon.InverseTransformPoint(animation.GetWorldCalibrationPoint(point));
            Slope(animation) = Mathf.Atan((target.z - lens.z) / (lens.y - target.y)) * Mathf.Rad2Deg;
        }
        animation.CollimatorLookAt(point);

        // The game's own setting left at the nearest, for where the mod is off.
        int nearest = 0;
        for (int i = 1; i < distances.Length; i++)
            if (Mathf.Abs(distances[i] - range) < Mathf.Abs(distances[nearest] - range))
                nearest = i;
        mod.SetSelectedOpticCalibrationPoint(mod.SelectedScope, nearest);
    }

    // The range keys taken from the game, for such a sight: a press of keys other than PgUp and PgDn, if rebound, is a step.
    internal static bool TranslatePrefix(ECommand command, ref InputNode.ETranslateResult __result)
    {
        if (command != ECommand.OpticCalibrationSwitchUp && command != ECommand.OpticCalibrationSwitchDown)
            return true;
        if (!NewCameraSwitch.Enabled)
            return true;
        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        ProceduralWeaponAnimation? animation = player?.ProceduralWeaponAnimation;
        if (animation == null || !Adjusts(animation) || !animation.IsAiming || !RangedSight(animation, out _, out _, out _, out _))
            return true;
        if (!Input.GetKey(KeyCode.PageUp) && !Input.GetKey(KeyCode.PageDown))
            _tapped += command == ECommand.OpticCalibrationSwitchUp ? TapMetres : -TapMetres;
        __result = InputNode.ETranslateResult.Block;
        return false;
    }

    // Where the eye looks from at the sight bone's depth, on the line from the lens to the range's point, and along it.
    internal static bool TryResolve(ProceduralWeaponAnimation animation, Transform bone, out Vector3 position, out Vector3 forward)
    {
        position = bone.position;
        forward = -bone.forward;
        Transform? weapon = animation.HandsContainer?.Weapon;
        ProceduralWeaponAnimation.SightNBone? scope = Scopes.Current(animation);
        if (weapon == null || !Adjusts(animation) || scope == null || scope.IsOptic || scope.ScopePrefabCache == null
            || !scope.ScopePrefabCache.HasCollimators || Point(animation) == Vector3.zero)
            return false;

        Vector3 lens = weapon.InverseTransformPoint(scope.ScopePrefabCache.GetLensCenter().position);
        Vector3 target = weapon.InverseTransformPoint(animation.GetWorldCalibrationPoint(Point(animation)));
        Vector3 eye = weapon.InverseTransformPoint(bone.position);
        Vector3 line = target - lens;
        line.x = 0f;
        // The weapon's -Y runs forward, as the game takes it.
        if (line.y >= -0.0001f)
            return false;
        float along = (eye.y - lens.y) / line.y;
        position = weapon.TransformPoint(new Vector3(eye.x, eye.y, lens.z + line.z * along));
        forward = weapon.TransformDirection(line).normalized;
        return true;
    }
}
