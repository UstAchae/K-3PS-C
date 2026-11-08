using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// A fisheye lens on the view. The FPS camera renders straight (rectilinear) with a field wide enough for the fisheye's
// corners, and an image effect of ours lays that frame out as the lens would, after the SSAA stage has upscaled it
// (Justachae/Fisheye). The horizontal field across the screen is the one set. It is on the whole frame, the optic and
// the eyepieces in it: whatever is drawn for the frame (the eyes' cameras, the thermal camera, the reticles) takes the
// FPS camera's field as it renders, so it is laid out straight at the wide field like the rest, and bent with it.
// The game's field is taken as it stands after its own frame (its zoom eases in coroutines, from the camera's field)
// and handed back at the end of the frame, so the game never sees ours.
// The fisheye magnifies the centre of the straight frame, which would leave it soft. Light on the card, the frame is
// read sharper there (Justachae/Fisheye). For the centre's own detail (HighResolution), the frame is made that much
// larger: the SSAA stage's buffers, which every image effect works in up to the last (SSAAImpl, drawing them onto the
// screen), are made that many times the screen, and the camera renders that many times as many pixels (the game's
// supersampling ratio, or its upscaler's render scale, multiplied); the upscalers make their picture at the size
// they are handed. The centre then keeps a pixel of its own for each pixel on the screen.
internal static class Fisheye
{
    internal static bool Enabled;
    internal static float Field = 120f;   // degrees, across the screen
    internal static float Curve = 0.4f;    // 0 the straight picture at that field, 1 the stereographic fisheye
    internal static float Vignette = 0.6f; // 0 none, 1 the corners well into the lens barrel's dark
    internal static bool HighResolution;    // the frame rendered larger for the centre's detail (else read sharper)
    internal static float Detail = 1f;     // the centre's magnification rendered back: 0 none, 1 all; above, that many
                                           // rendered pixels each way for each pixel on the screen there (supersampling)
    private const float HalvedAbove = 2f;
    private const float FadeSeconds = 0.25f;
    private const float MaxCornerDegrees = 85f;

    private static float _scale = 1f;       // the frame's size over the screen's, each way
    private static SSAA? _ssaa;             // the FPS camera's, and the ratio the game last set on it
    private static float _gameRatio = 1f;

    private static float _weight;          // 0 the straight picture, 1 the whole fisheye
    private static Camera? _widened;       // the camera given our field this frame
    private static float _gameField;       // its field as the game had it
    private static float _widenedField;    // ours on it
    private static Vector4 _shape;         // k, f, the straight frame's half height, aspect (Justachae/Fisheye)
    private static Vector4 _shade;         // the image circle's radius and soft edge, the light's falloff
    private static Camera? _imageCamera;
    private static FisheyeImage? _image;
    private static Material? _material;
    private static bool _installed;
    private static readonly int ShapeId = Shader.PropertyToID("_Fisheye");
    private static readonly int ShadeId = Shader.PropertyToID("_FisheyeShade");

    internal static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".fisheye");
        System.Reflection.MethodInfo? awake = AccessTools.Method(typeof(BSG.CameraEffects.NightVision), "Awake");
        if (awake == null)
        {
            NewCameraPlugin.Log?.LogWarning("Not patched (fisheye): the FPS camera's night vision has no Awake.");
            return;
        }
        // After the eyepieces' own image effect, so the lens takes the frame with them in it.
        harmony.Patch(awake, postfix: new HarmonyMethod(AccessTools.Method(typeof(Fisheye), nameof(NightVisionAwakePostfix))) { priority = Priority.Low });
        PatchOrLog(harmony, AccessTools.Method(typeof(SSAA), nameof(SSAA.Switch)), prefix: new HarmonyMethod(AccessTools.Method(typeof(Fisheye), nameof(SwitchPrefix))));
        HarmonyMethod screen = new HarmonyMethod(AccessTools.Method(typeof(Fisheye), nameof(FrameSize)));
        PatchOrLog(harmony, AccessTools.Method(typeof(SSAAPropagator), "OnRenderImage"), transpiler: screen);
        PatchOrLog(harmony, AccessTools.Method(typeof(SSAAPropagator), nameof(SSAAPropagator.FillCommandBuffer)), transpiler: screen);
    }

    private static void PatchOrLog(Harmony harmony, System.Reflection.MethodInfo? target, HarmonyMethod? prefix = null, HarmonyMethod? transpiler = null)
    {
        if (target == null)
        {
            NewCameraPlugin.Log?.LogWarning("Not patched (fisheye detail): a method of the SSAA stage was not found.");
            return;
        }
        harmony.Patch(target, prefix: prefix, transpiler: transpiler);
    }

    // The SSAA stage sizes its buffers from the screen: the frame's size instead.
    private static IEnumerable<CodeInstruction> FrameSize(IEnumerable<CodeInstruction> instructions)
    {
        System.Reflection.MethodInfo width = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.width));
        System.Reflection.MethodInfo height = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.height));
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(width))
                instruction.operand = AccessTools.Method(typeof(Fisheye), nameof(FrameWidth));
            else if (instruction.Calls(height))
                instruction.operand = AccessTools.Method(typeof(Fisheye), nameof(FrameHeight));
            yield return instruction;
        }
    }

    private static int FrameWidth() => Mathf.Max(1, Mathf.RoundToInt(Screen.width * _scale));

    private static int FrameHeight() => Mathf.Max(1, Mathf.RoundToInt(Screen.height * _scale));

    // The game setting the FPS camera's ratio (its supersampling, or its upscaler's render scale): ours on top of it.
    private static void SwitchPrefix(SSAA __instance, ref float superSamplingRatio)
    {
        if (!ReferenceEquals(__instance, CameraManager.Instance?.SSAA))
            return;
        _ssaa = __instance;
        _gameRatio = superSamplingRatio;
        superSamplingRatio = Scaled(superSamplingRatio);
    }

    // Under an upscaler the camera renders within its own frame, so no more than all of it: an upscaler takes no more in
    // than it puts out. Otherwise as large as the graphics card can make a texture.
    private static float Scaled(float ratio)
    {
        if (_scale <= 1.0001f)
            return ratio;
        return ratio < 1f ? Mathf.Min(ratio * _scale, 0.99f) : Mathf.Min(ratio * _scale, LargestScale());
    }

    private static float LargestScale() => (float)SystemInfo.maxTextureSize / Mathf.Max(Screen.width, Screen.height, 1);

    // The frame made the size wanted: the buffers follow at once, the camera's ratio once the game applies it (after
    // this frame's render).
    private static void Resize(float scale)
    {
        if (Mathf.Abs(scale - _scale) < 0.001f)
            return;
        _scale = scale;
        SSAA? ssaa = CameraManager.Instance?.SSAA;
        if (ssaa == null)
            return;
        if (!ReferenceEquals(ssaa, _ssaa))
        {
            _ssaa = ssaa;
            _gameRatio = ssaa.GetCurrentSSRatio();
        }
        ssaa.Switch(_gameRatio);
    }

    private static float WantedScale(float magnification)
    {
        magnification = Mathf.Max(1f, magnification);
        return Detail <= 1f ? Mathf.Lerp(1f, magnification, Detail) : magnification * Detail;
    }

    // How much the fisheye magnifies the centre of the straight frame (pixels on the screen to pixels rendered).
    private static float CentreMagnification(float aspect)
    {
        float k = 1f - 0.5f * Curve;
        Lens(aspect, Field * Mathf.Deg2Rad, k, out float f, out float halfHeight);
        return f * halfHeight;
    }

    // The fisheye r = f tan(k theta) / k across the screen at across, and the straight frame's half height (as a
    // tangent) that reaches just as far as its corners; its edges then reach past the fisheye's.
    private static void Lens(float aspect, float across, float k, out float f, out float halfHeight)
    {
        f = aspect * k / Mathf.Tan(k * across * 0.5f);
        float corner = Mathf.Sqrt(aspect * aspect + 1f);
        float cornerAngle = Mathf.Min(Mathf.Atan(k * corner / f) / k, MaxCornerDegrees * Mathf.Deg2Rad);
        halfHeight = Mathf.Tan(cornerAngle) / corner;
    }

    // The FPS camera (the one with SSAA) waking: the lens on it, off until wanted. Put on any later, it would come after
    // the SSAA stage hands the frame to the screen (see NightVisionComposite.cs).
    private static void NightVisionAwakePostfix(BSG.CameraEffects.NightVision __instance)
    {
        if (__instance.GetComponent<SSAA>() == null || __instance.GetComponent<FisheyeImage>() != null)
            return;
        __instance.gameObject.AddComponent<FisheyeImage>().enabled = false;
    }

    internal static float LastWideField => _widenedField;

    internal static void LateUpdate()
    {
        GiveBack();
        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        Camera? camera = CameraManager.Instance?.Camera;
        bool ours = NewCameraSwitch.Enabled && player != null && player.IsYourPlayer;
        _weight = ours ? Mathf.MoveTowards(_weight, Enabled ? 1f : 0f, Time.unscaledDeltaTime / FadeSeconds) : 0f;
        // In steps of 0.05, so dragging a slider does not make the frame anew every frame. As much as the lens magnifies,
        // on the player's choice and warning (F12): at the widest field and fullest curve the centre is magnified 2.8 times,
        // and a frame that much larger (7168 by 4032 for 2560 by 1440) has filled an 8 GB card's memory and stalled the game
        // for seconds at a time. Only what a graphics card cannot make at all is left out.
        float scale = ours && Enabled && HighResolution && camera != null
            ? Mathf.Round(Mathf.Min(WantedScale(CentreMagnification(camera.aspect)), LargestScale()) * 20f) / 20f
            : 1f;
        Resize(scale);
        FisheyeImage? image = Image(camera);
        float t = _weight * _weight * (3f - 2f * _weight);
        if (camera == null || image == null || t <= 0.0001f || Material() == null)
        {
            if (image != null && image.enabled)
                image.enabled = false;
            return;
        }

        // r = f tan(k theta) / k, in half screen heights: k = 1 is the game's straight view at its own field, k = 1/2 the
        // stereographic fisheye; the curve set picks between, at the field set. The less curved, the less the centre is
        // magnified and the more what is near the edges is stretched, as a body camera's wide lens has it.
        float aspect = camera.aspect;
        _gameField = camera.fieldOfView;
        float gameAcross = 2f * Mathf.Atan(Mathf.Tan(_gameField * 0.5f * Mathf.Deg2Rad) * aspect);
        float across = Mathf.Lerp(gameAcross, Field * Mathf.Deg2Rad, t);
        float k = 1f - 0.5f * Curve * t;
        Lens(aspect, across, k, out float f, out float halfHeight);
        _shape = new Vector4(k, f, halfHeight, aspect);
        // The image circle shrinks from past the corners (none of it seen) to within them.
        float shade = Vignette * t;
        _shade = new Vector4(1.2f - 0.3f * shade, 0.15f, 0.35f * shade, 0f);
        _widenedField = 2f * Mathf.Atan(halfHeight) * Mathf.Rad2Deg;
        camera.fieldOfView = _widenedField;
        _widened = camera;
        if (!image.enabled)
            image.enabled = true;
    }

    // Before the frame's first camera renders: something after this mod's LateUpdate may have set the camera's field
    // again (in the hideout a Cinemachine brain drives the FPS camera and pushes the game's field to it every frame).
    // Its field taken as the game's, and ours put back, so the frame is drawn as wide as the lens bends it.
    internal static void Reassert(Camera _)
    {
        if (_widened == null || Mathf.Abs(_widened.fieldOfView - _widenedField) < 0.001f)
            return;
        _gameField = _widened.fieldOfView;
        _widened.fieldOfView = _widenedField;
    }

    // Where a point of the straight frame (its place in the camera's view, 0 to 1) lands on the screen: the shader's
    // straightUv the other way round. The same point when the lens is not on.
    internal static Vector2 ToScreen(Vector2 view)
    {
        if (_widened == null)
            return view;
        float k = _shape.x, f = _shape.y, halfHeight = _shape.z, aspect = _shape.w;
        Vector2 straight = new Vector2((view.x * 2f - 1f) * aspect, view.y * 2f - 1f);
        float across = straight.magnitude;
        if (across < 0.00001f)
            return view;
        float r = f * Mathf.Tan(k * Mathf.Atan(across * halfHeight)) / k;
        Vector2 q = straight * (r / across);
        return new Vector2(q.x / aspect * 0.5f + 0.5f, q.y * 0.5f + 0.5f);
    }

    // The game's field back on the camera, at the end of the frame (and before the next one is worked out).
    internal static void GiveBack()
    {
        if (_widened != null)
            _widened.fieldOfView = _gameField;
        _widened = null;
    }

    private static FisheyeImage? Image(Camera? camera)
    {
        if (!ReferenceEquals(camera, _imageCamera) || _image == null)
        {
            _imageCamera = camera;
            _image = camera != null ? camera.GetComponent<FisheyeImage>() : null;
        }
        return _image;
    }

    private static Material? Material()
    {
        if (_material == null && NightVisionEyepieces.FisheyeShader is Shader shader)
            _material = new Material(shader) { name = "Justachae fisheye", hideFlags = HideFlags.DontSave };
        return _material;
    }

    internal static void Render(Component camera, RenderTexture source, RenderTexture destination)
    {
        SSAAPropagator? propagator = camera.GetComponent<SSAAPropagator>();
        RenderTexture from = source, to = destination;
        bool staged = propagator != null && propagator.GetSourceDestination(out from, out to);
        if (!staged)
        {
            from = source;
            to = destination;
        }
        // BRNVG's renderer, further on, putting the eyepieces in: the lens goes on there, after them.
        Behaviour? later = NightVisionEyepieces.EyepieceRenderer(camera);
        bool widened = ReferenceEquals(_widened, camera.GetComponent<Camera>());
        _left = widened && later != null && LeftTo(later);
        if (widened && !_left)
            Blit(from, to);
        else
            Graphics.Blit(from, to);
        if (staged)
            propagator!.ReleaseSourceDestination(from, to);
    }

    // The lens laid over from into to.
    internal static void Blit(RenderTexture from, RenderTexture to)
    {
        if (_material == null)
        {
            Graphics.Blit(from, to);
            return;
        }
        _material.SetVector(ShapeId, _shape);
        _material.SetVector(ShadeId, _shade);
        if (_scale < HalvedAbove)
        {
            Graphics.Blit(from, to, _material, 0);
            return;
        }
        // The game brings the frame to the screen with one bicubic read (SSAAImpl), which at more than about twice leaves
        // most of its pixels out: halved here until it would no longer, each pixel counted, so what was rendered is used.
        RenderTexture laid = RenderTexture.GetTemporary(to.width, to.height, 0, to.format);
        Graphics.Blit(from, laid, _material, 0);
        float over = _scale;
        while (over >= HalvedAbove)
        {
            RenderTexture half = RenderTexture.GetTemporary(Mathf.Max(laid.width / 2, 1), Mathf.Max(laid.height / 2, 1), 0, to.format);
            Graphics.Blit(laid, half);
            RenderTexture.ReleaseTemporary(laid);
            laid = half;
            over *= 0.5f;
        }
        Graphics.Blit(laid, to);
        RenderTexture.ReleaseTemporary(laid);
    }

    // The lens left by ours this frame to what comes after it: taken once, by that (NightVisionEyepieces.RenderImagePrefix).
    internal static bool TakeLeft()
    {
        bool left = _left;
        _left = false;
        return left;
    }

    private static bool _left;

    // An image effect coming after the lens on its camera, while the lens is on: what it puts in (the eyepieces, worked
    // out for the straight frame) has to be bent too, so the lens is left to it.
    private static bool LeftTo(Behaviour later)
    {
        if (_widened == null || later.gameObject != _widened.gameObject)
            return false;
        FisheyeImage? image = Image(_widened);
        if (image == null || !image.enabled)
            return false;
        if (!ReferenceEquals(later, _orderedLater) || !ReferenceEquals(image, _orderedImage))
        {
            Component[] all = image.GetComponents<Component>();
            _laterAfter = System.Array.IndexOf(all, later) > System.Array.IndexOf(all, image);
            _orderedLater = later;
            _orderedImage = image;
        }
        return _laterAfter;
    }

    private static Behaviour? _orderedLater;
    private static FisheyeImage? _orderedImage;
    private static bool _laterAfter;
}

// Ours, on the FPS camera: the fisheye lens over the frame.
internal sealed class FisheyeImage : MonoBehaviour
{
    private void OnRenderImage(RenderTexture source, RenderTexture destination) => Fisheye.Render(this, source, destination);
}
