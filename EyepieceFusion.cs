using System;
using System.Collections.Generic;
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
//
// More than one such mod, each for a device of its own (K-ENVG-B's, K-FPANO's; added 1.8.165, Version still 1): each
// calls Register with its device's root node name and its own handler, and Configure for its own rectangle and gain.
// On a claimed frame only the handler of the device worn is called (ClaimedDevice says which). A device with no
// handler registered is not claimed at all, so its mod goes on drawing its own way; but the first device, the ENVG-B
// ("nvg_envgb_psq42_LOD0"), is claimed as ever and served by Fuse, FusedRect and I2Gain below while nothing is
// registered for it, so a K-ENVG-B from before this works unchanged. A handler that throws is not called again; the
// others are.
// FieldInMainUv is the circle of the tube the picture goes into: the ENVG-B's right eyepiece; on a device with two
// tubes to an eyepiece (the F-PANO), the right eyepiece's centre tube, not the whole eyepiece.
public static class EyepieceFusion
{
    // The device the old members below serve.
    internal const string FirstDevice = "nvg_envgb_psq42_LOD0";

    private sealed class Entry
    {
        internal Func<RenderTexture, RenderTexture, bool>? Fuse;
        internal Vector2 Rect;
        internal float Gain = 1f;
        internal bool Configured, Failed;
    }
    private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

    // The device claimed on ClaimedFrame: its model's root node name. Null before any.
    public static string? ClaimedDevice { get; internal set; }

    // A handler for one device, by its model's root node name: (i2, dest) -> whether dest was written, as Fuse.
    public static void Register(string deviceNode, Func<RenderTexture, RenderTexture, bool> fuse)
    {
        if (string.IsNullOrEmpty(deviceNode) || fuse == null)
            return;
        Entry entry = EntryOf(deviceNode);
        entry.Fuse = fuse;
        entry.Failed = false;
    }

    // That device's own rectangle (as FusedRect) and tubes' gain (as I2Gain). May be called again at any time.
    public static void Configure(string deviceNode, Vector2 fusedRect, float i2Gain)
    {
        if (string.IsNullOrEmpty(deviceNode))
            return;
        Entry entry = EntryOf(deviceNode);
        entry.Rect = fusedRect;
        entry.Gain = i2Gain;
        entry.Configured = true;
    }

    private static Entry EntryOf(string deviceNode)
    {
        if (!Entries.TryGetValue(deviceNode, out Entry entry))
            Entries[deviceNode] = entry = new Entry();
        return entry;
    }

    // Whether a device's frames are claimed: a handler registered for it, or the first device (served by Fuse).
    internal static bool Serves(string deviceNode) =>
        deviceNode == FirstDevice || Entries.TryGetValue(deviceNode, out Entry entry) && entry.Fuse != null;

    // The handler to call for a device this frame: its own, else (the first device) Fuse; none once it has thrown.
    internal static Func<RenderTexture, RenderTexture, bool>? HandlerOf(string deviceNode)
    {
        if (Entries.TryGetValue(deviceNode, out Entry entry) && entry.Fuse != null)
            return entry.Failed ? null : entry.Fuse;
        return deviceNode == FirstDevice && !_firstFailed ? Fuse : null;
    }
    private static bool _firstFailed;

    internal static void Failed(string deviceNode)
    {
        if (Entries.TryGetValue(deviceNode, out Entry entry) && entry.Fuse != null)
            entry.Failed = true;
        else
            _firstFailed = true;
    }

    internal static Vector2 RectOf(string deviceNode) =>
        Entries.TryGetValue(deviceNode, out Entry entry) && entry.Configured ? entry.Rect : FusedRect;

    internal static float GainOf(string deviceNode) =>
        Entries.TryGetValue(deviceNode, out Entry entry) && entry.Configured ? entry.Gain : deviceNode == FirstDevice ? I2Gain : 1f;

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

    // The tubes' gain the other mod has set (0 dark, 1 as they are): both eyes' tube picture times this on every frame
    // of a fusion device (claimed or not), i2 handed to Fuse included, so the other mod does not dim it again. Optional; 1 leaves the tubes as they are.
    public static float I2Gain = 1f;

    // Set by the other mod: (i2, dest) -> whether dest was written.
    public static Func<RenderTexture, RenderTexture, bool>? Fuse;

    // Infrared things of another mod's (an IR strobe's flash): seen through the tubes only. (Added 1.8.167, Version
    // still 1: look for the members by name.)
    // IrLayerActive: K-3PS-C has the night-vision picture in its own hands now (eyepieces shown, or a goggle on its way
    // down or up): the game's own picture is not on the screen, and nothing infrared belongs in the main camera's frame.
    // False: K-3PS-C shows nothing of night vision; the other mod draws its own way, as without K-3PS-C.
    public static bool IrLayerActive => NightVisionEyepieces.IrInOurHands;

    // One mesh for this frame, drawn into the IR layer (the main camera's view, against its depth, after its
    // transparent things; the tubes amplify it with the rest). Called each frame it is to be seen, before the main
    // camera renders (Update, LateUpdate). The material's pass 0, an additive one. The block is read when the camera
    // renders: one block a call, left as it is till then. Returns IrLayerActive: true, it is in K-3PS-C's hands (drawn
    // through the tubes, or rightly not at all: a thermal device, the tubes not looking); false, not taken.
    public static bool DrawIr(Mesh mesh, Matrix4x4 matrix, Material material, MaterialPropertyBlock? block)
    {
        if (!NightVisionEyepieces.IrInOurHands)
            return false;
        if (mesh != null && material != null)
            NightVisionEyepieces.IrFromOutside(mesh, matrix, material, block);
        return true;
    }
}
