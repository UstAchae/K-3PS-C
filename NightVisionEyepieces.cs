using System;
using System.IO;
using System.Reflection;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The picture of the player's own head-mounted night vision, shown inside the device's eyepieces instead of over
// the whole screen. The picture is BRNVG's own, made where it always makes it, over the main camera's frame: this
// mod only places it. Just before BRNVG draws, its renderer is set to an open lens with no mask, no tube layout,
// vignette or edge distortion (the eyepieces here are the aperture); what it makes goes into the lenses and the
// frame it was given stays as the naked eye's scene around them. The tubes are 1x and afocal, so every line of
// sight through a lens shows the picture in that same direction. Which lens each pixel looks through comes from a
// lens map: the device's own meshes drawn from the eye, with the round lenses its texture paints on the eyepieces'
// floors (the GPNVG-18's two to an eyecup, making its "8"). While it shows, the game's own filter adds nothing.
// Needs BRNVG; the devices whose eyepieces have been measured are listed in Devices.
//
// The eyepieces light up when the device is switched on, the moment the game lights its green glow, and go dark
// when it is switched off, as the device flips; BRNVG's renderer is woken for that while the game's filter still
// waits for the flip to end. The glow itself stays off. Like real tubes they strike rather than fade in: switched
// on, a tube's output jumps at once to about half, flickering and mottled, and reaches full within a moment;
// switched off, it goes out with only a brief phosphor afterglow. The four do not quite keep step: each switch
// gives each tube its own small delay.
//
// The device itself is drawn by the main camera, as the game has it.

internal static partial class NightVisionEyepieces
{
    internal static bool EyepieceGlow = true;   // the lenses' light spread over the glass and eyecups (F12)
    internal static float EyeShift = 0.8f;      // how far each half of the device comes in, as a share of the way from its centre lens
    internal static bool LensReflections = true;    // the device's glass drawn at the eyes, with what it reflects (F12)
    internal static float MonocularSeeThrough = 0.5f;   // a monocular to the other eye: 0 not faded, 0.5 as it was, 1 faded fastest (F12)

    internal static bool Active { get; private set; }
    internal static bool ThermalShown => Active && _device.Thermal;

    private struct Tube
    {
        internal Vector3 Centre;   // on the lens's axis, where its eyecup's opening is narrowest
        internal Vector3 Normal;   // the axis, toward the eye; the tube looks along its opposite
        internal Vector3 Lens;     // the lens's centre on the floor, where it is not the device's FloorDepth under the Centre
        internal float LensRadius; // that lens's radius (0: the device's, under the Centre)
    }

    // One head-mounted night-vision model's eyepieces, measured from the game's model in its LOD0 mesh's frame
    // (metres; for every one the eye side is +Y). Each lens is a circle painted on the floor of its eyepiece; the
    // eyecup's opening narrows the view of it, at the Centre's level and again at the top of the rim. The four tubes
    // come an eyecup's two after each other; an eyecup with one lens lists it twice, a monocular all four times. Every
    // circle is the one through the outermost vertices of the model's ring it stands for (the lens's edge, the
    // opening's narrowest ring, the rim's), its centre and plane fitted to that ring, so it reaches all of the ring.
    private sealed class Device
    {
        internal string Node = "";
        internal Tube[] Tubes = Array.Empty<Tube>();
        internal float LensRadius;                 // the painted lens, on the floor
        internal float FloorDepth;                 // the floor under the Centres, along the axes
        internal float OpeningRadius;              // the opening at the Centres' level (an eyecup with two lenses: around both)
        internal float RimHeight, RimRadius;       // the top of the eyecup's rim above that, and the opening there
        internal Vector2 CardSize;                 // half of the card the lens map draws over an eyecup
        internal float CardRaise;                  // how far above the rim that card must go to clear the device
        internal bool Monocular => Tubes[0].Centre == Tubes[2].Centre;
        internal bool Thermal;                     // a thermal device: its picture is our thermal camera's (NightVisionThermal)
        internal bool Sealed;                      // its eyecups close against the face (NightVisionShade)
        internal float CupHeight;                  // a sealed eyecup's flared rim above the Centre, along the axis
        internal Vector4 CupRadii0, CupRadii1;     // its inner radius at 0, 5, 10, 20 mm and at 30, 40 mm above the Centre
        internal bool Fusion;                      // another mod may lay its picture into the eyepieces (EyepieceFusion)
        internal float FieldDegrees = 60f;         // the tubes' whole field (each eyepiece shows this cone about its axis; EyepieceFusion.FieldInMainUv)
        // Eyecups that come off (an item of their own in a slot under the device's node, CupSlot): their root's name starts
        // with CupNode. A device with CupNode set is this entry only while its cups are on (CupOff false) or off (true).
        internal string CupSlot = "", CupNode = "";
        internal bool CupOff;
    }

    // Whether a device's removable eyecups are on it (its cup slot holds an active node by their name).
    private static bool HasCups(Transform node, Device known)
    {
        Transform? slot = node.Find(known.CupSlot);
        if (slot == null)
            return false;
        for (int i = 0; i < slot.childCount; i++)
        {
            Transform cup = slot.GetChild(i);
            if (cup.gameObject.activeInHierarchy && cup.name.StartsWith(known.CupNode, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // TEMP DIAG: what the found device's cup slot holds, logged when it changes (and every device node of that name on the
    // player then, with its slot's contents), so a fitted eyecup not seen as one shows where it is instead.
    private static int _diagCupKey = int.MinValue, _diagCupLogs;
    private static void DiagCups(Player player)
    {
        if (_body == null || _device.CupNode.Length == 0 || _diagCupLogs >= 30)
            return;
        Transform? slot = _body.Find(_device.CupSlot);
        int key = _body.GetInstanceID() * 31 + (slot == null ? -1 : slot.childCount);
        if (slot != null)
            for (int i = 0; i < slot.childCount; i++)
                key = key * 31 + slot.GetChild(i).GetInstanceID() * 2 + (slot.GetChild(i).gameObject.activeInHierarchy ? 1 : 0);
        if (key == _diagCupKey)
            return;
        _diagCupKey = key;
        _diagCupLogs++;
        string Path(Transform t) { string r = t.name; for (Transform? u = t.parent; u != null; u = u.parent) r = u.name + "/" + r; return r; }
        string Slot(Transform node)
        {
            Transform? s = node.Find(_device.CupSlot);
            if (s == null) return "no " + _device.CupSlot;
            string r = $"{_device.CupSlot} ({s.childCount} children)";
            for (int i = 0; i < s.childCount; i++)
            {
                Transform c = s.GetChild(i);
                r += $" [{c.name} self {c.gameObject.activeSelf} inHierarchy {c.gameObject.activeInHierarchy} children {c.childCount}]";
            }
            return r;
        }
        NewCameraPlugin.Log?.LogInfo($"TEMP DIAG cups: frame {Time.frameCount}, entry cups {(_device.CupOff ? "off" : "on")}, HasCups {HasCups(_body, _device)}, found {Path(_body)} (id {_body.GetInstanceID()}, active {_body.gameObject.activeInHierarchy}): {Slot(_body)}");
        foreach (Transform t in player.gameObject.transform.root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == _device.Node && t != _body)
                NewCameraPlugin.Log?.LogInfo($"TEMP DIAG cups:   other {Path(t)} (id {t.GetInstanceID()}, active {t.gameObject.activeInHierarchy}): {Slot(t)}");
            else if (t.name.StartsWith(_device.CupNode, StringComparison.Ordinal) && (t.parent == null || t.parent.name != _device.CupSlot))
                NewCameraPlugin.Log?.LogInfo($"TEMP DIAG cups:   a cup elsewhere: {Path(t)} (active {t.gameObject.activeInHierarchy})");
        }
    }

    // The device found is the entry for the other state of its eyecups (put on or taken off since).
    // A device put away (its model swapped for one with or without the eyecups, the old one gone to the pool emptied of them)
    // is not this: it is looked for again as any device put away is.
    private static bool CupsChanged() => _body != null && _body.gameObject.activeInHierarchy && _device.CupNode.Length > 0 && HasCups(_body, _device) == _device.CupOff;

    private static Tube At(float x, float y, float z) => new Tube { Centre = new Vector3(x, y, z), Normal = Vector3.up };

    // The PVS-14's one eyepiece: its Centre at the lens's crown, the lens the dome's 60-sided edge 4.59 mm under it,
    // the dome tilted 0.3 degrees.
    private static readonly Tube Pvs14 = new Tube
    {
        Centre = new Vector3(0.01596f, 0.05146f, -0.02294f), Normal = new Vector3(0.0027f, 1f, 0.0046f),
        Lens = new Vector3(0.01595f, 0.04687f, -0.02296f), LensRadius = 0.01511f,
    };

    private static readonly Device[] Devices =
    {
        // GPNVG-18: the tubes look along -Y, the mount is toward +Z. Each eyecup holds two lenses side by side, painted
        // on its floor as two overlapping circles making an "8", under a clear plate; floor and plate bend from facing
        // straight back over the centre tube to facing 26.5 degrees outward over the side tube. The floor's two ends
        // are the "8"'s outer arcs: each lens is the circle through its end's vertices, in that end's plane (the centre
        // lens 18.61 mm, the side lens 18.88 mm), its Centre the centre of the plate's end above it, 8.1 mm up and up
        // to half a millimetre to one side. The plate is the eyecup's top, its edge at most 19.7 mm from the line
        // between its ends' centres; its ends stand up to 4.5 mm above its middle, which the card clears. The left
        // eyecup's floor sits 0.53 mm left of the right one's mirror image.
        new Device
        {
            Node = "nvg_l3_gpnvg-18_anvis_LOD0",
            Tubes = new[]
            {
                new Tube { Centre = new Vector3(0.02695f, 0.04766f, -0.07570f), Normal = new Vector3(0.0013f, 1f, -0.0043f), Lens = new Vector3(0.02718f, 0.03956f, -0.07568f), LensRadius = 0.01861f },
                new Tube { Centre = new Vector3(0.05039f, 0.05317f, -0.07576f), Normal = new Vector3(-0.4468f, 0.8946f, -0.0038f), Lens = new Vector3(0.05353f, 0.04568f, -0.07586f), LensRadius = 0.01888f },
                new Tube { Centre = new Vector3(-0.02695f, 0.04766f, -0.07570f), Normal = new Vector3(-0.0013f, 1f, -0.0043f), Lens = new Vector3(-0.02771f, 0.03956f, -0.07568f), LensRadius = 0.01861f },
                new Tube { Centre = new Vector3(-0.05039f, 0.05317f, -0.07576f), Normal = new Vector3(0.4468f, 0.8946f, -0.0038f), Lens = new Vector3(-0.05407f, 0.04568f, -0.07586f), LensRadius = 0.01888f },
            },
            FloorDepth = 0.0081f, OpeningRadius = 0.0197f, RimHeight = 0f, RimRadius = 0.0197f,
            CardSize = new Vector2(0.035f, 0.023f), CardRaise = 0.005f,
        },
        // PNV-10T: two straight eyepiece tubes (16-sided), 10.75 mm deep, with a rolled rubber rim; the lens is the
        // glass of the tube's floor out to where it meets the wall, the Centre at the tube's top, the rim the roll's inner
        // edge.
        new Device
        {
            Node = "nvg_alfa_pnv-10t_LOD0",
            Tubes = new[] { At(-0.03358f, 0.06246f, -0.03527f), At(-0.03358f, 0.06246f, -0.03527f), At(0.03666f, 0.06246f, -0.03527f), At(0.03666f, 0.06246f, -0.03527f) },
            LensRadius = 0.01573f, FloorDepth = 0.01075f, OpeningRadius = 0.01573f, RimHeight = 0.00324f, RimRadius = 0.01708f,
            CardSize = new Vector2(0.0205f, 0.0205f),
        },
        // PNV-57E: small lenses at the bottom of short 12-sided tubes, a thick rubber ring around each. The lens is the
        // glass, a low cone out to where it meets the wall, its plane taken there; the Centre at the tube's top, the rim
        // the ring's inner lip.
        new Device
        {
            Node = "Item_equipment_nightvision_57E_part_01_LOD0",
            Tubes = new[] { At(-0.03576f, 0.01333f, -0.08095f), At(-0.03576f, 0.01333f, -0.08095f), At(0.03576f, 0.01333f, -0.08095f), At(0.03576f, 0.01333f, -0.08095f) },
            LensRadius = 0.01062f, FloorDepth = 0.00528f, OpeningRadius = 0.01062f, RimHeight = 0.00227f, RimRadius = 0.01148f,
            CardSize = new Vector2(0.0155f, 0.0155f),
        },
        // Armasight N-15: a chamfer round each eyepiece, then a deep flared rubber eyecup. The model has no glass: the
        // lens is the chamfer's hole (a decagon, its corners 16.48 mm out), so the picture covers all of it; the
        // opening is the chamfer's top.
        new Device
        {
            Node = "nvg_armasight_n-15_LOD0", Sealed = true,
            CupHeight = 0.031f, CupRadii0 = new Vector4(0.0242f, 0.0252f, 0.0259f, 0.0282f), CupRadii1 = new Vector4(0.0294f, 0.0294f, 0f, 0f),
            Tubes = new[] { At(-0.03026f, 0.04487f, -0.02086f), At(-0.03026f, 0.04487f, -0.02086f), At(0.03026f, 0.04487f, -0.02086f), At(0.03026f, 0.04487f, -0.02086f) },
            LensRadius = 0.01648f, FloorDepth = 0.0025f, OpeningRadius = 0.02001f, RimHeight = 0.0175f, RimRadius = 0.0245f,
            CardSize = new Vector2(0.027f, 0.027f),
        },
        // L3Harris PVS-31A (a mod's): two pods on a bridge, short straight 24-sided eyepiece tubes with a thin lip. The
        // lens is the glass of the tube's floor out to where it meets the wall, the Centre at the tube's top, the rim the
        // lip's inner edge. Usually hung on a TATM, which is adjusted to bring the eyepieces to the eyes
        // (NightVisionMount).
        new Device
        {
            Node = "nvg_l3_pvs31a_device_01_LOD0",
            Tubes = new[] { At(-0.03481f, 0.06356f, -0.07219f), At(-0.03481f, 0.06356f, -0.07219f), At(0.03480f, 0.06356f, -0.07223f), At(0.03480f, 0.06356f, -0.07223f) },
            LensRadius = 0.01336f, FloorDepth = 0.00687f, OpeningRadius = 0.01336f, RimHeight = 0.00109f, RimRadius = 0.01366f,
            CardSize = new Vector2(0.016f, 0.016f),
        },
        // SPI T-7 thermal goggles: one sensor seen through two eyepieces, each a domed lens deep in a tube under a
        // soft eyecup. The Centre is at the tube's narrow lip, the floor at the lens's 16-sided edge, the rim the
        // eyecup's first ring.
        new Device
        {
            Node = "thermal_spi_t7_LOD0", Thermal = true, Sealed = true,
            CupHeight = 0.039f, CupRadii0 = new Vector4(0.0243f, 0.0253f, 0.0276f, 0.029f), CupRadii1 = new Vector4(0.0308f, 0.0318f, 0f, 0f),
            Tubes = new[] { At(-0.03063f, 0.05866f, -0.04368f), At(-0.03063f, 0.05866f, -0.04368f), At(0.03003f, 0.05866f, -0.04368f), At(0.03003f, 0.05866f, -0.04368f) },
            LensRadius = 0.01432f, FloorDepth = 0.00913f, OpeningRadius = 0.01636f, RimHeight = 0.00537f, RimRadius = 0.02433f,
            CardSize = new Vector2(0.026f, 0.026f),
        },
        // AN/PSQ-42 ENVG-B (K-ENVG-B's): two I2 tubes, each eyepiece a glass disc 14.9 mm across its 1 mm dome deep in a
        // rubber eyecup: the cup narrows to a 14.4 mm throat 5 mm over the glass, then flares to 28.7 mm at its lip,
        // 33.5 mm over the glass. The Centre is at the throat, the lens the disc's edge. Its thermal picture is the
        // other mod's, laid into the tubes' (EyepieceFusion).
        new Device
        {
            Node = "nvg_envgb_psq42_LOD0", CupSlot = "mod_eyecup", CupNode = "envgb_eyecup", Sealed = true, Fusion = true, FieldDegrees = 40f,
            CupHeight = 0.0285f, CupRadii0 = new Vector4(0.0144f, 0.0217f, 0.0223f, 0.0251f), CupRadii1 = new Vector4(0.0287f, 0.0287f, 0f, 0f),
            Tubes = new[] { At(-0.03077f, 0.02204f, -0.07569f), At(-0.03077f, 0.02204f, -0.07569f), At(0.03077f, 0.02204f, -0.07569f), At(0.03077f, 0.02204f, -0.07569f) },
            LensRadius = 0.0149f, FloorDepth = 0.0055f, OpeningRadius = 0.0144f, RimHeight = 0.0285f, RimRadius = 0.0287f,
            CardSize = new Vector2(0.031f, 0.031f),
        },
        // The ENVG-B with its eyecups taken off: the glass under the tube's bare mouth, 1.2 mm over the glass's crown, its
        // opening 13.7 mm across inside a 14.9 mm lip.
        new Device
        {
            Node = "nvg_envgb_psq42_LOD0", CupSlot = "mod_eyecup", CupNode = "envgb_eyecup", CupOff = true, Fusion = true, FieldDegrees = 40f,
            Tubes = new[] { At(-0.03077f, 0.01824f, -0.07569f), At(-0.03077f, 0.01824f, -0.07569f), At(0.03077f, 0.01824f, -0.07569f), At(0.03077f, 0.01824f, -0.07569f) },
            LensRadius = 0.0149f, FloorDepth = 0.0017f, OpeningRadius = 0.0137f, RimHeight = 0f, RimRadius = 0.0149f,
            CardSize = new Vector2(0.018f, 0.018f),
        },
        // AN/PVS-14: a monocular, its eyepiece lens bulging out of stepped rings, no eyecup to hide it.
        new Device
        {
            Node = "nvg_pvs_14_LOD0",
            Tubes = new[] { Pvs14, Pvs14, Pvs14, Pvs14 },
            FloorDepth = 0.00459f, OpeningRadius = 0.05f, RimHeight = 0f, RimRadius = 0.05f,
            CardSize = new Vector2(0.016f, 0.016f),
        },
    };
    // The eyepieces' optics: the exit pupil an eye relief out from the plate, made generous enough that the camera,
    // further back than an eye would be, sees the whole lens from where it sits; and how much light the lens keeps at
    // its rim.
    private const float EyeRelief = 0.025f, ExitPupil = 0.02f, ExitPupilEdge = 0.005f, RimLight = 0.35f;
    private const float OpenRadius = 3f;          // BRNVG's one lens, in its texture's heights: past the frame's corners
    private const int CentreLensA = 0, CentreLensB = 2;   // each eyecup's first lens; which is left is decided against the camera
    private const float MaxEyeDistance = 0.3f;    // the camera at the device's mount, not a free camera elsewhere

    private static readonly int ModeId = Shader.PropertyToID("_Mode");
    private static readonly int NightVisionId = Shader.PropertyToID("_NightVision");
    private static readonly int NightVisionVpId = Shader.PropertyToID("_NightVisionVP");
    private static readonly int PowerId = Shader.PropertyToID("_Power");
    private static readonly int LampId = Shader.PropertyToID("_Lamp");
    private static readonly int GlowId = Shader.PropertyToID("_Glow");
    private static readonly int DirectionId = Shader.PropertyToID("_Direction");

    private const float WarmUp = 0.15f;           // seconds from switch-on to full output
    private const float Strike = 0.45f;           // the output the tubes jump to the moment they are switched on
    private const float Afterglow = 0.025f;       // the phosphor's decay time once switched off (s)
    private const float Stagger = 0.12f;          // the longest a tube lags the switch (s)
    private static readonly int TubePowerId = Shader.PropertyToID("_TubePower");
    private static readonly int TubeMottleId = Shader.PropertyToID("_TubeMottle");
    private const float LensBrightness = 1.15f;   // the lenses against the dark around them (a thermal's as the game shows its picture)
    private const float GlowStrength = 0.55f;     // their light spread over the glass and eyecups
    private const float Distortion = 0.1f;        // pincushion: the share by which a line of sight at FieldEdge looks nearer the axis,
    private const float FieldEdge = 25f;          // degrees off a tube's axis, for an eye DistortionFar from the lenses
    private const float DistortionNear = 0.03f, DistortionFar = 0.15f;   // none at the eyecup; full well back from it

    private static bool _installed, _rendererPatched, _bundleTried;
    private static Shader? _binocularShader, _lensMaskShader, _glowShader, _meshReadShader, _fisheyeShader;

    // The fisheye lens's shader, which comes in the same bundle.
    internal static Shader? FisheyeShader
    {
        get
        {
            LoadShaders();
            return _fisheyeShader;
        }
    }
    private static Material? _binocularMaterial, _glowMaterial;
    private static readonly float[] _warm = new float[4], _power = new float[4], _lag = new float[4];   // per tube
    private static bool _switched;
    private static float _sinceSwitch;
    private static Texture2D? _openTexture;

    private static Transform? _body;
    private static Device _device = Devices[0];
    private static GameObject? _lenses;
    private static Transform[] _lensNodes = Array.Empty<Transform>();
    private static Vector4[] _lensFloors = Array.Empty<Vector4>();   // each lens's centre on the floor in its node's frame, w its radius
    private static int _searchedFrame = -100;
    private static Behaviour? _hiddenMask;

    // How far the eyes have moved apart, following the device as it flips down (0 one camera, 1 two eyes).
    private static float _split;
    private static CurveRotator? _rotator;
    private static bool _rotatorReversed;
    private static readonly FieldInfo? RotatorProgress = AccessTools.Field(typeof(CurveRotator), "_t");
    private static bool BinocularOn => _split > 0.001f && _binocularMaterial != null && _body != null;
    private static NightVisionDevice? _darkened;
    private static Behaviour? _woken;
    private static readonly FieldInfo? DeviceComponent = AccessTools.Field(typeof(NightVisionDevice), "_nightVision");

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".eyepieces");
        MethodInfo? preCull = AccessTools.Method(typeof(BSG.CameraEffects.NightVision), "OnPreCull");
        MethodInfo? postfix = typeof(NightVisionEyepieces).GetMethod(nameof(NightVisionPreCullPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (preCull != null && postfix != null)
            harmony.Patch(preCull, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
        InstallThermal(harmony);
        InstallEyepieceImage(harmony);
        InstallIr(harmony);
        InstallPreviewMounts(harmony);
        TrackAmbientLights();

        _installed = true;
    }

    internal static void Update(Player player)
    {
        if (!LoadShaders())
        {
            Release();
            return;
        }

        // Looked for again while none is worn, or the one found has been put away (a device swapped for another stays
        // in the scene, hidden).
        bool wornAny = player.NightVisionObserver?.Component != null;
        DiagCups(player); // TEMP DIAG
        // Its eyecups put on or taken off: the device let go of and found again, as the entry for its eyecups now.
        if (CupsChanged())
        {
            NewCameraPlugin.Log?.LogInfo($"NVG: {_device.Node}'s eyecups {(_device.CupOff ? "put on" : "taken off")}.");
            Release();
            _searchedFrame = -1000;
            DiagEyeDepthWanted = true; // TEMP DIAG
        }
        int searchEvery = wornAny && Time.time < _holdFilterUntil ? 1 : SearchFrames;
        if ((_body == null || !_body.gameObject.activeInHierarchy || _body.root != player.gameObject.transform.root || WornElsewhere(player)) && Time.frameCount - _searchedFrame >= searchEvery)
        {
            Device was = _device;
            Transform? found = FindDevice(player);
            if (found != _body)
            {
                Device now = _device;
                _device = was;
                // The last one let go of even when it is gone already (the raid it was in has ended): what was made
                // and held for it would otherwise live on into the next.
                if (!ReferenceEquals(_body, null))
                    Release();
                _device = now;
                Attach(found);
                HideoutNightVision.Refresh(player);
            }
            _searchedFrame = Time.frameCount;
        }

        CameraManager? cameras = BodyVisibility.Cameras;
        KeepFilterHeld(player, cameras, wornAny);
        bool thermal = _body != null && _device.Thermal;
        NightVisionDevice? device = _body != null && !thermal ? _body.GetComponentInParent<NightVisionDevice>() : null;
        // The game's own record of what is worn, not what a model was given when it was made: a model's went on saying off with
        // the filter on, the device worn since its last change or on a raid's first model.
        NightVisionComponent? nightItem = !thermal ? player.NightVisionObserver?.Component : null;
        ThermalVisionDevice? thermalDevice = thermal ? _body!.GetComponentInParent<ThermalVisionDevice>() : null;
        ThermalVisionComponent? thermalItem = thermal ? player.ThermalVisionObserver?.Component : null;
        Behaviour? renderer = !thermal && UseBrnvg ? NightRenderer(cameras?.NightVision) : null;
        // BRNVG draws no filter in the hideout (it puts no renderer on the camera there): the lenses then show the
        // game's picture, as without BRNVG.
        bool brnvg = !thermal && UseBrnvg && (renderer != null || player is not HideoutPlayer);
        bool present = _body != null && _body.gameObject.activeInHierarchy;
        bool worn = present && EyepieceImageReady(cameras) && (thermal ? EnsureThermalCamera() : !brnvg || renderer != null);
        bool switchedOn = worn && (thermal ? thermalItem?.Togglable != null && thermalItem.Togglable.On : nightItem?.Togglable != null ? nightItem.Togglable.On : device != null && SwitchedOn(device));
        // The tubes come on once the device is all the way down and the game has done switching its own filter on
        // (its switch dims the filter for a moment, the picture the lenses show), and go off before it flips up:
        // flipping down is flipping up the other way round. BRNVG is woken as soon as it is switched on.
        _flip = present ? FlipProgress(switchedOn) : 0f;
        bool settled = thermal
            ? cameras?.ThermalVision == null || !cameras.ThermalVision.InProcessSwitching
            : cameras?.NightVision != null && cameras.NightVision.On && !cameras.NightVision.InProcessSwitching;
        bool powered = switchedOn && _flip >= 0.999f && settled;
        float power = Power(powered);
        // The tubes' afterglow shown only while the game's filter is still on, the picture the lenses show: switched off
        // while aiming the game took its filter off within five frames, the tubes' glow still a tenth, and the lenses showed
        // the bare picture through the dead filter (a flash of the scene as the eye sees it).
        bool filterThere = thermal || brnvg || cameras?.NightVision != null && cameras.NightVision.On;
        bool on = worn && (powered || power > 0.01f && filterThere);
        Wake(worn && (switchedOn || on) ? renderer : null, cameras?.NightVision);
        HoldMainThermal(cameras, thermal && worn);
        KeepThermalGlow(thermalDevice);
        Active = on;
        _split = _flip;
        KeepEyepieceImage(cameras, worn && (on || _flip > 0.001f));
        KeepGlow(device);
        UpdateMountHeight(player);

        EnsureRendererPatch();
        RestoreMask(cameras);
    }

    // The gear changed with the device down (the helmet taken off, or another with its device down put on): the filter stays on for a
    // moment with no device to take it over, and was seen over the whole frame. So it is held back from when the device goes: for as long
    // as nothing is worn and the game has yet to turn it off, and a moment longer for the next device's model to be found. A device
    // this mod has no eyepieces for shows its filter after that.
    private const int SearchFrames = 30;
    private const float HoldFilterSeconds = 1f;
    private static float _holdFilterUntil = -1f;
    private static bool _deviceThere;

    private static bool HoldFilter => Time.time < _holdFilterUntil && (_body == null || !_body.gameObject.activeInHierarchy);

    private static void KeepFilterHeld(Player player, CameraManager? cameras, bool wornAny)
    {
        bool there = _body != null && _body.gameObject.activeInHierarchy && !WornElsewhere(player);
        if ((_deviceThere && !there) || (!there && !wornAny && Time.time < _holdFilterUntil && cameras?.NightVision != null && cameras.NightVision.On))
            _holdFilterUntil = Time.time + HoldFilterSeconds;
        _deviceThere = there;
    }

    internal static void Release()
    {
        ShowDeviceAgain();
        ClearDirect();
        _direct = false;
        ReleaseShade();
        Active = false;
        ReleaseMount();
        _flip = 0f;
        System.Array.Clear(_warm, 0, 4);
        System.Array.Clear(_power, 0, 4);
        _switched = false;
        KeepGlow(null);
        Wake(null, BodyVisibility.Cameras?.NightVision);
        RestoreMask(BodyVisibility.Cameras);
        if (_lenses != null)
            UnityEngine.Object.Destroy(_lenses);
        _lenses = null;
        _lensNodes = Array.Empty<Transform>();
        _lensFloors = Array.Empty<Vector4>();
        _faceLights = Array.Empty<Light?>();
        _body = null;
        _rotator = null;
        // What was learned of one device's rotator is not the next's.
        _rotatorReversed = false;
        _flipTarget = -1f;
        _flipWasOn = false;
        _flipSeenMoving = false;
        _split = 0f;
        _searchedFrame = -100;
        ReleaseEyes();
        ReleaseThermal();
        KeepEyepieceImage(null, false);
    }

    // The device's flip from up (0) to down (1), eased as it moves (its CurveRotator's progress through its curve).
    // Calibrated by the filter: when it is on the device is down.
    private static float FlipProgress(bool on)
    {
        if (_rotator == null || RotatorProgress?.GetValue(_rotator) is not float t)
            return on ? 1f : 0f;

        float eased = Mathf.Clamp01(_rotator.AnimationCurve != null ? _rotator.AnimationCurve.Evaluate(t) : t);
        // Which end is down: by where the game sets it going as it is switched. Not by where it stands when switched on: between
        // the switch and the hand reaching the device it has not started, and it read as down already. Only a device on for a
        // good while and never seen moving is taken by where it stands.
        float target = RotatorTarget?.GetValue(_rotator) is float set ? set : -1f;
        if (on != _flipWasOn)
        {
            _flipWasOn = on;
            _flipOnSince = Time.time;
        }
        if (target != _flipTarget)
        {
            if (_flipTarget >= 0f)
            {
                _rotatorReversed = on == target < 0.5f;
                _flipSeenMoving = true;
            }
            _flipTarget = target;
        }
        else if (on && !_flipSeenMoving && !_rotator.ShouldRotate() && Time.time - _flipOnSince > FlipSettleSeconds)
            _rotatorReversed = eased < 0.5f;
        return _rotatorReversed ? 1f - eased : eased;
    }

    private const float FlipSettleSeconds = 3f;
    private static readonly FieldInfo? RotatorTarget = AccessTools.Field(typeof(CurveRotator), "_targetT");
    private static float _flipTarget = -1f, _flipOnSince;
    private static bool _flipWasOn, _flipSeenMoving;

    // The model found belongs to another item than the one the game says is worn.
    private static bool WornElsewhere(Player player)
    {
        if (_body == null || _device.Thermal)
            return false;
        NightVisionComponent? worn = player.NightVisionObserver?.Component;
        NightVisionDevice? device = _body.GetComponentInParent<NightVisionDevice>();
        return worn != null && device != null && !ReferenceEquals(DeviceComponent?.GetValue(device), worn);
    }

    private static Transform? FindDevice(Player player)
    {
        PlayerBody? body = player.PlayerBody;
        if (body == null)
            return null;
        NightVisionComponent? wornItem = player.NightVisionObserver?.Component;

        foreach (EquipmentSlot slot in new[] { EquipmentSlot.Headwear, EquipmentSlot.FaceCover, EquipmentSlot.Eyewear })
        {
            if (!body.SlotViews.TryGetByKey(slot, out PlayerBody.SlotView? view) || view?.Model == null)
                continue;

            foreach (NightVisionDevice device in view.Model.GetComponentsInChildren<NightVisionDevice>(true))
            {
                // Only the model of the item worn: more than one may be there.
                if (wornItem != null && !ReferenceEquals(DeviceComponent?.GetValue(device), wornItem))
                    continue;
                foreach (Transform node in device.GetComponentsInChildren<Transform>(true))
                {
                    foreach (Device known in Devices)
                    {
                        if (node.name != known.Node || !node.gameObject.activeInHierarchy)
                            continue;
                        if (known.CupNode.Length > 0 && HasCups(node, known) == known.CupOff)
                            continue;
                        _device = known;
                        return node;
                    }
                }
            }

            // Thermal goggles carry no device component of their own (the T-7 has none): by their model's name.
            foreach (Transform node in view.Model.GetComponentsInChildren<Transform>(true))
            {
                foreach (Device known in Devices)
                {
                    if (!known.Thermal || node.name != known.Node || !node.gameObject.activeInHierarchy)
                        continue;
                    _device = known;
                    return node;
                }
            }
        }

        return null;
    }

    private static void Attach(Transform? body)
    {
        if (body == null)
            return;

        // Where each tube's lens sits and where it looks, following the device.
        _body = body;
        _rotator = body.GetComponentInParent<CurveRotator>();
        _lenses = new GameObject("Justachae NVG eyepieces");
        _lenses.layer = body.gameObject.layer;
        _lenses.transform.SetParent(body, false);
        _lensNodes = new Transform[_device.Tubes.Length];
        _lensFloors = new Vector4[_device.Tubes.Length];
        for (int i = 0; i < _device.Tubes.Length; i++)
        {
            Tube tube = _device.Tubes[i];
            GameObject lens = new GameObject("lens " + i) { layer = body.gameObject.layer };
            lens.transform.SetParent(_lenses.transform, false);
            Vector3 normal = tube.Normal.normalized;
            lens.transform.localPosition = tube.Centre;
            lens.transform.localRotation = Quaternion.LookRotation(normal, Vector3.forward);
            _lensNodes[i] = lens.transform;
            Vector3 floor = tube.LensRadius > 0f ? tube.Lens : tube.Centre - normal * _device.FloorDepth;
            Vector3 local = Quaternion.Inverse(lens.transform.localRotation) * (floor - tube.Centre);
            _lensFloors[i] = new Vector4(local.x, local.y, local.z, tube.LensRadius > 0f ? tube.LensRadius : _device.LensRadius);
        }
        MeasureObjective(body);
        MakeFaceLights();
    }

    private static bool LoadShaders()
    {
        if (_binocularMaterial != null)
            return true;
        if (_bundleTried)
            return false;

        _bundleTried = true;
        string path = Path.Combine(Path.GetDirectoryName(typeof(NightVisionEyepieces).Assembly.Location) ?? string.Empty, "nightvision.bundle");
        AssetBundle? bundle = File.Exists(path) ? AssetBundle.LoadFromFile(path) : null;
        if (bundle == null)
            return false;

        foreach (Shader shader in bundle.LoadAllAssets<Shader>())
        {
            if (shader.name == "Justachae/NVG/Binocular")
                _binocularShader = shader;
            else if (shader.name == "Justachae/NVG/LensMask")
                _lensMaskShader = shader;
            else if (shader.name == "Justachae/NVG/Glow")
                _glowShader = shader;
            else if (shader.name == "Justachae/NVG/Shade")
                _shadeShader = shader;
            else if (shader.name == "Justachae/NVG/MeshRead")
                _meshReadShader = shader;
            else if (shader.name == "Justachae/NVG/IrLight")
                _irLightShader = shader;
            else if (shader.name == "Justachae/Fisheye")
                _fisheyeShader = shader;
            else if (shader.name == "Justachae/NVG/Direct")
                _directShader = shader;
            else if (shader.name == "Justachae/NVG/Objective")
                _objectiveShader = shader;
            else if (shader.name == "Justachae/NVG/Defocus")
                _defocusShader = shader;
            else if (shader.name == "Justachae/NVG/Tube")
                _tubeShader = shader;
        }

        if (_binocularShader == null || _lensMaskShader == null || _glowShader == null)
            return false;
        _glowMaterial = new Material(_glowShader) { name = "Justachae NVG eyepiece glow", hideFlags = HideFlags.DontSave };
        _binocularMaterial = new Material(_binocularShader) { name = "Justachae NVG eyepieces", hideFlags = HideFlags.DontSave };
        if (_objectiveShader != null)
            _objective = new Material(_objectiveShader) { name = "Justachae NVG objective", hideFlags = HideFlags.DontSave };
        if (_defocusShader != null)
            _defocus = new Material(_defocusShader) { name = "Justachae NVG objective defocus", hideFlags = HideFlags.DontSave };
        return true;
    }

    // The game's own filter adds nothing while the eyepieces show; BRNVG's renderer is caught where it draws. While
    // the device flips with the filter off, the eyes go in at the SSAA stage, through the filter's hook.
    private static void NightVisionPreCullPostfix(BSG.CameraEffects.NightVision __instance)
    {
        if (!Active && !BinocularOn && !HoldFilter)
            return;

        // Nothing at the SSAA stage: the eyepieces go in by our own image effect (EyepieceImage).
        if (AccessTools.Field(typeof(BSG.CameraEffects.NightVision), "_nightVisionCB")?.GetValue(__instance) is CommandBuffer buffer)
            buffer.Clear();
        if (AccessTools.Field(typeof(BSG.CameraEffects.NightVision), "_ssaaPropagator")?.GetValue(__instance) is SSAAPropagator propagator && propagator != null)
            propagator.SetNightVisionMaterial(null);
        // IR lasers drawn into the IR layer only (NightVisionIr.cs), not into the naked eye's frame.
        if (IrWanted)
            Shader.SetGlobalFloat(NightVisionOnId, 0f);
        if ((Active || BinocularOn || HoldFilter) && __instance.TextureMask != null && __instance.TextureMask.enabled)
        {
            __instance.TextureMask.enabled = false;
            _hiddenMask = __instance.TextureMask;
        }
        // The game also lights the whole scene by its night-vision ambient while the filter is on (it sets the level's
        // ambient to it here, and back after the frame). With the eyepieces in, the frame is the naked eye's: it keeps
        // the level's own ambient, whatever other mods do or do not do about it.
        if (Comfort.Common.Singleton<LevelSettings>.Instance is LevelSettings level && level != null)
            level.AmbientType = AmbientType.Default;
    }

    // The game turns its mask on with the filter; BRNVG keeps it off itself.
    private static void RestoreMask(CameraManager? cameras)
    {
        // Not while the eyes still show, the device flipping up with the game's filter yet to switch off: its mask
        // would black out all around them for that moment.
        if (Active || BinocularOn || HoldFilter || _hiddenMask == null)
            return;

        if (cameras?.NightVision != null && cameras.NightVision.On && !BodyVisibility.UsesNightPipeline(cameras.NightVision))
            _hiddenMask.enabled = true;
        _hiddenMask = null;
    }

    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static MethodInfo? _renderEffect;
    private static FieldInfo? _propagatorField;
    private static FieldInfo?[] _openFields = Array.Empty<FieldInfo?>();
    private static readonly object?[] _savedOpen = new object?[7];

    private static void EnsureRendererPatch()
    {
        if (_rendererPatched || _body == null)
            return;

        _rendererPatched = true;
        Type? renderer = BodyVisibility.NightRendererType;
        if (renderer == null)
            return;
        _renderEffect = AccessTools.Method(renderer, "RenderEffect");
        _propagatorField = renderer.GetField("ssaaPropagator", Private);
        _openFields = new[]
        {
            renderer.GetField("lensTexture", Private), renderer.GetField("maskOverlay", Private), renderer.GetField("lensDefinitions", Private),
            renderer.GetField("multiLensVignette", Private), renderer.GetField("multiLensEdgeDistortion", Private),
            renderer.GetField("lensSeamMode", Private),
        };
        MethodInfo? render = AccessTools.Method(renderer, "OnRenderImage");
        MethodInfo? prefix = typeof(NightVisionEyepieces).GetMethod(nameof(RenderImagePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (render != null && prefix != null)
            new Harmony(NewCameraPlugin.PluginGuid + ".eyepieces.brnvg").Patch(render, prefix: new HarmonyMethod(prefix));
    }

    // BRNVG's renderer on the FPS camera: while the eyepieces have the device they are put in here, where BRNVG would
    // have drawn (after the effects that come before it, before those after), and they make their picture with its
    // effect themselves (LensPicture).
    private static bool RenderImagePrefix(Behaviour __instance, RenderTexture source, RenderTexture destination)
    {
        CameraManager? cameras = BodyVisibility.Cameras;
        if (HoldFilter && EyepieceImageReady(cameras) && __instance.gameObject == cameras!.NightVision.gameObject)
        {
            PassThrough(__instance, source, destination);
            return false;
        }
        if (_body == null || !EyepieceImageReady(cameras) || __instance.gameObject != cameras!.NightVision.gameObject)
            return true;

        EyepieceRenderImage(__instance, source, destination, Fisheye.TakeLeft());
        return false;
    }

    // The lenses' light at an eighth of the frame, blurred wide twice over: the glow they cast.
    private static RenderTexture Glow(Material composite, Material glow, RenderTexture frame)
    {
        glow.CopyPropertiesFromMaterial(composite);
        int width = Mathf.Max(frame.width / 4, 1), height = Mathf.Max(frame.height / 4, 1);
        RenderTexture light = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf);
        Graphics.Blit(frame, light, glow, 0);
        RenderTexture a = RenderTexture.GetTemporary(Mathf.Max(width / 2, 1), Mathf.Max(height / 2, 1), 0, RenderTextureFormat.ARGBHalf);
        RenderTexture b = RenderTexture.GetTemporary(a.width, a.height, 0, RenderTextureFormat.ARGBHalf);
        Graphics.Blit(light, a);
        RenderTexture.ReleaseTemporary(light);
        for (int i = 0; i < 2; i++)
        {
            glow.SetVector(DirectionId, new Vector4(1f, 0f, 0f, 0f));
            Graphics.Blit(a, b, glow, 1);
            glow.SetVector(DirectionId, new Vector4(0f, 1f, 0f, 0f));
            Graphics.Blit(b, a, glow, 1);
        }
        RenderTexture.ReleaseTemporary(b);
        return a;
    }

    // The tubes' output this frame, with the device switched on (true) or not; the composite reads it. Returns the
    // brightest tube's.
    private static float Power(bool switchedOn)
    {
        if (switchedOn != _switched)
        {
            _switched = switchedOn;
            _sinceSwitch = 0f;
            for (int i = 0; i < 4; i++)
                _lag[i] = UnityEngine.Random.value * Stagger;
        }
        _sinceSwitch += Time.deltaTime;

        Vector4 power = Vector4.zero, mottle = Vector4.zero;
        float brightest = 0f;
        for (int i = 0; i < 4; i++)
        {
            bool reached = _sinceSwitch >= _lag[i];   // this tube has followed the switch
            if (switchedOn && reached)
            {
                _warm[i] = Mathf.Min(_warm[i] + Time.deltaTime / WarmUp, 1f);
                _power[i] = _warm[i] < 1f ? (Strike + (1f - Strike) * _warm[i]) * (0.7f + 0.3f * UnityEngine.Random.value) : 1f;
                mottle[i] = 1f - _warm[i];
            }
            else if (!switchedOn && reached || switchedOn)
            {
                _warm[i] = 0f;
                _power[i] *= Mathf.Exp(-Time.deltaTime / Afterglow);
            }
            power[i] = _power[i];
            brightest = Mathf.Max(brightest, _power[i]);
        }
        for (int i = 1; i < 4 && i < _device.Tubes.Length; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (_device.Tubes[j].Centre != _device.Tubes[i].Centre)
                    continue;
                power[i] = power[j];   // the same lens listed again
                mottle[i] = mottle[j];
                break;
            }
        }
        if (_binocularMaterial != null)
        {
            _binocularMaterial.SetVector(TubePowerId, power);
            _binocularMaterial.SetVector(TubeMottleId, mottle);
            _binocularMaterial.SetVector(PowerId, new Vector4(0f, 0f, Time.frameCount, 0f));
            // Thermal goggles show a display, its picture straightened before it gets there: no pincushion, as the
            // game's own T-7 has none.
            _binocularMaterial.SetVector(LampId, new Vector4(_device.Thermal ? 1f : LensBrightness, EyepieceGlow ? GlowStrength : 0f, _device.Thermal ? 0f : Distortion, FieldEdge * Mathf.Deg2Rad));
        }
        return brightest;
    }

    // BRNVG's renderer with an open lens (alpha 0), an overlay that covers nothing, no vignette, edge distortion or
    // seams, and in place of its tube layout a single tube over the whole frame (it meters its exposure per tube, so
    // with none it would never adjust its gain); and back as it was.
    private static void OpenLens(Behaviour renderer, bool open)
    {
        if (_openTexture == null)
        {
            // BRNVG fits its lens texture to the frame's height at the texture's own aspect: a wide one reaches past both sides.
            _openTexture = new Texture2D(64, 4, TextureFormat.RGBA32, false) { name = "Justachae NVG open lens", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            _openTexture.SetPixels(new Color[64 * 4]);
            _openTexture.Apply(false, true);
        }

        for (int i = 0; i < _openFields.Length; i++)
        {
            FieldInfo? field = _openFields[i];
            if (field == null)
                continue;
            if (!open)
            {
                field.SetValue(renderer, _savedOpen[i]);
                continue;
            }

            _savedOpen[i] = field.GetValue(renderer);
            object? value = i switch
            {
                0 or 1 => _openTexture,
                2 => OpenLayout(field.FieldType.GetElementType()!),
                3 or 4 => false,
                _ => Enum.ToObject(field.FieldType, 0),
            };
            field.SetValue(renderer, value);
        }
    }
}

internal static partial class NightVisionEyepieces
{
    // The device's light off, or back as the game would have it (lit while the device is on).
    private static void KeepGlow(NightVisionDevice? device)
    {
        if (_darkened != null && _darkened != device && _darkened._light != null)
            _darkened.CG_Init();
        _darkened = device;
        if (device != null && device._light != null && device._light.enabled)
            device._light.enabled = false;
    }

    // Switched on: what lights the game's glow (the device's togglable), ahead of the filter.
    private static bool SwitchedOn(NightVisionDevice device)
    {
        return DeviceComponent?.GetValue(device) is NightVisionComponent component && component.Togglable != null && component.Togglable.On;
    }

    // BRNVG's renderer on the night-vision camera, once BRNVG has set it up for this device.
    private static Behaviour? NightRenderer(BSG.CameraEffects.NightVision? nightVision)
    {
        Type? type = BodyVisibility.NightRendererType;
        if (nightVision == null || type == null || !BodyVisibility.UsesNightPipeline(nightVision))
            return null;
        Behaviour? renderer = nightVision.GetComponent(type) as Behaviour;
        return renderer != null && type.GetProperty("OpticReady", Private)?.GetValue(renderer) is true ? renderer : null;
    }

    // BRNVG draws while the game's filter is on; between the switch and the end of the flip it is woken here, and
    // put back as the filter has it once the eyepieces go dark.
    private static void Wake(Behaviour? renderer, BSG.CameraEffects.NightVision? nightVision)
    {
        bool filterOn = nightVision != null && nightVision.On;
        if (_woken != null && (_woken != renderer || filterOn))
        {
            if (!filterOn)
                SetNightEnabled(_woken, false);
            _woken = null;
        }
        if (renderer == null || filterOn || renderer.enabled && NightEnabled(renderer))
            return;
        SetNightEnabled(renderer, true);
        _woken = renderer;
    }

    private static bool NightEnabled(Behaviour renderer)
    {
        return renderer.GetType().GetProperty("NightVisionEnabled", Private)?.GetValue(renderer) is true;
    }

    private static void SetNightEnabled(Behaviour renderer, bool on)
    {
        renderer.GetType().GetProperty("NightVisionEnabled", Private)?.SetValue(renderer, on);
        renderer.enabled = on;
    }

    private static Array OpenLayout(Type lens)
    {
        Array layout = Array.CreateInstance(lens, 4);
        layout.SetValue(Activator.CreateInstance(lens, new Vector2(0.5f, 0.5f), OpenRadius, 0f, 0, 1f), 0);
        return layout;
    }
}
