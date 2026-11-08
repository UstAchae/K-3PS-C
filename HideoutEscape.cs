using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace EFTNewCamera;

// The hideout takes Escape (leave the shooting range, close an area's screen, the menu) only in the first-person view;
// in any other it swallows the key, meant for its overview's free camera. This mod keeps the player in the
// third-person view while walking about, so there Escape counts it as first person. The free camera stays as it was.
internal static class HideoutEscape
{
    private static readonly MethodInfo PointOfViewGetter =
        AccessTools.PropertyGetter(typeof(Player), nameof(Player.PointOfView));

    private static readonly MethodInfo ViewForEscape =
        AccessTools.Method(typeof(HideoutEscape), nameof(PointOfViewForEscape));

    internal static void Install()
    {
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".hideoutescape");
        MethodInfo? target = AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.TranslateExitScreenInput));
        if (target == null)
        {
            NewCameraPlugin.Log?.LogWarning("Hideout Escape: HideoutPlayerOwner.TranslateExitScreenInput not found");
            return;
        }
        harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(HideoutEscape), nameof(Transpiler))));
        HideoutNightVision.Install(harmony);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(PointOfViewGetter))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, ViewForEscape)
                    .MoveLabelsFrom(instruction)
                    .MoveBlocksFrom(instruction);
                continue;
            }
            yield return instruction;
        }
        if (replaced == 0)
            NewCameraPlugin.Log?.LogWarning("Hideout Escape: no point-of-view read in TranslateExitScreenInput");
    }

    public static EPointOfView PointOfViewForEscape(Player player)
    {
        if (HideoutAreaView.Holding)
            return EPointOfView.FreeCamera;
        EPointOfView view = player.PointOfView;
        return view == EPointOfView.ThirdPerson && NewCameraSwitch.Enabled && player.IsYourPlayer ? EPointOfView.FirstPerson : view;
    }
}
