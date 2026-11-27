using UnityEngine;

namespace EFTNewCamera;

// The blend: how much of each thing is seen, and where it stands, while the two eyes' views are laid one over the other
// (a goggle before one eye, the bare eye aiming). All of it is worked out here, in one place, from three numbers; the
// drawing (NightVisionEyes.cs, NightVisionDirect.cs, the rest of TwoEyes.cs, the shaders) only reads what is given here
// and works none of it out again. (It had been worked out piece by piece where each piece was drawn, and two places
// that disagreed about one piece made the hole of 2026-11-20.)
//
// The three numbers, each 0 to 1:
//   _aim     how far the bare eye's aim has come up (0 not aiming, 1 aimed);
//   _lead    which eye leads (0 the goggle's, 1 the bare eye; F12 单筒：切换主视眼);
//   Handed   once the aim is let go, how far all has been handed back to the game's own drawing (a fifth of a second).
//
// The five things: the goggle eye's gun, the bare eye's gun, the device as the goggle's eye sees it, the device as the
// bare eye sees it, and the eyepieces' picture.
internal static partial class TwoEyes
{
    // How much of what the eye that does not lead sees is seen over the leading eye's: half (the user's, 2026-11-18).
    internal const float BareSeen = 0.5f;

    // ---------------------------------------------------------------- the two guns

    // The bare eye leading, its gun is whole from the first quarter of the aim's way up on: over that quarter the two
    // guns are all but in one place, and one takes the other's place unseen.
    private static float Whole => Mathf.Clamp01(_aim * 4f);

    // How much of the bare eye's gun is seen (it is laid over everything, without night vision). The goggle's eye
    // leading: half, as the aim comes up. The bare eye leading: whole. Either way none once all is handed back.
    private static float GunBareShare => Mathf.Lerp(_aim * BareSeen, Whole, _lead) * (1f - Handed);

    // How much of the goggle eye's gun is seen (in the frame, through the goggle where it stands in its picture). The
    // goggle's eye leading: whole. The bare eye leading: half, and whole again as all is handed back.
    private static float GunGoggleShare => Mathf.Lerp(1f, Mathf.Lerp(1f, BareSeen, Whole * (1f - Handed)), _lead);

    // How blurred the bare eye's gun is (0 sharp, 1 as blurred as it gets): the further the aim is from up, and all the
    // way as it is handed back, so it meets the goggle's blurred picture of the gun.
    private static float GunBareBlur => Mathf.Max(1f - _aim, Handed);

    // How far each gun stands out from where the camera sees it toward its own eye's place (0 at the camera's, 1 at its
    // eye's): out with the aim, back as all is handed back. The bare eye's none of the way once that eye leads: the
    // camera is then at that eye.
    private static float GunGoggleOut => _aim * (1f - Handed);
    private static float GunBareOut => (1f - _lead) * _aim * (1f - Handed);

    // ---------------------------------------------------------------- the sight on the bare eye's gun

    // How much of a scope's lens (its magnified picture) is laid over what lies behind it: as its gun is. The goggle's
    // eye leading, half; the bare eye leading, whole. (Not a setting any more, 1.9.38: the user's, 2026-11-27, "确保是
    // 这样的，不再允许玩家调整"; the F12 value had been 0.25 in their own settings, three quarters of the lens.)
    private static float SightLensShare => Mathf.Lerp(BareSeen, 1f, _lead) * _aim * (1f - Handed);

    // How much of a holographic or red-dot sight's window is laid in (the two eyes' views of what is seen through it):
    // as much as that gun is there.
    private static float SightWindowShare => Mathf.Lerp(_aim, Whole, _lead) * (1f - Handed);

    // ---------------------------------------------------------------- the device's two views, and the picture

    internal readonly struct DeviceBlend
    {
        // How far each eye is at its own place (0: both views from about where the camera is, as when not aiming;
        // 1: the goggle's eye right behind its eyepiece, the bare eye beside it).
        internal readonly float Apart;
        // How far the two views are laid one over the other, all over the frame, and not side by side.
        internal readonly float Mix;
        // How much of each view is seen once they are laid so: the leading eye's whole, the other's half.
        internal readonly float GoggleShare, BareShare;
        // How much the bare eye's view of the device stands before the eyepieces' picture (the picture not laid where
        // that view has the device), and so how much of that view is put into the frame within the goggle eye's
        // eyepiece, where the picture is made from the frame.
        internal readonly float BareBeforePicture;
        // How far the bare eye's view of a monocular is drawn from that eye's own place (1), not from where the goggle
        // eye's is drawn (0).
        internal readonly float BareOut;
        // How much of the eyepieces' picture is laid over the frame: whole, but half while the bare eye leads (1.9.38,
        // the user's, 2026-11-27: "如果是裸眼混合，就让裸眼的画面实心，混合过来的夜视画面透明度50%"; it had been whole
        // whichever eye led).
        internal readonly float PictureShare;

        internal DeviceBlend(float apart, float mix, float goggleShare, float bareShare, float bareBeforePicture, float bareOut, float pictureShare)
        {
            PictureShare = pictureShare;
            Apart = apart;
            Mix = mix;
            GoggleShare = goggleShare;
            BareShare = bareShare;
            BareBeforePicture = bareBeforePicture;
            BareOut = bareOut;
        }
    }

    // direct: the device is drawn into the main camera's frame (the usual way); monocular: a goggle of one tube.
    internal static DeviceBlend Device(bool direct, bool monocular)
    {
        float apart = BareAimShare * CameraPosition.ViewAimed;
        // A monocular, the bare eye leading: the two views slide apart from one place (BareOut), and the bare eye's is
        // whole from the first quarter of the way on. Otherwise they come and go by how far apart the eyes are.
        float mix = direct && monocular ? Mathf.Lerp(apart, Mathf.Clamp01(apart * 4f), _lead) : apart;
        bool bareLeads = _lead > 0.5f;
        // Before the picture only while the bare eye leads, only over the last of the way out (from 0.6 on: before that
        // its view of the device passes right over the eyepiece), and not once the aim is let go.
        float late = Mathf.Clamp01((apart - 0.6f) / 0.4f);
        late = late * late * (3f - 2f * late);
        float before = direct ? late * (1f - Handed) * _lead : 0f;
        float picture = Mathf.Lerp(1f, BareSeen, _lead * mix * (1f - Handed));
        NoteShares(mix, bareLeads, picture);
        return new DeviceBlend(apart, mix, bareLeads ? BareSeen : 1f, bareLeads ? 1f : BareSeen, before, 1f - _lead * (1f - apart), picture);
    }

    // Once for each eye's leading, with the aim all the way up and the lead settled: every share as it is handed to the
    // shaders, so that what the user asked to be made sure of is in the log and not only in the code.
    private static int _sharesNoted;
    private static void NoteShares(float mix, bool bareLeads, float picture)
    {
        int bit = bareLeads ? 2 : 1;
        if ((_sharesNoted & bit) != 0 || !BareAims || _aim < 0.999f || Handed > 0f || mix < 0.999f || _lead > 0.001f && _lead < 0.999f)
            return;
        _sharesNoted |= bit;
        NewCameraPlugin.Log?.LogInfo($"Two eyes, {(bareLeads ? "the bare eye leading" : "the goggle's eye leading")}, aimed: the bare eye's gun {GunBareShare:F2}, the goggle eye's gun {GunGoggleShare:F2}, the bare gun's scope lens {SightLensShare:F2}, its sight window {SightWindowShare:F2}; the device as the goggle's eye sees it {(bareLeads ? BareSeen : 1f):F2}, as the bare eye sees it {(bareLeads ? 1f : BareSeen):F2}; the eyepieces' picture {picture:F2} (1 whole).");
    }
}
