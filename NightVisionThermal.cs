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
    private static bool _effectsCopied;

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
        if (_thermalCamera != null)
            Object.Destroy(_thermalCamera.gameObject);
        _thermalCamera = null;
        _thermal = null;
        _thermalFinish?.Release();
        _thermalFinish = null;
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
            _thermal.IsNoisy = source.IsNoisy;
            _thermal.IsFpsStuck = source.IsFpsStuck;
            _thermal.IsMotionBlurred = source.IsMotionBlurred;
            _thermal.IsGlitch = source.IsGlitch;
            _thermal.IsPixelated = source.IsPixelated;
            _thermal.ChromaticAberrationThermalShift = source.ChromaticAberrationThermalShift;
            _thermal.UnsharpRadiusBlur = source.UnsharpRadiusBlur;
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

        // At the main camera's own resolution before its SSAA stage scales it to the screen (lower with DLSS and the
        // like): the goggles' grain and pixels drawn at the scale the game draws them, then spread over the screen.
        SSAA? ssaa = main.GetComponent<SSAA>();
        int width = Mathf.Max(ssaa != null ? ssaa.GetInputWidth() : Screen.width, 1), height = Mathf.Max(ssaa != null ? ssaa.GetInputHeight() : Screen.height, 1);
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

        if (!_effectsCopied)
            CopyMainEffects(main);
        Camera camera = _thermalCamera;
        camera.CopyFrom(main);
        camera.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
        camera.rect = new Rect(0f, 0f, 1f, 1f);
        camera.projectionMatrix = main.nonJitteredProjectionMatrix;
        camera.targetTexture = _thermalPicture;
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
        }
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
