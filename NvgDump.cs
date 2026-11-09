using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// Temporary, never shipped: switches in F12 to turn one thing off at a time and watch the device down.
internal static class NvgDump
{
    private const string Section = "16 夜视仪直接绘制（测试）";
    private const int RecheckFrames = 30;

    private static ConfigEntry<bool>? _near, _traceSix, _area, _post, _hbao, _sun, _game, _noEmission, _noFace;
    private static ConfigEntry<bool>? _upToo, _byRenderer;
    private static Camera? _on;
    private static PostProcessLayer? _layer;
    private static Behaviour? _ambientOcclusion;
    private static bool _layerOff, _ambientOff, _sunOff;
    private static Light? _sunLight;
    private static LightShadows _sunShadows;
    private static int _nextCheck;
    private static readonly List<CommandBuffer> _traceTaken = new List<CommandBuffer>();
    private static readonly List<CommandBuffer> _areaTaken = new List<CommandBuffer>();

    internal static bool NoEmission => _noEmission != null && _noEmission.Value;
    internal static bool UpToo => _upToo != null && _upToo.Value;
    internal static bool NoFaceLight => _noFace != null && _noFace.Value;
    internal static bool GameDraws => _game != null && _game.Value;
    internal static bool UseRenderer => _byRenderer != null && _byRenderer.Value;
    internal static float TestNear => _near != null && _near.Value ? 0.005f : 0f;

    internal static void Install(ConfigFile config)
    {
        ConfigEntry<bool> Switch(string name, string text)
        {
            ConfigEntry<bool> entry = config.Bind(Section, name, false, text);
            entry.Value = false;
            return entry;
        }
        _near = Switch("2 近裁剪面调到 0.005 米", "开：外壳的深度不再被压到近裁剪面。远处的阴影和涂鸦可能会抖。");
        _traceSix = Switch("3 关 TraceSix 光照", "开：拿掉 TraceSix 加在画面上的光。");
        _area = Switch("4 关面光源", "开：拿掉游戏的彩色面光源（车灯、灯管）。");
        _post = Switch("5 关后处理（含空间反射）", "开：关掉后处理，空间反射也在里面。");
        _hbao = Switch("6 关 HBAO", "开：关掉环境光遮蔽。");
        _sun = Switch("7 关太阳阴影", "开：太阳不再投影。");
        _game = Switch("8 游戏自己画外壳", "开：不画合并的外壳，翻下时看到的是游戏原样画的，会被近裁剪面切开。用来对比。");
        _noEmission = Switch("9 外壳去掉材质自带的反射", "开：外壳不要它自己算进画面的反射和环境光。");
        _upToo = Switch("10 翻上时也由我们画", "开：夜视仪翻上时也用合并的画法（翻上时没有平移，也不会贴到近裁剪面）。站着别动，来回开关它和开关 8，比两种画法同一姿势下的差别。");
        _noFace = Switch("11 关目镜照脸的灯", "开：关掉每个镜片往脸上打的那盏绿灯。");
        _byRenderer = Switch("12 外壳用 DrawRenderer 画", "开：我们画外壳时由渲染器自己画（DrawRenderer）；关：取出网格来画（DrawMesh）。开着开关 10 站着不动来回切，看反光和颜色。大地图远处可能会抖。");
    }

    // As the main camera culls.
    internal static void PreCull(Camera camera)
    {
        if (camera != Camera.main || _post == null)
            return;
        if (_on != camera)
        {
            // A raid's camera is made anew, and what was taken off the last went with it.
            _on = camera;
            _layer = camera.GetComponent<PostProcessLayer>();
            _ambientOcclusion = camera.GetComponent("HBAO") as Behaviour;
            _layerOff = _ambientOff = false;
            _traceTaken.Clear();
            _areaTaken.Clear();
            _nextCheck = 0;
        }
        Keep(_layer, _post.Value, ref _layerOff);
        Keep(_ambientOcclusion, _hbao!.Value, ref _ambientOff);
        // Lights come and go, and the sky sets the sun again: looked at now and then, not every frame.
        if (Time.frameCount < _nextCheck)
            return;
        _nextCheck = Time.frameCount + RecheckFrames;
        Take(camera, CameraEvent.AfterLighting, _traceSix!.Value, _traceTaken, b => b.name.StartsWith("TraceSix", System.StringComparison.Ordinal));
        Take(camera, CameraEvent.AfterImageEffectsOpaque, _area!.Value, _areaTaken, b => !b.name.StartsWith("Justachae", System.StringComparison.Ordinal));
        KeepSun(_sun!.Value);
    }

    private static void Keep(Behaviour? behaviour, bool off, ref bool taken)
    {
        if (behaviour == null)
            return;
        if (off)
        {
            behaviour.enabled = false;
            taken = true;
        }
        else if (taken)
        {
            behaviour.enabled = true;
            taken = false;
        }
    }

    private static void Take(Camera camera, CameraEvent at, bool off, List<CommandBuffer> taken, System.Predicate<CommandBuffer> which)
    {
        if (off)
        {
            foreach (CommandBuffer b in camera.GetCommandBuffers(at))
            {
                if (!which(b))
                    continue;
                camera.RemoveCommandBuffer(at, b);
                taken.Add(b);
            }
            return;
        }
        if (taken.Count == 0)
            return;
        foreach (CommandBuffer b in taken)
            camera.AddCommandBuffer(at, b);
        taken.Clear();
    }

    private static void KeepSun(bool off)
    {
        if (off)
        {
            foreach (Light light in Light.GetLights(LightType.Directional, 0))
            {
                if (light.shadows == LightShadows.None)
                    break;
                _sunLight = light;
                _sunShadows = light.shadows;
                _sunOff = true;
                light.shadows = LightShadows.None;
                break;
            }
        }
        else if (_sunOff)
        {
            if (_sunLight != null)
                _sunLight.shadows = _sunShadows;
            _sunLight = null;
            _sunOff = false;
        }
    }
}
