using System.IO;
using UnityEngine;

namespace EFTNewCamera;

// What the T-7's eyepieces cost against the game's own T-7, and what their picture is against the game's, measured in
// one stand (the user's, 2026-11-25: 64 frames a second ours, 110 the game's; "T-7 的画面比原版的 T-7 还是有噪点";
// "先测量"). Nothing is changed for good. The first time thermal goggles are down, on and settled (in a raid, not
// before it has run RaidSettleSeconds), each way below is held for ThermalProbeWarm + ThermalProbeCount frames, the
// median frame time of the last ThermalProbeCount written to the log, twice through; on the first round some of them
// also save what is seen. A frame's time is the whole frame's, so what a part costs is the difference of two lines.
//   1 as it is                                     saved: the screen, our camera's picture
//   2 our camera's picture at half its width and height
//   3 our camera's picture not drawn anew (the last one kept)
//   4 without the hold over frames and its jitter  saved: our camera's picture
//   5 and without the image effects copied from the main camera (bloom, sharpening)   saved: our camera's picture
//   6 no eyepieces: the game's own thermal picture over the whole frame   saved: the screen
// (1 less 3: our camera's whole render. 1 less 2: what of it goes with the picture's size. 3 against 6: what is left
// of ours, the main camera's empty frame and the eyepieces, against the game drawing the scene once.)
// Saved to BepInEx/k3psc-thermal-probe: the screen as PNG; our picture as PNG and as raw 32-bit floats (RGBA, rows
// from the bottom, after 8 bytes of width and height).
internal static partial class NightVisionEyepieces
{
    private static readonly string[] ThermalProbeSteps =
    {
        "as it is", "our picture at half size", "our picture not drawn anew", "without the hold over frames",
        "and without the copied image effects", "no eyepieces: the game's own thermal picture",
    };
    private const int ThermalProbeSettle = 180, ThermalProbeWarm = 40, ThermalProbeCount = 150, ThermalProbeRounds = 2;
    private const float ThermalRaidSettleSeconds = 60f;
    private static int _thermalProbeStep = -1, _thermalProbeFrames, _thermalProbeSettled, _thermalProbeRound;
    private static float _thermalProbeSeconds, _thermalRaidSince = -1f;
    private static readonly float[] _thermalProbeTimes = new float[ThermalProbeCount];
    private static bool _thermalProbeDone;
    private static readonly System.Collections.Generic.List<MonoBehaviour> _probeEffectsOff = new System.Collections.Generic.List<MonoBehaviour>();

    // Read by RenderThermal, PrepareThermalHold, JitterThermal, OnCameraPreRender and Update's "worn".
    internal static bool ProbeHalfPicture { get; private set; }
    internal static bool ProbeKeepPicture { get; private set; }
    internal static bool ProbeNoHold { get; private set; }
    internal static bool ProbeNoEyepieces { get; private set; }

    private static string ThermalProbeFolder => Path.Combine(BepInEx.Paths.BepInExRootPath, "k3psc-thermal-probe");

    // Once a frame, as the eyepieces' state for the frame is known.
    private static void ThermalProbeTick(bool downAndOn, bool inRaid)
    {
        if (_thermalProbeDone)
            return;
        if (!inRaid)
            _thermalRaidSince = -1f;
        else if (_thermalRaidSince < 0f)
            _thermalRaidSince = Time.unscaledTime;
        // (At the last step the eyepieces are off by our own doing: the goggles are still down.)
        bool there = (!inRaid || Time.unscaledTime - _thermalRaidSince >= ThermalRaidSettleSeconds) && _body != null && _device.Thermal
            && (ProbeNoEyepieces ? downAndOn : Active && _flip >= 0.999f && _thermalPicture != null);
        if (!there)
        {
            if (_thermalProbeStep >= 0)
            {
                ThermalProbeRestore();
                NewCameraPlugin.Log?.LogInfo("Thermal probe: broken off (the goggles went up or off), all put back; it starts again the next time they are down.");
            }
            _thermalProbeStep = -1;
            _thermalProbeSettled = 0;
            _thermalProbeRound = 0;
            return;
        }
        if (_thermalProbeStep < 0)
        {
            if (++_thermalProbeSettled < ThermalProbeSettle)
                return;
            _thermalProbeStep = 0;
            _thermalProbeFrames = 0;
            _thermalProbeSeconds = 0f;
            Directory.CreateDirectory(ThermalProbeFolder);
            NewCameraPlugin.Log?.LogInfo($"Thermal probe: measuring {_device.Node} {(inRaid ? "in a raid" : "in the hideout")} at {Screen.width}x{Screen.height}, our picture {_thermalPicture!.width}x{_thermalPicture.height} {_thermalPicture.format}; {ThermalProbeSteps.Length} ways, {ThermalProbeRounds} times through, about {ThermalProbeSteps.Length * ThermalProbeRounds * (ThermalProbeWarm + ThermalProbeCount)} frames. Saved to {ThermalProbeFolder}.");
        }
        _thermalProbeFrames++;
        if (_thermalProbeFrames > ThermalProbeWarm)
        {
            _thermalProbeTimes[_thermalProbeFrames - ThermalProbeWarm - 1] = Time.unscaledDeltaTime;
            _thermalProbeSeconds += Time.unscaledDeltaTime;
        }
        if (_thermalProbeFrames < ThermalProbeWarm + ThermalProbeCount)
            return;
        System.Array.Sort(_thermalProbeTimes);
        float median = _thermalProbeTimes[ThermalProbeCount / 2];
        NewCameraPlugin.Log?.LogInfo($"Thermal probe round {_thermalProbeRound + 1}, {_thermalProbeStep + 1}/{ThermalProbeSteps.Length} {ThermalProbeSteps[_thermalProbeStep]}: median {median * 1000f:F2} ms a frame ({1f / median:F0} a second; mean {_thermalProbeSeconds / ThermalProbeCount * 1000f:F2}, slowest {_thermalProbeTimes[ThermalProbeCount - 1] * 1000f:F2}).");
        if (_thermalProbeRound == 0)
            ThermalProbeSave(_thermalProbeStep);
        _thermalProbeStep++;
        _thermalProbeFrames = 0;
        _thermalProbeSeconds = 0f;
        if (_thermalProbeStep >= ThermalProbeSteps.Length && _thermalProbeRound + 1 < ThermalProbeRounds)
        {
            _thermalProbeRound++;
            _thermalProbeStep = 0;
        }
        if (_thermalProbeStep >= ThermalProbeSteps.Length)
        {
            ThermalProbeRestore();
            _thermalProbeDone = true;
            NewCameraPlugin.Log?.LogInfo("Thermal probe: done, all put back as it was.");
            return;
        }
        ThermalProbeSet(_thermalProbeStep);
    }

    private static void ThermalProbeSet(int step)
    {
        ProbeHalfPicture = step == 1;
        ProbeKeepPicture = step == 2;
        ProbeNoHold = step == 3 || step == 4;
        ProbeNoEyepieces = step == 5;
        ProbeEffects(step != 4);
    }

    private static void ThermalProbeRestore()
    {
        ProbeHalfPicture = ProbeKeepPicture = ProbeNoHold = ProbeNoEyepieces = false;
        ProbeEffects(true);
    }

    // The image effects copied onto our camera (CopyMainEffects), off for a step and back as they were.
    private static void ProbeEffects(bool on)
    {
        if (on)
        {
            foreach (MonoBehaviour effect in _probeEffectsOff)
            {
                if (effect != null)
                    effect.enabled = true;
            }
            _probeEffectsOff.Clear();
            return;
        }
        if (_thermalCamera == null || _probeEffectsOff.Count > 0)
            return;
        foreach (MonoBehaviour effect in _thermalCamera.GetComponents<MonoBehaviour>())
        {
            if (effect == null || !effect.enabled || System.Array.IndexOf(MainEffects, effect.GetType().Name) < 0)
                continue;
            effect.enabled = false;
            _probeEffectsOff.Add(effect);
        }
        NewCameraPlugin.Log?.LogInfo($"Thermal probe: {_probeEffectsOff.Count} copied image effect(s) off for this step.");
    }

    // What is seen at the end of a step of the first round.
    private static void ThermalProbeSave(int step)
    {
        try
        {
            string name = (step + 1) + "_" + ThermalProbeSteps[step].Replace(' ', '_').Replace(":", "").Replace("'", "");
            if (step == 0 || step == 5)
                ScreenCapture.CaptureScreenshot(Path.Combine(ThermalProbeFolder, name + "_screen.png"));
            if ((step == 0 || step == 3 || step == 4) && _thermalPicture != null)
            {
                RenderTexture picture = _thermalPicture, was = RenderTexture.active;
                var read = new Texture2D(picture.width, picture.height, TextureFormat.RGBAFloat, false, true);
                RenderTexture.active = picture;
                read.ReadPixels(new Rect(0f, 0f, picture.width, picture.height), 0, 0, false);
                read.Apply(false);
                RenderTexture.active = was;
                byte[] raw = read.GetRawTextureData();
                using (var file = new FileStream(Path.Combine(ThermalProbeFolder, name + "_picture.bin"), FileMode.Create))
                {
                    file.Write(System.BitConverter.GetBytes(picture.width), 0, 4);
                    file.Write(System.BitConverter.GetBytes(picture.height), 0, 4);
                    file.Write(raw, 0, raw.Length);
                }
                File.WriteAllBytes(Path.Combine(ThermalProbeFolder, name + "_picture.png"), read.EncodeToPNG());
                Object.Destroy(read);
            }
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogWarning("Thermal probe: saving failed: " + e.Message);
        }
    }
}
