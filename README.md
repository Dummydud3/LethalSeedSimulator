# Lethal Seed Simulator

Deterministic seed simulator for Lethal Company data dumps.

## What this includes

- `LethalSeedSimulator.Cli` for extract/inspect/search/validate/export workflows
- `LethalSeedSimulator.Gui` (Windows) for point-and-click usage
- Rulepack extraction from decompiled source + Unity YAML assets
- Moon-specific scrap pools, rarities, values, and seed-driven simulation

## Requirements

- .NET SDK 9.0+
- Decompiled dump folder containing:
  - `Assembly-CSharp/`
  - `Assets/MonoBehaviour/`

## Quick start

From repository root:

```powershell
dotnet build LethalSeedSimulator/LethalSeedSimulator.sln
```

Generate or refresh rulepack:

```powershell
dotnet run --project LethalSeedSimulator/src/LethalSeedSimulator.Cli -- extract --version decompiled-current --source-root .
```

Inspect one seed for one moon:

```powershell
dotnet run --project LethalSeedSimulator/src/LethalSeedSimulator.Cli -- inspect --version decompiled-current --moon 0 --seed 12345
```

Search a range:

```powershell
dotnet run --project LethalSeedSimulator/src/LethalSeedSimulator.Cli -- search --version decompiled-current --moon 0 --seed-start 1 --seed-end 500000 --query "only-goldbar-day"
```

Bulk export:

```powershell
dotnet run --project LethalSeedSimulator/src/LethalSeedSimulator.Cli -- export-all --version decompiled-current --moon 0 --seed-start 0 --seed-end 99999999 --output exports/moon0_all.db
```

Run GUI:

```powershell
dotnet run --project LethalSeedSimulator/src/LethalSeedSimulator.Gui
```

Install GUI app (Windows, per-user install):

```powershell
powershell -ExecutionPolicy Bypass -File LethalSeedSimulator/scripts/install-gui.ps1
```

Uninstall GUI app:

```powershell
powershell -ExecutionPolicy Bypass -File LethalSeedSimulator/scripts/uninstall-gui.ps1
```

## Path configuration

You can run from any working directory by providing these options/env vars:

- `--source-root <path>` or `LETHAL_SIM_SOURCE_ROOT`
- `--rules-root <path>` or `LETHAL_SIM_RULES_ROOT`
- `--export-root <path>` for CLI bulk exports

## Notes

- GUI is Windows-only (`WinForms`).
- CLI is cross-platform.
- Rulepack extraction depends on the structure and names in your decompiled dump.
- Installed GUI stores app data under `%LOCALAPPDATA%\LethalSeedSimulator`.

## GUI workflow (3 tabs)

- `Inspector`
  - Inspect one seed for one moon and view full JSON report.
- `Bulk Simulator`
  - Select a version and moon.
  - Use `Options` to choose seed range (default `0..99,999,999`).
  - `Simulate Selected` runs one moon.
  - `Simulate 100,000,000` runs the selected range for each moon in the list.
  - Default mode skips existing rows in moon DBs (`INSERT OR IGNORE`).
  - Enable `Force re-simulate` to overwrite rows (`INSERT OR REPLACE`) and refresh item-count details.
- `Bulk Viewer`
  - Load one moon DB at a time.
  - Sort by clicking table headers (`seed`, `total_scrap_value`, `scrap_count`, `key_count`, `apparatus_value`, `weather`, `dungeon_flow_theme`).
  - Apply optional min/max `total_scrap_value` filters.
  - Uses server-side pagination and sort SQL, so it does not load full tables into memory.

## AppData layout

GUI moon databases are stored per version and moon:

```text
%LOCALAPPDATA%/LethalSeedSimulator/data/<version>/<moon>/seeds.db
```

This layout supports resumable chunk/range runs and independent moon-level viewing.
