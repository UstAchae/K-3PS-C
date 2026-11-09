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
    internal static float EyeNearPlane = 0.001f;
    private static float EyeNear => Mathf.Clamp(EyeNearPlane, 0.00001f, 0.05f);
    private const float LensMapScale = 1f;   // the lenses' edges are sharp: the map at the eye's own size
    private const float LensMapRange = 1f;
    private const float Overscan = 0.06f;         // each eye's view past the middle of the frame, as a share of it
    // A tube's field, as the half angle off its axis within which a line of sight through its lens finds the
    // picture: wide enough for the whole "8" from where the camera sits behind it, so the lenses go dark only when
    // seen from well off to the side.
    private const float TubeField = 30f, TubeFieldFade = 5f;

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
        if (!NvgDump.UpToo && ((!BinocularOn && !Active) || _eyepieceImage == null || !_eyepieceImage.enabled))
            return false;
        Vector3 mount = _rotator != null ? _rotator.transform.position : _body.position;
        return NvgDump.Layered || (mount - camera.transform.position).sqrMagnitude <= MaxEyeDistance * MaxEyeDistance;
    }

    internal static void OnCameraPreCull(Camera camera)
    {
        LightFace(camera);
        if (camera != Camera.main)
            return;
        NvgDump.PreCull(camera);
        HideIrLaserLights(camera);
        if (_body != null)
            AdjustMount(camera);
        // Once the device's mount is set for this frame: the camera has been placed by now.
        NvgDump.DeviceUp = _flip <= 0.001f;
        StepCompare();
        // The layers' test goes in the free camera too, where the device is placed from the world.
        _direct = !NvgDump.GameDraws && _directShader != null && LensesOn(camera) && (PrepareRelative(camera) || NvgDump.Layered);
        if (_direct)
            HideDevice(camera);
    }

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
        binocular.SetVector(TubeFieldId, new Vector4(Mathf.Cos((TubeField + TubeFieldFade) * Mathf.Deg2Rad), Mathf.Cos(TubeField * Mathf.Deg2Rad), 0f, 0f));

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
        Vector3 placeA = FromCamera(_lensNodes[CentreLensA], position).GetColumn(3), placeB = FromCamera(_lensNodes[CentreLensB], position).GetColumn(3);
        float a = Vector3.Dot(placeA, right), b = Vector3.Dot(placeB, right);
        // A monocular is before one eye only: over the other eye's half the device fades out toward the edge.
        int monocular = _device.Monocular && BinocularOn ? (a >= 0f ? 1 : 0) : -1;
        float seeThrough = Mathf.Clamp01(MonocularSeeThrough);
        float fade = seeThrough <= 0.5f ? seeThrough * 2f : 1f + (seeThrough - 0.5f) * 6f;
        binocular.SetVector(MonocularId, new Vector4(monocular >= 0 ? 1f : 0f, monocular, fade, 0f));
        binocular.SetFloat(LensClearId, LensReflections ? 0f : 1f);
        binocular.SetVector(EyeSpanId, new Vector4(1f, 1f, 0f, 0f));
        binocular.SetFloat(OverscanId, Overscan);
        _eyesFrame = Time.frameCount;

        // Each eye the set share of the way from the camera to its lens: the camera gone over to the aiming eye (CameraPosition) is
        // that eye, its eyepiece straight ahead in the middle of the frame, the other off to its side. They stay back of the
        // camera when it has come forward to a sight (NightVisionScope.cs). Where the game draws the device, from the camera.
        Vector3 back = view.forward * ScopePullback;
        Vector3 EyeShiftOf(float lens) => _direct && NvgDump.ComparePhase == 0 ? right * (lens * EyeShift * _split) - back : Vector3.zero;
        Vector3 leftShift = EyeShiftOf(a <= b ? a : b), rightShift = EyeShiftOf(a <= b ? b : a);
        Vector3 leftPlace = a <= b ? placeA : placeB, rightPlace = a <= b ? placeB : placeA;
        if (monocular >= 0)
            leftShift = rightShift = EyeShiftOf(a);
        // The two views meet halfway between where each eye sees its own eyepiece, not at the frame's middle: with the camera over
        // at one eye, its eyepiece is in the middle and the other's off to the side.
        float split = monocular >= 0 ? 0.5f : Mathf.Clamp(0.5f * (Across(camera, leftPlace - leftShift) + Across(camera, rightPlace - rightShift)), 0.2f, 0.8f);
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
            // Until the device is on, no light comes through.
            if (Active)
                DrawLensMap(lensMap, glassMap, position, shift, turned, projection);
            else
            {
                ClearLensMap(lensMap);
                ClearLensMap(glassMap);
            }
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
    }

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
