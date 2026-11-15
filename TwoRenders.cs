using UnityEngine;

namespace EFTNewCamera;

// The user's experiment (2026-11-15): with the goggle down or flipping, the frame seen from each eye, half each. Each eye
// stands across where its eyepiece's centre lens is (NightVisionEyes.cs: the lenses across from the head's middle, as
// last seen from the right eye's render). The main camera renders first, before anything else renders
// (Application.onBeforeRender), from the left eye, its finished frame kept at the eyepieces' image effect; then as ever,
// from the right eye, its frame mixed half and half with the kept one there (NightVisionComposite.cs, before the
// interface). Each render is one eye, the camera itself: the device drawn into it and its lenses mapped from there.
// TEMP PROBES: each eye's own frame, before the two are mixed, in the lower corners (the left eye's on the left).
internal static class TwoRenders
{
    internal static bool On = true;
    internal static bool Probes = true;
    private const int HalvesPass = 1, OpaquePass = 2;   // NvgTube.shader

    private static bool _pass, _hooked, _failed;
    private static int _leftDone = -100;
    private static RenderTexture? _left;
    private static Material? _material;
    private static readonly int OtherId = Shader.PropertyToID("_Other"), OtherShareId = Shader.PropertyToID("_OtherShare");

    // Each eye across from the head's middle (m), from where the eyepieces' centre lenses are; until they are seen, the
    // aiming eye's distance either side.
    private static float _leftEye = -0.03f, _rightEye = 0.03f;

    internal static bool Now => On && !_failed && NightVisionEyepieces.DownOrFlipping;

    // The camera across from the head's middle while on: at the right eye (the left eye's render placed from there).
    internal static float? EyeOverride => Now ? _rightEye : (float?)null;

    // Where the camera is placed from its place this frame: over at the left eye during the left eye's render.
    internal static Vector3 OtherEyeOffset(Quaternion turn) => _pass ? turn * new Vector3(_leftEye - _rightEye, 0f, 0f) : Vector3.zero;

    // From NightVisionEyes.RenderLenses in the right eye's render: the centre lenses across from the head's middle.
    internal static void SeeLenses(float leftAcross, float rightAcross)
    {
        if (_pass || !Now || rightAcross - leftAcross < 0.02f)
            return;
        _leftEye = leftAcross;
        _rightEye = rightAcross;
    }

    internal static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Application.onBeforeRender += RenderLeftEye;
    }

    private static void RenderLeftEye()
    {
        if (!Now)
            return;
        Camera? main = Camera.main;
        if (main == null || !main.isActiveAndEnabled)
            return;
        _pass = true;
        try
        {
            main.Render();
        }
        catch (System.Exception e)
        {
            _failed = true;
            NewCameraPlugin.Log?.LogError("Two renders: the left eye's render failed, not tried again: " + e);
        }
        finally
        {
            _pass = false;
        }
    }

    // In the eyepieces' image effect, the frame done: the left eye's kept; the right eye's mixed half and half with it.
    internal static void TakeEye(RenderTexture into, Shader? tubeShader)
    {
        if (tubeShader == null)
            return;
        Material material = _material ??= new Material(tubeShader) { name = "Justachae two eyes", hideFlags = HideFlags.DontSave };
        if (_pass)
        {
            if (_left == null || _left.width != into.width || _left.height != into.height || _left.format != into.format)
            {
                if (_left != null)
                {
                    _left.Release();
                    Object.Destroy(_left);
                }
                _left = new RenderTexture(into.width, into.height, 0, into.format) { name = "Justachae left eye frame", hideFlags = HideFlags.DontSave };
                _left.Create();
            }
            Graphics.Blit(into, _left);
            _leftDone = Time.frameCount;
            return;
        }
        if (!Now || _leftDone != Time.frameCount || _left == null)
            return;
        RenderTexture right = RenderTexture.GetTemporary(into.width, into.height, 0, into.format);
        Graphics.Blit(into, right);
        material.SetTexture(OtherId, _left);
        material.SetFloat(OtherShareId, 0.5f);
        Graphics.Blit(right, into, material, HalvesPass);
        if (Probes)
            DrawProbes(into, _left, right, material);
        RenderTexture.ReleaseTemporary(right);
    }

    // Each eye's frame a quarter of the frame across, opaque in a thin white frame: the left eye's in the lower left
    // corner, the right's in the lower right.
    private static void DrawProbes(RenderTexture into, RenderTexture left, RenderTexture right, Material material)
    {
        int width = into.width / 4, height = into.height / 4, margin = into.height / 40;
        RenderTexture? active = RenderTexture.active;
        RenderTexture.active = into;
        GL.PushMatrix();
        // Top-left origin, as Graphics.DrawTexture's rects go.
        GL.LoadPixelMatrix(0, into.width, into.height, 0);
        for (int side = 0; side < 2; side++)
        {
            float x = side == 0 ? margin : into.width - width - margin, y = into.height - margin - height;
            Graphics.DrawTexture(new Rect(x - 2, y - 2, width + 4, 2), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x - 2, y + height, width + 4, 2), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x - 2, y, 2, height), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x + width, y, 2, height), Texture2D.whiteTexture);
            Graphics.DrawTexture(new Rect(x, y, width, height), side == 0 ? left : right, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, Color.white, material, OpaquePass);
        }
        GL.PopMatrix();
        RenderTexture.active = active;
    }
}
