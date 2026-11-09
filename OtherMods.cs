using BepInEx.Bootstrap;

namespace EFTNewCamera;

// Other authors' mods this one works with, named in F12 by their full names, as BepInEx has them, or, not installed,
// by the name they go by with "[未安装]" after it.
internal static class OtherMods
{
    internal const string ReworkGuid = "com.pein.camerarecoilmod", ReworkName = "SPTRecoilReworkLegacy";
    internal const string BrnvgGuid = "com.borkel.nvgmasks", BrnvgName = "Borkel's Realistic NVGs";
    internal const string NotInstalled = " [未安装]";

    internal static bool Installed(string guid)
    {
        return Chainloader.PluginInfos.TryGetValue(guid, out BepInEx.PluginInfo info) && info.Instance != null;
    }

    internal static string Label(string guid, string name)
    {
        if (Chainloader.PluginInfos.TryGetValue(guid, out BepInEx.PluginInfo info) && info.Instance != null)
        {
            string? full = info.Metadata?.Name;
            return string.IsNullOrEmpty(full) ? name : full!;
        }
        return name + NotInstalled;
    }

    // A saved choice of the mod, by any name it has had in the settings (bare, full, or marked not installed).
    internal static bool IsChoice(string value, string guid, string name, string shortName)
    {
        return value == shortName || value == name || value == name + NotInstalled || value == Label(guid, name);
    }
}
