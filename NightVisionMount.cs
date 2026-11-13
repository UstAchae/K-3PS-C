using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The helmet mount the device hangs from, set as a wearer sets it. A Norotos TATM (vanilla) has a base on the helmet:
// a plate, a vertical track with a knob on top, and a carriage riding the track; turning the knob runs the carriage up
// or down. The arm hangs from the carriage on a link (the hinge the device flips up on at its top, a pair of pins at
// its bottom the arm tilts on), and has a toothed slot along it that the shoe's slide rides in, fore and aft. The
// model has the base and the arm each as one mesh (of separate pieces), so each is read back once through the GPU
// (they are not readable on the CPU) and split into what moves. The player sets the three: the carriage's height
// (the knob turns with it), the arm's tilt and the slide's place on the slot, by hotkeys held while the eyepieces
// show and the weapon is not aimed. Each mount (the item, not its kind) keeps its own, in a file of the plugin's
// beside its config. Applied only for the main camera's rendering and put back
// after it: the game's own pose is never touched.
// A Wilcox L4 G24 (the GPNVG-18's own mount) is set the same three ways: its carriage, with the hinge and the shoe,
// rides up or down a plate on the helmet once its lock lever is swung open (it swings back when let go); a screw on
// the hinge sets the tilt; and fore and aft, with the release button pressed, the goggle moves from slot to slot of
// the ten along the shoe's rack (the button's pawl drops into the one it is let go at). There the base mesh holds
// plate, carriage, lever and screw, and the goggle's own mesh holds the shoe and the button. The rack's slots are
// only painted (the normal map), read off it.
// Wilcox's own L4 G24 (WTT's, black or coyote), with a goggle's adapter in its shoe, is set the same three ways too:
// everything but the plate on the helmet rides up or down once the lock lever on the carriage's face is swung over to
// its other side (it swings back when let go); the tilt, on the hinge, turns the knob at its end once the lock knob on
// the carriage's other side is turned a couple of turns loose (tightened again when let go); and fore and aft, with
// the buttons on the shoe's sides pressed in, the shoe slides along the arm. The base mesh holds the plate, the
// carriage with its lever, and the two knobs; the arm mesh, under the hinge, the arm, the shoe and its buttons.
internal static partial class NightVisionEyepieces
{
    private sealed class Mount
    {
        internal string Prefix = "";       // the mount's root, by name (a clone of its prefab)
        internal string Hinge = "";        // the arm's link, under the root, on the carriage; the device flips about it
        internal string Shoe = "";         // where the goggle hangs, under the hinge, on the slide
        internal string Base = "";         // the base's mesh: plate, track, knob and carriage together
        internal string Arm = "";          // the arm's mesh: link, pins, slotted arm and slide together
        internal float Lead;               // the carriage's travel per turn of the knob (m)
        internal Vector3 TiltPivot;        // the pins the arm tilts on, in the arm mesh's frame (m)
        internal Vector3 TiltAxis;
        internal Vector3 Slot;             // along the slot, away from the link, in the arm mesh's frame
        internal Vector3 Screw;            // the slide's screw, turning as it goes: a point on its axis (arm mesh's frame)
        internal Vector3 ScrewAxis;        // its axis, outward: the knob is what of the slide lies past ScrewFrom along it
        internal float ScrewFrom;
        internal float ScrewLead;          // the slide's travel per turn of the screw (m)
        internal float HeightMin, HeightMax, MinTilt, MaxTilt, SlideBack, SlideForward;   // from where the model has it (m, degrees)

        // A G24: the goggle carries the shoe and hangs right under the hinge; in the base mesh's frame, a point in the
        // carriage, the lever (a point in it, its hub, the axis it swings on and how far) and the tilt screw (a point in
        // it, its axis), and the plate: a triangle is the plate's when wholly past any of PlatePlanes (normal, offset).
        // Slot is then along the dovetail, forward, in the goggle's frame.
        internal bool G24;
        internal Vector3 CarriagePoint, LeverPoint, LeverPivot, LeverAxis, TiltScrewPoint, TiltScrewPivot, TiltScrewAxis;
        internal float LeverAngle, TiltPerTurn;
        internal Vector4[] PlatePlanes = System.Array.Empty<Vector4>();

        // Slots, where fore and aft is not free: each slot's place along Slot's line (goggle's frame, m, the pawl in
        // it) and the one the model has the pawl in; the release button (a point in it, its pivot, its axis and how
        // far it turns pressed), in the goggle's frame.
        internal float[] Slots = System.Array.Empty<float>();
        internal int ModelSlot;
        internal Vector3 ButtonPoint, ButtonPivot, ButtonAxis;     // the pivot is its bolt's centre, the axis the bolt's
        internal float ButtonAngle;
        // The base the button turns on, which stays with the goggle: a block between ButtonBaseLeft and ButtonBaseRight
        // (X), with an arch at each end (beyond ButtonArchInner along Y) the bolt runs through; what of the button lies
        // in that width and either in an arch or, below the bolt, square to the axes (the block's faces) is the base.
        internal float ButtonBaseLeft, ButtonBaseRight;
        internal Vector2 ButtonArchInner;

        // A Wilcox L4 G24 of its own; in the base mesh's frame as the coyote one has it (the black one has its carriage
        // higher, found by where the carriage's piece is): the carriage's lowest point, the lever (part of the carriage's
        // piece: what lies within its disc or its handle's box, above LeverFloor, reaching past LeverFace), a point in
        // each knob and in the lock knob's washer, the lock knob's turns and travel out per turn; in the arm mesh's
        // frame, a point in each of the shoe's buttons and how far they press in. HeightMax is the travel up from where
        // the coyote one has it; SlideForward toward the arm's end, SlideBack toward the hinge.
        internal bool L4;
        internal float CarriageLow, ShoeLow, LeverRadius, LeverFloor, LeverFace, LockTurns, LockLead, ButtonDepth;
        internal Vector3 LeverCentre, WasherPoint, LockPoint, ButtonLeftPoint, ButtonRightPoint;
        internal Vector2 LeverHandleMin, LeverHandleMax;

        // A mount whose moving parts are each a node of their own (K-ENVG-B's): found by their names under the root,
        // each set from where the prefab has it. The carriage slides along CarriageAlong (its parent's frame), the
        // height screw turns about its own Y by Lead a turn, the tilt node turns about its own X (eyepieces up), the
        // slide moves along its own Z (forward, away from the face). PathMatch: the item's prefab path holds it.
        internal bool Joints;
        internal string PathMatch = "";
        internal string CarriageNode = "", ScrewNode = "", TiltNode = "", SlideNode = "";
        internal Vector3 CarriageAlong;
    }

    private static readonly Mount[] Mounts =
    {
        new Mount
        {
            Prefix = "nvg_norotos_tatm_universal", Hinge = "axis", Shoe = "axis/mod_nvg",
            Base = "nvg_norotos_tatm_universal_base_LOD0", Arm = "nvg_norotos_tatm_universal_arm_LOD0", Lead = 0.004f,
            TiltPivot = new Vector3(0f, -0.0121f, -0.0232f), TiltAxis = Vector3.right, Slot = new Vector3(0f, -0.994f, -0.109f),
            Screw = new Vector3(0.0256f, -0.02946f, -0.04342f), ScrewAxis = Vector3.right, ScrewFrom = 0.0222f, ScrewLead = 0.003f,
            HeightMin = 0f, HeightMax = 0.02f, MinTilt = -15f, MaxTilt = 15f, SlideBack = 0.02f, SlideForward = 0.004f,
        },
        // The GPNVG-18 on its Wilcox L4 G24: about 0.9 in of height and 1.4 in of fore and aft travel; 15 degrees of tilt,
        // all of it eyepieces up from where the model has it (the G24's knob turns one way from its stop).
        new Mount
        {
            Prefix = "nvg_l3_gpnvg-18_anvis", Hinge = "axis", Base = "nvg_l3_gpnvg-18_anvis_base_LOD0", G24 = true,
            CarriagePoint = new Vector3(-0.00285f, -0.02319f, -0.01809f),
            LeverPoint = new Vector3(-0.00225f, -0.01733f, 0.00524f), LeverPivot = new Vector3(-0.00014f, -0.01918f, 0.00108f),
            LeverAxis = new Vector3(-0.003f, -1f, 0.016f), LeverAngle = -53.3f,
            TiltScrewPoint = new Vector3(0.03053f, -0.02051f, -0.02051f), TiltScrewPivot = new Vector3(0.0305f, -0.02051f, -0.02051f),
            TiltScrewAxis = Vector3.right, TiltPerTurn = 5f,
            PlatePlanes = new[] { new Vector4(0f, 1f, 0f, -0.0035f) },
            Slot = Vector3.down,
            // Slots 1 to 10 along the goggle's Y (toward the eyes), about 4 mm apart; the model's pawl sits in the 7th.
            Slots = new[] { -0.03108f, -0.02699f, -0.02296f, -0.01887f, -0.01485f, -0.01076f, -0.00674f, -0.00271f, 0.00138f, 0.00540f },
            ModelSlot = 6,
            ButtonPoint = new Vector3(-0.0430f, -0.0068f, -0.0240f), ButtonPivot = new Vector3(-0.03825f, -0.0068f, -0.0224f),
            ButtonAxis = Vector3.up, ButtonAngle = -12f,
            ButtonBaseLeft = -0.0425f, ButtonBaseRight = -0.0341f, ButtonArchInner = new Vector2(-0.0129f, -0.0006f),
            HeightMin = -0.0115f, HeightMax = 0.0115f, MinTilt = 0f, MaxTilt = 15f,
        },
        // Wilcox's L4 G24 (WTT): the black one has the carriage at the top of its travel, the coyote one at the bottom,
        // 34.3 mm lower. The shoe slides from the arm's end stop to the hinge's block.
        new Mount
        {
            Prefix = "nvg_wilcox_l4g24_mount", Hinge = "axis", Shoe = "axis/mod_nvg", L4 = true,
            Base = "nvg_wilcox_l4g24_mount_LOD0", Arm = "nvg_wilcox_l4g24_mount_axis_LOD0",
            CarriagePoint = new Vector3(0f, 0.010f, 0.012f), CarriageLow = -0.03748f, ShoeLow = -0.0574f, HeightMin = 0f, HeightMax = 0.0343f,
            LeverCentre = new Vector3(0f, -0.00775f, 0.0157f), LeverRadius = 0.0106f,
            LeverHandleMin = new Vector2(-0.021f, -0.003f), LeverHandleMax = new Vector2(-0.002f, 0.0135f), LeverFloor = 0.0155f, LeverFace = 0.0157f,
            LeverAxis = Vector3.forward, LeverAngle = -92f,
            TiltScrewPoint = new Vector3(-0.026f, -0.02722f, 0.02303f), TiltScrewAxis = Vector3.right, TiltPerTurn = 5f, MinTilt = -10f, MaxTilt = 10f,
            WasherPoint = new Vector3(0.02438f, -0.01275f, 0.01525f), LockPoint = new Vector3(0.0303f, -0.01275f, 0.01525f), LockTurns = 2f, LockLead = 0.0008f,
            ButtonLeftPoint = new Vector3(-0.0243f, -0.0365f, -0.014f), ButtonRightPoint = new Vector3(0.0243f, -0.0365f, -0.014f), ButtonDepth = 0.002f,
            SlideForward = 0.009f, SlideBack = 0.013f,
        },
        // The ENVG-B's own mount (K-ENVG-B): a plate on the helmet with a screw that runs a carriage up or down it (1 mm
        // a turn); the device flips on the carriage's hinge; the arm tilts on a pin into a bracket whose rails the
        // device's shoe slides along. Travel as the mesh allows: 3 mm down to the plate's stop, 14 mm up to the screw's
        // knob; 6 mm away from the face to the bracket's end, and 25 mm toward it (the shoe then 12 mm past the rails'
        // end, 19 mm still in them: the rails' 13 mm was too little). No stop shows for the tilt.
        new Mount
        {
            Prefix = "nvg_envgb_psq42", PathMatch = "envgb", Joints = true, Hinge = "nvg_envgb_psq42_mount_LOD0/envgb_carriage/axis",
            CarriageNode = "nvg_envgb_psq42_mount_LOD0/envgb_carriage", ScrewNode = "nvg_envgb_psq42_mount_LOD0/envgb_height_screw",
            TiltNode = "nvg_envgb_psq42_mount_LOD0/envgb_carriage/axis/envgb_arm/envgb_tilt",
            SlideNode = "nvg_envgb_psq42_mount_LOD0/envgb_carriage/axis/envgb_arm/envgb_tilt/envgb_bracket/envgb_slide",
            CarriageAlong = new Vector3(0.0003f, 0.9982f, 0.0602f), Lead = 0.001f,
            HeightMin = -0.003f, HeightMax = 0.014f, MinTilt = -10f, MaxTilt = 20f, SlideBack = 0.025f, SlideForward = 0.006f,
        },
    };

    private const float MountSpeed = 0.01f;         // height and slide while a hotkey is held (m/s)
    private const float MountTiltSpeed = 10f;       // degrees per second
    private const float PieceGap = 0.001f;          // how close a piece sits to another to go with it
    private const float LeverTime = 0.25f;          // a G24's lock lever swinging open or shut (s)
    private const float ButtonTime = 0.15f;         // a release button pressed or let go (s)
    private const float SlotSpeed = 0.025f;         // the goggle from slot to slot (m/s)
    private const float LockTime = 0.6f;            // an L4 G24's lock knob turned loose or tight (s)

    internal static KeyboardShortcut RaiseMount = new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftAlt), LowerMount = new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftAlt);
    internal static KeyboardShortcut TiltUp = new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftShift), TiltDown = new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftShift);
    internal static KeyboardShortcut SlideForward = new KeyboardShortcut(KeyCode.Equals, KeyCode.LeftControl), SlideBack = new KeyboardShortcut(KeyCode.Minus, KeyCode.LeftControl);
    private static readonly string MountsPath = Path.Combine(BepInEx.Paths.ConfigPath, "justachae.thirdpersoncamera.mounts.txt");
    private static Dictionary<string, Vector3>? _mountSettings;   // by the mount item's id: height, tilt, slide

    private static readonly KeyCode[][] ModifierKeys =
    {
        new[] { KeyCode.LeftShift, KeyCode.RightShift }, new[] { KeyCode.LeftControl, KeyCode.RightControl }, new[] { KeyCode.LeftAlt, KeyCode.RightAlt },
    };

    private static MountRig? _wornMount;
    private static Transform? _mountOf;
    private static float _flip;                      // the device's flip down (0 up, 1 down), whatever the eye mode

    // The worn mount: the one the current device hangs from.
    private static void FindMount()
    {
        if (_mountOf == _body)
            return;
        ReleaseMount();
        _mountOf = _body;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (_body != null)
            _wornMount = MountRig.Make(_body, EquipmentIds.Worn(player), player != null ? player.Transform.Original : null, worn: true, onlyIfSet: false);
    }

    // The player's hotkeys, only while the eyepieces show and the weapon is held at the hip: the carriage up or down
    // its track, the arm tilted, the slide along its slot. Saved once the keys are let go.
    private static void UpdateMountHeight(Player player)
    {
        FindMount();
        _wornMount?.SyncPieces();
        _wornMount?.UpdateKeys(player);
    }

    private static void AdjustMount(Camera camera)
    {
        FindMount();
        _wornMount?.Adjust();
    }

    // The worn mount whole again, as the model has it.
    private static void ReleaseMount()
    {
        _wornMount?.Release();
        _wornMount = null;
        _mountOf = null;
        _blockersOf = null;
    }

    private static bool HasMountSettings(string id)
    {
        ReadMountSettings();
        return _mountSettings!.ContainsKey(id);
    }

    private static Vector3 SavedMountSettings(string? id)
    {
        ReadMountSettings();
        return id != null && _mountSettings!.TryGetValue(id, out Vector3 saved) ? saved : Vector3.zero;
    }

    private static void ReadMountSettings()
    {
        if (_mountSettings != null)
            return;
        _mountSettings = new Dictionary<string, Vector3>();
        try
        {
            if (File.Exists(MountsPath))
            {
                foreach (string line in File.ReadAllLines(MountsPath))
                {
                    string[] f = line.Split(' ');
                    if (f.Length == 4 && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float h)
                        && float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float t)
                        && float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float s))
                        _mountSettings[f[0]] = new Vector3(h, t, s);
                }
            }
        }
        catch (IOException e)
        {
            NewCameraPlugin.Log?.LogWarning("Mount settings not read: " + e.Message);
        }
    }

    private static void SaveMountSettings(string? id, Vector3 settings)
    {
        if (id == null)
            return;
        ReadMountSettings();
        _mountSettings![id] = settings;
        var lines = new List<string>();
        foreach (KeyValuePair<string, Vector3> e in _mountSettings)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:R} {2:R} {3:R}", e.Key, e.Value.x, e.Value.y, e.Value.z));
        // Written off the main thread, so a slow disk never holds up a frame; one write at a time, in order.
        string path = MountsPath;
        System.Threading.Tasks.Task.Run(() =>
        {
            lock (MountsWriteLock)
            {
                try
                {
                    File.WriteAllLines(path, lines);
                }
                catch (IOException e)
                {
                    NewCameraPlugin.Log?.LogWarning("Mount settings not saved: " + e.Message);
                }
            }
        });
    }

    private static readonly object MountsWriteLock = new object();

    // The id of the mount item among the gear's headwear, by its prefab.
    private static string? MountItemId(Mount mount, InventoryEquipment? equipment)
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
                string? path = item.Prefab?.path;
                if (path != null && path.IndexOf(mount.PathMatch.Length > 0 ? mount.PathMatch : mount.Prefix, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return item.Id.ToString();
            }
        }
        return null;
    }

    // A slot's place as the goggle's offset along Slot (forward) from where the model has it.
    private static float SlotOffset(Mount mount, int slot) => -(mount.Slots[slot] - mount.Slots[mount.ModelSlot]);

    private static int NearestSlot(Mount mount, float offset)
    {
        int best = mount.ModelSlot;
        for (int i = 0; i < mount.Slots.Length; i++)
        {
            if (Mathf.Abs(SlotOffset(mount, i) - offset) < Mathf.Abs(SlotOffset(mount, best) - offset))
                best = i;
        }
        return best;
    }

    private static bool Turn(ref float value, KeyboardShortcut up, KeyboardShortcut down, float speed, float min, float max)
    {
        int direction = (Held(up) ? 1 : 0) - (Held(down) ? 1 : 0);
        if (direction == 0)
            return false;
        value = Mathf.Clamp(value + direction * speed * Time.deltaTime, min, max);
        return true;
    }

    // A shortcut held with exactly its modifiers: minus alone is not Shift or Ctrl and minus.
    internal static bool Held(KeyboardShortcut shortcut)
    {
        if (!shortcut.IsPressed())
            return false;
        var wanted = new List<KeyCode>(shortcut.Modifiers);
        foreach (KeyCode[] group in ModifierKeys)
        {
            bool held = Input.GetKey(group[0]) || Input.GetKey(group[1]);
            if (held && !wanted.Contains(group[0]) && !wanted.Contains(group[1]))
                return false;
        }
        return true;
    }

    // A mount the device hangs from, split into what moves and set as its item's settings have it: the worn one (set by
    // the player's keys) or one on a model of the player shown elsewhere (the inventory's, the loading screen's).
    private sealed class MountRig
    {
        internal bool Worn;
        private readonly Transform _device;
        private readonly Transform? _facing;     // which way the wearer faces
        private float MountHeight, MountTilt, MountSlide;    // m, degrees (eyepieces up), m (forward)
        private string? _mountId;
        private Mount? _mount;
        private Transform? _mountHinge, _mountShoe, _carriage, _knob, _arm, _slide, _screw, _lever, _tiltScrew, _goggleShoe;
        private MeshFilter? _base, _armFilter, _goggle;
        private Mesh? _baseMesh, _armMesh, _goggleMesh;
        private Transform[] _hingeChildren = System.Array.Empty<Transform>();
        private Vector3[] _hingeChildRest = System.Array.Empty<Vector3>();
        private Quaternion _hingeRestRotation;
        // What the adjustment set on the game's own transforms (the hinge, the shoe, the hinge's children), so that the
        // next frame can tell its own setting, put back before it is worked out afresh, from the game moving them itself.
        private Vector3 _hingeSetPosition, _shoeSetPosition;
        private Quaternion _hingeSetRotation, _shoeSetRotation;
        private Vector3[] _hingeChildSet = System.Array.Empty<Vector3>();
        private float _leverOpen;                 // a G24's lock lever: 0 shut, 1 open
        private float _buttonPress;               // the release button: 0 out, 1 pressed
        private int _slot = -1;                   // the slot the goggle is in or going to
        private Transform? _button;
        private Transform? _lockKnob, _l4Shoe, _buttonLeft, _buttonRight;
        private float _lockLoose;                 // an L4 G24's lock knob: 0 tight, 1 loose
        private float _modelLift;                 // how far up the model has the carriage from CarriageLow (m)
        private float _modelSlide;                // how far forward the model has an L4 G24's shoe from ShoeLow (m)
        private Vector3 _leverPivot, _tiltScrewPivot, _lockPivot;
        private readonly List<Mesh> _pieces = new List<Mesh>();
        private readonly List<(MeshRenderer piece, MeshRenderer source)> _pieceRenderers = new List<(MeshRenderer, MeshRenderer)>();
        private LODGroup? _lodGroup;
        private LOD[]? _lods;
        private Vector3 _knobPivot, _knobAxis, _hingeRestPosition, _shoeRestPosition;
        private Quaternion _shoeRestRotation;
        private float _tiltSign = 1f;
        private bool _mountMoved, _mountChanged;
        // A Joints mount's nodes and where the prefab has them.
        private Transform? _jointScrew, _jointTilt, _jointSlide;
        private Vector3 _carriageRest, _slideRest;
        private Quaternion _screwRest, _tiltRest;
        private bool _jointsMoved;

        private MountRig(Transform device, Transform? facing)
        {
            _device = device;
            _facing = facing;
        }

        internal Transform Device => _device;

        // The mount the device hangs from, if it is one this knows, with its base and arm split; its settings those kept
        // for its item among the gear given (none: only if some are kept).
        internal static MountRig? Make(Transform device, InventoryEquipment? equipment, Transform? facing, bool worn, bool onlyIfSet)
        {
            for (Transform? t = device.parent; t != null; t = t.parent)
            {
                foreach (Mount mount in Mounts)
                {
                    if (!t.name.StartsWith(mount.Prefix, System.StringComparison.Ordinal))
                        continue;
                    if (mount.Joints)
                    {
                        MountRig? jointed = MakeJoints(t, device, mount, equipment, facing, worn, onlyIfSet);
                        if (jointed != null || onlyIfSet)
                            return jointed;
                        continue;
                    }
                    Transform? hinge = t.Find(mount.Hinge), shoe = mount.G24 ? hinge : t.Find(mount.Shoe);
                    if (hinge == null || shoe == null || !device.IsChildOf(shoe))
                        continue;
                    string? id = MountItemId(mount, equipment);
                    if (onlyIfSet && (id == null || !HasMountSettings(id)))
                        return null;
                    var rig = new MountRig(device, facing) { Worn = worn, _mount = mount, _mountHinge = hinge, _mountShoe = shoe };
                    rig.LoadSettings(mount, id);
                    rig._lodGroup = t.GetComponentInChildren<LODGroup>(true);
                    rig._lods = rig._lodGroup != null ? rig._lodGroup.GetLODs() : null;
                    if (mount.G24)
                        rig.SplitG24(t, hinge, mount);
                    else if (mount.L4)
                        rig.SplitL4(t, hinge, shoe, mount);
                    else
                    {
                        rig.SplitBase(t, hinge, mount);
                        rig.SplitArm(hinge, shoe, mount);
                    }
                    return rig;
                }
            }
            return null;
        }

        // A Joints mount: its nodes found by name under the root, the device under its slide.
        private static MountRig? MakeJoints(Transform root, Transform device, Mount mount, InventoryEquipment? equipment, Transform? facing, bool worn, bool onlyIfSet)
        {
            Transform? carriage = root.Find(mount.CarriageNode), hinge = root.Find(mount.Hinge), screw = root.Find(mount.ScrewNode);
            Transform? tilt = root.Find(mount.TiltNode), slide = root.Find(mount.SlideNode);
            if (carriage == null || hinge == null || tilt == null || slide == null || !device.IsChildOf(slide))
                return null;
            string? id = MountItemId(mount, equipment);
            if (onlyIfSet && (id == null || !HasMountSettings(id)))
                return null;
            var rig = new MountRig(device, facing)
            {
                Worn = worn, _mount = mount, _mountHinge = hinge, _mountShoe = slide,
                _carriage = carriage, _arm = tilt, _jointScrew = screw, _jointTilt = tilt, _jointSlide = slide,
                _carriageRest = carriage.localPosition, _slideRest = slide.localPosition,
                _screwRest = screw != null ? screw.localRotation : Quaternion.identity, _tiltRest = tilt.localRotation,
            };
            rig.LoadSettings(mount, id);
            return rig;
        }

        // A Joints mount set: the carriage (and all it carries) along its track, the screw turned with it; the tilt
        // node turned; the slide along the rails. Every frame from where the prefab has them: the game moves none of
        // them (it turns only the hinge, between the carriage and the arm).
        private void AdjustJoints(Mount mount)
        {
            if (_carriage == null || _jointTilt == null || _jointSlide == null)
                return;
            float height = Mathf.Clamp(MountHeight, mount.HeightMin, mount.HeightMax);
            float tilt = Mathf.Clamp(MountTilt, mount.MinTilt, mount.MaxTilt);
            float slide = Mathf.Clamp(MountSlide, -mount.SlideBack, mount.SlideForward);
            _carriage.localPosition = _carriageRest + mount.CarriageAlong.normalized * height;
            if (_jointScrew != null)
                _jointScrew.localRotation = _screwRest * Quaternion.AngleAxis(height / mount.Lead * 360f, Vector3.up);
            _jointTilt.localRotation = _tiltRest * Quaternion.AngleAxis(tilt, Vector3.right);
            _jointSlide.localPosition = _slideRest + _jointSlide.localRotation * Vector3.forward * slide;
            _jointsMoved = true;
        }

        private void RestoreJoints()
        {
            if (!_jointsMoved)
                return;
            _jointsMoved = false;
            if (_carriage != null)
                _carriage.localPosition = _carriageRest;
            if (_jointScrew != null)
                _jointScrew.localRotation = _screwRest;
            if (_jointTilt != null)
                _jointTilt.localRotation = _tiltRest;
            if (_jointSlide != null)
                _jointSlide.localPosition = _slideRest;
        }

        private void LoadSettings(Mount mount, string? id)
        {
            _mountId = id;
            Vector3 v = SavedMountSettings(id);
            MountHeight = v.x;
            MountTilt = v.y;
            MountSlide = v.z;
            _slot = -1;
            if (mount.Slots.Length > 0)
            {
                _slot = NearestSlot(mount, MountSlide);
                MountSlide = SlotOffset(mount, _slot);
            }
        }

        // The player's hotkeys, only while the eyepieces show and the weapon is held at the hip: the carriage up or down
        // its track, the arm tilted, the slide along its slot. Saved once the keys are let go.
        internal void UpdateKeys(Player player)
        {
            bool aiming = player.ProceduralWeaponAnimation != null && player.ProceduralWeaponAnimation.IsAiming;
            bool can = Active && _mount != null && !aiming;
            bool moved = false;
            Mount? m = _mount;
            if (can && m != null && _carriage != null)
            {
                // A G24's height moves only with its lock lever open: swung open while a key is held, shut once let go.
                bool raising = Held(RaiseMount) || Held(LowerMount);
                if (_lever != null)
                    _leverOpen = Mathf.MoveTowards(_leverOpen, raising ? 1f : 0f, Time.deltaTime / LeverTime);
                if (_lever == null || _leverOpen >= 1f)
                    moved |= Turn(ref MountHeight, RaiseMount, LowerMount, MountSpeed, m.HeightMin - _modelLift, m.HeightMax - _modelLift);
            }
            else
                _leverOpen = Mathf.MoveTowards(_leverOpen, 0f, Time.deltaTime / LeverTime);
            if (can && m != null && (_arm != null || _goggleShoe != null || m.L4))
            {
                // An L4 G24 tilts only with its lock knob loose: turned loose while a key is held, tight again once let go.
                bool tilting = Held(TiltUp) || Held(TiltDown);
                if (_lockKnob != null)
                    _lockLoose = Mathf.MoveTowards(_lockLoose, tilting ? 1f : 0f, Time.deltaTime / LockTime);
                if (_lockKnob == null || _lockLoose >= 1f)
                    moved |= Turn(ref MountTilt, TiltUp, TiltDown, MountTiltSpeed, m.MinTilt, m.MaxTilt);
                if (m.Slots.Length > 0)
                    moved |= StepSlots(m);
                else if (m.L4)
                {
                    // Its shoe slides only with the buttons pressed in: pressed while a key is held, let out once let go.
                    bool sliding = Held(SlideForward) || Held(SlideBack);
                    _buttonPress = Mathf.MoveTowards(_buttonPress, sliding ? 1f : 0f, Time.deltaTime / ButtonTime);
                    if (_buttonPress >= 1f)
                        moved |= Turn(ref MountSlide, SlideForward, SlideBack, MountSpeed, -m.SlideBack - _modelSlide, m.SlideForward - _modelSlide);
                }
                else
                    moved |= Turn(ref MountSlide, SlideForward, SlideBack, MountSpeed, -m.SlideBack, m.SlideForward);
            }
            else
            {
                _buttonPress = Mathf.MoveTowards(_buttonPress, 0f, Time.deltaTime / ButtonTime);
                _lockLoose = Mathf.MoveTowards(_lockLoose, 0f, Time.deltaTime / LockTime);
            }
            if (moved)
                _mountChanged = true;
            else if (_mountChanged)
            {
                _mountChanged = false;
                SaveMountSettings(_mountId, new Vector3(MountHeight, MountTilt, MountSlide));
            }
        }

        // Fore and aft by slots: held, the button is pressed, then the goggle goes slot by slot the way asked, each reached
        // before the next; let go, it stays in the slot it has reached and the button comes back out.
        private bool StepSlots(Mount mount)
        {
            if (_slot < 0)
                _slot = NearestSlot(mount, MountSlide);
            int direction = (Held(SlideForward) ? 1 : 0) - (Held(SlideBack) ? 1 : 0);
            float at = SlotOffset(mount, _slot);
            bool there = Mathf.Abs(MountSlide - at) < 1e-5f;
            _buttonPress = Mathf.MoveTowards(_buttonPress, direction != 0 || !there ? 1f : 0f, Time.deltaTime / ButtonTime);
            if (_buttonPress < 1f)
                return false;
            if (there && direction != 0)
            {
                // Forward (away from the eyes) is down the slots' numbers.
                int next = Mathf.Clamp(_slot - direction, 0, mount.Slots.Length - 1);
                if (next == _slot)
                    return false;
                _slot = next;
                at = SlotOffset(mount, _slot);
            }
            float was = MountSlide;
            MountSlide = Mathf.MoveTowards(MountSlide, at, SlotSpeed * Time.deltaTime);
            return MountSlide != was;
        }

        // The carriage, and the arm on it, up the track, the knob turned to match; the arm tilted on its pins, and the
        // slide, with the goggle, along the slot.
        // The adjustment is the mount's real state, not a pose for the main camera alone: it stays from one frame to the
        // next, for every camera and for the frame's motion vectors (put back after each render, a moved mount went back
        // and forth every frame, and a motion blur smeared it). Before it is worked out again, the game's transforms it set
        // go back to where the game had them, those the game has not moved itself since.
        internal void Adjust()
        {
            if (_mount == null || _mountHinge == null || _mountShoe == null)
                return;
            if (_mount.Joints)
            {
                AdjustJoints(_mount);
                return;
            }
            TakeBackOwnSetting();
            if (_mount.G24)
                AdjustG24(_mount, _mountHinge);
            else if (_mount.L4)
                AdjustL4(_mount, _mountHinge, _mountShoe);
            else
                AdjustArm();
            NoteOwnSetting();
        }

        private void TakeBackOwnSetting()
        {
            if (!_mountMoved)
                return;
            _mountMoved = false;
            if (_mountHinge != null)
            {
                if (_mountHinge.localPosition == _hingeSetPosition)
                    _mountHinge.localPosition = _hingeRestPosition;
                if (_mountHinge.localRotation == _hingeSetRotation)
                    _mountHinge.localRotation = _hingeRestRotation;
            }
            if (_mountShoe != null)
            {
                if (_mountShoe.localPosition == _shoeSetPosition)
                    _mountShoe.localPosition = _shoeRestPosition;
                if (_mountShoe.localRotation == _shoeSetRotation)
                    _mountShoe.localRotation = _shoeRestRotation;
            }
            for (int i = 0; _mount != null && _mount.G24 && i < _hingeChildren.Length && i < _hingeChildSet.Length; i++)
            {
                if (_hingeChildren[i] != null && _hingeChildren[i].localPosition == _hingeChildSet[i])
                    _hingeChildren[i].localPosition = _hingeChildRest[i];
            }
        }

        private void NoteOwnSetting()
        {
            if (!_mountMoved)
                return;
            if (_mountHinge != null)
            {
                _hingeSetPosition = _mountHinge.localPosition;
                _hingeSetRotation = _mountHinge.localRotation;
            }
            if (_mountShoe != null)
            {
                _shoeSetPosition = _mountShoe.localPosition;
                _shoeSetRotation = _mountShoe.localRotation;
            }
            if (_mount == null || !_mount.G24)
                return;
            if (_hingeChildSet.Length != _hingeChildren.Length)
                _hingeChildSet = new Vector3[_hingeChildren.Length];
            for (int i = 0; i < _hingeChildren.Length; i++)
                _hingeChildSet[i] = _hingeChildren[i] != null ? _hingeChildren[i].localPosition : Vector3.zero;
        }

        // An arm mount's carriage, arm and slide.
        private void AdjustArm()
        {
            if (_mount == null || _mountHinge == null || _mountShoe == null)
                return;
            _mountMoved = true;
            _hingeRestPosition = _mountHinge.localPosition;
            _hingeRestRotation = _mountHinge.localRotation;
            _shoeRestPosition = _mountShoe.localPosition;
            _shoeRestRotation = _mountShoe.localRotation;
            float height = 0f, tilt = 0f, slide = 0f;
            if (_carriage != null && _knob != null && _base != null)
            {
                height = Mathf.Clamp(MountHeight, _mount.HeightMin, _mount.HeightMax);
                Transform root = _mountHinge.parent;
                Vector3 up = root.up * height;
                _mountHinge.localPosition = _hingeRestPosition + root.InverseTransformVector(up);
                _carriage.localPosition = _base.transform.InverseTransformVector(up);
                _knob.localRotation = Quaternion.AngleAxis(-height / _mount.Lead * 360f, _knobAxis);
            }
            if (_arm != null && _slide != null && _armFilter != null)
            {
                tilt = Mathf.Clamp(MountTilt, _mount.MinTilt, _mount.MaxTilt);
                slide = Mathf.Clamp(MountSlide, -_mount.SlideBack, _mount.SlideForward);
                Quaternion turn = Quaternion.AngleAxis(tilt * _tiltSign, _mount.TiltAxis);
                Vector3 pivot = _mount.TiltPivot;
                _arm.localRotation = turn;
                _arm.localPosition = pivot - turn * pivot;
                Vector3 at = pivot + turn * (_mount.Slot.normalized * slide - pivot);
                _slide.localRotation = turn;
                _slide.localPosition = at;
                if (_screw != null)
                {
                    _screw.localRotation = turn * Quaternion.AngleAxis(slide / _mount.ScrewLead * 360f, _mount.ScrewAxis);
                    _screw.localPosition = at + turn * _mount.Screw;
                }
                // The shoe goes as the slide does, in the arm mesh's frame.
                Transform frame = _armFilter.transform;
                Vector3 local = frame.InverseTransformPoint(_mountShoe.position);
                Quaternion localRotation = Quaternion.Inverse(frame.rotation) * _mountShoe.rotation;
                _mountShoe.SetPositionAndRotation(frame.TransformPoint(at + turn * local), frame.rotation * (turn * localRotation));
            }
        }

        // A G24: the carriage, the hinge and all under it up or down the plate, the lever swung as far as it is open; the
        // hinge turned by the tilt and the screw with it; the goggle along its dovetail, the shoe kept where it was.
        private void AdjustG24(Mount mount, Transform hinge)
        {
            _mountMoved = true;
            _hingeRestPosition = hinge.localPosition;
            _hingeRestRotation = hinge.localRotation;
            if (_mountShoe != null)
            {
                _shoeRestPosition = _mountShoe.localPosition;
                _shoeRestRotation = _mountShoe.localRotation;
            }
            if (_hingeChildren.Length != hinge.childCount)
            {
                _hingeChildren = new Transform[hinge.childCount];
                _hingeChildRest = new Vector3[hinge.childCount];
            }
            for (int i = 0; i < _hingeChildren.Length; i++)
            {
                _hingeChildren[i] = hinge.GetChild(i);
                _hingeChildRest[i] = _hingeChildren[i].localPosition;
            }

            Transform root = hinge.parent;
            float height = _carriage != null ? Mathf.Clamp(MountHeight, mount.HeightMin, mount.HeightMax) : 0f;
            Vector3 up = root.up * height;
            hinge.localPosition = _hingeRestPosition + root.InverseTransformVector(up);
            if (_base != null)
            {
                Vector3 lift = _base.transform.InverseTransformVector(up);
                if (_carriage != null)
                    _carriage.localPosition = lift;
                if (_lever != null)
                    _lever.SetLocalPositionAndRotation(mount.LeverPivot + lift, Quaternion.AngleAxis(Mathf.SmoothStep(0f, 1f, _leverOpen) * mount.LeverAngle, mount.LeverAxis.normalized));
                if (_tiltScrew != null)
                    _tiltScrew.localPosition = mount.TiltScrewPivot + lift;
            }

            float tilt = 0f, slide = 0f;
            if (_goggleShoe != null && _goggle != null)
            {
                tilt = Mathf.Clamp(MountTilt, mount.MinTilt, mount.MaxTilt);
                slide = mount.Slots.Length > 0
                    ? Mathf.Clamp(MountSlide, SlotOffset(mount, mount.Slots.Length - 1), SlotOffset(mount, 0))
                    : Mathf.Clamp(MountSlide, -mount.SlideBack, mount.SlideForward);
                if (_button != null)
                    _button.localRotation = Quaternion.AngleAxis(Mathf.SmoothStep(0f, 1f, _buttonPress) * mount.ButtonAngle, mount.ButtonAxis);
                hinge.localRotation = _hingeRestRotation * Quaternion.AngleAxis(tilt * _tiltSign, Vector3.right);
                if (_tiltScrew != null)
                    _tiltScrew.localRotation = Quaternion.AngleAxis(tilt / mount.TiltPerTurn * 360f, mount.TiltScrewAxis);
                Vector3 along = _goggle.transform.localRotation * mount.Slot.normalized * slide;
                for (int i = 0; i < _hingeChildren.Length; i++)
                    _hingeChildren[i].localPosition = _hingeChildRest[i] + along;
                _goggleShoe.localPosition = -mount.Slot.normalized * slide;
            }
        }

        // An L4 G24: everything but the plate up or down, the lever swung over as far as it is open; the hinge turned by
        // the tilt, the knob at its end with it, the lock knob as loose as it is; the shoe, the goggle in it, along the arm
        // (forward toward its end: with the goggle down the arm reaches forward from the hinge), its buttons as pressed.
        private void AdjustL4(Mount mount, Transform hinge, Transform shoe)
        {
            _mountMoved = true;
            _hingeRestPosition = hinge.localPosition;
            _hingeRestRotation = hinge.localRotation;
            _shoeRestPosition = shoe.localPosition;
            _shoeRestRotation = shoe.localRotation;

            Transform root = hinge.parent;
            float height = _carriage != null ? Mathf.Clamp(MountHeight, mount.HeightMin - _modelLift, mount.HeightMax - _modelLift) : 0f;
            float tilt = Mathf.Clamp(MountTilt, mount.MinTilt, mount.MaxTilt);
            Vector3 up = root.up * height;
            hinge.localPosition = _hingeRestPosition + root.InverseTransformVector(up);
            hinge.localRotation = _hingeRestRotation * Quaternion.AngleAxis(tilt * _tiltSign, Vector3.right);
            if (_base != null)
            {
                Vector3 lift = _base.transform.InverseTransformVector(up);
                if (_carriage != null)
                    _carriage.localPosition = lift;
                if (_lever != null)
                    _lever.SetLocalPositionAndRotation(_leverPivot + lift, Quaternion.AngleAxis(Mathf.SmoothStep(0f, 1f, _leverOpen) * mount.LeverAngle, mount.LeverAxis));
                if (_tiltScrew != null)
                    _tiltScrew.SetLocalPositionAndRotation(_tiltScrewPivot + lift, Quaternion.AngleAxis(tilt / mount.TiltPerTurn * 360f, mount.TiltScrewAxis));
                if (_lockKnob != null)
                {
                    // Loosened anticlockwise as seen from outside, backing out along its thread.
                    float turns = Mathf.SmoothStep(0f, 1f, _lockLoose) * mount.LockTurns;
                    _lockKnob.SetLocalPositionAndRotation(_lockPivot + lift + mount.TiltScrewAxis * (turns * mount.LockLead), Quaternion.AngleAxis(turns * 360f, mount.TiltScrewAxis));
                }
            }

            if (_l4Shoe != null && _armFilter != null)
            {
                Vector3 along = Vector3.down * Mathf.Clamp(MountSlide, -mount.SlideBack - _modelSlide, mount.SlideForward - _modelSlide);
                float press = Mathf.SmoothStep(0f, 1f, _buttonPress) * mount.ButtonDepth;
                _l4Shoe.localPosition = along;
                if (_buttonLeft != null)
                    _buttonLeft.localPosition = along + Vector3.right * press;
                if (_buttonRight != null)
                    _buttonRight.localPosition = along - Vector3.right * press;
                shoe.localPosition = _shoeRestPosition + hinge.InverseTransformVector(_armFilter.transform.TransformVector(along));
            }
        }

        private void Restore()
        {
            if (_mount != null && _mount.Joints)
            {
                RestoreJoints();
                return;
            }
            if (!_mountMoved)
                return;
            _mountMoved = false;
            if (_mountShoe != null)
            {
                _mountShoe.localPosition = _shoeRestPosition;
                _mountShoe.localRotation = _shoeRestRotation;
            }
            if (_mountHinge != null)
            {
                _mountHinge.localPosition = _hingeRestPosition;
                if (_mount != null && (_mount.G24 || _mount.L4))
                {
                    _mountHinge.localRotation = _hingeRestRotation;
                    for (int i = 0; i < _hingeChildren.Length; i++)
                    {
                        if (_hingeChildren[i] != null)
                            _hingeChildren[i].localPosition = _hingeChildRest[i];
                    }
                }
            }
            if (_lever != null)
                _lever.SetLocalPositionAndRotation(_leverPivot, Quaternion.identity);
            if (_tiltScrew != null)
                _tiltScrew.SetLocalPositionAndRotation(_tiltScrewPivot, Quaternion.identity);
            if (_lockKnob != null)
                _lockKnob.SetLocalPositionAndRotation(_lockPivot, Quaternion.identity);
            if (_l4Shoe != null)
                _l4Shoe.localPosition = Vector3.zero;
            if (_buttonLeft != null)
                _buttonLeft.localPosition = Vector3.zero;
            if (_buttonRight != null)
                _buttonRight.localPosition = Vector3.zero;
            if (_goggleShoe != null)
                _goggleShoe.localPosition = Vector3.zero;
            if (_button != null)
                _button.localRotation = Quaternion.identity;
            if (_carriage != null)
                _carriage.localPosition = Vector3.zero;
            if (_knob != null)
                _knob.localRotation = Quaternion.identity;
            if (_arm != null)
                _arm.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (_slide != null)
                _slide.SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (_screw != null && _mount != null)
                _screw.SetLocalPositionAndRotation(_mount.Screw, Quaternion.identity);
        }

        // The mount whole again, as the model has it.
        internal void Release()
        {
            Restore();
            if (_base != null && _baseMesh != null)
                _base.sharedMesh = _baseMesh;
            if (_armFilter != null && _armMesh != null)
                _armFilter.sharedMesh = _armMesh;
            if (_goggle != null && _goggleMesh != null)
                _goggle.sharedMesh = _goggleMesh;
            if (_lodGroup != null && _lods != null)
                _lodGroup.SetLODs(_lods);
            foreach ((MeshRenderer piece, MeshRenderer _) in _pieceRenderers)
            {
                if (piece != null)
                    Object.Destroy(piece.gameObject);
            }
            foreach (Mesh mesh in _pieces)
                Object.Destroy(mesh);
            _pieces.Clear();
            _pieceRenderers.Clear();
            _mount = null;
            _mountId = null;
            MountHeight = MountTilt = MountSlide = 0f;
            _mountHinge = _mountShoe = _carriage = _knob = _arm = _slide = _screw = _lever = _tiltScrew = _goggleShoe = _button = null;
            _lockKnob = _l4Shoe = _buttonLeft = _buttonRight = null;
            _jointScrew = _jointTilt = _jointSlide = null;
            _lockLoose = 0f;
            _modelLift = 0f;
            _modelSlide = 0f;
            _base = _armFilter = _goggle = null;
            _baseMesh = _armMesh = _goggleMesh = null;
            _hingeChildren = System.Array.Empty<Transform>();
            _hingeChildRest = System.Array.Empty<Vector3>();
            _leverOpen = 0f;
            _buttonPress = 0f;
            _slot = -1;
            _lodGroup = null;
            _lods = null;
        }

        // The pieces drawn as the meshes they came from are: the game hides and shows those (first person, shadows only).
        internal void SyncPieces()
        {
            foreach ((MeshRenderer piece, MeshRenderer source) in _pieceRenderers)
            {
                if (piece == null || source == null)
                    continue;
                piece.enabled = source.enabled;
                piece.forceRenderingOff = source.forceRenderingOff;
                piece.shadowCastingMode = source.shadowCastingMode;
                piece.gameObject.layer = source.gameObject.layer;
            }
        }

        // The base in its pieces: the carriage is the piece the hinge sits in, with whatever small piece touches it and
        // lies within its height (the paddle on its side); the knob is the highest of the rest.
        private void SplitBase(Transform root, Transform hinge, Mount mount)
        {
            MeshFilter? filter = null;
            foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.name == mount.Base && !f.transform.IsChildOf(hinge))
                    filter = f;
            }
            if (filter == null || !Read(filter, out Triangles mesh))
                return;
            Transform frame = filter.transform;
            Vector3 up = frame.InverseTransformDirection(root.up).normalized;
            int carriage = Containing(mesh, frame.InverseTransformPoint(hinge.position));
            if (carriage < 0)
                return;
            var role = new int[mesh.Bounds.Length];      // 0 the base, 1 the carriage, 2 the knob
            role[carriage] = 1;
            (float low, float high) span = Span(mesh.Bounds[carriage], up);
            Bounds near = mesh.Bounds[carriage];
            near.Expand(PieceGap * 2f);
            int knob = -1;
            for (int p = 0; p < role.Length; p++)
            {
                if (p == carriage)
                    continue;
                (float low, float high) s = Span(mesh.Bounds[p], up);
                if (near.Intersects(mesh.Bounds[p]) && s.low >= span.low - PieceGap && s.high <= span.high + PieceGap)
                    role[p] = 1;
                else if (knob < 0 || s.high > Span(mesh.Bounds[knob], up).high)
                    knob = p;
            }
            if (knob < 0)
                return;
            role[knob] = 2;

            // The knob turns about its thinnest direction, through its middle.
            Vector3 centre = Vector3.zero;
            int knobVertices = 0;
            for (int i = 0; i < mesh.Pos.Length; i++)
            {
                if (mesh.Piece[i / 3] != knob)
                    continue;
                centre += mesh.Pos[i];
                knobVertices++;
            }
            centre /= knobVertices;
            Vector3 axis = Thinnest(mesh.Pos, mesh.Piece, knob, centre);
            _knobPivot = centre;
            _knobAxis = Vector3.Dot(axis, up) < 0f ? -axis : axis;

            Transform[] pieces = Split(filter, mesh, ByTriangle(mesh, role), new[] { Vector3.zero, Vector3.zero, centre }, new[] { "", "Justachae TATM carriage", "Justachae TATM knob" });
            _base = filter;
            _baseMesh = mesh.Original;
            _carriage = pieces[1];
            _knob = pieces[2];
        }

        // The arm in its pieces: the link is the piece the hinge's pivot sits in, with the small pieces in it (the pins);
        // the slide is the piece the shoe sits in; the rest is the slotted arm.
        private void SplitArm(Transform hinge, Transform shoe, Mount mount)
        {
            MeshFilter? filter = null;
            foreach (MeshFilter f in hinge.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.name == mount.Arm && !shoe.IsChildOf(f.transform))
                    filter = f;
            }
            if (filter == null || !Read(filter, out Triangles mesh))
                return;
            Transform frame = filter.transform;
            int link = Containing(mesh, frame.InverseTransformPoint(hinge.position));
            int slide = Containing(mesh, frame.InverseTransformPoint(shoe.position));
            if (link < 0 || slide < 0 || link == slide)
                return;
            var role = new int[mesh.Bounds.Length];      // 0 the link, 1 the arm, 2 the slide
            Bounds near = mesh.Bounds[link];
            near.Expand(PieceGap * 2f);
            for (int p = 0; p < role.Length; p++)
            {
                if (p == link)
                    role[p] = 0;
                else if (p == slide)
                    role[p] = 2;
                else
                    role[p] = near.Intersects(mesh.Bounds[p]) && Volume(mesh.Bounds[p]) < Volume(mesh.Bounds[link]) * 0.1f ? 0 : 1;
            }

            // The screw's knob: what of the slide lies wholly past ScrewFrom along the screw's axis, and as wide as that about the axis
            // what crosses ScrewFrom too. Those left on the slide stood still as the knob turned, and holes opened between them.
            int[] triangles = ByTriangle(mesh, role);
            float reach = 0f;
            for (int t = 0; t < triangles.Length; t++)
            {
                if (triangles[t] != 2)
                    continue;
                bool past = true;
                for (int j = 0; j < 3; j++)
                    past &= Vector3.Dot(mesh.Pos[t * 3 + j], mount.ScrewAxis) >= mount.ScrewFrom;
                if (!past)
                    continue;
                triangles[t] = 3;
                for (int j = 0; j < 3; j++)
                    reach = Mathf.Max(reach, Vector3.ProjectOnPlane(mesh.Pos[t * 3 + j] - mount.Screw, mount.ScrewAxis).magnitude);
            }
            for (int t = 0; t < triangles.Length; t++)
            {
                if (triangles[t] != 2)
                    continue;
                bool crosses = false, within = true;
                for (int j = 0; j < 3; j++)
                {
                    crosses |= Vector3.Dot(mesh.Pos[t * 3 + j], mount.ScrewAxis) >= mount.ScrewFrom;
                    within &= Vector3.ProjectOnPlane(mesh.Pos[t * 3 + j] - mount.Screw, mount.ScrewAxis).magnitude <= reach + PieceGap;
                }
                if (crosses && within)
                    triangles[t] = 3;
            }
            Transform[] pieces = Split(filter, mesh, triangles, new[] { Vector3.zero, Vector3.zero, Vector3.zero, mount.Screw },
                new[] { "", "Justachae TATM arm", "Justachae TATM slide", "Justachae TATM slide screw" });
            _armFilter = filter;
            _armMesh = mesh.Original;
            _arm = pieces[1];
            _slide = pieces[2];
            _screw = pieces[3];

            // Tilting up raises the eyepieces.
            _tiltSign = TiltSign(frame.TransformDirection(mount.TiltAxis), hinge.parent.up);
        }

        // A G24 in its pieces. The base: the plate (what of the carriage's piece lies wholly past a plate plane), the
        // carriage (the rest of that piece, and every smaller piece that touches it, but the lever and the screw), the lever
        // and the tilt screw; what touches none of these (the battery pack and its cable) stays. The goggle: the shoe (the
        // piece the hinge sits in, with whatever lies within it) apart from the rest.
        private void SplitG24(Transform root, Transform hinge, Mount mount)
        {
            MeshFilter? baseFilter = null;
            foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.name == mount.Base && !f.transform.IsChildOf(hinge))
                    baseFilter = f;
            }
            if (baseFilter != null && Read(baseFilter, out Triangles mesh))
            {
                int carriage = Containing(mesh, mount.CarriagePoint), lever = Containing(mesh, mount.LeverPoint), screw = Containing(mesh, mount.TiltScrewPoint);
                if (carriage >= 0 && lever >= 0 && screw >= 0 && carriage != lever && carriage != screw)
                {
                    Bounds near = mesh.Bounds[carriage];
                    near.Expand(PieceGap * 2f);
                    var role = new int[mesh.Bounds.Length];        // 0 stays, 1 the carriage, 2 the lever, 3 the screw
                    for (int p = 0; p < role.Length; p++)
                    {
                        role[p] = p == lever ? 2 : p == screw ? 3
                            : p == carriage || near.Intersects(mesh.Bounds[p]) && Volume(mesh.Bounds[p]) < Volume(mesh.Bounds[carriage]) * 0.3f ? 1 : 0;
                    }
                    int[] triangles = ByTriangle(mesh, role);
                    for (int t = 0; t < triangles.Length; t++)
                    {
                        if (mesh.Piece[t] != carriage)
                            continue;
                        foreach (Vector4 plane in mount.PlatePlanes)
                        {
                            var n = (Vector3)plane;
                            if (Vector3.Dot(mesh.Pos[t * 3], n) > plane.w && Vector3.Dot(mesh.Pos[t * 3 + 1], n) > plane.w && Vector3.Dot(mesh.Pos[t * 3 + 2], n) > plane.w)
                                triangles[t] = 0;
                        }
                    }
                    Transform[] pieces = Split(baseFilter, mesh, triangles, new[] { Vector3.zero, Vector3.zero, mount.LeverPivot, mount.TiltScrewPivot },
                        new[] { "", "Justachae G24 carriage", "Justachae G24 lever", "Justachae G24 tilt screw" });
                    _base = baseFilter;
                    _baseMesh = mesh.Original;
                    _carriage = pieces[1];
                    _lever = pieces[2];
                    _tiltScrew = pieces[3];
                    _leverPivot = mount.LeverPivot;
                    _tiltScrewPivot = mount.TiltScrewPivot;
                }
            }

            MeshFilter? goggle = _device.GetComponent<MeshFilter>();
            if (goggle == null || !Read(goggle, out Triangles device))
                return;
            Transform frame = goggle.transform;
            int shoe = Containing(device, frame.InverseTransformPoint(hinge.position));
            if (shoe < 0)
                return;
            Bounds within = device.Bounds[shoe];
            within.Expand(PieceGap * 2f);
            int button = mount.Slots.Length > 0 ? Containing(device, mount.ButtonPoint) : -1;
            var parts = new int[device.Bounds.Length];          // 0 the goggle, 1 the shoe, 2 the release button
            for (int p = 0; p < parts.Length; p++)
                parts[p] = p == button ? 2 : p == shoe || within.Contains(device.Bounds[p].min) && within.Contains(device.Bounds[p].max) ? 1 : 0;
            int[] goggleParts = ByTriangle(device, parts);
            if (button >= 0)
            {
                // The arch base the button's bolt runs through stays (Mount.ButtonBaseLeft).
                for (int t = 0; t < goggleParts.Length; t++)
                {
                    if (goggleParts[t] != 2)
                        continue;
                    Vector3 a = device.Pos[t * 3], b = device.Pos[t * 3 + 1], c = device.Pos[t * 3 + 2];
                    Vector3 normal = Vector3.Cross(b - a, c - a).normalized;
                    bool inWidth = true, inArch = true, below = true;
                    foreach (Vector3 v in new[] { a, b, c })
                    {
                        inWidth &= v.x >= mount.ButtonBaseLeft && v.x <= mount.ButtonBaseRight;
                        inArch &= v.y <= mount.ButtonArchInner.x || v.y >= mount.ButtonArchInner.y;
                        below &= v.z <= mount.ButtonPivot.z;
                    }
                    bool square = Mathf.Max(Mathf.Abs(normal.x), Mathf.Abs(normal.y), Mathf.Abs(normal.z)) > 0.99f;
                    if (inWidth && (inArch || below && square))
                        goggleParts[t] = 0;
                }
            }
            Transform[] shoePieces = Split(goggle, device, goggleParts, new[] { Vector3.zero, Vector3.zero, mount.ButtonPivot },
                new[] { "", "Justachae G24 shoe", "Justachae G24 release button" });
            _goggle = goggle;
            _goggleMesh = device.Original;
            _goggleShoe = shoePieces[1];
            _button = button >= 0 ? shoePieces[2] : null;

            // Tilting up raises the eyepieces.
            _tiltSign = TiltSign(hinge.right, root.up);
        }

        // An L4 G24 in its pieces. The base: the carriage (its piece, less the lever, and the lock knob's washer), the lever,
        // the tilt knob and the lock knob, all found where the coyote model has them, raised as far as this one has its
        // carriage; the plate stays. The arm: the shoe (the largest piece the goggle's place is in, with whatever lies
        // within it) and its two buttons, apart from the arm.
        private void SplitL4(Transform root, Transform hinge, Transform shoe, Mount mount)
        {
            MeshFilter? baseFilter = null, armFilter = null;
            foreach (MeshFilter f in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.name == mount.Base && !f.transform.IsChildOf(hinge))
                    baseFilter = f;
                else if (f.name == mount.Arm && f.transform.IsChildOf(hinge))
                    armFilter = f;
            }
            if (baseFilter != null && Read(baseFilter, out Triangles mesh))
            {
                int carriage = Containing(mesh, mount.CarriagePoint);
                float lift = carriage >= 0 ? mesh.Bounds[carriage].min.y - mount.CarriageLow : 0f;
                var up = new Vector3(0f, lift, 0f);
                int knob = Containing(mesh, mount.TiltScrewPoint + up), washer = Containing(mesh, mount.WasherPoint + up), lockKnob = Containing(mesh, mount.LockPoint + up);
                if (carriage >= 0 && knob >= 0 && washer >= 0 && lockKnob >= 0 && knob != carriage && lockKnob != carriage && lockKnob != washer)
                {
                    var role = new int[mesh.Bounds.Length];        // 0 stays, 1 the carriage, 2 the lever, 3 the tilt knob, 4 the lock knob
                    role[carriage] = role[washer] = 1;
                    role[knob] = 3;
                    role[lockKnob] = 4;
                    int[] triangles = ByTriangle(mesh, role);
                    for (int t = 0; t < triangles.Length; t++)
                    {
                        if (mesh.Piece[t] != carriage)
                            continue;
                        bool within = true;
                        float low = float.MaxValue, high = float.MinValue;
                        for (int j = 0; j < 3; j++)
                        {
                            Vector3 v = mesh.Pos[t * 3 + j] - up;
                            bool disc = new Vector2(v.x - mount.LeverCentre.x, v.y - mount.LeverCentre.y).magnitude < mount.LeverRadius;
                            bool handle = v.x >= mount.LeverHandleMin.x && v.x <= mount.LeverHandleMax.x && v.y >= mount.LeverHandleMin.y && v.y <= mount.LeverHandleMax.y;
                            within &= disc || handle;
                            low = Mathf.Min(low, v.z);
                            high = Mathf.Max(high, v.z);
                        }
                        if (within && low > mount.LeverFloor && high > mount.LeverFace)
                            triangles[t] = 2;
                    }
                    _leverPivot = mount.LeverCentre + up;
                    _tiltScrewPivot = mesh.Bounds[knob].center;
                    _lockPivot = new Vector3(mesh.Bounds[lockKnob].center.x, mesh.Bounds[washer].center.y, mesh.Bounds[washer].center.z);
                    Transform[] pieces = Split(baseFilter, mesh, triangles, new[] { Vector3.zero, Vector3.zero, _leverPivot, _tiltScrewPivot, _lockPivot },
                        new[] { "", "Justachae L4 G24 carriage", "Justachae L4 G24 lever", "Justachae L4 G24 tilt knob", "Justachae L4 G24 lock knob" });
                    _base = baseFilter;
                    _baseMesh = mesh.Original;
                    _carriage = pieces[1];
                    _lever = pieces[2];
                    _tiltScrew = pieces[3];
                    _lockKnob = pieces[4];
                    _modelLift = lift;
                }
            }

            if (armFilter != null && Read(armFilter, out Triangles arm))
            {
                Vector3 at = armFilter.transform.InverseTransformPoint(shoe.position);
                int shoePiece = -1;
                for (int p = 0; p < arm.Bounds.Length; p++)
                {
                    if (arm.Bounds[p].Contains(at) && (shoePiece < 0 || Volume(arm.Bounds[p]) > Volume(arm.Bounds[shoePiece])))
                        shoePiece = p;
                }
                // A model may have the shoe elsewhere along its track (the black one further back): forward is down the
                // arm mesh's y.
                float shift = shoePiece >= 0 ? arm.Bounds[shoePiece].min.y - mount.ShoeLow : 0f;
                var back = new Vector3(0f, shift, 0f);
                int left = Containing(arm, mount.ButtonLeftPoint + back), right = Containing(arm, mount.ButtonRightPoint + back);
                if (shoePiece >= 0 && left >= 0 && right >= 0 && left != shoePiece && right != shoePiece && left != right)
                {
                    Bounds within = arm.Bounds[shoePiece];
                    within.Expand(PieceGap * 2f);
                    var parts = new int[arm.Bounds.Length];        // 0 the arm, 1 the shoe, 2 the left button, 3 the right
                    for (int p = 0; p < parts.Length; p++)
                        parts[p] = p == left ? 2 : p == right ? 3 : p == shoePiece || within.Contains(arm.Bounds[p].min) && within.Contains(arm.Bounds[p].max) ? 1 : 0;
                    Transform[] pieces = Split(armFilter, arm, ByTriangle(arm, parts), new[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero },
                        new[] { "", "Justachae L4 G24 shoe", "Justachae L4 G24 left button", "Justachae L4 G24 right button" });
                    _armFilter = armFilter;
                    _armMesh = arm.Original;
                    _l4Shoe = pieces[1];
                    _buttonLeft = pieces[2];
                    _buttonRight = pieces[3];
                    _modelSlide = -shift;
                }
            }

            // Tilting up raises the eyepieces.
            _tiltSign = TiltSign(hinge.right, root.up);
        }

        // Which way about axis tilts the eyepieces up: they hang in front of the mount once down, whether or not the device
        // is down as the mount is split (flipped up, they sit behind its hinge).
        private float TiltSign(Vector3 axis, Vector3 up)
        {
            Vector3 forward = _facing != null ? _facing.forward : _mountHinge != null ? _mountHinge.forward : Vector3.forward;
            return Vector3.Dot(Vector3.Cross(axis, forward), up) >= 0f ? 1f : -1f;
        }

        // The filter keeps the triangles of role 0; each other role becomes a child of it, its mesh about its pivot, drawn
        // with the filter's material and level of detail.
        private Transform[] Split(MeshFilter filter, Triangles mesh, int[] role, Vector3[] pivots, string[] names)
        {
            var pieces = new Transform[pivots.Length];
            for (int r = 0; r < pivots.Length; r++)
            {
                var p = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<Vector4>();
                var u = new List<Vector2>();
                int subs = mesh.Original.subMeshCount;
                var index = new List<int>[subs];
                for (int s = 0; s < subs; s++)
                    index[s] = new List<int>();
                for (int i = 0; i < mesh.Pos.Length; i++)
                {
                    if (role[i / 3] != r)
                        continue;
                    index[mesh.Sub[i / 3]].Add(p.Count);
                    p.Add(mesh.Pos[i] - pivots[r]);
                    n.Add(mesh.Normal[i]);
                    t.Add(mesh.Tangent[i]);
                    u.Add(mesh.Uv[i]);
                }
                var part = new Mesh { name = mesh.Original.name + (r == 0 ? " (Justachae)" : " (" + names[r] + ")") };
                part.SetVertices(p);
                part.SetNormals(n);
                part.SetTangents(t);
                part.SetUVs(0, u);
                part.subMeshCount = subs;
                for (int s = 0; s < subs; s++)
                    part.SetTriangles(index[s], s);
                part.RecalculateBounds();
                part.UploadMeshData(true);
                _pieces.Add(part);
                if (r == 0)
                    filter.sharedMesh = part;
                else
                    pieces[r] = Piece(filter.transform, mesh.Renderer, part, names[r], pivots[r]);
            }

            if (_lodGroup != null)
            {
                LOD[] lods = _lodGroup.GetLODs();
                for (int l = 0; l < lods.Length; l++)
                {
                    if (System.Array.IndexOf(lods[l].renderers, mesh.Renderer) < 0)
                        continue;
                    var renderers = new List<Renderer>(lods[l].renderers);
                    for (int r = 1; r < pieces.Length; r++)
                        renderers.Add(pieces[r].GetComponent<MeshRenderer>());
                    lods[l].renderers = renderers.ToArray();
                }
                _lodGroup.SetLODs(lods);
            }
            if (Worn)
                _blockersOf = null;    // the pieces go with the device's renderers and blockers
            return pieces;
        }

        private Transform Piece(Transform parent, MeshRenderer source, Mesh mesh, string name, Vector3 position)
        {
            var piece = new GameObject(name) { layer = source.gameObject.layer };
            piece.transform.SetParent(parent, false);
            piece.transform.localPosition = position;
            piece.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = piece.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.shadowCastingMode = source.shadowCastingMode;
            renderer.receiveShadows = source.receiveShadows;
            renderer.lightProbeUsage = source.lightProbeUsage;
            renderer.reflectionProbeUsage = source.reflectionProbeUsage;
            renderer.probeAnchor = source.probeAnchor;
            renderer.renderingLayerMask = source.renderingLayerMask;
            _pieceRenderers.Add((renderer, source));
            return piece.transform;
        }
    }

    private sealed class Triangles
    {
        internal Mesh Original = null!;
        internal MeshRenderer Renderer = null!;
        internal Vector3[] Pos = null!, Normal = null!;
        internal Vector4[] Tangent = null!;
        internal Vector2[] Uv = null!;
        internal int[] Piece = null!;               // each triangle's
        internal int[] Sub = null!;                 // each triangle's submesh
        internal Bounds[] Bounds = null!;           // each piece's
    }

    private static bool Read(MeshFilter filter, out Triangles mesh)
    {
        mesh = new Triangles();
        if (filter.sharedMesh == null || !filter.TryGetComponent(out MeshRenderer renderer))
            return false;
        mesh.Renderer = renderer;
        mesh.Original = filter.sharedMesh;
        var pos = new List<Vector3>();
        var normal = new List<Vector3>();
        var tangent = new List<Vector4>();
        var uv = new List<Vector2>();
        var sub = new List<int>();
        for (int s = 0; s < mesh.Original.subMeshCount; s++)
        {
            if (!ReadTriangles(mesh.Original, s, out Vector3[] p, out Vector3[] n, out Vector4[] t, out Vector2[] u))
                return false;
            pos.AddRange(p);
            normal.AddRange(n);
            tangent.AddRange(t);
            uv.AddRange(u);
            for (int i = 0; i < p.Length / 3; i++)
                sub.Add(s);
        }
        mesh.Pos = pos.ToArray();
        mesh.Normal = normal.ToArray();
        mesh.Tangent = tangent.ToArray();
        mesh.Uv = uv.ToArray();
        mesh.Sub = sub.ToArray();
        mesh.Piece = Pieces(mesh.Pos);
        int count = 0;
        foreach (int p in mesh.Piece)
            count = Mathf.Max(count, p + 1);
        mesh.Bounds = new Bounds[count];
        var seen = new bool[count];
        for (int i = 0; i < mesh.Pos.Length; i++)
        {
            int p = mesh.Piece[i / 3];
            if (seen[p])
                mesh.Bounds[p].Encapsulate(mesh.Pos[i]);
            else
                mesh.Bounds[p] = new Bounds(mesh.Pos[i], Vector3.zero);
            seen[p] = true;
        }
        return true;
    }

    private static int[] ByTriangle(Triangles mesh, int[] role)
    {
        var triangles = new int[mesh.Piece.Length];
        for (int t = 0; t < triangles.Length; t++)
            triangles[t] = role[mesh.Piece[t]];
        return triangles;
    }

    // The smallest piece whose bounds hold the point, or -1.
    private static int Containing(Triangles mesh, Vector3 point)
    {
        int found = -1;
        for (int p = 0; p < mesh.Bounds.Length; p++)
        {
            if (mesh.Bounds[p].Contains(point) && (found < 0 || Volume(mesh.Bounds[p]) < Volume(mesh.Bounds[found])))
                found = p;
        }
        return found;
    }


    // Each triangle's piece: triangles sharing a vertex position belong together.
    private static int[] Pieces(Vector3[] pos)
    {
        var ids = new Dictionary<Vector3Int, int>();
        var vertex = new int[pos.Length];
        for (int i = 0; i < pos.Length; i++)
        {
            var key = Vector3Int.RoundToInt(pos[i] * 1e5f);
            if (!ids.TryGetValue(key, out vertex[i]))
                ids[key] = vertex[i] = ids.Count;
        }
        var parent = new int[ids.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;
        int Find(int a)
        {
            while (parent[a] != a)
                a = parent[a] = parent[parent[a]];
            return a;
        }
        for (int i = 0; i < pos.Length; i += 3)
        {
            parent[Find(vertex[i])] = Find(vertex[i + 1]);
            parent[Find(vertex[i + 1])] = Find(vertex[i + 2]);
        }
        var pieces = new Dictionary<int, int>();
        var piece = new int[pos.Length / 3];
        for (int i = 0; i < piece.Length; i++)
        {
            int r = Find(vertex[i * 3]);
            if (!pieces.TryGetValue(r, out piece[i]))
                pieces[r] = piece[i] = pieces.Count;
        }
        return piece;
    }

    private static (float low, float high) Span(Bounds b, Vector3 up)
    {
        float low = float.MaxValue, high = float.MinValue;
        for (int c = 0; c < 8; c++)
        {
            Vector3 corner = b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
            float h = Vector3.Dot(corner, up);
            low = Mathf.Min(low, h);
            high = Mathf.Max(high, h);
        }
        return (low, high);
    }

    private static float Volume(Bounds b) => b.size.x * b.size.y * b.size.z;

    // The direction a piece is thinnest along: the least principal axis of its vertices, by power iteration on the
    // spread turned about (trace - covariance), whose largest axis that is.
    private static Vector3 Thinnest(Vector3[] pos, int[] piece, int which, Vector3 centre)
    {
        float xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
        for (int i = 0; i < pos.Length; i++)
        {
            if (piece[i / 3] != which)
                continue;
            Vector3 d = pos[i] - centre;
            xx += d.x * d.x; yy += d.y * d.y; zz += d.z * d.z;
            xy += d.x * d.y; xz += d.x * d.z; yz += d.y * d.z;
        }
        float trace = xx + yy + zz;
        Vector3 v = new Vector3(0.31f, 0.53f, 0.79f);
        for (int k = 0; k < 64; k++)
        {
            v = new Vector3(
                (trace - xx) * v.x - xy * v.y - xz * v.z,
                -xy * v.x + (trace - yy) * v.y - yz * v.z,
                -xz * v.x - yz * v.y + (trace - zz) * v.z).normalized;
        }
        return v;
    }

    // A submesh's triangles, three vertices each, read back through the GPU (a mesh the game loads is not readable on the
    // CPU): MeshRead draws each vertex to its own pixel of four float targets.
    private static bool ReadTriangles(Mesh mesh, int submesh, out Vector3[] pos, out Vector3[] normal, out Vector4[] tangent, out Vector2[] uv)
    {
        pos = normal = System.Array.Empty<Vector3>();
        tangent = System.Array.Empty<Vector4>();
        uv = System.Array.Empty<Vector2>();
        if (!LoadShaders() || _meshReadShader == null || mesh.GetTopology(submesh) != MeshTopology.Triangles)
            return false;
        int vertices = (int)mesh.GetIndexCount(submesh);
        const int width = 256;
        int height = (vertices + width - 1) / width;
        var material = new Material(_meshReadShader) { hideFlags = HideFlags.DontSave };
        material.SetFloat("_Width", width);
        material.SetFloat("_Height", height);
        var targets = new RenderTexture[4];
        var ids = new RenderTargetIdentifier[4];
        for (int i = 0; i < 4; i++)
        {
            targets[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat) { filterMode = FilterMode.Point };
            targets[i].Create();
            ids[i] = targets[i];
        }
        var command = new CommandBuffer { name = "Justachae mesh read" };
        command.SetRenderTarget(ids, ids[0]);
        command.ClearRenderTarget(false, true, Color.clear);
        command.DrawMesh(mesh, Matrix4x4.identity, material, submesh, 0);
        Graphics.ExecuteCommandBuffer(command);
        command.Release();

        var data = new Color[4][];
        RenderTexture previous = RenderTexture.active;
        var read = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
        for (int i = 0; i < 4; i++)
        {
            RenderTexture.active = targets[i];
            read.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            data[i] = read.GetPixels();
        }
        RenderTexture.active = previous;
        Object.Destroy(read);
        Object.Destroy(material);
        foreach (RenderTexture target in targets)
        {
            target.Release();
            Object.Destroy(target);
        }

        pos = new Vector3[vertices];
        normal = new Vector3[vertices];
        tangent = new Vector4[vertices];
        uv = new Vector2[vertices];
        var got = new bool[vertices];
        for (int px = 0; px < data[0].Length; px++)
        {
            int k = Mathf.RoundToInt(data[0][px].a) - 1;
            if (k < 0 || k >= vertices)
                continue;
            Color p = data[0][px], n = data[1][px], t = data[2][px], u = data[3][px];
            pos[k] = new Vector3(p.r, p.g, p.b);
            normal[k] = new Vector3(n.r, n.g, n.b);
            tangent[k] = new Vector4(t.r, t.g, t.b, t.a);
            uv[k] = new Vector2(u.r, u.g);
            got[k] = true;
        }
        return System.Array.TrueForAll(got, g => g);
    }
}
