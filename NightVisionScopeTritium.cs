using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// Tritium vials seen through a magnified sight with the goggle down. The sight is focused far off, so a vial a few
// metres away is out of its focus too: its objective spreads the vial into a disc as wide, in the sight's field, as
// the objective's radius over the vial's distance; the sight then magnifies the picture with the disc in it, and the
// goggle, focused at infinity as the sight's picture is, sees that disc sharp. So the discs are drawn into the sight
// camera's own picture (passes 8 and 9 of NvgObjective.shader, as for the frame, with the sight camera's view, its
// depth, and its objective for the aperture); the light through the sight, counted where the tube sees it (NightVisionScope.cs),
// dims them with the rest. Hidden where something nearer stands in the way, from the sight camera's depth.
// The sight's camera's part of the eyepieces' picture (the steps are listed at the head of NightVisionComposite.cs).
internal static partial class NightVisionEyepieces
{
    private const float ScopeFocus = 100f;               // where a sight is focused (m): the game has no figure for it
    private const float ScopeBlurLimit = 24f;            // the most a sight's own blur spreads (quarter-size pixels)
    private static Material? _scopeTritium;
    private static readonly int[] ScopeAllIds = Ids("_JustachaeScopeAll", 5), ScopeNearIds = Ids("_JustachaeScopeNear", 5), ScopeNearBlurIds = Ids("_JustachaeScopeNearBlur", 3);
    private static readonly int ScopeCutId = Shader.PropertyToID("_JustachaeScopeCut"), ScopeAcrossId = Shader.PropertyToID("_JustachaeScopeAcross");
    private static readonly int ScopeSharpId = Shader.PropertyToID("_JustachaeScopeSharp");
    private static readonly int StepShaderId = Shader.PropertyToID("_Step");

    private static int[] Ids(string name, int count)
    {
        var ids = new int[count];
        for (int i = 0; i < count; i++)
            ids[i] = Shader.PropertyToID(name + i);
        return ids;
    }
    private static RenderTexture? _opticOcclusion;
    private static Mesh? _opticDiscs;
    private static DepthTextureMode _opticDepthBefore;
    private static readonly List<VialSeen> _opticSeen = new();
    private static readonly Vector4[] _opticShape = new Vector4[MaxVials], _opticTint = new Vector4[MaxVials], _opticMasks = new Vector4[MaxVials];
    private static readonly float[] _opticTrace = new float[MaxVials];
    private static readonly List<Vector3> _discCorners = new(), _discAcross = new();
    private static readonly List<int> _discIndices = new();
    private static readonly int TanHalfShaderId = Shader.PropertyToID("_TanHalf");
    private static readonly int LensShaderId = Shader.PropertyToID("_Lens");

    // As the sight camera begins to render: the vials it sees, for its picture's discs.
    private static void NoteOptic(Camera optic)
    {
        if (_scopeBufferOn != optic || _scopeTritium == null || _opticDiscs == null || _scope.ObjectiveRadius <= 0f)
            return;
        Vector3 from = optic.transform.position;
        Matrix4x4 view = optic.worldToCameraMatrix, viewProjection = optic.nonJitteredProjectionMatrix * view;
        float tanUp = Mathf.Tan(optic.fieldOfView * 0.5f * Mathf.Deg2Rad);
        int width = Mathf.Max(optic.pixelWidth, 1), height = Mathf.Max(optic.pixelHeight, 1);
        float pixelsPerRadian = height / (2f * tanUp);
        _opticSeen.Clear();
        if (TritiumBrightness > 0f)
            foreach (VialSet set in _vialSets)
            {
                if (set.InHand || set.Renderer == null || !set.Renderer.enabled || !set.Renderer.gameObject.activeInHierarchy)
                    continue;                           // the gun's own sights are behind the sight
                Matrix4x4 toWorld = set.Renderer.localToWorldMatrix;
                foreach (var key in set.Submeshes)
                {
                    if (!VialsByMesh.TryGetValue(key, out Vial[]? vials) || vials == null)
                        continue;
                    foreach (Vial vial in vials)
                    {
                        Vector3 at = toWorld.MultiplyPoint3x4(vial.Centre);
                        Vector3 toSight = from - at;
                        float distance = toSight.magnitude;
                        if (distance < 0.05f || distance > VialRange)
                            continue;
                        float facing = vial.Normal.sqrMagnitude < 0.09f ? 1f
                            : Mathf.Clamp01(Vector3.Dot(toWorld.MultiplyVector(vial.Normal).normalized, toSight / distance) * 3f);
                        Vector4 clip = viewProjection * new Vector4(at.x, at.y, at.z, 1f);
                        float depth = -view.MultiplyPoint3x4(at).z;
                        if (facing <= 0f || clip.w <= 0f || depth <= 0f)
                            continue;
                        Vector2 uv = new Vector2(clip.x / clip.w * 0.5f + 0.5f, clip.y / clip.w * 0.5f + 0.5f);
                        float radius = _scope.ObjectiveRadius / distance * pixelsPerRadian;
                        if (uv.x < -radius * 3f / width || uv.x > 1f + radius * 3f / width || uv.y < -radius * 3f / height || uv.y > 1f + radius * 3f / height)
                            continue;
                        float glow = VialGlow * TritiumBrightness * facing;
                        if (radius < MinRadius)
                        {
                            glow *= radius * radius / (MinRadius * MinRadius);
                            radius = MinRadius;
                        }
                        _opticSeen.Add(new VialSeen
                        {
                            Distance = distance, At = at,
                            Shape = new Vector4(uv.x, uv.y, radius, glow),
                            Tint = new Vector4(vial.Tint.r, vial.Tint.g, vial.Tint.b, depth),
                        });
                    }
                }
            }
        _opticSeen.Sort(ByDistance);
        int count = Mathf.Min(_opticSeen.Count, MaxVials);
        _discCorners.Clear();
        _discAcross.Clear();
        _discIndices.Clear();
        for (int k = 0; k < count; k++)
        {
            Vector4 v = _opticSeen[k].Shape;
            _opticShape[k] = v;
            _opticTint[k] = _opticSeen[k].Tint;
            float r = Mathf.Max(v.z, 1f);
            for (int c = 0; c < 4; c++)
            {
                float across = c == 0 || c == 3 ? -3f : 3f, up = c < 2 ? -3f : 3f;
                _discIndices.Add(_discCorners.Count);
                _discCorners.Add(new Vector3(v.x + across * r / width, v.y + up * r / height, 0f));
                _discAcross.Add(new Vector3(across, up, k));
            }
        }
        _opticDiscs.Clear();
        _opticDiscs.SetVertices(_discCorners);
        _opticDiscs.SetUVs(0, _discAcross);
        _opticDiscs.SetIndices(_discIndices, MeshTopology.Quads, 0, false);
        _opticDiscs.bounds = new Bounds(Vector3.zero, Vector3.one * 10f);

        _scopeTritium.SetVectorArray(VialsId, _opticShape);
        _scopeTritium.SetVectorArray(VialTintId, _opticTint);
        _scopeTritium.SetVectorArray(VialMasksId, _opticMasks);
        _scopeTritium.SetFloatArray(VialTraceId, _opticTrace);
        _scopeTritium.SetFloat(VialCountId, count);
        _scopeTritium.SetVector(TargetId, new Vector4(width, height, _scope.ObjectiveRadius, 0f));
        _scopeTritium.SetVector(TanHalfShaderId, new Vector2(tanUp * width / height, tanUp));
        // The aperture at the sight camera; its own blur of what is near: focused at ScopeFocus, its objective's radius
        // in quarter-size pixels per radian of the defocus, at most ScopeBlurLimit.
        float quarterPerRadian = height / 4f / (2f * tanUp);
        _scopeTritium.SetVector(LensShaderId, new Vector4(ScopeFocus, _scope.ObjectiveRadius * quarterPerRadian, 0f, ScopeBlurLimit));
        _scopeTritium.SetTexture(OcclusionId, _opticOcclusion);
    }

    // The sight's sharp picture cut down to a quarter by a pass, then spread at each size, each level the last halved
    // and spread again (as Blurred does for the frame), into the levels' temporaries.
    private static void BlurredInto(CommandBuffer buffer, int pass, int[] levels)
    {
        for (int i = 0; i < levels.Length; i++)
        {
            int size = -(4 << i);
            buffer.GetTemporaryRT(ScopeCutId, size, size, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            if (i == 0)
                buffer.Blit(ScopeSharpId, ScopeCutId, _scopeTritium, pass);
            else
                buffer.Blit(levels[i - 1], ScopeCutId);
            buffer.GetTemporaryRT(ScopeAcrossId, size, size, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            buffer.SetGlobalVector(StepShaderId, new Vector4(1f, 0f, 0f, 0f));
            buffer.Blit(ScopeCutId, ScopeAcrossId, _scopeTritium, 1);
            buffer.GetTemporaryRT(levels[i], size, size, 0, FilterMode.Bilinear, RenderTextureFormat.ARGBHalf);
            buffer.SetGlobalVector(StepShaderId, new Vector4(0f, 1f, 0f, 0f));
            buffer.Blit(ScopeAcrossId, levels[i], _scopeTritium, 2);
            buffer.ReleaseTemporaryRT(ScopeAcrossId);
            buffer.ReleaseTemporaryRT(ScopeCutId);
        }
    }

    // The sight camera's steps after it has rendered: the vials' occlusion and discs.
    private static CommandBuffer MakeScopeBuffer()
    {
        _scopeTritium ??= new Material(_objectiveShader!) { name = "Justachae NVG sight tritium", hideFlags = HideFlags.DontSave };
        if (_opticOcclusion == null)
        {
            _opticOcclusion = new RenderTexture(4 * OcclusionTile, 4 * OcclusionTile, 0, RenderTextureFormat.RHalf)
            { name = "Justachae NVG sight tritium occlusion", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _opticOcclusion.Create();
        }
        if (_opticDiscs == null)
        {
            _opticDiscs = new Mesh { name = "Justachae NVG sight tritium discs", hideFlags = HideFlags.DontSave };
            _opticDiscs.MarkDynamic();
        }
        // The sight's picture, its near blurred by the sight's own objective (passes 0 to 5 and 3, as for the frame,
        // with the sight's defocus for the blur: Blur in NvgObjective.shader), its vials' discs laid on (8, 9).
        var buffer = new CommandBuffer { name = "Justachae NVG sight picture" };
        buffer.GetTemporaryRT(ScopeSharpId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
        buffer.Blit(BuiltinRenderTextureType.CameraTarget, ScopeSharpId);
        // The sight's own blur of what is near in its picture is left off: it was drawn with a hard edge where what is
        // near begins (the older way, levels halved), and a jagged, shimmering patch showed in the sight's picture,
        // where one aims (2026-11-11). The picture is taken as the sight's camera made it.
        buffer.GetTemporaryRT(ScopeTempId, -1, -1, 0, FilterMode.Bilinear, RenderTextureFormat.DefaultHDR);
        buffer.Blit(ScopeSharpId, ScopeTempId);
        buffer.ReleaseTemporaryRT(ScopeSharpId);
        buffer.Blit(Texture2D.blackTexture, _opticOcclusion, _scopeTritium, 8);
        buffer.SetRenderTarget(ScopeTempId);
        buffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -1f, 1f));  // the buffer makes it the GPUs own
        buffer.DrawMesh(_opticDiscs, Matrix4x4.identity, _scopeTritium, 0, 9);
        buffer.Blit(ScopeTempId, BuiltinRenderTextureType.CameraTarget);
        buffer.ReleaseTemporaryRT(ScopeTempId);
        return buffer;
    }
}
