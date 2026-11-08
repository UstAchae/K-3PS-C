using EFT;
using EFT.CameraControl;
using UnityEngine;

namespace EFTNewCamera;

// The hideout's camera as a raid's. In the hideout a Cinemachine brain on the FPS camera drives it from the hideout's
// virtual cameras: the overview's rails, and while the player walks about a fixed one of its own (VCamera_player, set
// by HideoutCameraController), whose lens (its field, near and far planes) the brain pushes onto the camera every
// frame after this mod has placed it. While this mod has the camera (walking about, not the overview, whose free
// camera is the brain's), the brain is off, as in a raid there is none; given back as this mod lets go, before the
// overview's blend from the player's place starts.
internal static class HideoutCinemachine
{
    private static Camera? _searched;
    private static Behaviour? _brain;
    private static bool _turnedOff;

    internal static void Update(Player player)
    {
        if (player is not HideoutPlayer)
            return;
        Camera? camera = CameraManager.Instance?.Camera;
        if (camera == null)
            return;
        if (camera != _searched)
        {
            Release();
            _searched = camera;
            _brain = camera.GetComponent("CinemachineBrain") as Behaviour;
        }
        if (_brain != null && _brain.enabled)
        {
            _brain.enabled = false;
            _turnedOff = true;
        }
    }

    internal static void Release()
    {
        if (_turnedOff && _brain != null)
            _brain.enabled = true;
        _turnedOff = false;
        _brain = null;
        _searched = null;
    }
}
