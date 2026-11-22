using System;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using HiddenStats.Client.UI;

namespace HiddenStats.Client.Patches;

/// <summary>
/// <c>ItemSpecificationPanel.method_1</c> fills the whole inspect panel (it calls
/// <see cref="ItemSpecificationPanel.RecreateAttributeBars"/> and then shows the durability /
/// mastering rows). <c>RecreateAttributeBars</c> is also called on its own when the item
/// changes (e.g. a mod is attached), which recreates the attribute rows; <c>Refresh</c> runs on
/// item events (mods, ammo, chamber check) and is hooked so the hidden stats follow those changes.
/// </summary>
internal static class InspectPanelPatch
{
    internal static void Apply(Harmony harmony)
    {
        MethodInfo fill = AccessTools.Method(typeof(ItemSpecificationPanel), "method_1")
            ?? throw new MissingMethodException(typeof(ItemSpecificationPanel).FullName, "method_1");
        MethodInfo recreate = AccessTools.Method(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.RecreateAttributeBars))
            ?? throw new MissingMethodException(typeof(ItemSpecificationPanel).FullName, nameof(ItemSpecificationPanel.RecreateAttributeBars));

        MethodInfo refresh = AccessTools.Method(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.Refresh))
            ?? throw new MissingMethodException(typeof(ItemSpecificationPanel).FullName, nameof(ItemSpecificationPanel.Refresh));

        var postfix = new HarmonyMethod(typeof(InspectPanelPatch), nameof(Postfix));
        harmony.Patch(fill, postfix: postfix);
        harmony.Patch(recreate, postfix: postfix);
        harmony.Patch(refresh, postfix: postfix); // mods / ammo / chamber changed: values may differ

        // Mod compare preview: vanilla calls Compare(clone with the dragged/hovered mod) and
        // Compare(null) to reset.
        MethodInfo compare = AccessTools.Method(typeof(ItemSpecificationPanel), nameof(ItemSpecificationPanel.Compare), new[] { typeof(Item) })
            ?? throw new MissingMethodException(typeof(ItemSpecificationPanel).FullName, nameof(ItemSpecificationPanel.Compare));
        harmony.Patch(compare, postfix: new HarmonyMethod(typeof(InspectPanelPatch), nameof(ComparePostfix)));

        // Weapon modding / edit-build screens: floating CharacteristicsPanel.
        MethodInfo modFill = AccessTools.Method(typeof(CharacteristicsPanel), nameof(CharacteristicsPanel.InstantiateItemFields))
            ?? throw new MissingMethodException(typeof(CharacteristicsPanel).FullName, nameof(CharacteristicsPanel.InstantiateItemFields));
        MethodInfo modCompare = AccessTools.Method(typeof(CharacteristicsPanel), nameof(CharacteristicsPanel.OnCompareRequired))
            ?? throw new MissingMethodException(typeof(CharacteristicsPanel).FullName, nameof(CharacteristicsPanel.OnCompareRequired));
        MethodInfo modReset = AccessTools.Method(typeof(CharacteristicsPanel), nameof(CharacteristicsPanel.ResetCompare))
            ?? throw new MissingMethodException(typeof(CharacteristicsPanel).FullName, nameof(CharacteristicsPanel.ResetCompare));
        harmony.Patch(modFill,
            prefix: new HarmonyMethod(typeof(InspectPanelPatch), nameof(ModdingFillPrefix)),
            postfix: new HarmonyMethod(typeof(InspectPanelPatch), nameof(ModdingFillPostfix)));
        var modComparePostfix = new HarmonyMethod(typeof(InspectPanelPatch), nameof(ModdingComparePostfix));
        harmony.Patch(modCompare, postfix: modComparePostfix);
        harmony.Patch(modReset, postfix: modComparePostfix);
    }

    private static void ModdingFillPrefix(CharacteristicsPanel __instance)
    {
        try
        {
            if (__instance != null)
            {
                ModdingStatsBlock.Clear(__instance);
            }
        }
        catch (Exception ex)
        {
            HiddenStatsPlugin.Log?.LogWarning($"HiddenStats: modding panel clear error: {ex}");
        }
    }

    private static void ModdingFillPostfix(CharacteristicsPanel __instance, Item item, Item compareItem)
    {
        if (__instance == null || !HiddenStatsPlugin.Enabled.Value)
        {
            return;
        }

        try
        {
            ModdingStatsBlock.Build(__instance, item, compareItem);
        }
        catch (Exception ex)
        {
            HiddenStatsPlugin.Log?.LogWarning($"HiddenStats: modding panel error: {ex}");
        }
    }

    private static void ModdingComparePostfix(CharacteristicsPanel __instance)
    {
        if (__instance == null || !HiddenStatsPlugin.Enabled.Value)
        {
            return;
        }

        try
        {
            ModdingStatsBlock.Compare(__instance);
        }
        catch (Exception ex)
        {
            HiddenStatsPlugin.Log?.LogWarning($"HiddenStats: modding panel compare error: {ex}");
        }
    }

    private static void ComparePostfix(ItemSpecificationPanel __instance, Item compareItem)
    {
        if (__instance == null || !HiddenStatsPlugin.Enabled.Value)
        {
            return;
        }

        try
        {
            HiddenStatsBlock.Compare(__instance, compareItem);
        }
        catch (Exception ex)
        {
            HiddenStatsPlugin.Log?.LogWarning($"HiddenStats: compare error: {ex}");
        }
    }

    private static void Postfix(ItemSpecificationPanel __instance)
    {
        if (__instance == null || !HiddenStatsPlugin.Enabled.Value)
        {
            return;
        }

        try
        {
            AttributeCompactor.Apply(__instance);
        }
        catch (Exception ex)
        {
            HiddenStatsPlugin.Log?.LogWarning($"HiddenStats: AttributeCompactor error: {ex}");
        }

        try
        {
            HiddenStatsBlock.Apply(__instance);
        }
        catch (Exception ex)
        {
            HiddenStatsPlugin.Log?.LogWarning($"HiddenStats: HiddenStatsBlock error: {ex}");
        }
    }
}
