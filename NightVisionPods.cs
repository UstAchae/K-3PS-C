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
    private static bool PodsTurned(Device kind) => kind.Pods.Length > 0 && kind.PodSwing > 0f && (!kind.PodsOwnMod || _podsHandedOver.Contains(kind.Node));
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
        return id != null && _podStates!.TryGetValue(id, out int bits) ? bits : 0;
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
        internal readonly Transform?[] Pods = new Transform?[2], Cups = new Transform?[2];

        internal void Find()
        {
            Pods[0] = Pods[1] = Cups[0] = Cups[1] = null;
            foreach (Transform node in Root.GetComponentsInChildren<Transform>(true))
            {
                int pod = System.Array.IndexOf(Kind.Pods, node.name);
                if (pod >= 0 && pod < 2 && node.gameObject.activeInHierarchy)
                    Pods[pod] = node;
                int cup = System.Array.IndexOf(Kind.CupPods, node.name);
                if (cup >= 0 && cup < 2 && node.gameObject.activeInHierarchy)
                    Cups[cup] = node;
            }
        }

        internal void Turn(float left, float right)
        {
            for (int pod = 0; pod < 2; pod++)
            {
                Quaternion turn = PodTurn(Kind, pod, pod == 0 ? left : right);
                if (Pods[pod] != null)
                    Pods[pod]!.localRotation = turn;
                if (Cups[pod] != null)
                    Cups[pod]!.localRotation = turn;
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
                + $" (device nodes {(_wornPods.Pods[0] != null ? 1 : 0) + (_wornPods.Pods[1] != null ? 1 : 0)}, eyecup nodes {(_wornPods.Cups[0] != null ? 1 : 0) + (_wornPods.Cups[1] != null ? 1 : 0)}).");
        }
        else if (_wornPodsStale || Lost(_wornPods.Pods[0]) || Lost(_wornPods.Pods[1]) || Lost(_wornPods.Cups[0]) || Lost(_wornPods.Cups[1]))
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

    // A node kept that is no longer in the shown model (destroyed, or its model laid aside).
    private static bool Lost(Transform? node) => node is not null && (node == null || !node.gameObject.activeInHierarchy);

    private static void ReleaseWornPods(bool turnDown)
    {
        if (_wornPods == null)
            return;
        if (turnDown)
            _wornPods.Turn(0f, 0f);
        _wornPods = null;
        _podUp[0] = _podUp[1] = 0f;
    }

    // ---------------------------------------------------------------- the player's other models

    private static readonly List<PodNodes> _shownPods = new List<PodNodes>();
    private static int _shownPodsVersion = -1;

    // A model of the player shown apart, its goggle found (SlotModelPostfix).
    private static void ShowPods(Transform model, Device kind, InventoryEquipment? equipment)
    {
        if (!PodsTurned(kind))
            return;
        string? item = GoggleItemId(equipment);
        var nodes = new PodNodes { Kind = kind, Root = model, Item = item };
        nodes.Find();
        if (nodes.Pods[0] == null && nodes.Pods[1] == null)
            return;
        int bits = PodState(item);
        nodes.Turn((bits & 1) != 0 ? 1f : 0f, (bits & 2) != 0 ? 1f : 0f);
        _shownPods.Add(nodes);
    }

    // A slot's model going: its pods down again, whoever's it was.
    private static void ForgetPods(Transform model)
    {
        for (int i = _shownPods.Count - 1; i >= 0; i--)
        {
            PodNodes nodes = _shownPods[i];
            if (nodes.Root == null || nodes.Root == model || nodes.Root.IsChildOf(model))
            {
                if (nodes.Root != null)
                    nodes.Turn(0f, 0f);
                _shownPods.RemoveAt(i);
            }
        }
        if (_wornPods != null && _wornPods.Root != null && _wornPods.Root.IsChildOf(model))
            ReleaseWornPods(turnDown: true);
    }

    // Each frame: the shown models' pods laid again when a state has changed.
    private static void UpdateShownPods()
    {
        if (_shownPodsVersion == _podsVersion)
            return;
        _shownPodsVersion = _podsVersion;
        for (int i = _shownPods.Count - 1; i >= 0; i--)
        {
            PodNodes nodes = _shownPods[i];
            if (nodes.Root == null)
            {
                _shownPods.RemoveAt(i);
                continue;
            }
            int bits = PodState(nodes.Item);
            nodes.Turn((bits & 1) != 0 ? 1f : 0f, (bits & 2) != 0 ? 1f : 0f);
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
