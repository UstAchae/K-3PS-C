using UnityEngine;

namespace EFTNewCamera;

// A monocular (the PVS-14) down or flipping, seen with both eyes (the user's, 2026-11-15 to 17). The camera stands at the
// goggle's eye (behind its lens, NightVisionEyes.cs), the bare eye as far the other side of the head's middle; the eyes go
// out to their places as the goggle flips down. One render of the frame; both eyes' guns (with the hands and arms holding
// them) are drawn into its G-buffer by NightVisionDirect.cs, on the two kinds of a chessboard (NvgDirect.shader, gunOf):
//   the goggle eye's is the frame's own: night vision and the objective's blur on it, as on all that eye sees;
//   the bare eye's is drawn unjittered, taken apart before the image effects (FillApart: at the camera's own pixels, the
//   upscale mixes the chessboard), gathered over frames along its own motion (the chessboard turned over each frame, its
//   light's grain smoothed), and laid over the eyepieces' picture after (TakeEye): no night vision, no blur on it.
// The aiming eye's gun is solid, the other's seen through (F12 单筒：非瞄准眼看到的枪的透明度). Under the bare eye's gun the
// goggle eye's own view is kept on half the pixels and made whole from them (its picture and its depth, for the
// eyepieces); behind the aiming bare eye's sight lens the goggle eye's gun is left out. The game's copy of the gun is kept
// to its shadow; the sight's lens is laid by the game while the goggle eye aims, by DrawLensLayer while the bare eye does.
internal static class TwoRenders
{
    // (Also the ENVG-B with one eyepiece swung away on its pod: NightVisionEyepieces.PodMonocular.)
    internal static bool Now => NightVisionEyepieces.DownOrFlipping && NightVisionEyepieces.MonocularNow;

    // F12 单筒：裸眼瞄准时分划的透明度: the bare eye's sight lens seen through (0 solid).
    internal static float BareLensSeeThrough = 0.5f;
    // F12 单筒：非瞄准眼看到的枪的透明度: the gun of the eye not aiming seen through (0 solid).
    internal static float OtherGunSeeThrough = 0.5f;
    private static float LensOpacity => 1f - Mathf.Clamp01(BareLensSeeThrough);
    // How much of each eye's gun is seen, eased over as the aim goes to the other eye (_shareBare: 1 the bare eye aims).
    private static float _shareBare;
    private static float BareGunOpacity => 1f - Mathf.Clamp01(OtherGunSeeThrough) * (1f - _shareBare);
    private static float GoggleGunOpacity => 1f - Mathf.Clamp01(OtherGunSeeThrough) * _shareBare;
    internal static bool BareAims => _shareBare > 0.5f;
    internal static float GoggleOpacityNow => GoggleGunOpacity;

    // The goggle's eye across from the head's middle (m), from where its lens is; the bare eye the other side.
    private static float _goggleEye = 0.03f;
    private static float BareEye => -_goggleEye;
    // As the goggle flips down the eyes go out to their places from where the camera would be, and back as it flips up.
    // An eyepiece swung away on its pod: as far as that swing has gone too.
    private static float Apart => Mathf.SmoothStep(0f, 1f, NightVisionEyepieces.Flip)
        * (NightVisionEyepieces.MonocularWorn || !NightVisionEyepieces.PodMonocular(out _, out float pod) ? 1f : pod);
    internal static float Place(float across) => Now ? Mathf.Lerp(across, _goggleEye, Apart) : across;
    private static Vector3 BareShift(Quaternion turn) => turn * new Vector3((BareEye - _goggleEye) * Apart, 0f, 0f);

    // From NightVisionEyes.RenderLenses: the goggle's lens across from the head's middle.
    internal static void SeeLens(float across)
    {
        if (Now && Mathf.Abs(across) >= 0.01f)
            _goggleEye = across;
    }

    // ---------------------------------------------------------------- the gun, the hands and the arms

    private static Transform? _gunRoot;
    private static EFT.PlayerBody? _bodyOf;
    private static System.Type? _handsType;
    private static System.Reflection.PropertyInfo? _weaponRoot;
    private static readonly System.Collections.Generic.List<Renderer> _gunRenderers = new System.Collections.Generic.List<Renderer>();

    // The gun in hand (its whole model, those switched off too: a laser's parts that come on with it) and the body's
    // hands and arms (the arms are the body's model, its sleeves with it); found again only when the gun or body changes.
    private static bool GatherGun()
    {
        EFT.Player? player = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer;
        object? hands = player?.HandsController;
        if (hands == null)
            return false;
        if (_handsType != hands.GetType())
        {
            _handsType = hands.GetType();
            _weaponRoot = HarmonyLib.AccessTools.Property(_handsType, "WeaponRoot");
        }
        Transform? root = (_weaponRoot?.GetValue(hands) as Transform)?.root;
        if (root == null)
            return false;
        EFT.PlayerBody? body = player?.PlayerBody;
        if (root != _gunRoot || body != _bodyOf || _gunRenderers.Exists(r => r == null))
        {
            HideGun(false);
            _gunRoot = root;
            _bodyOf = body;
            root.GetComponentsInChildren(true, _gunRenderers);
            if (body != null)
            {
                foreach (EFT.EBodyModelPart part in new[] { EFT.EBodyModelPart.Hands, EFT.EBodyModelPart.Body })
                {
                    if (body.BodySkins.TryGetValue(part, out var skin) && skin != null)
                        _gunRenderers.AddRange(skin.GetRenderers());
                }
            }
            _gunRenderers.RemoveAll(r => r is not MeshRenderer && r is not SkinnedMeshRenderer);
        }
        return _gunRenderers.Count > 0;
    }

    // Under the gun in the hands, as gathered (its own model's hierarchy).
    internal static bool IsOnGun(Transform t) => _gunRoot != null && t.IsChildOf(_gunRoot);

    // For NightVisionDirect.DrawGun: the gun to draw this frame, the bare eye's offset from the camera along the world's
    // axes, and that it is put in on the chessboard (shown below 1).
    internal static System.Collections.Generic.List<Renderer>? GunHere(Quaternion turn, out Vector3 shift, out float shown)
    {
        shift = Vector3.zero;
        shown = 0f;
        if (!Now || _gunsFrame != Time.frameCount || _gunRenderers.Count == 0)
            return null;
        shift = BareShift(turn);
        shown = 0.5f;
        return _gunRenderers;
    }

    // Lasers on the gun as the bare eye sees them (NightVisionIr.cs, drawn apart and laid over with its gun).
    internal static bool BareLaserShift(Camera camera, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (!Now || _gunsFrame != Time.frameCount || _gunRenderers.Count == 0)
            return false;
        shift = BareShift(camera.transform.rotation);
        return true;
    }

    // The aimed sight's lens, whichever eye aims (never drawn by its own material with the guns' parts).
    internal static Renderer? SightLens
    {
        get
        {
            try
            {
                return OpticAtWork() ? EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight?.LensRenderer : null;
            }
            catch
            {
                return null;
            }
        }
    }

    // The bare eye's gun's sight lens while that eye aims through it (NightVisionDirect.cs leaves the gun out there).
    internal static Renderer? BareLens
    {
        get
        {
            try
            {
                if (!OpticAtWork() || !BareAims)
                    return null;
                return _lensKept ?? EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight?.LensRenderer;
            }
            catch
            {
                return null;
            }
        }
    }

    // ---------------------------------------------------------------- each frame

    private static bool _hooked;
    private static int _gunsFrame = -1;

    internal static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Application.onBeforeRender += BeforeRender;
        Camera.onPostRender += AfterOptic;
    }

    private static bool OpticAtWork()
    {
        try
        {
            Camera? optic = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.Camera;
            return optic != null && optic.isActiveAndEnabled;
        }
        catch
        {
            return false;
        }
    }

    // Once a frame: once a sight's camera has rendered when one is at work (its picture is what the lens shows), otherwise
    // as rendering begins.
    private static void BeforeRender()
    {
        if (!OpticAtWork())
            PrepareFrame();
    }

    private static void AfterOptic(Camera camera)
    {
        if (OpticPicture.IsCamera(camera))
            PrepareFrame();
    }

    private static void PrepareFrame()
    {
        if (!Now || _gunsFrame == Time.frameCount)
            return;
        _gunsFrame = Time.frameCount;
        Camera? main = Camera.main;
        if (main == null || !main.isActiveAndEnabled || !GatherGun())
            return;
        // Over to the bare eye as the aiming eye goes over to it (CameraPosition: eased as the gun changes shoulders).
        float toward = CameraPosition.AimingEyeAcross / Mathf.Max(CameraPosition.AimEye, 1e-4f) * Mathf.Sign(BareEye);
        _shareBare = Mathf.Clamp01(0.5f + 0.5f * toward);
        HideGun(true);
        FillApart(main);
    }

    // From NewCameraPlugin.EndOfFrame.
    internal static void EndOfFrame()
    {
        HideGun(false);
        _apartBuffer?.Clear();
        NightVisionEyepieces.EmptyGunForward();
    }

    // ---------------------------------------------------------------- the game's copy of the gun, and the sight's lens

    private static bool _gunHidden;
    private static readonly System.Collections.Generic.List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)> _castings = new System.Collections.Generic.List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)>();
    private static SSAAPropagator? _lensOf;
    private static Renderer? _lensKept, _collimatorKept;
    private static Material? _lensMaterialKept;
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Renderer> LensRenderer =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Renderer>("_opticLensRenderer");
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Renderer> CollimatorRenderer =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Renderer>("_collimatorRenderer");
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Material> LensMaterial =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Material>("_opticLensMaterial");

    // The game's copy kept to its shadow (both eyes' guns are drawn into the frame; switched off whole, its shadow went
    // too). While the bare eye aims, the game's lens kept from it (it lays it in from the goggle's eye: DrawLensLayer lays it
    // from the bare eye instead). Back once the frame is done.
    private static void HideGun(bool hide)
    {
        if (hide == _gunHidden)
            return;
        _gunHidden = hide;
        if (hide)
        {
            _castings.Clear();
            foreach (Renderer r in _gunRenderers)
            {
                if (r == null)
                    continue;
                _castings.Add((r, r.shadowCastingMode));
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
        }
        else
        {
            foreach ((Renderer r, UnityEngine.Rendering.ShadowCastingMode casting) in _castings)
            {
                if (r != null)
                    r.shadowCastingMode = casting;
            }
            _castings.Clear();
        }
        SSAAPropagator? propagator = Camera.main != null ? Camera.main.GetComponent<SSAAPropagator>() : null;
        if (hide && propagator != null && BareAims)
        {
            _lensKept = LensRenderer(propagator);
            _collimatorKept = CollimatorRenderer(propagator);
            _lensMaterialKept = LensMaterial(propagator);
            _lensOf = propagator;
            LensRenderer(propagator) = null!;
            CollimatorRenderer(propagator) = null!;
        }
        else if (!hide && _lensOf != null)
        {
            if (LensRenderer(_lensOf) == null)
                LensRenderer(_lensOf) = _lensKept!;
            if (CollimatorRenderer(_lensOf) == null)
                CollimatorRenderer(_lensOf) = _collimatorKept!;
            _lensOf = null;
            _lensKept = _collimatorKept = null;
        }
    }

    // ---------------------------------------------------------------- taking the two eyes apart (NvgTube.shader)

    private const int LensSolidPass = 1, GunAndLensPass = 2, BareViewPass = 3, GoggleViewPass = 4, GoggleFillPass = 5,
        DepthWeighPass = 7, DepthPullPass = 8, BareGatherPass = 9;
    internal const int PartsLayPass = 6;
    private static Material? _material;
    internal static Material? TubeMaterial => _material;
    private static Material? MaterialFor(Shader? tubeShader) =>
        tubeShader == null ? null : _material ??= new Material(tubeShader) { name = "Justachae two eyes", hideFlags = HideFlags.DontSave };

    private static UnityEngine.Rendering.CommandBuffer? _apartBuffer;
    private static Camera? _apartCamera;
    private static RenderTexture? _bareView, _bareNow, _goggleFill, _depthFill, _goggleDepth;
    private static readonly RenderTexture?[] _bareGathered = new RenderTexture?[2];
    private static int _apartFrame = -1, _gatheredFrame = -10, _layFrame = -1;
    private static Texture? _ownDepth;
    private static readonly int ApartTempId = Shader.PropertyToID("_JustachaeGunsApart"), GunTexelId = Shader.PropertyToID("_GunTexel"),
        GoggleShareId = Shader.PropertyToID("_GoggleShare"), BareHistoryId = Shader.PropertyToID("_BareHistory"),
        BareMotionId = Shader.PropertyToID("_BareMotion"), BareHistoryOnId = Shader.PropertyToID("_BareHistoryOn"),
        CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");

    // For the guns' forward parts (NightVisionDirect.DrawGunForward): the bare eye's gun's picture this frame.
    internal static RenderTexture? BareViewNow => _apartFrame == Time.frameCount ? _bareNow : null;

    private static RenderTexture Kept(ref RenderTexture? kept, int w, int h, RenderTextureFormat format, string name, bool mips = false, FilterMode filter = FilterMode.Bilinear)
    {
        if (kept == null || kept.width != w || kept.height != h)
        {
            if (kept != null)
            {
                kept.Release();
                Object.Destroy(kept);
            }
            kept = new RenderTexture(w, h, 0, format) { name = name, hideFlags = HideFlags.DontSave, useMipMap = mips, autoGenerateMips = false, filterMode = filter };
            kept.Create();
        }
        return kept;
    }

    // At the camera's own pixels, after all is drawn and before the image effects (put on after the frame's transparent
    // things): the bare eye's gun made whole (pass 3) and gathered over frames (pass 9); the frame left the goggle eye's
    // alone, its gun seen through by its share (pass 4), what is not known filled from around (pass 5); its depth alike
    // (passes 7, 8). Filled each frame it is wanted, emptied at its end.
    private static void FillApart(Camera main)
    {
        Material? material = _material;
        if (material == null)
            return;
        _apartBuffer ??= new UnityEngine.Rendering.CommandBuffer { name = "Justachae two eyes' guns apart" };
        if (_apartCamera != main)
        {
            _apartCamera?.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, _apartBuffer);
            main.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, _apartBuffer);
            _apartCamera = main;
            NightVisionEyepieces.GunForwardAfter(main);
        }
        int w = Mathf.Max(main.pixelWidth, 1), h = Mathf.Max(main.pixelHeight, 1);
        RenderTexture bareView = Kept(ref _bareView, w, h, RenderTextureFormat.ARGBHalf, "Justachae bare eye's gun");
        var buffer = _apartBuffer;
        buffer.Clear();
        buffer.SetGlobalVector(GunTexelId, new Vector4(1f / w, 1f / h, w, h));
        buffer.SetGlobalFloat(GoggleShareId, GoggleGunOpacity);
        buffer.GetTemporaryRT(ApartTempId, w, h, 0, FilterMode.Point, RenderTextureFormat.DefaultHDR);
        buffer.Blit(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, ApartTempId);
        buffer.Blit(ApartTempId, bareView, material, BareViewPass);
        // Gathered into the one of two kept pictures not holding last frame's.
        RenderTexture? motion = NightVisionEyepieces.BareMotion;
        int at = Time.frameCount & 1;
        RenderTexture gathered = Kept(ref _bareGathered[at], w, h, RenderTextureFormat.ARGBHalf, "Justachae bare eye's gun gathered " + at);
        RenderTexture? last = _bareGathered[1 - at];
        bool history = motion != null && last != null && last.width == w && last.height == h && _gatheredFrame == Time.frameCount - 1;
        buffer.SetGlobalTexture(BareHistoryId, history ? last! : (Texture)Texture2D.blackTexture);
        buffer.SetGlobalTexture(BareMotionId, motion != null ? motion : (Texture)Texture2D.blackTexture);
        buffer.SetGlobalFloat(BareHistoryOnId, history ? 1f : 0f);
        buffer.Blit(bareView, gathered, material, BareGatherPass);
        _bareNow = gathered;
        _gatheredFrame = Time.frameCount;
        RenderTexture fill = Kept(ref _goggleFill, w, h, RenderTextureFormat.ARGBHalf, "Justachae goggle eye's frame to fill", mips: true);
        buffer.Blit(ApartTempId, fill, material, GoggleViewPass);
        buffer.GenerateMips(fill);
        buffer.Blit(fill, UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, material, GoggleFillPass);
        RenderTexture depthFill = Kept(ref _depthFill, w, h, RenderTextureFormat.RGFloat, "Justachae goggle eye's depth to fill", mips: true);
        RenderTexture depth = Kept(ref _goggleDepth, w, h, RenderTextureFormat.RFloat, "Justachae goggle eye's depth", filter: FilterMode.Point);
        buffer.Blit(ApartTempId, depthFill, material, DepthWeighPass);
        buffer.GenerateMips(depthFill);
        buffer.Blit(depthFill, depth, material, DepthPullPass);
        buffer.ReleaseTemporaryRT(ApartTempId);
        _apartFrame = Time.frameCount;
    }

    // At the eyepieces' image effect, before the eyepieces: their steps by the goggle eye's depth (the objective's blur
    // above all), until TakeEye.
    internal static void BeforeEyepieces(RenderTexture scene, Shader? tubeShader)
    {
        _layFrame = -1;
        if (MaterialFor(tubeShader) == null || !Now || _gunsFrame != Time.frameCount || _apartFrame != Time.frameCount || _bareNow == null)
            return;
        if (_goggleDepth != null && Shader.GetGlobalTexture(CameraDepthId) is Texture frameDepth && frameDepth != _goggleDepth)
        {
            _ownDepth = frameDepth;
            Shader.SetGlobalTexture(CameraDepthId, _goggleDepth);
        }
        _layFrame = Time.frameCount;
    }

    // ---------------------------------------------------------------- laying the bare eye's gun over (NvgTube.shader, pass 2)

    private static RenderTexture? _lensLayer, _lensSolid;
    private static UnityEngine.Rendering.CommandBuffer? _lensBuffer;
    private static readonly int OtherId = Shader.PropertyToID("_Other"), LensLayerId = Shader.PropertyToID("_LensLayer"),
        LensMaskId = Shader.PropertyToID("_LensMask"), GunShareId = Shader.PropertyToID("_GunShare"), LensShareId = Shader.PropertyToID("_LensShare"),
        LensUnderAimingId = Shader.PropertyToID("_LensUnderAiming"), GunWhich14Id = Shader.PropertyToID("_GunWhich14"),
        BareLasersId = Shader.PropertyToID("_BareLasers"), NonJitteredProjId = Shader.PropertyToID("NonJitteredProj"),
        CameraPlaceId = Shader.PropertyToID("_WorldSpaceCameraPos");

    // In the eyepieces' image effect, the frame done: the bare eye's gun, its lasers and its sight's lens laid over.
    internal static void TakeEye(RenderTexture into, Shader? tubeShader)
    {
        if (_ownDepth != null)
        {
            Shader.SetGlobalTexture(CameraDepthId, _ownDepth);
            _ownDepth = null;
        }
        if (_layFrame != Time.frameCount || _bareNow == null || MaterialFor(tubeShader) is not Material laying)
            return;
        _layFrame = -1;
        bool lens = DrawLensLayer(into, laying);
        RenderTexture frame = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, frame);
        laying.SetTexture(OtherId, _bareNow);
        laying.SetTexture(LensLayerId, lens ? _lensLayer : Texture2D.blackTexture);
        laying.SetTexture(LensMaskId, lens ? _lensSolid : Texture2D.blackTexture);
        laying.SetFloat(GunShareId, BareGunOpacity);
        // The bare eye's lens: by its F12 see-through while it aims; while the goggle eye aims, by that gun's share, and not
        // over the goggle eye's gun.
        laying.SetFloat(LensShareId, BareAims ? LensOpacity * _shareBare : BareGunOpacity);
        laying.SetFloat(LensUnderAimingId, BareAims ? 0f : 1f);
        laying.SetTexture(GunWhich14Id, NightVisionEyepieces.GunWhichTexture ?? (Texture)Texture2D.blackTexture);
        laying.SetTexture(BareLasersId, NightVisionEyepieces.BareLasers ?? (Texture)Texture2D.blackTexture);
        Graphics.Blit(frame, into, laying, GunAndLensPass);
        RenderTexture.ReleaseTemporary(frame);
    }

    // The bare eye's gun's sight lens, drawn from that eye with the game's own lens material (its picture is the sight
    // camera's, _CamTex): while the bare eye aims, the game's kept from it (HideGun); while the goggle eye aims, the game's
    // own, drawn again from the bare eye, seen from aside. Its mask too (pass 1).
    private static bool DrawLensLayer(RenderTexture into, Material material)
    {
        Camera? camera = Camera.main;
        Renderer? lensRenderer = _lensKept;
        Material? lensMaterial = _lensMaterialKept;
        if (!BareAims && camera != null && camera.GetComponent<SSAAPropagator>() is SSAAPropagator propagator)
        {
            lensRenderer = LensRenderer(propagator);
            lensMaterial = LensMaterial(propagator);
        }
        if (camera == null || lensRenderer == null || lensMaterial == null || !lensRenderer.enabled || !lensRenderer.gameObject.activeInHierarchy || !OpticAtWork())
            return false;
        Vector3 shift = BareShift(camera.transform.rotation);
        Vector3 eye = camera.transform.position + shift, place = camera.transform.position;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        RenderTexture layer = Kept(ref _lensLayer, into.width, into.height, RenderTextureFormat.ARGBHalf, "Justachae bare eye's sight lens");
        RenderTexture solid = Kept(ref _lensSolid, into.width, into.height, RenderTextureFormat.R8, "Justachae bare eye's sight lens mask");
        var buffer = _lensBuffer ??= new UnityEngine.Rendering.CommandBuffer { name = "Justachae bare eye's sight lens" };
        buffer.Clear();
        buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix * Matrix4x4.Translate(-shift), projection);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(eye.x, eye.y, eye.z, 1f));
        buffer.EnableShaderKeyword("TAA");
        buffer.EnableShaderKeyword("NON_JITTERED");
        buffer.SetGlobalMatrix(NonJitteredProjId, GL.GetGPUProjectionMatrix(projection, true));
        buffer.SetRenderTarget(layer);
        buffer.ClearRenderTarget(false, true, Color.clear);
        buffer.DrawRenderer(lensRenderer, lensMaterial);
        buffer.DisableShaderKeyword("NON_JITTERED");
        buffer.DisableShaderKeyword("TAA");
        buffer.SetRenderTarget(solid);
        buffer.ClearRenderTarget(false, true, Color.clear);
        buffer.DrawRenderer(lensRenderer, material, 0, LensSolidPass);
        buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x, place.y, place.z, 1f));
        Graphics.ExecuteCommandBuffer(buffer);
        return true;
    }
}
