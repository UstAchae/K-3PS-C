using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;

namespace EFTNewCamera;

// Where each setting shows in the F12 window: its group, and its place in the group. The window sorts the groups by
// name and the settings in a group by Order (highest first, then by name), so the groups are numbered and every
// setting is given its place here, in the order of the list. A setting bound under its old group takes over the value
// saved there, once.
internal static class SettingsLayout
{
    private static readonly Dictionary<string, (string Section, int Order)> Places = new();

    private static readonly (string Section, string[] Keys)[] Groups =
    {
        ("", new[] { "总开关", "骨架" }),
        ("01 镜头", new[] { "水平", "垂直", "前后", "瞄准时移到瞄准眼", "瞄准眼离中线（米）", "近裁剪面（米）" }),
        ("02 前庭平衡", new[] { "总开关", "强度", "扭转补偿" }),
        ("03 瞄准", new[] { "使用 Recoil Rework", "后坐力力度", "前后距离", "前后距离调远快捷键", "前后距离调近快捷键" }),
        ("04 瞄准摆动-平时", new[] { "摆动", "跟随头部", "呼吸" }),
        ("05 瞄准摆动-屏息", new[] { "摆动", "跟随头部", "呼吸" }),
        ("06 瞄准摆动-没体力", new[] { "摆动", "跟随头部", "呼吸" }),
        ("07 胸口抵肩", new[] { "启用", "瞄准键", "按住多久算按住（秒）", "比开镜时低（米）", "比开镜时偏右（米）", "比开镜时靠前（米）", "倾斜（度）" }),
        ("08 人物显示", new[] { "显示头部", "头部仰俯", "显示头部装备", "显示面部装备", "显示眼部装备", "显示面罩滤镜" }),
        ("09 头部动作", new[] { "走路摇头", "中键扭头角度", "中键低头额外角度", "中键扭头轴前后倾斜" }),
        ("10 头盔", new[] { "头部装备仰俯", "头部装备仰起", "头部装备俯下" }),
        ("11 夜视仪与热成像", new[] { "夜视画面来源", "原版夜视目镜亮度", "双眼平移比例", "双眼近裁剪面（米）", "目镜光晕", "镜片反光", "近处虚化距离", "近处虚化上限", "氚光亮度", "PVS-14 左眼透明度" }),
        ("12 夜视仪挂架", new[] { "挂架升高", "挂架降低", "挂架仰起", "挂架俯下", "挂架往前", "挂架往后" }),
        ("13 鱼眼", new[] { "启用", "视野", "弯曲", "暗角", "清晰度方式", "清晰度补偿" }),
        ("14 镜头朝向（高级）", new[] { "俯仰", "偏航", "翻滚" }),
        ("15 UI 隐藏", new[] { "隐藏血条", "隐藏体力和姿态", "隐藏快捷栏", "隐藏交互选项", "隐藏弹药和射击模式提示", "隐藏瞄具提示", "隐藏战术装置提示", "隐藏手势面板", "隐藏时间" }),
    };

    // The groups the settings were in before (1.4.4), by their new group, for taking the saved values over.
    private static readonly Dictionary<string, string> Before = new()
    {
        ["01 镜头"] = "镜头位置",
        ["02 前庭平衡"] = "前庭平衡",
        ["03 瞄准"] = "瞄准",
        ["04 瞄准摆动-平时"] = "瞄准摆动-平时",
        ["05 瞄准摆动-屏息"] = "瞄准摆动-屏息",
        ["06 瞄准摆动-没体力"] = "瞄准摆动-没体力",
        ["07 胸口抵肩"] = "胸口抵肩",
        ["08 人物显示"] = "人物",
        ["09 头部动作"] = "人物",
        ["10 头盔"] = "头戴式成像装备",
        ["11 夜视仪与热成像"] = "头戴式成像装备",
        ["12 夜视仪挂架"] = "头戴式成像装备",
        ["13 鱼眼"] = "鱼眼",
        ["14 镜头朝向（高级）"] = "镜头朝向",
        ["15 UI 隐藏"] = "隐藏界面",
    };

    static SettingsLayout()
    {
        foreach ((string section, string[] keys) in Groups)
            for (int i = 0; i < keys.Length; i++)
                Places[Id(OldOf(section), keys[i])] = (section, 100 - i);
    }

    private static string OldOf(string section) => Before.TryGetValue(section, out string old) ? old : section;

    private static string Id(string section, string key) => section + "\u0001" + key;

    // Bound as before (group, key), shown where the layout puts it.
    internal static ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T defaultValue, string description)
        => Bind(config, section, key, defaultValue, new ConfigDescription(description));

    internal static ConfigEntry<T> Bind<T>(ConfigFile config, string section, string key, T defaultValue, ConfigDescription? description)
    {
        description ??= new ConfigDescription(string.Empty);
        if (!Places.TryGetValue(Id(section, key), out (string Section, int Order) place))
        {
            NewCameraPlugin.Log?.LogWarning($"Settings layout: no place for [{section}] {key}");
            return config.Bind(section, key, defaultValue, description);
        }
        TakeOver(config, new ConfigDefinition(section, key), new ConfigDefinition(place.Section, key));
        // The layout's order; whether it is shown at all, the name shown and whether it can be changed, as the caller has it.
        ConfigurationManagerAttributes? given = description.Tags is object[] tags
            ? System.Array.Find(tags, t => t is ConfigurationManagerAttributes) as ConfigurationManagerAttributes : null;
        return config.Bind(place.Section, key, defaultValue,
            new ConfigDescription(description.Description, description.AcceptableValues, new ConfigurationManagerAttributes
            {
                Order = place.Order, Browsable = given?.Browsable, DispName = given?.DispName, ReadOnly = given?.ReadOnly,
            }));
    }

    // A value saved as the setting had it before (an option since renamed), given as it is now, before it is bound.
    internal static void MapSaved(ConfigFile config, string section, string key, System.Func<string, string?> map)
    {
        PropertyInfo? property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.GetValue(config) is not Dictionary<ConfigDefinition, string> orphans)
            return;
        string placed = Places.TryGetValue(Id(section, key), out (string Section, int Order) place) ? place.Section : section;
        foreach (ConfigDefinition definition in new[] { new ConfigDefinition(section, key), new ConfigDefinition(placed, key) })
        {
            if (orphans.TryGetValue(definition, out string value) && map(value) is string now)
                orphans[definition] = now;
        }
    }

    // The F12 window lists the groups in the order their first setting was bound, not by name: once everything is
    // bound, the settings are put in the layout's order (the config's entries, kept in the order they were added).
    internal static void Arrange(ConfigFile config)
    {
        PropertyInfo? property = typeof(ConfigFile).GetProperty("Entries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (property?.GetValue(config) is not Dictionary<ConfigDefinition, ConfigEntryBase> entries)
        {
            NewCameraPlugin.Log?.LogWarning("Settings layout: the config's entries were not found; the groups keep the order they were bound in.");
            return;
        }
        Dictionary<string, int> groupIndex = new();
        for (int i = 0; i < Groups.Length; i++)
            groupIndex[Groups[i].Section] = i;
        List<KeyValuePair<ConfigDefinition, ConfigEntryBase>> sorted = new(entries);
        int Rank(ConfigDefinition d) => groupIndex.TryGetValue(d.Section, out int g) ? g : Groups.Length;
        int Place(ConfigEntryBase e) => e.Description?.Tags is object[] tags
            && System.Array.Find(tags, t => t is ConfigurationManagerAttributes) is ConfigurationManagerAttributes a && a.Order is int o ? o : 0;
        sorted.Sort((x, y) =>
        {
            int c = Rank(x.Key).CompareTo(Rank(y.Key));
            return c != 0 ? c : Place(y.Value).CompareTo(Place(x.Value));
        });
        lock (entries)
        {
            entries.Clear();
            foreach (KeyValuePair<ConfigDefinition, ConfigEntryBase> pair in sorted)
                entries.Add(pair.Key, pair.Value);
        }
    }

    // The value saved under the old place, if any, moved to the new one before it is bound there.
    private static void TakeOver(ConfigFile config, ConfigDefinition from, ConfigDefinition to)
    {
        if (from.Equals(to))
            return;
        PropertyInfo? property = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic);
        if (property?.GetValue(config) is not Dictionary<ConfigDefinition, string> orphans || !orphans.TryGetValue(from, out string value))
            return;
        orphans.Remove(from);
        if (!orphans.ContainsKey(to))
            orphans[to] = value;
    }
}
