using UnityEngine;

namespace EFTNewCamera;

// What each part of the eyepieces' picture costs, measured (the user's, 2026-11-22: "先用一个普通的夜视仪量一下夜视").
// Nothing of the picture is changed for good: the first time a night-vision goggle (not a thermal one) is down and
// settled, its parts are switched off one after another, each way held for 240 frames and the mean frame time of the
// last 200 written to the log; then all is put back as it was. A frame's time is the whole frame's (the game is bound
// by the graphics card here), so what a part costs is the difference between two lines.
//   1 as it is
//   2 without the objective's blur of what is near (F12 近处虚化)
//   3 and without the lenses' glow over the glass and eyecups
//   4 and without the glass's reflections
//   5 and without the IR layer (IR lights and lasers for the tubes, the night-vision ambient)
//   6 no eyepieces at all: the game's own night-vision picture over the whole frame
// (1 to 5: the device drawn from each eye, the lens maps, the tube's picture and the composite stay; 5 less 6 is what
// they cost together.)
internal static partial class NightVisionEyepieces
{
    private static readonly string[] ProbeSteps =
    {
        "as it is", "without the objective's near blur", "and without the lenses' glow", "and without the glass's reflections",
        "and without the IR layer", "no eyepieces: the game's own night-vision picture",
    };
    private const int ProbeSettle = 180, ProbeWarm = 40, ProbeCount = 200;
    private static int _probeStep = -1, _probeFrames, _probeSettled;
    private static float _probeSeconds;
    private static bool _probeDone, _probeBlur, _probeGlow, _probeReflections;

    // Read by IrWanted and by Update's "worn".
    internal static bool ProbeNoIr { get; private set; }
    internal static bool ProbeNoEyepieces { get; private set; }

    // Once a frame, as the eyepieces' state for the frame is known.
    private static void CostProbeTick(bool downAndOn)
    {
        if (_probeDone)
            return;
        // (At the last step the eyepieces are off by our own doing: the goggle is still down.)
        bool there = _body != null && !_device.Thermal && (ProbeNoEyepieces ? downAndOn : Active && _flip >= 0.999f);
        if (!there)
        {
            // Flipped up or taken off part way: put back, and from the start the next time.
            if (_probeStep >= 0)
                ProbeRestore();
            _probeStep = -1;
            _probeSettled = 0;
            return;
        }
        if (_probeStep < 0)
        {
            if (++_probeSettled < ProbeSettle)
                return;
            _probeBlur = NearBlurOn;
            _probeGlow = EyepieceGlow;
            _probeReflections = LensReflections;
            _probeStep = 0;
            _probeFrames = 0;
            _probeSeconds = 0f;
            NewCameraPlugin.Log?.LogInfo($"NVG cost: measuring {_device.Node} at {Screen.width}x{Screen.height} (near blur {NearBlurOn}, glow {EyepieceGlow}, reflections {LensReflections}, picture {Picture}); keep the goggle down and stand still.");
        }
        _probeFrames++;
        if (_probeFrames > ProbeWarm)
            _probeSeconds += Time.unscaledDeltaTime;
        if (_probeFrames < ProbeWarm + ProbeCount)
            return;
        NewCameraPlugin.Log?.LogInfo($"NVG cost {_probeStep + 1}/{ProbeSteps.Length}: {_probeSeconds / ProbeCount * 1000f:F2} ms a frame ({ProbeCount / _probeSeconds:F0} a second), {ProbeSteps[_probeStep]}.");
        _probeStep++;
        _probeFrames = 0;
        _probeSeconds = 0f;
        if (_probeStep >= ProbeSteps.Length)
        {
            ProbeRestore();
            _probeDone = true;
            NewCameraPlugin.Log?.LogInfo("NVG cost: done, all put back as it was.");
            return;
        }
        NearBlurOn = _probeStep < 1 && _probeBlur;
        EyepieceGlow = _probeStep < 2 && _probeGlow;
        LensReflections = _probeStep < 3 && _probeReflections;
        ProbeNoIr = _probeStep >= 4;
        ProbeNoEyepieces = _probeStep >= 5;
    }

    private static void ProbeRestore()
    {
        NearBlurOn = _probeBlur;
        EyepieceGlow = _probeGlow;
        LensReflections = _probeReflections;
        ProbeNoIr = false;
        ProbeNoEyepieces = false;
    }
}
