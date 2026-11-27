using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace EFTNewCamera;

// A goggle's eyepieces swung up sideways on their pods, as a state of the goggle (the user's, 2026-11-27: "把夜视仪的
// 水平翻上也做成一种状态，和翻上翻下一样，能被加载页面的角色，预览图，总览等地方画出来，并且会被记住，继承到下一次
// 刷新"; here and not in the ENVG-B's own mod, "因为我将来还要把 PVS-31A 也做成可以水平翻上").
// The game has no such state, so it is this mod's: which pods of a goggle are up, kept by the goggle's item id in a
// file beside the mounts' (as the mount's adjustment is), and laid on every model of that goggle:
//   the worn one, eased over PodTravel as its key is pressed (F12 目镜水平翻上：左 / 右);
//   the player's other models (the loading screen's, the inventory's, the overview's), as they are made and
//   whenever the state changes: at once, with no easing.
// A model going back to the game's pool has its pods turned down again first: the next wearer of that model is
// someone else.
// The rest of the mod reads the pods where they stand (PodAngle), as before.
// A goggle whose own mod turns its pods (the ENVG-B's client) is left to it until that mod hands them over
// (Interop.EyepiecePods.HandOver): two turning the same node every frame would fight.
internal static partial class NightVisionEyepieces
{
    internal static KeyboardShortcut PodLeftKey = new KeyboardShortcut(KeyCode.LeftArrow, KeyCode.LeftAlt);
    internal static KeyboardShortcut PodRightKey = new KeyboardShortcut(KeyCode.RightArrow, KeyCode.LeftAlt);
    private const float PodTravel = 0.4f;   // seconds for a pod to swing all the way

    private static readonly string PodsPath = Path.Combine(BepInEx.Paths.ConfigPath, "justachae.thirdpersoncamera.pods.txt");
    private static Dictionary<string, int>? _podStates;
    private static int _podsVersion;
    private static readonly object PodsWriteLock = new object();
    private static readonly HashSet<string> _podsHandedOver = new HashSet<string>();

    internal static void PodsHandedOver(string deviceNode)
    {
        if (_podsHandedOver.Add(deviceNode))
            NewCameraPlugin.Log?.LogInfo($"NVG pods: {deviceNode}'s own mod hands its pods over; they are turned, kept and shown on every model here from now on.");
    }

    // Whether this mod turns this kind of goggle's pods.
    // (Not with this mod switched off: the goggle's own mod, which reads this every frame, then turns them again itself.)
    private static bool PodsTurned(Device kind) => NewCameraSwitch.Enabled && kind.Pods.Length > 0 && kind.PodSwing > 0f && (!kind.PodsOwnMod || _podsHandedOver.Contains(kind.Node));
    internal static bool PodsTurned(string deviceNode)
    {
        foreach (Device kind in Devices)
        {
            if (kind.Node == deviceNode)
                return PodsTurned(kind);
        }
        return false;
    }

    private static int PodState(string? id)
    {
        ReadPodStates();
        if (id == null)
            return 0;
        if (_podStates!.TryGetValue(id, out int bits))
            return bits;
        return _podCopies.TryGetValue(id, out string? of) && _podStates.TryGetValue(of, out bits) ? bits : 0;
    }

    // The game draws an item's icon, and dresses the body of the loading screen and of the overview, from a COPY of the
    // item, made for the drawing with ids of its own (ItemExtensions.CloneVisibleItem): by its id such a copy's goggle
    // had no state, and its pods were down (the user's, 2026-11-27, pods up on the head: "贴图还是不对…加载页面还是不
    // 对"; the log of that session: the copies' models laid with state 0). So each copy's goggle is told which goggle
    // it is a copy of, as the copy is made: by the item's kind, the first copy of a kind for the first of that kind.
    private static readonly Dictionary<string, string> _podCopies = new Dictionary<string, string>();
    private static bool _copyNoted;
    // The copying itself is a generic method, which could not be patched (1.9.51: "IL Compile Error", none of the two
    // ways taken; the icon stayed as it was, the user's "图标还是不对，这里你都改了很多遍了"). Each item of a copy is
    // made by two plain steps of the item factory, one right after the other: what differs of the original is taken
    // (GetDiff) and an item made with it under a new id (CreateItem). The first is remembered, and the second, given
    // that very thing, tells which item the new one is a copy of.
    private static object? _diffTaken;
    private static Item? _diffOf;

    private static void ItemDiffPostfix(Item item, object __result)
    {
        _diffTaken = __result;
        _diffOf = item;
    }

    private static void ItemMadePostfix(string itemId, object itemDiff, Item __result)
    {
        try
        {
            Item? from = _diffOf;
            bool copied = itemDiff != null && ReferenceEquals(itemDiff, _diffTaken);
            _diffTaken = null;
            _diffOf = null;
            if (!copied || from == null || __result == null || _podStates == null || _podStates.Count == 0)
                return;
            string of = from.Id.ToString();
            if (of == itemId)
                return;
            if (_podCopies.TryGetValue(of, out string? first))
                of = first;
            else if (!_podStates.ContainsKey(of))
                return;
            if (_podCopies.Count > 4096)
                _podCopies.Clear();
            _podCopies[itemId] = of;
            if (!_copyNoted)
            {
                _copyNoted = true;
                NewCameraPlugin.Log?.LogInfo($"NVG pods: a copy of a goggle ({itemId}) takes the pods' state of the one it is a copy of ({of}: {PodState(of)}).");
            }
        }
        catch (System.Exception)
        {
        }
    }

    private static void ReadPodStates()
    {
        if (_podStates != null)
            return;
        _podStates = new Dictionary<string, int>();
        try
        {
            if (File.Exists(PodsPath))
            {
                foreach (string line in File.ReadAllLines(PodsPath))
                {
                    string[] f = line.Split(' ');
                    if (f.Length == 2 && int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int bits))
                        _podStates[f[0]] = bits;
                }
            }
        }
        catch (IOException e)
        {
            NewCameraPlugin.Log?.LogWarning("Pod states not read: " + e.Message);
        }
    }

    private static void SavePodState(string id, int bits)
    {
        ReadPodStates();
        if (bits == 0)
            _podStates!.Remove(id);
        else
            _podStates![id] = bits;
        _podsVersion++;
        var lines = new List<string>();
        foreach (KeyValuePair<string, int> e in _podStates)
            lines.Add(e.Key + " " + e.Value.ToString(CultureInfo.InvariantCulture));
        // Written off the main thread, one write at a time, in order (as the mounts' are).
        string path = PodsPath;
        System.Threading.Tasks.Task.Run(() =>
        {
            lock (PodsWriteLock)
            {
                try
                {
                    File.WriteAllLines(path, lines);
                }
                catch (IOException e)
                {
                    NewCameraPlugin.Log?.LogWarning("Pod states not saved: " + e.Message);
                }
            }
        });
    }

    // A pod's turn, share 0 down to 1 up.
    private static Quaternion PodTurn(Device kind, int pod, float share)
    {
        Vector3 axis = pod < kind.PodAxes.Length ? kind.PodAxes[pod] : Vector3.up;
        return Quaternion.AngleAxis(Mathf.SmoothStep(0f, 1f, share) * kind.PodSwing, axis);
    }

    // A model's pod nodes, the device's own and its eyecups', by name, in the kind's order (null where it has none).
    private sealed class PodNodes
    {
        internal Device Kind = Devices[0];
        internal Transform Root = null!;
        internal string? Item;
        // Every node of each pod, the device's own and its eyecups', whatever model under Root they are in: a goggle's
        // model can hold more than one by a name (its eyecups' place and the eyecups hung there), and all of them turn.
        // (1.9.49 kept one a name: the other stayed where it was, a pod up and its eyecup not; the user's, 2026-11-27,
        // "加载页面也没对齐".)
        internal readonly List<Transform>[] Nodes = { new List<Transform>(), new List<Transform>() };
        internal int Count => Nodes[0].Count + Nodes[1].Count;
        // Frames after which the nodes are looked for once more (a model's eyecups are a model of their own, hung on it
        // a little after it is made): a few times after it is made, then no more.
        internal int LookAgain, MadeFrame;

        internal void Find()
        {
            Nodes[0].Clear();
            Nodes[1].Clear();
            foreach (Transform node in Root.GetComponentsInChildren<Transform>(true))
            {
                int pod = System.Array.IndexOf(Kind.Pods, node.name);
                if (pod < 0)
                    pod = System.Array.IndexOf(Kind.CupPods, node.name);
                if (pod >= 0 && pod < 2)
                    Nodes[pod].Add(node);
            }
        }

        // A node kept has been destroyed (its model made anew).
        internal bool Lost()
        {
            foreach (List<Transform> side in Nodes)
            {
                foreach (Transform node in side)
                {
                    if (node == null)
                        return true;
                }
            }
            return false;
        }

        internal void Turn(float left, float right)
        {
            for (int pod = 0; pod < 2; pod++)
            {
                Quaternion turn = PodTurn(Kind, pod, pod == 0 ? left : right);
                foreach (Transform node in Nodes[pod])
                {
                    if (node != null)
                        node.localRotation = turn;
                }
            }
        }
    }

    // ---------------------------------------------------------------- the worn goggle

    private static PodNodes? _wornPods;
    private static readonly float[] _podUp = new float[2];
    private static bool _wornPodsStale;
    private static int _wornPodsVersion = -1;

    // The worn device's pods as they stand, for the mod that handed them over (0 down, 1 up).
    internal static float WornPodUp(int pod) => _wornPods != null && pod >= 0 && pod < 2 ? _podUp[pod] : 0f;
    internal static bool WornPodWanted(int pod) => _wornPods != null && (PodState(_wornPods.Item) & (1 << pod)) != 0;

    // The headwear's model made anew on the player (an eyecup fitted or taken off, another helmet): the nodes looked
    // for again. (Told by the game making the model, NightVisionPreviewMounts.SlotModelPostfix: nothing is searched for
    // on a timer.)
    internal static void WornPodsStale() => _wornPodsStale = true;

    // The worn goggle's item id as text, made once for an item (not each frame).
    private static NightVisionComponent? _podItemFrom;
    private static string? _podItemText;
    private static string? PodItemOf(NightVisionComponent? goggle)
    {
        if (!ReferenceEquals(goggle, _podItemFrom))
        {
            _podItemFrom = goggle;
            _podItemText = goggle?.Item?.Id.ToString();
        }
        return _podItemText;
    }

    // Once a frame, with the device worn: its keys, the easing, the nodes turned.
    private static void UpdatePods(Player player, string? item)
    {
        if (_body == null || item == null || !PodsTurned(_device))
        {
            ReleaseWornPods(turnDown: false);
            return;
        }
        Transform root = DeviceSlot();
        if (_wornPods == null || _wornPods.Root != root || _wornPods.Item != item || _wornPods.Kind != _device)
        {
            _wornPods = new PodNodes { Kind = _device, Root = root, Item = item };
            _wornPods.Find();
            int bits = PodState(item);
            _podUp[0] = (bits & 1) != 0 ? 1f : 0f;
            _podUp[1] = (bits & 2) != 0 ? 1f : 0f;
            _wornPodsStale = false;
            NewCameraPlugin.Log?.LogInfo($"NVG pods: {_device.Node} ({item}) worn with its {(bits == 0 ? "pods down" : (bits & 1) != 0 && (bits & 2) != 0 ? "pods both up" : (bits & 1) != 0 ? "left pod up" : "right pod up")}"
                + $" ({_wornPods.Nodes[0].Count} left and {_wornPods.Nodes[1].Count} right nodes).");
        }
        else if (_wornPodsStale || _wornPods.Lost())
        {
            _wornPodsStale = false;
            _wornPods.Find();
        }
        int state = PodState(item);
        int was = state;
        if (PodLeftKey.IsDown())
            state ^= 1;
        if (PodRightKey.IsDown())
            state ^= 2;
        if (state != was)
            SavePodState(item, state);
        float step = Time.deltaTime / PodTravel;
        _podUp[0] = Mathf.MoveTowards(_podUp[0], (state & 1) != 0 ? 1f : 0f, step);
        _podUp[1] = Mathf.MoveTowards(_podUp[1], (state & 2) != 0 ? 1f : 0f, step);
        _wornPods.Turn(_podUp[0], _podUp[1]);
        _wornPodsVersion = _podsVersion;
    }

    private static void ReleaseWornPods(bool turnDown)
    {
        if (_wornPods == null)
            return;
        if (turnDown)
            _wornPods.Turn(0f, 0f);
        _wornPods = null;
        _podUp[0] = _podUp[1] = 0f;
    }

    // ---------------------------------------------------------------- every other model of a goggle

    // Every model of a goggle the game makes is told its item (EFT.Visual.NightVisionDevice.Init: the worn one, a body
    // shown apart on the loading screen or in the inventory, the one turning in an item's own window) and told when it
    // is let go (Deinit). By those two, 1.9.49. (1.9.47 went by the body's slot models: an item's own window has no
    // body, and a body not shown yet had its nodes passed over: the user's, 2026-11-27, one pod up on the head and
    // "总览的 ENVG-B 的预览图还是没变".)
    private static readonly List<PodNodes> _shownPods = new List<PodNodes>();
    private static int _shownPodsVersion = -1;
    private static int _shownPodsNoted;   // (the first few models noted, to show where they come from)

    // An item's picture in the inventory (its icon) is drawn once from its model and kept by a number worked out from
    // the item and what is fitted to it (IconsHash.GetItemHash); the pods' state is not the game's, so the picture was
    // the same with a pod up (the user's, 2026-11-27: "贴图还不对"). The state of every goggle in the item goes into
    // that number: another picture is drawn for it, from a model whose pods are laid as kept (GoggleModelPostfix).
    private static void IconHashPostfix(Item item, ref int __result)
    {
        try
        {
            if (item == null || _podStates == null || _podStates.Count == 0 || !NewCameraSwitch.Enabled)
                return;
            foreach (Item part in item.GetAllItems())
            {
                if (_podStates.TryGetValue(part.Id.ToString(), out int bits) && bits != 0)
                    // (Another number than 1.9.50's and 1.9.51's: their pictures were drawn from copies with their pods down, and are kept.)
                    __result ^= unchecked((bits + 1) * (int)0xC2B2AE35);
            }
        }
        catch (System.Exception)
        {
        }
    }

    private static void InstallPods(HarmonyLib.Harmony harmony)
    {
        ReadPodStates();
        System.Reflection.MethodInfo? hash = HarmonyLib.AccessTools.Method(typeof(IconsHash), nameof(IconsHash.GetItemHash));
        if (hash != null)
            harmony.Patch(hash, postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(NightVisionEyepieces), nameof(IconHashPostfix))));
        else
            NewCameraPlugin.Log?.LogWarning("NVG pods: the item icons' hash not found; an item's picture does not show its pods' state.");
        System.Reflection.MethodInfo? diff = HarmonyLib.AccessTools.Method(typeof(EFT.ItemFactory), "GetDiff");
        System.Reflection.MethodInfo? made = HarmonyLib.AccessTools.Method(typeof(EFT.ItemFactory), "CreateItem");
        if (diff != null && made != null)
        {
            harmony.Patch(diff, postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(NightVisionEyepieces), nameof(ItemDiffPostfix))));
            harmony.Patch(made, postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(NightVisionEyepieces), nameof(ItemMadePostfix))));
            NewCameraPlugin.Log?.LogInfo("NVG pods: copies of items followed for their goggles' state (the item factory's GetDiff and CreateItem).");
        }
        else
            NewCameraPlugin.Log?.LogWarning($"NVG pods: the item factory's {(diff == null ? "GetDiff" : "CreateItem")} not found; a copy of a goggle (icons, the loading screen) does not show its pods' state.");
        System.Reflection.MethodInfo? init = HarmonyLib.AccessTools.Method(typeof(EFT.Visual.NightVisionDevice), nameof(EFT.Visual.NightVisionDevice.Init));
        System.Reflection.MethodInfo? deinit = HarmonyLib.AccessTools.Method(typeof(EFT.Visual.NightVisionDevice), nameof(EFT.Visual.NightVisionDevice.Deinit));
        if (init == null || deinit == null)
        {
            NewCameraPlugin.Log?.LogWarning("NVG pods: the goggle model's Init or Deinit not found; only the worn goggle's pods are turned.");
            return;
        }
        harmony.Patch(init, postfix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(NightVisionEyepieces), nameof(GoggleModelPostfix))));
        harmony.Patch(deinit, prefix: new HarmonyLib.HarmonyMethod(HarmonyLib.AccessTools.Method(typeof(NightVisionEyepieces), nameof(GoggleModelGoingPrefix))));
    }

    private static void GoggleModelPostfix(EFT.Visual.NightVisionDevice __instance, Item item)
    {
        try
        {
            if (__instance == null || item == null)
                return;
            Transform model = __instance.transform;
            Device? kind = null;
            foreach (Transform node in model.GetComponentsInChildren<Transform>(true))
            {
                foreach (Device known in Devices)
                {
                    if (known.Pods.Length > 0 && known.PodSwing > 0f && node.name == known.Node)
                        kind = known;
                }
                if (kind != null)
                    break;
            }
            if (kind == null)
                return;
            for (int i = _shownPods.Count - 1; i >= 0; i--)
            {
                if (_shownPods[i].Root == null || _shownPods[i].Root == model)
                    _shownPods.RemoveAt(i);
            }
            var nodes = new PodNodes { Kind = kind, Root = model, Item = item.Id.ToString(), LookAgain = 3, MadeFrame = Time.frameCount };
            _shownPods.Add(nodes);
            LayShown(nodes);
            if (_shownPodsNoted < 6)
            {
                _shownPodsNoted++;
                NewCameraPlugin.Log?.LogInfo($"NVG pods: a model of {kind.Node} ({nodes.Item}) made under {(model.root != null ? model.root.name : "nothing")}, its pods laid as kept (turned here: {PodsTurned(kind)}; state {PodState(nodes.Item)}; {nodes.Nodes[0].Count} left and {nodes.Nodes[1].Count} right nodes).");
            }
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("NVG pods: a goggle's model not laid: " + e.Message);
        }
    }

    // A model's pods as kept, at once. Not the worn one's while it is worn: its pods are eased (UpdatePods).
    private static void LayShown(PodNodes nodes)
    {
        if (!PodsTurned(nodes.Kind) || _wornPods != null && _wornPods.Root != null && nodes.Root.IsChildOf(_wornPods.Root))
            return;
        nodes.Find();
        int bits = PodState(nodes.Item);
        nodes.Turn((bits & 1) != 0 ? 1f : 0f, (bits & 2) != 0 ? 1f : 0f);
    }

    // A model let go (to the game's pool): its pods down again, whoever wears it next.
    private static void GoggleModelGoingPrefix(EFT.Visual.NightVisionDevice __instance)
    {
        try
        {
            if (__instance == null)
                return;
            ForgetPods(__instance.transform);
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("NVG pods: a goggle's model not put back: " + e.Message);
        }
    }

    // A model going (its own, or the slot's it is part of): its pods down again.
    private static void ForgetPods(Transform model)
    {
        for (int i = _shownPods.Count - 1; i >= 0; i--)
        {
            PodNodes nodes = _shownPods[i];
            if (nodes.Root == null || nodes.Root == model || nodes.Root.IsChildOf(model))
            {
                if (nodes.Root != null)
                {
                    nodes.Find();
                    nodes.Turn(0f, 0f);
                }
                _shownPods.RemoveAt(i);
            }
        }
        if (_wornPods != null && _wornPods.Root != null && (_wornPods.Root.IsChildOf(model) || model.IsChildOf(_wornPods.Root)))
            ReleaseWornPods(turnDown: true);
    }

    // Each frame: the models' pods laid again when a state has changed, and a new model's a few times more, for its
    // eyecups (2, 10 and 40 frames after it was made).
    private static void UpdateShownPods()
    {
        bool changed = _shownPodsVersion != _podsVersion;
        _shownPodsVersion = _podsVersion;
        for (int i = _shownPods.Count - 1; i >= 0; i--)
        {
            PodNodes nodes = _shownPods[i];
            if (nodes.Root == null)
            {
                _shownPods.RemoveAt(i);
                continue;
            }
            bool again = false;
            if (nodes.LookAgain > 0)
            {
                int since = Time.frameCount - nodes.MadeFrame;
                int due = nodes.LookAgain == 3 ? 2 : nodes.LookAgain == 2 ? 10 : 40;
                if (since >= due)
                {
                    nodes.LookAgain--;
                    again = true;
                }
            }
            if (changed || again)
                LayShown(nodes);
        }
    }

    // The goggle among the gear's headwear: the item with the night vision's own component.
    private static string? GoggleItemId(InventoryEquipment? equipment)
    {
        if (equipment == null)
            return null;
        foreach (EquipmentSlot slot in new[] { EquipmentSlot.Headwear, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear })
        {
            Item? top = equipment.GetSlot(slot).ContainedItem;
            if (top == null)
                continue;
            foreach (Item item in top.GetAllItems())
            {
                if (item.GetItemComponent<NightVisionComponent>() != null)
                    return item.Id.ToString();
            }
        }
        return null;
    }
}
