using EFT;
using EFT.Animations;

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

    // As the game's list of sights is made again (the gun taken up, or a part of it changed in a raid): the eye's place along the gun is
    // worked out again from it, as the game does at that point only in first person (its method_11). This mod's view is third person,
    // and the place it had worked out for the last sights stayed: a magnified sight taken off left its far plane, the eye a third of a
    // metre back from the iron sight.
    internal static void DepthAsFirstPerson(ProceduralWeaponAnimation __instance)
    {
        if (__instance == null || __instance.FirstPersonPointOfView || !NewCameraSwitch.Enabled
            || !ReferenceEquals(Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer?.ProceduralWeaponAnimation, __instance)
            || __instance.ScopeAimTransforms == null || __instance.ScopeAimTransforms.Count == 0)
            return;
        try
        {
            __instance.UpdateFarPlane();
            __instance.FindCameraDepth();
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("The eye's place along the gun not worked out again: " + e.Message);
        }
    }
}
