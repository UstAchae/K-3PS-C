using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace EFTNewCamera;

// The hideout's player model dressed from the gear worn, as in a raid. The game dresses it from the shooting range's
// copy of the gear (HideoutPlayer's range inventory), filled with copies on entering the range and emptied on leaving,
// not from the gear the inventory screen shows and saves (OriginalInventory): outside the range it shows the player in
// the first person, no body seen. In this mod's third person the body is seen, so the model's slots watch the worn gear
// itself, as a raid's do the player's own, in the range and out of it: put on, taken off or changed in part (goggles on
// a helmet), the model follows at once; entering or leaving the range changes nothing on it; and its devices, and the
// settings kept for them, are the worn items' own. In the range the player holds a copy of a weapon worn: the weapon in
// its slot is then not shown on the body, as the game does with the one held. With this mod off, the game's copy again.
// Per frame only two references are compared.
internal static class HideoutGear
{
    private static readonly AccessTools.FieldRef<PlayerBody, InventoryEquipment> BodyEquipment = AccessTools.FieldRefAccess<PlayerBody, InventoryEquipment>("_equipment");

    private static HideoutPlayer? _bound;
    private static bool _installed;

    internal static void Install(Harmony harmony)
    {
        if (_installed)
            return;
        _installed = true;
        // The slot views' choice of what to show, the item worn or none while it is held: a compiler-made lambda, found
        // by what it takes and gives.
        int found = 0;
        foreach (System.Type nested in typeof(PlayerBody.SlotView).GetNestedTypes(AccessTools.all))
        {
            foreach (MethodInfo method in nested.GetMethods(AccessTools.all | BindingFlags.DeclaredOnly))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (method.ReturnType != typeof(Item) || parameters.Length != 2 || parameters[0].ParameterType != typeof(Item) || parameters[1].ParameterType != typeof(Item))
                    continue;
                harmony.Patch(method, postfix: new HarmonyMethod(AccessTools.Method(typeof(HideoutGear), nameof(HeldPostfix))));
                found++;
            }
        }
        if (found != 1)
            NewCameraPlugin.Log?.LogWarning($"Hideout model: found {found} of 1 held-weapon choices; in the shooting range a weapon held may also be seen worn");
    }

    // A copy of the worn item held (the range's): not shown worn either.
    private static void HeldPostfix(Item? __0, Item? __1, ref Item? __result)
    {
        if (__result == null || __1 == null || _bound == null || !ReferenceEquals(Singleton<GameWorld>.Instance?.MainPlayer, _bound))
            return;
        if (__1.TemplateId == __0!.TemplateId && __1.CurrentAddress?.Container is Slot held && __0.CurrentAddress?.Container is Slot worn && held.ID == worn.ID)
            __result = null;
    }

    internal static void Update(Player player)
    {
        if (player is HideoutPlayer hideout && Dress(hideout, hideout.OriginalInventory?.Inventory?.Equipment))
            _bound = hideout;
    }

    // This mod let go of the player: the model back on the range's copy, as the game has it.
    internal static void Release(Player? player)
    {
        if (_bound != null && ReferenceEquals(player, _bound))
            Dress(_bound, _bound.ShootingRangeInventory?.Inventory?.Equipment);
        _bound = null;
    }

    // The model's slots made to watch the gear given, as PlayerBody.Init makes them. True when it was done.
    private static bool Dress(HideoutPlayer hideout, InventoryEquipment? equipment)
    {
        PlayerBody? body = hideout.PlayerBody;
        if (body == null || equipment == null || ReferenceEquals(body.Equipment, equipment))
            return false;
        body.Equipment = equipment;
        BodyEquipment(body) = equipment;
        Slot backpack = equipment.GetSlot(EquipmentSlot.Backpack);
        foreach (EquipmentSlot name in PlayerBody.SlotNames)
        {
            PlayerBody.SlotView view = new PlayerBody.SlotView(body, equipment.GetSlot(name), body.GetSlotBone(name), name, backpack, body.GetAlternativeHolsterBone(name));
            body.SlotViews.AddOrReplace(name, view)?.Dispose();
        }
        HideoutNightVision.Refresh(hideout);
        return true;
    }
}
