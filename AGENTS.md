# HiddenStats — agent notes

SPT 4.1.6 (EFT 0.16.9.40743) client-only BepInEx plugin (net472). Goal: show weapon hidden
stats in the item inspect window; step 1 compresses the vanilla attribute area to make room.

## Build / deploy

```bash
dotnet build -c Release HiddenStats.Client/HiddenStats.Client.csproj
# stop the game first, then:
cp HiddenStats.Client/bin/Release/HiddenStats.Client.dll ../BepInEx/plugins/
```

## Layout

- `HiddenStatsPlugin.cs` — config (Chinese names via `ConfigText`, F12 ConfigurationManager).
- `Patches/InspectPanelPatch.cs` — postfix on `ItemSpecificationPanel.method_1` (fills the panel)
  and `RecreateAttributeBars` (rebuilds attribute rows when the item changes).
- `UI/AttributeCompactor.cs` — replaces the `_charPanelContainer` VerticalLayoutGroup with an
  N-column GridLayoutGroup, lowers the `_compactPanel` grid cell height, and scales each row's
  internals (`RowShrinker`). `CompactState` keeps cell sizes in sync in LateUpdate.
- `Core/HiddenStatRows.cs` — builds rows; formulas mirror game code: malfunction =
  `Player.FirearmController.GetTotalMalfunctionChance` minus overheat (durability 93–100% ignores
  durability/ammo/mag malfs via `Malfunction.DurRangeToIgnoreMalfs`), durability loss =
  `Weapon.GetDurabilityLossOnShot` at `Overheat.DurReduceMinMult`, heat = `Weapon.GetShotOverheat`,
  cooling = factors of `Weapon.GetCurrentOverheat`, wear = `Weapon.GetBarrelDeviation`. Recoil feel
  fields are template-only (consumed by `NewRecoilShotEffect` / `ProceduralWeaponAnimation`).
  Gunshot distance: only `SilencerComponent` presence matters (`Player` → `AIData.TryPlayShootSound`
  power `Core.GUNSHOT_SPREAD` 280 / `_SILENCE` 60 → `BotHearingSensor.IsSoundHeard`: dist < power ×
  bot `HearingSense`); `bHearDist`, mod `Loudness`, ammo `ammoHear` are never read by the client.
- `UI/HiddenStatsBlock.cs` — two FlexibleGridLayoutGroups: `HiddenStatsBars` after
  `_charPanelContainer` (dynamic rows, vanilla `_charPanelTemplate` + `RangedItemAttribute` with
  `Base` = bar fill, Range 0..1; value text is ours) and `HiddenStatsCompact` after `_compactPanel`
  (static rows, `_compactCharTemplate`). Rebuilt on `method_1`, `RecreateAttributeBars`, `Refresh`
  postfixes. Compare preview: postfix on `Compare(Item)` (vanilla passes a clone with the
  dragged/hovered mod applied, null to reset) rebuilds rows for the clone, matches by `Key`, calls
  `CompareWith` (blinking compare segment) and appends a coloured delta to the value text.
- `UI/ModdingStatsBlock.cs` — same rows in the weapon modding / edit-build screens' floating
  `CharacteristicsPanel` (no compression): rows go straight into `_charPanelContainer` (bars after
  the last vanilla full bar, cells at the end), removed in an `InstantiateItemFields` prefix (vanilla
  `TemplateList` sibling indices count foreign children) and recreated in its postfix. Preview:
  `OnCompareRequired` / `ResetCompare` postfixes rebuild rows for the persistent `_compareItem`
  clone. Bar/cell creation and compare logic are shared (`HiddenStatsBlock.CreateBar/CreateCell/
  ApplyCompare`).
- `UI/LayoutDump.cs` — one-shot runtime dump of the vanilla attribute area (first weapon inspected
  per session, config `LayoutDump`, default off) — the game has no MonoBehaviour typetrees, so prefabs can't be read statically.

## Hidden-stat research

`WeaponTemplate` fields vs. what `Weapon`'s constructor registers as `Attributes`
(`Weapon.cs` ~L987-1075). Survey script: `C:/Users/Administrator/scratch/hidden-stats/survey*.py`
(reads `SPT_Runtime/SPT_Data/database/templates/items.json`).

Compatible with RaidIntel: its InspectScrollMerge re-parents the vanilla attribute blocks into the
description scroll content (once per window, possibly after our postfix). Our grids are therefore
kept on the panel (`PanelGrids`, never looked up by parent) and each one follows its vanilla anchor
(`GridRefs.Follow`, every LateUpdate: same parent, sibling index anchor+1, row-gap padding
recomputed for the new parent's spacing). No mod detection needed.
