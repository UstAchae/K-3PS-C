using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace EFTNewCamera;

// The helmet tilted on the head, apart from the head: its brim lifted (never lowered) about the skull's middle, the camera's right
// axis, as the bones are posed each frame (after the head model's lift); what hangs from it (the night vision) goes
// with it. Set by hotkeys held (F12), kept for each helmet (the worn item, as each mount keeps its own: NightVisionMount)
// in its own file; a helmet never set sits as the game has it.
internal static class HeadwearTilt
{
    private const float Speed = 2.5f;         // degrees per second while a key is held
    internal const float Range = 5f;          // up only, degrees
    private const float SkullMiddle = 0.09f;  // the pivot above the head bone, along the camera's up (m)

    internal static float Degrees;               // the brim up, the worn helmet's
    internal static KeyboardShortcut Up = new KeyboardShortcut(KeyCode.Equals);
    internal static KeyboardShortcut Down = new KeyboardShortcut(KeyCode.Minus);

    private static Transform? _model;
    private static Vector3 _restPosition;
    private static Quaternion _restRotation;
    private static bool _turned;
    private static string? _helmetId;

    private static readonly string HelmetsPath = Path.Combine(BepInEx.Paths.ConfigPath, "justachae.thirdpersoncamera.helmets.txt");
    private static Dictionary<string, float>? _saved;   // by the helmet item's id
    private static readonly object WriteLock = new object();

    private static float SavedFor(string? id)
    {
        if (_saved == null)
        {
            _saved = new Dictionary<string, float>();
            try
            {
                if (File.Exists(HelmetsPath))
                {
                    foreach (string line in File.ReadAllLines(HelmetsPath))
                    {
                        string[] f = line.Split(' ');
                        if (f.Length == 2 && float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
                            _saved[f[0]] = d;
                    }
                }
            }
            catch (IOException e)
            {
                NewCameraPlugin.Log?.LogWarning("Helmet tilts not read: " + e.Message);
            }
        }
        return id != null && _saved.TryGetValue(id, out float degrees) ? degrees : 0f;
    }

    private static void Save()
    {
        if (_helmetId == null || _saved == null)
            return;
        _saved[_helmetId] = Mathf.Round(Degrees * 10f) / 10f;
        var lines = new List<string>();
        foreach (KeyValuePair<string, float> e in _saved)
            lines.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1:R}", e.Key, e.Value));
        // Written off the main thread, so a slow disk never holds up a frame; one write at a time, in order.
        string path = HelmetsPath;
        System.Threading.Tasks.Task.Run(() =>
        {
            lock (WriteLock)
            {
                try
                {
                    File.WriteAllLines(path, lines);
                }
                catch (IOException e)
                {
                    NewCameraPlugin.Log?.LogWarning("Helmet tilts not saved: " + e.Message);
                }
            }
        });
    }

    internal static void Apply(Player player, Transform head)
    {
        Transform? model = player.PlayerBody != null && player.PlayerBody.SlotViews.TryGetByKey(EquipmentSlot.Headwear, out PlayerBody.SlotView? view)
            && view?.Model != null ? view.Model.transform : null;
        if (model != _model)
        {
            Release();
            if (model != null)
            {
                _restPosition = model.localPosition;
                _restRotation = model.localRotation;
                // This helmet's own tilt.
                Item? helmet = EquipmentIds.Worn(player)?.GetSlot(EquipmentSlot.Headwear).ContainedItem;
                _helmetId = helmet?.Id.ToString();
                Degrees = Mathf.Clamp(SavedFor(_helmetId), 0f, Range);
            }
            _model = model;
        }
        if (_model == null)
            return;

        // At the hip only: aiming, the same keys set the aim's distance.
        Adjust(player.ProceduralWeaponAnimation == null || !player.ProceduralWeaponAnimation.IsAiming);
        _model.SetLocalPositionAndRotation(_restPosition, _restRotation);
        if (Degrees < 0.01f)
            return;
        Quaternion camera = CameraDirection.Resolve(head.rotation);
        _model.RotateAround(head.position + camera * Vector3.up * SkullMiddle, camera * Vector3.right, -Degrees);
    }

    // Turned while a key is held; written to the config once let go.
    private static void Adjust(bool free)
    {
        int direction = free ? (NightVisionEyepieces.Held(Up) ? 1 : 0) - (NightVisionEyepieces.Held(Down) ? 1 : 0) : 0;
        if (direction != 0)
        {
            Degrees = Mathf.Clamp(Degrees + direction * Speed * Time.deltaTime, 0f, Range);
            _turned = true;
        }
        else if (_turned)
        {
            _turned = false;
            Save();
        }
    }

    // The player's helmets on models of the player shown elsewhere (the inventory's, the loading screen's), each tilted
    // as kept for it, as the worn one is; set every frame (the head moves), let go before the model goes back to the
    // game's pool (NightVisionPreviewMounts).
    private sealed class Shown
    {
        internal Transform Model = null!, Head = null!;
        internal Vector3 RestPosition;
        internal Quaternion RestRotation;
        internal float Degrees;
    }

    private static readonly List<Shown> _shown = new List<Shown>();
    private static readonly Dictionary<string, float> _shownByKind = new Dictionary<string, float>();

    internal static void Show(PlayerBody body, Transform model, Item? helmet)
    {
        Transform? head = body.PlayerBones?.Head?.Original;
        if (head == null || helmet == null)
            return;
        // The loading screen's model wears a copy the game makes with ids of its own, right after the helmet itself: none of those is
        // ever saved, so such a one goes by the last of its kind that was.
        string id = helmet.Id.ToString(), kind = helmet.TemplateId.ToString();
        float saved = SavedFor(id);
        if (_saved != null && _saved.ContainsKey(id))
            _shownByKind[kind] = saved;
        else if (_shownByKind.TryGetValue(kind, out float sameKind))
            saved = sameKind;
        float degrees = Mathf.Clamp(saved, 0f, Range);
        if (degrees < 0.01f)
            return;
        Forget(model);
        _shown.Add(new Shown { Model = model, Head = head, RestPosition = model.localPosition, RestRotation = model.localRotation, Degrees = degrees });
    }

    internal static void Forget(Transform model)
    {
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            if (_shown[i].Model != model)
                continue;
            if (model != null)
                model.SetLocalPositionAndRotation(_shown[i].RestPosition, _shown[i].RestRotation);
            _shown.RemoveAt(i);
        }
    }

    internal static void UpdateShown()
    {
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            Shown s = _shown[i];
            if (s.Model == null || s.Head == null || !NewCameraSwitch.Enabled)
            {
                if (s.Model != null)
                    s.Model.SetLocalPositionAndRotation(s.RestPosition, s.RestRotation);
                _shown.RemoveAt(i);
                continue;
            }
            s.Model.SetLocalPositionAndRotation(s.RestPosition, s.RestRotation);
            Quaternion camera = CameraDirection.Resolve(s.Head.rotation);
            s.Model.RotateAround(s.Head.position + camera * Vector3.up * SkullMiddle, camera * Vector3.right, -s.Degrees);
        }
    }

    // The helmet back where the game has it.
    internal static void Release()
    {
        if (_model != null)
            _model.SetLocalPositionAndRotation(_restPosition, _restRotation);
        _model = null;
    }
}
