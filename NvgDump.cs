using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// Temporary, never shipped: one capture answers why the device in the main camera does not look as the game's. As the device
// starts to come down and again once it is down: the same view drawn each way and with one thing left out at a time. Each way is
// held for two frames, so what was changed before the camera culled has come into effect.
internal static class NvgDump
{
    private static readonly string[] Starting = { "mesh", "game", "renderer" };
    private static readonly string[] Down = { "mesh", "renderer", "no_tracesix", "no_ambient", "no_hbao", "no_postprocess", "no_sun_shadow", "game" };

    private static ConfigEntry<bool>? _switch;
    private static string[]? _running;
    private static string _stage = "", _folder = "";
    private static int _index, _frame, _downFrames;
    private static bool _startingDone, _downDone;
    private static Camera? _on;
    private static readonly List<(string name, RenderTexture rt)> _shots = new List<(string, RenderTexture)>();
    private static readonly List<(CameraEvent at, CommandBuffer buffer)> _hung = new List<(CameraEvent, CommandBuffer)>();
    private static readonly List<(CameraEvent at, CommandBuffer[] all)> _stripped = new List<(CameraEvent, CommandBuffer[])>();
    private static readonly List<Behaviour> _disabled = new List<Behaviour>();
    private static Light? _sun;
    private static LightShadows _sunShadows;

    internal static bool GameDraws;
    internal static bool UseRenderer;
    private static bool Shooting => _running != null && _frame == 1;

    internal static void Install(ConfigFile config)
    {
        _switch = config.Bind("16 夜视仪直接绘制（测试）", "抓一帧", false, "夜视仪翻上时勾上，再翻下，等 2 秒。开关自己关掉就是抓完了。画面会闪几下、卡一下。");
        _switch.Value = false;
    }

    internal static void PreCull(Camera camera)
    {
        if (camera != Camera.main)
            return;
        if (_running == null)
        {
            if (_switch == null || !_switch.Value)
            {
                _startingDone = _downDone = false;
                return;
            }
            float flip = NightVisionEyepieces.FlipNow;
            _downFrames = flip >= 0.999f ? _downFrames + 1 : 0;
            if (!_startingDone && flip > 0.001f && flip < 0.3f)
                Begin(camera, "starting", Starting);
            else if (!_downDone && _downFrames > 30)
                Begin(camera, "down", Down);
            return;
        }
        _frame++;
        if (_frame == 1)
            HangShots(camera);
        else if (_frame >= 2)
            Next(camera);
    }

    private static void Begin(Camera camera, string stage, string[] ways)
    {
        _on = camera;
        _stage = stage;
        _running = ways;
        _index = 0;
        _frame = 0;
        if (_folder.Length == 0 || !Directory.Exists(_folder))
        {
            _folder = Path.GetFullPath(Path.Combine(BepInEx.Paths.BepInExRootPath, "..", ".codex-work", "nvgdump", System.DateTime.Now.ToString("HHmmss")));
            Directory.CreateDirectory(_folder);
        }
        NightVisionEyepieces.DescribeDevice(camera);
        foreach (MonoBehaviour m in camera.GetComponents<MonoBehaviour>())
        {
            if (m != null && m.enabled)
                NewCameraPlugin.Log?.LogInfo($"NVG dump, on the camera: {m.GetType().FullName}");
        }
        foreach (CameraEvent at in System.Enum.GetValues(typeof(CameraEvent)))
        {
            foreach (CommandBuffer b in camera.GetCommandBuffers(at))
                NewCameraPlugin.Log?.LogInfo($"NVG dump, at {at}: {b.name}");
        }
        Apply(camera, ways[0]);
    }

    private static void Apply(Camera camera, string way)
    {
        GameDraws = way == "game";
        UseRenderer = way == "renderer";
        switch (way)
        {
            case "no_tracesix":
                Strip(camera, CameraEvent.AfterLighting, "TraceSix");
                break;
            case "no_ambient":
                Strip(camera, CameraEvent.AfterLighting, "Custom Ambient");
                break;
            case "no_hbao":
                Disable(camera.GetComponent("HBAO") as Behaviour);
                break;
            case "no_postprocess":
                Disable(camera.GetComponent<PostProcessLayer>());
                break;
            case "no_sun_shadow":
                foreach (Light light in Light.GetLights(LightType.Directional, 0))
                {
                    _sun = light;
                    _sunShadows = light.shadows;
                    light.shadows = LightShadows.None;
                    break;
                }
                break;
        }
    }

    private static void Disable(Behaviour? behaviour)
    {
        if (behaviour == null || !behaviour.enabled)
            return;
        behaviour.enabled = false;
        _disabled.Add(behaviour);
    }

    private static void Strip(Camera camera, CameraEvent at, string name)
    {
        CommandBuffer[] all = camera.GetCommandBuffers(at);
        _stripped.Add((at, all));
        camera.RemoveCommandBuffers(at);
        foreach (CommandBuffer b in all)
        {
            if (!b.name.StartsWith(name, System.StringComparison.Ordinal))
                camera.AddCommandBuffer(at, b);
        }
    }

    private static string Prefix => _stage + "_" + _index + "_" + _running![_index];

    private static RenderTexture Shot(Camera camera, string name)
    {
        var rt = new RenderTexture(Mathf.Max(camera.pixelWidth, 8), Mathf.Max(camera.pixelHeight, 8), 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { name = "NVG dump " + name };
        _shots.Add((name, rt));
        return rt;
    }

    private static void Hang(Camera camera, CameraEvent at, string suffix, BuiltinRenderTextureType from)
    {
        var b = new CommandBuffer { name = "NVG dump " + suffix };
        b.Blit(from, Shot(camera, Prefix + suffix));
        b.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        camera.AddCommandBuffer(at, b);
        _hung.Add((at, b));
    }

    private static void HangShots(Camera camera)
    {
        Hang(camera, CameraEvent.BeforeLighting, "_g0", BuiltinRenderTextureType.GBuffer0);
        Hang(camera, CameraEvent.BeforeLighting, "_g1", BuiltinRenderTextureType.GBuffer1);
        Hang(camera, CameraEvent.BeforeLighting, "_g2", BuiltinRenderTextureType.GBuffer2);
        Hang(camera, CameraEvent.BeforeLighting, "_g3", BuiltinRenderTextureType.CameraTarget);
        Hang(camera, CameraEvent.AfterLighting, "_lit", BuiltinRenderTextureType.CameraTarget);
        // The frame as it goes to the screen, with all the effects.
        ScreenCapture.CaptureScreenshot(Path.Combine(_folder, Prefix + "_screen.png"));
    }

    // Each half's own surface as drawn, before they are put together: the shader's two halves compared with each other.
    internal static void AfterInsert(CommandBuffer buffer, Camera main, int[] left, int[] right)
    {
        if (!Shooting)
            return;
        for (int t = 0; t < 4; t++)
        {
            buffer.Blit(left[t], Shot(main, Prefix + "_left_g" + t));
            buffer.Blit(right[t], Shot(main, Prefix + "_right_g" + t));
        }
    }

    private static void Undo(Camera camera)
    {
        foreach ((CameraEvent at, CommandBuffer buffer) in _hung)
            camera.RemoveCommandBuffer(at, buffer);
        _hung.Clear();
        foreach ((CameraEvent at, CommandBuffer[] all) in _stripped)
        {
            camera.RemoveCommandBuffers(at);
            foreach (CommandBuffer b in all)
                camera.AddCommandBuffer(at, b);
        }
        _stripped.Clear();
        foreach (Behaviour b in _disabled)
        {
            if (b != null)
                b.enabled = true;
        }
        _disabled.Clear();
        if (_sun != null)
        {
            _sun.shadows = _sunShadows;
            _sun = null;
        }
        GameDraws = false;
        UseRenderer = false;
    }

    private static void Next(Camera camera)
    {
        Undo(camera);
        _index++;
        _frame = 0;
        if (_index < _running!.Length)
        {
            Apply(camera, _running[_index]);
            return;
        }
        if (_stage == "starting")
            _startingDone = true;
        else
            _downDone = true;
        _running = null;
        Save();
        NewCameraPlugin.Log?.LogInfo($"NVG dump: {_stage} saved to {_folder}");
        if (_startingDone && _downDone && _switch != null)
        {
            _switch.Value = false;
            NewCameraPlugin.Log?.LogInfo("NVG dump: done");
        }
    }

    private static void Save()
    {
        RenderTexture? active = RenderTexture.active;
        foreach ((string name, RenderTexture rt) in _shots)
        {
            RenderTexture half = RenderTexture.GetTemporary(rt.width / 2, rt.height / 2, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Graphics.Blit(rt, half);
            var read = new Texture2D(half.width, half.height, TextureFormat.RGBAFloat, false, true);
            RenderTexture.active = half;
            read.ReadPixels(new Rect(0, 0, half.width, half.height), 0, 0);
            Color[] raw = read.GetPixels();
            // Surface data as it is, light lifted out of the dark.
            bool data = name.EndsWith("_g0", System.StringComparison.Ordinal) || name.EndsWith("_g1", System.StringComparison.Ordinal) || name.EndsWith("_g2", System.StringComparison.Ordinal);
            var pixels = new Color[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                Color c = raw[i];
                pixels[i] = data ? new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f)
                    : new Color(Mathf.Pow(Mathf.Clamp01(c.r), 0.4f), Mathf.Pow(Mathf.Clamp01(c.g), 0.4f), Mathf.Pow(Mathf.Clamp01(c.b), 0.4f), 1f);
            }
            var png = new Texture2D(half.width, half.height, TextureFormat.RGB24, false, true);
            png.SetPixels(pixels);
            File.WriteAllBytes(Path.Combine(_folder, name + ".png"), png.EncodeToPNG());
            // The surface's smoothness is kept in this layer's alpha.
            if (name.EndsWith("_g1", System.StringComparison.Ordinal))
            {
                for (int i = 0; i < raw.Length; i++)
                    pixels[i] = new Color(raw[i].a, raw[i].a, raw[i].a, 1f);
                png.SetPixels(pixels);
                File.WriteAllBytes(Path.Combine(_folder, name + "_alpha.png"), png.EncodeToPNG());
            }
            Object.Destroy(read);
            Object.Destroy(png);
            RenderTexture.ReleaseTemporary(half);
            rt.Release();
            Object.Destroy(rt);
        }
        RenderTexture.active = active;
        _shots.Clear();
    }
}
