using UnityEngine;

namespace EFTNewCamera;

// A goggle before one eye only, seen with both eyes: a monocular (the PVS-14), or the ENVG-B with one eyepiece swung away
// on its pod (NightVisionEyepieces.PodMonocular). As the user set it out, 2026-11-15 to 18.
//
// The camera: where it is for any goggle while not aiming (it does not go over to the goggle's eye as the goggle comes
// down). Aiming, it goes over to an eye, as with any goggle: with the goggle's eye aiming, to that eye; with the bare eye
// aiming, to the eye that leads (BareLeads): the goggle's (the user's, 2026-11-19: left between the two eyes it was
// neither's) or the bare one (CameraPosition, AimAcross).
//
// Through a sight's window on the bare eye's gun the frame from before the eyepieces (no night vision) is laid over
// their picture by the lens's share: the two eyes' views one over the other, and apart, each moved over to its own eye's
// place by the depth of what is seen there (TakeEye; only in the window).
internal static class TwoEyes
{
    // Also the ENVG-B with one eyepiece swung away (NightVisionEyepieces.PodMonocular), but the camera left where it is (it
    // goes over to the aiming eye only to aim; the one left brought to the middle as a binocular's are,
    // NightVisionEyes.RenderLenses; the user's, 2026-11-17): the bare eye aiming is then the camera, its gun the camera's.
    internal static bool Now => NightVisionEyepieces.DownOrFlipping
        && (NightVisionEyepieces.MonocularWorn || NightVisionEyepieces.PodMonocular(out _));
    // The camera does not go over to the goggle's eye as it flips down (the user's, 2026-11-18: the PVS-14 as the others,
    // NightVisionEyes.RenderLenses), and does not go over to the bare eye when that eye aims (the user's): it stays where
    // it is, its own gun as it sees it, and the bare eye's gun, drawn from that eye, fades in over it. Aiming with the
    // goggle's eye it goes over to it, as with any goggle.
    // How much the aiming eye is the bare one, 0 to 1 (it goes over as the gun changes shoulders, and with the flip).
    internal static float BareAimShare => Now
        ? Mathf.Clamp01(0.5f + 0.5f * CameraPosition.AimSide * Mathf.Sign(BareEye)) * Apart
            * NightVisionEyepieces.OneEyeShare
        : 0f;

    // F12 单筒：裸眼瞄准时分划的透明度: the bare eye's sight lens seen through (0 solid).
    internal static float BareLensSeeThrough = 0.5f;
    private static float LensOpacity => 1f - Mathf.Clamp01(BareLensSeeThrough);
    // Which eye aims (_shareBare: 1 the bare eye; it goes over as the gun changes shoulders, CameraPosition), and how far
    // the aim has come up (_aim, 0 to 1): the bare eye's gun only while it aims.
    private static float _shareBare, _aim;
    internal static bool BareAims => _shareBare > 0.5f && _aim > 0.001f;
    // The goggle eye's gun drawn too, always (the bare eye leading, by half, from that eye: the user's, 2026-11-19).
    internal static bool GoggleGunToo => true;
    // (Whole as the bare eye comes to lead: the camera is then at that eye, and this gun its own.)
    private static float BareGunOpacity => Mathf.Lerp(_aim * BareSeen, 1f, _lead);
    private static float GoggleGunOpacity => Mathf.Lerp(1f, BareSeen, _lead);

    // Which eye leads while the bare eye aims: false the goggle's (the camera stays), true the bare eye (the camera goes
    // over to it). _lead follows it over a quarter of a second.
    internal static bool BareLeads;
    private static float _lead;
    internal static float Lead => _lead;
    internal static BepInEx.Configuration.KeyboardShortcut LeadKey = new BepInEx.Configuration.KeyboardShortcut(KeyCode.Slash);

    // From NewCameraPlugin.Update: the key, only while the bare eye aims; and the easing.
    internal static void Update()
    {
        if (LeadKey.IsDown() && Now && BareAims)
        {
            BareLeads = !BareLeads;
            NewCameraPlugin.Log?.LogInfo($"Two eyes: the {(BareLeads ? "bare eye" : "goggle's eye")} leads from now on.");
        }
        _lead = Mathf.MoveTowards(_lead, BareLeads ? 1f : 0f, Time.deltaTime * 4f);
    }

    // Where the camera goes to aim (CameraPosition), across from the head's middle: the aiming eye, or, the bare eye
    // aiming and the goggle's eye leading, the goggle's eye.
    internal static float AimAcross(float aimingEye) => Mathf.Lerp(aimingEye, Mathf.Lerp(_goggleEye, aimingEye, _lead), BareAimShare);
    // How much of what the bare eye sees (its gun, its lasers, the device from its side) is seen over the goggle eye's:
    // half (the user's, 2026-11-18).
    internal const float BareSeen = 0.5f;
    // The bare eye's gun drawn this frame (it aims).
    private static bool BareGunNow => Now && BareAims && _gunsFrame == Time.frameCount && _gunRenderers.Count > 0;

    // The goggle's eye across from the head's middle (m), from where its lens is; the bare eye the other side.
    private static float _goggleEye = 0.03f;
    private static float BareEye => -_goggleEye;
    // As the goggle flips down the eyes go out to their places from where the camera would be, and back as it flips up.
    private static float Apart => Mathf.SmoothStep(0f, 1f, NightVisionEyepieces.Flip);
    // From the camera to the bare eye while it aims: the aiming eye's place (the gun is placed for it) from the camera's.
    // From the camera to the goggle's eye, for its gun and the lasers on it: where its lens stands from the head's middle
    // (SeeLens). Eased in with the aim (not aiming, the gun is the game's, from the camera). None while the goggle's eye
    // leads (the camera is then that eye); the bare eye leading, the whole way across.
    private static Vector3 GoggleShift(Quaternion turn) => turn * new Vector3((_goggleEye - CameraPosition.Horizontal) * _aim, 0f, 0f);
    // (None of it once the bare eye leads: the camera is then that eye.)
    private static Vector3 BareShift(Quaternion turn) => turn * new Vector3((CameraPosition.AimingEyeAcross - CameraPosition.Horizontal) * (1f - _lead), 0f, 0f);

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

    // For NightVisionDirect.DrawGun: the bare eye's gun to draw this frame (it aims), that eye's offset from the camera
    // along the world's axes, and that it is put in on the chessboard (shown below 1).
    internal static System.Collections.Generic.List<Renderer>? GunHere(Quaternion turn, out Vector3 shift, out Vector3 goggleShift, out float shown)
    {
        shift = goggleShift = Vector3.zero;
        shown = 0f;
        if (!BareGunNow)
            return null;
        shift = BareShift(turn);
        goggleShift = GoggleShift(turn);
        shown = 0.5f;
        return _gunRenderers;
    }

    // While the bare eye's gun is drawn: the lasers on the gun as that eye sees them too (NightVisionIr.cs: drawn apart,
    // moved by the opposite of its offset, and laid over with its gun, by its share; the camera's gun keeps its own).
    // The lasers on the gun as the goggle's eye sees them: for the main camera their emitters are moved by the opposite of
    // that eye's offset (LaserReplay), the whole beam and the point it lands on alike, so they go from the gun drawn from
    // that eye.
    internal static bool GoggleLaserShift(Camera camera, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (camera == null || !BareGunNow || !GoggleGunToo)
            return false;
        shift = GoggleShift(camera.transform.rotation);
        return shift.sqrMagnitude > 1e-10f;
    }

    internal static bool BareLaserShift(Camera camera, out Vector3 shift)
    {
        shift = Vector3.zero;
        if (camera == null || !BareGunNow)
            return false;
        // (None where that eye is the camera. From the lasers as the main camera has them drawn: moved to the goggle
        // eye's gun, GoggleLaserShift.)
        shift = BareShift(camera.transform.rotation) - GoggleShift(camera.transform.rotation);
        return shift.sqrMagnitude > 1e-10f;
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

    // The bare eye aiming through its sight: behind its lens the goggle eye's picture shows what lies beyond, not the gun,
    // nor the lasers on it (NightVisionIr.cs: drawn apart and added but there; the user's, 2026-11-18).
    internal static bool LasersCutAtLens => BareGunNow && BareLens != null;

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
        try
        {
            var fill = HarmonyLib.AccessTools.Method(typeof(SSAAPropagator), nameof(SSAAPropagator.FillCommandBuffer));
            new HarmonyLib.Harmony(NewCameraPlugin.PluginGuid + ".bareLens").Patch(fill,
                prefix: new HarmonyLib.HarmonyMethod(typeof(TwoEyes), nameof(LensOutPrefix)),
                finalizer: new HarmonyLib.HarmonyMethod(typeof(TwoEyes), nameof(LensOutFinalizer)));
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogError($"Two eyes: the game's lens could not be kept out while the bare eye aims: {e}");
        }
    }

    // While the bare eye aims the game lays no sight lens in (it would, from the camera: DrawLensLayer lays it from the bare
    // eye): its lens and collimator taken from it only while it fills its command buffer, and given back at once. (Taken
    // for the whole frame and given back at its end, the game cleared them in between as an aim ended, with their materials;
    // given back then, the lens stood without a material and FillCommandBuffer threw every frame from there on: nothing it
    // or what follows it draws was drawn, the lasers added after the upscale among them; found in Player.log, 2026-11-18.)
    private static void LensOutPrefix(SSAAPropagator __instance, out (Renderer? lens, Renderer? collimator, bool taken) __state)
    {
        __state = (null, null, false);
        if (!_gunHidden || !BareAims || _gunsFrame != Time.frameCount || !__instance.CompareTag("MainCamera"))
            return;
        __state = (LensRenderer(__instance), CollimatorRenderer(__instance), true);
        LensRenderer(__instance) = null!;
        CollimatorRenderer(__instance) = null!;
    }

    private static void LensOutFinalizer(SSAAPropagator __instance, (Renderer? lens, Renderer? collimator, bool taken) __state)
    {
        if (!__state.taken)
            return;
        LensRenderer(__instance) = __state.lens!;
        CollimatorRenderer(__instance) = __state.collimator!;
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
        float toward = CameraPosition.AimSide * Mathf.Sign(BareEye);
        _shareBare = Mathf.Clamp01(0.5f + 0.5f * toward);
        // (By how much the goggle is before one eye only: an eyepiece of the ENVG-B swung up or down while the bare eye
        // aims, all that is laid in for that eye comes and goes with the swing; at once, the whole device flashed over,
        // the user's, 2026-11-19.)
        _aim = CameraPosition.ViewAimed * NightVisionEyepieces.OneEyeShare;
        // The goggle eye aims: the game's own gun.
        if (!BareAims)
        {
            HideGun(false);
            return;
        }
        HideGun(true);
        FillApart(main);
    }

    // From NewCameraPlugin.EndOfFrame.
    internal static void EndOfFrame()
    {
        HideGun(false);
        _apartBuffer?.Clear();
        if (_frameDepth != null && _goggleDepth != null && Shader.GetGlobalTexture(CameraDepthId) == _goggleDepth)
            Shader.SetGlobalTexture(CameraDepthId, _frameDepth);
        NightVisionEyepieces.EmptyGunForward();
    }

    // ---------------------------------------------------------------- the game's copy of the gun, and the sight's lens

    private static bool _gunHidden;
    private static readonly System.Collections.Generic.List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)> _castings = new System.Collections.Generic.List<(Renderer, UnityEngine.Rendering.ShadowCastingMode)>();
    private static Renderer? _lensKept, _collimatorKept;
    private static Material? _lensMaterialKept, _collimatorMaterialKept;
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Material> CollimatorMaterial =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Material>("_collimatorMaterial");
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Renderer> LensRenderer =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Renderer>("_opticLensRenderer");
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Renderer> CollimatorRenderer =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Renderer>("_collimatorRenderer");
    private static readonly HarmonyLib.AccessTools.FieldRef<SSAAPropagator, Material> LensMaterial =
        HarmonyLib.AccessTools.FieldRefAccess<SSAAPropagator, Material>("_opticLensMaterial");

    // While the bare eye aims (its gun drawn into the frame instead): the game's copy kept to its shadow (switched off
    // whole, its shadow went too), and the game's lens kept from it (it lays it in from the goggle's eye: DrawLensLayer lays
    // it from the bare eye instead). Back once the frame is done.
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
        // The game's lens and its material, for DrawLensLayer (the game's own fields are left as they are: LensOutPrefix).
        if (hide && propagator != null && BareAims)
        {
            _lensKept = LensRenderer(propagator);
            _lensMaterialKept = LensMaterial(propagator);
            _collimatorKept = CollimatorRenderer(propagator);
            _collimatorMaterialKept = CollimatorMaterial(propagator);
        }
        else if (!hide)
        {
            _lensKept = _collimatorKept = null;
            _lensMaterialKept = _collimatorMaterialKept = null;
        }
    }

    // ---------------------------------------------------------------- the bare eye's gun apart (NvgTube.shader)

    private const int LensSolidPass = 1, GunAndLensPass = 2, BareViewPass = 3, GoggleViewPass = 4, GoggleFillPass = 5,
        DepthWeighPass = 7, DepthPullPass = 8, BareGatherPass = 9;
    private static Material? _material;
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

    // For the gun's forward parts (NightVisionDirect.DrawGunForward): the bare eye's gun's picture this frame.
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
    // things): the bare eye's gun taken out (pass 3) and gathered over frames (pass 9); the frame left as the goggle eye
    // sees it, what lies under that gun filled from around (passes 4, 5: the objective's blur spreads none of the gun into
    // the goggle's picture); its depth alike (passes 7, 8). Filled each frame it is wanted, emptied at its end.
    private static void FillApart(Camera main)
    {
        Material? material = _material;
        if (material == null)
            return;
        _apartBuffer ??= new UnityEngine.Rendering.CommandBuffer { name = "Justachae bare eye's gun apart" };
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
        // From here on the frame is the goggle eye's, and so is the depth all that follows reads (the image effects, the
        // upscaler: with the bare eye's gun's depth, near, on every other pixel under that gun, what smooths the frame over
        // frames by its depth mottled the picture seen through that gun as the camera moved). The camera's own given back
        // at the frame's end.
        if (Shader.GetGlobalTexture(CameraDepthId) is Texture frameDepth && frameDepth != depth)
            _frameDepth = frameDepth;
        buffer.SetGlobalTexture(CameraDepthId, depth);
        _apartFrame = Time.frameCount;
    }
    private static Texture? _frameDepth;

    // At the eyepieces' image effect, before the eyepieces: while the bare eye's gun is drawn, their steps by the goggle
    // eye's depth (the objective's blur above all), until TakeEye.
    internal static void BeforeEyepieces(RenderTexture scene, Shader? tubeShader)
    {
        _layFrame = -1;
        if (MaterialFor(tubeShader) == null || !Now || _gunsFrame != Time.frameCount || !BareAims || _apartFrame != Time.frameCount || _bareNow == null)
            return;
        if (BareAims && _goggleDepth != null && Shader.GetGlobalTexture(CameraDepthId) is Texture frameDepth && frameDepth != _goggleDepth)
        {
            _ownDepth = frameDepth;
            Shader.SetGlobalTexture(CameraDepthId, _goggleDepth);
        }
        // The frame as it is before the eyepieces (no night vision), for what the bare eye sees through a sight's window.
        _sceneFrame = -1;
        if (NightVisionEyepieces.GunHasGlass)
        {
            Graphics.Blit(scene, Kept(ref _sceneBare, scene.width, scene.height, scene.format, "Justachae frame before the eyepieces"));
            _sceneFrame = Time.frameCount;
        }
        _layFrame = Time.frameCount;
    }
    private static RenderTexture? _sceneBare, _windowMask;
    private static int _sceneFrame = -1;
    private static readonly int WindowMaskId = Shader.PropertyToID("_WindowMask"), SceneBareId = Shader.PropertyToID("_SceneBare"),
        WindowWorldId = Shader.PropertyToID("_WindowWorld"), WindowDepthId = Shader.PropertyToID("_WindowDepth");

    // Where the bare eye's gun's sights' windows are (their glass, from that eye), for TakeEye.
    private static bool DrawWindowMask(RenderTexture into, Material material)
    {
        Camera? camera = Camera.main;
        if (camera == null || _sceneFrame != Time.frameCount || _sceneBare == null)
            return false;
        RenderTexture mask = Kept(ref _windowMask, into.width, into.height, RenderTextureFormat.R8, "Justachae bare eye's sight windows");
        Matrix4x4 turned = camera.worldToCameraMatrix;
        turned.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        var buffer = _lensBuffer ??= new UnityEngine.Rendering.CommandBuffer { name = "Justachae bare eye's sight lens" };
        buffer.Clear();
        buffer.SetRenderTarget(mask);
        buffer.ClearRenderTarget(false, true, Color.clear);
        // (The gun's parts are kept from the camera, turned with the world: NightVisionDirect.DrawGun.)
        buffer.SetViewProjectionMatrices(turned, camera.nonJitteredProjectionMatrix);
        bool any = NightVisionEyepieces.DrawGunGlass(buffer, material, LensSolidPass);
        buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        Graphics.ExecuteCommandBuffer(buffer);
        return any;
    }

    // ---------------------------------------------------------------- laying the bare eye's gun over (NvgTube.shader, pass 2)

    private static RenderTexture? _lensLayer, _lensSolid;
    private static readonly int AimLensMaskId = Shader.PropertyToID("_AimLensMask");
    private static UnityEngine.Rendering.CommandBuffer? _lensBuffer;
    private static readonly int OtherId = Shader.PropertyToID("_Other"), LensLayerId = Shader.PropertyToID("_LensLayer"),
        LensMaskId = Shader.PropertyToID("_LensMask"), GunShareId = Shader.PropertyToID("_GunShare"), LensShareId = Shader.PropertyToID("_LensShare"),
        LensUnderAimingId = Shader.PropertyToID("_LensUnderAiming"), GunWhich14Id = Shader.PropertyToID("_GunWhich14"),
        BareLasersId = Shader.PropertyToID("_BareLasers"), NonJitteredProjId = Shader.PropertyToID("NonJitteredProj"),
        CameraPlaceId = Shader.PropertyToID("_WorldSpaceCameraPos");

    // In the eyepieces' image effect, the frame done, while the bare eye aims: its gun laid over (solid: no night vision, no
    // blur on it), and its sight's lens (by its F12 see-through) over the goggle eye's picture of what lies behind.
    internal static void TakeEye(RenderTexture into, Shader? tubeShader)
    {
        if (_ownDepth != null)
        {
            Shader.SetGlobalTexture(CameraDepthId, _ownDepth);
            _ownDepth = null;
        }
        if (_layFrame != Time.frameCount || MaterialFor(tubeShader) is not Material laying)
            return;
        _layFrame = -1;
        if (!BareAims || _bareNow == null)
            return;
        bool lens = DrawLensLayer(into, laying);
        RenderTexture frame = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, frame);
        laying.SetTexture(OtherId, _bareNow);
        laying.SetFloat(GunShareId, BareGunOpacity);
        laying.SetTexture(LensLayerId, lens ? _lensLayer : Texture2D.blackTexture);
        laying.SetTexture(LensMaskId, lens ? _lensSolid : Texture2D.blackTexture);
        // (Laid over by as much as the aim has come up, with its gun.)
        laying.SetFloat(LensShareId, LensOpacity * _aim);
        laying.SetFloat(LensUnderAimingId, 0f);
        laying.SetTexture(AimLensMaskId, Texture2D.blackTexture);
        laying.SetTexture(GunWhich14Id, NightVisionEyepieces.GunWhichTexture ?? (Texture)Texture2D.blackTexture);
        laying.SetTexture(BareLasersId, NightVisionEyepieces.BareLasers ?? (Texture)Texture2D.blackTexture);
        bool windows = DrawWindowMask(into, laying);
        // In a window the two eyes' views stand apart, as two eyes' do (the user's, 2026-11-19): each layer the frame moved
        // over to its own eye's place by the depth at the pixel (NvgTube pass 2, windowWorld); the eye the camera is, not at
        // all. The goggle eye's layer was moved so while its whole picture was (1.8.123 to 1.8.125, the bare eye leading),
        // and went with it when that was taken out for what it did to the gun elsewhere in the eyepiece.
        Camera? main = Camera.main;
        if (windows && main != null && _goggleDepth != null)
        {
            float scale = 0.5f * main.projectionMatrix.m00, near = main.nearClipPlane, far = main.farClipPlane;
            float bareAcross = (CameraPosition.AimingEyeAcross - CameraPosition.Horizontal) * _aim;
            float goggleAcross = (_goggleEye - CameraPosition.Horizontal) * _aim;
            // (A raw depth to metres, as Unity has it: 1 / (y * raw + z).)
            Vector2 metres = SystemInfo.usesReversedZBuffer
                ? new Vector2((far / near - 1f) / far, 1f / far)
                : new Vector2((1f - far / near) / far, far / near / far);
            laying.SetVector(WindowWorldId, new Vector4(scale * bareAcross, metres.x, metres.y, scale * goggleAcross));
            laying.SetTexture(WindowDepthId, _goggleDepth);
        }
        else
            laying.SetVector(WindowWorldId, new Vector4(0f, 0f, 1f, 0f));
        laying.SetTexture(WindowMaskId, windows ? _windowMask : Texture2D.blackTexture);
        laying.SetTexture(SceneBareId, windows ? _sceneBare : Texture2D.blackTexture);
        Graphics.Blit(frame, into, laying, GunAndLensPass);
        RenderTexture.ReleaseTemporary(frame);
        DrawCollimator(into);
    }

    // A collimator's (a holographic or red-dot sight's) mark, which the game lays in after the upscale from the camera, by
    // the sight's own material: kept from the game while the bare eye aims (LensOutPrefix) and laid here from that eye,
    // over its gun, as the game lays it (only with no sight lens: the game draws the one or the other). Without it the
    // sight's window had no mark at all.
    private static void DrawCollimator(RenderTexture into)
    {
        Camera? camera = Camera.main;
        Renderer? mark = _collimatorKept;
        Material? material = _collimatorMaterialKept;
        if (camera == null || mark == null || material == null || _lensKept != null || !mark.gameObject.activeInHierarchy)
            return;
        Vector3 shift = BareShift(camera.transform.rotation);
        Vector3 eye = camera.transform.position + shift, place = camera.transform.position;
        Matrix4x4 projection = camera.nonJitteredProjectionMatrix;
        var buffer = _lensBuffer ??= new UnityEngine.Rendering.CommandBuffer { name = "Justachae bare eye's sight lens" };
        buffer.Clear();
        buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix * Matrix4x4.Translate(-shift), projection);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(eye.x, eye.y, eye.z, 1f));
        buffer.EnableShaderKeyword("TAA");
        buffer.EnableShaderKeyword("NON_JITTERED");
        buffer.SetGlobalMatrix(NonJitteredProjId, GL.GetGPUProjectionMatrix(projection, true));
        buffer.SetRenderTarget(into);
        buffer.DrawRenderer(mark, material);
        buffer.DisableShaderKeyword("NON_JITTERED");
        buffer.DisableShaderKeyword("TAA");
        buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        buffer.SetGlobalVector(CameraPlaceId, new Vector4(place.x, place.y, place.z, 1f));
        Graphics.ExecuteCommandBuffer(buffer);
    }

    // The bare eye's gun's sight lens, drawn from that eye with the game's own lens material (its picture is the sight
    // camera's, _CamTex), the game's kept from it (HideGun). Its mask too (pass 1).
    private static bool DrawLensLayer(RenderTexture into, Material material)
    {
        Camera? camera = Camera.main;
        Renderer? lensRenderer = _lensKept;
        Material? lensMaterial = _lensMaterialKept;
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
