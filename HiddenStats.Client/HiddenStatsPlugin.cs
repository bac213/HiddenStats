using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HiddenStats.Client.Patches;

namespace HiddenStats.Client;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class HiddenStatsPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.spt.hiddenstats.client";
    public const string PluginName = "HiddenStats";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log { get; private set; }

    // 1. 通用
    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<bool> ShowHiddenStats;

    // 2. 紧凑属性区
    internal static ConfigEntry<bool> CompactAttributes;
    internal static ConfigEntry<int> FullBarColumns;
    internal static ConfigEntry<float> RowHeightScale;
    internal static ConfigEntry<float> FontScale;

    // 9. 调试
    internal static ConfigEntry<bool> LayoutDump;
    internal static ConfigEntry<bool> DebugLogging;

    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;
        BindConfig();

        _harmony = new Harmony(PluginGuid);
        try
        {
            InspectPanelPatch.Apply(_harmony);
        }
        catch (Exception ex)
        {
            Log.LogError($"{PluginName}: failed to apply patches: {ex}");
        }

        Log.LogInfo($"{PluginName} {PluginVersion} loaded");
    }

    private void BindConfig()
    {
        var t = new ConfigText(Config);

        const string catGeneral = "1. 通用";
        Enabled = t.Bind(
            "1. General", "Enabled", true,
            catGeneral, "总开关",
            "启用或关闭 HiddenStats。");
        ShowHiddenStats = t.Bind(
            "1. General", "ShowHiddenStats", true,
            catGeneral, "显示隐藏属性",
            "在武器与弹匣检视窗的原版属性下方显示「隐藏属性」：组装后的实际故障率、每发耐久损耗、升温与散热、耐久精度惩罚、后坐方向/回正/阻尼等。鼠标悬停查看计算说明。");

        const string catCompact = "2. 紧凑属性区";
        CompactAttributes = t.Bind(
            "2. Compact", "CompactAttributes", true,
            catCompact, "压缩原版属性区",
            "压缩物品检视窗中原版属性条（耐久、人机工效、精度、后坐力等）与下方小格属性的显示空间。重新打开检视窗生效。");
        FullBarColumns = t.Bind(
            "2. Compact", "FullBarColumns", 2,
            catCompact, "属性条列数",
            "原版属性条的排列列数。1 = 原版单列，2 = 两列并排。重新打开检视窗生效。",
            ConfigText.Range(1, 2));
        RowHeightScale = t.Bind(
            "2. Compact", "RowHeightScale", 0.7f,
            catCompact, "行高比例",
            "属性条与小格属性的行高相对原版的比例。重新打开检视窗生效。",
            ConfigText.Range(0.5f, 1f));
        FontScale = t.Bind(
            "2. Compact", "FontScale", 0.85f,
            catCompact, "字号比例",
            "属性区文字字号相对原版的比例。重新打开检视窗生效。",
            ConfigText.Range(0.6f, 1f));

        t.Advanced = true;
        const string catDebug = "9. 调试";
        LayoutDump = t.Bind(
            "9. Debug", "LayoutDump", false,
            catDebug, "输出布局结构",
            "每次启动游戏后，首次打开武器检视窗时将原版属性区的 UI 布局结构写入 BepInEx\\LogOutput.log。");
        DebugLogging = t.Bind(
            "9. Debug", "DebugLogging", false,
            catDebug, "调试日志",
            "输出更详细的调试日志。");
    }

    internal static void DebugLog(string message)
    {
        if (DebugLogging != null && DebugLogging.Value)
        {
            Log?.LogInfo("[HiddenStats Debug] " + message);
        }
    }
}
