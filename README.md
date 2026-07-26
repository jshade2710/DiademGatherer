# DiademGatherer

A Dalamud plugin that runs the Ishgardian Restoration loop in **The Diadem**: it
gathers a fixed route, certifies the loot, spends scrips, repairs, and re-enters
the instance — then does it again.

## What it does

- **Gathers a route natively.** It finds the real node near each recorded
  waypoint, flies in, lands, and works the item slots itself. Node approach uses
  a combined fly→land→walk path so it descends under navigation control rather
  than dropping onto the node.
- **Respects the node chains.** Diadem nodes spawn sequentially — the next node
  only appears once the previous one is *exhausted* — so it always empties a node
  before moving on. Abandoning one forfeits every node behind it.
- **Learns where to stand.** Whenever a node opens, the spot you were standing on
  is remembered and reused (several per node, picked at random) so approaches get
  more reliable over time and don't repeat the same pixel every lap.
- **Spends GP procs.** A Revisit proc refunds GP and adds integrity; the
  buff plan re-arms so that bonus is gathered under buffs.
- **Handles the round trip.** Leave duty → certify at Flotpassant → spend scrips
  at Enie → repair with dark matter → back in via Aurvael.
- **Stops at the cap.** Per-class Firmament score caps at 500,000; it skips
  turn-ins for a capped class instead of wasting points.

There's also a crafting loop (Artisan → Potkin turn-ins → Lizbeth's Kupo of
Fortune) for the collectables side.

## Requirements

- [vnavmesh](https://github.com/awgil/ffxiv_navmesh) — required, all movement
  goes through it
- [Artisan](https://github.com/PunishXIV/Artisan) — only if you use the crafting tab

## Usage

`/diadem` opens the window. Everything is driven from there: Routes, Crafting,
Shop and Settings tabs, with Start / Pause / Skip / Force Reinstance controls.

## Reporting a problem

**Settings → Troubleshooting → Save Log Report.**

That writes `diadem-report.txt` next to the plugin config and copies the path to
your clipboard. Attach it to the issue. It contains only this plugin's log lines
plus your settings — Dalamud's own log interleaves every plugin, which is too
noisy to be useful. Mention which node label it went wrong at (e.g. `R4`, `B7`)
if you know it; the report is indexed by those.

## Credit

Node-approach behaviour follows
[GatherBuddyReborn](https://github.com/FFXIV-CombatReborn/GatherBuddyReborn)
(Apache-2.0) — its combined-path routine, interaction envelope and repair
sequence were the reference for getting this reliable.
