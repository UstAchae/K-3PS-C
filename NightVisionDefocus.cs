using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The objective's defocus (step 3 of the eyepieces' picture, NightVisionComposite.cs). A point at a distance d from a
// lens of aperture D focused at s is spread over a disc of even light, whose diameter seen from the lens is
// D·|1/d − 1/s|. A few metres off that is a fraction of a degree, as the lens has it. Nearer it grows without end
// (things a few centimetres from the objective spread over the whole field and are not seen at all), so here it eases
// toward DefocusMostDegrees instead: the arms and the gun stay things seen, softly, the nearer the softer, never at a
// fixed most. Each point near enough to be spread more than a quarter-size pixel is drawn as its disc
// (NvgDefocus.shader): the small ones at a quarter of the frame's size, the larger at an eighth, a sixteenth and a
// thirty-second, each where its disc is 6 to 12 of that level's pixels across. What is near is laid over what lies
// behind it with as much of the lens's light as it blocks: through a sight's window, the scene beyond, sharp; behind
// its own edge, where the frame has nothing but the thing itself, what is about it spread as wide (NvgDefocus.shader).
internal static partial class NightVisionEyepieces
{
    private const float DefocusAperture = 0.0225f;      // the objective's, 27 mm at f/1.2 (as the tritium's)
    private const float DefocusMostDegrees = 3f;        // what a disc's radius eases toward, nearer and nearer
    private const int DefocusScale = 4;
    private const int DefocusLevels = 4;                // radii up to 12, 24, 48 and beyond quarter-size pixels
    // A disc is drawn where its radius is 6 to 12 of the level's pixels: coarser, a pixel's mean of what lies under it
    // jumped from frame to frame as the view moved, and what was spread rippled like water.
    private const float DefocusBand = 12f;

    private static Shader? _defocusShader;
    private static Material? _defocus;
    private static CommandBuffer? _discBuffer;
    private static readonly MaterialPropertyBlock[] _discBlocks = { new(), new(), new(), new() };
    private static readonly RenderTexture?[] _levelColor = new RenderTexture?[DefocusLevels], _levelCoc = new RenderTexture?[DefocusLevels],
        _levelAcc = new RenderTexture?[DefocusLevels], _levelFar = new RenderTexture?[DefocusLevels];
    private static bool _defocusNoted;

    private static readonly int DefocusLensId = Shader.PropertyToID("_DefocusLens");
    private static readonly int SrcId = Shader.PropertyToID("_Src");
    private static readonly int CocId = Shader.PropertyToID("_Coc");
    private static readonly int GridId = Shader.PropertyToID("_Grid");
    private static readonly int BandId = Shader.PropertyToID("_Band");
    private static readonly int ShapeId = Shader.PropertyToID("_Shape");
    private static readonly int[] NearAccIds = { Shader.PropertyToID("_NearAcc0"), Shader.PropertyToID("_NearAcc1"),
        Shader.PropertyToID("_NearAcc2"), Shader.PropertyToID("_NearAcc3") };
    private static readonly int[] FarIds = { Shader.PropertyToID("_Far0"), Shader.PropertyToID("_Far1"), Shader.PropertyToID("_Far2"),
        Shader.PropertyToID("_Far3") };

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
        // z: how far the objective is ahead of the camera; w: the radius it eases toward (quarter-size pixels).
        defocus.SetVector(DefocusLensId, new Vector4(0f, 0.5f * DefocusAperture * pixelsPerRadian, ahead,
            DefocusMostDegrees * Mathf.Deg2Rad * pixelsPerRadian));
        defocus.SetVector(TanHalfId, TanHalf(view, frame));
        BindScope(defocus);

        // What is near, and its discs' radii (as radius times cover, and cover), at a quarter of the size, then each
        // level half the last: a plain halving keeps the mean of both, so the radius stays the cover's mean.
        for (int level = 0; level < DefocusLevels; level++)
        {
            int w = Mathf.Max(width >> level, 1), h = Mathf.Max(height >> level, 1);
            _levelColor[level] = Temporary(w, h);
            _levelCoc[level] = Temporary(w, h);
            _levelAcc[level] = Temporary(w, h);
            _levelFar[level] = Temporary(w, h);
            if (level == 0)
            {
                Graphics.Blit(frame, _levelColor[0], defocus, 1);
                Graphics.Blit(frame, _levelCoc[0], defocus, 2);
                Graphics.Blit(frame, _levelFar[0], defocus, 3);
            }
            else
            {
                Graphics.Blit(_levelColor[level - 1], _levelColor[level], defocus, 7);
                Graphics.Blit(_levelCoc[level - 1], _levelCoc[level], defocus, 7);
                Graphics.Blit(_levelFar[level - 1], _levelFar[level], defocus, 7);
            }
        }

        CommandBuffer discs = _discBuffer ??= new CommandBuffer { name = "Justachae NVG objective discs" };
        discs.Clear();
        float flip = SystemInfo.graphicsUVStartsAtTop ? -1f : 1f;
        for (int level = 0; level < DefocusLevels; level++)
        {
            RenderTexture acc = _levelAcc[level]!;
            discs.SetRenderTarget(acc);
            discs.ClearRenderTarget(false, true, Color.clear);
            MaterialPropertyBlock block = _discBlocks[level];
            block.Clear();
            block.SetTexture(SrcId, _levelColor[level]);
            block.SetTexture(CocId, _levelCoc[level]);
            // Into a render texture the frame's up is flipped where textures start at the top; w: this level's pixel
            // in quarter-size pixels.
            block.SetVector(GridId, new Vector4(acc.width, acc.height, flip, 1 << level));
            // The radii (quarter-size pixels) this level draws: above the last level's, up to twice that.
            float least = level == 0 ? 0f : DefocusBand * (1 << (level - 1)), most = level == DefocusLevels - 1 ? 1e6f : DefocusBand * (1 << level);
            block.SetVector(BandId, new Vector4(least, most, 0f, 0f));
            discs.DrawProcedural(Matrix4x4.identity, defocus, 4, MeshTopology.Triangles, acc.width * acc.height * 6, 1, block);
        }
        Graphics.ExecuteCommandBuffer(discs);

        for (int level = 0; level < DefocusLevels; level++)
        {
            defocus.SetTexture(NearAccIds[level], _levelAcc[level]);
            defocus.SetTexture(FarIds[level], _levelFar[level]);
        }
        DiagDefocus(frame, pixelsPerRadian, ahead); // TEMP DIAG
        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        // Its outline: the near shape (what of the frame is near, at an eighth of its size) blurred wide and smooth.
        RenderTexture shape = _levelColor[1]!;
        RenderTexture across = Temporary(shape.width, shape.height), soft = Temporary(shape.width, shape.height);
        Graphics.Blit(shape, soft);
        for (int pass = 0; pass < 2; pass++)
        {
            defocus.SetVector(StepId, new Vector2(1f, 0f));
            Graphics.Blit(soft, across, defocus, 8);
            defocus.SetVector(StepId, new Vector2(0f, 1f));
            Graphics.Blit(across, soft, defocus, 8);
        }
        RenderTexture.ReleaseTemporary(across);
        defocus.SetTexture(ShapeId, soft);
        Graphics.Blit(frame, made, defocus, 5);
        DumpDefocus(frame, made, defocus); // TEMP DIAG
        AddTritium(view, made);
        ReleaseMask();
        _blurMask = Temporary(width, height);
        Graphics.Blit(frame, _blurMask, defocus, 6);
        defocus.SetTexture(ShapeId, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(soft);
        for (int level = 0; level < DefocusLevels; level++)
        {
            defocus.SetTexture(NearAccIds[level], Texture2D.blackTexture);
            defocus.SetTexture(FarIds[level], Texture2D.blackTexture);
            RenderTexture.ReleaseTemporary(_levelColor[level]);
            RenderTexture.ReleaseTemporary(_levelCoc[level]);
            RenderTexture.ReleaseTemporary(_levelAcc[level]);
            RenderTexture.ReleaseTemporary(_levelFar[level]);
            _levelColor[level] = _levelCoc[level] = _levelAcc[level] = _levelFar[level] = null;
        }
        if (!_defocusNoted)
        {
            _defocusNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG objective: defocus by discs at four sizes, over what lies behind ({width}x{height} quarter grid).");
        }
        return true;
    }

    // TEMP DIAG: on F11, each of its layers saved as a picture (BepInEx/defocus-dump):
    // the frame, what stands behind, the discs' colour, how solid, and the frame as made. Each scaled so its brightest
    // hundredth is white, as the frame is dark before the tube's gain.
    private static int _dumps;
    private static readonly int ShowId = Shader.PropertyToID("_DefocusShow");
    private static void DumpDefocus(RenderTexture frame, RenderTexture made, Material defocus)
    {
        // On F11 (no other mod here binds it), as often as pressed.
        if (!Input.GetKeyDown(KeyCode.F11))
            return;
        _dumps++;
        string folder = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "defocus-dump", _dumps.ToString());
        System.IO.Directory.CreateDirectory(folder);
        Save(frame, System.IO.Path.Combine(folder, "0-frame.png"));
        string[] names = { "", "1-behind.png", "2-disc-colour.png", "3-solid.png" };
        for (int show = 1; show <= 3; show++)
        {
            RenderTexture layer = RenderTexture.GetTemporary(frame.width, frame.height, 0, RenderTextureFormat.ARGBHalf);
            defocus.SetFloat(ShowId, show);
            Graphics.Blit(frame, layer, defocus, 5);
            Save(layer, System.IO.Path.Combine(folder, names[show]), show == 3);
            RenderTexture.ReleaseTemporary(layer);
        }
        defocus.SetFloat(ShowId, 0f);
        Save(made, System.IO.Path.Combine(folder, "4-made.png"));
        NewCameraPlugin.Log?.LogInfo("TEMP DIAG defocus layers saved to " + folder);
    }

    private static void Save(RenderTexture texture, string path, bool asIs = false)
    {
        int w = texture.width, h = texture.height;
        AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBAFloat, r =>
        {
            if (r.hasError)
                return;
            var data = r.GetData<Color>();
            float[] lum = new float[data.Length];
            for (int i = 0; i < data.Length; i++)
                lum[i] = Mathf.Max(data[i].r, Mathf.Max(data[i].g, data[i].b));
            float scale = 1f;
            if (!asIs)
            {
                float[] sorted = (float[])lum.Clone();
                System.Array.Sort(sorted);
                scale = 1f / Mathf.Max(sorted[(int)(sorted.Length * 0.99f)], 1e-6f);
            }
            var pixels = new Color32[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                Color c = data[i] * scale;
                pixels[i] = new Color32((byte)(Mathf.Clamp01(c.r) * 255), (byte)(Mathf.Clamp01(c.g) * 255), (byte)(Mathf.Clamp01(c.b) * 255), 255);
            }
            byte[] png = ImageConversion.EncodeArrayToPNG(pixels, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm, (uint)w, (uint)h);
            System.IO.File.WriteAllBytes(path, png);
        });
    }

    // TEMP DIAG: the discs' radii and how much of the near layer's light they keep, and the frame time, a few times.
    private static float _diagDefocusAt, _diagFrameSum;
    private static int _diagDefocusCount, _diagFrames;
    private static void DiagDefocus(RenderTexture frame, float pixelsPerRadian, float ahead)
    {
        _diagFrameSum += Time.unscaledDeltaTime;
        _diagFrames++;
        if (_diagDefocusCount >= 6 || Time.time - _diagDefocusAt < 3f)
            return;
        float ms = 1000f * _diagFrameSum / Mathf.Max(_diagFrames, 1);
        _diagFrameSum = 0f;
        _diagFrames = 0;
        _diagDefocusAt = Time.time;
        int n = ++_diagDefocusCount;
        string head = $"frame {frame.width}x{frame.height}, mean frame {ms:F1} ms, px/rad {pixelsPerRadian:F0}, objective ahead {ahead:F3} m, most radius {DefocusMostDegrees * Mathf.Deg2Rad * pixelsPerRadian:F1}";
        float cover = 0f, maxR = 0f, sumR = 0f;
        int count = 0;
        AsyncGPUReadback.Request(_levelCoc[0]!, 0, TextureFormat.RGBAFloat, r =>
        {
            if (r.hasError) return;
            var d = r.GetData<Color>();
            for (int i = 0; i < d.Length; i++)
            {
                if (d[i].g <= 0f) continue;
                float radius = d[i].r / d[i].g;
                maxR = Mathf.Max(maxR, radius);
                sumR += radius;
                cover += d[i].g;
                count++;
            }
        });
        float[] kept = new float[DefocusLevels];
        int done = 0;
        for (int level = 0; level < DefocusLevels; level++)
        {
            int l = level, scale = 1 << level;
            AsyncGPUReadback.Request(_levelAcc[level]!, 0, TextureFormat.RGBAFloat, r =>
            {
                if (!r.hasError)
                {
                    var d = r.GetData<Color>();
                    float sum = 0f;
                    for (int i = 0; i < d.Length; i++) sum += d[i].a;
                    kept[l] = sum * scale * scale;
                }
                if (++done < DefocusLevels) return;
                float all = kept[0] + kept[1] + kept[2] + kept[3];
                NewCameraPlugin.Log?.LogInfo($"TEMP DIAG defocus {n}: {head}; near texels {count}, radius mean {(count > 0 ? sumR / count : 0f):F2} max {maxR:F2} (quarter px); light kept {(cover > 0f ? all / cover : 0f):F3} (by level {kept[0]:F0} {kept[1]:F0} {kept[2]:F0} {kept[3]:F0} of {cover:F0})");
            });
        }
    }
}
