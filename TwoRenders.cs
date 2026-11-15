using UnityEngine;

namespace EFTNewCamera;

// The user's experiment (2026-11-15): a monocular (the PVS-14) down or flipping, seen with both eyes, half each. The
// goggle's eye stands behind its lens (NightVisionEyes.cs: the lens across from the head's middle, as last seen from
// that eye's render), the bare eye as far the other side of the head's middle. The main camera renders first, before
// anything else renders (Application.onBeforeRender), from the bare eye, its finished frame kept at the eyepieces'
// image effect; then as ever, from the goggle's eye, its frame mixed half and half with the kept one there
// (NightVisionComposite.cs, before the interface): the goggle and its picture seen whole, the bare eye's view (its gun
// among it) through it at half. Each render is one eye, the camera itself: the device drawn into it and its lens mapped
// from there. A binocular is drawn as ever, its two halves in one frame.
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

    // The goggle's eye across from the head's middle (m), from where its lens is; the bare eye the other side. Until the
    // lens is seen, the aiming eye's distance.
    private static float _goggleEye = 0.03f;
    private static float BareEye => -_goggleEye;

    internal static bool Now => On && !_failed && NightVisionEyepieces.DownOrFlipping && NightVisionEyepieces.MonocularWorn;

    // The camera across from the head's middle while on: at the goggle's eye (the bare eye's render placed from there).
    internal static float? EyeOverride => Now ? _goggleEye : (float?)null;

    // Where the camera is placed from its place this frame: over at the bare eye during the bare eye's render.
    internal static Vector3 OtherEyeOffset(Quaternion turn) => _pass ? turn * new Vector3(BareEye - _goggleEye, 0f, 0f) : Vector3.zero;

    // From NightVisionEyes.RenderLenses in the goggle eye's render: its lens across from the head's middle.
    internal static void SeeLens(float across)
    {
        if (_pass || !Now || Mathf.Abs(across) < 0.01f)
            return;
        _goggleEye = across;
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
            NewCameraPlugin.Log?.LogError("Two renders: the bare eye's render failed, not tried again: " + e);
        }
        finally
        {
            _pass = false;
        }
    }

    // In the eyepieces' image effect, the frame done: the bare eye's kept; the goggle eye's mixed half and half with it.
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
        {
            bool goggleLeft = _goggleEye < 0f;
            DrawProbes(into, goggleLeft ? right : _left, goggleLeft ? _left : right, material);
        }
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
