using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace HiddenStats.Client.Core;

/// <summary>
/// One hidden-stat row. Bar rows (<see cref="IsBar"/>) are values that change with mods, ammo,
/// magazine or durability; they render as vanilla full bars and take part in the mod compare
/// preview (matched by <see cref="Key"/>). Text rows are template-only values (compact cells).
/// </summary>
internal sealed class HiddenStatRow
{
    public string Key;
    public string Name;
    public string Text;
    public string Tooltip;
    public Enum IconId = EItemAttributeId.Undefined;

    public bool IsBar;
    public float Value;
    /// <summary>Bar fill, 0..1.</summary>
    public float Bar;
    public bool LessIsGood;
    /// <summary>Formats a value difference (compare preview), e.g. "+0.012%".</summary>
    public Func<float, string> FormatDelta;
    /// <summary>Differences below this are treated as "no change".</summary>
    public float Epsilon = 1e-6f;

    internal static HiddenStatRow MakeText(string key, string name, string text, string tooltip, Enum iconId)
    {
        return new HiddenStatRow { Key = key, Name = name, Text = text, Tooltip = tooltip, IconId = iconId ?? EItemAttributeId.Undefined };
    }

    internal static HiddenStatRow MakeBar(string key, string name, float value, float bar, string text, string tooltip, Enum iconId,
        bool lessIsGood, Func<float, string> formatDelta, float epsilon)
    {
        return new HiddenStatRow
        {
            Key = key,
            Name = name,
            Text = text,
            Tooltip = tooltip,
            IconId = iconId ?? EItemAttributeId.Undefined,
            IsBar = true,
            Value = value,
            Bar = Mathf.Clamp01(bar),
            LessIsGood = lessIsGood,
            FormatDelta = formatDelta,
            Epsilon = epsilon,
        };
    }
}

/// <summary>
/// Builds the hidden-stat rows for an item. Every formula mirrors the game code (decompiled
/// Assembly-CSharp, SPT 4.1.6) so assembled weapons show the values the game actually uses:
/// <list type="bullet">
/// <item>malfunction: <c>Player.FirearmController.GetTotalMalfunctionChance</c> (overheat part
/// excluded — it depends on the current barrel temperature);</item>
/// <item>durability loss: <c>Weapon.GetDurabilityLossOnShot</c> at normal temperature
/// (<c>Overheat.DurReduceMinMult</c>), no skill bonus;</item>
/// <item>heat: <c>Weapon.GetShotOverheat</c> / cooling factors of <c>Weapon.GetCurrentOverheat</c>;</item>
/// <item>barrel wear: <c>Weapon.GetBarrelDeviation</c>;</item>
/// <item>recoil feel: template fields consumed by <c>NewRecoilShotEffect</c> /
/// <c>ProceduralWeaponAnimation</c> (not changed by mods).</item>
/// </list>
/// The current ammo is <c>Weapon.CurrentAmmoTemplate</c> (chamber → magazine → default ammo).
/// Also used on the cloned item the vanilla mod compare preview builds.
/// </summary>
internal static class HiddenStatRows
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Bar scales (from a survey of all SPT 4.1.6 weapons / mods / ammo).
    private const float MalfBarMinLog = -5f;           // 0.001 %/shot
    private static readonly float MalfBarMaxLog = Mathf.Log10(0.05f); // 5 %/shot
    private const float DurLossBarMax = 0.15f;
    private const float HeatBarMax = 10f;
    private const float CoolBarMax = 6f;
    private const float DeviationBarMaxLog = 2f;       // ×100
    // Bot HearingSense (SPT 4.1.6 database/bots/types, normal difficulty): usec/bear 2.9, assault 1.85.
    private const float PmcHearingSense = 2.9f;
    private const float ScavHearingSense = 1.85f;

    internal static List<HiddenStatRow> Build(Item item)
    {
        switch (item)
        {
            case Weapon weapon:
                return BuildWeapon(weapon);
            case Magazine magazine:
                return BuildMagazine(magazine);
            default:
                return null;
        }
    }

    private static List<HiddenStatRow> BuildWeapon(Weapon weapon)
    {
        WeaponTemplate t = weapon.Template;
        GlobalConfiguration globals = Globals();
        AmmoTemplate ammo = SafeAmmo(weapon);
        var rows = new List<HiddenStatRow>(18);

        // ---- dynamic (bars) --------------------------------------------------------------------
        rows.Add(MalfunctionRow(weapon, ammo, globals));

        float durLoss = DurabilityLossPerShot(weapon, ammo, globals);
        rows.Add(HiddenStatRow.MakeBar("durloss", "每发耐久损耗", durLoss, durLoss / DurLossBarMax,
            durLoss > 0f ? $"{durLoss.ToString("0.000", Inv)} (≈{Mathf.CeilToInt(1f / durLoss)}发/点)" : "0",
            $"每开一枪损失的耐久（常温、未计技能减免）。\n= 模板耐久 {weapon.Repairable.TemplateDurability} / 使用寿命 {t.OperatingResource:0} × 枪械倍率 {t.DurabilityBurnRatio:0.##} × 配件倍率 × 弹药倍率 {(ammo != null ? ammo.DurabilityBurnModificator : 1f):0.##}\n过热后最高再 ×{(globals != null ? globals.Overheat.DurReduceMaxMult : 3f):0.#}。",
            EItemAttributeId.DurabilityBurn, lessIsGood: true, d => Signed(d, "0.000"), 0.0005f));

        if (weapon.AllowOverheat)
        {
            float heat = ShotHeat(weapon, ammo, globals);
            float problems = globals != null ? globals.Overheat.OverheatProblemsStart : 100f;
            rows.Add(HiddenStatRow.MakeBar("heat", "每发升温", heat, heat / HeatBarMax,
                heat.ToString("0.00", Inv),
                $"每开一枪增加的枪管温度（含配件与当前弹药{(ammo != null ? $" ×{ammo.HeatFactor:0.##}" : "")}）。\n温度达到 {problems:0} 后开始出现过热故障、加速耐久与上限磨损；不计冷却约 {Mathf.CeilToInt(problems / Mathf.Max(0.0001f, heat))} 发达到。",
                EItemAttributeId.HeatFactor, lessIsGood: true, d => Signed(d, "0.00"), 0.005f));

            float cool = CoolFactor(weapon, globals);
            rows.Add(HiddenStatRow.MakeBar("cool", "散热系数", cool, cool / CoolBarMax,
                cool.ToString("0.00", Inv),
                $"枪管冷却速度，越大降温越快。\n= 枪械散热 {t.CoolFactorGun:0.##} × 配件散热合计（含模板 {t.CoolFactorGunMods:0.###}）",
                EItemAttributeId.CoolFactor, lessIsGood: false, d => Signed(d, "0.00"), 0.005f));
        }

        float dev = SafeDeviation(weapon);
        rows.Add(HiddenStatRow.MakeBar("deviation", "耐久精度惩罚", dev, Mathf.Log10(Mathf.Max(1f, dev)) / DeviationBarMaxLog,
            $"×{dev.ToString("0.00", Inv)} (最差×{weapon.DeviationMax.ToString("0.#", Inv)})",
            $"枪管磨损对散布的放大倍率（当前耐久下）。耐久越低越大，耐久 0 时达到最差值。\n由枪管决定：上限 {weapon.DeviationMax:0.#}，曲线 {weapon.DeviationCurve:0.##}。",
            EItemAttributeId.CenterOfImpact, lessIsGood: true, d => Signed(d, "0.00"), 0.005f));

        rows.Add(GunshotRow(weapon));

        // ---- static (compact cells) ------------------------------------------------------------
        rows.Add(HiddenStatRow.MakeText("malftypes", "故障类型", MalfTypes(weapon),
            "这把枪可能出现的故障种类（耐久、弹药、弹匣或过热触发故障时，从中抽取具体类型）。\n卡壳 = 弹壳卡住，供弹 = 供弹不良，哑火 = 子弹未击发，套筒 = 套筒/枪机未复位。",
            EItemAttributeId.MalfFeedChance));
        if (!weapon.AllowOverheat)
        {
            rows.Add(HiddenStatRow.MakeText("overheat", "过热", "不会过热", "这把枪不会因连续射击而过热。", EItemAttributeId.HeatFactor));
        }
        rows.Add(HiddenStatRow.MakeText("recoilangle", "后坐方向",
            $"{t.RecoilAngle}° ±{t.RecolDispersion}°",
            "每发后坐的枪口上跳方向：90° 为正上方，± 为左右随机偏移范围（越小越稳定）。",
            EItemAttributeId.RecoilUp));
        rows.Add(HiddenStatRow.MakeText("recoilreturn", "后坐回正",
            t.RecoilReturnSpeedHandRotation.ToString("0.##", Inv),
            "后坐后枪口回到原位的速度（俗称“收敛”），越大回正越快。由枪型决定，配件不影响。",
            EItemAttributeId.RecoilUp));
        rows.Add(HiddenStatRow.MakeText("recoildamping", "后坐阻尼",
            t.RecoilDampingHandRotation.ToString("0.##", Inv),
            "后坐摆动的阻尼，越大枪口晃动越快平息。",
            EItemAttributeId.RecoilBack));
        rows.Add(HiddenStatRow.MakeText("recoilcamera", "镜头后坐",
            t.RecoilCamera.ToString("0.###", Inv),
            "开枪时镜头本身的抖动强度，绝对值越小画面越稳。",
            EItemAttributeId.RecoilBack));
        rows.Add(HiddenStatRow.MakeText("stableshot", "稳定发数",
            $"第 {t.RecoilStableIndexShot} 发",
            "连射到第几发后，后坐进入稳定阶段（上跳不再继续累积）。",
            EItemAttributeId.RecoilUp));
        if (weapon.WeapFireType != null && weapon.WeapFireType.Contains(Weapon.EFireMode.single))
        {
            rows.Add(HiddenStatRow.MakeText("singlerate", "半自动射速",
                $"{(t.SingleFireRate > 0 ? t.SingleFireRate : 240)} 发/分",
                "单发模式下最快的连续点射速度。",
                EItemAttributeId.FireRate));
        }
        rows.Add(HiddenStatRow.MakeText("boltcatch", "空仓挂机",
            weapon.IsBoltCatch ? (t.ManualBoltCatch ? "有（手动）" : "有") : "无",
            "打空弹匣后枪机是否停在后方。有空仓挂机时换弹无需重新拉栓，换弹更快。",
            EItemAttributeId.Undefined));
        rows.Add(HiddenStatRow.MakeText("chamberload", "可单独压膛",
            t.isChamberLoad ? "是" : "否",
            "能否不经弹匣，直接把一发子弹压入膛室。",
            EItemAttributeId.Undefined));
        rows.Add(HiddenStatRow.MakeText("mountable", "可架枪",
            t.IsMountable ? "是" : "否",
            "能否依托掩体边缘架枪以降低后坐。",
            EItemAttributeId.Undefined));

        return rows;
    }

    /// <summary>
    /// AI hearing of gunshots (decompiled): <c>Player</c> reports each shot as
    /// <c>AISoundType.silencedGun</c> if the weapon has any <see cref="SilencerComponent"/>
    /// (suppressor, or integrally suppressed barrel), else <c>gun</c>;
    /// <c>AIData.TryPlayShootSound</c> maps that to power <c>Core.GUNSHOT_SPREAD</c> (280) /
    /// <c>GUNSHOT_SPREAD_SILENCE</c> (60); <c>BotHearingSensor.IsSoundHeard</c> hears it when
    /// distance &lt; power × the bot's <c>HearingSense</c>. The template's <c>bHearDist</c>, mods'
    /// <c>Loudness</c> and ammo's <c>ammoHear</c> are never read by the client.
    /// </summary>
    private static HiddenStatRow GunshotRow(Weapon weapon)
    {
        bool silenced = weapon.GetItemComponentsInChildren<SilencerComponent>().Any();
        BotGlobalsCoreSettings core = BotInternalSettingsController.Core;
        float loud = core != null && core.GUNSHOT_SPREAD > 0 ? core.GUNSHOT_SPREAD : 280f;
        float quiet = core != null && core.GUNSHOT_SPREAD_SILENCE > 0 ? core.GUNSHOT_SPREAD_SILENCE : 60f;
        float power = silenced ? quiet : loud;

        float pmc = power * PmcHearingSense;
        float scav = power * ScavHearingSense;
        string tip =
            $"AI 能听到你开枪的最远距离（按 PMC 普通难度估算）。{(silenced ? "已装消音器。" : "未装消音器。")}\n" +
            $"= 枪声传播值 × AI 听觉系数：未消音 {loud:0}，消音 {quiet:0}；PMC 普通 ×{PmcHearingSense}，Scav 普通 ×{ScavHearingSense}（难度与 Boss 不同）。\n" +
            $"PMC：未消音 ≈{loud * PmcHearingSense:0} 米，消音 ≈{quiet * PmcHearingSense:0} 米\n" +
            $"Scav：未消音 ≈{loud * ScavHearingSense:0} 米，消音 ≈{quiet * ScavHearingSense:0} 米\n" +
            "只有消音器（或自带消音的枪管）会改变这个距离；枪械与配件面板上的「响度」游戏实际不使用。";
        return HiddenStatRow.MakeBar("gunshot", "枪声距离", pmc, pmc / (loud * PmcHearingSense),
            $"≈{pmc:0} 米{(silenced ? " (消音)" : "")}", tip,
            EItemAttributeId.Loudness, lessIsGood: true, d => Signed(d, "0") + "米", 0.5f);
    }

    private static List<HiddenStatRow> BuildMagazine(Magazine magazine)
    {
        GlobalConfiguration globals = Globals();
        float mult = globals != null ? globals.Malfunction.MagazineMalfChanceMult : 1f;
        float perShot = magazine.MalfunctionChance * mult / 1000f;
        return new List<HiddenStatRow>
        {
            HiddenStatRow.MakeBar("magmalf", "故障率/发", perShot, MalfBar(perShot),
                FormatChance(perShot),
                $"使用此弹匣时每发额外增加的故障几率（原版只显示等级）。\n武器耐久 {IgnoreRange(globals)} 时不会触发弹匣故障。",
                EItemAttributeId.MalfFeedChance, lessIsGood: true, FormatChanceDelta, 1e-7f),
        };
    }

    // ---- formulas -------------------------------------------------------------------------------

    private static HiddenStatRow MalfunctionRow(Weapon weapon, AmmoTemplate ammo, GlobalConfiguration globals)
    {
        if (!weapon.AllowMalfunction)
        {
            return HiddenStatRow.MakeBar("malf", "故障率/发", 0f, 0f, "不会故障", "这把枪不会发生任何故障。",
                EItemAttributeId.MalfMisfireChance, lessIsGood: true, FormatChanceDelta, 1e-7f);
        }

        float templateDurability = Mathf.Max(1f, weapon.Repairable.TemplateDurability);
        float durabilityPct = weapon.Repairable.Durability / templateDurability * 100f;
        double protections = weapon.Buff != null ? weapon.Buff.MalfunctionProtections : 1.0;

        double durPart = DurabilityMalfChance(weapon.BaseMalfunctionChance, weapon.FireRate, Mathf.Floor(durabilityPct)) * protections;
        float ammoMult = globals != null ? globals.Malfunction.AmmoMalfChanceMult : 1f;
        float magMult = globals != null ? globals.Malfunction.MagazineMalfChanceMult : 1f;
        float ammoPart = ammo != null ? (ammo.MalfMisfireChance + ammo.MalfFeedChance) * ammoMult / 1000f : 0f;
        Magazine mag = weapon.GetCurrentMagazine();
        float magPart = mag != null ? mag.MalfunctionChance * magMult / 1000f : 0f;

        bool ignored = IsIgnored(globals, durabilityPct);
        double total = ignored ? 0.0 : Math.Min(1.0, durPart + ammoPart + magPart);

        var tip = new StringBuilder(256);
        tip.Append("当前状态下每开一枪发生故障的几率（不含过热，过热后另加）。\n");
        tip.Append("· 耐久(").Append(Mathf.Floor(durabilityPct).ToString("0", Inv)).Append("%)：").Append(FormatChance(durPart)).Append('\n');
        tip.Append("· 弹药").Append(ammo != null ? "" : "(无)").Append("：").Append(FormatChance(ammoPart)).Append('\n');
        tip.Append("· 弹匣").Append(mag != null ? "" : "(无)").Append("：").Append(FormatChance(magPart)).Append('\n');
        tip.Append("基础故障率 ").Append(weapon.BaseMalfunctionChance.ToString("0.###", Inv)).Append("，射速越高耐久部分越小。\n");
        tip.Append("耐久 ").Append(IgnoreRange(globals)).Append(" 时以上三项都不会触发。\n");
        tip.Append("参考（耐久部分）：80% ").Append(FormatChance(DurabilityMalfChance(weapon.BaseMalfunctionChance, weapon.FireRate, 80f) * protections))
           .Append("，60% ").Append(FormatChance(DurabilityMalfChance(weapon.BaseMalfunctionChance, weapon.FireRate, 60f) * protections))
           .Append("，40% ").Append(FormatChance(DurabilityMalfChance(weapon.BaseMalfunctionChance, weapon.FireRate, 40f) * protections));

        string text = ignored ? $"0 (耐久≥{IgnoreMin(globals):0}%)" : FormatChance(total);
        return HiddenStatRow.MakeBar("malf", "故障率/发", (float)total, MalfBar(total), text, tip.ToString(),
            EItemAttributeId.MalfMisfireChance, lessIsGood: true, FormatChanceDelta, 1e-7f);
    }

    /// <summary><c>GetTotalMalfunctionChance</c>'s durability term (per shot, 0..1).</summary>
    private static double DurabilityMalfChance(float baseChance, int fireRate, float durability)
    {
        double chance;
        if (durability > 59f)
        {
            chance = (Math.Pow(baseChance + 1f, 3.0 + (100f - durability) / (20.0 - 10.0 / Math.Pow(fireRate / 10.0, 0.322))) - 1.0) / 1000.0;
        }
        else
        {
            chance = (Math.Pow(baseChance + 1f, Math.Log10(Math.Pow(101f - durability, (50.0 - Math.Pow(durability, 1.286) / 4.8) / (Math.Pow(fireRate, 0.17) / 2.9815 + 2.1)))) - 1.0) / 1000.0;
        }
        return double.IsNaN(chance) ? 0.0 : Math.Max(0.0, Math.Min(1.0, chance));
    }

    private static float MalfBar(double p)
    {
        if (p <= 0.0)
        {
            return 0f;
        }
        return Mathf.InverseLerp(MalfBarMinLog, MalfBarMaxLog, (float)Math.Log10(p));
    }

    private static GlobalConfiguration Globals() =>
        Singleton<GlobalConfiguration>.Instantiated ? Singleton<GlobalConfiguration>.Instance : null;

    private static bool IsIgnored(GlobalConfiguration globals, float durabilityPct)
    {
        Vector3 range = globals != null ? globals.Malfunction.DurRangeToIgnoreMalfs : new Vector3(100f, 100f, 0f);
        return durabilityPct >= range.x && durabilityPct <= range.y;
    }

    private static float IgnoreMin(GlobalConfiguration globals) =>
        globals != null ? globals.Malfunction.DurRangeToIgnoreMalfs.x : 100f;

    private static string IgnoreRange(GlobalConfiguration globals)
    {
        Vector3 r = globals != null ? globals.Malfunction.DurRangeToIgnoreMalfs : new Vector3(100f, 100f, 0f);
        return $"{r.x:0}%~{r.y:0}%";
    }

    private static float DurabilityLossPerShot(Weapon weapon, AmmoTemplate ammo, GlobalConfiguration globals)
    {
        if (weapon.Template.OperatingResource <= 0f)
        {
            return 0f;
        }
        float overheatFactor = globals != null ? globals.Overheat.DurReduceMinMult : 1f;
        return weapon.GetDurabilityLossOnShot(ammo != null ? ammo.DurabilityBurnModificator : 1f, overheatFactor, 0f, out _);
    }

    private static float ShotHeat(Weapon weapon, AmmoTemplate ammo, GlobalConfiguration globals)
    {
        float modHeatFactor = globals != null ? globals.Overheat.ModHeatFactor : 1f;
        return weapon.GetShotOverheat(ammo != null ? ammo.HeatFactor : 1f, modHeatFactor, out _);
    }

    private static float CoolFactor(Weapon weapon, GlobalConfiguration globals)
    {
        float mods = weapon.Template.CoolFactorGunMods;
        foreach (Mod mod in weapon.GetAllItems().OfType<Mod>())
        {
            mods *= mod.CoolFactor;
        }
        float modCoolFactor = globals != null ? globals.Overheat.ModCoolFactor : 1f;
        return weapon.CoolFactorGun * mods * modCoolFactor;
    }

    private static string MalfTypes(Weapon weapon)
    {
        WeaponTemplate t = weapon.Template;
        var parts = new List<string>(4);
        if (t.AllowJam)
        {
            parts.Add("卡壳");
        }
        if (t.AllowFeed)
        {
            parts.Add("供弹");
        }
        if (t.AllowMisfire)
        {
            parts.Add("哑火");
        }
        if (t.AllowSlide)
        {
            parts.Add("套筒");
        }
        return parts.Count == 0 ? "无" : string.Join(" ", parts);
    }

    private static AmmoTemplate SafeAmmo(Weapon weapon)
    {
        try
        {
            return weapon.CurrentAmmoTemplate;
        }
        catch
        {
            return null;
        }
    }

    private static float SafeDeviation(Weapon weapon)
    {
        try
        {
            return weapon.GetBarrelDeviation();
        }
        catch
        {
            return 1f;
        }
    }

    private static string Signed(float d, string format) =>
        (d > 0f ? "+" : d < 0f ? "-" : "") + Mathf.Abs(d).ToString(format, Inv);

    private static string FormatChanceDelta(float d)
    {
        double pct = Math.Abs(d) * 100.0;
        string s = pct >= 1.0 ? pct.ToString("0.##", Inv) : pct >= 0.01 ? pct.ToString("0.###", Inv) : pct.ToString("0.####", Inv);
        return (d > 0f ? "+" : "-") + s + "%";
    }

    /// <summary>Per-shot probability (0..1) as "0.012% (≈1/8300)".</summary>
    internal static string FormatChance(double p)
    {
        if (p <= 0.0)
        {
            return "0";
        }
        double pct = p * 100.0;
        string s = pct >= 1.0 ? pct.ToString("0.##", Inv) : pct >= 0.01 ? pct.ToString("0.###", Inv) : pct.ToString("0.####", Inv);
        double every = 1.0 / p;
        string n = every >= 10000 ? (every / 10000.0).ToString("0.#", Inv) + "万" : every.ToString("0", Inv);
        return $"{s}% (≈1/{n})";
    }
}
