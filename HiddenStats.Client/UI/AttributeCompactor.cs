using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HiddenStats.Client.UI;

/// <summary>
/// Shrinks the vanilla attribute area of the inspect window.
///
/// Vanilla (decompiled <see cref="ItemSpecificationPanel.RecreateAttributeBars"/>):
/// <list type="bullet">
/// <item><c>_charPanelContainer</c> receives one <see cref="CharacteristicPanel"/> per
/// <c>FullBar</c> attribute (ergonomics, accuracy, recoil, ...) via a ViewList; durability and
/// mastering are prefab-placed <see cref="CharacteristicPanel"/>s.</item>
/// <item><c>_compactPanel</c> is a <see cref="GridLayoutGroup"/> of
/// <see cref="CompactCharacteristicPanel"/> cells; vanilla only switches its column count
/// (1 / 2) from the measured text width, which is measured one frame after Show — i.e. after our
/// font change, so the narrower text is taken into account automatically.</item>
/// <item><see cref="CharacteristicPanel"/> only moves the bar fill anchors along X; bar height
/// is the images' own fixed height, so row internals are scaled together with the row.</item>
/// </list>
///
/// Full bars: the container's VerticalLayoutGroup is replaced by the game's
/// <see cref="FlexibleGridLayoutGroup"/> (LayoutGroup is DisallowMultipleComponent) — the same
/// component vanilla uses for <c>_compactPanel</c>. Unlike a plain GridLayoutGroup it reports
/// min/preferred width 0 and derives the cell width from its own rect, so a narrower window
/// shrinks the cells instead of the grid's min width pinning the container (which clipped the
/// right column). Both grids get a reduced row height; <see cref="CompactState"/> keeps the cell
/// height and drops to one column when cells would get too narrow.
/// </summary>
internal static class AttributeCompactor
{
    private const float ColumnGap = 6f;
    private const float MinCellWidth = 200f;
    private const float FallbackRowHeight = 28f;

    private static readonly FieldInfo ItemField = AccessTools.Field(typeof(ItemSpecificationPanel), "_item");

    private static bool _loggedOnce;

    internal static void Apply(ItemSpecificationPanel panel)
    {
        if (!HiddenStatsPlugin.CompactAttributes.Value)
        {
            return;
        }

#pragma warning disable CS0618
        RectTransform full = panel._charPanelContainer as RectTransform;
        RectTransform compact = panel._compactPanel as RectTransform;
        CharacteristicPanel durability = panel._charDurability;
        CharacteristicPanel mastering = panel._charMastering;
#pragma warning restore CS0618

        if (HiddenStatsPlugin.LayoutDump.Value && ItemField?.GetValue(panel) is Weapon)
        {
            LayoutDump.DumpOnce(panel);
        }

        float heightScale = HiddenStatsPlugin.RowHeightScale.Value;
        float fontScale = HiddenStatsPlugin.FontScale.Value;

        string fullInfo = full != null ? CompactFullBars(full, HiddenStatsPlugin.FullBarColumns.Value, heightScale, fontScale) : "missing";
        string compactInfo = compact != null ? CompactGrid(compact, heightScale, fontScale) : "missing";

        // Prefab-placed rows that may live outside _charPanelContainer.
        ShrinkLoose(durability, heightScale, fontScale);
        ShrinkLoose(mastering, heightScale, fontScale);

        InfoWindow window = panel.GetComponentInParent<InfoWindow>(true);
        RectTransform root = window != null ? (RectTransform)window.transform : (RectTransform)panel.transform;
        LayoutRebuilder.ForceRebuildLayoutImmediate(root);

        if (!_loggedOnce && fullInfo != null)
        {
            _loggedOnce = true;
            HiddenStatsPlugin.Log?.LogInfo($"[HiddenStats] compact attributes: full bars {fullInfo}; compact grid {compactInfo}; font x{fontScale:0.##}");
        }
    }

    private static string CompactFullBars(RectTransform container, int columns, float heightScale, float fontScale)
    {
        CompactState state = container.GetComponent<CompactState>();
        if (state == null)
        {
            float origRow = MeasureRowHeight(container);
            if (origRow <= 0f)
            {
                return null; // no rows yet; initialize on a later refresh
            }

            state = container.gameObject.AddComponent<CompactState>();
            state.OriginalRowHeight = origRow;
            state.TargetRowHeight = Mathf.Round(origRow * heightScale);

            LayoutGroup group = container.GetComponent<LayoutGroup>();
            if (columns >= 2 && group is VerticalLayoutGroup vlg)
            {
                var padding = new RectOffset(vlg.padding.left, vlg.padding.right, vlg.padding.top, vlg.padding.bottom);
                float spacing = vlg.spacing;
                Object.DestroyImmediate(vlg);

                GridLayoutGroup grid = container.gameObject.AddComponent<FlexibleGridLayoutGroup>();
                grid.padding = padding;
                grid.spacing = new Vector2(ColumnGap, spacing);
                grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
                grid.startAxis = GridLayoutGroup.Axis.Horizontal;
                grid.childAlignment = TextAnchor.UpperLeft;
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = columns;
                state.Grid = grid;
                state.Columns = columns;
                state.Mode = CompactState.GridMode.FitColumns;
            }
            else if (group is GridLayoutGroup existing)
            {
                state.Grid = existing;
                state.Mode = CompactState.GridMode.HeightOnly;
            }
            else
            {
                state.Mode = CompactState.GridMode.None; // single column: rows keep their LayoutElement
            }
        }

        ShrinkChildren(container, state, fontScale);
        state.Sync();

        string layout = state.Mode == CompactState.GridMode.FitColumns ? $"VLG->FlexibleGrid {state.Columns} cols (min cell {MinCellWidth:0})" : state.Mode.ToString();
        return $"{layout}, row {state.OriginalRowHeight:0}->{state.TargetRowHeight:0}, cell {(state.Grid != null ? state.Grid.cellSize.ToString("0") : "-")}, width {container.rect.width:0}";
    }

    private static string CompactGrid(RectTransform container, float heightScale, float fontScale)
    {
        GridLayoutGroup grid = container.GetComponent<GridLayoutGroup>();
        if (grid == null)
        {
            return "no GridLayoutGroup";
        }

        CompactState state = container.GetComponent<CompactState>();
        if (state == null)
        {
            state = container.gameObject.AddComponent<CompactState>();
            state.Grid = grid;
            state.Mode = CompactState.GridMode.HeightOnly;
            state.OriginalRowHeight = grid.cellSize.y > 1f ? grid.cellSize.y : FallbackRowHeight;
            state.TargetRowHeight = Mathf.Round(state.OriginalRowHeight * heightScale);
        }

        ShrinkChildren(container, state, fontScale);
        state.Sync();
        return $"row {state.OriginalRowHeight:0}->{state.TargetRowHeight:0}, cell {grid.cellSize:0}, cols {grid.constraintCount}";
    }

    private static float MeasureRowHeight(RectTransform container)
    {
        for (int i = 0; i < container.childCount; i++)
        {
            var row = container.GetChild(i) as RectTransform;
            if (row == null || !row.gameObject.activeSelf || row.GetComponent<CompactCharacteristicPanel>() == null)
            {
                continue;
            }

            float h = row.rect.height;
            LayoutElement le = row.GetComponent<LayoutElement>();
            if (le != null && le.preferredHeight > h)
            {
                h = le.preferredHeight;
            }
            return h > 1f ? h : FallbackRowHeight;
        }
        return 0f;
    }

    private static void ShrinkChildren(RectTransform container, CompactState state, float fontScale)
    {
        for (int i = 0; i < container.childCount; i++)
        {
            CompactCharacteristicPanel row = container.GetChild(i).GetComponent<CompactCharacteristicPanel>();
            if (row != null)
            {
                RowShrinker.Shrink(row, state.OriginalRowHeight, state.TargetRowHeight, fontScale, drivenByGrid: state.Grid != null);
            }
        }
    }

    private static void ShrinkLoose(CharacteristicPanel row, float heightScale, float fontScale)
    {
        if (row == null || row.transform.parent == null || row.transform.parent.GetComponent<CompactState>() != null)
        {
            return; // handled with its container
        }

        float orig = ((RectTransform)row.transform).rect.height;
        if (orig <= 1f)
        {
            return;
        }
        RowShrinker.Shrink(row, orig, Mathf.Round(orig * heightScale), fontScale, drivenByGrid: false);
    }

    /// <summary>Keeps grid cells sized to the reduced row height and the container width.</summary>
    internal sealed class CompactState : MonoBehaviour
    {
        internal enum GridMode
        {
            None,
            FitColumns,
            HeightOnly,
        }

        public GridMode Mode;
        public GridLayoutGroup Grid;
        public int Columns = 1;
        public float OriginalRowHeight;
        public float TargetRowHeight;

        private void LateUpdate()
        {
            Sync();
        }

        internal void Sync()
        {
            if (Grid == null)
            {
                return;
            }

            if (Mode == GridMode.FitColumns)
            {
                // FlexibleGridLayoutGroup computes the cell width itself; we only pick the count.
                float width = ((RectTransform)transform).rect.width - Grid.padding.horizontal;
                int count = Columns;
                while (count > 1 && (width - Grid.spacing.x * (count - 1)) / count < MinCellWidth)
                {
                    count--;
                }
                if (width > 1f && Grid.constraintCount != count)
                {
                    Grid.constraintCount = count;
                }
            }

            if (!Mathf.Approximately(Grid.cellSize.y, TargetRowHeight))
            {
                Grid.cellSize = new Vector2(Grid.cellSize.x, TargetRowHeight);
            }
        }
    }
}

/// <summary>Scales one attribute row's internals (bar images, icon, text) to a new row height.</summary>
internal static class RowShrinker
{
    private sealed class ShrunkMarker : MonoBehaviour
    {
    }

    internal static void Shrink(CompactCharacteristicPanel row, float originalHeight, float targetHeight, float fontScale, bool drivenByGrid)
    {
        if (row == null || row.GetComponent<ShrunkMarker>() != null)
        {
            return;
        }
        row.gameObject.AddComponent<ShrunkMarker>();

        var root = (RectTransform)row.transform;
        float k = originalHeight > 1f ? Mathf.Clamp(targetHeight / originalHeight, 0.3f, 1f) : 1f;

        if (k < 0.999f)
        {
            RectTransform[] all = row.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                RectTransform rt = all[i];
                if (rt == root || rt.GetComponent<TMP_SubMeshUI>() != null)
                {
                    continue;
                }

                LayoutElement childLe = rt.GetComponent<LayoutElement>();
                if (childLe != null && !childLe.ignoreLayout)
                {
                    bool squareLe = childLe.preferredHeight > 0f && Mathf.Approximately(childLe.preferredWidth, childLe.preferredHeight);
                    if (childLe.preferredHeight > 0f)
                    {
                        childLe.preferredHeight *= k;
                    }
                    if (squareLe)
                    {
                        childLe.preferredWidth *= k;
                    }
                    if (childLe.minHeight > 0f)
                    {
                        childLe.minHeight *= k;
                    }
                }

                if (Mathf.Approximately(rt.anchorMin.y, rt.anchorMax.y))
                {
                    Vector2 size = rt.sizeDelta;
                    bool fixedWidth = Mathf.Approximately(rt.anchorMin.x, rt.anchorMax.x);
                    bool squareIcon = fixedWidth && size.y > 0f && Mathf.Abs(size.x - size.y) < 1f && rt.GetComponent<Graphic>() is Image;
                    size.y *= k;
                    if (squareIcon)
                    {
                        size.x *= k;
                    }
                    rt.sizeDelta = size;
                    rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, rt.anchoredPosition.y * k);
                }
                else
                {
                    rt.offsetMin = new Vector2(rt.offsetMin.x, rt.offsetMin.y * k);
                    rt.offsetMax = new Vector2(rt.offsetMax.x, rt.offsetMax.y * k);
                }
            }

            LayoutElement le = row.GetComponent<LayoutElement>();
            if (le != null)
            {
                if (le.preferredHeight > 0f)
                {
                    le.preferredHeight = targetHeight;
                }
                if (le.minHeight > targetHeight)
                {
                    le.minHeight = targetHeight;
                }
            }
            if (!drivenByGrid && Mathf.Approximately(root.anchorMin.y, root.anchorMax.y))
            {
                root.sizeDelta = new Vector2(root.sizeDelta.x, targetHeight);
            }
        }

        if (fontScale < 0.999f)
        {
            TMP_Text[] texts = row.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text t = texts[i];
                if (t.enableAutoSizing)
                {
                    t.fontSizeMax *= fontScale;
                    t.fontSizeMin *= fontScale;
                }
                t.fontSize *= fontScale;
            }
        }
    }
}
