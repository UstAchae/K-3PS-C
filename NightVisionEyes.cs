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
    private static readonly int MonocularId = Shader.PropertyToID("_Monocular");
    private static readonly int SplitId = Shader.PropertyToID("_Split");
    private static readonly int EyeSpanId = Shader.PropertyToID("_EyeSpan");
    private static readonly int LensClearId = Shader.PropertyToID("_LensClear");
    private static readonly int OpticsId = Shader.PropertyToID("_Optics");
    private static readonly int[] EyeIds = { Shader.PropertyToID("_LeftEye"), Shader.PropertyToID("_RightEye") };
    private static readonly int[] LensIds = { Shader.PropertyToID("_LeftLens"), Shader.PropertyToID("_RightLens") };
    private static readonly int[] GlassIds = { Shader.PropertyToID("_LeftGlass"), Shader.PropertyToID("_RightGlass") };
    private static readonly int[] EyeInvVpIds = { Shader.PropertyToID("_EyeInvVP0"), Shader.PropertyToID("_EyeInvVP1") };
    private static readonly int[] EyePosIds = { Shader.PropertyToID("_EyePos0"), Shader.PropertyToID("_EyePos1") };

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
        // Once the device's mount is set for this frame: the camera has been placed by now.
        _direct = _directShader != null && LensesOn(camera) && PrepareRelative(camera);
        if (_direct)
            HideDevice(camera);
        ClaimFusion(camera);
    }

    // A fusion device's eyepieces shown this frame: claimed for EyepieceFusion, with where its right tube looks on the
    // screen and how far it is down. The ENVG-B lays its thermal into the right tube only (the wearer's right, of the
    // two eyepieces the one further along the camera's right); the left stays the tube's own.
    private static Vector4 _fusedTubes;
    private static void ClaimFusion(Camera camera)
    {
        if (!_device.Fusion || _lensNodes.Length == 0 || !LensesOn(camera))
            return;
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
            _fusedTubes[i] = i < _lensRest.Length && Vector3.Dot(_lensRest[i].position, right) > middle ? 1f : 0f;
        Vector3 axis = -rightTube.forward;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        Vector3 ahead = camera.worldToCameraMatrix.MultiplyPoint3x4(camera.transform.position + axis * 100f);
        Vector4 clip = projection * new Vector4(ahead.x, ahead.y, ahead.z, 1f);
        Vector2 uv = clip.w > 1e-4f ? new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f) : new Vector2(0.5f, 0.5f);
        float radius = 0.5f * Mathf.Tan(_device.FieldDegrees * 0.5f * Mathf.Deg2Rad) * projection.m11;
        Interop.EyepieceFusion.FieldInMainUv = new Vector4(uv.x, uv.y, radius, camera.aspect);
        Interop.EyepieceFusion.Deployed = _flip;
        Interop.EyepieceFusion.ClaimedFrame = Time.frameCount;
        if (!_fusionClaimNoted)
        {
            _fusionClaimNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG fusion: the eyepieces claim this device for another mod's picture (field at {uv.x:F3},{uv.y:F3}, radius {radius:F3}; its picture {(Interop.EyepieceFusion.Fuse != null ? "offered" : "not offered yet")}).");
        }
    }
    private static bool _fusionClaimNoted;

    internal static void OnCameraPostRender(Camera camera)
    {
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
        if (_device.Thermal && Active)
            RenderThermal(camera);
    }

    private static void RenderLenses(Camera camera, Material binocular, Transform view, Vector3 position)
    {
        Vector3 right = view.right;
        // The eyes by where the eyepieces stand down before them, whether or not a pod has swung one away.
        Vector3 placeA = FromCamera(_lensRest[CentreLensA], position).GetColumn(3), placeB = FromCamera(_lensRest[CentreLensB], position).GetColumn(3);
        float a = Vector3.Dot(placeA, right), b = Vector3.Dot(placeB, right);
        // A monocular is before one eye only: over the other eye's half the device fades out toward the edge. Which eye, by where its lens
        // is from the head's middle, not from the camera: gone over to the aiming eye, the camera is right behind a lens before that eye.
        // So too a binocular with one eyepiece swung away on its pod (the ENVG-B's): the other, still before its eye, shown
        // whole as a monocular's, over the bare eye's half faded as the PVS-14's (the same see-through).
        // Eased: over the first stretch of the swing the two eyes' views come together into the eyepiece left (going), then
        // shown as a monocular, the bare eye's half fading in over the next (bare).
        bool downA = PodDown(CentreLensA), downB = PodDown(CentreLensB);
        // Both swung away, the one less far is the monocular's while it goes (below), so nothing jumps back.
        int lone = downA != downB ? (downA ? CentreLensA : CentreLensB)
            : !downA && !downB ? (PodAngle(CentreLensA) <= PodAngle(CentreLensB) ? CentreLensA : CentreLensB) : -1;
        int raisedTube = lone == CentreLensA ? CentreLensB : CentreLensA;
        // Both swung away, the second on its way up: over its first stretch the bare half's fade goes again (its tube
        // fading out), over its next the eyes part back to their own (apart), each bare, neither tube working.
        bool both = !downA && !downB;
        float again = both ? PodGone(lone) : 0f, apart = both ? PodBare(lone) : 0f;
        float going = lone >= 0 && !_device.Monocular && BinocularOn ? PodGone(raisedTube) * (1f - apart) : 0f;
        float bare = lone >= 0 ? PodBare(raisedTube) * (1f - again) : 0f;
        int loneSide = lone >= 0 ? ((lone == CentreLensA) == (a <= b) ? 0 : 1) : -1;
        int monocular = !BinocularOn ? -1
            : _device.Monocular ? (a + CameraPosition.Horizontal >= 0f ? 1 : 0)
            : going >= 1f ? loneSide : -1;
        if (lone < 0)
            lone = CentreLensA;
        float loneAcross = lone == CentreLensA ? a : b;
        // The one swung up, for the device drawn into the main camera (NightVisionDirect.OnRaisedPod).
        RaisedPod = RaisedCupPod = null;
        if (monocular >= 0 && !_device.Monocular)
        {
            Transform? raised = _lensPods[lone == CentreLensA ? CentreLensB : CentreLensA];
            int which = raised != null ? System.Array.IndexOf(_device.Pods, raised.name) : -1;
            RaisedPod = raised != null ? raised.name : null;
            RaisedCupPod = which >= 0 && which < _device.CupPods.Length ? _device.CupPods[which] : null;
        }
        float seeThrough = Mathf.Clamp01(MonocularSeeThrough);
        float fade = seeThrough <= 0.5f ? seeThrough * 2f : 1f + (seeThrough - 0.5f) * 6f;
        if (monocular >= 0 && !_device.Monocular)
            fade *= bare;
        binocular.SetVector(MonocularId, new Vector4(monocular >= 0 ? 1f : 0f, monocular, fade, 0f));
        binocular.SetFloat(LensClearId, LensReflections ? 0f : 1f);
        binocular.SetVector(EyeSpanId, new Vector4(1f, 1f, 0f, 0f));
        binocular.SetFloat(OverscanId, Overscan);
        _eyesFrame = Time.frameCount;

        // Each eye the set share of the way from the camera to its lens: the camera gone over to the aiming eye (CameraPosition) is
        // that eye, its eyepiece straight ahead in the middle of the frame, the other off to its side. They stay back of the
        // camera when it has come forward to a sight (NightVisionScope.cs). Where the game draws the device, from the camera.
        // A monocular comes in none of the way once aimed: as the camera goes over to the aiming eye, and back as it returns.
        Vector3 back = view.forward * ScopePullback;
        float bothIn = EyeShift * _split, oneIn = bothIn * (1f - CameraPosition.ViewAimed);
        float comesIn = monocular >= 0 ? oneIn : bothIn;
        Vector3 EyeShiftOf(float lens) => _direct ? right * (lens * comesIn) - back : Vector3.zero;
        Vector3 leftShift = EyeShiftOf(a <= b ? a : b), rightShift = EyeShiftOf(a <= b ? b : a);
        Vector3 leftPlace = a <= b ? placeA : placeB, rightPlace = a <= b ? placeB : placeA;
        if (monocular >= 0)
            leftShift = rightShift = EyeShiftOf(loneAcross);
        // An eye is never let past its eyepiece: brought so near (the mount slid toward the face, the head turned), the
        // camera went in past the lens's mouth and the lenses had nothing to show from there (the tube's inside, the
        // scene beyond its far end). Held back along the tube's axis to EyeClear in front of the eyepiece's mouth.
        Transform leftLens = _lensRest[a <= b ? CentreLensA : CentreLensB], rightLens = _lensRest[a <= b ? CentreLensB : CentreLensA];
        if (monocular >= 0)
            leftLens = rightLens = _lensRest[lone];
        leftShift = KeptClear(leftLens, position, leftShift);
        rightShift = KeptClear(rightLens, position, rightShift);
        // On the way to a monocular: both eyes toward the one it will be.
        if (monocular < 0 && going > 0f)
        {
            Vector3 one = KeptClear(_lensRest[lone], position, _direct ? right * (loneAcross * oneIn) - back : Vector3.zero);
            leftShift = Vector3.Lerp(leftShift, one, going);
            rightShift = Vector3.Lerp(rightShift, one, going);
        }
        // The two views meet halfway between where each eye sees its own eyepiece, not at the frame's middle: with the camera over
        // at one eye, its eyepiece is in the middle and the other's off to the side.
        float split = monocular >= 0 ? 0.5f : Mathf.Clamp(0.5f * (Across(camera, leftPlace - leftShift) + Across(camera, rightPlace - rightShift)), 0.2f, 0.8f);
        // ...and the eye left taking the frame over, its half widening past the far edge.
        if (monocular < 0 && going > 0f)
            split = Mathf.Lerp(split, loneSide == 0 ? 1f + Overscan : -Overscan, going);
        binocular.SetFloat(SplitId, split);

        int width = Mathf.Max(Screen.width, 1), height = Mathf.Max(Screen.height, 1);
        // Each pixel's line of sight, by the view turned but not moved (as if at the world's origin): with the place in it, far out
        // in a map, the inverse loses so much to rounding that the lines of sight, and the lenses' picture with them, jump about.
        Matrix4x4 turned = camera.worldToCameraMatrix;
        turned.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        Matrix4x4 projection = Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, EyeNear, LensMapRange);
        Matrix4x4 inverse = (Matrix4x4.Perspective(camera.fieldOfView, camera.aspect, DirectNear, DirectFar) * turned).inverse;
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
            DrawDirect(camera, leftShift, rightShift, split, new Vector4(monocular >= 0 ? 1f : 0f, monocular, fade, 0f));
        FusionField(camera, a <= b ? CentreLensB : CentreLensA, position + rightShift);
    }

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
        float least = (Mathf.Max(_device.RimHeight, 0f) + EyeClear) * scale;
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

    // A claimed fusion device: its right eyepiece as the right eye sees it this frame, for EyepieceFusion.FieldInMainUv.
    // The eyepiece's own glass (its rim, as the lens node stands), projected from where this file puts the eye: the
    // fused rectangle is sized and placed by it, and so stays still against the eyepiece, as if fixed to it. Not the
    // narrowest of what the eye looks through (lens, eyecup throat, lip) nor cut by the tube's field: which of those
    // was narrowest changed with the eye (aimed or not, eyecups on or off), and the rectangle grew and shrank against
    // the eyepiece. Where the cup's lip or throat hides some of it, it is hidden there like the rest of the picture.
    private static void FusionField(Camera camera, int rightLens, Vector3 eye)
    {
        if (!_device.Fusion || Interop.EyepieceFusion.ClaimedFrame != Time.frameCount || rightLens >= _lensNodes.Length)
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
        Interop.EyepieceFusion.FieldInMainUv = new Vector4(centre.x / aspect, centre.y, radius, aspect);
        if (!_fusionFieldNoted)
        {
            _fusionFieldNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG fusion: the right eyepiece's glass as the right eye sees it: {Interop.EyepieceFusion.FieldInMainUv} (rim {rim * 1000f:F1} mm, {(glass - eye).magnitude * 1000f:F0} mm from the eye).");
        }
    }
    private static bool _fusionFieldNoted;

    // Where across the frame (0 left, 1 right) something at this place from an eye is seen straight on.
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
        _lensMapMaterial.SetVector(OpeningId, new Vector4(_device.OpeningRadius, _device.RimRadius, _device.RimHeight, 0f) * scale);
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
            Vector3 middle = (pa + pb) * 0.5f + normal * ((_device.RimHeight + _device.CardRaise + 0.001f) * scale);
            Vector2 size = _device.CardSize * scale;
            Matrix4x4 card = Matrix4x4.TRS(middle, Quaternion.LookRotation(normal, _body.forward), new Vector3(size.x, size.y, 1f));
            buffer.DrawMesh(_card, card, _lensMapMaterial, 0, 1);
        }
        Graphics.ExecuteCommandBuffer(buffer);
    }

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
