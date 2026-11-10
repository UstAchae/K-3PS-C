using EFT;
using EFT.Animations;
using UnityEngine;

namespace EFTNewCamera;

internal static class Scopes
{
    // The sight aimed through, or null: the game's CurrentScope indexes its list of sights, which is empty with nothing
    // in hand (and it throws then).
    internal static ProceduralWeaponAnimation.SightNBone? Current(ProceduralWeaponAnimation? animation)
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

    // A part of the gun in hand changed in a raid: the game makes its list of sights only as the gun is taken up, so it went on holding
    // the sight taken off, and the eye was put behind that one. Found again the next frame (the gun's parts as they are by then), as
    // the game finds them taking the gun up.
    private static ProceduralWeaponAnimation? _changed;
    private static int _changedFrame = -10;

    internal static void WeaponModifiedPostfix(Player.FirearmController __instance)
    {
        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || !ReferenceEquals(player.HandsController, __instance) || player.ProceduralWeaponAnimation == null)
            return;
        _changed = player.ProceduralWeaponAnimation;
        _changedFrame = Time.frameCount;
    }

    internal static void FindAgainIfChanged(Player player)
    {
        if (_changed == null || Time.frameCount <= _changedFrame)
            return;
        ProceduralWeaponAnimation animation = _changed;
        _changed = null;
        if (!ReferenceEquals(player.ProceduralWeaponAnimation, animation))
            return;
        try
        {
            animation.FindAimTransforms();
            // Temporary: that this happens.
            NewCameraPlugin.Log?.LogInfo($"Sights found again after the gun was changed: {animation.ScopeAimTransforms.Count}");
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("Sights not found again after the gun was changed: " + e.Message);
        }
    }
}
