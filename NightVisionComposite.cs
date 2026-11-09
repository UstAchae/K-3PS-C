using System;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace EFTNewCamera;

// The eyepieces put into the frame, one way whatever the picture: an image effect of ours on the FPS camera takes the
// frame from the SSAA stage (as BRNVG's renderer takes it), and lays the eyes' views and the lenses over it. What the
// lenses show is the one thing that differs: BRNVG's picture (its renderer's own effect, with an open lens), the
// game's (its night-vision material, the one it would lay over the whole frame), thermal goggles' (our thermal
// camera's), or black while the device is off or flipping. Meanwhile the game's whole-frame pass and its mask stay off
// (NightVisionPreCullPostfix). With BRNVG's renderer on the camera the eyepieces are put in in its place
// (RenderImagePrefix) and ours passes the frame on: one copy of the frame, not two (a second copy through the SSAA
// stage's buffers left the lenses' picture about a third as bright).
// The image effect is put on as the camera wakes, so that it comes in the camera's chain of image effects before the
// SSAA stage hands the frame to the screen; one put on later comes after that, and what it draws never reaches the
// screen (the picture stops). A camera without it keeps the game's own filter: the eyepieces do not take over.
internal static partial class NightVisionEyepieces
{
    internal const string PictureAuto = "自动", PictureBrnvg = "BRNVG", PictureGame = "原版";

    internal static string Picture = PictureAuto;

    // BRNVG's picture: chosen (or left to choice) and BRNVG there.
    private static bool UseBrnvg => Picture != PictureGame && BodyVisibility.NightRendererType != null;

    private static EyepieceImage? _eyepieceImage;

    private static void InstallEyepieceImage(HarmonyLib.Harmony harmony)
    {
        System.Reflection.MethodInfo? awake = HarmonyLib.AccessTools.Method(typeof(BSG.CameraEffects.NightVision), "Awake");
        System.Reflection.MethodInfo? postfix = typeof(NightVisionEyepieces).GetMethod(nameof(NightVisionAwakePostfix),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (awake != null && postfix != null)
            harmony.Patch(awake, postfix: new HarmonyLib.HarmonyMethod(postfix));
    }

    // The FPS camera (the one with SSAA) waking: our image effect on it, off until wanted.
    private static void NightVisionAwakePostfix(BSG.CameraEffects.NightVision __instance)
    {
        if (__instance.GetComponent<SSAA>() == null || __instance.GetComponent<EyepieceImage>() != null)
            return;
        __instance.gameObject.AddComponent<EyepieceImage>().enabled = false;
    }

    // The eyepieces can be put in: our image effect was put on the FPS camera as it woke.
    private static bool EyepieceImageReady(CameraManager? cameras)
    {
        return cameras?.NightVision != null && cameras.NightVision.GetComponent<EyepieceImage>() != null;
    }

    // The image effect on while the eyes may show (the device on or flipping), off otherwise.
    private static void KeepEyepieceImage(CameraManager? cameras, bool shown)
    {
        BSG.CameraEffects.NightVision? nightVision = shown ? cameras?.NightVision : null;
        if (_eyepieceImage != null && (nightVision == null || _eyepieceImage.gameObject != nightVision.gameObject))
        {
            _eyepieceImage.enabled = false;
            if (nightVision != null)
                _eyepieceImage = null;
        }
        if (nightVision == null)
            return;
        // Unity's own test: the FPS camera is made anew each raid, and the last raid's effect is gone with it.
        if (_eyepieceImage == null)
            _eyepieceImage = nightVision.GetComponent<EyepieceImage>();
        if (_eyepieceImage != null && !_eyepieceImage.enabled)
        {
            _eyepieceImage.enabled = true;
        }
    }

    // Ours on the FPS camera: it puts the eyepieces in unless BRNVG's renderer, further on, does (RenderImagePrefix).
    internal static void EyepieceImageRender(Component camera, RenderTexture source, RenderTexture destination)
    {
        if (EyepieceRenderer(camera) != null)
            PassThrough(camera, source, destination);
        else
            EyepieceRenderImage(camera, source, destination);
    }

    // BRNVG's renderer on this camera, when it is the one putting the eyepieces in (RenderImagePrefix).
    internal static Behaviour? EyepieceRenderer(Component camera)
    {
        CameraManager? cameras = BodyVisibility.Cameras;
        Behaviour? brnvg = NightRenderer(cameras?.NightVision);
        return brnvg != null && brnvg.isActiveAndEnabled && brnvg.gameObject == camera.gameObject && _body != null
            && EyepieceImageReady(cameras) ? brnvg : null;
    }

    private static void PassThrough(Component camera, RenderTexture source, RenderTexture destination)
    {
        SSAAPropagator? propagator = camera.GetComponent<SSAAPropagator>();
        RenderTexture from = source, to = destination;
        bool staged = propagator != null && propagator.GetSourceDestination(out from, out to);
        if (!staged)
        {
            from = source;
            to = destination;
        }
        Graphics.Blit(from, to);
        if (staged)
            propagator!.ReleaseSourceDestination(from, to);
    }

    // lens: the fisheye laid over the frame after the eyepieces (it comes before this in the chain).
    internal static void EyepieceRenderImage(Component camera, RenderTexture source, RenderTexture destination, bool lens = false)
    {
        Material? composite = _binocularMaterial, glow = _glowMaterial;
        SSAAPropagator? propagator = camera.GetComponent<SSAAPropagator>();
        RenderTexture from = source, to = destination;
        bool staged = propagator != null && propagator.GetSourceDestination(out from, out to);
        if (!staged)
        {
            from = source;
            to = destination;
        }

        RenderTexture? lensed = lens ? RenderTexture.GetTemporary(from.width, from.height, 0, from.format) : null;
        RenderTexture into = lensed != null ? lensed : to;
        bool eyes = _eyesFrame == Time.frameCount;
        if (composite == null || glow == null || _body == null || !eyes)
            Graphics.Blit(from, into);
        else
        {
            RenderTexture? made = null, withIr = null, focused = null;
            // The lenses see IR, the naked eye around them does not; and they see it through the objective.
            RenderTexture seenFrame = WithIr(from, ref withIr), through = ThroughObjective(camera, seenFrame, ref focused);
            Texture picture = LensPicture(camera, through, ref made);
            picture = EaseGrain(picture, ref made);
            ReleaseMask();
            Compose(composite, glow, from, into, picture);
            KeepCompositeOnce(from, through, picture, into);
            if (made != null)
                RenderTexture.ReleaseTemporary(made);
            if (withIr != null)
                RenderTexture.ReleaseTemporary(withIr);
            if (focused != null)
                RenderTexture.ReleaseTemporary(focused);
        }
        if (lensed != null)
        {
            Fisheye.Blit(lensed, to);
            RenderTexture.ReleaseTemporary(lensed);
        }
        if (staged)
            propagator!.ReleaseSourceDestination(from, to);
    }

    // Temporary: once in a raid, the device down and on for a while, one frame of each step the lenses' picture comes by, to work out
    // where the dark place between the lenses comes from.
    private const int KeepAfterFrames = 300;
    private static int _keepFrames;
    private static Transform? _keptFor;

    private static void KeepCompositeOnce(RenderTexture frame, Texture through, Texture picture, RenderTexture final)
    {
        if (!Active || _flip < 0.999f || !_direct)
        {
            _keepFrames = 0;
            return;
        }
        if (ReferenceEquals(_keptFor, _body) || ++_keepFrames != KeepAfterFrames)
            return;
        _keptFor = _body;
        string folder = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, ".codex-work", "nvgprobe");
        System.IO.Directory.CreateDirectory(folder);
        Log($"NVG keep: split {_binocularMaterial!.GetFloat(SplitId):F3}, overscan {_binocularMaterial.GetFloat(OverscanId):F3}, shift {EyeShift}, frame {frame.width}x{frame.height}, format {frame.format}");
        void Keep(Texture from, string name)
        {
            var copy = new RenderTexture(frame.width, frame.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "Justachae NVG keep", filterMode = FilterMode.Point };
            Graphics.Blit(from, copy);
            string file = System.IO.Path.Combine(folder, $"{name}_{copy.width}_{copy.height}.bin");
            UnityEngine.Rendering.AsyncGPUReadback.Request(copy, 0, request =>
            {
                if (request.hasError)
                    Log($"NVG keep: {name} failed");
                else
                    System.IO.File.WriteAllBytes(file, request.GetData<byte>().ToArray());
                copy.Release();
                UnityEngine.Object.Destroy(copy);
            });
        }
        RenderTexture active = RenderTexture.active;
        Keep(frame, "frame");
        Keep(through, "through");
        Keep(picture, "picture");
        Keep(final, "final");
        for (int side = 0; side < 2; side++)
        {
            Keep(_lensMaps[side]!, side == 0 ? "lensL" : "lensR");
            Keep(_glassMaps[side]!, side == 0 ? "glassL" : "glassR");
        }
        RenderTexture.active = active;
    }

    // What the lenses show this frame, from the frame as it stands: made into a temporary (made) when it is made here.
    private static Texture LensPicture(Component camera, RenderTexture frame, ref RenderTexture? made)
    {
        if (!Active)
            return Texture2D.blackTexture;
        if (_device.Thermal)
            return _thermalPicture != null ? _thermalPicture : Texture2D.blackTexture;

        Behaviour? brnvg = UseBrnvg ? NightRenderer(BodyVisibility.Cameras?.NightVision) : null;
        if (brnvg != null && _renderEffect != null)
        {
            made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
            OpenLens(brnvg, true);
            try
            {
                _renderEffect.Invoke(brnvg, new object[] { frame, made });
            }
            finally
            {
                OpenLens(brnvg, false);
            }
            return made;
        }

        BSG.CameraEffects.NightVision? nightVision = camera.GetComponent<BSG.CameraEffects.NightVision>();
        if (nightVision == null)
            return Texture2D.blackTexture;
        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        Material filter = nightVision.Material;
        filter.EnableKeyword("NIGHT_VISION_NOISE");
        Graphics.Blit(frame, made, filter);
        return made;
    }

    // The eyepieces put together over the frame (from) into to, with picture as what the lenses show.
    private static void Compose(Material composite, Material glow, RenderTexture from, RenderTexture to, Texture picture)
    {
        composite.SetFloat(ModeId, 1f);
        composite.SetTexture(NightVisionId, picture);
        // The glow worked out only when it is shown.
        RenderTexture? lit = EyepieceGlow ? Glow(composite, glow, from) : null;
        composite.SetTexture(GlowId, lit != null ? lit : Texture2D.blackTexture);
        Graphics.Blit(from, to, composite);
        composite.SetTexture(NightVisionId, Texture2D.blackTexture);
        composite.SetTexture(GlowId, Texture2D.blackTexture);
        if (lit != null)
            RenderTexture.ReleaseTemporary(lit);
    }
}

// Ours, on the FPS camera: the eyepieces into the frame.
internal sealed class EyepieceImage : MonoBehaviour
{
    private void OnRenderImage(RenderTexture source, RenderTexture destination) => NightVisionEyepieces.EyepieceImageRender(this, source, destination);
}
