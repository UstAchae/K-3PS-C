using UnityEngine;

namespace EFTNewCamera;

// What the eyepieces' picture costs, measured (the user's, 2026-11-22). Nothing of the picture is changed for good: the
// first time a night-vision goggle (not a thermal one) is down and settled, parts are left out one after another, each
// way held for ProbeWarm + ProbeCount frames and the mean frame time of the last ProbeCount written to the log; then all
// is put back as it was. A frame's time is the whole frame's (the game is bound by the graphics card here), so what a
// part costs is the difference between two lines.
//
// The first measurement (1.8.192, an N-15 with BRNVG's picture, 2560x1440, the hideout; one run, each figure good to
// a millisecond or so): as it is 18.5 ms; without the objective's near blur 14.2; without the IR layer too 15.0; no
// eyepieces at all (the game's own picture) 10.5. So: the near blur 4.3 ms, all the rest of the eyepieces about 4, the
// IR layer nothing that shows.
//
// This one: the near blur's own parts (NightVisionDefocus.cs, DefocusThrough), left out one after another:
//   1 as it is
//   2 without the seven bands of nearness, each blurred by itself (56 of its 85 or so passes)
//   3 and without the four blurs of what lies behind a near thing
//   4 and without closing the near parts' gaps and the seam about them
//   5 and without gathering the nearness over frames
//   6 no near blur at all
// (5 less 6: what is left, the frame taken down to a quarter with its nearness, and the last pass at the frame's size.)
internal static partial class NightVisionEyepieces
{
    // (That measurement, 1.8.193, a PNV-10T: as it is 18.2 ms; without the bands 16.8; the three after it no different;
    // no near blur 15.0. So the bands 1.4 ms, taking the frame down and laying it back 1.8, the rest nothing.)
    //
    // This one (1.8.195): what working the near blur only about the eyepieces (1.8.194) saves, the two ways turn and
    // turn about in one stand, so the same goggle, place and view: about the eyepieces, all over the frame, about the
    // eyepieces, all over the frame, then no near blur.
    private static readonly string[] ProbeSteps =
    {
        "the near blur about the eyepieces only", "the near blur all over the frame", "about the eyepieces only (again)", "all over the frame (again)",
        "no near blur at all",
    };
    // Read by DefocusThrough: the frame worked all over, as before 1.8.194.
    internal static bool ProbeNoRegion { get; private set; }
    private const int ProbeSettle = 180, ProbeWarm = 40, ProbeCount = 400;
    private static int _probeStep = -1, _probeFrames, _probeSettled;
    private static float _probeSeconds;
    private static bool _probeDone, _probeBlur;

    // Read by IrWanted and by Update's "worn" (the first measurement's; not used by this one).
    internal static bool ProbeNoIr => false;
    internal static bool ProbeNoEyepieces => false;
    // Read by DefocusThrough: how many of its parts are left out (0 none, 4 all four).
    internal static int ProbeDefocusLess { get; private set; }

    // Once a frame, as the eyepieces' state for the frame is known.
    private static void CostProbeTick(bool downAndOn)
    {
        if (_probeDone)
            return;
        bool there = _body != null && !_device.Thermal && Active && _flip >= 0.999f;
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
            if (!NearBlurOn)
            {
                NewCameraPlugin.Log?.LogInfo("NVG cost: the near blur is switched off (F12 近处虚化): nothing to measure.");
                _probeDone = true;
                return;
            }
            _probeBlur = NearBlurOn;
            _probeStep = 0;
            _probeFrames = 0;
            _probeSeconds = 0f;
            NewCameraPlugin.Log?.LogInfo($"NVG cost: measuring the near blur's parts on {_device.Node} at {Screen.width}x{Screen.height} (picture {Picture}); keep the goggle down and stand still.");
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
        ProbeNoRegion = _probeStep == 1 || _probeStep == 3;
        NearBlurOn = _probeStep < 4 && _probeBlur;
    }

    private static void ProbeRestore()
    {
        NearBlurOn = _probeBlur;
        ProbeDefocusLess = 0;
        ProbeNoRegion = false;
    }
}
