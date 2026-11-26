using UnityEngine;

namespace EFTNewCamera;

// The tubes' own turning down of their gain in bright light, for the game's night-vision picture in the eyepieces (the
// user's, 2026-11-26, on players finding the goggles blinding before a strong light: "最好让原版画面和 BRNVG 都能有自动
// 门控"; BRNVG's picture has its own automatic gain and is not touched here; a fusion device's own mod turns its tubes down by its keys through
// EyepieceFusion, laid on after this gain).
// A tube's screen has a brightest it can be, and the tube holds its picture under it: with much light on it, it
// turns its gain down for the whole picture, and up again as the light goes. An auto-gated tube (the GPNVG-18's, the
// PVS-14's, the PVS-31A's) does it at once; one without (the PNV-10T's, the N-15's) over a moment, and comes back
// more slowly; the PNV-57E's not at all (Device.AutoGain).
// Each frame, all on the graphics card and nothing read back: the light that reaches the tubes is measured over what
// the lenses show (NvgTube.shader pass 11, into a small target whose smallest mip is the mean), and the gain, kept in
// a target of one pixel, goes toward what brings that light down to AutoGainTarget (pass 12); the tube's pass reads
// it (pass 0). Only ever down from 1: where it is dark the picture is as without this.
internal static partial class NightVisionEyepieces
{
    internal static bool AutoGainOn = true;

    // The frame's light at this stage is in display range (after the tone mapping); the lenses then show it times
    // 原版夜视目镜亮度 (2.5 as the user has it), so 0.4 of it is already white on the screen. Held to a mean of this.
    // (0.12 by the mean and the root mean square, 1.9.29: nothing to be seen of it, the user's, 2026-11-26.)
    private const float AutoGainTarget = 0.10f;
    private const float AutoGainLeast = 1f / 16f;
    // A second, going down and coming back: at once and in half a second or so; over a third of a second and in a second or more.
    private const float GatedDown = 30f, GatedBack = 2f, UngatedDown = 6f, UngatedBack = 1f;
    private const int MeterPass = 11, GainPass = 12, MeterWidth = 512, MeterHeight = 288;

    private static readonly int AutoGainId = Shader.PropertyToID("_AutoGain"), AutoGainUseId = Shader.PropertyToID("_AutoGainUse");
    private static readonly int MeterId = Shader.PropertyToID("_Meter"), GainPrevId = Shader.PropertyToID("_GainPrev");
    private static readonly int GainSetId = Shader.PropertyToID("_GainSet"), GainDtId = Shader.PropertyToID("_GainDt");
    private static readonly int MeterLensLId = Shader.PropertyToID("_MeterLensL"), MeterLensRId = Shader.PropertyToID("_MeterLensR");
    private static readonly int MeterSplitId = Shader.PropertyToID("_MeterSplit");

    private static RenderTexture? _meter;
    private static readonly RenderTexture?[] _gains = new RenderTexture?[2];
    private static int _gainNow, _gainFrame = -10;
    private static string? _autoGainNoted, _gainSeenFor;
    private static float _gainLeast = 1f;
    private static int _gainLooks, _gainLookFrame = -1000;
    private static bool _gainLooking;
    private static readonly System.Action<UnityEngine.Rendering.AsyncGPUReadbackRequest> GainRead = request =>
    {
        _gainLooking = false;
        if (request.hasError || _gainLooks < 0)
            return;
        float now = request.GetData<float>()[0];
        _gainLeast = Mathf.Min(_gainLeast, now);
        _gainLooks++;
        // (The first time it is under 0.8, under 0.5 and under 0.25, and how low it has been after 20 looks and at the
        // last, the 400th, some ten minutes of the goggle down: a handful of lines a device. 1.9.31 looked for half a
        // minute only, and said 0.96 of a goggle whose wearer may not have faced a light in that time.)
        if (_gainStep < GainSteps.Length && now < GainSteps[_gainStep])
        {
            NewCameraPlugin.Log?.LogInfo($"NVG tubes: {_gainSeenFor}'s gain down to {now:F2} (first under {GainSteps[_gainStep]:F2}, at look {_gainLooks}).");
            while (_gainStep < GainSteps.Length && now < GainSteps[_gainStep])
                _gainStep++;
        }
        if (_gainLooks == 20 || _gainLooks >= 400)
            NewCameraPlugin.Log?.LogInfo($"NVG tubes: {_gainSeenFor}'s gain {_gainLeast:F2} at the least in its first {_gainLooks} looks ({now:F2} now).");
        if (_gainLooks >= 400 || _gainStep >= GainSteps.Length)
            _gainLooks = -1;
    };
    private static readonly float[] GainSteps = { 0.8f, 0.5f, 0.25f };
    private static int _gainStep;

    // (A fusion device's too, 1.9.32: the ENVG-B's and the F-PANO's clients turn their tubes down only by their keys
    // while these eyepieces show them, their own automatic brightness being for when they do not; agreed with their
    // session, 2026-11-26. Their keys' gain is laid on after this one, TubesGain. The user judged the gain on an
    // F-PANO and saw nothing of it: it was not at work there.)
    private static bool AutoGainWanted => AutoGainOn && _tubeShader != null && _device.AutoGain > 0
        && _lensMaps[0] != null && _lensMaps[1] != null;

    // This frame's gain (a texture of one pixel), worked out from the light that reaches the tubes (frame).
    private static Texture AutoGainNow(RenderTexture frame, Material tube)
    {
        if (_meter == null)
        {
            _meter = new RenderTexture(MeterWidth, MeterHeight, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                name = "Justachae NVG light on the tubes", useMipMap = true, autoGenerateMips = true, filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
            };
            _meter.Create();
        }
        for (int i = 0; i < 2; i++)
        {
            if (_gains[i] != null)
                continue;
            _gains[i] = new RenderTexture(1, 1, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
            {
                name = "Justachae NVG tube gain " + i, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave,
            };
            _gains[i]!.Create();
            _gainFrame = -10;
        }
        int read = _gainNow, write = 1 - read;
        // Not the frame after the last one (the goggle was up or off meanwhile, or another one is worn): from full gain.
        if (_gainFrame != Time.frameCount - 1 || !_gains[read]!.IsCreated())
        {
            RenderTexture was = RenderTexture.active;
            RenderTexture.active = _gains[read];
            GL.Clear(false, true, Color.white);
            RenderTexture.active = was;
        }
        // (Twice in one frame, a second camera: the gain already worked out stands.)
        if (_gainFrame == Time.frameCount)
            return _gains[read]!;
        _gainFrame = Time.frameCount;
        bool gated = _device.AutoGain >= 2;
        tube.SetTexture(MeterLensLId, _lensMaps[0]);
        tube.SetTexture(MeterLensRId, _lensMaps[1]);
        tube.SetFloat(MeterSplitId, _binocularMaterial != null ? _binocularMaterial.GetFloat(SplitId) : 0.5f);
        Graphics.Blit(frame, _meter, tube, MeterPass);
        tube.SetTexture(MeterId, _meter);
        tube.SetTexture(GainPrevId, _gains[read]);
        tube.SetVector(GainSetId, new Vector4(AutoGainTarget, AutoGainLeast, gated ? GatedDown : UngatedDown, gated ? GatedBack : UngatedBack));
        tube.SetFloat(GainDtId, Mathf.Min(Time.unscaledDeltaTime, 0.1f));
        Graphics.Blit(Texture2D.blackTexture, _gains[write], tube, GainPass);
        _gainNow = write;
        // Once for a device: how far down the gain has gone, read back from the card now and then (not waited for) until
        // it has been under 0.8 or half a minute of looking has passed.
        if (_gainSeenFor != _device.Node)
        {
            _gainSeenFor = _device.Node;
            _gainLeast = 1f;
            _gainLooks = 0;
            _gainStep = 0;
            _gainLooking = false;
        }
        if (_gainLooks >= 0 && Time.frameCount - _gainLookFrame >= 90 && !_gainLooking)
        {
            _gainLookFrame = Time.frameCount;
            _gainLooking = true;
            UnityEngine.Rendering.AsyncGPUReadback.Request(_gains[write], 0, GainRead);
        }
        if (_autoGainNoted != _device.Node)
        {
            _autoGainNoted = _device.Node;
            NewCameraPlugin.Log?.LogInfo($"NVG tubes: {_device.Node}'s gain turned down in bright light ({(gated ? "at once, auto-gated" : "over a moment, not gated")}): the light on the tubes held to a mean of {AutoGainTarget:F2}, the gain never under {AutoGainLeast:F3}.");
        }
        return _gains[write]!;
    }
}
