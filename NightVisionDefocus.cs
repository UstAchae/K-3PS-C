using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The objective's defocus (step 3 of the eyepieces' picture, NightVisionComposite.cs). A point at a distance d from a
// lens of aperture D focused at s is spread over a disc of even light, whose diameter seen from the lens is
// D·|1/d − 1/s|: the hands and the gun a few tenths of a metre off become discs degrees wide, the world a few metres
// off a fraction of one. Each point near enough to be spread more than a quarter-size pixel is drawn as its disc
// (NvgDefocus.shader), at a quarter of the frame's size: discs, not Gaussians. What is near stays solid: its spread
// colour laid over the sharp rest, its edge fading out over as far as its discs reach (a lens would let what lies
// behind it show through its edge; the frame has nothing of that, and a near thing seen through came out as glass).
internal static partial class NightVisionEyepieces
{
    private const float DefocusAperture = 0.0225f;      // the objective's, 27 mm at f/1.2 (as the tritium's)
    private const float DefocusMaxRadius = 96f;         // a disc's radius at most, in quarter-size pixels
    private const int DefocusScale = 4;

    private static Shader? _defocusShader;
    private static Material? _defocus;
    private static CommandBuffer? _discBuffer;
    private static readonly MaterialPropertyBlock _nearDiscs = new MaterialPropertyBlock();
    private static bool _defocusNoted;

    private static readonly int DefocusLensId = Shader.PropertyToID("_DefocusLens");
    private static readonly int SrcId = Shader.PropertyToID("_Src");
    private static readonly int CocId = Shader.PropertyToID("_Coc");
    private static readonly int GridId = Shader.PropertyToID("_Grid");
    private static readonly int NearAccId = Shader.PropertyToID("_NearAcc");

    // Step 3 the lens's way: false (the frame left to the older blur) without the shader.
    private static bool DefocusThrough(Camera view, RenderTexture frame, ref RenderTexture? made)
    {
        if (_defocus == null)
            return false;
        Material defocus = _defocus;
        int width = Mathf.Max(frame.width / DefocusScale, 1), height = Mathf.Max(frame.height / DefocusScale, 1);
        float tanUp = Mathf.Tan(view.fieldOfView * 0.5f * Mathf.Deg2Rad);
        float pixelsPerRadian = height / (2f * tanUp);
        float ahead = Mathf.Clamp(Vector3.Dot(_body!.TransformPoint(_objectiveFront) - _renderedFrom, _renderedForward), 0f, MaxEyeDistance);
        // x: 1 over the focus distance (far away: 0); y: a disc's radius in quarter-size pixels per unit of |1/d − 1/s|;
        // z: how far the objective is ahead of the camera; w: the largest radius drawn.
        defocus.SetVector(DefocusLensId, new Vector4(0f, 0.5f * DefocusAperture * pixelsPerRadian, ahead, DefocusMaxRadius));
        defocus.SetVector(TanHalfId, TanHalf(view, frame));
        BindScope(defocus);

        RenderTexture nearColor = Temporary(width, height), coc = Temporary(width, height);
        Graphics.Blit(frame, nearColor, defocus, 1);
        Graphics.Blit(frame, coc, defocus, 2);

        RenderTexture nearAcc = Temporary(width, height);
        CommandBuffer discs = _discBuffer ??= new CommandBuffer { name = "Justachae NVG objective discs" };
        discs.Clear();
        discs.SetRenderTarget(nearAcc);
        discs.ClearRenderTarget(false, true, Color.clear);
        _nearDiscs.Clear();
        _nearDiscs.SetTexture(SrcId, nearColor);
        _nearDiscs.SetTexture(CocId, coc);
        // Into a render texture the frame's up is flipped where textures start at the top.
        _nearDiscs.SetVector(GridId, new Vector4(width, height, SystemInfo.graphicsUVStartsAtTop ? -1f : 1f, 0f));
        discs.DrawProcedural(Matrix4x4.identity, defocus, 4, MeshTopology.Triangles, width * height * 6, 1, _nearDiscs);
        Graphics.ExecuteCommandBuffer(discs);

        defocus.SetTexture(NearAccId, nearAcc);
        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        Graphics.Blit(frame, made, defocus, 5);
        AddTritium(view, made);
        ReleaseMask();
        _blurMask = Temporary(width, height);
        Graphics.Blit(frame, _blurMask, defocus, 6);
        defocus.SetTexture(NearAccId, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(nearColor);
        RenderTexture.ReleaseTemporary(coc);
        RenderTexture.ReleaseTemporary(nearAcc);
        if (!_defocusNoted)
        {
            _defocusNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG objective: defocus by discs, what is near kept solid ({width}x{height} discs).");
        }
        return true;
    }
}
