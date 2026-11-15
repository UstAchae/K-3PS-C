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
    internal static bool Probes = true;
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
        object? hands = Comfort.Common.Singleton<EFT.GameWorld>.Instance?.MainPlayer?.HandsController;
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
        if (root != _gunRoot || _gunRenderers.Exists(r => r == null))
        {
            HideGun(false);
            _gunRoot = root;
            _gunLayers = 0;
            root.GetComponentsInChildren(false, _gunRenderers);
            _gunRenderers.RemoveAll(r => r is not MeshRenderer && r is not SkinnedMeshRenderer);
            foreach (Renderer r in _gunRenderers)
            {
                if (r is MeshRenderer || r is SkinnedMeshRenderer)
                    _gunLayers |= 1 << r.gameObject.layer;
            }
        }
        return _gunLayers;
    }
    private static readonly int NearReachId = Shader.PropertyToID("_NearReach");
    private static readonly int GunGId = Shader.PropertyToID("_GunG"), GunBId = Shader.PropertyToID("_GunB");
    // Only the aiming eye's gun is seen (the user's, 2026-11-15); as the gun goes over to the other shoulder, the one
    // fades out as the other fades in: how much of each this frame (they add up to one).
    private static float _shareGoggle = 1f, _shareBare;
    // How far the bare eye's gun is seen through (F12 单筒：裸眼瞄准时枪的透明度): 0 solid, 1 not seen; at half, as the two
    // eyes' frames laid together did.
    // How far each eye's gun is seen through (F12): 0 solid, 1 not seen. The goggle eye's laid in first, the bare eye's
    // over it.
    internal static float BareGunSeeThrough = 0.3f, GoggleGunSeeThrough = 0.7f;
    private static float BareOpacity => 1f - Mathf.Clamp01(BareGunSeeThrough);
    private static float GoggleOpacity => 1f - Mathf.Clamp01(GoggleGunSeeThrough);
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
        // Both eyes' guns, each seen through as set (F12); which aims decides only which has the sight's picture.
        RenderGun(main, GunPass.Bare, gunLayers);
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
        _gun = which;
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
            _gun = GunPass.None;
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

    // The FPS camera's motion blurs (AmplifyMotion's, the game's own), found once for it.
    private static System.Collections.Generic.List<Behaviour> MotionBlursOf(Camera camera)
    {
        if (_blursOf != camera)
        {
            _blursOf = camera;
            _blurs.Clear();
            foreach (Behaviour b in camera.GetComponents<Behaviour>())
            {
                string name = b != null ? b.GetType().Name : "";
                if (name.IndexOf("Motion", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    _blurs.Add(b!);
            }
            NewCameraPlugin.Log?.LogInfo("Two renders: motion blurs off in the gun renders: " + (_blurs.Count > 0 ? string.Join(", ", _blurs.ConvertAll(x => x.GetType().Name)) : "none found"));
        }
        return _blurs;
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
        foreach (Renderer r in _gunRenderers)
        {
            if (r != null)
                r.forceRenderingOff = hide;
        }
        // The sight's lens too: the game lays it in at its upscaler whatever the camera draws, and with the gun kept out
        // it hung there by itself, a dark disc going with the camera.
        SSAAPropagator? propagator = Camera.main != null ? Camera.main.GetComponent<SSAAPropagator>() : null;
        if (hide && propagator != null)
        {
            _lensKept = LensRenderer(propagator);
            _collimatorKept = CollimatorRenderer(propagator);
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
    private static Renderer? _lensKept, _collimatorKept;

    // From NewCameraPlugin.EndOfFrame.
    internal static void EndOfFrame() => HideGun(false);

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
        material.SetFloat(NearReachId, NearReach);
        if (_gun == GunPass.Bare)
        {
            Graphics.Blit(scene, Kept(ref _bareGun, scene, "Justachae bare eye's gun"), material, NearPass);
            _bareDone = Time.frameCount;
            return;
        }
        if (_gun == GunPass.Goggle)
        {
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
        material.SetFloat(ShareGId, goggleGun ? GoggleOpacity : 0f);
        material.SetFloat(ShareBId, 0f);
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
        Material? material = MaterialFor(tubeShader);
        if (material == null || _pass || !Now || !_gunHidden || _bareGun == null || _bareDone != Time.frameCount)
            return;
        RenderTexture goggle = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, goggle);
        material.SetTexture(OtherId, _bareGun);
        material.SetFloat(OtherShareId, BareOpacity);
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
