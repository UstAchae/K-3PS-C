using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

// A sealed eyecup (the T-7's, the N-15's) shuts out the sky and the room once it is against the face: the game lights
// the lens and the cup's inside as if they stood in the open, sky reflected in the glass. So as the device flips down
// the light within the eyecups fades out with it (NvgShade.shader): on each camera that draws the device, once the
// game has lit the frame, what lies within each eyecup's measured shape keeps less of its light the further down the
// device is, and next to none against the face. Flipped up again it comes back the same way.
internal static partial class NightVisionEyepieces
{
    private const float SealedKeep = 0f;            // what an eyecup's inside keeps of its light against the face
    private const float ShadeMainReach = 3f;        // the main camera shades the device while within this of it (m)
    private const float LensRadiance = 32f;         // the lens's light within the cup, at full power (linear)
    private const float SkinAlbedo = 0.3f;          // the face, closing the cup, sends that much of the lens's light back
    private const float LensFieldSoftness = 0.03f;  // its field's edge, as a cosine
    private const float LensScatter = 0.05f;        // the share of it scattered to every side

    private static readonly int ShadeNowId = Shader.PropertyToID("_ShadeNow");
    private static readonly int ShadeInvProjId = Shader.PropertyToID("_ShadeInvProj");
    private static readonly int ShadeViewToWorldId = Shader.PropertyToID("_ShadeViewToWorld");
    private static readonly int[] ShadeCupIds = { Shader.PropertyToID("_ShadeCup0"), Shader.PropertyToID("_ShadeCup1") };
    private static readonly int[] ShadeAxisIds = { Shader.PropertyToID("_ShadeAxis0"), Shader.PropertyToID("_ShadeAxis1") };
    private static readonly int ShadeShapeId = Shader.PropertyToID("_ShadeShape");
    private static readonly int ShadeRadii0Id = Shader.PropertyToID("_ShadeRadii0");
    private static readonly int ShadeRadii1Id = Shader.PropertyToID("_ShadeRadii1");
    private static readonly int ShadeKeepId = Shader.PropertyToID("_ShadeKeep");
    private static readonly int ShadePlaceId = Shader.PropertyToID("_ShadePlace");
    private static readonly int ShadeFacingId = Shader.PropertyToID("_ShadeFacing");
    private static readonly int ShadeLensId = Shader.PropertyToID("_ShadeLens");
    private static readonly int ShadeSkinId = Shader.PropertyToID("_ShadeSkin");
    private static readonly int[] ShadeLightIds = { Shader.PropertyToID("_ShadeLight0"), Shader.PropertyToID("_ShadeLight1") };
    private static readonly int ShadePlacedId = Shader.PropertyToID("_ShadePlaced");

    private static Shader? _shadeShader;
    private static Material? _shadeMaterial;
    private static readonly CommandBuffer?[] _shadeApply = new CommandBuffer?[3];   // each eye, and the main camera
    private static Camera? _shadeMainCamera;

    // How much of its light an eyecup's inside keeps now: all flipped up, next to none down against the face.
    private static float ShadeKeep => Mathf.Lerp(1f, SealedKeep, Mathf.SmoothStep(0f, 1f, _flip));

    // A camera's fading: slot 0 and 1 the eyes, 2 the main camera; place: where each lens stands from this camera.
    private static void ShadeCamera(int slot, Camera camera, Matrix4x4 projection, System.Func<Transform, Vector3> place)
    {
        _shadeApply[slot] ??= new CommandBuffer { name = "Justachae NVG eyecups sealed" };
        CommandBuffer apply = _shadeApply[slot]!;
        camera.RemoveCommandBuffer(CameraEvent.AfterLighting, apply);
        apply.Clear();
        float keep = ShadeKeep;
        if (_shadeShader == null || _body == null || !_device.Sealed || _lensNodes.Length == 0)
            return;
        _shadeMaterial ??= new Material(_shadeShader) { name = "Justachae NVG eyecups sealed", hideFlags = HideFlags.DontSave };
        // An eye drawing the device itself has its places (NightVisionPrecise); the main camera works them out.
        const bool placed = false;
        if (keep >= 0.999f)
        {
            return;
        }

        float scale = _body.lossyScale.x;
        int[] cups = { CentreLensA, Mathf.Min(CentreLensB, _lensNodes.Length - 1) };
        for (int k = 0; k < 2; k++)
        {
            Transform lens = _lensNodes[cups[k]];
            Vector3 c = place(lens);
            bool distinct = k == 0 || (c - place(_lensNodes[cups[0]])).sqrMagnitude > 1e-8f;
            apply.SetGlobalVector(ShadeCupIds[k], new Vector4(c.x, c.y, c.z, distinct ? 1f : 0f));
            apply.SetGlobalVector(ShadeAxisIds[k], lens.forward);
            // Thermal goggles' display, kept dim at night, lights nothing.
            float power = Active && !_device.Thermal ? _power[cups[k]] : 0f;
            apply.SetGlobalVector(ShadeLightIds[k], (Vector4)(PhosphorColor().linear * (LensRadiance * power)));
        }
        apply.SetGlobalVector(ShadeLensId, new Vector4(_lensFloors[cups[0]].w * scale, Mathf.Cos(FieldOfView * 0.5f * Mathf.Deg2Rad), LensFieldSoftness, LensScatter));
        apply.SetGlobalFloat(ShadeSkinId, SkinAlbedo);
        Matrix4x4 turned = camera.worldToCameraMatrix;
        turned.SetColumn(3, new Vector4(0f, 0f, 0f, 1f));
        apply.SetGlobalMatrix(ShadeInvProjId, projection.inverse);
        apply.SetGlobalMatrix(ShadeViewToWorldId, turned.inverse);
        apply.SetGlobalVector(ShadeShapeId, new Vector4(_device.FloorDepth, _device.CupHeight, 0f, 0f) * scale);
        apply.SetGlobalVector(ShadeRadii0Id, _device.CupRadii0 * scale);
        apply.SetGlobalVector(ShadeRadii1Id, _device.CupRadii1 * scale);
        apply.SetGlobalFloat(ShadeKeepId, keep);
        apply.SetGlobalFloat(ShadePlacedId, placed ? 1f : 0f);
        apply.GetTemporaryRT(ShadeNowId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.ARGBHalf);
        apply.Blit(BuiltinRenderTextureType.CameraTarget, ShadeNowId);
        apply.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        apply.DrawProcedural(Matrix4x4.identity, _shadeMaterial, 0, MeshTopology.Triangles, 3);
        apply.ReleaseTemporaryRT(ShadeNowId);
        // After the game's own buffers at this point (its ambient light among them), being added after them.
        camera.AddCommandBuffer(CameraEvent.AfterLighting, apply);
    }

    // The main camera fades the eyecups as the eyes do whenever it draws the device (the eyes not drawing it, or seen
    // from a free camera nearby).
    private static Camera? _depthAddedTo;

    private static void ShadeMain(Camera camera, bool draws)
    {
        Vector3 mount = _rotator != null ? _rotator.transform.position : _body != null ? _body.position : camera.transform.position;
        bool shade = draws && _body != null && (mount - camera.transform.position).sqrMagnitude <= ShadeMainReach * ShadeMainReach;
        if (_shadeMainCamera != null && (!shade || _shadeMainCamera != camera))
        {
            if (_shadeApply[2] != null)
                _shadeMainCamera.RemoveCommandBuffer(CameraEvent.AfterLighting, _shadeApply[2]);
            _shadeMainCamera = null;
        }
        if (!shade)
            return;
        // Its depth for the fade; taken back when the device is let go if this put it there (ReleaseShade).
        if ((camera.depthTextureMode & DepthTextureMode.Depth) == 0)
            _depthAddedTo = camera;
        camera.depthTextureMode |= DepthTextureMode.Depth;
        _shadeMainCamera = camera;
        Vector3 place = camera.transform.position;
        ShadeCamera(2, camera, camera.nonJitteredProjectionMatrix, t => t.position - place);
    }

    private static void ReleaseShade()
    {
        if (_shadeMainCamera != null)
            ShadeMain(_shadeMainCamera, false);
        if (_depthAddedTo != null)
            _depthAddedTo.depthTextureMode &= ~DepthTextureMode.Depth;
        _depthAddedTo = null;
    }
}
