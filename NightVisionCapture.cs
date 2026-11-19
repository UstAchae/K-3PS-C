using UnityEngine;

namespace EFTNewCamera;

// Temporary (1.8.152): the hole seen in a view of the device as the aim comes up or goes, with a goggle before one eye
// (the user's, 2026-11-19; guessed at four times). As the camera's going over passes a quarter, a half and three
// quarters of the way, either way, the frame is saved at each step of the eyepieces' image effect, with each eye's view of
// the device and its lens map, and what they were drawn from in the log. Twelve shots a session, then no more.
internal static partial class NightVisionEyepieces
{
    // (1.8.154: only while the bare eye leads, where the hole is seen; the first twelve were all with the goggle's eye
    // leading and showed none. More steps, eighteen shots.)
    private static readonly float[] CaptureAt = { 0.1f, 0.25f, 0.4f, 0.55f, 0.7f, 0.85f };
    private static float _captureWas;
    private static int _captureShots, _captureFrame = -1, _captureNumber;
    private static Vector3 _captureLeft, _captureRight;
    private static float _captureSplit, _capturePictureSplit;
    private static Vector4 _captureBareOver;

    private static bool CaptureNow()
    {
        if (_captureFrame == Time.frameCount)
            return true;
        float aimed = CameraPosition.ViewAimed, was = _captureWas;
        _captureWas = aimed;
        if (_captureShots >= 18 || !TwoEyes.Now || TwoEyes.BareAimShare <= 0.5f || TwoEyes.Lead < 0.5f)
            return false;
        foreach (float at in CaptureAt)
        {
            if ((was < at) != (aimed < at))
            {
                _captureFrame = Time.frameCount;
                _captureNumber = ++_captureShots;
                NewCameraPlugin.Log?.LogInfo($"[hole] shot {_captureNumber}: aimed {aimed:0.000} (was {was:0.000}), lead {TwoEyes.Lead:0.00}, bare aims {TwoEyes.BareAims}, leaving {CameraPosition.AimLeaving}"
                    + $", eyes mixed {_eyesMixed:0.000}, over {_eyesOver:0.000}, bare over {_captureBareOver}, goggle eye right {_goggleEyeRight}, monocular {_device.Monocular}"
                    + $", split {_captureSplit:0.000}, picture's {_capturePictureSplit:0.000}, left eye from {_captureLeft * 1000f} mm, right {_captureRight * 1000f} mm, camera across {CameraPosition.Horizontal * 1000f:0.0} mm");
                return true;
            }
        }
        return false;
    }

    private static void Capture(Texture? picture, string name, bool depth = false)
    {
        if (picture == null)
            return;
        try
        {
            int w = Mathf.Max(picture.width / 2, 8), h = Mathf.Max(picture.height / 2, 8);
            RenderTexture small = RenderTexture.GetTemporary(w, h, 0, depth ? RenderTextureFormat.RFloat : RenderTextureFormat.ARGB32);
            small.filterMode = FilterMode.Point;
            Graphics.Blit(picture, small);
            RenderTexture active = RenderTexture.active;
            RenderTexture.active = small;
            Texture2D read = new Texture2D(w, h, depth ? TextureFormat.RFloat : TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            RenderTexture.active = active;
            RenderTexture.ReleaseTemporary(small);
            if (depth)
            {
                // (Where anything was drawn: white.)
                var raw = read.GetRawTextureData<float>();
                Texture2D mask = new Texture2D(w, h, TextureFormat.R8, false);
                var into = mask.GetRawTextureData<byte>();
                bool reversed = SystemInfo.usesReversedZBuffer;
                for (int i = 0; i < raw.Length && i < into.Length; i++)
                    into[i] = (reversed ? raw[i] > 0f : raw[i] < 1f) ? (byte)255 : (byte)0;
                mask.Apply(false);
                Object.Destroy(read);
                read = mask;
            }
            string folder = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, ".codex-work", "nvgdump", "hole");
            System.IO.Directory.CreateDirectory(folder);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, $"{_captureNumber:00}-{name}.png"), read.EncodeToPNG());
            Object.Destroy(read);
        }
        catch (System.Exception e)
        {
            NewCameraPlugin.Log?.LogInfo($"[hole] {name}: {e.Message}");
        }
    }

    private static void CaptureDevice()
    {
        Capture(_directDepths[0], "4-device-left-eye", true);
        Capture(_directDepths[1], "5-device-right-eye", true);
        Capture(_lensMaps[0], "6-lens-map-left");
        Capture(_lensMaps[1], "7-lens-map-right");
    }
}
