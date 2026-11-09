using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using Diz.LanguageExtensions;
using EFT;
using EFT.InventoryLogic;
using EFT.InventoryLogic.Operations;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using JsonType;
using UnityEngine;

namespace EFTNewCamera;

// The adapter's parts and bones, under its root, as the prefab has them; set as they are or over to the other shoe.
internal sealed class DovetailAdapterPose : MonoBehaviour
{
    private Transform[] _parts = System.Array.Empty<Transform>();
    private Vector3[] _positions = System.Array.Empty<Vector3>();
    private Quaternion[] _rotations = System.Array.Empty<Quaternion>();

    internal void Capture(Transform root)
    {
        _parts = new Transform[root.childCount];
        _positions = new Vector3[_parts.Length];
        _rotations = new Quaternion[_parts.Length];
        for (int i = 0; i < _parts.Length; i++)
        {
            _parts[i] = root.GetChild(i);
            _positions[i] = _parts[i].localPosition;
            _rotations[i] = _parts[i].localRotation;
        }
    }

    internal void Set(bool turned, Quaternion turn, Vector3 shift)
    {
        for (int i = 0; i < _parts.Length; i++)
        {
            if (_parts[i] != null)
                _parts[i].SetLocalPositionAndRotation(turned ? shift + turn * _positions[i] : _positions[i], turned ? turn * _rotations[i] : _rotations[i]);
        }
    }
}

// The PVS-14's Norotos dual dovetail adapter has a shoe on two of its sides, and whichever goes into the mount decides
// the eye. The game gives the adapter one slot; here it has one for each shoe, of which only one can be taken: the game's
// own (the model's shoe, the right eye) and a second (the other shoe, the left eye). The second exists only on this client:
// the server keeps the slot's name and does not look at it. It needs a bone, added to the adapter beside the first. A device in
// it turns the adapter, on whatever model it is (the worn one, a preview, an icon, a portrait: all made by the game's slot
// views), over to the other shoe: about the rail by AdapterTurn, moved by AdapterShift, from where the game has it.
// With the mod's master switch off the second slot is not shown and takes nothing, and a device in it is moved to the first.
internal static class DovetailSlots
{
    private const string AdapterId = "5a16b93dfcdbcbcae6687261";
    private const string FirstSlot = "mod_nvg";
    private const string SecondSlot = "mod_nvg_left";
    private const float AdapterTurn = -108.9f;
    private static readonly Vector3 AdapterShift = new Vector3(0.0266f, -0.0401f, 0f);
    private static readonly AccessTools.FieldRef<TarkovApplication, MainMenuShowOperation> MenuOperation =
        AccessTools.FieldRefAccess<TarkovApplication, MainMenuShowOperation>("_menuOperation");
    private static bool _devicesMoved;

    internal static void Install()
    {
        // Whatever goes wrong here stays here: the rest of the mod still loads.
        try
        {
            var harmony = new Harmony("justachae.thirdpersoncamera.dovetailslots");
            Patch(harmony, AccessTools.Constructor(typeof(ItemFactory), new[] { typeof(ItemTemplates) }), nameof(TemplatesPrefix), null);
            Patch(harmony, AccessTools.Method(typeof(EFT.ObjectsFactory), nameof(EFT.ObjectsFactory.AttachMods)), nameof(AttachModsPrefix), null);
            Patch(harmony, AccessTools.Method(typeof(ContainerCollectionView.SlotView), nameof(ContainerCollectionView.SlotView.InsertItem)), null, nameof(InsertItemPostfix));
            Patch(harmony, AccessTools.Method(typeof(ContainerCollectionView.SlotView), nameof(ContainerCollectionView.SlotView.RemoveItem)), nameof(RemoveItemPrefix), null);
            Patch(harmony, AccessTools.Method(typeof(Slot), nameof(Slot.CheckConditions)), null, nameof(CheckConditionsPostfix));
            Patch(harmony, AccessTools.Method(typeof(ModSlotView), nameof(ModSlotView.Show),
                new[] { typeof(Slot), typeof(ItemContext), typeof(ItemController), typeof(ItemUiContext) }), null, nameof(ShowPostfix));
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogError("Dual dovetail's second slot not set up: " + e);
        }
    }

    private static void Patch(Harmony harmony, MethodBase? target, string? prefix, string? postfix)
    {
        if (target == null)
        {
            NewCameraPlugin.Log?.LogWarning("Dual dovetail's second slot: a game method not found (" + (prefix ?? postfix) + ")");
            return;
        }
        harmony.Patch(target,
            prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(DovetailSlots), prefix)) : null,
            postfix != null ? new HarmonyMethod(AccessTools.Method(typeof(DovetailSlots), postfix)) : null);
    }

    private static bool IsAdapter(Item? item) => item != null && item.TemplateId.ToString() == AdapterId;

    private static Slot? SlotOf(CompoundItem adapter, string id)
    {
        foreach (Slot slot in adapter.Slots)
        {
            if (slot.ID == id)
                return slot;
        }
        return null;
    }

    // The second slot on the adapter's template, before anything is made from it: a profile may have a device in it.
    private static void TemplatesPrefix(ItemTemplates itemTemplates)
    {
        foreach (KeyValuePair<MongoID, ItemTemplate> pair in itemTemplates)
        {
            if (pair.Key.ToString() != AdapterId || pair.Value is not CompoundItemTemplate template)
                continue;
            var slots = new List<Slot>(template.Slots);
            Slot? first = slots.Find(s => s.ID == FirstSlot);
            if (first == null || slots.Exists(s => s.ID == SecondSlot))
                return;
            slots.Add(new Slot(SecondSlot, first.Filters, false, first.MergeContainerWithChildren));
            template.Slots = slots.ToArray();
            NewCameraPlugin.Log?.LogInfo("Dual dovetail: second slot added");
            return;
        }
    }

    // The bone the game looks for by the slot's name, where the first slot's is (the adapter is what turns).
    private static void AttachModsPrefix(ContainerCollection containerCollection, ContainerCollectionView collectionView)
    {
        if (containerCollection is not Item item || !IsAdapter(item) || collectionView?.GameObject == null)
            return;
        Transform root = collectionView.GameObject.transform;
        // A model from the pool starts as the prefab has it, whatever it was last.
        if (root.Find(SecondSlot) != null)
        {
            SetAdapter(root, false);
            return;
        }
        Transform? first = root.Find(FirstSlot);
        if (first == null)
            return;
        var bone = new GameObject(SecondSlot).transform;
        bone.SetParent(root, false);
        bone.SetLocalPositionAndRotation(first.localPosition, first.localRotation);
        bone.gameObject.layer = first.gameObject.layer;
        root.gameObject.AddComponent<DovetailAdapterPose>().Capture(root);
    }

    // What is under the adapter's root is turned, not the root: the game places the root, on a mount or (the adapter alone,
    // as in its item view) by the viewer, and the other shoe should show in both.
    private static void SetAdapter(Transform root, bool secondShoe)
    {
        DovetailAdapterPose? pose = root.GetComponent<DovetailAdapterPose>();
        if (pose != null)
            pose.Set(secondShoe && NewCameraSwitch.Enabled, Quaternion.AngleAxis(AdapterTurn, Vector3.forward), AdapterShift);
    }

    // A device going into the adapter: which slot it is decides the shoe.
    private static void InsertItemPostfix(ContainerCollectionView.SlotView __instance, Item item)
    {
        Transform? bone = __instance.Bone;
        if (bone == null || bone.parent == null || (bone.name != SecondSlot && bone.name != FirstSlot) || !IsAdapter(item.Parent?.Container?.ParentItem))
            return;
        SetAdapter(bone.parent, bone.name == SecondSlot);
    }

    private static void RemoveItemPrefix(ContainerCollectionView.SlotView __instance)
    {
        Transform? bone = __instance.Bone;
        if (bone != null && bone.parent != null && bone.name == SecondSlot)
            SetAdapter(bone.parent, false);
    }

    // One device at a time; moving it from one slot to the other is fine. The second takes nothing with the mod off.
    private static void CheckConditionsPostfix(Slot __instance, Item item, bool ignoreRestrictions, ref Diz.LanguageExtensions.Option<bool> __result)
    {
        if (ignoreRestrictions || __result.Failed || (__instance.ID != FirstSlot && __instance.ID != SecondSlot)
            || !IsAdapter(__instance.ParentItem) || __instance.ParentItem is not CompoundItem adapter)
            return;
        if (__instance.ID == SecondSlot && !NewCameraSwitch.Enabled)
        {
            __result = new Slot.ItemFiltersWontAllowError(item, __instance);
            return;
        }
        Slot? other = SlotOf(adapter, __instance.ID == FirstSlot ? SecondSlot : FirstSlot);
        if (other?.ContainedItem != null && other.ContainedItem != item)
            __result = new Slot.SlotNotEmptyError(item, other);
    }

    // The two slots named for the eye they put the device before; the second not shown with the mod off.
    private static void ShowPostfix(ModSlotView __instance, Slot slot)
    {
        if ((slot.ID != FirstSlot && slot.ID != SecondSlot) || !IsAdapter(slot.ParentItem))
            return;
        if (!NewCameraSwitch.Enabled)
        {
            if (slot.ID == SecondSlot)
                __instance.gameObject.SetActive(false);
            return;
        }
        bool chinese = LocalizationManager.Instance.Culture.StartsWith("ch", System.StringComparison.OrdinalIgnoreCase)
            || LocalizationManager.Instance.Culture.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);
        __instance._slotName.text = slot.ID == FirstSlot ? (chinese ? "右眼" : "RIGHT EYE") : (chinese ? "左眼" : "LEFT EYE");
    }

    // With the mod off, the player's devices in second slots go to the first, as the game knows them. Once the player's
    // inventory is there, each time the switch goes off.
    internal static void Update()
    {
        if (NewCameraSwitch.Enabled)
        {
            _devicesMoved = false;
            return;
        }
        if (_devicesMoved)
            return;
        InventoryController? controller = Singleton<GameWorld>.Instance?.MainPlayer?.InventoryController;
        if (controller == null && TarkovApplication.Exist(out TarkovApplication application))
            controller = MenuOperation(application)?.InventoryController;
        if (controller == null)
            return;
        _devicesMoved = true;
        MoveDevices(controller, controller.Inventory.Equipment);
        if (controller.Inventory.Stash != null)
            MoveDevices(controller, controller.Inventory.Stash);
    }

    private static void MoveDevices(InventoryController controller, CompoundItem top)
    {
        var moving = new List<Item>();
        foreach (Item item in top.GetAllItems())
        {
            if (item.Parent is SlotItemAddress address && address.Slot.ID == SecondSlot && IsAdapter(address.Slot.ParentItem))
                moving.Add(item);
        }
        foreach (Item item in moving)
        {
            if (item.Parent is not SlotItemAddress address || address.Slot.ParentItem is not CompoundItem adapter)
                continue;
            Slot? first = SlotOf(adapter, FirstSlot);
            if (first == null || first.ContainedItem != null)
                continue;
            OperationResult<MoveResult> moved = ItemManipulator.Move(item, first.CreateItemAddress(), controller);
            if (moved.Succeeded)
                controller.RunNetworkTransaction(moved.Value);
            else
                NewCameraPlugin.Log?.LogWarning("Dual dovetail: a device not moved back to the first slot: " + moved.Error);
        }
    }
}
