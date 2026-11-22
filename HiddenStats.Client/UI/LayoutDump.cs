using System.Globalization;
using System.Text;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace HiddenStats.Client.UI;

/// <summary>
/// One-shot runtime dump of the vanilla attribute area (the game ships without MonoBehaviour
/// typetrees, so the prefab layout can only be inspected at runtime). Taken before any change.
/// </summary>
internal static class LayoutDump
{
    private const int MaxDepth = 7;
    private const int ExpandedRowsPerContainer = 2;

    private static bool _done;

    internal static void DumpOnce(ItemSpecificationPanel panel)
    {
        if (_done)
        {
            return;
        }
        _done = true;

#pragma warning disable CS0618
        Transform full = panel._charPanelContainer;
        Transform compact = panel._compactPanel;
        Transform durability = panel._charDurability != null ? panel._charDurability.transform : null;
        Transform mastering = panel._charMastering != null ? panel._charMastering.transform : null;
#pragma warning restore CS0618

        Transform root = CommonAncestor(full, compact);
        if (root == null)
        {
            return;
        }

        var sb = new StringBuilder(8192);
        sb.AppendLine("[HiddenStats LayoutDump] vanilla attribute area");
        sb.Append("  _charPanelContainer = ").AppendLine(PathOf(full, panel.transform));
        sb.Append("  _compactPanel       = ").AppendLine(PathOf(compact, panel.transform));
        sb.Append("  _charDurability     = ").AppendLine(PathOf(durability, panel.transform));
        sb.Append("  _charMastering      = ").AppendLine(PathOf(mastering, panel.transform));
        Dump(sb, root, 0);
        HiddenStatsPlugin.Log?.LogInfo(sb.ToString());
    }

    private static void Dump(StringBuilder sb, Transform t, int depth)
    {
        sb.Append(' ', 2 + depth * 2).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name);
        Describe(sb, t);
        sb.AppendLine();

        if (depth >= MaxDepth)
        {
            return;
        }

        int expanded = 0;
        for (int i = 0; i < t.childCount; i++)
        {
            Transform c = t.GetChild(i);
            bool isRow = c.GetComponent<CompactCharacteristicPanel>() != null;
            if (isRow && expanded >= ExpandedRowsPerContainer)
            {
                sb.Append(' ', 4 + depth * 2).Append("… ").Append(c.gameObject.activeSelf ? "" : "(off) ").Append(c.name);
                AppendRect(sb, c as RectTransform);
                sb.AppendLine();
                continue;
            }
            if (isRow)
            {
                expanded++;
            }
            Dump(sb, c, depth + 1);
        }
    }

    private static void Describe(StringBuilder sb, Transform t)
    {
        var rt = t as RectTransform;
        AppendRect(sb, rt);
        if (rt != null)
        {
            sb.Append(" a=").Append(V(rt.anchorMin)).Append('-').Append(V(rt.anchorMax))
              .Append(" sd=").Append(V(rt.sizeDelta)).Append(" pos=").Append(V(rt.anchoredPosition));
        }

        Component[] comps = t.GetComponents<Component>();
        sb.Append(" [");
        for (int i = 0; i < comps.Length; i++)
        {
            Component c = comps[i];
            if (c == null || c is Transform || c is CanvasRenderer)
            {
                continue;
            }
            sb.Append(c.GetType().Name);
            switch (c)
            {
                case LayoutElement le:
                    sb.Append('(').Append("min=").Append(F(le.minWidth)).Append('x').Append(F(le.minHeight))
                      .Append(" pref=").Append(F(le.preferredWidth)).Append('x').Append(F(le.preferredHeight))
                      .Append(" flex=").Append(F(le.flexibleWidth)).Append('x').Append(F(le.flexibleHeight))
                      .Append(le.ignoreLayout ? " ignore" : "").Append(')');
                    break;
                case GridLayoutGroup g:
                    sb.Append("(cell=").Append(V(g.cellSize)).Append(" sp=").Append(V(g.spacing))
                      .Append(" pad=").Append(Pad(g.padding)).Append(' ').Append(g.constraint).Append('=').Append(g.constraintCount).Append(')');
                    break;
                case HorizontalOrVerticalLayoutGroup h:
                    sb.Append("(sp=").Append(F(h.spacing)).Append(" pad=").Append(Pad(h.padding))
                      .Append(" ctrl=").Append(h.childControlWidth ? 'W' : '-').Append(h.childControlHeight ? 'H' : '-')
                      .Append(" expand=").Append(h.childForceExpandWidth ? 'W' : '-').Append(h.childForceExpandHeight ? 'H' : '-')
                      .Append(" align=").Append(h.childAlignment).Append(')');
                    break;
                case ContentSizeFitter f:
                    sb.Append('(').Append(f.horizontalFit).Append(',').Append(f.verticalFit).Append(')');
                    break;
                case TMP_Text tmp:
                    string text = tmp.text ?? "";
                    if (text.Length > 24)
                    {
                        text = text.Substring(0, 24) + "…";
                    }
                    sb.Append("(fs=").Append(F(tmp.fontSize)).Append(tmp.enableAutoSizing ? $" auto {F(tmp.fontSizeMin)}-{F(tmp.fontSizeMax)}" : "")
                      .Append(" wrap=").Append(tmp.enableWordWrapping ? 1 : 0).Append(' ').Append(tmp.overflowMode)
                      .Append(" align=").Append(tmp.alignment).Append(" \"").Append(text.Replace("\n", "\\n")).Append("\")");
                    break;
                case Image img:
                    sb.Append("(").Append(img.type).Append(img.preserveAspect ? " aspect" : "")
                      .Append(img.sprite != null ? " " + img.sprite.name : "").Append(')');
                    break;
            }
            sb.Append(' ');
        }
        sb.Append(']');
    }

    private static void AppendRect(StringBuilder sb, RectTransform rt)
    {
        if (rt != null)
        {
            sb.Append(" {").Append(F(rt.rect.width)).Append('x').Append(F(rt.rect.height)).Append('}');
        }
    }

    private static Transform CommonAncestor(Transform a, Transform b)
    {
        if (a == null)
        {
            return b;
        }
        if (b == null)
        {
            return a;
        }
        for (Transform t = a.parent; t != null; t = t.parent)
        {
            if (b.IsChildOf(t))
            {
                return t;
            }
        }
        return null;
    }

    private static string PathOf(Transform t, Transform stop)
    {
        if (t == null)
        {
            return "<null>";
        }
        var sb = new StringBuilder(t.name);
        for (Transform p = t.parent; p != null && p != stop; p = p.parent)
        {
            sb.Insert(0, p.name + "/");
        }
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    private static string V(Vector2 v) => F(v.x) + "," + F(v.y);

    private static string Pad(RectOffset p) => $"{p.left},{p.right},{p.top},{p.bottom}";
}
