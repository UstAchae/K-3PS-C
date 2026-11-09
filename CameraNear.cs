using EFT.CameraControl;
using UnityEngine;

namespace EFTNewCamera;

// The FPS camera's near clip plane brought closer while this mod places the camera. The camera sits close under the
// helmet's brim: in a raid the game's plane is 3 cm (the hideout's 1 cm), and what is nearer is cut away; through the
// cut (a shell drawn from the outside only) the scene behind shows. The plane is set once and kept, not changed and
// handed back every frame: the frame's motion vectors and the upscaler's history then see the one plane the frame is
// drawn with. Never farther than the game's own; the game's put back when this mod lets go.
internal static class CameraNear
{
    internal static float Near = 0.01f;   // metres
    private static Camera? _camera;
    private static float _gameNear;
    private static float _ours = -1f;

    internal static void Apply()
    {
        Camera? camera = CameraManager.Instance?.Camera;
        if (!ReferenceEquals(camera, _camera))
        {
            Release();
            _camera = camera;
            if (camera == null)
                return;
            _gameNear = camera.nearClipPlane;
        }
        if (camera == null)
            return;
        float now = camera.nearClipPlane;
        // Set by the game since (a new location, the hideout's camera rig): that is its plane now.
        if (Mathf.Abs(now - _ours) > 1e-6f)
            _gameNear = now;
        float near = Mathf.Min(Near, _gameNear);
        _ours = near;
        if (Mathf.Abs(now - near) > 1e-6f)
            camera.nearClipPlane = near;
    }

    internal static void Release()
    {
        if (_camera != null && Mathf.Abs(_camera.nearClipPlane - _ours) < 1e-6f)
            _camera.nearClipPlane = _gameNear;
        _camera = null;
        _ours = -1f;
    }
}
