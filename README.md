# OSMEarth (OSMRoads)

A KSP 1.12.5 mod that builds real-world roads, buildings, forests, crops and street lighting
from [OpenStreetMap](https://www.openstreetmap.org/copyright) data, **procedurally, at runtime** —
no pre-baked meshes, no Kerbal Konstructs statics for the terrain itself. Built for a real-Earth
planet pack (Kopernicus Sol, `SystemScale = Real`), but reads `body.Radius` at runtime so it adapts
to other scales too.

## What it does

- **Roads** — meshes built from OSM ways and snapped to the real PQS terrain, with sidewalks,
  curbs, junctions, bridges (height-compensated, with ramps onto connecting roads) and street lamps.
- **Buildings** — extruded from OSM footprints with height/levels/roof-shape tags, LOD'd by distance.
- **Trees** — procedural meshes (no imported assets), instanced with `Graphics.DrawMeshInstanced`
  like Parallax's scatter system, with world-matrix caching so a forest doesn't recompute every
  tree's transform every frame.
- **Crops / landuse fills** — chunked scatter meshes for farmland and parks.
- **Live map planner** — in-game satellite/road map window with search, routing and a point-to-point
  measuring tool.
- **Kerbal Konstructs export** — generates launch-site configs from real airport runways (Poland) and
  real launch-complex locations (Cape Canaveral / KSC, from `man_made=launch_pad` + `aeroway=runway`
  tags), fetched live via Overpass.
- **A full on/off switch**, saved to `settings.cfg`: off means zero work (no scanning, no worker
  thread, no LateUpdate) and every rendered tile is torn down immediately.

Data comes live from the public [Overpass API](https://overpass-api.de); no account or key needed.

## Requirements to build

- KSP 1.12.5 install (for the reference assemblies) and [Kopernicus](https://github.com/Kopernicus/Kopernicus).
- [Harmony](https://github.com/pardeike/KSPHarmony) (`000_Harmony`) in your `GameData`.
- [DeltaUI](https://github.com/deltaspacesystems-ksp/DeltaUI) — windows, tabs and the shared UI theme.
  Build it first; this project references `..\DeltaUI\dist\GameData\DeltaUI\Plugins\DeltaUI.dll`.
- .NET SDK (the csproj targets `net472`, KSP 1.12's profile).

```bash
dotnet build OSMRoads.csproj -c Release
```

Output: `dist/GameData/OSMRoads/Plugins/OSMRoads.dll`. Copy `dist/GameData/OSMRoads` into your KSP's
`GameData`, with KSP closed.

## Layout

- `src/` — the mod itself (KSPAddons, Harmony patches, map/scatter/building/road builders).
- `tools/` — standalone desktop tools used to prepare and inspect data: `OSMView` (renders test
  tiles without launching KSP), `PlacesIndex`/`PlanetPack`/`Lod2Pack`/`OsmPack` (build the binary
  pack format from raw OSM extracts).
- `tools/planetiler/planetiler.jar` (not committed — see `.gitignore`) is a third-party tool used
  when preparing world packs; fetch it separately if you need to rebuild packs from scratch.

## License

Map data © [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), ODbL.

---

Built with [Claude Code](https://claude.com/claude-code).
