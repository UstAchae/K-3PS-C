using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace EFTNewCamera;

// Night vision in the hideout as in a raid, while this mod is on. The hideout has a filter of its own for its overview
// camera (HideoutNightVisionTemplate) and puts it in place of the goggles: whenever the player is not in the first-person
// view (this mod keeps the third), and whenever its slot observers find no goggles, which is always outside the shooting
// range, for they watch the range's inventory, empty until the range is entered, not the gear the player wears and is
// seen in (HideoutPlayer.OriginalInventory). Here the observers give the goggles (or thermal) on the model, which wears
// the worn gear in the range and out of it (HideoutGear); the hideout's filter is never given, never kept in step with the goggles,
// and its button in the hideout's screen is not shown. N switches and flips the goggles worn, as in a raid.
internal static class HideoutNightVision
{
    private static bool _installed;

    internal static void Install(Harmony harmony)
    {
        if (_installed)
            return;
        _installed = true;

        int observers = 0;
        HarmonyMethod nightVision = new HarmonyMethod(AccessTools.Method(typeof(HideoutNightVision), nameof(NightVisionPrefix)));
        HarmonyMethod thermal = new HarmonyMethod(AccessTools.Method(typeof(HideoutNightVision), nameof(ThermalPrefix)));
        // The observers' choices: compiler-made lambdas, found by what they take and give, not by their names.
        foreach (System.Type nested in typeof(HideoutPlayer).GetNestedTypes(AccessTools.all))
        {
            foreach (MethodInfo method in nested.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || parameters[0].ParameterType != method.ReturnType)
                    continue;
                if (method.ReturnType == typeof(NightVisionComponent))
                    harmony.Patch(method, prefix: nightVision);
                else if (method.ReturnType == typeof(ThermalVisionComponent))
                    harmony.Patch(method, prefix: thermal);
                else
                    continue;
                observers++;
            }
        }

        // Keeping the hideout's filter in step with the goggles (method_167 on the night-vision observer's change,
        // method_168 on the thermal's), told apart by what they touch.
        int kept = 0;
        foreach (string name in new[] { "method_167", "method_168" })
        {
            MethodInfo? method = AccessTools.Method(typeof(HideoutPlayer), name);
            if (method == null || method.GetParameters().Length != 0)
                continue;
            harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(HideoutNightVision), nameof(KeepInStepPrefix))));
            kept++;
        }

        MethodInfo? button = AccessTools.Method(typeof(EFT.Hideout.HideoutScreenOverlay), nameof(EFT.Hideout.HideoutScreenOverlay.EnableNightVisionButton));
        if (button != null)
            harmony.Patch(button, prefix: new HarmonyMethod(AccessTools.Method(typeof(HideoutNightVision), nameof(ButtonPrefix))));

        HideoutGear.Install(harmony);

        if (observers < 2 || kept < 2 || button == null)
            NewCameraPlugin.Log?.LogWarning($"Hideout night vision: found {observers} of 2 observer choices, {kept} of 2 filter keepers, button {(button != null)}; what was not found stays as the game has it");
    }

    private static HideoutPlayer? Hideout()
    {
        return NewCameraSwitch.Enabled ? Singleton<GameWorld>.Instance?.MainPlayer as HideoutPlayer : null;
    }

    // The goggles (or thermal) on the headwear the model wears, as the game finds what N flips in a raid
    // (Player.ToggleGoggles: the headwear's items): their models are bound to these, and flip and light with them. Not
    // looked for on the models (a T-7 worn in the hideout flipped nothing on N when they were).
    // Asked only when the observers look again (gear changed), not each frame.
    private static T? Worn<T>(Player player) where T : class, IItemComponent
    {
        Item? headwear = EquipmentIds.Worn(player)?.GetSlot(EquipmentSlot.Headwear).ContainedItem;
        if (headwear is not CompoundItem compound)
            return null;
        foreach (T component in compound.GetItemComponentsInChildren<T>())
            return component;
        return null;
    }

    private static bool NightVisionPrefix(NightVisionComponent? __0, ref NightVisionComponent? __result)
    {
        HideoutPlayer? hideout = Hideout();
        if (hideout == null)
            return true;
        // The model's goggles; none (no hideout filter) when it wears none, or wears thermal ones.
        __result = Worn<NightVisionComponent>(hideout);
        return false;
    }

    private static bool ThermalPrefix(ThermalVisionComponent? __0, ref ThermalVisionComponent? __result)
    {
        HideoutPlayer? hideout = Hideout();
        if (hideout == null)
            return true;
        __result = Worn<ThermalVisionComponent>(hideout);
        return false;
    }

    private static bool KeepInStepPrefix()
    {
        return Hideout() == null;
    }

    private static bool ButtonPrefix(EFT.Hideout.HideoutScreenOverlay __instance)
    {
        if (!NewCameraSwitch.Enabled)
            return true;
        __instance._nightVisionButton?.Close();
        return false;
    }

    // The worn goggles found anew (put on, taken off, swapped): the observers asked again, as the game does not when
    // only the worn gear changes.
    internal static void Refresh(Player player)
    {
        if (player is not HideoutPlayer hideout || !NewCameraSwitch.Enabled)
            return;
        hideout.ThermalVisionObserver?.Update();
        hideout.NightVisionObserver?.Update();
    }
}
