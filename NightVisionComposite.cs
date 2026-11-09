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
//
// 目镜成像流水线, each frame (with a magnified sight looked through with the goggle down, its lens's part in each step;
// 倍镜状态 _scope, NightVisionScope.cs, is the only place that says whether, where and how bright):
//   Before any camera renders  ScopePull: _scope.Through, the camera to the goggle's objective.
//   The sight's camera         IR lights through the goggle or its own night vision (OpticSeesNightVision); IR lasers
//                              drawn by us, visible ones at their own light, unless its picture is its own night-vision
//                              one (OpticOwnNightVision: the game's _NightVisionOn, NightVisionIr.cs); what is near in it blurred by its own objective and its
//                              vials' discs laid on (NightVisionScopeTritium.cs). The game lays it on the sight's lens.
//   The main camera, before    NoteScope: _scope's lens and Light. The IR layer (IR lights and IR lasers), drawn from
//                              the eyes, sight or no sight (NightVisionIr.cs).
//   Its image effect (here, EyepieceRenderImage):
//     1 ScopeLensStep          the lens's mask (NightVisionObjective.cs, DrawScopeLens).
//     2 ForTube                the light that reaches the tube (NvgIrLight.shader pass 1): out of the lens, the frame
//                              with the IR layer added (visible lasers in it at their own light, as everything the tube
//                              amplifies); in it, the sight's picture at _scope.Light, and nothing past the sight. The one
//                              place the sight's light is cut, whatever else is switched on.
//     3 ThroughObjective       the objective's blur of what is near (NvgObjective.shader): the lens sharp, the
//                              eyepiece's rim blurred over its edge, the lens's picture kept out of what lies behind
//                              the rim; the frame's vials' discs (pass 8: none through the sight's tube).
//     4 LensPicture            the tube: BRNVG's, the game's (NvgTube.shader's gain first), or the thermal camera's.
//     5 EaseGrain              the tube's grain eased where the frame was blurred.
//     6 Compose                the eyepieces laid over the naked eye's frame (NvgBinocular.shader).
internal static partial class NightVisionEyepieces
{
    internal const string PictureAuto = "自动", PictureGame = "原版";
    // BRNVG's picture, by its full name ("[未安装]" after it when it is not installed).
    internal static readonly string PictureBrnvg = OtherMods.Label(OtherMods.BrnvgGuid, OtherMods.BrnvgName);

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
            RenderTexture? made = null, tubed = null, focused = null;
            // The steps at the head of this file, in order.
            ScopeLensStep(camera, from);
            Texture picture = LensPicture(camera, ThroughObjective(camera, ForTube(from, ref tubed), ref focused), ref made);
            picture = EaseGrain(picture, ref made);
            ReleaseMask();
            Compose(composite, glow, from, into, picture);
            if (made != null)
                RenderTexture.ReleaseTemporary(made);
            if (tubed != null)
                RenderTexture.ReleaseTemporary(tubed);
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
        // The tube's gain and its pressing of the bright end, before the game's filter (NvgTube.shader); only this
        // picture, the game's: BRNVG's above is its own, set in its own settings.
        Material? tube = TubeMaterial();
        if (tube == null)
        {
            Graphics.Blit(frame, made, filter);
            return made;
        }
        tube.SetFloat(TubeGainId, GameLensGain);
        tube.SetFloat(TubeCompressId, GameLensCompress);
        RenderTexture tubed = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        Graphics.Blit(frame, tubed, tube);
        Graphics.Blit(tubed, made, filter);
        RenderTexture.ReleaseTemporary(tubed);
        return made;
    }

    // The game's own night-vision picture in the lenses (F12 原版夜视目镜增益, 原版夜视目镜亮处压缩): its light times the
    // gain, the bright end pressed toward the tube's ceiling by the compression. The frame is the naked eye's, lit by the
    // level's own ambient (NightVisionPreCullPostfix): what is dark stays dark.
    internal static float GameLensGain = 1f;
    internal static float GameLensCompress;
    private static Shader? _tubeShader;
    private static Material? _tubeMaterial;
    private static readonly int TubeGainId = Shader.PropertyToID("_TubeGain");
    private static readonly int TubeCompressId = Shader.PropertyToID("_TubeCompress");

    // Null while there is nothing to change (gain 1, no compression), or without the shader.
    private static Material? TubeMaterial()
    {
        if (Mathf.Approximately(GameLensGain, 1f) && GameLensCompress <= 0f || _tubeShader == null)
            return null;
        return _tubeMaterial ??= new Material(_tubeShader) { name = "Justachae NVG tube", hideFlags = HideFlags.DontSave };
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
