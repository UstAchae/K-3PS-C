using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// TEMPORARY (1.9.34, to be taken out once the cause is known; the user's go-ahead, 2026-11-27): why BRNVG's automatic
// gain shows with this mod switched off and not in our eyepieces. After every call of its RenderEffect, ours through
// the open lens or its own, about once a second and at most ProbeLines times: what its gain stands at (its exposure
// texture, read back without waiting) and the state it was worked out from. Nothing of BRNVG's is changed.
internal static class BrnvgGainProbe
{
    private const int ProbeLines = 90, ProbeEvery = 45;   // (lines for each of the two ways)
    private static int _linesOpen, _linesOwn, _frame = -1000, _calls, _callFrame = -1, _callsThisFrame;
    private static bool _installed, _pending;
    internal static bool OpenNow;   // our call, with the lens open (NightVisionEyepieces.OpenLens)
    private static FieldInfo? _read, _valid, _auto, _min, _max, _target, _bright, _dark, _protect, _enabled, _lenses, _previous, _signed, _manual, _manualOn;

    internal static void Install(Type renderer)
    {
        if (_installed)
            return;
        _installed = true;
        const BindingFlags all = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        FieldInfo? F(string name) => renderer.GetField(name, all);
        _read = F("exposureRead"); _valid = F("exposureHistoryValid"); _auto = F("autoExposure"); _min = F("minimumExposureEV");
        _max = F("maximumExposureEV"); _target = F("targetLuminance"); _bright = F("brightSceneSpeed"); _dark = F("darkSceneSpeed");
        _protect = F("highlightProtection"); _enabled = F("nightVisionEnabled"); _lenses = F("lensDefinitions");
        _previous = F("previousRenderTime"); _signed = F("supportsSignedIntermediates"); _manual = F("manualExposureEV"); _manualOn = F("manualGainControl");
        MethodInfo? effect = AccessTools.Method(renderer, "RenderEffect");
        MethodInfo? postfix = typeof(BrnvgGainProbe).GetMethod(nameof(Postfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (effect != null && postfix != null)
            new Harmony(NewCameraPlugin.PluginGuid + ".brnvg.gainprobe").Patch(effect, postfix: new HarmonyMethod(postfix));
        NewCameraPlugin.Log?.LogInfo($"BRNVG gain probe: on {renderer.FullName} (RenderEffect {(effect != null ? "found" : "NOT found")}; fields missing: {Missing()}).");
    }

    private static string Missing()
    {
        string s = "";
        void M(FieldInfo? f, string n) { if (f == null) s += n + " "; }
        M(_read, "exposureRead"); M(_valid, "exposureHistoryValid"); M(_auto, "autoExposure"); M(_min, "minimumExposureEV"); M(_max, "maximumExposureEV");
        M(_target, "targetLuminance"); M(_bright, "brightSceneSpeed"); M(_dark, "darkSceneSpeed"); M(_protect, "highlightProtection");
        M(_enabled, "nightVisionEnabled"); M(_lenses, "lensDefinitions"); M(_previous, "previousRenderTime"); M(_signed, "supportsSignedIntermediates");
        return s.Length > 0 ? s : "none";
    }

    private static void Postfix(object __instance, RenderTexture source)
    {
        try
        {
            _calls++;
            if (_callFrame != Time.frameCount)
            {
                _callFrame = Time.frameCount;
                _callsThisFrame = 0;
            }
            _callsThisFrame++;
            if ((OpenNow ? _linesOpen : _linesOwn) >= ProbeLines || _pending || Time.frameCount - _frame < ProbeEvery)
                return;
            if (_read?.GetValue(__instance) is not RenderTexture[] textures || textures.Length == 0 || textures[0] == null || !textures[0].IsCreated())
            {
                _frame = Time.frameCount;
                if (OpenNow) _linesOpen++; else _linesOwn++;
                NewCameraPlugin.Log?.LogInfo($"BRNVG gain probe: no exposure texture yet ({(OpenNow ? "our open lens" : "its own")}; night {_enabled?.GetValue(__instance)}, auto {_auto?.GetValue(__instance)}, signed {_signed?.GetValue(__instance)}).");
                return;
            }
            _frame = Time.frameCount;
            if (OpenNow) _linesOpen++; else _linesOwn++;
            _pending = true;
            string lenses = "";
            if (_lenses?.GetValue(__instance) is Array defs)
            {
                foreach (object def in defs)
                {
                    Type t = def.GetType();
                    lenses += $"[{t.GetField("enabled")?.GetValue(def)} r {t.GetField("radiusInTextureHeights")?.GetValue(def):F2} at {t.GetField("centerUV")?.GetValue(def)}] ";
                }
            }
            string head = $"{(OpenNow ? "OUR open lens" : "ITS OWN")} frame {Time.frameCount} ({_callsThisFrame} call(s) this frame, {_calls} in all), source {source.width}x{source.height} {source.format}; "
                + $"night {_enabled?.GetValue(__instance)}, auto {_auto?.GetValue(__instance)}, history valid {_valid?.GetValue(__instance)}, signed {_signed?.GetValue(__instance)}, "
                + $"EV {_min?.GetValue(__instance)}..{_max?.GetValue(__instance)}, manual {_manualOn?.GetValue(__instance)} {_manual?.GetValue(__instance)}, target {_target?.GetValue(__instance)}, "
                + $"speeds {_bright?.GetValue(__instance)}/{_dark?.GetValue(__instance)}, protection {_protect?.GetValue(__instance)}, last render time {_previous?.GetValue(__instance)} (now {Time.unscaledTime:F3}); lenses {lenses}";
            AsyncGPUReadback.Request(textures[0], 0, TextureFormat.RGBAFloat, request =>
            {
                _pending = false;
                if (request.hasError)
                {
                    NewCameraPlugin.Log?.LogInfo("BRNVG gain probe: read failed; " + head);
                    return;
                }
                Color c = request.GetData<Color>()[0];
                NewCameraPlugin.Log?.LogInfo($"BRNVG gain probe: exposure texture 0 = ({c.r:F4}, {c.g:F4}, {c.b:F4}, {c.a:F4}); {head}");
            });
        }
        catch (Exception e)
        {
            _linesOpen = _linesOwn = ProbeLines;
            NewCameraPlugin.Log?.LogWarning("BRNVG gain probe stopped: " + e.Message);
        }
    }
}
