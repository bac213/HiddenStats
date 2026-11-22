using System.Collections.Generic;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using HiddenStats.Client.Core;
using UnityEngine;
using UnityEngine.UI;

namespace HiddenStats.Client.UI;

/// <summary>
/// Hidden stats in the weapon modding / edit-build screens' floating stats panel
/// (<see cref="CharacteristicsPanel"/>, used by <c>ItemObserveScreen</c>).
///
/// Vanilla (decompiled): <c>InstantiateItemFields(item, compareItem)</c> puts one full bar per
/// <c>FullBar</c> attribute and then one compact cell per <c>Compact</c> attribute into the same
/// single-column <c>_charPanelContainer</c>. <c>_compareItem</c> is a persistent clone of the
/// weapon; when a mod is previewed, <c>OnCompareRequired(mod, slot)</c> installs it on the clone
/// (and <c>ResetCompare(slot)</c> restores it) and vanilla bars follow through attribute updates.
///
/// Our rows are added straight into that container (no compression, same layout as vanilla):
/// bars right after the last vanilla full bar, cells at the end. They are removed before vanilla
/// rebuilds (prefix) so vanilla's sibling-index bookkeeping (<c>TemplateList</c> counts foreign
/// children) is unaffected, recreated afterwards, and re-ordered after every compare change
/// (vanilla may add a bar for a new attribute then). Compare preview: rows rebuilt for the clone.
/// </summary>
internal static class ModdingStatsBlock
{
    private static readonly FieldInfo CompareItemField = AccessTools.Field(typeof(CharacteristicsPanel), "_compareItem");
    private static readonly FieldInfo TooltipField = AccessTools.Field(typeof(CharacteristicsPanel), "_tooltip");

    private static bool _loggedOnce;

    private sealed class OurRow : MonoBehaviour
    {
    }

    private sealed class Holder : MonoBehaviour
    {
        public readonly List<HiddenStatsBlock.BarView> Bars = new List<HiddenStatsBlock.BarView>();
        public readonly List<Transform> Cells = new List<Transform>();
    }

    /// <summary>Prefix of <c>InstantiateItemFields</c>: drop our rows before vanilla rebuilds.</summary>
    internal static void Clear(CharacteristicsPanel panel)
    {
        Holder holder = panel.GetComponent<Holder>();
        if (holder == null)
        {
            return;
        }

        foreach (HiddenStatsBlock.BarView bar in holder.Bars)
        {
            Destroy(bar.View != null ? bar.View.transform : null);
        }
        foreach (Transform cell in holder.Cells)
        {
            Destroy(cell);
        }
        holder.Bars.Clear();
        holder.Cells.Clear();
    }

    /// <summary>Postfix of <c>InstantiateItemFields(item, compareItem)</c>.</summary>
    internal static void Build(CharacteristicsPanel panel, Item item, Item compareItem)
    {
        Clear(panel);
        if (item == null || !HiddenStatsPlugin.ShowHiddenStats.Value)
        {
            return;
        }

#pragma warning disable CS0618
        Transform container = panel._charPanelContainer;
        CharacteristicPanel barTemplate = panel._charPanelTemplate;
        CompactCharacteristicPanel cellTemplate = panel._compactCharTemplate;
#pragma warning restore CS0618
        if (container == null || barTemplate == null || cellTemplate == null)
        {
            return;
        }

        List<HiddenStatRow> rows = HiddenStatRows.Build(item);
        if (rows == null || rows.Count == 0)
        {
            return;
        }

        Holder holder = panel.GetComponent<Holder>();
        if (holder == null) // explicit check: Unity fake-null breaks ??
        {
            holder = panel.gameObject.AddComponent<Holder>();
        }
        SimpleTooltip tooltip = TooltipField?.GetValue(panel) as SimpleTooltip;

        foreach (HiddenStatRow row in rows)
        {
            if (row.IsBar)
            {
                HiddenStatsBlock.BarView bar = HiddenStatsBlock.CreateBar(barTemplate, container, row, tooltip);
                bar.View.gameObject.AddComponent<OurRow>();
                holder.Bars.Add(bar);
            }
            else
            {
                CompactCharacteristicPanel cell = HiddenStatsBlock.CreateCell(cellTemplate, container, row, tooltip);
                cell.gameObject.AddComponent<OurRow>();
                holder.Cells.Add(cell.transform);
            }
        }

        Order(container, holder);
        HiddenStatsBlock.ApplyCompare(holder.Bars, compareItem ?? CompareItemField?.GetValue(panel) as Item);
        LayoutRebuilder.MarkLayoutForRebuild((RectTransform)panel.transform);

        if (!_loggedOnce)
        {
            _loggedOnce = true;
            HiddenStatsPlugin.Log?.LogInfo($"[HiddenStats] modding panel: {holder.Bars.Count} bars + {holder.Cells.Count} cells for {item.GetType().Name} '{item.TemplateId}'");
        }
    }

    /// <summary>Postfix of <c>OnCompareRequired</c> / <c>ResetCompare</c>: the clone changed.</summary>
    internal static void Compare(CharacteristicsPanel panel)
    {
        Holder holder = panel.GetComponent<Holder>();
        if (holder == null || holder.Bars.Count == 0)
        {
            return;
        }

#pragma warning disable CS0618
        Transform container = panel._charPanelContainer;
#pragma warning restore CS0618
        if (container != null)
        {
            Order(container, holder);
        }
        HiddenStatsBlock.ApplyCompare(holder.Bars, CompareItemField?.GetValue(panel) as Item);
    }

    /// <summary>Bars right after the last vanilla full bar; cells at the very end.</summary>
    private static void Order(Transform container, Holder holder)
    {
        int lastVanillaBar = -1;
        for (int i = 0; i < container.childCount; i++)
        {
            Transform child = container.GetChild(i);
            if (child.gameObject.activeSelf && child.GetComponent<OurRow>() == null && child.GetComponent<CharacteristicPanel>() != null)
            {
                lastVanillaBar = i;
            }
        }

        // Pull ours out of the way first (to the end), then insert bars in order.
        foreach (HiddenStatsBlock.BarView bar in holder.Bars)
        {
            if (bar.View != null)
            {
                bar.View.transform.SetAsLastSibling();
            }
        }
        foreach (Transform cell in holder.Cells)
        {
            if (cell != null)
            {
                cell.SetAsLastSibling();
            }
        }

        int index = lastVanillaBar + 1;
        foreach (HiddenStatsBlock.BarView bar in holder.Bars)
        {
            if (bar.View != null)
            {
                bar.View.transform.SetSiblingIndex(index++);
            }
        }
    }

    private static void Destroy(Transform t)
    {
        if (t == null)
        {
            return;
        }
        t.SetParent(null, false);
        Object.Destroy(t.gameObject);
    }
}
