using System.Reflection;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// Thermal goggles (the T-7): the game turns every material on the main camera to heat for the whole frame, so that
// frame has no naked scene left for around the eyepieces. A thermal scope is done the other way: the main camera
// stays as it is and the scope's own camera renders the scene a second time with its own ThermalVision. So here: the
// game's thermal on the main camera is kept off while the eyepieces take the goggles, and a camera of our own, made
// from the same prefab the game makes its scope camera from, renders the thermal picture from the main camera's
// place and view (the device and its mount hidden as for the main camera), with the goggles' settings as the game
// gave them to the main camera's ThermalVision. The eyepieces show it as they show BRNVG's picture (LensPicture).
// (Taken out with the eyepieces-only branch, 2026-11-15, the goggles left to the game's picture over the whole frame;
// back 1.8.168 at the user's word, as 1.8.2 had it. The device is out of this camera's sight as it is out of the main
// camera's: HideDevice has its renderers casting shadows only by the time this renders.)
internal static partial class NightVisionEyepieces
{
    private static Camera? _thermalCamera;
    private static ThermalVision? _thermal;            // ours
    private static ThermalVision? _heldOff;            // the main camera's, kept off
    private static bool _thermalWanted;                // what the game last asked of it
    private static bool _switchingMain;
    private static RenderTexture? _thermalPicture;
    private static ThermalVisionDevice? _thermalDarkened;
    private static CommandBuffer? _thermalFinish;
    private static bool _effectsCopied, _pixelationNoted;
    private const float SharpenRadius = 1f;

    // What our camera's own image effects cost (the user's, 2026-11-22: "t-7的帧率也还是太低了"; 14 to 16 ms a frame
    // against the vanilla goggles' 10.5): once the pictures above are saved, 240 frames each of our camera as it is,
    // without the effects copied from the main camera (bloom, colour, sharpening), and without those and its
    // post-processing layer; then as it was. Each one's mean frame time to the log.
    private static int _costStep = -1, _costFrames;
    private static float _costSeconds;
    private static readonly System.Collections.Generic.List<MonoBehaviour> _costEffects = new System.Collections.Generic.List<MonoBehaviour>();
    private static PostProcessLayer? _costLayer;
    private static readonly string[] CostSteps = { "as it is", "without the main camera's effects", "without them and its post-processing layer" };

    private static void CostProbe(Camera camera)
    {
        if (_sharpenFrames <= SharpenOursAt || _costStep >= CostSteps.Length || !Active || _flip < 0.999f)
            return;
        if (_costStep < 0)
        {
            _costStep = 0;
            _costEffects.Clear();
            foreach (MonoBehaviour behaviour in camera.GetComponents<MonoBehaviour>())
            {
                if (behaviour != null && behaviour.enabled && System.Array.IndexOf(MainEffects, behaviour.GetType().Name) >= 0)
                    _costEffects.Add(behaviour);
            }
            _costLayer = camera.TryGetComponent(out PostProcessLayer layer) && layer.enabled ? layer : null;
            NewCameraPlugin.Log?.LogInfo($"NVG thermal cost: our camera has {_costEffects.Count} effect(s) copied from the main camera and {(_costLayer != null ? "a" : "no")} post-processing layer on.");
        }
        _costFrames++;
        if (_costFrames > 40)
            _costSeconds += Time.unscaledDeltaTime;
        if (_costFrames < 240)
            return;
        NewCameraPlugin.Log?.LogInfo($"NVG thermal cost {_costStep + 1}/{CostSteps.Length}: {_costSeconds / 200f * 1000f:F1} ms a frame over 200 frames, our camera {CostSteps[_costStep]}.");
        _costStep++;
        _costFrames = 0;
        _costSeconds = 0f;
        bool effects = _costStep == 0 || _costStep >= CostSteps.Length, layerOn = _costStep != 2;
        foreach (MonoBehaviour behaviour in _costEffects)
        {
            if (behaviour != null)
                behaviour.enabled = effects;
        }
        if (_costLayer != null)
            _costLayer.enabled = layerOn;
    }

    // The game's own picture of the same view, to hold ours against (the user's, 2026-11-22: "怎么感觉 T-7 看不到热源了",
    // and the steps at edges): the first time thermal goggles are switched on, the game is left to draw them its own
    // way for VanillaFrames frames (over the whole frame, no eyepieces), the screen saved near their end; then ours.
    private static int _vanillaFrames;
    private const int VanillaFrames = 240, VanillaSaveAt = 200;

    internal static bool VanillaProbe(bool on)
    {
        if (_vanillaFrames >= VanillaFrames)
            return false;
        if (!on)
            return true;
        _vanillaFrames++;
        if (_vanillaFrames == VanillaSaveAt)
        {
            try
            {
                string folder = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, ".codex-work", "nvgdump", "t7-edges");
                System.IO.Directory.CreateDirectory(folder);
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, "vanilla-screen.png"));
                ThermalVision? main = BodyVisibility.Cameras?.ThermalVision;
                NewCameraPlugin.Log?.LogInfo($"NVG thermal: saved the game's own picture to {folder} (its ThermalVision on {main?.On}, noisy {main?.IsNoisy}, blurred {main?.IsMotionBlurred}, pixelated {main?.IsPixelated}, stuck {main?.IsFpsStuck}, sharpening {main?.UnsharpRadiusBlur}/{main?.UnsharpBias}).");
            }
            catch (System.Exception e)
            {
                NewCameraPlugin.Log?.LogWarning("NVG thermal: saving the game's picture failed: " + e.Message);
            }
        }
        return true;
    }
    private static int _sharpenFrames;
    private const int SharpenGameUntil = 150, SharpenOursAt = 300;

    // The first frames with the goggles down and settled: the game's own reach (true), then ours.
    private static bool SharpenProbe() => _sharpenFrames < SharpenGameUntil;

    // Once our camera has rendered: the picture saved at the end of each stretch, as it is and not scaled.
    private static void SaveSharpenProbe(ThermalVision? source)
    {
        if (_sharpenFrames > SharpenOursAt || !Active || _flip < 0.999f || _thermalPicture == null)
            return;
        _sharpenFrames++;
        if (_sharpenFrames != SharpenGameUntil - 1 && _sharpenFrames != SharpenOursAt)
            return;
        string tag = _sharpenFrames == SharpenOursAt ? "reach-ours" : "reach-game";
        try
        {
            string folder = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, ".codex-work", "nvgdump", "t7-edges");
            System.IO.Directory.CreateDirectory(folder);
            int width = _thermalPicture.width, height = _thermalPicture.height;
            RenderTexture plain = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            plain.filterMode = FilterMode.Point;
            Graphics.Blit(_thermalPicture, plain);
            RenderTexture? before = RenderTexture.active;
            RenderTexture.active = plain;
            var read = new Texture2D(width, height, TextureFormat.RGB24, false);
            read.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            read.Apply(false);
            RenderTexture.active = before;
            RenderTexture.ReleaseTemporary(plain);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, tag + "-picture.png"), read.EncodeToPNG());
            Object.Destroy(read);
            ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, tag + "-screen.png"));
            NewCameraPlugin.Log?.LogInfo($"NVG thermal: saved {tag} to {folder} (picture {width}x{height}; sharpening reach {_thermal?.UnsharpRadiusBlur}, bias {_thermal?.UnsharpBias}; the game's {source?.UnsharpRadiusBlur}, {source?.UnsharpBias}; stuck {_thermal?.IsFpsStuck}).");
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("NVG thermal: saving the picture failed: " + e.Message);
        }
    }

    // The picture held over frames (NvgIrLight.shader pass 6). The vanilla goggles' frame is the main camera's, jittered
    // and smoothed over frames by the game; ours was one sample a pixel from a camera not jittered, and every edge in
    // it crawled with the breathing (the user's, 2026-11-22: "跟 ENVG-B 遇到的波动差不多"). So: our camera jittered
    // by a sub-pixel pattern (its own layer's, when that has temporal anti-aliasing on; else one of our own), and its
    // frame blended into the frames before. Where: after the goggles' own passes (their grain with them) and before
    // the one the SSAA stage gives the main camera's (FinishThermal), which is where the game's smoothing stands for
    // the main camera's frame: the grain is smoothed with the rest, as the vanilla goggles' is. (1.8.171 had it
    // before the goggles' passes, the grain left whole and new every frame: the user's "感觉还是有噪点".)
    private static CommandBuffer? _thermalHold;
    private static Material? _thermalHoldMaterial;
    private static readonly RenderTexture?[] _thermalHistory = new RenderTexture?[2];
    private static int _thermalHistoryNow, _thermalHeldFrame = -10, _thermalJitterAt;
    private static Matrix4x4 _thermalThen;
    private static bool _thermalJitterHooked, _thermalHoldNoted;
    private const int ThermalHoldPass = 6;            // NvgIrLight.shader
    private const float ThermalHoldShare = 0.12f;     // how much of each new frame goes in
    private const int ThermalJitterCount = 8;
    private static readonly int ThermalHistoryId = Shader.PropertyToID("_ThermalHistory");
    private static readonly int ThermalFrameId = Shader.PropertyToID("_JustachaeThermalFrame");
    private static readonly int ThermalClipToWorldId = Shader.PropertyToID("_ThermalClipToWorld");
    private static readonly int ThermalNowId = Shader.PropertyToID("_ThermalNow");
    private static readonly int ThermalThenId = Shader.PropertyToID("_ThermalThen");
    private static readonly int ThermalHoldId = Shader.PropertyToID("_ThermalHold");

    private static float Halton(int index, int radix)
    {
        float result = 0f, fraction = 1f / radix;
        while (index > 0)
        {
            result += index % radix * fraction;
            index /= radix;
            fraction /= radix;
        }
        return result;
    }

    // Before our camera renders: this frame's command buffer, the last frame's history to read and the other to write.
    private static void PrepareThermalHold(int width, int height)
    {
        if (_thermalCamera == null || _irLightShader == null)
            return;
        for (int i = 0; i < 2; i++)
        {
            RenderTexture? history = _thermalHistory[i];
            if (history != null && history.width == width && history.height == height)
                continue;
            if (history != null)
            {
                history.Release();
                Object.Destroy(history);
            }
            _thermalHistory[i] = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf)
            {
                name = "Justachae thermal history " + i, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
            };
            _thermalHeldFrame = -10;
        }
        _thermalHoldMaterial ??= new Material(_irLightShader) { name = "Justachae thermal hold", hideFlags = HideFlags.DontSave };
        if (_thermalHold == null)
        {
            _thermalHold = new CommandBuffer { name = "Justachae thermal held over frames" };
            // After the game's own buffer there (the goggles' passes, added as its ThermalVision woke), before ours
            // that finishes the picture.
            if (_thermalFinish != null)
                _thermalCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _thermalFinish);
            _thermalCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _thermalHold);
            if (_thermalFinish != null)
                _thermalCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _thermalFinish);
        }
        if (!_thermalJitterHooked)
        {
            _thermalJitterHooked = true;
            Camera.onPreRender += JitterThermal;
        }
        int read = _thermalHistoryNow, write = 1 - read;
        _thermalHistoryNow = write;
        _thermalHold.Clear();
        _thermalHold.GetTemporaryRT(ThermalFrameId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
        _thermalHold.Blit(BuiltinRenderTextureType.CameraTarget, ThermalFrameId);
        _thermalHold.SetGlobalTexture(ThermalHistoryId, _thermalHistory[read]);
        _thermalHold.Blit(ThermalFrameId, _thermalHistory[write], _thermalHoldMaterial, ThermalHoldPass);
        // (Last onto the camera's own target, as the game's buffer before this one leaves it.)
        _thermalHold.Blit(_thermalHistory[write], BuiltinRenderTextureType.CameraTarget);
        _thermalHold.ReleaseTemporaryRT(ThermalFrameId);
    }

    // As our camera is about to render (its layer has set its projection by now): jittered, and the matrices the hold
    // follows the still world by.
    private static void JitterThermal(Camera camera)
    {
        if (camera != _thermalCamera || _thermalHoldMaterial == null || _thermalPicture == null)
            return;
        // Its own layer's temporal anti-aliasing has jittered it by now (the log of 1.8.171: on, for ours as for the main
        // camera): that jitter taken as it is. Else one of our own.
        bool layerJitters = camera.TryGetComponent(out PostProcessLayer ownLayer) && ownLayer.enabled
            && ownLayer.antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing;
        Matrix4x4 plain = layerJitters ? camera.nonJitteredProjectionMatrix : camera.projectionMatrix, jittered = camera.projectionMatrix;
        float x = (jittered.m02 - plain.m02) * 0.5f * _thermalPicture.width, y = (jittered.m12 - plain.m12) * 0.5f * _thermalPicture.height;
        if (!layerJitters)
        {
            _thermalJitterAt = (_thermalJitterAt + 1) % ThermalJitterCount;
            x = Halton(_thermalJitterAt + 1, 2) - 0.5f;
            y = Halton(_thermalJitterAt + 1, 3) - 0.5f;
            jittered.m02 += 2f * x / _thermalPicture.width;
            jittered.m12 += 2f * y / _thermalPicture.height;
            camera.nonJitteredProjectionMatrix = plain;
            camera.projectionMatrix = jittered;
        }
        Matrix4x4 view = camera.worldToCameraMatrix, now = plain * view;
        // A history only from the frame right before this one.
        bool follows = _thermalHeldFrame == Time.frameCount - 1;
        _thermalHoldMaterial.SetMatrix(ThermalClipToWorldId, (jittered * view).inverse);
        _thermalHoldMaterial.SetMatrix(ThermalNowId, now);
        _thermalHoldMaterial.SetMatrix(ThermalThenId, follows ? _thermalThen : now);
        _thermalHoldMaterial.SetVector(ThermalHoldId, new Vector4(follows ? 1f : 0f, ThermalHoldShare, 0f, 0f));
        _thermalThen = now;
        _thermalHeldFrame = Time.frameCount;
        if (!_thermalHoldNoted && follows)
        {
            _thermalHoldNoted = true;
            string own = camera.TryGetComponent(out PostProcessLayer layer) ? layer.antialiasingMode.ToString() : "no layer";
            Camera? main = Camera.main;
            string mains = main != null && main.TryGetComponent(out PostProcessLayer mainLayer) ? mainLayer.antialiasingMode.ToString() : "no layer";
            NewCameraPlugin.Log?.LogInfo($"NVG thermal: the picture held over frames ({_thermalPicture.width}x{_thermalPicture.height}, jitter {x:F2},{y:F2} px, {(layerJitters ? "its layer's" : "our own")}; after the goggles' passes); anti-aliasing of our camera's layer {own}, of the main camera's {mains}.");
        }
    }

    private static readonly AccessTools.FieldRef<ThermalVision, Material> ThermalMaterial =
        AccessTools.FieldRefAccess<ThermalVision, Material>("_material");
    private static readonly int ThermalFinishId = Shader.PropertyToID("_JustachaeThermalFinish");

    private static void InstallThermal(Harmony harmony)
    {
        MethodInfo? switchMethod = AccessTools.Method(typeof(ThermalVision), nameof(ThermalVision.Switch));
        MethodInfo? prefix = typeof(NightVisionEyepieces).GetMethod(nameof(ThermalSwitchPrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (switchMethod != null && prefix != null)
            harmony.Patch(switchMethod, prefix: new HarmonyMethod(prefix));
    }

    // The game switching the main camera's thermal while the eyepieces have the goggles: noted, and kept off.
    private static void ThermalSwitchPrefix(ThermalVision __instance, ref bool on)
    {
        if (_switchingMain || __instance == _thermal || __instance != _heldOff)
            return;
        _thermalWanted = on;
        on = false;
    }

    // Sealed thermal goggles all the way down: their cups shut out all there is around the eyepieces, and the main
    // camera's whole scene was drawn only to be hidden behind them, while the picture in the eyepieces is our thermal
    // camera's, a second drawing of the scene (the user's, 2026-11-22: "只画 T-7 目镜里的东西"). So the main camera
    // draws no scene then, for that frame only: nothing in its culling mask, cleared to black. This mod's own drawing
    // on it (the device from each eye, the eyepieces) is by command buffers and goes on. Not while the goggles flip,
    // when what is around them is seen.
    private static Camera? _sceneSkipped;
    private static int _skippedMask;
    private static CameraClearFlags _skippedClear;
    private static Color _skippedBackground;
    // And it sees no further than the device (SkippedFar): with nothing in its culling mask the lights were still lit
    // and their shafts through the air still marched, and with no scene to stop at they went on to the far plane,
    // long and bright, into the frame the eyepieces are laid on (the user's "白色阴影", "看起来很像光", there from
    // the frame the scene was first left out). A light or a fog further off than that is not in its sight at all.
    private static float _skippedFarPlane;
    private const float SkippedFar = 2f;
    private static bool _skipNoted;

    private static bool ThermalAlone => _direct && _device.Thermal && _device.Sealed && Active && _flip >= 0.999f
        && _thermalCamera != null && _thermalPicture != null;

    // As the main camera is about to cull.
    private static void SkipMainScene(Camera camera)
    {
        GiveMainSceneBack();
        if (!ThermalAlone)
            return;
        _sceneSkipped = camera;
        _skippedMask = camera.cullingMask;
        _skippedClear = camera.clearFlags;
        _skippedBackground = camera.backgroundColor;
        _skippedFarPlane = camera.farClipPlane;
        if (camera.farClipPlane > SkippedFar)
            camera.farClipPlane = SkippedFar;
        camera.cullingMask = 0;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        if (!_skipNoted)
        {
            _skipNoted = true;
            NewCameraPlugin.Log?.LogInfo("NVG thermal: sealed goggles down, the main camera draws no scene (only the device and the eyepieces).");
        }
    }

    // Once it has rendered (and whenever the goggles are let go of).
    private static void GiveMainSceneBack()
    {
        if (_sceneSkipped != null)
        {
            _sceneSkipped.cullingMask = _skippedMask;
            _sceneSkipped.clearFlags = _skippedClear;
            _sceneSkipped.backgroundColor = _skippedBackground;
            _sceneSkipped.farClipPlane = _skippedFarPlane;
        }
        _sceneSkipped = null;
    }

    // Measured 2026-11-22 (1.8.176, the hideout, 2560x1440; the vanilla goggles 10.5 ms): the picture at the main
    // camera's size before its SSAA stage (1707x960) with the scene drawn 18.3 ms a frame, not drawn 14.0; at the
    // screen's size not drawn 19.0, drawn 24.2.
    //
    // With the scene not drawn, light bands came into what the eyepieces show ("白色阴影"): the lights' shafts, gone
    // with the far plane brought in (SkippedFar; 15.6 ms a frame then, elsewhere in the hideout).
    //
    // The picture at the main camera's size before its SSAA stage, not the screen's (the user's, on the numbers above:
    // 71 frames a second down to 53 "不是更差了吗").
    private static bool PictureAtInputSize => true;

    // The main camera's thermal held off (taken) or given back as the game last asked for it.
    private static void HoldMainThermal(CameraManager? cameras, bool taken)
    {
        ThermalVision? main = taken ? cameras?.ThermalVision : null;
        if (_heldOff != null && _heldOff != main)
        {
            if (_thermalWanted && _heldOff != null)
                SwitchMain(_heldOff, true);
            _heldOff = null;
            _thermalWanted = false;
        }
        if (main == null)
            return;
        _heldOff = main;
        if (main.On)
        {
            _thermalWanted = true;
            SwitchMain(main, false);
        }
    }

    private static void SwitchMain(ThermalVision thermal, bool on)
    {
        _switchingMain = true;
        try
        {
            thermal.Switch(on);
        }
        finally
        {
            _switchingMain = false;
        }
    }

    // The goggles' own light (their glow on the face) off, or back as the game would have it.
    private static void KeepThermalGlow(ThermalVisionDevice? device)
    {
        if (_thermalDarkened != null && _thermalDarkened != device && _thermalDarkened._light != null)
            _thermalDarkened.CG_Init();
        _thermalDarkened = device;
        if (device != null && device._light != null && device._light.enabled)
            device._light.enabled = false;
    }

    // Our thermal camera: the game's scope camera prefab, without what ties it to a scope.
    private static bool EnsureThermalCamera()
    {
        if (_thermalCamera != null && _thermal != null)
            return true;
        if (UnityResourcesProxy.Load("BaseOpticCamera") is not GameObject prefab)
            return false;
        var holder = new GameObject("Justachae thermal holder");
        holder.SetActive(false);
        GameObject made = Object.Instantiate(prefab, holder.transform);
        made.name = "Justachae thermal camera";
        made.tag = "Untagged";
        foreach (MonoBehaviour behaviour in made.GetComponents<MonoBehaviour>())
        {
            if (behaviour is OpticComponentUpdater || behaviour is OpticRetrice)
                Object.DestroyImmediate(behaviour);
        }
        made.transform.SetParent(null, false);
        Object.Destroy(holder);
        Object.DontDestroyOnLoad(made);
        _thermalCamera = made.GetComponent<Camera>();
        _thermal = made.GetComponent<ThermalVision>();
        if (made.TryGetComponent(out BSG.CameraEffects.NightVision nightVision))
            nightVision.enabled = false;
        if (made.TryGetComponent(out PostProcessLayer layer))
            layer.computeAutoExposure = false;
        if (_thermalCamera == null || _thermal == null)
        {
            Object.Destroy(made);
            _thermalCamera = null;
            _thermal = null;
            return false;
        }
        _thermalCamera.enabled = false;
        // The prefab comes with its ThermalVision off (the game turns it on only for a thermal scope), and with what
        // makes a scope's picture: its lens's warp and vignette, and its own culling.
        _thermal.enabled = true;
        foreach (MonoBehaviour behaviour in made.GetComponents<MonoBehaviour>())
        {
            string kind = behaviour != null ? behaviour.GetType().Name : "";
            if (kind == "Fisheye" || kind == "CC_FastVignette" || kind == "OpticCullingMask")
                behaviour!.enabled = false;
        }
        return true;
    }

    private static void ReleaseThermal()
    {
        HoldMainThermal(BodyVisibility.Cameras, false);
        KeepThermalGlow(null);
        GiveMainSceneBack();
        if (_thermalCamera != null)
            Object.Destroy(_thermalCamera.gameObject);
        _thermalCamera = null;
        _thermal = null;
        _thermalFinish?.Release();
        _thermalFinish = null;
        _thermalHold?.Release();
        _thermalHold = null;
        _thermalHeldFrame = -10;
        for (int i = 0; i < 2; i++)
        {
            RenderTexture? history = _thermalHistory[i];
            if (history == null)
                continue;
            history.Release();
            Object.Destroy(history);
            _thermalHistory[i] = null;
        }
        _effectsCopied = false;
        if (_thermalPicture != null)
        {
            _thermalPicture.Release();
            Object.Destroy(_thermalPicture);
        }
        _thermalPicture = null;
    }

    // The thermal picture of what the main camera sees, from its place and with its view (not jittered), with the
    // goggles' settings as the game set them on the main camera's ThermalVision.
    private static void RenderThermal(Camera main)
    {
        if (_thermalCamera == null || _thermal == null)
            return;
        ThermalVision? source = BodyVisibility.Cameras?.ThermalVision;
        if (source != null)
        {
            _thermal.ThermalVisionUtilities = source.ThermalVisionUtilities;
            _thermal.StuckFpsUtilities = source.StuckFpsUtilities;
            _thermal.MotionBlurUtilities = source.MotionBlurUtilities;
            _thermal.GlitchUtilities = source.GlitchUtilities;
            _thermal.PixelationUtilities = source.PixelationUtilities;
            // No grain on the goggles' picture (the user's, 2026-11-22: the real T-7 has none to speak of; the game adds it).
            _thermal.IsNoisy = false;
            _thermal.IsFpsStuck = source.IsFpsStuck;
            // Nor the lag the game gives them (each frame blended into the ones before, through a texture of its own, by up
            // to 0.92: at our frame rate everything bright left a trail; the user's, 2026-11-22, "物体也拖影").
            _thermal.IsMotionBlurred = false;
            _thermal.IsGlitch = source.IsGlitch;
            // Nor the game's blocks (256 across, some 7 pixels of this picture each): the goggles' own data has them off
            // (items.json: IsPixelated false), yet the main camera's ThermalVision, held off here, still said on, and
            // the picture came in blocks (the pictures saved 2026-11-22; the user's "还是不清晰").
            if (!_pixelationNoted)
            {
                _pixelationNoted = true;
                NewCameraPlugin.Log?.LogInfo($"NVG thermal: the main camera's ThermalVision says noisy {source.IsNoisy}, blurred {source.IsMotionBlurred}, pixelated {source.IsPixelated} (mode {source.PixelationUtilities?.Mode}, {source.PixelationUtilities?.BlockCount} blocks), stuck {source.IsFpsStuck}, glitch {source.IsGlitch}; ours: none of the first three.");
            }
            _thermal.IsPixelated = false;
            _thermal.ChromaticAberrationThermalShift = source.ChromaticAberrationThermalShift;
            // The goggles' sharpening reaches SharpenRadius of this picture's pixels, not the game's 5: with pixelation
            // off the edges still came in steps some 5 pixels deep (the user's, 2026-11-22: "物体边缘的地方有像素感"),
            // which is that reach. Measured, not taken on trust: the first time the goggles are down the picture is
            // saved once at the game's reach and once at ours (SharpenProbe).
            _thermal.UnsharpRadiusBlur = SharpenProbe() ? source.UnsharpRadiusBlur : SharpenRadius;
            _thermal.UnsharpBias = source.UnsharpBias;
        }
        if (!_thermal.On)
        {
            try
            {
                _thermal.Switch(true);
            }
            catch (System.Exception e)
            {
                _thermal.On = true;
                NewCameraPlugin.Log?.LogWarning("Thermal picture: switching it on failed, set on directly: " + e.Message);
            }
        }
        if (!_thermal.enabled)
            _thermal.enabled = true;
        if (_thermal.TextureMask != null && _thermal.TextureMask.enabled)
            _thermal.TextureMask.enabled = false;
        FinishThermal();

        // At the screen's own size (the user's, 2026-11-22: what not drawing the main camera's scene saves goes to the
        // goggles' picture). It had been the main camera's size before its SSAA stage scales it to the screen (two thirds
        // of it at the user's settings), for the goggles' grain to be at the game's scale; the grain is off now.
        int width = Mathf.Max(Screen.width, 1), height = Mathf.Max(Screen.height, 1);
        if (PictureAtInputSize && main.TryGetComponent(out SSAA ssaa))
        {
            width = Mathf.Max(ssaa.GetInputWidth(), 1);
            height = Mathf.Max(ssaa.GetInputHeight(), 1);
        }
        if (_thermalPicture == null || _thermalPicture.width != width || _thermalPicture.height != height)
        {
            if (_thermalPicture != null)
            {
                _thermalPicture.Release();
                Object.Destroy(_thermalPicture);
            }
            _thermalPicture = new RenderTexture(width, height, 24, main.allowHDR ? RenderTextureFormat.ARGBHalf : RenderTextureFormat.ARGB32)
            {
                name = "Justachae thermal picture",
                filterMode = FilterMode.Bilinear,
            };
            _thermalPicture.Create();
        }

        PrepareThermalHold(width, height);
        if (!_effectsCopied)
            CopyMainEffects(main);
        Camera camera = _thermalCamera;
        camera.CopyFrom(main);
        camera.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        // The main camera as it would be, were its scene not left out for this frame (SkipMainScene).
        if (_sceneSkipped == main)
        {
            camera.cullingMask = _skippedMask;
            camera.clearFlags = _skippedClear;
            camera.backgroundColor = _skippedBackground;
            camera.farClipPlane = _skippedFarPlane;
        }
        camera.projectionMatrix = main.nonJitteredProjectionMatrix;
        camera.targetTexture = _thermalPicture;
        // Its layer's own temporal anti-aliasing off: that follows the engine's motion vectors, which this camera, rendered
        // out of turn, has none worth the name (they smeared a motion blur over its whole picture); with the view moving it
        // left copies of every bright thing behind it, in steps (the lamp in the picture saved 2026-11-22). The hold above
        // is the one smoothing over frames, by this camera's own matrices, with a jitter of our own (JitterThermal).
        if (camera.TryGetComponent(out PostProcessLayer ownLayer) && ownLayer.antialiasingMode != PostProcessLayer.Antialiasing.None)
            ownLayer.antialiasingMode = PostProcessLayer.Antialiasing.None;
        HoldMotionBlur(camera);
        try
        {
            camera.Render();
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("Thermal picture: render failed: " + e);
        }
        finally
        {
            camera.ResetProjectionMatrix();
            camera.targetTexture = null;
            GiveMotionBlurBack();
        }
        SaveSharpenProbe(source);
        CostProbe(camera);
    }

    // No post-processing motion blur on this camera's picture: it is rendered out of turn, from a place set by hand,
    // and has no motion vectors of its own worth the name; a motion blur in a volume it comes under (any mod's, or
    // the game's) smeared its whole picture (the user's, 2026-11-22, with another mod's motion blur on). Off in the
    // volumes this camera's layer takes, for this render only; the main camera's frame keeps it.
    private static readonly System.Collections.Generic.List<PostProcessVolume> _thermalVolumes = new System.Collections.Generic.List<PostProcessVolume>();
    private static readonly System.Collections.Generic.List<MotionBlur> _motionBlurHeld = new System.Collections.Generic.List<MotionBlur>();
    private static bool _motionBlurNoted;

    private static void HoldMotionBlur(Camera camera)
    {
        _motionBlurHeld.Clear();
        if (!camera.TryGetComponent(out PostProcessLayer layer) || PostProcessManager.instance == null)
            return;
        _thermalVolumes.Clear();
        PostProcessManager.instance.GetActiveVolumes(layer, _thermalVolumes, true, true);
        foreach (PostProcessVolume volume in _thermalVolumes)
        {
            PostProcessProfile? profile = volume != null ? volume.sharedProfile : null;
            if (volume != null && volume.HasInstantiatedProfile())
                profile = volume.profile;
            if (profile == null || !profile.TryGetSettings(out MotionBlur blur) || blur == null || !blur.enabled.value || _motionBlurHeld.Contains(blur))
                continue;
            blur.enabled.value = false;
            _motionBlurHeld.Add(blur);
        }
        if (_motionBlurHeld.Count > 0 && !_motionBlurNoted)
        {
            _motionBlurNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG thermal: a post-processing motion blur ({_motionBlurHeld.Count}) kept off our thermal camera's picture.");
        }
    }

    private static void GiveMotionBlurBack()
    {
        foreach (MotionBlur blur in _motionBlurHeld)
        {
            if (blur != null)
                blur.enabled.value = true;
        }
        _motionBlurHeld.Clear();
    }

    // On the main camera the game's SSAA stage gives the thermal frame one more pass of the goggles' own material
    // (SSAAPropagator: pass 1, once the scene and ThermalVision's own passes are done, before the rest of the
    // post-processing); a scope's camera has no such stage. Ours does it at the same point, after ThermalVision's own
    // buffer there, with the same material as ThermalVision set it for this frame.
    private static void FinishThermal()
    {
        if (_thermalCamera == null || _thermal == null || _thermalFinish != null)
            return;
        Material? material = ThermalMaterial(_thermal);
        if (material == null)
            return;
        _thermalFinish = new CommandBuffer { name = "Justachae thermal SSAA pass" };
        _thermalFinish.GetTemporaryRT(ThermalFinishId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
        _thermalFinish.Blit(BuiltinRenderTextureType.CameraTarget, ThermalFinishId);
        _thermalFinish.Blit(ThermalFinishId, BuiltinRenderTextureType.CameraTarget, material, 1);
        _thermalFinish.ReleaseTemporaryRT(ThermalFinishId);
        _thermalCamera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _thermalFinish);
    }

    // The image effects the game runs the goggles' frame through on the main camera, after its thermal passes: its
    // own (bloom, colour, sharpening) and the bloom it switches on for thermal (ThermalVision.SwitchComponentsOn). A
    // scope's camera has none of them; ours gets each, in the main camera's order, set as the game set the main
    // camera's, and runs it on its own frame.
    private static readonly string[] MainEffects = { "Bloom", "UltimateBloom", "PrismEffects", "BloomAndFlares", "CC_Sharpen" };

    private static void CopyMainEffects(Camera main)
    {
        _effectsCopied = true;
        if (_thermalCamera == null)
            return;
        ThermalVision? mainThermal = main.GetComponent<ThermalVision>();
        var switchedOn = new System.Collections.Generic.HashSet<MonoBehaviour>(mainThermal?.SwitchComponentsOn ?? System.Array.Empty<MonoBehaviour>());
        GameObject ours = _thermalCamera.gameObject;
        // Made while ours is inactive, so each wakes with the main camera's settings already in it.
        ours.SetActive(false);
        try
        {
            foreach (MonoBehaviour effect in main.GetComponents<MonoBehaviour>())
            {
                if (effect == null || System.Array.IndexOf(MainEffects, effect.GetType().Name) < 0)
                    continue;
                bool wanted = effect.enabled || switchedOn.Contains(effect);
                if (!wanted)
                    continue;
                Component made = ours.AddComponent(effect.GetType());
                CopySettings(effect, made);
                ((MonoBehaviour)made).enabled = true;
            }
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("Thermal picture: copying the main camera's effects failed: " + e);
        }
        finally
        {
            ours.SetActive(true);
        }
    }

    // What Unity would save of a component: its public fields and those marked to be serialised, down its classes.
    private static void CopySettings(Component from, Component to)
    {
        for (System.Type? type = from.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsNotSerialized || field.IsInitOnly || (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null))
                    continue;
                field.SetValue(to, field.GetValue(from));
            }
        }
    }
}
