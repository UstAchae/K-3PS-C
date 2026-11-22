using UnityEngine;

namespace EFTNewCamera;

// What the eyepieces' picture costs in a raid, measured (the user's, 2026-11-22: an F-PANO on Ground Zero, 75 frames a
// second flipped up and 40 down, so 11.7 ms a frame for the eyepieces and the thermal fusion together). Nothing of the
// picture is changed for good: the first time in a raid (not the hideout) that a night-vision goggle (not a thermal
// one) is down and settled, parts are left out one after another, each way held for ProbeWarm + ProbeCount frames and
// the mean frame time of the last ProbeCount written to the log; then all is put back as it was. A frame's time is the
// whole frame's (the game is bound by the graphics card here), so what a part costs is the difference between two lines.
//   1 as it is
//   2 without the objective's blur of what is near (F12 近处虚化)
//   3 and without the IR layer (IR lights and lasers for the tubes, the night-vision ambient)
//   4 and without the other mod's picture laid into the tubes (EyepieceFusion: the thermal fusion)
//   5 and without the device drawn from each eye into the main camera's frame (the game draws it, from the camera)
//   6 no eyepieces at all: the game's own night-vision picture over the whole frame
// (5 less 6: what is left, the lens maps, the tube's picture and the composite.)
//
// Earlier, in the hideout, one run each, good to a millisecond or two: an N-15 as it is 18.5 ms, without the near
// blur 14.2, no eyepieces 10.5; within the near blur (a PNV-10T, 3.2 ms) its seven bands 1.4 and taking the frame down
// and laying it back 1.8; the F-PANO's thermal fusion 1.6 (K-FPANO's own measurement).
internal static partial class NightVisionEyepieces
{
    private static readonly string[] ProbeSteps =
    {
        "as it is", "without the objective's near blur", "and without the IR layer", "and without the other mod's picture (the fusion)",
        "and without the device drawn from each eye", "no eyepieces: the game's own night-vision picture",
    };
    // The first run in a raid (1.8.205, an F-PANO on Ground Zero) told nothing: begun as the raid began, its first step
    // came to 86 ms a frame, and the game's own picture (28.2) to more than the eyepieces without their near blur (22.5).
    // So: not before the raid has run RaidSettleSeconds; each step's MEDIAN frame time, not its mean (a hitch then counts
    // for nothing); and the six steps gone through twice, so two figures for each show how far one can be trusted.
    private const int ProbeSettle = 180, ProbeWarm = 30, ProbeCount = 150, ProbeRounds = 2;
    private const float RaidSettleSeconds = 60f;
    private static int _probeStep = -1, _probeFrames, _probeSettled, _probeRound;
    private static float _probeSeconds, _raidSince = -1f;
    private static readonly float[] _probeTimes = new float[ProbeCount];
    private static bool _probeDone, _probeBlur;

    // Read by IrWanted, Fused, OnCameraPreCull's "_direct" and Update's "worn".
    internal static bool ProbeNoIr { get; private set; }
    internal static bool ProbeNoFuse { get; private set; }
    internal static bool ProbeNoDirect { get; private set; }
    internal static bool ProbeNoEyepieces { get; private set; }

    // Once a frame, as the eyepieces' state for the frame is known.
    private static void CostProbeTick(bool downAndOn, bool inRaid)
    {
        if (_probeDone)
            return;
        // (At the last step the eyepieces are off by our own doing: the goggle is still down.)
        if (!inRaid)
            _raidSince = -1f;
        else if (_raidSince < 0f)
            _raidSince = Time.unscaledTime;
        bool there = inRaid && Time.unscaledTime - _raidSince >= RaidSettleSeconds && _body != null && !_device.Thermal && (ProbeNoEyepieces ? downAndOn : Active && _flip >= 0.999f);
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
            _probeStep = 0;
            _probeFrames = 0;
            _probeSeconds = 0f;
            NewCameraPlugin.Log?.LogInfo($"NVG cost: measuring {_device.Node} in a raid at {Screen.width}x{Screen.height} (near blur {NearBlurOn}, glow {EyepieceGlow}, reflections {LensReflections}, picture {(UseBrnvg ? Picture : PictureGame)}, fused this frame {Interop.EyepieceFusion.ClaimedFrame == Time.frameCount}); keep the goggle down and stand still.");
        }
        _probeFrames++;
        if (_probeFrames > ProbeWarm)
        {
            _probeTimes[_probeFrames - ProbeWarm - 1] = Time.unscaledDeltaTime;
            _probeSeconds += Time.unscaledDeltaTime;
        }
        if (_probeFrames < ProbeWarm + ProbeCount)
            return;
        System.Array.Sort(_probeTimes);
        float median = _probeTimes[ProbeCount / 2];
        NewCameraPlugin.Log?.LogInfo($"NVG cost round {_probeRound + 1}, {_probeStep + 1}/{ProbeSteps.Length}: median {median * 1000f:F2} ms a frame ({1f / median:F0} a second; mean {_probeSeconds / ProbeCount * 1000f:F2}, slowest {_probeTimes[ProbeCount - 1] * 1000f:F0}), {ProbeSteps[_probeStep]}.");
        _probeStep++;
        _probeFrames = 0;
        _probeSeconds = 0f;
        if (_probeStep >= ProbeSteps.Length && _probeRound + 1 < ProbeRounds)
        {
            _probeRound++;
            _probeStep = 0;
        }
        if (_probeStep >= ProbeSteps.Length)
        {
            ProbeRestore();
            _probeDone = true;
            NewCameraPlugin.Log?.LogInfo("NVG cost: done, all put back as it was.");
            return;
        }
        NearBlurOn = _probeStep < 1 && _probeBlur;
        ProbeNoIr = _probeStep >= 2;
        ProbeNoFuse = _probeStep >= 3;
        ProbeNoDirect = _probeStep >= 4;
        ProbeNoEyepieces = _probeStep >= 5;
    }

    private static void ProbeRestore()
    {
        NearBlurOn = _probeBlur;
        ProbeNoIr = false;
        ProbeNoFuse = false;
        ProbeNoDirect = false;
        ProbeNoEyepieces = false;
    }
}
