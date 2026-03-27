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
dotnet run --project LethalSeedSimulator/src/LethalSeedSimulator.Cli -- export-all --version decompiled-current --moon 0 --seed-start 0 --seed-end 99999999 --output exports/moon0_all.csv
```

Run GUI:

```powershell
dotnet run --project LethalSeedSimulator/src/LethalSeedSimulator.Gui
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
