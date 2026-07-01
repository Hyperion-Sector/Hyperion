# Edison port sources (vendored archive)

Verbatim copies of third-party fork content we plan to port, archived here so the
port doesn't depend on the upstream repos staying alive. Nothing in this directory
is loaded by the game; files keep their original repo-relative paths under each
source's subdirectory.

## Sources

| Subdirectory | Repository | Commit | Archived |
|---|---|---|---|
| `coyote-frontier/` | https://github.com/ARF-SS13/coyote-frontier | `2f30035e7bb0a6922f8ca0df120a009eb4789242` | 2026-07-01 |
| `far-horizons/` | https://github.com/Far-Horizons-SS14/Far-Horizons-SS14 | `4afadbf176233a3bef6e6a327eb0a4b4bb8ccedc` | 2026-07-01 |

## Licenses

Each subdirectory carries the license files from its source repo.

- **coyote-frontier**: dual MIT (pre-cutoff Wizden/Frontier heritage) / AGPLv3.
  The Edison map and `_CS` content are post-cutoff, treat as AGPLv3.
- **far-horizons**: MIT for original Wizden + FH changes; "Starlight License"
  (MIT + mandatory attribution to https://github.com/ss14Starlight/space-station-14)
  for Starlight-derived code and all pre-`5b5744cd` submissions.
- **WARNING**: FH's README states the nuclear-reactor code is taken from
  Goonstation and licensed **CC-BY-NC-SA-3.0** (NonCommercial ShareAlike). Treat
  the whole `FissionGenerator` stack as CC-BY-NC-SA-3.0 unless clarified with FH.
  Porting it imports the NC restriction; decide before the port PR, not after.

## What's here and why

### coyote-frontier/

- `Resources/Maps/_CS/POI/edison.yml`: the map itself (Coyote's rebuilt Edison
  Power Plant, ~13.9k entities).
- `Resources/Prototypes/_NF/PointsOfInterest/edison.yml`: their POI/gameMap wiring
  (gridPath repoint, IFF, transit routes, PlantManager/PlantTechnician job slots).
- `Resources/{Prototypes,Textures,Audio,Locale/en-US}/_CS/`: Coyote's whole custom
  content layer, copied wholesale (~4 MB) so Edison's `_CS` references can't dangle.
- Individual `_NF`/`_HL`/`_DV`/`Recipes` YAML files: defining files for the 47
  prototypes + 2 tiles + 2 decals the map uses that Hyperion lacks, plus the four
  ancestor files needed to close their `parent:` chains
  (`crates.yml`, `lathe.yml`, `sink.yml`, `machine_boards.yml`).
- `Content.{Client,Server,Shared}/_EE/**Supermatter**` + SM textures/audio + `_DV`
  supermatter YAML: the Einstein Engines Supermatter stack the map was built
  around. Archived for optionality even though the current plan replaces it.
- Referenced sprite/audio assets missing from Hyperion (resolved from the archived
  YAML; RSIs identical to ours were not duplicated).

### far-horizons/

- `Content.{Client,Server,Shared}/_FarHorizons/Power/**`: the fission reactor,
  gas turbine, centrifuge, and monitoring console code (~5.2k lines) plus
  `CCVar/` and the 72-line `Materials/` shim it depends on.
- `Resources/Prototypes/_FarHorizons/**`: reactor/turbine/centrifuge parts,
  structures, effects, lathe recipes, cargo entries, and the bohrum/cerenkite
  material definitions (`Reagents/Materials/metals.yml`).
- `Resources/{Textures,Audio}/_FarHorizons/**`: all fission-related assets.
- `Resources/Locale/en-US/_FarHorizons/fission-generator/`: UI strings.
- `Resources/Maps/_FarHorizons/Dungeon/Reactor/` + `Procedural/Reactor/`: their
  reactor dungeon content. Not needed for Edison; archived because it's small and
  entangled with the reactor prototypes.

## Known port-time gotchas

- Whole-file copies include extra prototypes beyond Edison's needs; only the
  Edison-relevant parent chains were verified complete. Don't blind-load these
  files; extract the prototypes the port needs.
- `NFPoweredlightShieldedEmpty` sets `solarFlareShieldingCoefficient` on
  `PoweredLight`: a Frontier solar-flare feature Hyperion doesn't have. Strip the
  field or port the feature.
- `MonoCatwalk` (94 placements) is rename drift: Hyperion has `SteelMonoCatwalk`
  and friends. Fix in the map, don't port.
- `HeliumCanister`/`WarningHelium` (1 each) reference a Frontier helium gas
  Hyperion lacks. Substitute; do not port a gas for two entities.
- The map's `_CS` console/techfab variants and PlantManager/PlantTechnician jobs
  need their access/loadout wiring checked against our job tables.
