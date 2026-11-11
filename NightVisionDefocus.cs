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
    // How many times (each way) a near thing's shape and colour are blurred for its soft outline (F12 头戴式成像装备
    // 近处轮廓模糊遍数): each one more widens it about as the square root of the count.
    internal static int DefocusOutlinePasses = 3;
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
    private static readonly int ShapeWideId = Shader.PropertyToID("_ShapeWide");
    private static readonly int CoverId = Shader.PropertyToID("_Cover");
    private static readonly int CoverWideId = Shader.PropertyToID("_CoverWide");
    private static readonly int FarSoftId = Shader.PropertyToID("_FarSoft");

    // A level blurred wide and smooth (three Gaussians each way), and the same at half its size blurred again: twice as
    // wide.
    private static void Smoothed(RenderTexture source, Material defocus, out RenderTexture soft, out RenderTexture wide)
    {
        soft = Temporary(source.width, source.height);
        RenderTexture across = Temporary(source.width, source.height);
        Graphics.Blit(source, soft);
        Gaussians(soft, across, defocus);
        RenderTexture.ReleaseTemporary(across);
        wide = Temporary(Mathf.Max(soft.width / 2, 1), Mathf.Max(soft.height / 2, 1));
        RenderTexture wideAcross = Temporary(wide.width, wide.height);
        Graphics.Blit(soft, wide, defocus, 7);
        Gaussians(wide, wideAcross, defocus);
        RenderTexture.ReleaseTemporary(wideAcross);
    }

    private static void Gaussians(RenderTexture texture, RenderTexture across, Material defocus)
    {
        for (int pass = 0; pass < DefocusOutlinePasses; pass++)
        {
            defocus.SetVector(StepId, new Vector2(1f, 0f));
            Graphics.Blit(texture, across, defocus, 8);
            defocus.SetVector(StepId, new Vector2(0f, 1f));
            Graphics.Blit(across, texture, defocus, 8);
        }
    }
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

        // The look chosen on the tuner page (_build/tuner.html, its numbers for a 1440-line frame: quarter-size pixels
        // there, scaled here by the frame's height).
        float scale = height / 360f;

        // What is not near, at a quarter of the size and each level half the last: what lies behind a near thing,
        // filled in from about it.
        for (int level = 0; level < DefocusLevels; level++)
        {
            _levelFar[level] = Temporary(Mathf.Max(width >> level, 1), Mathf.Max(height >> level, 1));
            if (level == 0)
                Graphics.Blit(frame, _levelFar[0], defocus, 3);
            else
                Graphics.Blit(_levelFar[level - 1], _levelFar[level], defocus, 7);
            defocus.SetTexture(FarIds[level], _levelFar[level]);
        }

        // The frame and how near each part of it is, at a quarter of the size.
        RenderTexture qColor = Temporary(width, height), qRad = Temporary(width, height);
        Graphics.Blit(frame, qColor, defocus, 9);
        Graphics.Blit(frame, qRad, defocus, 10);
        // How thick each near part is: kept by square openings of 9 to 17 quarter-size pixels' half-width, a fifth each
        // (the tuner's thick = smoothstep((half-width - 8) / 10)).
        RenderTexture nearMask = Threshold(qRad, 0.5f, defocus);
        RenderTexture thick = Temporary(width, height);
        Graphics.Blit(Texture2D.blackTexture, thick);
        foreach (float half in DefocusThickSteps)
        {
            RenderTexture eroded = MinMax(nearMask, half * scale, false, defocus), opened = MinMax(eroded, half * scale, true, defocus);
            RenderTexture.ReleaseTemporary(eroded);
            defocus.SetFloat(ScaleId, 0.2f);
            Graphics.Blit(opened, thick, defocus, 13);
            RenderTexture.ReleaseTemporary(opened);
        }
        // What the near parts close about (5 quarter-size pixels): a sight's window and the gaps between parts.
        RenderTexture grown = MinMax(nearMask, 5f * scale, true, defocus), closed = MinMax(grown, 5f * scale, false, defocus);
        RenderTexture.ReleaseTemporary(grown);
        RenderTexture.ReleaseTemporary(nearMask);

        defocus.SetTexture(QColorId, qColor);
        defocus.SetTexture(QRadId, qRad);
        defocus.SetTexture(ThickId, thick);
        defocus.SetTexture(ClosedId, closed);
        // Thin parts only of what is spread by more than 3 quarter-size pixels (not the targets a few metres off).
        defocus.SetFloat(ThinNearId, 3f * scale);
        RenderTexture layerThick = Layer(0, width, height, defocus), layerThin = Layer(1, width, height, defocus), masks = Layer(2, width, height, defocus);
        RenderTexture thickBlurred = Blur(layerThick, 3f * scale, defocus), masksBlurred = Blur(masks, 3f * scale, defocus);
        RenderTexture thinBlurred = BlurWide(layerThin, 40f * scale, defocus);
        // the thin parts' wash: 40 x 2.5 + 6 quarter-size pixels
        RenderTexture thinWash = BlurWide(layerThin, 106f * scale, defocus), masksWash = BlurWide(masks, 106f * scale, defocus);
        RenderTexture.ReleaseTemporary(layerThick);
        RenderTexture.ReleaseTemporary(layerThin);
        RenderTexture.ReleaseTemporary(masks);
        // About any near thing (one quarter-size pixel out, eased), what lies behind is the filled-in.
        RenderTexture anyNear = Threshold(qRad, 0.02f, defocus), anyGrown = MinMax(anyNear, Mathf.Max(1f, Mathf.Round(scale)), true, defocus);
        RenderTexture seam = Blur(anyGrown, scale, defocus);
        RenderTexture.ReleaseTemporary(anyNear);
        RenderTexture.ReleaseTemporary(anyGrown);

        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        defocus.SetTexture(LThickBId, thickBlurred);
        defocus.SetTexture(LThinBId, thinBlurred);
        defocus.SetTexture(LThinFId, thinWash);
        defocus.SetTexture(MaskBId, masksBlurred);
        defocus.SetTexture(MaskFId, masksWash);
        defocus.SetTexture(SeamId, seam);
        Graphics.Blit(frame, made, defocus, 5);
        DumpDefocus(frame, made, defocus); // TEMP DIAG
        AddTritium(view, made);
        ReleaseMask();
        _blurMask = Temporary(width, height);
        defocus.SetTexture(CoverId, masksBlurred);
        Graphics.Blit(frame, _blurMask, defocus, 6);
        defocus.SetTexture(CoverId, Texture2D.blackTexture);
        foreach (int id in TunerTextureIds)
            defocus.SetTexture(id, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(thickBlurred);
        RenderTexture.ReleaseTemporary(masksBlurred);
        RenderTexture.ReleaseTemporary(thinBlurred);
        RenderTexture.ReleaseTemporary(thinWash);
        RenderTexture.ReleaseTemporary(masksWash);
        RenderTexture.ReleaseTemporary(seam);
        RenderTexture.ReleaseTemporary(qColor);
        RenderTexture.ReleaseTemporary(qRad);
        RenderTexture.ReleaseTemporary(thick);
        RenderTexture.ReleaseTemporary(closed);
        for (int level = 0; level < DefocusLevels; level++)
        {
            defocus.SetTexture(FarIds[level], Texture2D.blackTexture);
            RenderTexture.ReleaseTemporary(_levelFar[level]);
            _levelFar[level] = null;
        }
        if (!_defocusNoted)
        {
            _defocusNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG objective: defocus as chosen on the tuner page (thick 3, thin 40 gone, thin wash 106; {width}x{height} quarter grid, scale {scale:F2}).");
        }
        return true;
    }


    private static readonly float[] DefocusThickSteps = { 9f, 11f, 13f, 15f, 17f };
    private static readonly int QColorId = Shader.PropertyToID("_QColor"), QRadId = Shader.PropertyToID("_QRad"),
        ThickId = Shader.PropertyToID("_Thick"), ClosedId = Shader.PropertyToID("_Closed"), LThickBId = Shader.PropertyToID("_LThickB"),
        LThinBId = Shader.PropertyToID("_LThinB"), LThinFId = Shader.PropertyToID("_LThinF"), MaskBId = Shader.PropertyToID("_MaskB"),
        MaskFId = Shader.PropertyToID("_MaskF"), SeamId = Shader.PropertyToID("_Seam"), RadiusId = Shader.PropertyToID("_Radius"),
        MinMaxModeId = Shader.PropertyToID("_Mode"), SigmaId = Shader.PropertyToID("_Sigma"), WhichId = Shader.PropertyToID("_Which"),
        ThreshId = Shader.PropertyToID("_Thresh"), ScaleId = Shader.PropertyToID("_Scale"), ThinNearId = Shader.PropertyToID("_ThinNear");
    private static readonly int[] TunerTextureIds = { LThickBId, LThinBId, LThinFId, MaskBId, MaskFId, SeamId, QColorId, QRadId, ThickId, ClosedId };

    // 1 where how near (green) is above the threshold.
    private static RenderTexture Threshold(RenderTexture source, float threshold, Material defocus)
    {
        RenderTexture result = Temporary(source.width, source.height);
        defocus.SetFloat(ThreshId, threshold);
        Graphics.Blit(source, result, defocus, 12);
        return result;
    }

    // A square min (or max) of red, `radius` texels each way.
    private static RenderTexture MinMax(RenderTexture source, float radius, bool max, Material defocus)
    {
        RenderTexture across = Temporary(source.width, source.height), result = Temporary(source.width, source.height);
        defocus.SetFloat(RadiusId, radius);
        defocus.SetFloat(MinMaxModeId, max ? 1f : 0f);
        defocus.SetVector(StepId, new Vector2(1f, 0f));
        Graphics.Blit(source, across, defocus, 11);
        defocus.SetVector(StepId, new Vector2(0f, 1f));
        Graphics.Blit(across, result, defocus, 11);
        RenderTexture.ReleaseTemporary(across);
        return result;
    }

    private static RenderTexture Layer(int which, int width, int height, Material defocus)
    {
        RenderTexture result = Temporary(width, height);
        defocus.SetFloat(WhichId, which);
        Graphics.Blit(Texture2D.blackTexture, result, defocus, 14);
        return result;
    }

    // A Gaussian of `sigma` texels.
    private static RenderTexture Blur(RenderTexture source, float sigma, Material defocus)
    {
        RenderTexture across = Temporary(source.width, source.height), result = Temporary(source.width, source.height);
        defocus.SetFloat(SigmaId, Mathf.Max(sigma, 0.3f));
        defocus.SetVector(StepId, new Vector2(1f, 0f));
        Graphics.Blit(source, across, defocus, 15);
        defocus.SetVector(StepId, new Vector2(0f, 1f));
        Graphics.Blit(across, result, defocus, 15);
        RenderTexture.ReleaseTemporary(across);
        return result;
    }

    // A wide Gaussian (`sigma` quarter-size pixels): halved until it is a dozen texels or less, then blurred.
    private static RenderTexture BlurWide(RenderTexture source, float sigma, Material defocus)
    {
        RenderTexture current = source;
        while (sigma > 12f && current.width > 8 && current.height > 8)
        {
            RenderTexture half = Temporary(Mathf.Max(current.width / 2, 1), Mathf.Max(current.height / 2, 1));
            Graphics.Blit(current, half, defocus, 7);
            if (current != source)
                RenderTexture.ReleaseTemporary(current);
            current = half;
            sigma /= 2f;
        }
        RenderTexture result = Blur(current, sigma, defocus);
        if (current != source)
            RenderTexture.ReleaseTemporary(current);
        return result;
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
        string[] names = { "", "1-behind.png", "2-near-colour.png", "3-shown.png", "5-radius.png" };
        for (int show = 1; show <= 4; show++)
        {
            RenderTexture layer = RenderTexture.GetTemporary(frame.width, frame.height, 0, RenderTextureFormat.ARGBHalf);
            defocus.SetFloat(ShowId, show);
            Graphics.Blit(frame, layer, defocus, 5);
            Save(layer, System.IO.Path.Combine(folder, names[show]), show >= 3);
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
