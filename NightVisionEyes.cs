using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// Where each pixel of the main frame looks through a lens, past the housing. There BRNVG's picture shows, in the direction of that
// line of sight; the device itself is drawn by the main camera, as the game has it.
internal static partial class NightVisionEyepieces
{
    // The eyes' near plane, the eyes' own drawing and the lenses' picture alike (F12 「头戴式成像装备」 双眼近裁剪面). At a
    // fixed centimetre, an eye coming that close to an eyecup lost the picture on that side: the card the lenses'
    // picture is worked out on, just above the rim, was cut off by the near plane. The eyes see only half a metre, so
    // it can go far below the main camera's without the depth suffering.
    internal static float EyeNearPlane = 0.00001f;
    private static float EyeNear => Mathf.Clamp(EyeNearPlane, 0.00001f, 0.05f);
    private const float LensMapScale = 1f;   // the lenses' edges are sharp: the map at the eye's own size
    private const float LensMapRange = 1f;
    private const float Overscan = 0.06f;         // each eye's view past the middle of the frame, as a share of it
    // A tube's field, as the half angle off its axis within which a line of sight through its lens finds the
    // picture: wide enough for the whole "8" from where the camera sits behind it, so the lenses go dark only when
    // seen from well off to the side.
    private const float TubeFieldFade = 5f;   // the field fades out over this past its edge (half the device's FieldDegrees)

    private static readonly int TubeFieldId = Shader.PropertyToID("_TubeField");
    private static readonly int TubeRectId = Shader.PropertyToID("_TubeRect"), TubeUpId = Shader.PropertyToID("_TubeUp");
    private const float TubeRectFade = 0.012f;   // in tangent: the screen's edge, a little soft
    private static string? _rectNoted;
    private static readonly int[] TubeAxisIds =
    {
        Shader.PropertyToID("_TubeAxis0"), Shader.PropertyToID("_TubeAxis1"), Shader.PropertyToID("_TubeAxis2"), Shader.PropertyToID("_TubeAxis3"),
    };
    private static readonly int[] LensIds4 =
    {
        Shader.PropertyToID("_Lens0"), Shader.PropertyToID("_Lens1"), Shader.PropertyToID("_Lens2"), Shader.PropertyToID("_Lens3"),
    };
    private static readonly int[] AxisIds =
    {
        Shader.PropertyToID("_Axis0"), Shader.PropertyToID("_Axis1"), Shader.PropertyToID("_Axis2"), Shader.PropertyToID("_Axis3"),
    };
    private static readonly int[] LensFloorIds =
    {
        Shader.PropertyToID("_LensFloor0"), Shader.PropertyToID("_LensFloor1"), Shader.PropertyToID("_LensFloor2"), Shader.PropertyToID("_LensFloor3"),
    };
    private static readonly int OpeningId = Shader.PropertyToID("_Opening");
    private static readonly int EyeId = Shader.PropertyToID("_Eye");
    private static readonly int OverscanId = Shader.PropertyToID("_Overscan");
    private static readonly int SplitId = Shader.PropertyToID("_Split");
    private static readonly int EyeSpanId = Shader.PropertyToID("_EyeSpan");
    private static readonly int LensClearId = Shader.PropertyToID("_LensClear");
    private static readonly int OpticsId = Shader.PropertyToID("_Optics");
    private static readonly int[] EyeIds = { Shader.PropertyToID("_LeftEye"), Shader.PropertyToID("_RightEye") };
    private static readonly int[] LensIds = { Shader.PropertyToID("_LeftLens"), Shader.PropertyToID("_RightLens") };
    private static readonly int[] GlassIds = { Shader.PropertyToID("_LeftGlass"), Shader.PropertyToID("_RightGlass") };
    private static readonly int[] EyeInvVpIds = { Shader.PropertyToID("_EyeInvVP0"), Shader.PropertyToID("_EyeInvVP1") };
    private static readonly int[] EyePosIds = { Shader.PropertyToID("_EyePos0"), Shader.PropertyToID("_EyePos1") };
    private static readonly int[] EyeAimIds = { Shader.PropertyToID("_EyeAim0"), Shader.PropertyToID("_EyeAim1") };
    // How far the two eyepieces stand apart on the screen this frame: 0 one round field, 1 each its own (RenderLenses).
    private static float _fieldApart;
    private static Vector3 _aimRight;   // the right eye's lines of sight's turn this frame (as _EyeAim1)

    private static readonly RenderTexture?[] _lensMaps = new RenderTexture?[2];
    // Each eye's glass: where it looks at a lens at all, whether or not light reaches it through the exit pupil (the
    // lens map's own value is the light, dark where the line of sight misses the pupil).
    private static readonly RenderTexture?[] _glassMaps = new RenderTexture?[2];
    private static readonly RenderTargetIdentifier[] _lensTargets = new RenderTargetIdentifier[2];
    private static Material? _lensMapMaterial;
    private static Mesh? _card;
    private static CommandBuffer? _lensMapBuffer;
    private static Renderer[] _blockers = System.Array.Empty<Renderer>();
    private static Transform? _blockersOf;
    private static int _eyesFrame = -100;
    private static bool LensesOn(Camera camera)
    {
        // Only while our image effect is on to lay the lenses' picture over the frame.
        if (_body == null || _lensNodes.Length == 0 || camera != Camera.main)
            return false;
        if ((!BinocularOn && !Active) || _eyepieceImage == null || !_eyepieceImage.enabled)
            return false;
        // Both eyepieces swung all the way away on their pods: the eyes are bare, the device the game's to draw again,
        // as with it flipped up (the eyes eased back to the camera on the way, below).
        if (_lensPods.Length > 0 && PodBare(CentreLensA) >= 1f && PodBare(CentreLensB) >= 1f)
            return false;
        Vector3 mount = _rotator != null ? _rotator.transform.position : _body.position;
        return (mount - camera.transform.position).sqrMagnitude <= MaxEyeDistance * MaxEyeDistance;
    }

    internal static void OnCameraPreCull(Camera camera)
    {
        LightFace(camera);
        if (camera != Camera.main)
            return;
        HideIrLaserLights(camera);
        if (_body != null)
            AdjustMount(camera);
        // (Before anything here reads the camera's projection: sealed thermal goggles down, it sees no further than the
        // device this frame, NightVisionThermal.cs; by how the last frame stood.)
        SkipMainScene(camera);
        // Once the device's mount is set for this frame: the camera has been placed by now. The device drawn into the
        // main camera by this mod (NightVisionDirect.cs), its lenses left open, so the lenses' picture is made from the
        // scene behind them; drawn with the eye at the origin, so it does not shake far out in a map.
        _direct = _directShader != null && LensesOn(camera) && PrepareRelative(camera);
        if (_direct)
            HideDevice(camera);
        ClaimFusion(camera);
    }

    // A fusion device's eyepieces shown this frame: claimed for EyepieceFusion, with where its right tube looks on the
    // screen and how far it is down. The ENVG-B lays its thermal into the right tube only (the wearer's right, of the
    // two eyepieces the one further along the camera's right); the left stays the tube's own.
    private static Vector4 _fusedTubes;
    // Whose handler this frame's claim is for: the device's own node, or the clip-on's name (ClaimClipOn).
    private static string _fusionKey = "";
    private static bool _clipOnClaimed;
    private static void ClaimFusion(Camera camera)
    {
        _clipOnClaimed = false;
        if (_lensNodes.Length == 0 || !LensesOn(camera))
            return;
        if (ClaimClipOn(camera))
            return;
        // (Only a device some mod serves: one with no handler registered is left to its own mod's way of drawing.)
        if (!_device.Fusion || !Interop.EyepieceFusion.Serves(_device.Node))
            return;
        _fusionKey = _device.Node;
        Interop.EyepieceFusion.ClaimedClipOn = null;
        // Which is which by where the eyepieces stand down before the eyes (not where a pod has swung one).
        int indexB = Mathf.Min(CentreLensB, _lensNodes.Length - 1);
        Transform a = _lensRest[CentreLensA], b = _lensRest[indexB];
        Vector3 right = camera.transform.right;
        float middle = (Vector3.Dot(a.position, right) + Vector3.Dot(b.position, right)) * 0.5f;
        bool aRight = Vector3.Dot(a.position, right) >= Vector3.Dot(b.position, right);
        Transform rightTube = aRight ? a : b;
        // The right eyepiece swung away from the eye: its tube is off, and there is nothing to lay the thermal into.
        // Until then, as it fades out (and K-ENVG-B's thermal with it), still laid in.
        if (PodGone(aRight ? CentreLensA : indexB) >= 1f)
            return;
        for (int i = 0; i < 4; i++)
            _fusedTubes[i] = _device.FusionTubes != null
                ? (System.Array.IndexOf(_device.FusionTubes, i) >= 0 ? 1f : 0f)
                : i < _lensRest.Length && Vector3.Dot(_lensRest[i].position, right) > middle ? 1f : 0f;
        Vector3 axis = -rightTube.forward;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        Vector3 ahead = camera.worldToCameraMatrix.MultiplyPoint3x4(camera.transform.position + axis * 100f);
        Vector4 clip = projection * new Vector4(ahead.x, ahead.y, ahead.z, 1f);
        Vector2 uv = clip.w > 1e-4f ? new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f) : new Vector2(0.5f, 0.5f);
        float radius = 0.5f * Mathf.Tan(_device.FieldDegrees * 0.5f * Mathf.Deg2Rad) * projection.m11;
        // The tube's whole field about its axis: only until the glass's own circle is known (FusionField, as the lenses are
        // drawn, later in the frame). From then on the last frame's glass circle stands until this frame's replaces it:
        // set here every frame, this far larger circle about another centre was what a mod read that looked before the
        // lenses were drawn (K-ENVG-B's rectangle, 2026-11-22: "太大了，而且也没有居中"; this 0.414 of the height about
        // 0.490, the glass 0.298 about 0.563).
        _fusionCone = new Vector3(uv.x, uv.y, radius);
        if (_fusionFieldFrame != Time.frameCount - 1 || _fusionFieldOf != _device.Node)
            Interop.EyepieceFusion.FieldInMainUv = new Vector4(uv.x, uv.y, radius, camera.aspect);
        Interop.EyepieceFusion.Deployed = _flip;
        Interop.EyepieceFusion.ClaimedFrame = Time.frameCount;
        Interop.EyepieceFusion.ClaimedDevice = _device.Node;
        if (_fusionClaimNoted != _device.Node)
        {
            _fusionClaimNoted = _device.Node;
            NewCameraPlugin.Log?.LogInfo($"NVG fusion: the eyepieces claim this device for another mod's picture (field at {uv.x:F3},{uv.y:F3}, radius {radius:F3}; its picture {(Interop.EyepieceFusion.HandlerOf(_device.Node) != null ? "offered" : "not offered yet")}).");
        }
    }
    private static string? _fusionClaimNoted;

    // Another mod's clip-on hung before one of this goggle's objectives (EyepieceFusion.FitClipOn; K-ECOTI's): the frame
    // claimed for it, its picture into the tube it hangs before alone (the tubes nearest it across the device), the
    // field given its own cone about that tube's axis.
    private static bool ClaimClipOn(Camera camera)
    {
        if (_deviceRoot == null || _body == null)
            return false;
        string? name = Interop.EyepieceFusion.ClipOnOn(_deviceRoot, out Transform? fitted, out float fieldDegrees);
        if (name == null || fitted == null)
            return false;
        float across = _body.InverseTransformPoint(fitted.position).x, nearest = float.MaxValue;
        int tube = 0;
        for (int i = 0; i < _device.Tubes.Length && i < 4; i++)
        {
            float off = Mathf.Abs(_device.Tubes[i].Centre.x - across);
            if (off < nearest - 1e-5f)
            {
                nearest = off;
                tube = i;
            }
        }
        if (tube >= _lensRest.Length || PodGone(tube) >= 1f)
            return false;
        for (int i = 0; i < 4; i++)
            _fusedTubes[i] = i < _device.Tubes.Length && Mathf.Abs(_device.Tubes[i].Centre.x - across) <= nearest + 0.001f ? 1f : 0f;
        Vector3 axis = -_lensRest[tube].forward;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        Vector3 ahead = camera.worldToCameraMatrix.MultiplyVector(axis);
        Vector4 clip = projection * new Vector4(ahead.x, ahead.y, ahead.z, 0f);
        Vector2 uv = clip.w > 1e-4f ? new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f) : new Vector2(0.5f, 0.5f);
        float radius = 0.5f * Mathf.Tan(fieldDegrees * 0.5f * Mathf.Deg2Rad) * projection.m11;
        // (Its cone about the tube's axis only until its place in its own eyepiece is known, as the lenses are drawn,
        // later in the frame: FusionField; from then on the last frame's stands till this frame's replaces it.)
        _fusionCone = new Vector3(uv.x, uv.y, radius);
        _clipTube = tube;
        _clipShare = Mathf.Clamp01(fieldDegrees / Mathf.Max(_device.FieldDegrees, 1f));
        _clipHostCone = 0.5f * Mathf.Tan(_device.FieldDegrees * 0.5f * Mathf.Deg2Rad) * projection.m11;
        if (_fusionFieldFrame != Time.frameCount - 1 || _fusionFieldOf != name)
            Interop.EyepieceFusion.FieldInMainUv = new Vector4(uv.x, uv.y, radius, camera.aspect);
        Interop.EyepieceFusion.Deployed = _flip;
        Interop.EyepieceFusion.ClaimedFrame = Time.frameCount;
        Interop.EyepieceFusion.ClaimedDevice = _device.Node;
        Interop.EyepieceFusion.ClaimedClipOn = name;
        _fusionKey = name;
        _clipOnClaimed = true;
        if (_clipOnNoted != name || _clipOnNotedOn != _device.Node)
        {
            _clipOnNoted = name;
            _clipOnNotedOn = _device.Node;
            NewCameraPlugin.Log?.LogInfo($"NVG fusion: the clip-on {name} on {_device.Node} claimed, before tube {tube} ({across * 1000f:F1} mm across, tubes {_fusedTubes}); its {fieldDegrees:F0} degree field at {uv.x:F3},{uv.y:F3}, radius {radius:F3}.");
        }
        return true;
    }
    private static string? _clipOnNoted, _clipOnNotedOn;
    private static int _clipTube;       // the tube the clip-on hangs before
    private static float _clipShare;    // its field as a share of the tube's
    private static float _clipHostCone; // the tube's own field's radius on the screen (of the height)

    internal static void OnCameraPostRender(Camera camera)
    {
        if (camera == Camera.main)
            GiveMainSceneBack();
        if (camera == Camera.main)
            ShowDeviceAgain();
    }

    // Just before the main camera renders, when its pose for the frame is final: each eye, moved apart as far as the
    // device has flipped down; or, with one camera, the lens map from the main camera (only once the filter is on).
    internal static void OnCameraPreRender(Camera camera)
    {
        // Whatever comes of this frame, the last one's device is not drawn again, on whichever camera it hangs on.
        if (camera == _directCamera)
            ClearDirect();
        if (camera != Camera.main || _body == null || _binocularMaterial == null || _lensNodes.Length == 0)
            return;
        Material binocular = _binocularMaterial;
        Transform view = camera.transform;
        Vector3 position = view.position;
        binocular.SetMatrix(NightVisionVpId, camera.nonJitteredProjectionMatrix * camera.worldToCameraMatrix);
        for (int i = 0; i < TubeAxisIds.Length; i++)
            binocular.SetVector(TubeAxisIds[i], -_lensNodes[i].forward);
        float half = _device.FieldDegrees * 0.5f;
        binocular.SetVector(TubeFieldId, new Vector4(Mathf.Cos((half + TubeFieldFade) * Mathf.Deg2Rad), Mathf.Cos(half * Mathf.Deg2Rad), 0f, 0f));
        // (A screen's field: a rectangle, level with the camera; NvgEyes.cginc, tubeField.)
        Vector2 rect = _device.FieldRect;
        binocular.SetVector(TubeRectId, rect.x > 0f
            ? new Vector4(Mathf.Tan(rect.x * 0.5f * Mathf.Deg2Rad), Mathf.Tan(rect.y * 0.5f * Mathf.Deg2Rad), TubeRectFade, 0f) : Vector4.zero);
        binocular.SetVector(TubeUpId, view.up);
        if (rect.x > 0f && _rectNoted != _device.Node)
        {
            _rectNoted = _device.Node;
            NewCameraPlugin.Log?.LogInfo($"NVG eyepieces: {_device.Node}'s field a rectangle of {rect.x:F0} by {rect.y:F0} degrees about each tube's axis (the camera's own field {Camera.VerticalToHorizontalFieldOfView(camera.fieldOfView, camera.aspect):F0} by {camera.fieldOfView:F0}).");
        }

        ShadeMain(camera, !_direct);
        if (!_direct)
            ProbeStencil(camera);
        if (!LensesOn(camera))
            return;

        // The device as it truly stands from the camera (NightVisionPrecise.cs).
        if (!_direct)
            PrepareRelative(camera);
        RenderLenses(camera, binocular, view, position);
        // Thermal goggles' picture, for the lenses (LensPicture).
        if (_device.Thermal && Active && !_thermalOnMain)
            RenderThermal(camera);
    }

    private static void RenderLenses(Camera camera, Material binocular, Transform view, Vector3 position)
    {
        Vector3 right = view.right;
        // The eyes by where the eyepieces stand down before them, whether or not a pod has swung one away.
        Vector3 placeA = FromCamera(_lensRest[CentreLensA], position).GetColumn(3), placeB = FromCamera(_lensRest[CentreLensB], position).GetColumn(3);
        float a = Vector3.Dot(placeA, right), b = Vector3.Dot(placeB, right);
        NoteEyes(a + CameraPosition.Horizontal, b + CameraPosition.Horizontal);
        // Which side a monocular's eye is (TwoEyes.cs): its lens across from the head's middle.
        if (_device.Monocular)
            TwoEyes.SeeLens(a + CameraPosition.Horizontal);
        // A monocular is before one eye only: over the other eye's half the device fades out toward the edge. Which eye, by where its lens
        // is from the head's middle, not from the camera: gone over to the aiming eye, the camera is right behind a lens before that eye.
        // A binocular with one eyepiece swung away on its pod (the ENVG-B's): seen as when both are down, each eye's half
        // from that eye brought in toward the middle, the camera where it is (the user's, 2026-11-17: the camera went over
        // to the one left, then the one left alone over the whole frame looked like the whole device moved); both swung all
        // the way away, the game draws the device (LensesOn).
        bool podMono = PodMonocular(out int lone);
        // (Which side the bare eye is, for its gun and sight while it aims: TwoEyes.cs.)
        if (podMono)
            TwoEyes.SeeLens((lone == CentreLensA ? a : b) + CameraPosition.Horizontal);
        // A monocular (the PVS-14) as the others (the user's, 2026-11-18: the camera went over to its eyepiece, which no other
        // goggle does): the camera where it is, its eye's half from that eye brought in toward its eyepiece by the set
        // share, the other eye's half from that eye, as far the other side of the head's middle, brought in alike.
        bool monocularHalves = _device.Monocular && _direct;
        bool asMonocular = _device.Monocular && !monocularHalves;
        binocular.SetFloat(LensClearId, LensReflections ? 0f : 1f);
        binocular.SetVector(EyeSpanId, new Vector4(1f, 1f, 0f, 0f));
        binocular.SetFloat(OverscanId, Overscan);
        _eyesFrame = Time.frameCount;

        // Each eye the set share of the way from the camera to its lens: the camera gone over to the aiming eye (CameraPosition) is
        // that eye, its eyepiece straight ahead in the middle of the frame, the other off to its side. They stay back of the
        // camera when it has come forward to a sight (NightVisionScope.cs). Where the game draws the device, from the camera.
        Vector3 back = view.forward * ScopePullback;
        // A goggle before one eye, the bare eye aiming: how the two eyes' views of the device are laid together, all of it
        // from TwoEyes.Device (TwoEyesBlend.cs); nothing of it is worked out here.
        _blend = TwoEyes.Device(_direct, _device.Monocular);
        float eyesOwn = _blend.Apart;
        _goggleEyeRight = _device.Monocular ? a + CameraPosition.Horizontal > 0f : podMono && (lone == CentreLensA) != (a <= b);
        binocular.SetVector(BareOverId, new Vector4(_blend.BareBeforePicture, _goggleEyeRight ? 0f : 1f, 0f, 0f));
        float comesIn = Mathf.Lerp(EyeShift, 1f, eyesOwn) * _split;
        Vector3 EyeShiftOf(float lens) => _direct && !asMonocular ? right * (lens * comesIn) - back : Vector3.zero;
        Vector3 leftShift = EyeShiftOf(a <= b ? a : b), rightShift = EyeShiftOf(a <= b ? b : a);
        // An eyepiece swung away on its pod (the ENVG-B's): that side is no longer brought in toward the middle, it goes
        // back to how the camera itself sees the device, as far as its own eyepiece has swung (each side by its own pod:
        // by how far the two were apart together, the side already up sprang in and out as the second swung; the user's,
        // 2026-11-18). Both up, both sides are the camera's, which is how the game then draws the device.
        // The device's parts follow (NightVisionDirect.PieceShift): the eyepiece still down from its own eye in both
        // halves, the rest from the camera in both, so the halves show the same thing where they meet (each half whole
        // from its own side, parts of the device were missing between the eyepiece left and the one swung up).
        int leftTube = a <= b ? CentreLensA : CentreLensB, rightTube = a <= b ? CentreLensB : CentreLensA;
        _sideGone[0] = _direct ? PodGone(leftTube) : 0f;
        _sideGone[1] = _direct ? PodGone(rightTube) : 0f;
        for (int side = 0; side < 2; side++)
        {
            int tube = side == 0 ? leftTube : rightTube;
            Transform? pod = tube < _lensPods.Length ? _lensPods[tube] : null;
            int which = pod != null ? System.Array.IndexOf(_device.Pods, pod.name) : -1;
            _sidePods[side] = pod != null ? pod.name : null;
            _sideCups[side] = which >= 0 && which < _device.CupPods.Length ? _device.CupPods[which] : null;
        }
        // (Not while the bare eye aims, by how far the eyes are apart: then each half is its own eye's whole view of the device, the eyes at
        // their own places, the bare eye's laid over the goggle eye's; with both halves made alike there was no second
        // view left to lay over, the user's, 2026-11-18.)
        _sideGone[0] *= 1f - _blend.Apart;
        _sideGone[1] *= 1f - _blend.Apart;
        // (Each eye as it stands with both eyepieces down, for how far the two fields are apart, below.)
        // (And not held back of a camera come forward to a sight, 1.9.16: aiming, the eyes stood some 78 mm from their
        // glasses for 31, the glasses 22 degrees together for 55, and counted as apart on and off, lines turned up to 18
        // degrees in the log of 2026-11-23: the ENVG-B's fused rectangle off its glass while aiming, the user's, "用
        // ENVG-B 瞄准的时候，热融合区域偏了".)
        Vector3 leftDown = leftShift + back, rightDown = rightShift + back;
        leftShift = Vector3.Lerp(leftShift, -back, _sideGone[0]);
        rightShift = Vector3.Lerp(rightShift, -back, _sideGone[1]);
        bool monocularLeft = a + CameraPosition.Horizontal <= 0f;
        if (monocularHalves)
        {
            // (The other eye: across from the camera, its eyepiece's place mirrored in the head's middle.)
            Vector3 own = EyeShiftOf(a), other = EyeShiftOf(-a - 2f * CameraPosition.Horizontal);
            leftShift = monocularLeft ? own : other;
            rightShift = monocularLeft ? other : own;
        }
        Vector3 leftPlace = a <= b ? placeA : placeB, rightPlace = a <= b ? placeB : placeA;
        // An eye is never let past its eyepiece: brought so near (the mount slid toward the face, the head turned), the
        // camera went in past the lens's mouth and the lenses had nothing to show from there (the tube's inside, the
        // scene beyond its far end). Held back along the tube's axis to EyeClear in front of the eyepiece's mouth.
        Transform leftLens = _lensRest[a <= b ? CentreLensA : CentreLensB], rightLens = _lensRest[a <= b ? CentreLensB : CentreLensA];
        if (!monocularHalves || monocularLeft)
            leftShift = KeptClear(leftLens, position, leftShift);
        if (!monocularHalves || !monocularLeft)
            rightShift = KeptClear(rightLens, position, rightShift);
        // The bare eye leading: its view from where the goggle eye's is drawn, out to its own place with the aim. (From
        // that eye's place as held clear of its eyepiece, here: taken before that, 1.8.142, the bare eye's view was drawn
        // from inside the eyepiece, a black disc over the whole picture with a small hole in it: the user's video,
        // 2026-11-19.)
        if (monocularHalves)
        {
            Vector3 goggle = monocularLeft ? leftShift : rightShift, bare = monocularLeft ? rightShift : leftShift;
            bare = Vector3.Lerp(goggle, bare, _blend.BareOut);
            // (And never past the eyepiece's mouth on the way: the bare eye's own place is not held back as the goggle
            // eye's is, and between the two the eye went in past the mouth, beside the lens: the hole the user knows from
            // an eye too near, 2026-11-19. Both eyes stand as far back of the eyepiece.)
            bare = KeptClear(monocularLeft ? leftLens : rightLens, position, bare);
            if (monocularLeft)
                rightShift = bare;
            else
                leftShift = bare;
        }
        // The two views meet halfway between where each eye sees its own eyepiece, not at the frame's middle: with the camera over
        // at one eye, its eyepiece is in the middle and the other's off to the side.
        // By each eyepiece's glass's centre (its lens floor), not its node (the ENVG-B's stand off its glass): halfway between
        // where each eye sees its own eyepiece's middle (the user's, 2026-11-17).
        Vector3 GlassOf(int tube) => tube < _lensFloors.Length ? FromCamera(_lensRest[tube], position).MultiplyPoint3x4(_lensFloors[tube]) : (Vector3)FromCamera(_lensRest[tube], position).GetColumn(3);
        Vector3 leftGlass = GlassOf(a <= b ? CentreLensA : CentreLensB), rightGlass = GlassOf(a <= b ? CentreLensB : CentreLensA);
        // Each tube's field about the middle of its own eyepiece, once the two eyepieces stand apart on the screen (1.9.5).
        // The lenses' picture is the main camera's, each line of sight showing what lies along it, each tube's field a
        // cone about its axis: right while the two eyepieces are one round field about that axis. With the goggle far
        // from the eyes they stand apart, each off to its side of the axis, and each showed only the inner edge of the
        // field, a sliver of it at 40 degrees, and of a clip-on's circle (the user's, 2026-11-23: "它应该是在自己所在的
        // 那个夜视仪物镜的中间位置才对"). An eye at an eyepiece is on its tube's axis: so each eye's lines are turned by what
        // takes the middle of its glass to its tube's axis (NvgEyes.cginc, _EyeAim), as far as the two glasses stand
        // apart: not at all while they overlap (their middles nearer than 0.8 of their radii together), in full once
        // they no longer touch. (By how far the glasses stand off the axis against the field, 1.9.6: brought near the
        // eyes the glasses are large and far apart in angle, and counted as apart while overlapping: the one picture
        // became two side by side, the user's, 2026-11-23, "怎么离近了以后画面变成两个了？".) A monocular, never (1.9.13;
        // always, 1.9.5 to 1.9.12): turned, a line through its glass shows the frame somewhere else, and with the device
        // near and off the axis that was where the device itself is drawn into the frame: its own eyepiece ring inside
        // its picture (the user's, 2026-11-23: "夜视仪里面还能看到一个夜视仪"). Its lines are the screen's own again.
        Vector3 leftLine = (leftGlass - leftShift).normalized, rightLine = (rightGlass - rightShift).normalized;
        float RadiusOf(int tube, Vector3 glass, Vector3 shift) => tube < _lensFloors.Length
            ? Mathf.Atan2(_lensFloors[tube].w * _lensRest[tube].lossyScale.x, Mathf.Max((glass - shift).magnitude, 1e-4f)) : 0f;
        // (With an eyepiece swung away on its pod, 1.9.14: its side's eye goes back to the camera, the line from there to
        // its glass turns far out, 50 to 65 degrees from the other's in the log of 2026-11-23, and the two counted as
        // apart on and off: the eyepiece left had its lines turned 7 to 16 degrees and its fused rectangle moved off its
        // glass, the user's, 2026-11-23, "另一个目镜的位置偏了". So measured by the eyes as they stand with both down;
        // and with one away the one left is one eye's, as a monocular: not turned.)
        Vector3 leftDownLine = (leftGlass - leftDown).normalized, rightDownLine = (rightGlass - rightDown).normalized;
        float together = RadiusOf(leftTube, leftGlass, leftDown) + RadiusOf(rightTube, rightGlass, rightDown);
        float podAway = Mathf.SmoothStep(0f, 1f, Mathf.Max(PodGone(leftTube), PodGone(rightTube)) * 3f);
        // (Nor sealed thermal goggles, 1.9.22: down, the T-7's eyes stand 7 mm from glasses 64 degrees wide each, the
        // two 111 to 125 degrees apart for 126 to 129 together: on the threshold, apart 0.4 to 0.9 as the head moved, the
        // lines turned 9 to 20 degrees in the log of 2026-11-25, and each half's field stood in over the other's: the
        // user's, "夜视仪画面周围有什么东西把画面挡住了", arcs along the top and bottom of the picture.)
        _fieldApart = _device.Monocular || _device.Thermal ? 0f
            : together > 1e-4f ? Mathf.SmoothStep(0f, 1f, (Vector3.Angle(leftDownLine, rightDownLine) * Mathf.Deg2Rad / together - 0.8f) / 0.2f) * (1f - podAway) : 0f;
        // (Only with the device down and its eyepieces before the eyes: flipping up, an eyepiece's axis turns away from the
        // line to its glass, 99 degrees in the log of 2026-11-23, and the lines were turned by as much.)
        float AimShare(Vector3 axis, Vector3 line) => _fieldApart * Mathf.Clamp01(_split) * (1f - Mathf.SmoothStep(0f, 1f, (Vector3.Angle(axis, line) - 25f) / 15f));
        Vector3 leftAxis = -_lensRest[leftTube].forward, rightAxis = -_lensRest[rightTube].forward;
        Vector3 aimLeft = (leftAxis - leftLine) * AimShare(leftAxis, leftLine);
        _aimRight = (rightAxis - rightLine) * AimShare(rightAxis, rightLine);
        binocular.SetVector(EyeAimIds[0], aimLeft);
        binocular.SetVector(EyeAimIds[1], _aimRight);
        if (!_fieldApartNoted || Mathf.Abs(_fieldApart - _fieldApartWas) > 0.5f)
        {
            _fieldApartNoted = true;
            _fieldApartWas = _fieldApart;
            NewCameraPlugin.Log?.LogInfo($"NVG eyepieces: their fields {(_fieldApart > 0.5f ? "each about its own eyepiece" : "one round field")} (apart {_fieldApart:F2}, a pod away {podAway:F2}; the glasses {Vector3.Angle(leftDownLine, rightDownLine):F1} degrees apart, {together * Mathf.Rad2Deg:F1} their radii together; lines turned {Vector3.Angle(leftLine, leftLine + aimLeft):F1} and {Vector3.Angle(rightLine, rightLine + _aimRight):F1} degrees).");
        }
        float split = asMonocular ? 0.5f : Mathf.Clamp(0.5f * (Across(camera, leftGlass - leftShift) + Across(camera, rightGlass - rightShift)), 0.2f, 0.8f);
        // One eyepiece swung away: the halves meet past the far side of the one left, its cup and housing about its glass
        // (twice the glass's radius), as its own eye sees it: it is seen whole (cut nearer, it was cut through, the user's,
        // 2026-11-17), the other eye's half from there on.
        if (monocularHalves)
        {
            // The whole device as the goggle's eye sees it, all over the frame (cut past its eyepiece, the rest of its body
            // was missing: the user's, 2026-11-18); the other eye's view of it only as that eye aims (NvgDirect, _DirectOver).
            split = monocularLeft ? 2f + Overscan : -1f - Overscan;
        }
        // The eyepieces' picture (the composite's own split): with one eyepiece swung away the one left has the whole frame,
        // eased over the first third of the other's swing (cut where the device's halves meet, half its night vision was
        // missing: the user's, 2026-11-18). The device's halves keep the split above.
        float pictureSplit = split;
        if (podMono)
        {
            bool loneLeft = (lone == CentreLensA) == (a <= b);
            float away = Mathf.SmoothStep(0f, 1f, PodGone(loneLeft ? rightTube : leftTube) * 3f);
            pictureSplit = Mathf.Lerp(split, loneLeft ? 1f + Overscan : -Overscan, away);
        }
        binocular.SetFloat(SplitId, pictureSplit);

        int width = Mathf.Max(Screen.width, 1), height = Mathf.Max(Screen.height, 1);
        // Each pixel's line of sight, by the view turned but not moved (as if at the world's origin): with the place in it, far out
        // in a map, the inverse loses so much to rounding that the lines of sight, and the lenses' picture with them, jump about.
        Matrix4x4 turned = camera.worldToCameraMatrix;
        turned.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        Matrix4x4 projection = Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, EyeNear, LensMapRange);
        Matrix4x4 inverse = (Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, RayNear, RayFar) * turned).inverse;
        for (int side = 0; side < 2; side++)
        {
            Vector3 shift = side == 0 ? leftShift : rightShift;
            // 24-bit depth: with the near plane down to a hundredth of a millimetre a 16-bit one told the housing from the card
            // before it only to some millimetres.
            RenderTexture lensMap = Target(_lensMaps, side, Mathf.Max((int)(width * LensMapScale), 8), Mathf.Max((int)(height * LensMapScale), 8), 24,
                RenderTextureFormat.ARGB32, "Justachae NVG lens map");
            RenderTexture glassMap = Target(_glassMaps, side, lensMap.width, lensMap.height, 0, RenderTextureFormat.RG16, "Justachae NVG glass map");
            // Until the device is on, no light comes through; but the glass is there all the same, dark, and the scene is not seen
            // through it. With the glass's map cleared too, the lenses were holes while the device was down and off: switched off
            // while aiming, the game left it down for a couple of seconds before flipping it up, and the room showed in both
            // lenses as through open tubes (a flash), until the device flipped away.
            DrawLensMap(lensMap, glassMap, position, shift, turned, projection);
            if (!Active)
                ClearLensMap(lensMap);
            Transform lens = _lensNodes[side == 0 ? (a <= b ? CentreLensA : CentreLensB) : (a <= b ? CentreLensB : CentreLensA)];
            float eyeBack = Mathf.InverseLerp(DistortionNear, DistortionFar, (lens.position - position - shift).magnitude / lens.lossyScale.x);
            Vector3 eye = position + shift;
            binocular.SetTexture(EyeIds[side], Texture2D.blackTexture);
            binocular.SetTexture(LensIds[side], lensMap);
            binocular.SetTexture(GlassIds[side], glassMap);
            binocular.SetMatrix(EyeInvVpIds[side], inverse);
            binocular.SetVector(EyePosIds[side], new Vector4(eye.x, eye.y, eye.z, eyeBack));
        }
        if (_direct)
            DrawDirect(camera, leftShift, rightShift, split);
        // (The tube the other mod's picture goes into: the device's own first such, or the right eyepiece's.)
        if (_clipOnClaimed)
        {
            // (A clip-on's: its own tube's eyepiece, from that eyepiece's eye.)
            bool clipLeft = _device.Monocular ? monocularLeft : (_clipTube < CentreLensB) == (leftTube == CentreLensA);
            FusionField(camera, clipLeft ? leftTube : rightTube, position + (clipLeft ? leftShift : rightShift));
        }
        else
            FusionField(camera, _device.FusionTubes != null && _device.FusionTubes.Length > 0 ? _device.FusionTubes[0] : a <= b ? CentreLensB : CentreLensA, position + rightShift);
    }
    private static bool _fieldApartNoted;
    private static float _fieldApartWas;
    private const float RayFar = 2f;                               // m: the lines of sight's frame (only their direction counts)
    private static float RayNear => Mathf.Max(EyeNear, RayFar / 2e5f);

    // An eye's shift from the camera, with as much added along its eyepiece's axis as keeps it EyeClear in front of the
    // eyepiece's mouth (its Centre, as the lens node stands), or of its eyecup's lip where it has one: an eye brought
    // inside the eyecup sees only the cup's inside and the tube's (the mount slid toward the face, the head turned).
    // Only as far as the device is down (_split), as the eyes themselves come in: flipping up, the eyepiece turns away
    // over the eye, which then stands "behind" it; held clear in full, the eye was thrown out along the turned axis, the
    // device drawn here seen from there, and it jumped where the game took it back, from the camera.
    private const float EyeClear = 0.002f;
    private static Vector3 KeptClear(Transform lens, Vector3 position, Vector3 shift)
    {
        float scale = Mathf.Max(lens.lossyScale.x, 1e-6f);
        float along = lens.InverseTransformPoint(position + shift).z * scale;
        // A soft eyecup gives against the face: the eye stands within it, as near the glass as without the cup. Held at its
        // lip (the ENVG-B's, 28.5 mm over the throat), the eye was some 15 mm further from the glass with the cups on than
        // off, the glass smaller before it and the two eyes' fields one round one (the user's, 2026-11-22: "不应该让它被顶远").
        float rim = _device.CupYields ? 0f : Mathf.Max(_device.RimHeight, 0f);
        float least = (rim + EyeClear) * scale;
        if (along >= least)
            return shift;
        if (!_eyeHeldNoted)
        {
            _eyeHeldNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG: an eye held back in front of its eyepiece ({along * 1000f:F1} mm from its mouth, kept to {least * 1000f:F1} mm).");
        }
        return shift + lens.forward * ((least - along) * Mathf.Clamp01(_split));
    }
    private static bool _eyeHeldNoted;

    // A claimed fusion device: the tube the picture goes into (the right eyepiece; on a device with two tubes to an
    // eyepiece, the one of them it names) as the right eye sees it this frame, for EyepieceFusion.FieldInMainUv.
    // The eyepiece's own glass (its rim, as the lens node stands), projected from where this file puts the eye: the
    // fused rectangle is sized and placed by it, and so stays still against the eyepiece, as if fixed to it. Not the
    // narrowest of what the eye looks through (lens, eyecup throat, lip) nor cut by the tube's field: which of those
    // was narrowest changed with the eye (aimed or not, eyecups on or off), and the rectangle grew and shrank against
    // the eyepiece. Where the cup's lip or throat hides some of it, it is hidden there like the rest of the picture.
    private static void FusionField(Camera camera, int rightLens, Vector3 eye)
    {
        if (!_device.Fusion && !_clipOnClaimed || Interop.EyepieceFusion.ClaimedFrame != Time.frameCount || rightLens >= _lensNodes.Length)
            return;
        Transform lens = _lensNodes[rightLens];
        float scale = lens.lossyScale.x;
        Vector4 floor = _lensFloors[rightLens];
        Vector3 glass = lens.TransformPoint(new Vector3(floor.x, floor.y, floor.z));
        float rim = floor.w * scale;
        Matrix4x4 toView = camera.worldToCameraMatrix, projection = camera.nonJitteredProjectionMatrix;
        float aspect = camera.aspect;
        // A place, as seen from the eye with the camera's turn, in uv of the screen's height (x stretched by the aspect).
        bool Seen(Vector3 place, out Vector2 uv)
        {
            Vector3 view = toView.MultiplyVector(place - eye);
            Vector4 clip = projection * new Vector4(view.x, view.y, view.z, 0f);
            uv = clip.w > 1e-6f ? new Vector2((clip.x / clip.w * 0.5f + 0.5f) * aspect, clip.y / clip.w * 0.5f + 0.5f) : Vector2.zero;
            return clip.w > 1e-6f;
        }
        if (!Seen(glass, out Vector2 centre) || !Seen(glass + lens.right * rim, out Vector2 across) || !Seen(glass + lens.up * rim, out Vector2 up))
            return;
        // The rim seen at a slant is an ellipse; its two radii from the centre, along the glass's own axes, span it: the
        // circle of the same area.
        Vector2 u = across - centre, v = up - centre;
        float radius = Mathf.Sqrt(Mathf.Abs(u.x * v.y - u.y * v.x));
        // What of the tube's picture the eye in fact sees is where the glass and the tube's own field (its cone about its
        // axis, ClaimFusion) both are: the picture is at infinity, the glass only a window on it. With the eye far from the
        // glass the glass is the smaller, and the circle is the glass's (the rectangle fixed to the eyepiece, the user's
        // choice of 2026-11-14). With the goggle brought close (the mounts' longer travel, 1.8.181) the glass grew to twice
        // the field and stood off to one side of it (20 mm from the eye: 0.55 of the height about 0.62, the field 0.26
        // about 0.49), and the rectangle with it, twice too large and to the right (the user's, 2026-11-22, their own
        // marking of where it should be fitting the field). So: the smaller of the two radii, about the field's centre
        // as far as that keeps the circle within the glass.
        Vector2 glassCentre = centre;
        float glassRadius = radius;
        var coneCentre = new Vector2(_fusionCone.x * aspect, _fusionCone.y);
        if (_clipOnClaimed)
        {
            // A clip-on's circle (1.9.7): in the middle of its own tube's eyepiece as that eye sees it, and its field's
            // share of what that eyepiece shows of the tube's (30 of 40 degrees: three quarters of the glass where the
            // glass is the narrower), never more than its own field. About the tube's axis at its full 30 degrees it
            // stood, with the two eyepieces one round field, between the two and nearly as large as either (the
            // user's, 2026-11-23: "热融合区域怎么可能占这么大面积还在两个物镜的中键位置？"). Given as where in the picture
            // that is: the picture is shown moved by _fieldApart of the way from the glass's middle to the axis.
            // (1.9.9) Not the glass's middle whatever the distance: about the middle of what that eyepiece shows of its
            // tube's field, the circle where its glass and the tube's cone about its axis both are (as a fusion
            // device's, below), and its field's share of that circle's radius. Far from the eye the glass is the
            // narrower: the glass's middle, and smaller the further. Brought near, the glass grows past the field,
            // and the circle goes over toward the axis, the middle of the one round picture, at its own 30 degrees
            // (the user's, 2026-11-23: "应该会随着目镜变近而像中间靠拢的，而且应该是离得越远越小").
            // (1.9.10) By the largest circle within what is seen of the tube's field, of two: within this eyepiece's own
            // glass and the field (its middle halfway along where the two overlap), or, while the two eyepieces are
            // one picture, about the axis within both glasses together and the field (out to where the two glasses'
            // edges cross). The larger of the two, eased from one to the other. Far, the glasses small and apart: its
            // own glass's. Near, the two glasses one round field: the axis, the picture's middle. By the own glass
            // alone (1.9.9) it stayed off to its side in the one round picture, each glass there about as large as
            // the field and 7 degrees off the axis (the user's, 2026-11-23: "离近的时候又感觉没居中").
            float field = _clipHostCone;
            float off = (coneCentre - glassCentre).magnitude;
            float own = Mathf.Clamp(0.5f * (glassRadius + field - off), 0f, Mathf.Min(glassRadius, field));
            Vector2 toAxis = off > 1e-5f ? (coneCentre - glassCentre) / off : Vector2.zero;
            // (The overlap's middle along the line from the glass's middle to the axis.)
            Vector2 ownCentre = glassCentre + toAxis * (0.5f * (Mathf.Max(-glassRadius, off - field) + Mathf.Min(glassRadius, off + field)));
            float both = off < glassRadius ? Mathf.Min(field, Mathf.Sqrt(glassRadius * glassRadius - off * off)) : 0f;
            float atAxis = _device.Monocular ? 0f
                : Mathf.SmoothStep(0f, 1f, (both - own) / Mathf.Max(own, 1e-4f) / 0.3f + 0.5f) * (1f - _fieldApart);
            float seen = Mathf.Lerp(own, both, atAxis);
            Vector2 onScreen = Vector2.Lerp(ownCentre, coneCentre, atAxis);
            radius = Mathf.Min(_fusionCone.z, _clipShare * seen);
            centre = onScreen - (glassCentre - coneCentre) * _fieldApart;
        }
        else if (_fusionCone.z > 0f)
        {
            // (With the eyepieces apart, 1.9.5, the field stands about the glass's own middle on the screen; and what is
            // given is where in the picture that is, the picture then shown moved by as much: both by _fieldApart.)
            Vector2 shown = Vector2.Lerp(coneCentre, glassCentre, _fieldApart);
            radius = Mathf.Min(glassRadius, _fusionCone.z);
            centre = glassCentre + Vector2.ClampMagnitude(shown - glassCentre, Mathf.Max(glassRadius - radius, 0f));
            centre -= (glassCentre - coneCentre) * _fieldApart;
        }
        Interop.EyepieceFusion.FieldInMainUv = new Vector4(centre.x / aspect, centre.y, radius, aspect);
        _fusionFieldFrame = Time.frameCount;
        _fusionFieldOf = _clipOnClaimed ? _fusionKey : _device.Node;
        if (_fusionFieldNoted != _fusionFieldOf)
        {
            _fusionFieldNoted = _fusionFieldOf;
            NewCameraPlugin.Log?.LogInfo($"NVG fusion: the field given {Interop.EyepieceFusion.FieldInMainUv}: the glass as its eye sees it ({glassCentre.x / aspect:F3}, {glassCentre.y:F3}) radius {glassRadius:F3} (rim {rim * 1000f:F1} mm, {(glass - eye).magnitude * 1000f:F0} mm from the eye), the tube's own field ({_fusionCone.x:F3}, {_fusionCone.y:F3}) radius {_fusionCone.z:F3}.");
        }
    }
    private static string? _fusionFieldNoted;
    private static int _fusionFieldFrame = -10;
    private static Vector3 _fusionCone;   // the tube's own field this frame: its centre (uv) and radius (of the height)
    private static string? _fusionFieldOf;

    // Where across the frame (0 left, 1 right) something at this place from an eye is seen straight on.
    // A monocular's bare eye aiming, 0 to 1 as the aim comes up: the two eyes' views of the device no longer side by side but
    // one over the other all over the frame, the goggle eye's whole, the bare eye's seen through (TwoEyes.BareSeen;
    // NvgDirect.shader, coverage; the user's, 2026-11-18: both eyes' devices seen, as both eyes' guns are).
    private static TwoEyes.DeviceBlend _blend;
    private static bool _goggleEyeRight;
    private static readonly int BareOverId = Shader.PropertyToID("_BareOver");

    // Where the eyes are while a goggle is worn (the user's, 2026-11-19): behind its eyepieces as they stand down before
    // the eyes, by the mount as it is set, not the set distance from the head's middle (CameraPosition.AimEye, which is
    // for when none is worn). There were both: the camera went to aim by the set distance and the device was drawn from
    // behind the eyepiece, a few millimetres apart. Measured while the goggle is all the way down (across from the head's
    // middle, m) and kept for as long as that goggle is worn: flipped up, or the ENVG-B's eyepieces swung away, the eyes
    // are where they were. A monocular's other eye: as far the other side.
    private static Transform? _eyesOf;
    private static float _eyeLeft, _eyeRight;

    private static void NoteEyes(float acrossA, float acrossB)
    {
        if (_body == null || _split < 0.999f)
            return;
        float left, right;
        if (_device.Monocular)
            left = right = Mathf.Abs(acrossA);
        else
        {
            left = -Mathf.Min(acrossA, acrossB);
            right = Mathf.Max(acrossA, acrossB);
        }
        // (Only what an eye's place can be: a mount set far off to one side is not where the eye is.)
        if (left < 0.02f || left > 0.05f || right < 0.02f || right > 0.05f)
            return;
        _eyeLeft = left;
        _eyeRight = right;
        _eyesOf = _body;
    }

    // The aiming eye from the head's middle (m, unsigned) for CameraPosition: side -1 the left eye, 1 the right, eased
    // between as the gun changes shoulders. otherwise: with no goggle worn, or one not yet seen down.
    internal static float EyeFromMiddle(float side, float otherwise) =>
        _body != null && _eyesOf == _body ? Mathf.Lerp(_eyeLeft, _eyeRight, 0.5f * (side + 1f)) : otherwise;
    private static float Across(Camera camera, Vector3 to)
    {
        Transform view = camera.transform;
        float z = Vector3.Dot(to, view.forward);
        if (z <= 1e-4f)
            return 0.5f;
        return 0.5f + 0.5f * camera.projectionMatrix.m00 * Vector3.Dot(to, view.right) / z;
    }

    // Where a transform stands from the head along the world's axes, by the local offsets between them: up from each
    // to the nearest transform they share, and back down to the other. Null if they share none.
    private static Vector3? FromHead(Transform head, Transform target)
    {
        Transform? shared = target;
        while (shared != null && !head.IsChildOf(shared))
            shared = shared.parent;
        if (shared == null)
            return null;
        Matrix4x4 headInShared = Chain(shared, head), targetInShared = Chain(shared, target);
        Vector3 offset = targetInShared.GetColumn(3) - headInShared.GetColumn(3);
        // Along the shared transform's axes, turned (and scaled) to the world's.
        Matrix4x4 sharedTurn = shared.localToWorldMatrix;
        sharedTurn.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        return sharedTurn.MultiplyVector(offset);
    }

    // A transform's place and turn within an ancestor's frame, from the local offsets between them.
    private static Matrix4x4 Chain(Transform ancestor, Transform t)
    {
        Matrix4x4 m = Matrix4x4.identity;
        for (Transform? at = t; at != null && at != ancestor; at = at.parent)
            m = Matrix4x4.TRS(at.localPosition, at.localRotation, at.localScale) * m;
        return m;
    }

    // The game's ambient light, which it lays on through command buffers of each camera's own (the bulk of the light
    // in the dark): the eyes have them as the main camera has, or what they draw comes out near black.
    // Kept as the game makes and destroys them (it announces each), so they never have to be searched for.
    private static readonly System.Collections.Generic.List<AmbientLight> _ambientLights = new System.Collections.Generic.List<AmbientLight>();

    private static void TrackAmbientLights()
    {
        OnRenderObjectConnector.OnRenderObjectConnectorAdd += connector =>
        {
            if (connector is AmbientLight ambient && !_ambientLights.Contains(ambient))
                _ambientLights.Add(ambient);
        };
        OnRenderObjectConnector.OnRenderObjectConnectorRemove += connector =>
        {
            if (connector is AmbientLight ambient)
                _ambientLights.Remove(ambient);
        };
    }

    private static readonly AccessTools.FieldRef<AmbientLight, Dictionary<Camera, AmbientLight.CamSettings>> AmbientCamerasOf =
        AccessTools.FieldRefAccess<AmbientLight, Dictionary<Camera, AmbientLight.CamSettings>>("_cameras");
    private static readonly AccessTools.FieldRef<AmbientLight, bool> AmbientReady = AccessTools.FieldRefAccess<AmbientLight, bool>("_isInitialized");

    // The game's ambient light lays its indoor shade (StencilShadow: where the sky's light and reflection do not reach)
    // and its analytic light sources out for each camera in its LateUpdate, from where the camera stands then: the
    // volumes in its view, and whether it is inside each source. This mod places the camera later, as it culls (at the
    // eyes, turned by the head): the shade was laid out for the game's own camera place and turn, so turning the head
    // swept a dark band across the view and the sky's reflection went unshaded where it should not reach. Laid out again
    // for this camera as it now stands.
    internal static void RedrawAmbient(Camera camera)
    {
        foreach (AmbientLight ambient in _ambientLights)
        {
            if (ambient == null || !ambient.isActiveAndEnabled || !AmbientReady(ambient))
                continue;
            Dictionary<Camera, AmbientLight.CamSettings>? cameras = AmbientCamerasOf(ambient);
            if (cameras != null && cameras.TryGetValue(camera, out AmbientLight.CamSettings settings) && settings != null)
            {
                ambient.DrawStencil(settings, camera);
            }
        }
    }


    private static void ClearLensMap(RenderTexture target)
    {
        RenderTexture active = RenderTexture.active;
        RenderTexture.active = target;
        GL.Clear(true, true, Color.clear);
        RenderTexture.active = active;
    }

    // Which tube each pixel looks into: the device's own opaque meshes drawn black, then a card over each eyecup's
    // opening, just above its rim, on which each line of sight is followed to the lenses (NvgLensMask.shader).
    // Everything as it stands from the eye (the eye at zero, its view turned but not moved; see NightVisionPrecise).
    private static void DrawLensMap(RenderTexture target, RenderTexture glass, Vector3 cameraPlace, Vector3 shift, Matrix4x4 view, Matrix4x4 projection)
    {
        _lensMapMaterial ??= new Material(_lensMaskShader) { name = "Justachae NVG lens map", hideFlags = HideFlags.DontSave };
        _card ??= CardMesh();
        float scale = _body!.lossyScale.x;
        Matrix4x4 fromEye = Matrix4x4.Translate(-shift);
        Vector3 eye = Vector3.zero;
        for (int i = 0; i < _lensNodes.Length; i++)
        {
            Matrix4x4 node = fromEye * FromCamera(_lensNodes[i], cameraPlace);
            Vector3 point = node.GetColumn(3), floor = node.MultiplyPoint3x4(_lensFloors[i]);
            _lensMapMaterial.SetVector(LensIds4[i], new Vector4(point.x, point.y, point.z, _lensFloors[i].w * scale));
            _lensMapMaterial.SetVector(LensFloorIds[i], floor);
            _lensMapMaterial.SetVector(AxisIds[i], _lensNodes[i].forward);
        }
        // A soft eyecup with the eye within it (CupYields, KeptClear): the card, which stands just above the rim, would be
        // behind the eye and never drawn, and no line of sight would find a lens: no glass and no picture at all, only the
        // plain scene through the tubes (1.8.200, the user's: "一带上眼罩就不成像了"). So the card comes down to just before
        // the eye, and the rim, now behind the eye too, is no longer asked whether the line of sight passes it (a line
        // carried back to the rim's plane crosses it anywhere).
        float cardHeight = (_device.RimHeight + _device.CardRaise + 0.001f) * scale, rimRadius = _device.RimRadius * scale;
        float eyeAbove = float.MaxValue;
        _eyeWithinCup = false;
        if (_device.CupYields)
        {
            for (int centre = CentreLensA; centre <= CentreLensB; centre += CentreLensB - CentreLensA)
            {
                Vector3 mouth = (fromEye * FromCamera(_lensNodes[centre], cameraPlace)).GetColumn(3);
                eyeAbove = Mathf.Min(eyeAbove, Vector3.Dot(-mouth, _lensNodes[centre].forward));
            }
            if (eyeAbove < cardHeight + 0.0015f * scale)
            {
                cardHeight = Mathf.Max(eyeAbove - 0.0015f * scale, 0.0003f * scale);
                rimRadius = 1000f;
                _eyeWithinCup = true;
                if (!_cardLoweredNoted)
                {
                    _cardLoweredNoted = true;
                    NewCameraPlugin.Log?.LogInfo($"NVG: the eye within {_device.Node}'s soft eyecup ({eyeAbove / scale * 1000f:F1} mm above its throat, the rim at {_device.RimHeight * 1000f:F1}): the lens map's card brought down to {cardHeight / scale * 1000f:F1} mm.");
                }
            }
        }
        _lensMapMaterial.SetVector(OpeningId, new Vector4(_device.OpeningRadius * scale, rimRadius, _device.RimHeight * scale, 0f));
        _lensMapMaterial.SetVector(EyeId, eye);
        // A thermal goggles' display lit evenly to its edge (the game's T-7 has no falloff); an image tube's dims at its rim.
        _lensMapMaterial.SetVector(OpticsId, new Vector4(EyeRelief * scale, ExitPupil * scale, ExitPupilEdge * scale, _device.Thermal ? 1f : RimLight));

        _lensMapBuffer ??= new CommandBuffer { name = "Justachae NVG lens map" };
        CommandBuffer buffer = _lensMapBuffer;
        buffer.Clear();
        // Both maps at once (the light through the lenses, and the glass), on the lens map's depth.
        _lensTargets[0] = target;
        _lensTargets[1] = glass;
        buffer.SetRenderTarget(_lensTargets, target.depthBuffer);
        buffer.ClearRenderTarget(true, true, Color.clear);
        buffer.SetViewProjectionMatrices(view, projection);
        // Meshes at their transforms as they are now: a renderer hidden from the main camera keeps the pose it had
        // when last drawn, a frame behind the device.
        foreach (Renderer blocker in Blockers())
        {
            Mesh? mesh = blocker.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null)
                continue;
            for (int part = 0; part < mesh.subMeshCount; part++)
                buffer.DrawMesh(mesh, fromEye * FromCamera(blocker.transform, cameraPlace), _lensMapMaterial, part, 0);
        }
        for (int centre = CentreLensA; centre <= CentreLensB; centre += CentreLensB - CentreLensA)
        {
            // An eyepiece swung away on its pod, its tube off: no lens to look into there, only the device. With its card
            // still drawn, the composite and the device drawn into the main camera left a lens-shaped hole in it.
            if (PodGone(centre) >= 1f)
                continue;
            Transform a = _lensNodes[centre], b = _lensNodes[centre + 1];
            Vector3 normal = (a.forward + b.forward).normalized;
            Vector3 pa = (fromEye * FromCamera(a, cameraPlace)).GetColumn(3), pb = (fromEye * FromCamera(b, cameraPlace)).GetColumn(3);
            Vector3 middle = (pa + pb) * 0.5f + normal * cardHeight;
            Vector2 size = _device.CardSize * scale;
            Matrix4x4 card = Matrix4x4.TRS(middle, Quaternion.LookRotation(normal, _body.forward), new Vector3(size.x, size.y, 1f));
            buffer.DrawMesh(_card, card, _lensMapMaterial, 0, 1);
        }
        Graphics.ExecuteCommandBuffer(buffer);
    }

    private static bool _cardLoweredNoted, _eyeWithinCup;

    private static Mesh CardMesh()
    {
        Mesh mesh = new Mesh
        {
            name = "Justachae NVG lens card",
            vertices = new[] { new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f) },
            triangles = new[] { 0, 1, 2, 0, 2, 3 },
            hideFlags = HideFlags.DontSave,
        };
        mesh.RecalculateBounds();
        return mesh;
    }

    // The device with the mount and adapters it hangs from: all that is in the helmet's slot for it. Left in the main
    // camera's view they would come into the picture through the lenses, from where that camera is; and any of it may
    // stand between an eye and the lenses.
    private static Transform DeviceSlot()
    {
        Transform root = _body!.GetComponentInParent<EFT.Visual.NightVisionDevice>()?.transform
            ?? _body.GetComponentInParent<EFT.Visual.ThermalVisionDevice>()?.transform ?? _body.parent ?? _body;
        for (Transform? t = root.parent; t != null && t != t.root; t = t.parent)
        {
            if (t.name.StartsWith("mod_nvg", System.StringComparison.Ordinal))
                root = t;
        }
        return root;
    }

    // The device's own opaque meshes and its mount's, which stand between the eye and the lenses. The see-through ones let the light
    // by: the GPNVG-18 has a clear plate over each eyecup's two lenses.
    private static Renderer[] Blockers()
    {
        if (_blockers.Length > 0 && _blockersOf == _body && System.Array.TrueForAll(_blockers, r => r != null))
            return _blockers;

        Transform root = DeviceSlot();
        _blockers = System.Array.FindAll(root.GetComponentsInChildren<Renderer>(true), Opaque);
        _blockersOf = _body;
        return _blockers;
    }

    private static bool Opaque(Renderer renderer)
    {
        foreach (Material material in renderer.sharedMaterials)
        {
            if (material == null)
                continue;
            if (material.renderQueue > 2500 || material.name.IndexOf("glass", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }
        return true;
    }

    private static RenderTexture Target(RenderTexture?[] targets, int side, int width, int height, int depth, RenderTextureFormat format, string name)
    {
        RenderTexture? target = targets[side];
        if (target != null && target.width == width && target.height == height)
            return target;

        if (target != null)
        {
            target.Release();
            Object.Destroy(target);
        }
        target = new RenderTexture(width, height, depth, format) { name = name + (side == 0 ? " left" : " right") };
        targets[side] = target;
        return target;
    }

    private static void ReleaseEyes()
    {
        DropDirectTextures();
        for (int i = 0; i < 2; i++)
        {
            Drop(_lensMaps, i);
            Drop(_glassMaps, i);
        }
    }

    private static void Drop(RenderTexture?[] targets, int i)
    {
        if (targets[i] == null)
            return;
        targets[i]!.Release();
        Object.Destroy(targets[i]);
        targets[i] = null;
    }
}
