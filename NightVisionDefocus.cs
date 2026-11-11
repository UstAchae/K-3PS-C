using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// The objective's defocus (step 3 of the eyepieces' picture, NightVisionComposite.cs). A point at a distance d from a
// lens of aperture D focused at s is spread over a disc of even light, whose diameter seen from the lens is
// D·|1/d − 1/s|; nearer, it eases toward DefocusMostDegrees instead of growing without end, so the arms and the gun stay
// things seen, softly, the nearer the softer. At a quarter of the frame's size (NvgDefocus.shader): how near each part
// is, from the depth's own texels at the points they show this frame (the camera is jittered, its picture smoothed over
// it, its depth not) and gathered over the frames; the near parts blurred in bands of their radius, each as much as it
// is near; shown over what lies behind them by their share against it, as a table behind them would count; what lies
// behind them filled in from about them. A magnified sight's lens and the ring of its eyepiece are left as they are.
internal static partial class NightVisionEyepieces
{
    private const float DefocusAperture = 0.0225f;      // the objective's, 27 mm at f/1.2 (as the tritium's)
    private const float DefocusMostDegrees = 3f;        // what a disc's radius eases toward, nearer and nearer
    private const int DefocusScale = 4;
    private const int DefocusLevels = 4;                // the sizes of what is filled in behind a near thing

    private static Shader? _defocusShader;
    private static Material? _defocus;
    private static readonly RenderTexture?[] _levelFar = new RenderTexture?[DefocusLevels];
    private static bool _defocusNoted;

    private static readonly int DefocusLensId = Shader.PropertyToID("_DefocusLens");
    private static readonly int CoverId = Shader.PropertyToID("_Cover");
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
        defocus.SetVector(DepthTexelId, new Vector4(1f / frame.width, 1f / frame.height, frame.width, frame.height));
        // Where in the depth the picture's (unjittered) point is: a point at p in NDC is drawn at p - jitter, half that
        // in UV. Without it each near outline in the depth walked about the picture's from frame to frame.
        Texture? depthTexture = Shader.GetGlobalTexture(CameraDepthId);
        int depthWidth = depthTexture != null ? depthTexture.width : frame.width, depthHeight = depthTexture != null ? depthTexture.height : frame.height;
        defocus.SetVector(DepthSizeId, new Vector4(depthWidth, depthHeight, 1f / depthWidth, 1f / depthHeight));
        defocus.SetVector(DepthJitterId, new Vector4(-0.5f * _renderedJitter.x, -0.5f * _renderedJitter.y, 0f, 0f));
        // the frame's width over its height, and how far about a magnified sight's lens its eyepiece's own dark ring
        // reaches (of the frame's height): neither blurred
        defocus.SetFloat(AspectId, (float)frame.width / frame.height);
        defocus.SetFloat(ScopeMarginId, 0.04f);
        // A magnified sight's lens mask at a quarter of the size, blurred over that margin: what of the frame is the
        // sight's own, eased (its edge stepped, and differently each frame).
        RenderTexture? scopeSoft = null;
        if (ScopeMasked)
        {
            RenderTexture quarterLens = Temporary(width, height);
            Graphics.Blit(_scopeLens!, quarterLens);
            scopeSoft = Blur(quarterLens, 0.04f * height * 0.5f, defocus);
            RenderTexture.ReleaseTemporary(quarterLens);
        }
        defocus.SetTexture(ScopeSoftId, scopeSoft != null ? scopeSoft : Texture2D.blackTexture);
        BindScope(defocus);

        // Its sizes are in quarter-size pixels as they were set: at 386 of them a radian (a 1440-line frame at the game's
        // field). Scaled by this frame's pixels a radian, not its height alone: with the fisheye on, the frame is rendered
        // over a far wider field, the gun is fewer pixels across while each blur stayed as many, and its dark spread was
        // the wider about it.
        float scale = pixelsPerRadian / DefocusPixelsPerRadianSet;

        // The frame and how near each part of it is, at a quarter of the size.
        RenderTexture qColor = Temporary(width, height), qRad = Temporary(width, height);
        Graphics.Blit(frame, qColor, defocus, 9);
        Graphics.Blit(frame, qRad, defocus, 10);
        // How near, gathered over the frames as the game gathers its picture: the game's camera is jittered by part of a
        // pixel of its render on every frame in a long cycle, and its picture smoothed over them, but not its depth: a
        // thin part (a sight's ring, two or three texels of the depth) covered more texels or fewer as the jitter went,
        // and its blur pulsed while the picture stood still (raw floats of four frames, 2026-11-11). A quarter of this
        // frame's is taken each frame, the rest the gathered one held within what this frame has about each pixel (its
        // three by three): what moves is followed at once, only what stands still is gathered.
        qRad = Gathered(qRad, defocus);
        // What is not near (by the nearness just made, not each pixel's own depth taken at the jittered texel nearest
        // it), for what is filled in behind a near thing: each a Gaussian of the quarter-size one (1.5, 4, 10 and 24
        // quarter-size pixels), not halvings, whose pixels jumped as the gun swayed with the breath.
        defocus.SetTexture(QRadId, qRad);
        RenderTexture farQuarter = Temporary(width, height);
        Graphics.Blit(frame, farQuarter, defocus, 3);
        for (int level = 0; level < DefocusLevels; level++)
        {
            _levelFar[level] = BlurWide(farQuarter, DefocusFillSigmas[level] * scale, defocus);
            defocus.SetTexture(FarIds[level], _levelFar[level]);
        }
        RenderTexture.ReleaseTemporary(farQuarter);
        // How near, as it is (no line drawn at a threshold: such a line moved from frame to frame).
        RenderTexture nearSoft = Threshold(qRad, -1f, defocus);
        // What the near parts close about (5 quarter-size pixels): a sight's window and the gaps between parts.
        RenderTexture grown = MinMax(nearSoft, 5f * scale, true, defocus), closed = MinMax(grown, 5f * scale, false, defocus);
        RenderTexture.ReleaseTemporary(grown);

        defocus.SetTexture(QColorId, qColor);
        defocus.SetTexture(QRadId, qRad);
        defocus.SetTexture(ClosedId, closed);
        // Thin parts taken as the thick ones, everywhere: with a table behind it a sight's thin parts joined the table
        // into one thick near shape and showed full and soft, the look wanted; measured thin against a far scene, they
        // went into it (transparent), and flipped as the gun swung across the edge of what is near. Nothing passes the
        // thin parts' gate, and their layers stay empty.
        defocus.SetTexture(ThickId, Texture2D.blackTexture);
        defocus.SetFloat(ThinNearId, 1e6f);
        // Each near point blurred as much as it is near: in bands of its disc's radius (their shares easing from band to
        // band), each band by DefocusBlurPerRadius of its radius, so the blur grows on from what is just near to the
        // nearest, with no step where what is near begins. One blur for all (the A version's 8) made what was only a
        // little near as soft as the gun.
        RenderTexture thickBlurred = Temporary(width, height), masksBlurred = Temporary(width, height);
        Graphics.Blit(Texture2D.blackTexture, thickBlurred);
        Graphics.Blit(Texture2D.blackTexture, masksBlurred);
        for (int band = 0; band < DefocusBandMiddles.Length; band++)
        {
            float middle = DefocusBandMiddles[band] * scale;
            float below = band == 0 ? -1f : DefocusBandMiddles[band - 1] * scale;
            float above = band == DefocusBandMiddles.Length - 1 ? 1e6f : DefocusBandMiddles[band + 1] * scale;
            defocus.SetVector(BandHatId, new Vector3(below, middle, above));
            defocus.SetFloat(EncInId, band == DefocusEncBand ? 1f : 0f);
            RenderTexture layer = Layer(0, width, height, defocus), mask = Layer(2, width, height, defocus);
            // at least a quarter-size pixel: less, a band was shown at a quarter of the size all but unblurred, and moved
            // in its pixels' steps
            float sigma = Mathf.Max(DefocusBlurPerRadius * middle, scale);
            RenderTexture layerBlurred = Blur(layer, sigma, defocus), maskBlurred = Blur(mask, sigma, defocus);
            Graphics.Blit(layerBlurred, thickBlurred, defocus, 16);
            Graphics.Blit(maskBlurred, masksBlurred, defocus, 16);
            RenderTexture.ReleaseTemporary(layer);
            RenderTexture.ReleaseTemporary(mask);
            RenderTexture.ReleaseTemporary(layerBlurred);
            RenderTexture.ReleaseTemporary(maskBlurred);
        }
        // About any near thing (one quarter-size pixel out, eased), what lies behind is the filled-in.
        RenderTexture anyGrown = MinMax(nearSoft, Mathf.Max(1f, Mathf.Round(scale)), true, defocus);
        RenderTexture seam = Blur(anyGrown, scale, defocus);
        RenderTexture.ReleaseTemporary(anyGrown);
        RenderTexture.ReleaseTemporary(nearSoft);

        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        defocus.SetTexture(LThickBId, thickBlurred);
        defocus.SetTexture(LThinBId, Texture2D.blackTexture);
        defocus.SetTexture(LThinFId, Texture2D.blackTexture);
        defocus.SetTexture(MaskBId, masksBlurred);
        RenderTexture thickWide = Blur(thickBlurred, DefocusShareSigma * scale, defocus), masksWide = Blur(masksBlurred, DefocusShareSigma * scale, defocus);
        defocus.SetTexture(LThickWId, thickWide);
        defocus.SetTexture(MaskWId, masksWide);
        defocus.SetTexture(MaskFId, Texture2D.blackTexture);
        defocus.SetTexture(SeamId, seam);
        Graphics.Blit(frame, made, defocus, 5);
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
        defocus.SetTexture(LThickWId, Texture2D.blackTexture);
        defocus.SetTexture(MaskWId, Texture2D.blackTexture);
        RenderTexture.ReleaseTemporary(thickWide);
        RenderTexture.ReleaseTemporary(masksWide);
        RenderTexture.ReleaseTemporary(seam);
        RenderTexture.ReleaseTemporary(qColor);
        RenderTexture.ReleaseTemporary(qRad);
        RenderTexture.ReleaseTemporary(closed);
        defocus.SetTexture(ScopeSoftId, Texture2D.blackTexture);
        if (scopeSoft != null)
            RenderTexture.ReleaseTemporary(scopeSoft);
        for (int level = 0; level < DefocusLevels; level++)
        {
            defocus.SetTexture(FarIds[level], Texture2D.blackTexture);
            RenderTexture.ReleaseTemporary(_levelFar[level]);
            _levelFar[level] = null;
        }
        if (!_defocusNoted)
        {
            _defocusNoted = true;
            NewCameraPlugin.Log?.LogInfo($"NVG objective: defocus by bands of nearness (each blurred 0.35 x its radius, shown as against a table), depth jitter-corrected and gathered over frames ({width}x{height} quarter grid, scale {scale:F2}).");
        }
        return true;
    }


    // Bands of a disc's radius (quarter-size pixels at 1440 lines; the most is 20.2) and how much each is blurred per
    // quarter-size pixel of it: the nearest by 0.35 × 20 = 7, a thing spread by 2.5 by under one.
    private static readonly float[] DefocusBandMiddles = { 1f, 2.5f, 4.5f, 7f, 10.5f, 15f, 20.2f };
    private const int DefocusEncBand = 4;   // what the near parts close about goes with the gun's own (10.5)
    private const float DefocusBlurPerRadius = 0.35f;
    private const float DefocusPixelsPerRadianSet = 386f;
    // How much wider (quarter-size pixels) the layers are blurred again for the share they show by (as against a table).
    private const float DefocusShareSigma = 5f;
    private static RenderTexture? _gatheredQRad;
    private static readonly int HistoryId = Shader.PropertyToID("_History"), TakeId = Shader.PropertyToID("_Take"), TolId = Shader.PropertyToID("_Tol");
    private const float DefocusTake = 0.35f;
    private static RenderTexture Gathered(RenderTexture current, Material defocus)
    {
        if (_gatheredQRad == null || _gatheredQRad.width != current.width || _gatheredQRad.height != current.height)
        {
            _gatheredQRad?.Release();
            _gatheredQRad = new RenderTexture(current.width, current.height, 0, RenderTextureFormat.ARGBHalf) { name = "Justachae NVG defocus gathered nearness", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _gatheredQRad.Create();
            Graphics.Blit(current, _gatheredQRad);
        }
        RenderTexture gathered = Temporary(current.width, current.height);
        defocus.SetTexture(HistoryId, _gatheredQRad);
        defocus.SetFloat(TakeId, DefocusTake);
        // how far the jitter moves each (radius, nearness, nearness by Front) at a thin part (raw floats, 2026-11-11: near
        // changed by up to 0.12 a frame where the picture stood still)
        defocus.SetVector(TolId, new Vector4(1.5f, 0.12f, 0.06f, 1f));
        Graphics.Blit(current, gathered, defocus, 18);
        defocus.SetTexture(HistoryId, Texture2D.blackTexture);
        Graphics.Blit(gathered, _gatheredQRad);
        RenderTexture.ReleaseTemporary(current);
        return gathered;
    }
    private static readonly int DepthTexelId = Shader.PropertyToID("_DepthTexel"), DepthJitterId = Shader.PropertyToID("_DepthJitter"),
        DepthSizeId = Shader.PropertyToID("_DepthSize"), CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
    private static readonly int LThickWId = Shader.PropertyToID("_LThickW"), MaskWId = Shader.PropertyToID("_MaskW");
    private static readonly float[] DefocusFillSigmas = { 1.5f, 4f, 10f, 24f };
    private static readonly int BandHatId = Shader.PropertyToID("_BandHat"), EncInId = Shader.PropertyToID("_EncIn"),
        AspectId = Shader.PropertyToID("_Aspect"), ScopeMarginId = Shader.PropertyToID("_ScopeMargin"),
        ScopeSoftId = Shader.PropertyToID("_ScopeSoft");
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
}
