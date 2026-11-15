using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

internal static class OpticElevation
{
    // Explicit B list, plus C/D as requested. Unconfirmed optics and A remain native.
    private static readonly HashSet<string> Supported = new()
    {
        "56ea70acd2720b844b8b4594", "57c5ac0824597754771e88a9",
        "5a37cb10c4a282329a73b4e7", "5aa66be6e5b5b0214e506e97",
        "617151c1d92c473c770214ab", "61714eec290d254f5e6b2ffc", "62850c28da09541f43158cca",
        "676175789dcee773150c6925", "5b2388675acfc4771e1be0be", "5b3b99475acfc432ff4dcbee",
        "618ba27d9008e4636a67f61d", "6567e7681265c8a131069b0f", "5dfe6104585a0c3e995c7b82",
        "544a3d0a4bdc2d1b388b4567", "68caad7f933990601c07f47c", "6a3ceaff7047a606b90f5321",
        "67999c756e3bd6cbe36d2ba2", "67999c8251c50313d9e21910", "67999c87c061e3d7f3b0ab61",
        "57ac965c24597706be5f975c", "57aca93d2459771f2c7e26db",
        "5a1eaa87fcdbcb001865f75e", "5d1b5e94d7ad1a2b865a96b0", "63fc44e2429a8a166c7f61e6",
        "6478641c19d732620e045e17", "67641b461c2eb66ade05dba6", "609bab8b455afd752b2e6138",
        "606f2696f2cb2e02a42aceb1",
        "5d0a3a58d7ad1a669c15ca14", // KMZ 1P59 3-10x (user-selected B behaviour)
        "5d0a3e8cd7ad1a6f6a3d35bd"  // KMZ 1P69 3-10x (user-selected B behaviour)
    };
    private static readonly Dictionary<string, float> Offsets = new();
    private static readonly HashSet<string> ReticleMoving = new()
    {
        "5b3f7c1c5acfc40dc5296b1d", // PU 3.5x
        "5c82342f2e221644f31c060e", // PSO-1
        "5c82343a2e221644f31c0611", // PSO-1M2
        "576fd4ec2459777f0b518431"  // PSO-1M2-1
    };
    private static Matrix4x4 _reticleProjection, _reticleNonJittered;
    // Not offered as settings. One step is the real turret click where a published value was found, in mrad
    // (1/4 MOA = 0.0727, 1/2 MOA = 0.1454). Where the game's reticle shows which of a scope's MOA / MIL versions
    // it is, that version's click is used. Everything else (no reliable value, version unknown, digital thermal
    // zeroing, the range-drum Russian scopes) keeps the uniform 0.05 mrad.
    private const float DefaultStepMrad = 0.05f;
    private const float QuarterMoa = 0.0727f;
    private const float HalfMoa = 0.1454f;
    private static readonly Dictionary<string, float> StepMrad = new()
    {
        ["56ea70acd2720b844b8b4594"] = 0.1f,       // Hensoldt FF 4-16x56: 1 cm / 100 m
        ["57c5ac0824597754771e88a9"] = 0.1f,       // March Tactical 3-24x42 FFP: 0.1 mil
        ["6a3ceaff7047a606b90f5321"] = 0.1f,       // DEON March-F 1-8x24 Shorty (WTT): 0.1 mil
        ["5a37cb10c4a282329a73b4e7"] = QuarterMoa, // Leupold Mark 4 LR/T 6.5-20x50 M1: 1/4 MOA
        ["5aa66be6e5b5b0214e506e97"] = QuarterMoa, // Nightforce ATACR 7-35x56: in-game reticle is marked MOA (10/20/30/40), 1/4 MOA
        ["676175789dcee773150c6925"] = 0.1f,       // Leupold Mark 5HD 5-25x56 FDE: in-game reticle is in mil (1-7), M5C3 0.1 mil
        ["61714eec290d254f5e6b2ffc"] = 0.1f,       // S&B PM II 3-20x50: 0.1 mrad
        ["62850c28da09541f43158cca"] = 0.1f,       // S&B PM II 5-25x56: 0.1 mrad
        ["67999c8251c50313d9e21910"] = 0.1f,       // S&B PM II 3-20x50 Black (WA2000): 0.1 mrad
        ["67999c87c061e3d7f3b0ab61"] = 0.1f,       // S&B PM II 5-25x56 Black (WA2000): 0.1 mrad
        ["5b2388675acfc4771e1be0be"] = HalfMoa,    // Burris FullField TAC30 1-4x24: 1/2 MOA
        ["5b3b99475acfc432ff4dcbee"] = 0.2f,       // EOTech Vudu 1-6x24: SR-1 MRAD reticle, 0.2 mrad
        ["618ba27d9008e4636a67f61d"] = HalfMoa,    // Vortex Razor HD Gen II 1-6x24 (JM-1): 1/2 MOA
        ["6567e7681265c8a131069b0f"] = 0.2f,       // SIG TANGO6T 1-6x24: 0.2 mrad
        ["5dfe6104585a0c3e995c7b82"] = HalfMoa,    // NcSTAR ADO P4 Sniper 3-9x42: 1/2 MOA
        ["544a3d0a4bdc2d1b388b4567"] = QuarterMoa, // Nightforce NXS 2.5-10x24: MOA reticle (Burris BDC), 1/4 MOA
        ["57ac965c24597706be5f975c"] = HalfMoa,    // ELCAN SpecterDR 1x/4x: 1/2 MOA
        ["57aca93d2459771f2c7e26db"] = HalfMoa,    // ELCAN SpecterDR 1x/4x FDE: 1/2 MOA
    };

    private static float Step(SightComponent mod)
        => StepMrad.TryGetValue(mod.Item.TemplateId, out float step) ? step : DefaultStepMrad;
    private const float RepeatPerSecond = 60f;
    private const float SmoothSeconds = 0.04f;
    private static readonly Dictionary<string, float> Displayed = new();
    private static int _inputFrame = -1, _smoothFrame = -1;
    private static string _heldKey = "";
    private static int _heldSign;
    private static float _repeatAt;
    private static Camera? _camera;
    private static Matrix4x4 _projection, _nonJittered;
    private static readonly AccessTools.FieldRef<OpticRetrice, CommandBuffer> ReticleBuffer = AccessTools.FieldRefAccess<OpticRetrice, CommandBuffer>("_commandBuffer");
    private static readonly AccessTools.FieldRef<OpticRetrice, Material> ReticleMaterial = AccessTools.FieldRefAccess<OpticRetrice, Material>("_material");
    private static readonly AccessTools.FieldRef<OpticRetrice, float> ReticleScale = AccessTools.FieldRefAccess<OpticRetrice, float>("_scale");
    private static readonly AccessTools.FieldRef<FirearmHandsInputTranslator, IFirearmHandsController> Controller = AccessTools.FieldRefAccess<FirearmHandsInputTranslator, IFirearmHandsController>("_controller");

    internal static void Initialize()
    {
        LegacyReticle.Install();
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".opticElevation");
        harmony.Patch(AccessTools.Method(typeof(FirearmHandsInputTranslator), "TranslateCommand"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(InputPrefix)));
        harmony.Patch(AccessTools.Method(typeof(OpticRetrice), "OnPreCull"),
            postfix: new HarmonyMethod(typeof(OpticElevation), nameof(ReticlePostfix)));
        harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), "OpticCalibrationSwitchUp"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(Up)));
        harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), "OpticCalibrationSwitchDown"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(Down)));
        harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), "OpticCalibrationSwitch"),
            prefix: new HarmonyMethod(typeof(OpticElevation), nameof(Switch)));
    }

    private static bool Up(ProceduralWeaponAnimation __instance) => Adjust(__instance, 1f);
    private static bool Down(ProceduralWeaponAnimation __instance) => Adjust(__instance, -1f);
    private static bool Switch(ProceduralWeaponAnimation __instance, bool __0) => Adjust(__instance, __0 ? 1f : -1f);

    // Intercept before the controller receives a ScopeState[] with a changed native zero index.
    private static bool InputPrefix(FirearmHandsInputTranslator __instance, ECommand __0, ref InputNode.ETranslateResult __result)
    {
        if (__0 != ECommand.OpticCalibrationSwitchUp && __0 != ECommand.OpticCalibrationSwitchDown) return true;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(Controller(__instance), player.HandsController)
            || !TryTarget(out SightComponent? mod))
        {
            return true;
        }
        if (UnityEngine.Input.GetKey(KeyCode.PageUp) || UnityEngine.Input.GetKey(KeyCode.PageDown))
            UpdateHeld();
        else
            Change(mod!, __0 == ECommand.OpticCalibrationSwitchUp ? 1f : -1f);
        __result = InputNode.ETranslateResult.Block;
        return false;
    }

    private static void Change(SightComponent mod, float sign)
    {
        string key = Key(mod);
        Offsets.TryGetValue(key, out float offset);
        Offsets[key] = OpticAdjustmentMath.Next(offset, sign, Step(mod));
    }

    private static bool Adjust(ProceduralWeaponAnimation animation, float sign)
    {
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || player.ProceduralWeaponAnimation != animation || !TryTarget(out SightComponent? mod))
            return true;
        if (UnityEngine.Input.GetKey(KeyCode.PageUp) || UnityEngine.Input.GetKey(KeyCode.PageDown)) UpdateHeld();
        else Change(mod!, sign);
        return false;
    }

    private static string Key(SightComponent mod) => mod.Item.Id + ":" + mod.SelectedScope;

    internal static void UpdateHeld()
    {
        if (!NewCameraSwitch.Enabled) LegacyReticle.Release();
        if (_inputFrame == Time.frameCount) return;
        _inputFrame = Time.frameCount;
        int sign = (UnityEngine.Input.GetKey(KeyCode.PageUp) ? 1 : 0) - (UnityEngine.Input.GetKey(KeyCode.PageDown) ? 1 : 0);
        if (sign == 0 || !TryTarget(out SightComponent? mod))
        {
            _heldSign = 0;
            _heldKey = "";
            return;
        }
        string key = Key(mod!);
        float now = Time.unscaledTime;
        if (_heldSign != sign || _heldKey != key)
        {
            _heldSign = sign;
            _heldKey = key;
            Change(mod!, sign);
            _repeatAt = now + 0.15f;
        }
        else if (now >= _repeatAt)
        {
            int steps = 1 + Mathf.FloorToInt((now - _repeatAt) * RepeatPerSecond);
            _repeatAt += steps / RepeatPerSecond;
            Change(mod!, sign * steps);
        }
    }

    private static bool Eligible(ProceduralWeaponAnimation.SightNBone scope, OpticSight? current)
    {
        return scope.Mod != null && (Supported.Contains(scope.Mod.Item.TemplateId)
            || ReticleMoving.Contains(scope.Mod.Item.TemplateId))
            && scope.Mod.SelectedScope == 0
            && (scope.IsOptic || OpticPicture.ResolveForElevation(scope) != null);
    }

    private static bool TryTarget(out SightComponent? mod)
    {
        mod = null;
        if (!NewCameraSwitch.Enabled) return false;
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer || player.PointOfView == EPointOfView.FreeCamera
            || player.HandsController == null || !player.HandsController.IsAiming) return false;
        ProceduralWeaponAnimation? animation = player.ProceduralWeaponAnimation;
        OpticSight? current = CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
        if (Scopes.Current(animation) is { } aimed && Eligible(aimed, current))
        {
            mod = aimed.Mod;
            return true;
        }
        // The Ultima screen is rendered separately from its iron-sight aiming channel.
        if (animation?.ScopeAimTransforms != null)
            foreach (var scope in animation.ScopeAimTransforms)
                if (scope.Mod?.Item.TemplateId == "606f2696f2cb2e02a42aceb1" && Eligible(scope, current))
                {
                    mod = scope.Mod;
                    return true;
                }
        return false;
    }

    internal static void Apply(Camera camera)
    {
        if (!OpticPicture.IsCamera(camera)) return;
        // Multiple pre-cull calls must never accumulate the rotation.
        Release();
        bool eligible = TryTarget(out SightComponent? mod);
        bool legacy = eligible && ReticleMoving.Contains(mod!.Item.TemplateId)
            && CameraManager.Instance?.OpticCameraManager?.OpticRetrice?.CanUpdate != true;
        if (!legacy) LegacyReticle.Release();
        float offset = 0f;
        if (eligible) Offsets.TryGetValue(Key(mod!), out offset);
        if (eligible)
        {
            string key = Key(mod!);
            Displayed.TryGetValue(key, out float shown);
            if (_smoothFrame != Time.frameCount)
            {
                float blend = 1f - Mathf.Exp(-Time.unscaledDeltaTime / SmoothSeconds);
                shown = Mathf.Lerp(shown, offset, blend);
                if (Mathf.Abs(shown - offset) < 0.00001f) shown = offset;
                Displayed[key] = shown;
                _smoothFrame = Time.frameCount;
            }
            offset = shown;
        }
        if (legacy)
            LegacyReticle.Apply(CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight, camera, offset);
        if (!eligible || Mathf.Abs(offset) < 0.000001f) return;
        _camera = camera;
        _projection = camera.projectionMatrix;
        _nonJittered = camera.nonJitteredProjectionMatrix;
        _reticleProjection = _projection;
        _reticleNonJittered = _nonJittered;
        // Off-axis projection translates the scene vertically without rotating any geometry.
        Matrix4x4 shifted = _projection;
        shifted.m12 += Mathf.Tan(offset / 1000f) * shifted.m11;
        Matrix4x4 steady = _nonJittered;
        steady.m12 += Mathf.Tan(offset / 1000f) * steady.m11;
        if (ReticleMoving.Contains(mod!.Item.TemplateId))
        {
            // Shift only the reticle's clip-space projection. Never rotate/translate its mesh.
            _reticleProjection = shifted;
            _reticleNonJittered = steady;
        }
        else
        {
            camera.projectionMatrix = shifted;
            camera.nonJitteredProjectionMatrix = steady;
        }
    }

    private static void ReticlePostfix(OpticRetrice __instance)
    {
        Camera camera = __instance.GetComponent<Camera>();
        if (!OpticPicture.IsCamera(camera)) return;
        // Ensure the skeleton pose and projection are ready regardless of callback order.
        NewCameraSwitch.OnCameraPreCull(camera);
        if (_camera != camera || !__instance.CanUpdate || __instance.Renderer == null) return;
        CommandBuffer command = ReticleBuffer(__instance);
        Material material = ReticleMaterial(__instance);
        if (command == null || material == null) return;
        command.Clear();
        command.SetGlobalFloat(Shader.PropertyToID("_Scale"), ReticleScale(__instance));
        command.SetGlobalMatrix(Shader.PropertyToID("_NonJitteredProj"), GL.GetGPUProjectionMatrix(_reticleNonJittered, true));
        command.SetViewProjectionMatrices(camera.worldToCameraMatrix, _reticleProjection);
        command.DrawRenderer(__instance.Renderer, material, 0, 0);
        command.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        command.SetGlobalMatrix(Shader.PropertyToID("_NonJitteredProj"), GL.GetGPUProjectionMatrix(camera.nonJitteredProjectionMatrix, true));
    }

    internal static void Restore(Camera camera)
    {
        if (_camera == camera) Release();
    }

    // The optic camera's projection back to following its field of view: written back as a matrix it would stay at that
    // field of view for good (a variable scope's zoom no longer changing the picture). The game never sets it itself.
    internal static void Release()
    {
        if (_camera != null)
            _camera.ResetProjectionMatrix();
        _camera = null;
    }

    internal static void Shutdown()
    {
        Release();
        LegacyReticle.Release();
    }
}
