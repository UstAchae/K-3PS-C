using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;

namespace EFTNewCamera;

// What the game shows the player only in the first-person view, which this mod keeps the player out of: these places
// read the view as first person while the mod is on. The fire mode's text, the compass's panel (shown and hidden), and
// the portable rangefinder's sight and reading, the AGS-17's sight. (The magazine check: MagazineCount.)
internal static class FirstPersonGates
{
    private static readonly MethodInfo FirstPersonGetter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.FirstPersonPointOfView));
    private static readonly MethodInfo ViewGetter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.PointOfView));

    internal static void Install()
    {
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".gates");
        HarmonyMethod transpiler = new HarmonyMethod(AccessTools.Method(typeof(FirstPersonGates), nameof(Transpiler)));
        Patch(harmony, transpiler, "fire mode", AccessTools.Method(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.ShowFireMode)));
        Patch(harmony, transpiler, "compass shown", AccessTools.Method(typeof(Player), nameof(Player.SetPropVisibility)));
        Patch(harmony, transpiler, "compass hidden", AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.CompassStateChanged)));
        Patch(harmony, transpiler, "AGS-17 sight", AgsRotation());
        Patch(harmony, transpiler, "rangefinder", AccessTools.Method(typeof(PortableRangeFinderController.PortableRangeFinderIdling), nameof(PortableRangeFinderController.PortableRangeFinderIdling.SetAiming)));
        Patch(harmony, transpiler, "rangefinder, dropping the backpack", AccessTools.Method(typeof(PortableRangeFinderController.PortableRangeFinderDropBackpackOperation), nameof(PortableRangeFinderController.PortableRangeFinderDropBackpackOperation.SetAiming)));
    }

    // The stationary weapon's rotation function: a delegate in a compiler's class, found by what it calls.
    private static MethodInfo? AgsRotation()
    {
        MethodInfo adjust = AccessTools.Method(typeof(EFT.Animations.ProceduralWeaponAnimation), nameof(EFT.Animations.ProceduralWeaponAnimation.AdjustOpticsPAG17));
        foreach (System.Type nested in typeof(MovementContext).GetNestedTypes(AccessTools.all))
            foreach (MethodInfo method in nested.GetMethods(AccessTools.all))
            {
                if (method.IsAbstract || method.DeclaringType != nested || method.IsGenericMethod)
                    continue;
                foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(method))
                    if (instruction.Calls(adjust))
                        return method;
            }
        return null;
    }

    private static void Patch(Harmony harmony, HarmonyMethod transpiler, string what, MethodInfo? target)
    {
        if (target == null)
        {
            NewCameraPlugin.Log?.LogWarning($"Not patched (first-person gate, {what}): method not found.");
            return;
        }
        harmony.Patch(target, transpiler: transpiler);
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        MethodInfo firstPerson = AccessTools.Method(typeof(FirstPersonAudio), nameof(FirstPersonAudio.IsEffectiveFirstPerson));
        MethodInfo view = AccessTools.Method(typeof(FirstPersonAudio), nameof(FirstPersonAudio.GetEffectivePointOfView));
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            MethodInfo? ours = instruction.Calls(FirstPersonGetter) ? firstPerson : instruction.Calls(ViewGetter) ? view : null;
            if (ours == null)
            {
                yield return instruction;
                continue;
            }
            replaced++;
            yield return new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
        }
        if (replaced == 0)
            NewCameraPlugin.Log?.LogWarning($"First-person gate: no point-of-view read in {original.DeclaringType?.Name}.{original.Name}.");
    }
}
