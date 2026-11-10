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
}
