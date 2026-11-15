namespace EFTNewCamera;

// EXPERIMENT (the user's, 2026-11-15): a device seen with both eyes, half each, over the whole frame. The eyes stand at
// the two aiming eyes, AimEye either side of the head's middle (F12 「镜头位置」 瞄准眼离中线). The main camera renders
// the scene once, from where it is set (not moved over to the aiming eye while aimed); the device is drawn into it from
// each eye, and each eye's lenses are mapped from that eye (NightVisionEyes.RenderLenses), and the two eyes are laid
// over the whole frame half each (_EyeMix, _DirectMix) instead of the left eye's on the left and the right's on the
// right. The scene itself is one view for both (the gun among it). To be kept or taken out once seen.
internal static partial class NightVisionEyepieces
{
    internal static bool MonocularBothEyes = true;

    // The experiment is on for this device: one with eyepieces, down, flipping or switched on.
    private static bool ExperimentOn => MonocularBothEyes && _body != null && _lensNodes.Length > 0 && (Active || _flip > 0.001f);

    // Each frame, before the camera is placed: kept where it is set while the experiment is on (not over to the aiming eye).
    private static void PlaceExperimentEye()
    {
        CameraPosition.EyeOverride = ExperimentOn ? CameraPosition.Across : (float?)null;
        if (ExperimentOn && !_experimentNoted)
        {
            _experimentNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG experiment: the eyes {CameraPosition.AimEye * 1000f:F0} mm either side of the head's middle, the camera at {CameraPosition.Across * 1000f:F0} mm; the device drawn from each, half each over the frame.");
        }
    }
    private static bool _experimentNoted;

    private static void ReleaseOtherEye()
    {
        CameraPosition.EyeOverride = null;
        _experimentNoted = false;
    }
}
