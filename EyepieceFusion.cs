using System;
using UnityEngine;

namespace EFTNewCamera.Interop;

// For another mod that lays its own picture into a device's eyepieces (K-ENVG-B's thermal fusion), without either mod
// needing the other: it finds this type by name ("EFTNewCamera.Interop.EyepieceFusion, JustachaeThirdPersonCamera"),
// checks Version, and sets Fuse once. Only Unity and BCL types, so nothing of either mod is referenced by the other.
//
// Each frame the eyepieces show a device made for it (a Device with Fusion), by the main camera's pre-cull:
// ClaimedFrame = Time.frameCount, FieldInMainUv and Deployed for this frame. Then, in the eyepieces' image effect (after
// the main camera's own rendering and its command buffers, before the SSAA stage puts the frame on screen), once the
// tube's picture is made and before the eyepieces are laid over the frame: Fuse(i2, dest), i2 the tube's picture and
// dest a texture of its size and format, both in the main camera's screen uv (not jittered, not flipped). It returns
// true when it wrote dest, which the eyepieces then show; false leaves them the tube's picture. A frame not claimed
// (the mod off, its master switch off, another device, the eyepieces not shown) never has ClaimedFrame equal to
// Time.frameCount: the other mod draws its picture its own way. If Fuse throws, it is not called again this session.
public static class EyepieceFusion
{
    // Raised only for a change the other side cannot read the old way; new optional members keep it.
    public const int Version = 1;

    // Time.frameCount of the last frame the eyepieces showed a fusion device, set at the main camera's pre-cull.
    public static int ClaimedFrame { get; internal set; } = -1;

    // The eyepiece the picture goes into (the right one, the wearer's), as the eye sees it on the main camera's
    // screen this frame: xy the uv of its centre (0,0 bottom left), z its circle's radius in uv of the screen's height,
    // w the screen's width over its height. Only the right tube shows dest; the left shows i2.
    public static Vector4 FieldInMainUv { get; internal set; }

    // How far the device is flipped down: 0 up, 1 down (the game's own flip, as it eases).
    public static float Deployed { get; internal set; }

    // The rectangle the other mod draws its picture in, as shares of the eyepiece circle's diameter (width, height),
    // centred on FieldInMainUv.xy: shown whole in both lenses wherever they see it. The other mod may set it.
    public static Vector2 FusedRect = new Vector2(0.68f, 0.52f);

    // The tubes' gain the other mod has set (0 dark, 1 as they are): both eyes' tube picture times this on a claimed
    // frame, i2 handed to Fuse included, so the other mod does not dim it again. Optional; 1 leaves the tubes as they are.
    public static float I2Gain = 1f;

    // Set by the other mod: (i2, dest) -> whether dest was written.
    public static Func<RenderTexture, RenderTexture, bool>? Fuse;
}
