using System.Diagnostics;
using UnityEngine;

namespace EFTNewCamera;

// Temporary (1.8.138): what each state of the goggle costs, measured, before anything is changed for speed (the user's,
// 2026-11-19: "where is it still dear?"). The frame's whole time by state, and our own work on the processor within it:
// the gun's skinned parts baked, and the device's and guns' draws laid. One line every 15 s while a goggle is worn.
internal static class PerfLog
{
    private static readonly string[] Names = { "no goggle", "no goggle, aiming", "goggle down", "goggle down, its eye aiming", "bare eye aiming, goggle eye leads", "bare eye aiming, bare eye leads" };
    private static readonly double[] _frame = new double[6], _bake = new double[6], _lay = new double[6];
    private static readonly int[] _frames = new int[6], _gunDraws = new int[6];
    private static long _bakeTicks, _layTicks, _from;
    private static int _draws;
    private static float _since;

    internal static long Start() => Stopwatch.GetTimestamp();
    internal static void Baked(long from) => _bakeTicks += Stopwatch.GetTimestamp() - from;
    internal static void Laid(long from, int gunDraws)
    {
        _layTicks += Stopwatch.GetTimestamp() - from;
        _draws = gunDraws;
    }

    private static int State()
    {
        float aimed = CameraPosition.ViewAimed;
        bool aiming = aimed > 0.95f, resting = aimed < 0.05f;
        if (!aiming && !resting)
            return -1;
        if (!NightVisionEyepieces.DownOrFlipping)
            return aiming ? 1 : 0;
        if (!NightVisionEyepieces.Active)
            return -1;
        if (!aiming)
            return 2;
        if (!TwoEyes.BareAims)
            return 3;
        float lead = TwoEyes.Lead;
        return lead < 0.05f ? 4 : lead > 0.95f ? 5 : -1;
    }

    internal static void Update()
    {
        float dt = Time.unscaledDeltaTime;
        int state = Camera.main != null ? State() : -1;
        if (state >= 0 && dt < 0.1f)
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            _frame[state] += dt * 1000.0;
            _bake[state] += _bakeTicks * toMs;
            _lay[state] += _layTicks * toMs;
            _gunDraws[state] = _draws;
            _frames[state]++;
        }
        _bakeTicks = 0;
        _layTicks = 0;
        _draws = 0;
        _since += dt;
        if (_since < 15f)
            return;
        _since = 0f;
        bool goggle = false;
        for (int s = 2; s < 6; s++)
            goggle |= _frames[s] > 0;
        if (goggle)
        {
            var line = new System.Text.StringBuilder("[perf] ").Append(Screen.width).Append('x').Append(Screen.height);
            for (int s = 0; s < 6; s++)
            {
                if (_frames[s] < 30)
                    continue;
                line.Append(" | ").Append(Names[s]).Append(": ").Append((_frame[s] / _frames[s]).ToString("0.00")).Append(" ms (")
                    .Append(_frames[s]).Append(" frames), bake ").Append((_bake[s] / _frames[s]).ToString("0.000")).Append(", lay ")
                    .Append((_lay[s] / _frames[s]).ToString("0.000")).Append(", gun draws ").Append(_gunDraws[s]);
            }
            NewCameraPlugin.Log?.LogInfo(line.ToString());
        }
        for (int s = 0; s < 6; s++)
        {
            _frame[s] = _bake[s] = _lay[s] = 0.0;
            _frames[s] = 0;
        }
    }
}
