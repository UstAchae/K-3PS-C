using EFT;
using EFT.Animations;
using EFT.CameraControl;
using UnityEngine;

namespace EFTNewCamera;

internal static class OpticPicture
{
    private static int _frame = -1;
    private static OpticSight? _held;
    // The other sights' optics this turned off (QuietOthers), turned back on when it lets go.
    private static readonly System.Collections.Generic.List<OpticSight> _quieted = new System.Collections.Generic.List<OpticSight>();

    internal static bool IsCamera(Camera camera)
    {
        try
        {
            Camera? optic = CameraManager.Instance?.OpticCameraManager?.Camera;
            return optic != null && optic.GetInstanceID() == camera.GetInstanceID();
        }
        catch
        {
            return false;
        }
    }

    internal static void Apply(Player player)
    {
        if (_frame == Time.frameCount)
            return;
        _frame = Time.frameCount;

        try
        {
            ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
            OpticCameraManager? manager = CameraManager.Instance?.OpticCameraManager;
            if (animation == null || manager == null)
            {
                ReleaseHeld();
                return;
            }

            bool aiming = player.HandsController != null && player.HandsController.IsAiming;
            ProceduralWeaponAnimation.SightNBone? scope = Scopes.Current(animation);
            // The stock at the chest (ChestStance): nothing is looked through, so no sight makes its picture (the game
            // would render its whole scene again for it every frame); the sight aimed through next is turned on again.
            // (Only while the stance is held, 1.9.36: by its weight, which eases out as the gun comes back up to the eye,
            // the sight stayed off all the way up and came on at the end, its lens black before the eye and then the
            // picture; the user's, 2026-11-27: "从抵肩瞄准回到瞄准时，能看到光学瞄具从变黑变回工作状态的切换，这里穿帮
            // 了". Let go, it is on again from the first frame, the gun still down at the chest.)
            if (aiming && ChestStance.Active)
            {
                ReleaseHeld();
                if (animation.ScopeAimTransforms != null)
                    foreach (ProceduralWeaponAnimation.SightNBone sat in animation.ScopeAimTransforms)
                    {
                        OpticSight? optic = sat.ScopePrefabCache != null ? sat.ScopePrefabCache.CurrentModOpticSight : null;
                        if (optic == null || !optic.enabled)
                            continue;
                        optic.enabled = false;
                        if (!_quieted.Contains(optic))
                            _quieted.Add(optic);
                    }
                return;
            }
            OpticSight? picture = null;
            if (aiming && scope != null)
            {
                OpticSight? sight = Resolve(animation);
                if (WantsPicture(scope, sight) && sight != null && sight.gameObject.activeInHierarchy)
                    picture = sight;
            }

            // 正在看普通放大镜时，画面留给它。否则打开枪上那块自己出画面的屏幕（MP-155 智能瞄具）。
            // 通过全息、红点瞄准时屏幕不开：开着的瞄具镜片登记在游戏那里，游戏就不把准星放到超分之后画，准星会拖影。
            bool throughCollimator = aiming && picture == null && scope?.ScopePrefabCache != null && scope.ScopePrefabCache.HasCollimators;
            OpticSight? screen = picture == null && !throughCollimator ? FindScreen(animation) : null;
            OpticSight? show = picture ?? screen;
            if (show == null)
            {
                ReleaseHeld();
                return;
            }

            QuietOthers(animation, show);
            Hold(show);
        }
        catch
        {
        }
    }

    // Handed back to the game: the sights this turned off on again, and the held one on only if the player still aims
    // through it (the game switches it as the aim changes from then on).
    internal static void Release(Player? player)
    {
        try
        {
            foreach (OpticSight sight in _quieted)
            {
                if (sight != null && !sight.enabled)
                    sight.enabled = true;
            }
            if (_held != null)
            {
                bool aimed = player?.HandsController != null && player.HandsController.IsAiming && player.ProceduralWeaponAnimation != null
                    && Same(Resolve(player.ProceduralWeaponAnimation), _held);
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = aimed,
                    OpticSight = _held
                };
            }
        }
        catch
        {
        }

        _quieted.Clear();
        _held = null;
        _frame = -1;
    }

    private static void ReleaseHeld()
    {
        if (_held == null)
            return;

        try
        {
            OpticSight.OpticSightStatus state = OpticSight.OpticSightState.Value;
            if (Same(state.OpticSight, _held))
            {
                OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
                {
                    IsEnabled = false,
                    OpticSight = _held
                };
            }
        }
        catch
        {
        }

        _held = null;
    }

    // 普通放大瞄具走 IsOptic。斜边热成像和带专用瞄准面的智能瞄具（MP-155 Ultima）不是这条，
    // 但身上仍有热成像或夜视画面，第三人称下也要打开。
    private static bool WantsPicture(ProceduralWeaponAnimation.SightNBone scope, OpticSight? sight)
    {
        if (scope.IsOptic)
            return true;

        if (sight != null && sight.IsThermalSightAvailableAt45Degrees)
            return true;

        if (scope.ScopePrefabCache != null && scope.ScopePrefabCache.CurrentModHasOptics)
            return true;

        if (scope.Mod != null && !string.IsNullOrEmpty(scope.Mod.CustomAimPlane) && sight != null)
            return true;

        return HasScopeFilter(sight);
    }

    private static bool HasScopeFilter(OpticSight? sight)
    {
        if (sight?.ScopeData == null)
            return false;

        try
        {
            object data = sight.ScopeData;
            if (Flag(data, "ThermalVisionData", "ThermalVision"))
                return true;

            return Flag(data, "NightVisionData", "NightVision");
        }
        catch
        {
            return false;
        }
    }

    private static bool Flag(object owner, string childName, string flagName)
    {
        object? child = owner.GetType().GetProperty(childName)?.GetValue(owner)
            ?? owner.GetType().GetField(childName)?.GetValue(owner);
        if (child == null)
            return false;

        object? flag = child.GetType().GetProperty(flagName)?.GetValue(child)
            ?? child.GetType().GetField(flagName)?.GetValue(child);
        return flag is bool on && on;
    }

    // 第三人称下游戏不会跑第一人称那套开关。智能瞄具的屏幕不占当前瞄准位，也要单独打开。
    // 斜边热成像（T12W 之类）也带专用瞄准面，但只在侧过枪通过它瞄准时才要画面（WantsPicture 管）：一直开着的话，
    // 游戏一直登记着它的镜片，就不再把全息、红点的准星放到超分之后画，准星回到 DLSS 混合的画面里拖影。
    private static OpticSight? FindScreen(ProceduralWeaponAnimation animation)
    {
        if (animation.ScopeAimTransforms == null)
            return null;

        OpticSight? screen = null;
        foreach (ProceduralWeaponAnimation.SightNBone sat in animation.ScopeAimTransforms)
        {
            ScopePrefabCache? cache = sat.ScopePrefabCache;
            if (cache == null || !cache.CurrentModHasOptics || cache.CurrentModOpticSight == null)
                continue;

            bool ownScreen = cache.CurrentModIgnoreOpticsForCameraPlane
                || (sat.Mod != null && !string.IsNullOrEmpty(sat.Mod.CustomAimPlane));
            if (!ownScreen || !cache.CurrentModOpticSight.gameObject.activeInHierarchy
                || cache.CurrentModOpticSight.IsThermalSightAvailableAt45Degrees)
                continue;

            screen = cache.CurrentModOpticSight;
        }

        return screen;
    }

    private static void QuietOthers(ProceduralWeaponAnimation animation, OpticSight keep)
    {
        if (animation.ScopeAimTransforms == null)
            return;

        foreach (ProceduralWeaponAnimation.SightNBone sat in animation.ScopeAimTransforms)
        {
            OpticSight? sight = sat.ScopePrefabCache != null ? sat.ScopePrefabCache.CurrentModOpticSight : null;
            if (sight == null || Same(sight, keep) || !sight.enabled)
                continue;

            sight.enabled = false;
            if (!_quieted.Contains(sight))
                _quieted.Add(sight);
        }
    }

    private static void Hold(OpticSight sight)
    {
        if (!sight.enabled)
            sight.enabled = true;
        sight.LensFade(false);

        OpticSight? current = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
        if (current != null && !Same(current, sight))
        {
            OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
            {
                IsEnabled = false,
                OpticSight = current
            };
        }

        OpticSight.OpticSightStatus state = OpticSight.OpticSightState.Value;
        if (!state.IsEnabled || !Same(state.OpticSight, sight))
        {
            OpticSight.OpticSightState.Value = new OpticSight.OpticSightStatus
            {
                IsEnabled = true,
                OpticSight = sight
            };
        }

        _held = sight;
    }

    internal static OpticSight? ResolveForElevation(ProceduralWeaponAnimation.SightNBone scope)
    {
        Transform? bone = scope.Bone;
        if (bone == null) return null;
        ScopePrefabCache? cache = bone.GetComponentInParent<ScopePrefabCache>(true);
        if (cache != null && cache.CurrentModHasOptics && cache.CurrentModOpticSight != null)
            return cache.CurrentModOpticSight;
        return bone.GetComponentInParent<OpticSight>(true) ?? bone.GetComponentInChildren<OpticSight>(true);
    }

    private static OpticSight? Resolve(ProceduralWeaponAnimation animation)
    {
        Transform? bone = Scopes.Current(animation)?.Bone;
        if (bone == null)
            return null;

        ScopePrefabCache? cache = bone.GetComponentInParent<ScopePrefabCache>(true);
        if (cache != null && cache.CurrentModHasOptics && cache.CurrentModOpticSight != null)
            return cache.CurrentModOpticSight;

        OpticSight? sight = bone.GetComponentInParent<OpticSight>(true);
        if (sight != null)
            return sight;

        return bone.GetComponentInChildren<OpticSight>(true);
    }

    private static bool Same(Object? left, Object? right)
    {
        if (left == null || right == null)
            return left == right;

        return left.GetInstanceID() == right.GetInstanceID();
    }
}
