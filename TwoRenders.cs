using UnityEngine;

namespace EFTNewCamera;

// The user's experiment (2026-11-15): a monocular (the PVS-14) down or flipping, seen with both eyes, half each. The
// goggle's eye stands behind its lens (NightVisionEyes.cs: the lens across from the head's middle, as last seen from
// that eye's render), the bare eye as far the other side of the head's middle. The main camera renders first, before
// anything else renders (Application.onBeforeRender), from the bare eye, its finished frame kept at the eyepieces'
// image effect; then as ever, from the goggle's eye, its frame mixed half and half with the kept one there
// (NightVisionComposite.cs, before the interface): the goggle and its picture seen whole, the bare eye's view (its gun
// among it) through it at half. Each render is one eye, the camera itself: the device drawn into it and its lens mapped
// from there. A binocular is drawn as ever, its two halves in one frame.
// Since 1.8.14 the frame's own render is the goggle eye's without its gun; the gun is rendered alone from each eye,
// half each (the goggle eye's laid in before its eyepiece, the bare eye's over after); only the aiming eye's gun has
// the sight's picture on its lens.
// TEMP PROBES: each eye's own frame, before the two are mixed, in the lower corners (the left eye's on the left).
internal static class TwoRenders
{
    internal static bool On = true;
    internal static bool Probes = false;
    private const int HalvesPass = 1, OpaquePass = 2, NearPass = 3;   // NvgTube.shader
    private const float NearReach = 2f;   // m: the bare eye's render goes no further

    // The layers the gun in hand is drawn on, found again only when the gun changes.
    private static Transform? _gunRoot;
    private static int _gunLayers;
    private static System.Type? _handsType;
    private static System.Reflection.PropertyInfo? _weaponRoot;
    private static readonly System.Collections.Generic.List<Renderer> _gunRenderers = new System.Collections.Generic.List<Renderer>();

    private static int GunLayers()
    {
        EFT.Player? player = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer;
        object? hands = player?.HandsController;
        if (hands == null)
            return 0;
        if (_handsType != hands.GetType())
        {
            _handsType = hands.GetType();
            _weaponRoot = HarmonyLib.AccessTools.Property(_handsType, "WeaponRoot");
        }
        Transform? root = (_weaponRoot?.GetValue(hands) as Transform)?.root;
        if (root == null)
            return 0;
        EFT.PlayerBody? body = player?.PlayerBody;
        if (root != _gunRoot || body != _handsOf || _gunRenderers.Exists(r => r == null))
        {
            HideGun(false);
            _gunRoot = root;
            _handsOf = body;
            _gunLayers = 0;
            // Those switched off now too (a laser's parts that come on with it): each drawn or hidden as it is then.
            root.GetComponentsInChildren(true, _gunRenderers);
            // The hands and the arms holding it too: one gun, one pair of hands and arms (the user's, 2026-11-15). The arms are
            // the body's model (its sleeves with it), so the body goes along.
            if (body != null)
            {
                foreach (EFT.EBodyModelPart part in new[] { EFT.EBodyModelPart.Hands, EFT.EBodyModelPart.Body })
                {
                    if (body.BodySkins.TryGetValue(part, out var skin) && skin != null)
                        _gunRenderers.AddRange(skin.GetRenderers());
                }
            }
            _gunRenderers.RemoveAll(r => r is not MeshRenderer && r is not SkinnedMeshRenderer);
            foreach (Renderer r in _gunRenderers)
            {
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    _gunLayers |= 1 << r.gameObject.layer;
            }
        }
        return _gunLayers;
    }
    private static EFT.PlayerBody? _handsOf;
    private static readonly int NearReachId = Shader.PropertyToID("_NearReach");
    private static readonly int GunGId = Shader.PropertyToID("_GunG"), GunBId = Shader.PropertyToID("_GunB");
    // Only the aiming eye's gun is seen (the user's, 2026-11-15); as the gun goes over to the other shoulder, the one
    // fades out as the other fades in: how much of each this frame (they add up to one).
    private static float _shareGoggle = 1f, _shareBare;
    // How far the bare eye's gun is seen through (F12 单筒：裸眼瞄准时枪的透明度): 0 solid, 1 not seen; at half, as the two
    // eyes' frames laid together did.
    internal static float BareGunSeeThrough = 0.5f;   // now the bare eye's sight lens's (F12 单筒：裸眼瞄准时分划的透明度)
    // Both eyes' guns seen (the user's, 2026-11-16): the goggle eye's is the frame's own (night vision and the objective's
    // blur on it, as on all it sees), the bare eye's put in on every other pixel, taken apart before the eyepieces and laid
    // over their picture after, by this (F12 单筒：裸眼看到的枪的透明度), with no night vision or blur on it.
    internal const bool BothGuns = true;
    // The aiming eye's gun solid, the other's seen through by this (F12 单筒：非瞄准眼看到的枪的透明度; the user's,
    // 2026-11-16), eased over as the aim goes to the other eye.
    internal static float BareGunTransparency = 0.5f;
    private static float BareGunOpacity => 1f - Mathf.Clamp01(BareGunTransparency) * (1f - _shareBare);
    private static float GoggleGunOpacity => 1f - Mathf.Clamp01(BareGunTransparency) * _shareBare;
    private static readonly int GoggleShareId = Shader.PropertyToID("_GoggleShare");
    // Which eye's gun is put in whole (NvgDirect.shader, gunOf): the aiming eye's.
    internal static bool BareAims => _shareBare > 0.5f;

    // The two eyes' guns taken apart at the camera's own pixels, after all is drawn and before the image effects (the
    // upscale among them mixes the chessboard's pixels, and the diagnostics of 1.8.55 showed the frame at 2560x1440 with the
    // camera's at 1707x960): the bare eye's gun whole kept (_bareView, laid over after the eyepieces, TakeEye); the frame
    // left the goggle eye's alone, its gun seen through by its share. Filled each frame it is wanted, emptied at its end.
    private static UnityEngine.Rendering.CommandBuffer? _apartBuffer;
    private static Camera? _apartCamera;
    private static RenderTexture? _bareView, _goggleFill;
    private const int GoggleFillPass = 17;   // NvgTube.shader
    private static int _apartFrame = -1;
    private static readonly int ApartTempId = Shader.PropertyToID("_JustachaeGunsApart"), GunTexelId = Shader.PropertyToID("_GunTexel");

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
        }
        int w = Mathf.Max(main.pixelWidth, 1), h = Mathf.Max(main.pixelHeight, 1);
        if (_bareView == null || _bareView.width != w || _bareView.height != h)
        {
            if (_bareView != null)
            {
                _bareView.Release();
                Object.Destroy(_bareView);
            }
            _bareView = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { name = "Justachae bare eye's gun", hideFlags = HideFlags.DontSave };
            _bareView.Create();
        }
        var buffer = _apartBuffer;
        buffer.Clear();
        buffer.SetGlobalVector(GunTexelId, new Vector4(1f / w, 1f / h, w, h));
        buffer.SetGlobalFloat(GoggleShareId, GoggleGunOpacity);
        buffer.GetTemporaryRT(ApartTempId, w, h, 0, FilterMode.Point, RenderTextureFormat.DefaultHDR);
        buffer.Blit(UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, ApartTempId);
        buffer.Blit(ApartTempId, _bareView, material, BareViewPass);
        if (_goggleFill == null || _goggleFill.width != w || _goggleFill.height != h)
        {
            if (_goggleFill != null)
            {
                _goggleFill.Release();
                Object.Destroy(_goggleFill);
            }
            _goggleFill = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf) { name = "Justachae goggle eye's frame to fill", hideFlags = HideFlags.DontSave, useMipMap = true, autoGenerateMips = false };
            _goggleFill.Create();
        }
        buffer.Blit(ApartTempId, _goggleFill, material, GoggleViewPass);
        buffer.GenerateMips(_goggleFill);
        buffer.Blit(_goggleFill, UnityEngine.Rendering.BuiltinRenderTextureType.CameraTarget, material, GoggleFillPass);
        buffer.ReleaseTemporaryRT(ApartTempId);
        _apartFrame = Time.frameCount;
    }
    private const int BareViewPass = 15, GoggleViewPass = 16;   // NvgTube.shader
    private static readonly int BareLasersId = Shader.PropertyToID("_BareLasers");
    private static float LensOpacity => 1f - Mathf.Clamp01(BareGunSeeThrough);
    private static float GunOpacity => LensOpacity;
    // The goggle is never seen through: nothing laid over what the camera has nearer than this (m; the gun is further).
    private const float DeviceReach = 0.12f;
    private static readonly int ShareGId = Shader.PropertyToID("_ShareG"), ShareBId = Shader.PropertyToID("_ShareB"), DeviceReachId = Shader.PropertyToID("_DeviceReach");
    private const int GunsInPass = 4, DepthCopyPass = 5, DepthBothPass = 6;
    private static RenderTexture? _goggleDepth, _bothDepth;
    private static Texture? _ownDepth;
    private static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture"), GunDepthId = Shader.PropertyToID("_GunDepth");   // NvgTube.shader: the goggle eye's gun laid into its scene, ahead of the bare eye's

    // The renders of a frame: the gun alone from the bare eye, then from the goggle's eye; then the frame's own, from the
    // goggle's eye, without the gun.
    private enum GunPass { None, Bare, Goggle }
    private static GunPass _gun = GunPass.None;
    private static bool _pass => _gun != GunPass.None;
    internal static bool GunOnly => _gun != GunPass.None;
    private static bool _hooked, _failed, _gunHidden;
    private static int _bareDone = -100, _goggleDone = -100;
    private static RenderTexture? _bareGun, _goggleGun;
    private static Material? _material;
    private static readonly int OtherId = Shader.PropertyToID("_Other"), OtherShareId = Shader.PropertyToID("_OtherShare");
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Renderer> LensRenderer =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Renderer>("_opticLensRenderer");
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Renderer> CollimatorRenderer =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Renderer>("_collimatorRenderer");

    // The goggle's eye across from the head's middle (m), from where its lens is; the bare eye the other side. Until the
    // lens is seen, the aiming eye's distance.
    private static float _goggleEye = 0.03f;
    private static float BareEye => -_goggleEye;

    internal static bool Now => On && !_failed && NightVisionEyepieces.DownOrFlipping && NightVisionEyepieces.MonocularWorn;

    // The camera across from the head's middle while on: at the goggle's eye (the bare eye's render placed from there).
    // As the goggle flips down the eyes go out to their places from where the camera would be, and back as it flips up
    // (the user's, 2026-11-15: no jump into the two views).
    private static float Apart => Mathf.SmoothStep(0f, 1f, NightVisionEyepieces.Flip);
    internal static float Place(float across) => Now ? Mathf.Lerp(across, _goggleEye, Apart) : across;

    // Where the camera is placed from its place this frame: over at the bare eye during the bare eye's render.
    internal static Vector3 OtherEyeOffset(Quaternion turn) => _gun == GunPass.Bare ? turn * new Vector3((BareEye - _goggleEye) * Apart, 0f, 0f) : Vector3.zero;

    // From NightVisionEyes.RenderLenses in the goggle eye's own render: its lens across from the head's middle.
    // The bare eye's gun drawn into the frame (the user's, 2026-11-15) rather than rendered by the camera again.
    internal static bool DrawnIntoFrame = true;
    // The bare eye's gun kept out of the eyepieces' picture, laid over it after (no night vision, no blur on it).
    internal static bool GunKeptApart => DrawnIntoFrame;
    private const int GunOutPass = 9, GunAlonePass = 10;   // NvgTube.shader
    private static RenderTexture? _gunApart, _preLens;
    private static int _preLensFrame = -1;
    private static readonly int PreLensId = Shader.PropertyToID("_PreLens");

    // The sight's lens of the bare eye's gun, drawn here from that eye with the game's own lens material (its picture is
    // the sight camera's, _CamTex), laid over at the gun's see-through: the game lays it in from the camera, which is at
    // the goggle's eye, and in solid. Its mask drawn too (pass 11), the lens laid only there (pass 12).
    private const int LensSolidPass = 11, LensLayPass = 12;   // NvgTube.shader
    private static RenderTexture? _lensLayer, _lensSolid;
    private static UnityEngine.Rendering.CommandBuffer? _lensBuffer;
    private static readonly int NonJitteredProjId = Shader.PropertyToID("NonJitteredProj"), CameraPlaceId = Shader.PropertyToID("_WorldSpaceCameraPos");

    private static bool DrawLensLayer(RenderTexture into, Material material)
    {
        Camera? camera = Camera.main;
        // The bare eye's gun's sight lens: kept from the game while that eye aims; while the goggle eye aims, the game's own
        // (it lays it for the goggle eye's gun), drawn here again from the bare eye, seen as that eye sees it from aside.
        Renderer? lensRenderer = _lensKept;
        Material? lensMaterial = _lensMaterialKept;
        if (BothGuns && !BareAims && camera != null && camera.GetComponent<SSAAPropagator>() is SSAAPropagator propagator)
        {
            lensRenderer = LensRenderer(propagator);
            lensMaterial = LensMaterial(propagator);
        }
        if (camera == null || lensRenderer == null || lensMaterial == null || !lensRenderer.enabled || !lensRenderer.gameObject.activeInHierarchy || !OpticAtWork())
            return false;
        Vector3 shift = camera.transform.rotation * new Vector3((BareEye - _goggleEye) * Apart, 0f, 0f);
        Vector3 eye = camera.transform.position + shift, place = camera.transform.position;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        RenderTexture layer = Kept(ref _lensLayer, into, "Justachae bare eye's sight lens");
        RenderTexture solid = Kept(ref _lensSolid, into.width, into.height);
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
    private const int GunAndLensPass = 14;   // NvgTube.shader
    private static readonly int LensUnderAimingId = Shader.PropertyToID("_LensUnderAiming"), GunWhich14Id = Shader.PropertyToID("_GunWhich14");
    private static readonly int LensLayerId = Shader.PropertyToID("_LensLayer"), GunShareId = Shader.PropertyToID("_GunShare"), LensShareId = Shader.PropertyToID("_LensShare");

    // For SightAtBareEye: where the frame from before the lens is kept, this frame.
    internal static RenderTexture PreLensFor(RenderTexture frame)
    {
        _preLensFrame = Time.frameCount;
        return Kept(ref _preLens, frame, "Justachae frame before the lens");
    }
    private static int _gunApartFrame = -1;
    private static readonly int DirectGunDepthId = Shader.PropertyToID("_DirectGD");

    // For NightVisionDirect.DrawDirect: the bare eye's gun to draw this frame, from where that eye is (along the world's
    // axes, from the camera), and how much of it is seen.
    internal static System.Collections.Generic.List<Renderer>? GunHere(Quaternion turn, out Vector3 shift, out float shown)
    {
        shift = Vector3.zero;
        shown = 0f;
        if (!DrawnIntoFrame || !Now || _gunsFrame != Time.frameCount || _shareBare <= 0.001f && !BothGuns || _gunRenderers.Count == 0)
            return null;
        shift = turn * new Vector3((BareEye - _goggleEye) * Apart, 0f, 0f);
        // Solid (the user's, 2026-11-16); where its sight's lens is, left out (NightVisionDirect.cs).
        shown = BothGuns ? 0.5f : 1f;
        return _gunRenderers;
    }

    // For SightAtBareEye: the sight's lens laid in from the bare eye this frame (it aims, its gun drawn into the frame):
    // where that eye is from the camera, along the world's axes.
    internal static bool SightFromBareEye(Quaternion turn, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (!DrawnIntoFrame || !Now || _gunsFrame != Time.frameCount || _shareBare <= 0.5f)
            return false;
        shift = turn * new Vector3((BareEye - _goggleEye) * Apart, 0f, 0f);
        return true;
    }

    // The bare eye aims through its sight: only one gun is drawn (that eye's), so behind its sight's lens, where the frame
    // is seen through it, no laser of the gun is seen at 1x (the one in the sight's picture is that gun's): the frame's
    // lasers drawn apart and added back but there (NightVisionIr.cs).
    internal static bool LasersCutAtLens
    {
        get
        {
            Camera? camera = Camera.main;
            // (Both guns seen: the frame's lasers are the goggle eye's gun's, seen; the bare eye's are laid over apart.)
            return !BothGuns && camera != null && SightFromBareEye(camera.transform.rotation, out _) && BareLens != null;
        }
    }

    // The aimed sight's lens, whichever eye aims: never drawn with the bare eye's gun by its own material (its picture is
    // the sight camera's, laid by the game for the goggle eye, by DrawLensLayer for the bare eye).
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

    // Lasers on the gun as the bare eye sees them (NightVisionIr.cs, drawn apart and laid over with its gun): moved by the
    // opposite of that eye's offset from the camera.
    internal static bool BareLaserShift(Camera camera, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (!BothGuns || !DrawnIntoFrame || !Now || _gunsFrame != Time.frameCount || _gunRenderers.Count == 0)
            return false;
        shift = camera.transform.rotation * new Vector3((BareEye - _goggleEye) * Apart, 0f, 0f);
        return true;
    }

    // The bare eye's gun's sight lens this frame (NightVisionDirect.cs leaves the gun out there).
    internal static Renderer? BareLens
    {
        get
        {
            try
            {
                // Only while the sight's camera is at work (aimed): otherwise the lens has no picture, and is the gun's.
                // (And only while the bare eye aims: otherwise the goggle eye's gun has the sight's picture.)
                if (!OpticAtWork() || _shareBare <= 0.5f)
                    return null;
                return _lensKept ?? EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight?.LensRenderer;
            }
            catch
            {
                return null;
            }
        }
    }

    // Under the gun in the hands, as gathered (its own model's hierarchy).
    internal static bool IsOnGun(Transform t) => _gunRoot != null && t.IsChildOf(_gunRoot);

    internal static void SeeLens(float across)
    {
        if (_pass || !Now || Mathf.Abs(across) < 0.01f)
            return;
        _goggleEye = across;
    }

    internal static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Application.onBeforeRender += BeforeRender;
        Camera.onPostRender += AfterOptic;
    }

    // A sight's camera at work this frame: the guns rendered once it has (its picture is what the game lays on the
    // sight's lens; rendered before it, the lens showed nothing but the reticle). Otherwise as rendering begins.
    private static int _gunsFrame = -1;

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

    private static void BeforeRender()
    {
        if (!OpticAtWork())
            RenderGuns();
    }

    private static void AfterOptic(Camera camera)
    {
        if (!_pass && OpticPicture.IsCamera(camera))
            RenderGuns();
    }

    // The gun alone from each eye (its layers only, no further than NearReach: anything else on those layers, other
    // players, is far off; the goggle not drawn, NightVisionEyes.cs), the sight's picture on its lens only for the aiming
    // eye's; then the gun hidden from the frame's own render.
    private static void RenderGuns()
    {
        if (!Now || _gunsFrame == Time.frameCount)
            return;
        _gunsFrame = Time.frameCount;
        Camera? main = Camera.main;
        if (main == null || !main.isActiveAndEnabled)
            return;
        int gunLayers = GunLayers();
        if (gunLayers == 0)
            return;
        // The sight's lens too (its picture is laid where it was drawn; on a layer of its own, or switched on only
        // while aimed, after the gun's layers were gathered, it was left out and the lens showed only the reticle).
        try
        {
            Renderer? lens = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight?.LensRenderer;
            if (lens != null)
                gunLayers |= 1 << lens.gameObject.layer;
        }
        catch
        {
        }
        // Over to the bare eye as the aiming eye goes over to it (CameraPosition: eased as the gun changes shoulders).
        float toward = CameraPosition.AimingEyeAcross / Mathf.Max(CameraPosition.AimEye, 1e-4f) * Mathf.Sign(BareEye);
        _shareBare = Mathf.Clamp01(0.5f + 0.5f * toward);
        _shareGoggle = 1f - _shareBare;
        // Aimed with the goggle's eye: its gun solid, as the frame's own render draws it (the user's, 2026-11-15).
        if (_shareBare <= 0.001f && !BothGuns)
            return;
        // The bare eye's gun drawn into the frame's own G-buffer from that eye (NightVisionDirect.cs, GunHere), the
        // goggle eye's kept out once the bare eye's is the more seen: no render of the camera of its own.
        if (DrawnIntoFrame)
        {
            if (BothGuns || _shareBare > 0.5f)
                HideGun(true);
            if (BothGuns)
                FillApart(main);
            return;
        }
        RenderGun(main, GunPass.Bare, gunLayers);
        if (_shareGoggle > 0.001f)
            RenderGun(main, GunPass.Goggle, gunLayers);
        if (_bareDone == Time.frameCount || _goggleDone == Time.frameCount)
            HideGun(true);
    }

    private static void RenderGun(Camera main, GunPass which, int gunLayers)
    {
        if (_failed)
            return;
        SSAAPropagator? propagator = main.GetComponent<SSAAPropagator>();
        // Only the aiming eye's gun has the sight's picture (on the way over, the one more seen).
        bool aims = which == GunPass.Bare ? _shareBare >= 0.5f : _shareGoggle > 0.5f;
        Renderer? lens = null, collimator = null;
        if (!aims && propagator != null)
        {
            lens = LensRenderer(propagator);
            collimator = CollimatorRenderer(propagator);
            LensRenderer(propagator) = null!;
            CollimatorRenderer(propagator) = null!;
        }
        // The camera's reach left as it is (the sight's lens works its picture out from the camera's projection: with
        // the reach cut to a couple of metres it found none); what is past NearReach is left out when laid in.
        int mask = main.cullingMask;
        main.cullingMask = gunLayers;
        // The game's blur about a sight looked through (its depth of field on the FPS camera) off: the eye sees its gun
        // as it is.
        Behaviour? dof = DepthOfFieldOf(main);
        bool dofOn = dof != null && dof.enabled;
        if (dofOn)
            dof!.enabled = false;
        // Nor any motion blur: from one eye's render to the other's the camera moves the eyes' distance in no time, and
        // the near gun was smeared by it.
        _blurOff.Clear();
        foreach (Behaviour b in MotionBlursOf(main))
        {
            if (b != null && b.enabled)
            {
                b.enabled = false;
                _blurOff.Add(b);
            }
        }
        // The post-processing's volumes left out of the gun render (its auto exposure and other history carried from
        // frame to frame took the near-empty frame in, and the picture pulsed).
        var post = main.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>();
        LayerMask volumes = post != null ? post.volumeLayer : (LayerMask)0;
        if (post != null)
            post.volumeLayer = 0;
        _gun = which;
        // The gun's own meshes into a mask as it renders (pass 7), so that only the gun is laid in.
        RenderTexture gunMask = Kept(ref _gunMask, Mathf.Max(main.pixelWidth, 8), Mathf.Max(main.pixelHeight, 8));
        _maskBuffer ??= new UnityEngine.Rendering.CommandBuffer { name = "Justachae gun mask" };
        _maskBuffer.Clear();
        _maskBuffer.SetRenderTarget(gunMask);
        _maskBuffer.ClearRenderTarget(false, true, Color.clear);
        Material? maskMaterial = _material;
        if (maskMaterial != null)
        {
            foreach (Renderer r in _gunRenderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy)
                    continue;
                int parts = r.sharedMaterials.Length;
                for (int s = 0; s < parts; s++)
                    _maskBuffer.DrawRenderer(r, maskMaterial, s, GunMaskPass);
            }
        }
        RenderTexture lensMask = Kept(ref _lensMask, gunMask.width, gunMask.height);
        _maskBuffer.SetRenderTarget(lensMask);
        _maskBuffer.ClearRenderTarget(false, true, Color.clear);
        Renderer? sightLens = null;
        try
        {
            sightLens = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight?.LensRenderer;
        }
        catch
        {
        }
        if (maskMaterial != null && sightLens != null && sightLens.enabled)
            _maskBuffer.DrawRenderer(sightLens, maskMaterial, 0, GunMaskPass);
        main.AddCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, _maskBuffer);
        _maskReady = maskMaterial != null;
        try
        {
            main.Render();
        }
        catch (System.Exception e)
        {
            _failed = true;
            NewCameraPlugin.Log?.LogError("Two renders: a gun's render failed, not tried again: " + e);
        }
        finally
        {
            main.RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent.AfterForwardAlpha, _maskBuffer);
            _gun = GunPass.None;
            if (post != null)
                post.volumeLayer = volumes;
            main.cullingMask = mask;
            if (dofOn)
                dof!.enabled = true;
            foreach (Behaviour b in _blurOff)
            {
                if (b != null)
                    b.enabled = true;
            }
            _blurOff.Clear();
            if (!aims && propagator != null)
            {
                LensRenderer(propagator) = lens!;
                CollimatorRenderer(propagator) = collimator!;
            }
        }
    }

    private static Camera? _dofOf;
    private static Behaviour? _dof;

    private static readonly System.Collections.Generic.List<Behaviour> _blurs = new System.Collections.Generic.List<Behaviour>(), _blurOff = new System.Collections.Generic.List<Behaviour>();
    private static Camera? _blursOf;

    // The FPS camera's image effects that carry something over from frame to frame (motion blur, a night-vision
    // picture's afterglow and noise, other mods' effects), found once for it: kept out of the gun renders, which would
    // otherwise be taken into that history (the lights pulsed, the eyepiece kept ghosts of what was seen before). Left
    // on: the upscaler (it lays the sight's lens in), this mod's eyepieces (the gun is taken there) and the game's
    // post-processing (the gun's look).
    private static System.Collections.Generic.List<Behaviour> MotionBlursOf(Camera camera)
    {
        if (_blursOf != camera)
        {
            _blursOf = camera;
            _blurs.Clear();
            var all = new System.Collections.Generic.List<string>();
            foreach (Behaviour b in camera.GetComponents<Behaviour>())
            {
                if (b == null)
                    continue;
                System.Type type = b.GetType();
                System.Reflection.MethodInfo? render = HarmonyLib.AccessTools.Method(type, "OnRenderImage");
                bool effect = render != null || type.Name.IndexOf("Motion", System.StringComparison.OrdinalIgnoreCase) >= 0;
                bool kept = b is SSAAPropagator || b is EyepieceImage || type.Name.StartsWith("SSAA", System.StringComparison.Ordinal) || type.Name == "PostProcessLayer" || type.Name == "DepthOfField";
                if (effect && !kept)
                {
                    // Passed over, not switched off: switched off and on each frame, some start over (BRNVG's gain was
                    // reset every frame, and the picture pulsed).
                    if (render != null)
                        PassOver(render);
                    else
                        _blurs.Add(b);
                }
                all.Add(type.Name + (effect ? (kept ? " (kept)" : render != null ? " (passed over)" : " (off)") : ""));
            }
            NewCameraPlugin.Log?.LogInfo("Two renders: the FPS camera's components, image effects off in the gun renders marked: " + string.Join(", ", all));
        }
        return _blurs;
    }

    private static readonly System.Collections.Generic.HashSet<System.Reflection.MethodInfo> _passedOver = new System.Collections.Generic.HashSet<System.Reflection.MethodInfo>();
    private static HarmonyLib.Harmony? _passHarmony;

    // An image effect's OnRenderImage left out of the gun renders: the frame handed on as it came.
    private static void PassOver(System.Reflection.MethodInfo render)
    {
        if (!_passedOver.Add(render) || render.GetParameters().Length != 2)
            return;
        try
        {
            _passHarmony ??= new HarmonyLib.Harmony(NewCameraPlugin.PluginGuid + ".gunRenderPassOver");
            _passHarmony.Patch(render, prefix: new HarmonyLib.HarmonyMethod(typeof(TwoRenders), nameof(PassOverPrefix)));
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning($"Two renders: {render.DeclaringType?.Name}.OnRenderImage not passed over: {e.Message}");
        }
    }

    private static bool PassOverPrefix(object[] __args)
    {
        if (!GunOnly)
            return true;
        if (__args.Length == 2 && __args[0] is RenderTexture source)
            Graphics.Blit(source, __args[1] as RenderTexture);
        return false;
    }

    private static Behaviour? DepthOfFieldOf(Camera camera)
    {
        if (_dofOf != camera)
        {
            _dofOf = camera;
            _dof = null;
            foreach (Behaviour b in camera.GetComponents<Behaviour>())
            {
                if (b != null && b.GetType().Name == "DepthOfField")
                {
                    _dof = b;
                    break;
                }
            }
        }
        return _dof;
    }

    // The gun kept from the frame's own render (its two eyes' pictures laid in instead); back once the frame is done.
    private static void HideGun(bool hide)
    {
        if (hide == _gunHidden)
            return;
        _gunHidden = hide;
        // Drawn into the frame, kept to its shadow as the device is (switched off whole, its shadow went too, and the
        // light about the player changed as the bare eye took the aim); otherwise not drawn at all.
        // (Both guns seen: both drawn into the frame, NightVisionDirect.cs; the frame's own kept to its shadow too.)
        if (DrawnIntoFrame)
        {
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
        }
        else
        {
            foreach (Renderer r in _gunRenderers)
            {
                if (r != null)
                    r.forceRenderingOff = hide;
            }
        }
        // The sight's lens too: the game lays it in at its upscaler whatever the camera draws, and with the gun kept out
        // it hung there by itself, a dark disc going with the camera.
        SSAAPropagator? propagator = Camera.main != null ? Camera.main.GetComponent<SSAAPropagator>() : null;
        // (Drawn into the frame, the bare eye's gun keeps its lens: laid in from that eye, SightAtBareEye.cs.)
        // (Both guns seen: only while the bare eye aims; otherwise the game lays it for the goggle eye's gun.)
        if (hide && propagator != null && (!BothGuns || _shareBare > 0.5f))
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
    private static SSAAPropagator? _lensOf;
    private static readonly System.Collections.Generic.List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)> _castings = new System.Collections.Generic.List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)>();
    private static Renderer? _lensKept, _collimatorKept;
    private static Material? _lensMaterialKept;
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Material> LensMaterial =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Material>("_opticLensMaterial");

    // From NewCameraPlugin.EndOfFrame.
    internal static void EndOfFrame()
    {
        HideGun(false);
        _apartBuffer?.Clear();
        NightVisionEyepieces.EmptyGunForward();
        Diagnose();
    }

    // DIAGNOSTICS (both guns, 2026-11-16; to come out once the cause is found): every two seconds while a monocular is
    // down, the shares, whether the steps ran, and what the map of which eye's gun each pixel was given holds, read back.
    private static int _diagNext, _diagBefore, _diagTake;
    private static void Diagnose()
    {
        if (!BothGuns || !Now || Time.frameCount < _diagNext)
            return;
        _diagNext = Time.frameCount + 120;
        string head = $"Both guns: shareBare {_shareBare:F2}, bare opacity {BareGunOpacity:F2}, goggle opacity {GoggleGunOpacity:F2}, transparency {BareGunTransparency:F2}, aiming eye across {CameraPosition.AimingEyeAcross:F3}, goggle eye {_goggleEye:F3}, apart {Apart:F2}, gunsFrame now {_gunsFrame == Time.frameCount}; last 120 frames: taken apart {_diagBefore}, laid over {_diagTake}";
        _diagBefore = _diagTake = 0;
        RenderTexture? which = NightVisionEyepieces.GunWhichTexture;
        RenderTexture? apart = BothGuns ? _bareView : _gunApart;
        if (which == null)
        {
            NewCameraPlugin.Log?.LogInfo(head + "; no map");
            return;
        }
        int w = which.width, h = which.height;
        UnityEngine.Rendering.AsyncGPUReadback.Request(which, 0, request =>
        {
            if (request.hasError)
            {
                NewCameraPlugin.Log?.LogInfo(head + "; map readback failed");
                return;
            }
            var data = request.GetData<byte>();
            int bare = 0, goggle = 0, bareEven = 0, goggleEven = 0, other = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = data[y * w + x];
                    bool even = ((x + y) & 1) == 0;
                    if (v > 191) { bare++; if (even) bareEven++; }
                    else if (v > 64) { goggle++; if (even) goggleEven++; }
                    else if (v > 0) other++;
                }
            }
            NewCameraPlugin.Log?.LogInfo($"{head}; map {w}x{h} ({which.format}): bare {bare} (even {bareEven}), goggle {goggle} (even {goggleEven}), other {other}");
        });
        if (apart != null)
        {
            int aw = apart.width, ah = apart.height;
            UnityEngine.Rendering.AsyncGPUReadback.Request(apart, 0, request =>
            {
                if (request.hasError)
                    return;
                var data = request.GetData<ushort>();
                int seen = 0, part = 0;
                for (int i = 3; i < data.Length; i += 4)
                {
                    float a = Mathf.HalfToFloat(data[i]);
                    if (a > 0.99f) seen++;
                    else if (a > 0.01f) part++;
                }
                NewCameraPlugin.Log?.LogInfo($"Both guns: the bare eye's gun taken apart {aw}x{ah} ({apart.format}): whole {seen}, part {part}");
            });
        }
    }

    private const int GunMaskPass = 7, LensSharpPass = 8;   // NvgTube.shader
    private static RenderTexture? _lensMask;
    private static readonly int SharpId = Shader.PropertyToID("_Sharp"), LensMaskId = Shader.PropertyToID("_LensMask");
    private static RenderTexture? _gunMask;
    private static UnityEngine.Rendering.CommandBuffer? _maskBuffer;
    private static bool _maskReady;
    private static readonly int GunMaskId = Shader.PropertyToID("_GunMask"), UseGunMaskId = Shader.PropertyToID("_UseGunMask");

    private static RenderTexture Kept(ref RenderTexture? kept, int width, int height)
    {
        if (kept == null || kept.width != width || kept.height != height)
        {
            if (kept != null)
            {
                kept.Release();
                Object.Destroy(kept);
            }
            kept = new RenderTexture(width, height, 0, RenderTextureFormat.R8) { name = "Justachae gun mask", hideFlags = HideFlags.DontSave };
            kept.Create();
        }
        return kept;
    }

    private static RenderTexture Kept(ref RenderTexture? kept, RenderTexture like, string name)
    {
        if (kept == null || kept.width != like.width || kept.height != like.height)
        {
            if (kept != null)
            {
                kept.Release();
                Object.Destroy(kept);
            }
            kept = new RenderTexture(like.width, like.height, 0, RenderTextureFormat.ARGBHalf) { name = name, hideFlags = HideFlags.DontSave };
            kept.Create();
        }
        return kept;
    }

    private static Material? MaterialFor(Shader? tubeShader) =>
        tubeShader == null ? null : _material ??= new Material(tubeShader) { name = "Justachae two eyes", hideFlags = HideFlags.DontSave };

    // At the eyepieces' image effect, before the eyepieces: a gun's render kept (its picture, and where it has something
    // in alpha); in the frame's own, the goggle eye's gun laid into its scene at half (so its eyepiece shows it as it
    // shows anything), weighted for the bare eye's laid over after.
    internal static void BeforeEyepieces(RenderTexture scene, Shader? tubeShader)
    {
        Material? material = MaterialFor(tubeShader);
        if (material == null)
            return;
        // The bare eye's gun drawn into the frame: taken out of it here (the eyepieces' picture is the goggle eye's,
        // which does not see it), and laid back over it once the eyepieces are done (TakeEye).
        _gunApartFrame = -1;
        if (GunKeptApart && !_pass && Now && (_shareBare > 0.001f || BothGuns) && _gunsFrame == Time.frameCount && Shader.GetGlobalTexture(DirectGunDepthId) != null)
        {
            if (BothGuns)
            {
                // The two eyes' pictures taken apart: the bare eye's gun whole (its pixels, and the others made from them),
                // kept to lay over after; the frame the goggle eye's alone (where the bare eye's gun was put in, made from the
                // pixels next to it), for the eyepieces.
                // Done before the upscale, at the camera's own pixels (FillApart): the frame is the goggle eye's by now.
                if (_apartFrame != Time.frameCount || _bareView == null)
                    return;
                _gunApartFrame = Time.frameCount;
                _diagBefore++;
                return;
            }
            // The sight's lens laid in this frame from the bare eye (SightAtBareEye.cs): the frame from before it, where
            // it differs the lens, the gun's too.
            // The frame as the bare eye's gun is in it, before the eyepieces: laid back over their picture where the gun
            // is (the goggle's eye does not see it, nor night vision touch it).
            Graphics.Blit(scene, Kept(ref _gunApart, scene, "Justachae frame with the bare eye's gun"));
            _gunApartFrame = Time.frameCount;
            return;
        }
        material.SetFloat(NearReachId, NearReach);
        if (_gun == GunPass.Bare)
        {
            material.SetTexture(GunMaskId, _gunMask != null ? _gunMask : Texture2D.blackTexture);
            material.SetFloat(UseGunMaskId, _maskReady ? 1f : 0f);
            // Blurred as the goggle's objective blurs what is near (the user's, 2026-11-15), its sight's lens kept sharp.
            RenderTexture? blurred = null;
            Component? camera = Camera.main;
            bool blur = camera != null && NightVisionEyepieces.BlurNear(camera, scene, ref blurred) && blurred != null;
            RenderTexture bare = Kept(ref _bareGun, scene, "Justachae bare eye's gun");
            Graphics.Blit(blur ? blurred! : scene, bare, material, NearPass);
            if (blur)
            {
                RenderTexture sharpened = RenderTexture.GetTemporary(bare.width, bare.height, 0, bare.format);
                material.SetTexture(SharpId, scene);
                material.SetTexture(LensMaskId, _lensMask != null ? _lensMask : Texture2D.blackTexture);
                Graphics.Blit(bare, sharpened, material, LensSharpPass);
                Graphics.Blit(sharpened, bare);
                RenderTexture.ReleaseTemporary(sharpened);
                RenderTexture.ReleaseTemporary(blurred);
            }
            _bareDone = Time.frameCount;
            return;
        }
        if (_gun == GunPass.Goggle)
        {
            material.SetTexture(GunMaskId, _gunMask != null ? _gunMask : Texture2D.blackTexture);
            material.SetFloat(UseGunMaskId, _maskReady ? 1f : 0f);
            Graphics.Blit(scene, Kept(ref _goggleGun, scene, "Justachae goggle eye's gun"), material, NearPass);
            // Its depth too, for the objective's blur in the frame's own render (which has no gun in its depth).
            if (Shader.GetGlobalTexture(CameraDepthId) is Texture depth)
                Graphics.Blit(null, KeptDepth(ref _goggleDepth, depth.width, depth.height, "Justachae goggle eye's gun depth"), material, DepthCopyPass);
            _goggleDone = Time.frameCount;
            return;
        }
        if (!Now || !_gunHidden)
            return;
        bool goggleGun = _goggleDone == Time.frameCount && _goggleGun != null;
        bool bareGun = _bareDone == Time.frameCount && _bareGun != null;
        RenderTexture laid = RenderTexture.GetTemporary(scene.width, scene.height, 0, scene.format);
        material.SetTexture(GunGId, goggleGun ? _goggleGun : Texture2D.blackTexture);
        material.SetFloat(ShareGId, goggleGun ? _shareGoggle : 0f);   // the goggle eye's gun solid
        material.SetFloat(ShareBId, bareGun ? GunOpacity * _shareBare : 0f);
        material.SetFloat(DeviceReachId, DeviceReach);
        material.SetTexture(GunBId, bareGun ? _bareGun : Texture2D.blackTexture);
        Graphics.Blit(scene, laid, material, GunsInPass);
        Graphics.Blit(laid, scene);
        RenderTexture.ReleaseTemporary(laid);
        // The depth the eyepieces' steps go by (the objective's blur above all): the gun's where it is nearer, until
        // the eyepieces are done (TakeEye gives the camera's own back).
        if (goggleGun && _goggleDepth != null && Shader.GetGlobalTexture(CameraDepthId) is Texture own && own.width == _goggleDepth.width && own.height == _goggleDepth.height)
        {
            material.SetTexture(GunDepthId, _goggleDepth);
            Graphics.Blit(null, KeptDepth(ref _bothDepth, own.width, own.height, "Justachae depth with the gun"), material, DepthBothPass);
            _ownDepth = own;
            Shader.SetGlobalTexture(CameraDepthId, _bothDepth);
        }
    }

    private static RenderTexture KeptDepth(ref RenderTexture? kept, int width, int height, string name)
    {
        if (kept == null || kept.width != width || kept.height != height)
        {
            if (kept != null)
            {
                kept.Release();
                Object.Destroy(kept);
            }
            kept = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat) { name = name, hideFlags = HideFlags.DontSave, filterMode = FilterMode.Point };
            kept.Create();
        }
        return kept;
    }

    // In the eyepieces' image effect, the frame done: the bare eye's gun laid over at half where it is.
    internal static void TakeEye(RenderTexture into, Shader? tubeShader)
    {
        if (_ownDepth != null)
        {
            Shader.SetGlobalTexture(CameraDepthId, _ownDepth);
            _ownDepth = null;
        }
        if (_gunApartFrame == Time.frameCount && (BothGuns ? _bareView : _gunApart) != null && MaterialFor(tubeShader) is Material laying)
        {
            _gunApartFrame = -1;
            _diagTake++;
            // The gun solid (the user's, 2026-11-16); only its sight's lens seen through, over what lies behind it in
            // the frame: the eyepieces' picture where they are.
            bool lens = DrawLensLayer(into, laying);
            RenderTexture frame = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
            Graphics.Blit(into, frame);
            laying.SetTexture(OtherId, BothGuns ? _bareView : _gunApart);
            laying.SetTexture(LensLayerId, lens ? _lensLayer : Texture2D.blackTexture);
            laying.SetTexture(LensMaskId, lens ? _lensSolid : Texture2D.blackTexture);
            laying.SetFloat(GunShareId, BothGuns ? BareGunOpacity : _shareBare);
            laying.SetFloat(LensShareId, BothGuns && !BareAims ? BareGunOpacity : LensOpacity * _shareBare);
            laying.SetFloat(LensUnderAimingId, BothGuns && !BareAims ? 1f : 0f);
            laying.SetTexture(GunWhich14Id, NightVisionEyepieces.GunWhichTexture ?? (Texture)Texture2D.blackTexture);
            laying.SetTexture(BareLasersId, NightVisionEyepieces.BareLasers ?? (Texture)Texture2D.blackTexture);
            Graphics.Blit(frame, into, laying, GunAndLensPass);
            RenderTexture.ReleaseTemporary(frame);
            return;
        }
        Material? material = MaterialFor(tubeShader);
        if (material == null || _pass || !Now || !_gunHidden || _bareGun == null || _bareDone != Time.frameCount)
            return;
        RenderTexture goggle = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, goggle);
        material.SetTexture(OtherId, _bareGun);
        material.SetFloat(OtherShareId, GunOpacity * _shareBare);
        material.SetFloat(DeviceReachId, DeviceReach);
        Graphics.Blit(goggle, into, material, HalvesPass);
        if (Probes)
        {
            bool goggleLeft = _goggleEye < 0f;
            DrawProbes(into, goggleLeft ? goggle : _bareGun, goggleLeft ? _bareGun : goggle, material);
        }
        RenderTexture.ReleaseTemporary(goggle);
    }

    // Each eye's frame a quarter of the frame across, opaque in a thin white frame: the left eye's in the lower left
    // corner, the right's in the lower right.
    private static void DrawProbes(RenderTexture into, RenderTexture left, RenderTexture right, Material material)
    {
        int width = into.width / 4, height = into.height / 4, margin = into.height / 40;
        RenderTexture? active = RenderTexture.active;
        RenderTexture.active = into;
        GL.PushMatrix();
        // Top-left origin, as Graphics.DrawTexture's rects go.
        GL.LoadPixelMatrix(0, into.width, into.height, 0);
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? margin : into.width - width - margin, y = into.height - margin - height;
            Graphics.DrawTexture(new Rect(x - 2, y - 2, width + 4, 2), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x - 2, y + height, width + 4, 2), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x - 2, y, 2, height), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x + width, y, 2, height), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x, y, width, height), side == 0 ? left : right, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, Color.white, material, OpaquePass);
        }
        GL.PopMatrix();
        RenderTexture.active = active;
    }
}
