using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace EFTNewCamera;

// Lasers on the player or the weapon draw where the game posed them, before this mod carries the gun. Their
// LateUpdate is held back and run again for each camera as it culls, with the emitter carried along with the gun,
// and drawn for that camera only.
internal static class LaserReplay
{
    private static MethodInfo? _laserLateUpdate;
    private static Camera? _drawCamera;
    private static int _laserFrame = -1;
    private static bool _replayingLaser;
    private static readonly List<MonoBehaviour> DeferredLasers = new();

    // The lasers' LateUpdate, the method patched; null when the game has no LaserBeam.
    internal static MethodInfo? FindLateUpdate()
    {
        System.Type? laserType = AccessTools.TypeByName("LaserBeam");
        _laserLateUpdate = laserType == null ? null : AccessTools.Method(laserType, "LateUpdate");
        return _laserLateUpdate;
    }

    internal static void Replay(Camera camera)
    {
        if (_laserLateUpdate == null || DeferredLasers.Count == 0)
            return;

        _drawCamera = camera;
        _replayingLaser = true;
        try
        {
            foreach (MonoBehaviour laser in DeferredLasers)
            {
                if (laser == null || !laser.isActiveAndEnabled)
                    continue;

                Transform emitter = laser.transform;
                bool alreadyMoved = EmitterAlreadyMoved(emitter);
                Vector3 saved = emitter.position;
                Quaternion savedRotation = emitter.rotation;
                if (AimArms.GunCarried && !alreadyMoved)
                    AimArms.CarryWithGun(emitter);
                _laserLateUpdate.Invoke(laser, null);
                if (AimArms.GunCarried && !alreadyMoved)
                    emitter.SetPositionAndRotation(saved, savedRotation);
            }
        }
        finally
        {
            _replayingLaser = false;
            _drawCamera = null;
        }
    }

    // The camera a laser is being drawn for now (null: drawn by the game for all of them).
    internal static Camera? DrawCamera => _drawCamera;

    public static Camera? GetDrawCamera()
    {
        // Under an upscaler the FPS camera's lasers are drawn apart and put in after the upscale (NightVisionIr.cs).
        return NightVisionEyepieces.LaserDrawCamera(_drawCamera);
    }

    internal static IEnumerable<CodeInstruction> DrawForReplayCamera(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        MethodInfo? getCamera = typeof(LaserReplay).GetMethod(nameof(GetDrawCamera), BindingFlags.Public | BindingFlags.Static);
        if (getCamera == null)
            return list;

        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].opcode != OpCodes.Call || list[i].operand is not MethodInfo called || called.Name != nameof(Graphics.DrawMesh))
                continue;

            for (int previous = i - 1; previous >= 0 && previous >= i - 8; previous--)
            {
                if (list[previous].opcode != OpCodes.Ldnull)
                    continue;

                list[previous] = new CodeInstruction(OpCodes.Call, getCamera);
                break;
            }
        }

        return list;
    }

    private static bool EmitterAlreadyMoved(Transform emitter)
    {
        foreach (Transform arm in BodySkins.Upperarms)
        {
            if (arm != null && emitter.IsChildOf(arm))
                return true;
        }

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        Transform? weapon = player?.PlayerBones?.Weapon_Root_Anim;
        return weapon != null && emitter.IsChildOf(weapon);
    }

    internal static bool LaserLateUpdatePrefix(MonoBehaviour __instance)
    {
        if (_replayingLaser || __instance == null || !NewCameraSwitch.Enabled)
            return true;

        if (_laserFrame != Time.frameCount)
        {
            DeferredLasers.Clear();
            _laserFrame = Time.frameCount;
        }

        Player? player = Comfort.Common.Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !player.IsYourPlayer)
            return true;
        // The free camera places nothing; the game's own drawing stands.
        if (player.PointOfView == EPointOfView.FreeCamera)
            return true;
        // From the hip too, the gun at rest included: left to the game's own LateUpdate there, the beam came away from the
        // emitter whenever the mouse stopped (and jumped between the two as it moved), though the emitter stood where it was.

        Transform playerRoot = ((Component)player).transform;
        Transform? weapon = player.PlayerBones?.Weapon_Root_Anim;
        bool onPlayer = __instance.transform.IsChildOf(playerRoot);
        bool onWeapon = weapon != null && __instance.transform.IsChildOf(weapon);
        if (!onPlayer && !onWeapon)
            return true;

        DeferredLasers.Add(__instance);
        return false;
    }
}
