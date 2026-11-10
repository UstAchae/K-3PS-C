using EFT.Animations;
using UnityEngine;

namespace EFTNewCamera;

internal static class Scopes
{
    // The sight aimed through, or null: the game's CurrentScope indexes its list of sights, which is empty with nothing
    // in hand (and it throws then).
    // Only a sight on the gun in hand. With a sight changed in a raid the game's list was found still holding the one taken off, far from
    // the gun: placed by it, the gun was pulled metres away and round. Then the game is asked to find the gun's sights again (once a
    // second at most), as it does when a gun is taken up; while none on the gun is found, there is none.
    private const float RefindSeconds = 1f;
    private static ProceduralWeaponAnimation? _refoundFor;
    private static float _refoundAt = -10f;

    internal static ProceduralWeaponAnimation.SightNBone? Current(ProceduralWeaponAnimation? animation)
    {
        ProceduralWeaponAnimation.SightNBone? scope = Listed(animation);
        if (scope == null || OnTheGun(animation!, scope))
            return scope;
        if (!ReferenceEquals(_refoundFor, animation) || Time.unscaledTime - _refoundAt >= RefindSeconds)
        {
            _refoundFor = animation;
            _refoundAt = Time.unscaledTime;
            try
            {
                animation!.FindAimTransforms();
                // Temporary: that this happens.
                NewCameraPlugin.Log?.LogInfo($"Sights: {scope.Bone?.name} is not on the gun in hand; the game found the gun's sights again: {animation.ScopeAimTransforms.Count}");
            }
            catch (System.Exception e)
            {
                NewCameraPlugin.Log?.LogWarning("Sights not found again: " + e.Message);
            }
            scope = Listed(animation);
            if (scope != null && OnTheGun(animation!, scope))
                return scope;
        }
        return null;
    }

    private static ProceduralWeaponAnimation.SightNBone? Listed(ProceduralWeaponAnimation? animation)
    {
        if (animation?.ScopeAimTransforms == null || animation.ScopeAimTransforms.Count == 0)
            return null;
        try
        {
            return animation.CurrentScope;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool OnTheGun(ProceduralWeaponAnimation animation, ProceduralWeaponAnimation.SightNBone scope)
    {
        Transform? root = animation.HandsContainer?.WeaponRootAnim;
        return root == null || scope.Bone != null && scope.Bone.gameObject.activeInHierarchy && scope.Bone.IsChildOf(root);
    }
}
