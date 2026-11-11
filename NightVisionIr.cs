using System.Collections.Generic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// IR seen only through the lenses. The game lights IR for the FPS camera's whole frame while night vision is on; with
// the eyepieces in, the frame (the naked eye's scene around them) is drawn with none, and what IR adds is drawn into
// a layer of its own that only the lenses' picture is made with (ForTube, step 2 at the head of NightVisionComposite.cs).
// The night-vision ambient's difference goes into the same layer (NightVisionAmbient.cs).
// IR lights: left in the frame at their intensity but through a blank cookie, so the game still draws their shadow maps
// and they light nothing (at a mere trace of intensity the game leaves them out, shadows and all); each is drawn into
// the layer as the game's deferred pass would light it, a shadowed one right after its shadow map (that map, and the
// view-projection it was drawn with, are then what is set). The lasers' own impact lights the same way, unshadowed.
// IR lasers: their beam and point drawn again into the layer, against the frame's depth, with night vision on, as last
// drawn this frame (AimArms draws them again once the gun is where the camera sees it). Visible lasers are left as the
// frame has them, as the naked eye sees them: the tube amplifies their light as it does all the rest. The game's
// night-vision look of them (many times brighter: it stands in for the gain the game's own picture lacks) laid under the
// tube's gain counted that gain twice, and a laser's point became a white blot, in a sight's picture most of all.
internal static partial class NightVisionEyepieces
{
    private struct IrLight
    {
        internal Light Light;
        internal float Intensity;
    }

    private struct IrMesh
    {
        internal Mesh Mesh;
        internal Matrix4x4 Matrix;
        internal Material Material;
        internal MaterialPropertyBlock Block;
    }

    private struct IrLaser
    {
        internal IrMesh? Beam, Point;
        internal IrLight? Light;
        internal bool Seen;                 // a visible laser: the naked eye sees it too
    }

    private static Shader? _irLightShader;
    private static Material? _irPlain, _irHard, _irSoft, _irAdd;
    private static RenderTexture? _irLayer, _irNaked;
    private static readonly List<IrLight> _irLights = new List<IrLight>();
    private static readonly List<IrMesh> _irMeshes = new List<IrMesh>(), _irOnlyMeshes = new List<IrMesh>();
    private static readonly List<IrLight> _irLaserLights = new List<IrLight>();
    private static int _irLightsFrame = -1, _irMeshesFrame = -1, _irDrawnFrame = -1;
    private static readonly Dictionary<Light, CommandBuffer> _irShadowed = new Dictionary<Light, CommandBuffer>();
    private static readonly List<Light> _irAttached = new List<Light>();
    private static readonly List<IrLight> _irLaserHidden = new List<IrLight>();
    private static CommandBuffer? _irBefore, _irAfter, _irLasers;
    private static Camera? _irCamera;
    private static readonly Dictionary<LaserBeam, IrLaser> _irLaserRecords = new Dictionary<LaserBeam, IrLaser>();
    private static readonly Dictionary<Light, Texture?> _irCookies = new Dictionary<Light, Texture?>();
    private static Texture? _irSoftCookie, _irFalloff;
    private static Texture2D? _irBlank;
    private static int _nakedFrame = -1;
    private static Camera? _nowhere;

    // Under an upscaler that blends frames itself (Unity's TAA pass off: DLSS and the like) the FPS camera's lasers are
    // not drawn in its frame, where the upscaler would blend them over frames (they have no motion vectors) and break
    // them up: they are drawn apart, against its depth and not jittered, and added once the upscale is done.
    private static bool LasersLifted => NewCameraSwitch.Enabled && Upscaled(EFT.CameraControl.CameraManager.Instance?.Camera) && LoadShaders() && _irLightShader != null;

    private static readonly System.Reflection.FieldInfo? ResolveDisabledField = AccessTools.Field(typeof(UnityEngine.Rendering.PostProcessing.PostProcessLayer), "_isTAAResolveDisabled");

    private static bool Upscaled(Camera? camera)
    {
        var layer = camera != null ? camera.GetComponent<UnityEngine.Rendering.PostProcessing.PostProcessLayer>() : null;
        return layer != null && layer.antialiasingMode == UnityEngine.Rendering.PostProcessing.PostProcessLayer.Antialiasing.TemporalAntialiasing
            && ResolveDisabledField?.GetValue(layer) is true;
    }

    // The upscaler's buffer just filled (as the camera culls): the lasers added after its upscale.
    private static void FillCommandBufferPostfix(SSAAPropagator __instance, CommandBuffer externalCommandBuffer)
    {
        if (externalCommandBuffer == null || !__instance.CompareTag("MainCamera"))
            return;
        if (__instance.GetSourceDestination(out RenderTexture source, out _) && source != null)
            LasersAfterUpscale(externalCommandBuffer, source);
    }

    // Where a laser is drawn: the FPS camera's draw (its own, or every camera's) to a camera that never renders.
    internal static Camera? LaserDrawCamera(Camera? camera)
    {
        Camera? fps = EFT.CameraControl.CameraManager.Instance?.Camera;
        if (camera != null && camera != fps || !LasersLifted)
            return camera;
        if (_nowhere == null)
        {
            var go = new GameObject("Justachae lasers drawn apart") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go);
            _nowhere = go.AddComponent<Camera>();
            _nowhere.enabled = false;
        }
        return _nowhere;
    }

    // Once the upscale is done: the lasers, as the frame shows them, added in. Put in the upscaler's buffer as the
    // camera culls, before this frame's lasers are drawn (OnCameraPreRenderIr, which clears them every frame).
    internal static void LasersAfterUpscale(CommandBuffer buffer, RenderTexture target)
    {
        if (_irNaked == null || _irAdd == null || !LasersLifted)
            return;
        buffer.Blit(_irNaked, target, _irAdd, 2);
    }

    private static readonly int IrClipToWorldId = Shader.PropertyToID("_IrClipToWorld");
    private static readonly int IrScreenId = Shader.PropertyToID("_IrScreen");
    private static readonly int IrCameraId = Shader.PropertyToID("_IrCamera");
    private static readonly int IrForwardId = Shader.PropertyToID("_IrForward");
    private static readonly int IrLightPosId = Shader.PropertyToID("_IrLightPos");
    private static readonly int IrLightColorId = Shader.PropertyToID("_IrLightColor");
    private static readonly int IrWorldToLightId = Shader.PropertyToID("_IrWorldToLight");
    private static readonly int IrSpotId = Shader.PropertyToID("_IrSpot");
    private static readonly int IrCookieId = Shader.PropertyToID("_IrCookie");
    private static readonly int IrFalloffId = Shader.PropertyToID("_IrFalloff");
    private static readonly int IrShadowMapId = Shader.PropertyToID("_IrShadowMap");
    private static readonly int IrLayerId = Shader.PropertyToID("_IrLayer");
    private static readonly int IrHistoryId = Shader.PropertyToID("_IrHistory");
    private static readonly int IrJitterId = Shader.PropertyToID("_IrJitter");
    private const float IrHeld = 0.1f;               // how much of this frame's layer goes into the held one

    private static readonly RenderTexture?[] _irHistory = new RenderTexture?[2];
    private static int _irHistoryIndex, _irResolvedFrame = -10;
    private static Vector2 _irJitter;
    private static readonly int NightVisionOnId = Shader.PropertyToID("_NightVisionOn");

    private static readonly AccessTools.FieldRef<IkLight, float> IkIntensity = AccessTools.FieldRefAccess<IkLight, float>("_prevIntensity");
    private static readonly AccessTools.FieldRef<LaserBeam, Mesh> LaserBeamMesh = AccessTools.FieldRefAccess<LaserBeam, Mesh>("_beamMesh");
    private static readonly AccessTools.FieldRef<LaserBeam, Mesh> LaserPointMesh = AccessTools.FieldRefAccess<LaserBeam, Mesh>("_pointMesh");
    private static readonly AccessTools.FieldRef<LaserBeam, MaterialPropertyBlock> LaserBeamBlock = AccessTools.FieldRefAccess<LaserBeam, MaterialPropertyBlock>("_beamBlock");
    private static readonly AccessTools.FieldRef<LaserBeam, MaterialPropertyBlock> LaserPointBlock = AccessTools.FieldRefAccess<LaserBeam, MaterialPropertyBlock>("_pointBlock");
    private static readonly AccessTools.FieldRef<LaserBeam, Light> LaserLight = AccessTools.FieldRefAccess<LaserBeam, Light>("_light");

    // IR kept from the frame while the eyepieces are in (flipping too: the game's night vision may be on by then), and
    // not thermal (IR is no heat); drawn for the lenses once the device is on.
    private static bool IrWanted => !_device.Thermal && _eyepieceImage != null && _eyepieceImage.enabled && _irLightShader != null;

    private static void InstallIr(Harmony harmony)
    {
        var preCull = AccessTools.Method(typeof(IkLight), nameof(IkLight.OnPreCullLight));
        var lateUpdate = AccessTools.Method(typeof(LaserBeam), nameof(LaserBeam.LateUpdate));
        if (preCull != null)
            harmony.Patch(preCull, prefix: new HarmonyMethod(typeof(NightVisionEyepieces), nameof(IkLightPreCullPrefix)));
        if (lateUpdate != null)
        {
            harmony.Patch(lateUpdate, prefix: new HarmonyMethod(typeof(NightVisionEyepieces), nameof(LaserAsItIsPrefix)) { priority = Priority.First });
            harmony.Patch(lateUpdate, prefix: new HarmonyMethod(typeof(NightVisionEyepieces), nameof(LaserToNakedEyePrefix)) { priority = Priority.Last });
            harmony.Patch(lateUpdate, postfix: new HarmonyMethod(typeof(NightVisionEyepieces), nameof(LaserLateUpdatePostfix)) { priority = Priority.Last });
        }
        var fill = AccessTools.Method(typeof(SSAAPropagator), nameof(SSAAPropagator.FillCommandBuffer));
        if (fill != null)
            harmony.Patch(fill, postfix: new HarmonyMethod(typeof(NightVisionEyepieces), nameof(FillCommandBufferPostfix)));
    }

    // For the FPS camera, an IR light through a blank cookie (its flare hidden) and taken for the layer. Its own cookie
    // goes back once the FPS camera has drawn, not for the cameras drawn meanwhile (the eyes, the thermal camera).
    private static bool IkLightPreCullPrefix(IkLight __instance, Camera cam)
    {
        Light? light = __instance.Light;
        // The scope's camera, the sight looked through bare (OpticSeesNightVision): the IR lights light nothing there. The game
        // lights them for it whenever the goggle is on.
        if (cam != null && IrWanted && OpticPicture.IsCamera(cam) && !OpticSeesNightVision())
        {
            if (light != null)
                light.intensity = 0f;
            if (__instance.FlareLight != null)
                __instance.FlareLight.SetAlpha(-1f);
            return false;
        }
        if (cam == null || cam != EFT.CameraControl.CameraManager.Instance?.Camera || !IrWanted)
            return true;
        if (_irLightsFrame != Time.frameCount)
        {
            _irLights.Clear();
            _irLightsFrame = Time.frameCount;
        }
        if (light != null)
        {
            float intensity = IkIntensity(__instance);
            light.intensity = intensity;
            if (!_irCookies.ContainsKey(light))
                _irCookies[light] = light.cookie;
            if (light.isActiveAndEnabled && intensity > 0f)
            {
                _irLights.Add(new IrLight { Light = light, Intensity = intensity });
                AttachIrShadowed(light);
            }
            light.cookie = IrBlank();
        }
        if (__instance.FlareLight != null)
            __instance.FlareLight.SetAlpha(-1f);
        return false;
    }

    // A shadowed IR light's buffer on it before the FPS camera culls (one put on later is not run that frame), empty
    // until the camera is about to render (OnCameraPreRenderIr): the cameras drawn meanwhile find nothing in it.
    private static void AttachIrShadowed(Light light)
    {
        if (QualitySettings.shadows == ShadowQuality.Disable || light.shadows == LightShadows.None || _irAttached.Contains(light))
            return;
        if (!_irShadowed.TryGetValue(light, out CommandBuffer? buffer))
            _irShadowed[light] = buffer = new CommandBuffer { name = "Justachae NVG IR light after its shadow" };
        buffer.Clear();
        light.AddCommandBuffer(LightEvent.AfterShadowMap, buffer);
        _irAttached.Add(light);
    }

    // Something else may dim visible lasers while night vision is on (BRNVG: its "Visible laser intensity with NVGs"), for the whole
    // frame. With the eyepieces in, visible lasers are seen at their own light (the tube amplifies it): drawn as they were, but for a
    // sight's picture that is the game's own night-vision one (its own night vision on), as the game has it.
    private static float _laserAsItWas = -1f;
    private static readonly int LaserColorId = Shader.PropertyToID("_Color");

    private static void LaserAsItIsPrefix(LaserBeam __instance)
    {
        _laserAsItWas = __instance != null && __instance.BeamMaterial != null ? __instance.IntensityFactor : -1f;
    }

    private static void LaserToNakedEyePrefix(LaserBeam __instance)
    {
        float was = _laserAsItWas;
        _laserAsItWas = -1f;
        if (was <= 0f || __instance == null || __instance.BeamMaterial == null || __instance.BeamMaterial.name == "LaserBeamIk")
            return;
        if (__instance.IntensityFactor / was >= 0.999f || !IrWanted)
            return;
        Camera? camera = LaserReplay.DrawCamera;
        if (camera != null && OpticPicture.IsCamera(camera) && OpticOwnNightVision())
            return;
        __instance.IntensityFactor = was;
        if (__instance.BeamMaterial.HasProperty(LaserColorId) && LaserBeamBlock(__instance) is MaterialPropertyBlock block)
            block.SetColor(LaserColorId, __instance.BeamMaterial.GetColor(LaserColorId));
    }

    // A laser as it was just drawn (the last time this frame counts): its beam, its point where it hits, and an IR
    // one's impact light. Visible ones too, for lasers drawn apart under an upscaler (LasersLifted).
    private static void LaserLateUpdatePostfix(LaserBeam __instance)
    {
        if (__instance.BeamMaterial == null || !IrWanted && !LasersLifted)
            return;
        bool seen = __instance.BeamMaterial.name != "LaserBeamIk";
        if (_irMeshesFrame != Time.frameCount)
        {
            _irLaserRecords.Clear();
            _irMeshesFrame = Time.frameCount;
        }
        Transform t = __instance.transform;
        Vector3 forward = t.forward;
        var record = new IrLaser { Seen = seen };
        Mesh beam = LaserBeamMesh(__instance), point = LaserPointMesh(__instance);
        if (beam != null)
            record.Beam = new IrMesh { Mesh = beam, Matrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one), Material = __instance.BeamMaterial, Block = LaserBeamBlock(__instance) };
        if (point != null && __instance.PointMaterial != null
            && Physics.Raycast(t.position + forward * __instance.RayStart, forward, out RaycastHit hit, __instance.MaxDistance, __instance.Mask))
            record.Point = new IrMesh { Mesh = point, Matrix = Matrix4x4.TRS(hit.point, Quaternion.LookRotation(hit.normal), Vector3.one), Material = __instance.PointMaterial, Block = LaserPointBlock(__instance) };
        Light light = LaserLight(__instance);
        if (!seen && light != null && light.isActiveAndEnabled && light.intensity > 0f)
            record.Light = new IrLight { Light = light, Intensity = light.intensity };
        _irLaserRecords[__instance] = record;
    }

    // This frame's lasers, each as last drawn: all of them, and the IR ones apart.
    private static void CollectIrLasers()
    {
        _irMeshes.Clear();
        _irOnlyMeshes.Clear();
        _irLaserLights.Clear();
        if (_irMeshesFrame != Time.frameCount)
            return;
        foreach (IrLaser laser in _irLaserRecords.Values)
        {
            foreach (IrMesh? mesh in new[] { laser.Beam, laser.Point })
            {
                if (mesh == null)
                    continue;
                _irMeshes.Add(mesh.Value);
                if (!laser.Seen)
                    _irOnlyMeshes.Add(mesh.Value);
            }
            if (laser.Light != null)
                _irLaserLights.Add(laser.Light.Value);
        }
    }

    // A cookie through which a spot light lights nothing.
    private static Texture2D IrBlank()
    {
        if (_irBlank == null)
        {
            _irBlank = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "Justachae NVG blank cookie", wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave };
            _irBlank.SetPixels32(new Color32[16]);
            _irBlank.Apply(false, true);
        }
        return _irBlank;
    }

    // The light's own cookie: the one it had before the blank was put in.
    private static Texture? IrCookie(Light light) => _irCookies.TryGetValue(light, out Texture? cookie) ? cookie : light.cookie;

    // Before the FPS camera culls: the lasers' impact lights out of its frame (back once it has drawn).
    internal static void HideIrLaserLights(Camera camera)
    {
        _irLaserHidden.Clear();
        if (!IrWanted || camera != EFT.CameraControl.CameraManager.Instance?.Camera)
            return;
        CollectIrLasers();
        foreach (IrLight l in _irLaserLights)
        {
            if (l.Light == null)
                continue;
            _irLaserHidden.Add(l);
            l.Light.intensity = 0f;
        }
    }

    // Just before the FPS camera renders (the eyes and the thermal camera drawn by now): the layer cleared before the
    // lights, each IR light drawn into it, the lasers after the transparent things.
    internal static void OnCameraPreRenderIr(Camera camera)
    {
        if (camera != EFT.CameraControl.CameraManager.Instance?.Camera)
            return;
        bool ir = IrWanted && Active, lifted = LasersLifted;
        if (!ir && !lifted)
            return;
        CollectIrLasers();
        bool lights = ir && _irLightsFrame == Time.frameCount && _irLights.Count > 0;
        bool lasers = lifted && _irMeshes.Count > 0 || ir && _irOnlyMeshes.Count > 0;
        // The night-vision ambient's difference (NightVisionAmbient.cs), once the layer's materials are made.
        bool ambient = ir && IrResources() && NightAmbientWanted();
        if (!lights && !lasers && !ambient || !IrResources())
        {
            // No lasers this frame: none added after the upscale either.
            if (lifted && _irNaked != null && _nakedFrame >= 0)
            {
                _irBefore ??= new CommandBuffer { name = "Justachae NVG IR layer clear" };
                _irBefore.Clear();
                _irBefore.SetRenderTarget(_irNaked);
                _irBefore.ClearRenderTarget(false, true, Color.clear);
                Graphics.ExecuteCommandBuffer(_irBefore);
                _nakedFrame = -1;
            }
            return;
        }

        int width = Mathf.Max(camera.pixelWidth, 1), height = Mathf.Max(camera.pixelHeight, 1);
        if (_irLayer == null || _irLayer.width != width || _irLayer.height != height)
        {
            if (_irLayer != null)
                Object.Destroy(_irLayer);
            if (_irNaked != null)
                Object.Destroy(_irNaked);
            _irLayer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf) { name = "Justachae NVG IR layer", hideFlags = HideFlags.DontSave };
            _irNaked = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf) { name = "Justachae NVG lasers to the naked eye", hideFlags = HideFlags.DontSave };
        }

        Matrix4x4 clip = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix;
        // Cleared now, before anything of this frame's may be drawn into it (shadow maps may come before the lighting).
        _irBefore ??= new CommandBuffer { name = "Justachae NVG IR layer clear" };
        _irBefore.Clear();
        _irBefore.SetGlobalMatrix(IrClipToWorldId, clip.inverse);
        _irBefore.SetGlobalVector(IrScreenId, new Vector4(width, height, 1f / width, 1f / height));
        _irBefore.SetGlobalVector(IrCameraId, camera.transform.position);
        _irBefore.SetGlobalVector(IrForwardId, camera.transform.forward);
        _irBefore.SetRenderTarget(_irLayer);
        _irBefore.ClearRenderTarget(false, true, Color.clear);
        _irBefore.SetRenderTarget(_irNaked);
        _irBefore.ClearRenderTarget(false, true, Color.clear);
        Graphics.ExecuteCommandBuffer(_irBefore);

        _irAfter ??= new CommandBuffer { name = "Justachae NVG IR lights" };
        _irAfter.Clear();
        _irAfter.SetRenderTarget(_irLayer);
        if (lights)
        {
            foreach (IrLight l in _irLights)
            {
                if (l.Light == null)
                    continue;
                if (_irAttached.Contains(l.Light) && _irShadowed.TryGetValue(l.Light, out CommandBuffer? buffer))
                {
                    buffer.Clear();
                    buffer.SetGlobalTexture(IrShadowMapId, BuiltinRenderTextureType.CurrentActive);
                    buffer.SetRenderTarget(_irLayer);
                    DrawIrLight(buffer, l, l.Light.shadows == LightShadows.Soft && QualitySettings.shadows == ShadowQuality.All ? _irSoft! : _irHard!);
                }
                else
                    DrawIrLight(_irAfter, l, _irPlain!);
            }
        }
        foreach (IrLight l in _irLaserLights)
        {
            if (l.Light != null)
                DrawIrLight(_irAfter, l, _irPlain!);
        }
        if (ambient)
            DrawNightAmbient(_irAfter);
        if (ir)
            camera.AddCommandBuffer(CameraEvent.AfterLighting, _irAfter);

        if (lasers)
        {
            _irLasers ??= new CommandBuffer { name = "Justachae NVG IR lasers" };
            _irLasers.Clear();
            // Under an upscaler not jittered: put in after the upscale, they are not blended over frames.
            if (lifted)
                _irLasers.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.nonJitteredProjectionMatrix);
            // As the frame shows them (night vision as it is for it), all of them, added after the upscale (LasersAfterUpscale).
            if (lifted)
            {
                _irLasers.SetRenderTarget(_irNaked, BuiltinRenderTextureType.CameraTarget);
                foreach (IrMesh m in _irMeshes)
                {
                    if (m.Mesh != null && m.Material != null)
                        _irLasers.DrawMesh(m.Mesh, m.Matrix, m.Material, 0, 0, m.Block);
                }
                _nakedFrame = Time.frameCount;
            }
            // The IR lasers as night vision shows them, for the lenses.
            if (ir && _irOnlyMeshes.Count > 0)
            {
                _irLasers.SetRenderTarget(_irLayer, BuiltinRenderTextureType.CameraTarget);
                _irLasers.SetGlobalFloat(NightVisionOnId, 1f);
                foreach (IrMesh m in _irOnlyMeshes)
                {
                    if (m.Mesh != null && m.Material != null)
                        _irLasers.DrawMesh(m.Mesh, m.Matrix, m.Material, 0, 0, m.Block);
                }
                _irLasers.SetGlobalFloat(NightVisionOnId, 0f);
            }
            if (lifted)
                _irLasers.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
            camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _irLasers);
        }
        _irCamera = camera;
        // This frame's jitter (as uv): the projection's offset from the unjittered one.
        Matrix4x4 jittered = camera.projectionMatrix, steady = camera.nonJitteredProjectionMatrix;
        _irJitter = new Vector2((jittered.m02 - steady.m02) * 0.5f, (jittered.m12 - steady.m12) * 0.5f);
        if (ir)
            _irDrawnFrame = Time.frameCount;
    }

    // The light's own settings, as the game's deferred pass is given them.
    private static void DrawIrLight(CommandBuffer buffer, IrLight l, Material material)
    {
        Light light = l.Light;
        Transform t = light.transform;
        var block = new MaterialPropertyBlock();
        block.SetVector(IrLightPosId, new Vector4(t.position.x, t.position.y, t.position.z, 1f / (light.range * light.range)));
        block.SetVector(IrLightColorId, LinearLightColor(light.color, l.Intensity));
        block.SetMatrix(IrWorldToLightId, Matrix4x4.TRS(t.position, t.rotation, Vector3.one).inverse);
        block.SetVector(IrSpotId, new Vector4(1f / Mathf.Tan(light.spotAngle * 0.5f * Mathf.Deg2Rad), 1f - light.shadowStrength, 0f, 0f));
        Texture? cookie = IrCookie(light);
        block.SetTexture(IrCookieId, cookie != null ? cookie : _irSoftCookie!);
        block.SetTexture(IrFalloffId, _irFalloff!);
        buffer.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, block);
    }

    // As Unity turns a light's colour and intensity into what its shaders get.
    private static Vector4 LinearLightColor(Color color, float intensity)
    {
        if (QualitySettings.activeColorSpace != ColorSpace.Linear)
            return color * intensity;
        return GraphicsSettings.lightsUseLinearIntensity ? color.linear * intensity : (color * intensity).linear;
    }

    // After the FPS camera has drawn: the layer's buffers off; the IR lights' cookies and the impact lights back.
    internal static void OnCameraPostRenderIr(Camera camera)
    {
        if (camera != EFT.CameraControl.CameraManager.Instance?.Camera)
            return;
        DetachIr();
        foreach (KeyValuePair<Light, Texture?> c in _irCookies)
        {
            if (c.Key != null)
                c.Key.cookie = c.Value;
        }
        _irCookies.Clear();
        foreach (IrLight l in _irLaserHidden)
        {
            if (l.Light != null)
                l.Light.intensity = l.Intensity;
        }
        _irLaserHidden.Clear();
    }

    // The optic's camera (a scope's picture). The game draws IR lasers only while night vision is on (_NightVisionOn), which it
    // sets from the FPS camera's night vision in its pre-cull and leaves for the optic's camera; with the eyepieces in that is
    // off for the naked eye's frame, so it is set here for the optic's camera, off again after:
    //   its own night vision (OpticOwnNightVision): on, the sight's picture the game's own night-vision one;
    //   the goggle's (ThroughScope): off, so that visible lasers are drawn at their own light for the tube to amplify, as
    //   in the lenses' picture, and the IR lasers drawn by us with it on after the transparent things (_opticIrLasers).
    // IkLight lights the IR lights for it in either case (OpticSeesNightVision).
    private static bool _opticIr, _bareReticleNoted;
    private static CommandBuffer? _opticIrLasers;
    private static Camera? _opticIrLasersOn;

    internal static void OnOpticPreCullIr(Camera camera)
    {
        // The goggle's filter says night vision is on (_NightVisionOn, set as the main camera culls, and the sight's camera
        // renders after), and a sight's reticle drawn under it goes unlit. Left lit whether the sight is looked through
        // with the goggle or bare (a monocular before the other eye): its own light, seen as it is or through the tube.
        // A sight with its own night vision sets it again below.
        if (OpticPicture.IsCamera(camera)
            && EFT.CameraControl.CameraManager.Instance?.NightVision is BSG.CameraEffects.NightVision goggle && goggle.On)
        {
            Shader.SetGlobalFloat(NightVisionOnId, 0f);
            if (!_bareReticleNoted)
            {
                _bareReticleNoted = true;
                NewCameraPlugin.Log?.LogInfo("NVG: a sight under the goggle drawn with its reticle lit (night vision off for its camera).");
            }
        }
        DiagReticle(camera); // TEMP DIAG
        if (!IrWanted || !OpticPicture.IsCamera(camera))
            return;
        if (OpticOwnNightVision())
        {
            Shader.SetGlobalFloat(NightVisionOnId, 1f);
            _opticIr = true;
            return;
        }
        if (!OpticSeesNightVision() || _irMeshesFrame != Time.frameCount)
            return;
        // This frame's IR lasers, as just drawn for the optic's camera (LaserReplay, as it culls, before this).
        _opticIrLasers ??= new CommandBuffer { name = "Justachae NVG IR lasers in the sight" };
        _opticIrLasers.Clear();
        _opticIrLasers.SetGlobalFloat(NightVisionOnId, 1f);
        int drawn = 0;
        foreach (IrLaser laser in _irLaserRecords.Values)
        {
            if (!laser.Seen)
                drawn += DrawInSight(laser.Beam) + DrawInSight(laser.Point);
        }
        _opticIrLasers.SetGlobalFloat(NightVisionOnId, 0f);
        if (drawn == 0)
            return;
        camera.AddCommandBuffer(CameraEvent.AfterForwardAlpha, _opticIrLasers);
        _opticIrLasersOn = camera;
    }

    private static int DrawInSight(IrMesh? mesh)
    {
        if (mesh is not IrMesh m || m.Mesh == null || m.Material == null)
            return 0;
        _opticIrLasers!.DrawMesh(m.Mesh, m.Matrix, m.Material, 0, 0, m.Block);
        return 1;
    }

    // TEMP DIAG: once a second while a sight is looked through, the reticle's material (shader, keywords, every float
    // and colour it has), the goggle's state and the global _NightVisionOn, to set beside the same without the goggle.
    private static float _diagReticleAt;
    private static void DiagReticle(Camera camera)
    {
        if (!OpticPicture.IsCamera(camera) || Time.time - _diagReticleAt < 1f)
            return;
        _diagReticleAt = Time.time;
        EFT.CameraControl.OpticSight? sight = EFT.CameraControl.CameraManager.Instance?.OpticCameraManager?.CurrentOpticSight;
        Material? m = sight?.ScopeData?.Reticle?.Material;
        var goggle = EFT.CameraControl.CameraManager.Instance?.NightVision;
        var sb = new System.Text.StringBuilder();
        sb.Append($"TEMP DIAG reticle: sight {(sight != null ? sight.name : "none")}, goggle {(goggle != null && goggle.On)}, global NV {Shader.GetGlobalFloat(NightVisionOnId)}, ");
        if (m != null)
        {
            sb.Append($"shader {m.shader.name}, keywords [{string.Join(",", m.shaderKeywords)}]");
            for (int i = 0; i < m.shader.GetPropertyCount(); i++)
            {
                string n = m.shader.GetPropertyName(i);
                var t = m.shader.GetPropertyType(i);
                if (t == UnityEngine.Rendering.ShaderPropertyType.Float || t == UnityEngine.Rendering.ShaderPropertyType.Range)
                    sb.Append($", {n}={m.GetFloat(n):F3}");
                else if (t == UnityEngine.Rendering.ShaderPropertyType.Color)
                    sb.Append($", {n}={m.GetColor(n)}");
            }
        }
        else sb.Append("no reticle material");
        NewCameraPlugin.Log?.LogInfo(sb.ToString());
    }

    // The sight's own night vision on: its picture is the game's own night-vision one.
    private static bool OpticOwnNightVision()
    {
        var manager = EFT.CameraControl.CameraManager.Instance;
        return manager?.OpticCameraManager?.NightVision != null && manager.OpticCameraManager.NightVision.On;
    }

    // Whether the scope's picture is seen with night vision: the sight's own, or the goggle's, but the goggle's only when the
    // sight is looked through with it (倍镜状态, ThroughScope). A monocular before the other eye leaves the aiming eye to look through
    // the sight bare: no IR there, and lasers as the naked eye sees them.
    private static bool OpticSeesNightVision()
    {
        if (OpticOwnNightVision())
            return true;
        var manager = EFT.CameraControl.CameraManager.Instance;
        return manager?.NightVision != null && manager.NightVision.On && ThroughScope;
    }

    internal static void OnOpticPostRenderIr(Camera camera)
    {
        if (_opticIrLasersOn != null && _opticIrLasers != null)
        {
            _opticIrLasersOn.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _opticIrLasers);
            _opticIrLasersOn = null;
        }
        if (!_opticIr)
            return;
        _opticIr = false;
        Shader.SetGlobalFloat(NightVisionOnId, 0f);
    }

    private static void DetachIr()
    {
        foreach (Light light in _irAttached)
        {
            if (light != null && _irShadowed.TryGetValue(light, out CommandBuffer? buffer))
                light.RemoveCommandBuffer(LightEvent.AfterShadowMap, buffer);
        }
        _irAttached.Clear();
        if (_irCamera != null)
        {
            if (_irAfter != null)
                _irCamera.RemoveCommandBuffer(CameraEvent.AfterLighting, _irAfter);
            if (_irLasers != null)
                _irCamera.RemoveCommandBuffer(CameraEvent.AfterForwardAlpha, _irLasers);
        }
        _irCamera = null;
    }

    // Unity's own spot shape (a light without a cookie) and distance falloff, as its deferred pass uses them.
    private static bool IrResources()
    {
        if (_irPlain != null && _irSoftCookie != null && _irFalloff != null)
            return true;
        if (_irLightShader == null)
            return false;
        try
        {
            _irSoftCookie ??= Resources.GetBuiltinResource<Texture2D>("Soft.psd");
        }
        catch (System.Exception)
        {
        }
        if (_irFalloff == null)
        {
            foreach (Texture texture in Resources.FindObjectsOfTypeAll<Texture>())
            {
                if (texture.name == "UnityAttenuation")
                {
                    _irFalloff = texture;
                    break;
                }
            }
        }
        if (_irSoftCookie == null || _irFalloff == null)
            return false;
        _irPlain = new Material(_irLightShader) { name = "Justachae NVG IR light", hideFlags = HideFlags.DontSave };
        _irHard = new Material(_irLightShader) { name = "Justachae NVG IR light, hard shadows", hideFlags = HideFlags.DontSave };
        _irHard.EnableKeyword("IR_SHADOWS");
        _irSoft = new Material(_irLightShader) { name = "Justachae NVG IR light, soft shadows", hideFlags = HideFlags.DontSave };
        _irSoft.EnableKeyword("IR_SOFT_SHADOWS");
        _irAdd ??= new Material(_irLightShader) { name = "Justachae NVG IR layer added", hideFlags = HideFlags.DontSave };
        return true;
    }

    // Step 2 of the eyepieces' picture (NightVisionComposite.cs): the light that reaches the tube, made into a temporary
    // (made); the frame itself when nothing changes it. This frame's IR added (visible lasers are in the frame already, at
    // their own light); a magnified sight's lens (倍镜状态) at the share of its light that reaches the tube, with the sight's own picture
    // only: IR and lasers as its camera sees them, not the layer's, which is what lies past the sight from the eye.
    private static RenderTexture ForTube(RenderTexture frame, ref RenderTexture? made)
    {
        bool ir = _irDrawnFrame == Time.frameCount && _irLayer != null;
        if (!ir && !ScopeMasked || _irLightShader == null)
            return frame;
        Material add = _irAdd ??= new Material(_irLightShader) { name = "Justachae NVG IR layer added", hideFlags = HideFlags.DontSave };
        made = RenderTexture.GetTemporary(frame.width, frame.height, 0, frame.format);
        add.SetTexture(IrLayerId, ir ? HoldIr(_irLayer!, add) : Texture2D.blackTexture);
        BindScope(add);
        Graphics.Blit(frame, made, add, 1);
        return made;
    }

    // The layer held over frames (pass 3): this frame's blended into the last one's, which goes on only from the
    // frame just before.
    private static Texture HoldIr(RenderTexture layer, Material material)
    {
        for (int i = 0; i < 2; i++)
        {
            if (_irHistory[i] != null && _irHistory[i]!.width == layer.width && _irHistory[i]!.height == layer.height)
                continue;
            if (_irHistory[i] != null)
                Object.Destroy(_irHistory[i]);
            _irHistory[i] = new RenderTexture(layer.width, layer.height, 0, RenderTextureFormat.ARGBHalf) { name = "Justachae NVG IR layer held", hideFlags = HideFlags.DontSave };
            _irResolvedFrame = -10;
        }
        RenderTexture read = _irHistory[_irHistoryIndex]!, write = _irHistory[1 - _irHistoryIndex]!;
        bool following = _irResolvedFrame == Time.frameCount - 1;
        material.SetTexture(IrHistoryId, read);
        material.SetVector(IrJitterId, new Vector4(_irJitter.x, _irJitter.y, following ? 1f : 0f, IrHeld));
        Graphics.Blit(layer, write, material, 3);
        _irHistoryIndex = 1 - _irHistoryIndex;
        _irResolvedFrame = Time.frameCount;
        return write;
    }
}
