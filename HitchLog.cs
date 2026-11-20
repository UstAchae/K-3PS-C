using UnityEngine;

namespace EFTNewCamera;

// Temporary (1.8.162): the game catches now and then (the user's, 2026-11-20), and no log says when or in what state. A
// line for each frame that takes far longer than those about it: how long, against the usual, what was worn and done at
// the time, and whether the garbage collector ran in it (its count before and after). Nothing written otherwise, but a
// count every minute. Two hundred lines a session, then only the counts.
internal static class HitchLog
{
    private static float _usual = -1f, _since, _worst;
    private static int _lines, _hitches, _collected, _frames;
    private static int _gc0 = -1, _gc1, _gc2;
    private static readonly System.Text.StringBuilder _line = new System.Text.StringBuilder(256);

    internal static void Update()
    {
        float dt = Time.unscaledDeltaTime;
        int gc0 = System.GC.CollectionCount(0), gc1 = System.GC.CollectionCount(1), gc2 = System.GC.CollectionCount(2);
        bool first = _gc0 < 0;
        int ran0 = first ? 0 : gc0 - _gc0, ran1 = first ? 0 : gc1 - _gc1, ran2 = first ? 0 : gc2 - _gc2;
        _gc0 = gc0;
        _gc1 = gc1;
        _gc2 = gc2;
        // (Only in a scene with the game's camera, and not across a loading screen or a pause.)
        if (Camera.main == null || dt <= 0f || dt > 2f)
        {
            _usual = -1f;
            return;
        }
        if (_usual < 0f)
        {
            _usual = dt;
            return;
        }
        _frames++;
        _collected += ran0;
        bool hitch = dt > Mathf.Max(_usual * 2.5f, _usual + 0.025f);
        if (hitch)
        {
            _hitches++;
            _worst = Mathf.Max(_worst, dt);
            if (_lines < 200)
            {
                _lines++;
                _line.Length = 0;
                _line.Append("[hitch] ").Append((dt * 1000f).ToString("0")).Append(" ms (usual ").Append((_usual * 1000f).ToString("0.0")).Append(")")
                    .Append(", gc ").Append(ran0).Append('/').Append(ran1).Append('/').Append(ran2)
                    .Append(" | ").Append(State());
                NewCameraPlugin.Log?.LogInfo(_line.ToString());
            }
        }
        else
            _usual = Mathf.Lerp(_usual, dt, 0.05f);
        _since += dt;
        if (_since < 60f)
            return;
        if (_hitches > 0 || _collected > 0)
            NewCameraPlugin.Log?.LogInfo($"[hitch] the last minute: {_hitches} long frames of {_frames} (worst {_worst * 1000f:0} ms, usual {_usual * 1000f:0.0} ms), the collector ran {_collected} times");
        _since = 0f;
        _hitches = _collected = _frames = 0;
        _worst = 0f;
    }

    private static string State()
    {
        bool optic = false;
        try
        {
            Camera? sight = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.Camera;
            optic = sight != null && sight.isActiveAndEnabled;
        }
        catch
        {
        }
        string goggle = !NightVisionEyepieces.DownOrFlipping ? "no goggle down"
            : NightVisionEyepieces.Active ? "goggle down" : "goggle flipping";
        return goggle
            + (NightVisionEyepieces.MonocularWorn ? ", monocular" : "")
            + (NightVisionEyepieces.ThermalShown ? ", thermal" : "")
            + (NightVisionEyepieces.PodsBothAway ? ", both eyepieces away" : "")
            + $", one eye {NightVisionEyepieces.OneEyeShare:0.00}, aimed {CameraPosition.ViewAimed:0.00}"
            + (TwoEyes.BareAims ? $", bare eye aims (lead {TwoEyes.Lead:0.00})" : "")
            + (optic ? ", sight's camera on" : "")
            + $", frame {Time.frameCount}, t {Time.realtimeSinceStartup:0.0}";
    }
}
