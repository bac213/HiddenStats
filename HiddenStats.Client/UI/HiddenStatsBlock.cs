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
/// Adds the hidden stats to the inspect panel as two extra grids in the same column as the vanilla
/// attribute blocks (no header):
/// <list type="bullet">
/// <item><c>HiddenStatsBars</c> right after <c>_charPanelContainer</c>: dynamic values (mods /
/// ammo / magazine / durability) as vanilla full bars (<c>_charPanelTemplate</c>,
/// <see cref="CharacteristicPanel"/>), fed with <see cref="RangedItemAttribute"/>s whose
/// <c>Base</c> is the bar fill (Range 0..1). They join the vanilla mod compare preview:
/// <see cref="Compare"/> runs after <c>ItemSpecificationPanel.Compare(Item)</c> — which vanilla
/// calls with a clone of the item that has the dragged/hovered mod applied, and with null to reset
/// — rebuilds the rows for the clone and calls <c>CompareWith</c>, so the bar shows the blinking
/// compare segment exactly like vanilla bars; the value text gets a coloured delta.</item>
/// <item><c>HiddenStatsCompact</c> right after <c>_compactPanel</c>: template-only values as
/// vanilla compact cells (<c>_compactCharTemplate</c>).</item>
/// </list>
/// Both grids are the game's <see cref="FlexibleGridLayoutGroup"/> and share
/// <see cref="AttributeCompactor.CompactState"/> with the vanilla grids (row height, column
/// fallback). Rows are rebuilt on every panel fill / refresh.
/// </summary>
internal static class HiddenStatsBlock
{
    private const string BarsName = "HiddenStatsBars";
    private const string CompactName = "HiddenStatsCompact";
    private const float VanillaRowHeight = 23f;
    private const float VanillaBarGap = 4f;
    private const float VanillaColumnGap = 12.7f;

    private static readonly FieldInfo ItemField = AccessTools.Field(typeof(ItemSpecificationPanel), "_item");
    private static readonly FieldInfo TooltipField = AccessTools.Field(typeof(ItemSpecificationPanel), "_secondaryTooltip");

    // Vanilla compact-cell colours (CompactCharacteristicPanel defaults / Enhancement blue). The bar
    // prefab's own _increasingColor/_decreasingColor are unused by vanilla and not set up.
    private const string BetterHex = "54C1FF";
    private const string WorseHex = "C40000";

    private static bool _loggedOnce;

    internal sealed class BarView
    {
        public CharacteristicPanel View;
        public HiddenStatRow Row;
        public RangedItemAttribute Attribute;
    }

    /// <summary>
    /// One of our grids. It follows its vanilla <see cref="Anchor"/> block: other mods (e.g.
    /// RaidIntel's scroll merge) may re-parent the vanilla attribute blocks, before or after our
    /// postfix runs, so the grid re-checks its parent / position every frame (cheap: two compares)
    /// and moves itself right behind the anchor.
    /// </summary>
    private sealed class GridRefs : MonoBehaviour
    {
        public bool IsBars;
        public Transform Anchor;
        public readonly List<BarView> Bars = new List<BarView>();

        private void LateUpdate()
        {
            Follow();
        }

        internal void Follow()
        {
            if (Anchor == null || Anchor.parent == null)
            {
                return;
            }

            Transform parent = Anchor.parent;
            bool moved = false;
            if (transform.parent != parent)
            {
                transform.SetParent(parent, false);
                moved = true;
            }

            int anchorIndex = Anchor.GetSiblingIndex();
            int index = transform.GetSiblingIndex();
            if (index != anchorIndex + 1)
            {
                // SetSiblingIndex removes first: if we sit before the anchor, its index drops by one.
                transform.SetSiblingIndex(index < anchorIndex ? anchorIndex : anchorIndex + 1);
                moved = true;
            }

            if (moved)
            {
                MatchRowGap(this, Anchor);
                LayoutRebuilder.MarkLayoutForRebuild((RectTransform)parent);
            }
        }
    }

    /// <summary>Holds our grids on the panel itself, so they are found wherever they were moved.</summary>
    private sealed class PanelGrids : MonoBehaviour
    {
        public GridRefs Bars;
        public GridRefs Compact;
    }

    internal static void Apply(ItemSpecificationPanel panel)
    {
#pragma warning disable CS0618
        Transform barsAnchor = panel._charPanelContainer;
        Transform compactAnchor = panel._compactPanel;
        CharacteristicPanel barTemplate = panel._charPanelTemplate;
        CompactCharacteristicPanel compactTemplate = panel._compactCharTemplate;
#pragma warning restore CS0618
        if (barsAnchor == null || compactAnchor == null || barsAnchor.parent == null || compactAnchor.parent == null
            || barTemplate == null || compactTemplate == null)
        {
            return;
        }

        Item item = ItemField?.GetValue(panel) as Item;
        List<HiddenStatRow> rows = HiddenStatsPlugin.ShowHiddenStats.Value && item != null && panel.Examined
            ? HiddenStatRows.Build(item)
            : null;

        PanelGrids holder = panel.GetComponent<PanelGrids>();
        GridRefs bars = holder != null ? holder.Bars : null;
        GridRefs compact = holder != null ? holder.Compact : null;
        if (rows == null || rows.Count == 0)
        {
            bool changed = SetActive(bars, false) | SetActive(compact, false);
            if (changed)
            {
                RebuildLayout(panel);
            }
            return;
        }

        var barRows = new List<HiddenStatRow>();
        var textRows = new List<HiddenStatRow>();
        foreach (HiddenStatRow r in rows)
        {
            (r.IsBar ? barRows : textRows).Add(r);
        }

        if (holder == null)
        {
            holder = panel.gameObject.AddComponent<PanelGrids>();
        }
        if (bars == null)
        {
            bars = holder.Bars = CreateGrid(barsAnchor, BarsName, true);
        }
        if (compact == null)
        {
            compact = holder.Compact = CreateGrid(compactAnchor, CompactName, false);
        }
        bars.Follow();
        compact.Follow();
        MatchRowGap(bars, barsAnchor);
        MatchRowGap(compact, compactAnchor);

        SimpleTooltip tooltip = TooltipField?.GetValue(panel) as SimpleTooltip;
        FillBars(bars, barTemplate, barRows, tooltip);
        FillCompact(compact, compactTemplate, textRows, tooltip);
        RebuildLayout(panel);

        if (!_loggedOnce)
        {
            _loggedOnce = true;
            HiddenStatsPlugin.Log?.LogInfo($"[HiddenStats] hidden stats: {barRows.Count} bars + {textRows.Count} cells for {item.GetType().Name} '{item.TemplateId}'");
        }
    }

    /// <summary>Postfix of <c>ItemSpecificationPanel.Compare(Item)</c>.</summary>
    internal static void Compare(ItemSpecificationPanel panel, Item compareItem)
    {
#pragma warning disable CS0618
#pragma warning restore CS0618
        PanelGrids holder = panel.GetComponent<PanelGrids>();
        GridRefs bars = holder != null ? holder.Bars : null;
        if (bars == null || !bars.gameObject.activeSelf || bars.Bars.Count == 0)
        {
            return;
        }

        ApplyCompare(bars.Bars, compareItem);
    }

    /// <summary>
    /// Shows the compare preview on our bars: rebuilds the rows for <paramref name="compareItem"/>
    /// (a clone with the previewed mod applied), matches by key and calls <c>CompareWith</c> (blinking
    /// segment) plus a coloured delta in the value text. Null / equal values reset the preview.
    /// </summary>
    internal static void ApplyCompare(List<BarView> bars, Item compareItem)
    {
        Dictionary<string, HiddenStatRow> other = null;
        if (compareItem != null)
        {
            List<HiddenStatRow> compareRows = HiddenStatRows.Build(compareItem);
            if (compareRows != null)
            {
                other = new Dictionary<string, HiddenStatRow>(compareRows.Count);
                foreach (HiddenStatRow r in compareRows)
                {
                    if (r.IsBar)
                    {
                        other[r.Key] = r;
                    }
                }
            }
        }

        foreach (BarView bar in bars)
        {
            if (bar.View == null)
            {
                continue;
            }

            HiddenStatRow cmp = null;
            if (other != null && other.TryGetValue(bar.Row.Key, out HiddenStatRow c) && Mathf.Abs(c.Value - bar.Row.Value) > bar.Row.Epsilon)
            {
                cmp = c;
            }

            bar.View.CompareWith(cmp != null ? MakeAttribute(cmp) : null);
            SetBarText(bar, cmp);
        }
    }

    // ---- building -------------------------------------------------------------------------------

    private static void FillBars(GridRefs grid, CharacteristicPanel template, List<HiddenStatRow> rows, SimpleTooltip tooltip)
    {
        Clear(grid);
        grid.gameObject.SetActive(rows.Count > 0);
        var state = grid.GetComponent<AttributeCompactor.CompactState>();

        for (int i = 0; i < rows.Count; i++)
        {
            BarView bar = CreateBar(template, grid.transform, rows[i], tooltip);
            grid.Bars.Add(bar);
            Shrink(bar.View, state);
        }

        state?.Sync();
    }

    private static void FillCompact(GridRefs grid, CompactCharacteristicPanel template, List<HiddenStatRow> rows, SimpleTooltip tooltip)
    {
        Clear(grid);
        grid.gameObject.SetActive(rows.Count > 0);
        var state = grid.GetComponent<AttributeCompactor.CompactState>();

        for (int i = 0; i < rows.Count; i++)
        {
            Shrink(CreateCell(template, grid.transform, rows[i], tooltip), state);
        }

        state?.Sync();
    }

    /// <summary>A vanilla full bar (<see cref="CharacteristicPanel"/>) showing a dynamic row.</summary>
    internal static BarView CreateBar(CharacteristicPanel template, Transform parent, HiddenStatRow row, SimpleTooltip tooltip)
    {
        CharacteristicPanel view = Object.Instantiate(template, parent, false);
        view.name = "HiddenStat_" + row.Key;

        RangedItemAttribute attribute = MakeAttribute(row);
        view.Show(attribute, tooltip, true);
        view.CompareWith(null); // vanilla does this via Compare(null); sets bar + value text

        var bar = new BarView { View = view, Row = row, Attribute = attribute };
        SetBarText(bar, null);
        return bar;
    }

    /// <summary>A vanilla compact cell (<see cref="CompactCharacteristicPanel"/>) showing a static row.</summary>
    internal static CompactCharacteristicPanel CreateCell(CompactCharacteristicPanel template, Transform parent, HiddenStatRow row, SimpleTooltip tooltip)
    {
        CompactCharacteristicPanel view = Object.Instantiate(template, parent, false);
        view.name = "HiddenStat_" + row.Key;

        string name = row.Name;
        string text = row.Text;
        string tip = row.Tooltip;
        var attribute = new ItemAttribute(row.IconId)
        {
            Name = name,
            DisplayNameFunc = () => name,
            Base = () => 0f,
            StringValue = () => text,
            FullStringValue = () => tip,
            DisplayType = () => EItemAttributeDisplayType.Compact,
        };

        view.Show(attribute, tooltip, true);
        view.CompareWith(null);
        return view;
    }

    private static RangedItemAttribute MakeAttribute(HiddenStatRow row)
    {
        string name = row.Name;
        string text = row.Text;
        string tip = row.Tooltip;
        float bar = row.Bar;
        EItemAttributeId id = row.IconId is EItemAttributeId e ? e : EItemAttributeId.Undefined;
        return new RangedItemAttribute(id)
        {
            Name = name,
            DisplayNameFunc = () => name,
            Range = new Vector2(0f, 1f),
            Base = () => bar,
            Delta = () => 0f,
            LessIsGood = row.LessIsGood,
            StringValue = () => text,
            FullStringValue = () => tip,
            DisplayType = () => EItemAttributeDisplayType.FullBar,
        };
    }

    /// <summary>Value text is ours (vanilla would append a raw bar-fill difference).</summary>
    private static void SetBarText(BarView bar, HiddenStatRow cmp)
    {
        if (bar.View.ValueText == null)
        {
            return;
        }

        string text = bar.Row.Text;
        if (cmp != null && bar.Row.FormatDelta != null)
        {
            float d = cmp.Value - bar.Row.Value;
            bool better = bar.Row.LessIsGood ? d < 0f : d > 0f;
            text += $"<color=#{(better ? BetterHex : WorseHex)}>({bar.Row.FormatDelta(d)})</color>";
        }
        bar.View.ValueText.richText = true;
        bar.View.ValueText.text = text;
    }

    private static void Shrink(CompactCharacteristicPanel view, AttributeCompactor.CompactState state)
    {
        if (HiddenStatsPlugin.CompactAttributes.Value && state != null)
        {
            RowShrinker.Shrink(view, state.OriginalRowHeight, state.TargetRowHeight, HiddenStatsPlugin.FontScale.Value, drivenByGrid: true);
        }
    }

    private static GridRefs CreateGrid(Transform anchor, string name, bool isBars)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(anchor.parent, false);
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        if (anchor is RectTransform anchorRt && anchorRt.rect.width > 1f)
        {
            rt.sizeDelta = new Vector2(anchorRt.rect.width, 0f);
        }

        // Mirror the vanilla neighbour: the (compacted) bar grid / the compact grid.
        GridLayoutGroup vanillaGrid = anchor.GetComponent<GridLayoutGroup>();
        AttributeCompactor.CompactState vanillaState = anchor.GetComponent<AttributeCompactor.CompactState>();

        GridLayoutGroup grid = go.AddComponent<FlexibleGridLayoutGroup>();
        grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
        grid.startAxis = GridLayoutGroup.Axis.Horizontal;
        grid.childAlignment = TextAnchor.UpperLeft;
        grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
        if (vanillaGrid != null)
        {
            grid.spacing = vanillaGrid.spacing;
        }
        else
        {
            grid.spacing = isBars ? new Vector2(6f, VanillaBarGap) : new Vector2(VanillaColumnGap, VanillaBarGap);
        }

        int columns = isBars
            ? (HiddenStatsPlugin.CompactAttributes.Value ? HiddenStatsPlugin.FullBarColumns.Value : 1)
            : 2;
        grid.constraintCount = columns;

        var le = go.AddComponent<LayoutElement>();
        le.minWidth = 0f;
        le.flexibleWidth = 1f;

        var state = go.AddComponent<AttributeCompactor.CompactState>();
        state.Grid = grid;
        state.Columns = columns;
        state.Mode = AttributeCompactor.CompactState.GridMode.FitColumns;
        state.OriginalRowHeight = VanillaRowHeight;
        float fallbackHeight = HiddenStatsPlugin.CompactAttributes.Value
            ? Mathf.Round(VanillaRowHeight * HiddenStatsPlugin.RowHeightScale.Value)
            : VanillaRowHeight;
        state.TargetRowHeight = vanillaState != null && vanillaState.TargetRowHeight > 1f ? vanillaState.TargetRowHeight
            : vanillaGrid != null && vanillaGrid.cellSize.y > 1f ? vanillaGrid.cellSize.y : fallbackHeight;
        grid.cellSize = new Vector2(100f, state.TargetRowHeight);

        var refs = go.AddComponent<GridRefs>();
        refs.IsBars = isBars;
        refs.Anchor = anchor;
        return refs;
    }

    /// <summary>
    /// Our grid is a sibling block of the vanilla one, so the gap between them is the parent
    /// column's spacing (vanilla 6; 0 inside RaidIntel's scroll content) rather than the vanilla row
    /// gap (4). The top padding (negative or positive) makes up the difference so the two blocks
    /// read as one continuous list.
    /// </summary>
    private static void MatchRowGap(GridRefs grid, Transform anchor)
    {
        var layout = grid.GetComponent<GridLayoutGroup>();
        var parentGroup = anchor.parent != null ? anchor.parent.GetComponent<HorizontalOrVerticalLayoutGroup>() : null;
        if (layout == null || parentGroup == null)
        {
            return;
        }

        float rowGap = layout.spacing.y;
        int top = Mathf.RoundToInt(rowGap - parentGroup.spacing);
        if (layout.padding.top != top)
        {
            layout.padding = new RectOffset(layout.padding.left, layout.padding.right, top, layout.padding.bottom);
        }
    }

    private static void Clear(GridRefs grid)
    {
        grid.Bars.Clear();
        Transform t = grid.transform;
        for (int i = t.childCount - 1; i >= 0; i--)
        {
            GameObject child = t.GetChild(i).gameObject;
            child.transform.SetParent(null, false);
            Object.Destroy(child);
        }
    }

    private static bool SetActive(GridRefs grid, bool active)
    {
        if (grid != null && grid.gameObject.activeSelf != active)
        {
            grid.gameObject.SetActive(active);
            return true;
        }
        return false;
    }

    private static void RebuildLayout(ItemSpecificationPanel panel)
    {
        InfoWindow window = panel.GetComponentInParent<InfoWindow>(true);
        RectTransform root = window != null ? (RectTransform)window.transform : (RectTransform)panel.transform;
        LayoutRebuilder.ForceRebuildLayoutImmediate(root);
    }
}
