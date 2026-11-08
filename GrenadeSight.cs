using EFT;
using EFT.Interactive;

namespace EFTNewCamera;

// The AGS-17 grenade launcher in hand. Its sight's picture is the game's own (ProceduralWeaponAnimation.AdjustOpticsPAG17,
// let through by FirstPersonGates); the sight body stays level and only yaws (AimArms.SightCamera).
internal static class GrenadeSight
{
    internal static bool InHands(Player player)
    {
        return StationaryHeld(player, out StationaryWeapon? weapon)
            && weapon!.Animation == StationaryWeapon.EStationaryAnimationType.AGS_17;
    }

    private static bool StationaryHeld(Player player, out StationaryWeapon? weapon)
    {
        weapon = null;
        if (player.HandsController == null || player.MovementContext == null || !player.MovementContext.IsStationaryWeaponInHands)
            return false;
        weapon = player.MovementContext.StationaryWeapon;
        return weapon != null;
    }
}
