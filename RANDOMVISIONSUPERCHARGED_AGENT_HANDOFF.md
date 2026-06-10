# RandomVisionSuperCharged Agent Handoff

## Workspace

- Current working folder: `/Users/cynthialiu/Desktop/RandomVisionSuperCharged`
- User constraint: do not change anything beyond the current working folder. Reading outside the folder has been used for inspection, but writes should stay under this workspace.
- `AGENTS.md` instruction: for every installed library, inspect the library and dependencies for supply-chain risk. No new libraries were installed during this work.
- Git repo exists locally, but many files are untracked. Do not revert unrelated changes.

## Project

RandomVisionSuperCharged is a Godot/C# Slay the Spire 2 mod that previews event outcomes, map encounter queues, draw pile order, and related seeded predictions.

The user expects every complete fix to be packaged into:

- `/Users/cynthialiu/Desktop/RandomVisionSuperCharged/RandomVisionSuperChargedBuild/RandomVisionSuperCharged.dll`
- `/Users/cynthialiu/Desktop/RandomVisionSuperCharged/RandomVisionSuperChargedBuild/RandomVisionSuperCharged.json`
- `/Users/cynthialiu/Desktop/RandomVisionSuperCharged/RandomVisionSuperChargedBuild/RandomVisionSuperCharged.pck`

## Build And Publish

Use these exact paths:

```sh
dotnet build RandomVisionSuperCharged.csproj -c Release \
  -p:Sts2DataDir="/Users/cynthialiu/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64" \
  -p:Sts2Path="/Users/cynthialiu/Library/Application Support/Steam/steamapps/common/Slay the Spire 2" \
  --no-restore
```

```sh
dotnet publish RandomVisionSuperCharged.csproj -c Release \
  -p:Sts2DataDir="/Users/cynthialiu/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64" \
  -p:Sts2Path="/Users/cynthialiu/Library/Application Support/Steam/steamapps/common/Slay the Spire 2" \
  -p:ModOutputDir="/Users/cynthialiu/Desktop/RandomVisionSuperCharged/RandomVisionSuperChargedBuild" \
  -p:GodotPath="/Users/cynthialiu/Desktop/sts2/sts2mod/.tools/godot-4.5.1/Godot_mono.app/Contents/MacOS/Godot" \
  --no-restore
```

After publishing, run:

```sh
chflags nohidden RandomVisionSuperChargedBuild/RandomVisionSuperCharged.dll
ls -laO@ RandomVisionSuperChargedBuild
shasum -a 256 RandomVisionSuperChargedBuild/RandomVisionSuperCharged.dll RandomVisionSuperChargedBuild/RandomVisionSuperCharged.json RandomVisionSuperChargedBuild/RandomVisionSuperCharged.pck
dotnet list RandomVisionSuperCharged.csproj package --vulnerable --include-transitive
find . -maxdepth 2 \( -name .rvinspect -o -name Inspector.csproj \) -print
```

Known publish warning:

```text
Detected another project.godot at res://FrozenEye. The folder will be ignored.
```

This has been expected because `FrozenEye/` is a nested project under the workspace and is excluded from the csproj/export.

## Important Files

- Main mod initializer: `MainFile.cs`
- Event overlay: `Services/RandomVisionSuperChargedEventOverlay.cs`
- Event prediction registry: `Services/RandomVisionSuperChargedPreviewRegistry.cs`
- Map overlay: `Services/RandomVisionSuperChargedMapEncounterOverlay.cs`
- Map overlay patch: `Patches/MapEncounterOverlayPatch.cs`
- Shared preview models: `Services/EventPreviewModels.cs`
- Ordered draw pile patch, ported from FrozenEye: `Patches/OrderedDrawPilePatch.cs`

## Current Map Overlay State

Primary recent work is in `Services/RandomVisionSuperChargedMapEncounterOverlay.cs`.

Current behavior:

- Overlay appears on the map screen, upper-right by default.
- It has 4 columns:
  - Normal encounters, max 10 queued rows.
  - Elite encounters, max 5 queued rows.
  - Events, max 15 queued rows.
  - Ancients, one row per act where an Ancient is seeded.
- Normal/elite rows show encounter title. Hover text shows encounter id and monster names.
- Event rows show title. Hover text shows event id, title, requirements, and current `IsAllowed` check result.
- Event row text is green when `eventModel.IsAllowed(runState)` passes.
- Ancient rows show `Act X: Ancient Name` and a subtitle of option titles where readable.
- Ancient rows no longer attempt to initialize the Ancient or predict option details, because `SetInitialEventState()` caused null-reference crashes for `NEOW`, `OROBAS`, and `NONUPEIPE`.
- Ancient option extraction is defensive:
  - Reads `_generatedOptions` through Harmony `FieldRef` if non-null/non-empty.
  - Falls back to `ancient.AllPossibleOptions`.
  - Per-act Ancient row generation is wrapped, so a bad Ancient cannot crash the whole map overlay.

Recent map overlay fixes:

- Replaced game `NHoverTipSet` for map rows with a RandomVisionSuperCharged-owned tooltip node named `RandomVisionSuperChargedMapEncounterTooltip`.
- Tooltip is added under `RandomVisionSuperChargedMapEncounterRoot` with high `ZIndex`, so it should render above the overlay panel rather than under it.
- Constants at top include:
  - `PanelWidth = 840f`
  - `PanelHeight = 360f`
  - `RowTooltipWidth = 340f`
  - `RowTooltipHeight = 190f`
  - `RowTooltipZIndex = 10000`

Latest crash fixed:

```text
System.NullReferenceException at RandomVisionSuperCharged.Services.RandomVisionSuperChargedMapEncounterOverlay.ReadAncientOptionTitles(AncientEventModel ancient)
```

Cause: `_generatedOptions` could be null. It is now null-checked and wrapped.

## Event Requirement Hover Text

The event column hover text contains manually summarized `IsAllowed` requirements, inspected from base-game event classes. Examples:

- `MorphicGrove`: at least 100 gold and at least 2 transformable deck cards.
- `EndlessConveyor`: at least 120 gold.
- `RanwidTheElder`: Act 2+, tradable relic, at least 100 gold, at least one potion.
- `WelcomeToWongos`: Act 2 and at least 100 gold.
- `WhisperingHollow`: at least 44 gold.
- `WarHistorianRepy`: base-game `IsAllowed` currently returns false.

The live `IsAllowed` check is recalculated when the overlay is rebuilt/refreshed, currently from `NMapScreen.SetMap` and `NMapScreen.Open`, not every frame while the map stays open.

## Previous Feature History

The user has asked for many event/Ancient-specific prediction fixes in `RandomVisionSuperChargedPreviewRegistry.cs`. Important implemented cases include:

- Neow options:
  - Lead Paperweight card choices
  - Hefty Tablet rare card choices
  - New Leaf transform target and entities
  - Kaleidoscope card rewards from other characters
  - Neow's Bones relics and curse, with nested Neow relic adapter previews
  - AromaOfChaos entities
- Ancient options:
  - Darv `PANDORAS_BOX`
  - Darv `CALLING_BELL`
  - Darv `ASTROLABE`
  - Vakuu `SERE_TALON`
  - Orobas `GLASS_EYE`, `ALCHEMICAL_COFFER`, `SEA_GLASS`, `SAND_CASTLE`
  - Tezcatara `TOY_BOX`
- Event cases:
  - `Trial` guilty and innocent transform/card reward branches
  - `TinkerTime` all levels of options
  - `SlipperyBridge` next 10 hold-on results
  - `EndlessConveyor` repeats until insufficient gold, including 75 gold gain case, plus second option card preview
  - `DoorsOfLightAndDark` first option upgrades 2 random cards
  - `PunchOff` fight reward: potion, relic, card reward
  - `TabletOfTruth` upgrade order
  - `MorphicGrove` transform 2 cards
  - `TrashHeap` relic/card preview
  - `WelcomeToWongos` card preview
  - `Wellspring` and `WhisperingHollow` potion preview
  - `LuminousChoir` relic preview
  - `WarHistorianRepy` potion/reward preview
  - `HungryForMushrooms` upgraded cards
  - `RoundTeaParty` relic reward
  - `ThisOrThat` and `RanwidTheElder` relic hover previews
  - `PotionCourier` and `PhialHolster` potion previews
  - `BattlewornDummy`, `Reflections`, `Symbiote` entities

## FrozenEye And Math Ports

FrozenEye utility:

- Draw-pile order functionality was moved into RandomVisionSuperCharged through `Patches/OrderedDrawPilePatch.cs`.
- Logs were added earlier to debug whether it runs.

Math folder:

- `MathMapOddsPrediction` was inspected and initially ported as a focus patch, then removed at the user’s request.
- The current map overlay is not the old Math focus patch. It uses the same RandomVisionSuperCharged overlay style instead.

## Last Published Package

Latest package after fixing the Ancient option null crash:

```text
143cbe044991f18b59c7642fd53611a870bf8ba34895ce9b88de68ae22aaae16  RandomVisionSuperChargedBuild/RandomVisionSuperCharged.dll
0354606788c49950ce6ba29cfd6f61b3ac5ed65a8c14a487f6dc6989060138cb  RandomVisionSuperChargedBuild/RandomVisionSuperCharged.json
71977df9b4b3ef676d9e35ab663a7e2c34cb45cdfac366586075130af8fc0123  RandomVisionSuperChargedBuild/RandomVisionSuperCharged.pck
```

Build and publish passed. `dotnet list RandomVisionSuperCharged.csproj package --vulnerable --include-transitive` reported no vulnerable packages.

## Recommended Next Checks

When the next agent starts:

1. Ask the user for the newest game log after loading the latest build.
2. Verify whether the custom row tooltip now appears above the overlay.
3. Verify whether the Ancient column displays direct option titles without crashing.
4. If Ancient option titles show as unavailable, inspect whether the desired option state exists only after the actual Ancient room is created. In that case, avoid calling `GenerateInitialOptions()` unless the required `Owner`, `Rng`, and runtime event context are safely initialized.
5. Preserve the user’s workspace-only write constraint.
