# Changelog

## 0.1.8 - 2026-02-06
- Sorbet dashboard/control payload extended with turn signals, ignition, starter, and interior light fields.
- Sorbet control sampling now prefers explicit FSM variables over raw state index when available.
- BUS nav payload fields are now included in realtime `NpcState` packets (`TargetSpeed`, `Route`, `Waypoint`, `WaypointStart`, `WaypointEnd`) instead of snapshot-only behavior.
- Scrape replication stability pass:
  - Added remote-baseline dedupe for incoming scrape states.
  - Increased remote hold/suppress windows and blocked remote->local scrape echo loops.
  - Added explicit scrape-finish `OFF`/inside-reset handling to reduce lingering scrape audio loops.
- Reduced vehicle dashboard PlayMaker spam by filtering non-Sorbet/non-BUS vehicle event-only loops.
- Suppressed Sorbet dashboard `EventOnly` door events (ON/OFF chatter) and rely on `SorbetDashboardState` as the authoritative channel for those controls.
- NPC animator candidate matching now uses hierarchy path tokens in addition to object name.
- Expanded default NPC name filters to include pub/shop actors (`teimo,shop,seller,cashier,customer,bartender`).
- Updated config templates with the expanded NPC name filter defaults.

## 0.1.6 - 2026-01-26
- Sorbet scrape state sync + initial snapshot on join to align frost state.
- Sorbet dashboard/HVAC replication (heater temp/blower/direction/window heater/lights/hazard), including state-index apply.
- Passenger seat snapping (position + rotation) to reduce in-car avatar lag.
- NPC/traffic scanning upgraded with delayed rescans and candidate dumps for filter tuning.
- Updated default NPC vehicle filter to include bus/taxi/truck/van.

## 0.1.5 - 2026-01-14
- Remote avatar now loads the MWCMP mpdata AssetBundle by default (Unity 5.0.0f4 build).
- Added auto-ground offset using mesh bounds and tuned scale defaults (Scale 3.8, YOffset 0.85).
- Cabin sink/tap and fridge interactions sync.
- Sorbet vehicle sync is still WIP: doors almot complete, driving/ownership working but clunky - still in progress.

## 0.1.4 - 2026-01-11
- Added vehicle hinge door updates (protocol v7) and spring-based remote apply to avoid car jumps.
- Updated vehicle door rotation policy with clearer logging and safe fallback for non-physics doors.
- Expanded DoorSync name filter defaults to include sink/tap hinges.
- Added avatar AssetBundle config (BundlePath, AssetName, Scale, YOffset).
- Added PlayMaker FSM scanner for sink/phone diagnostics (verbose logging only).
- Added TimeOfDay sync (also logs time for both client and host in overlay)

## 0.1.3 - 2026-01-11
- DoorSync now maps PlayMaker FSMs to door pivots/hinges and merges FSMs into hinge entries.
- Added door rescan when transforms/FSMs go missing to reduce desync.
- Interior door sync improved (room doors + cabin front entrance).

## 0.1.2 - 2025-12-30
- Remote avatar updated to simple body primitives.
- Player locator improvements (standard assets controllers, menu camera filtering).
- Added UnityPlayerSettingsPatcher tool and launch script improvements.
- Verbose logging enabled in templates and expanded debug tracing.
- Added developer iteration guide (instructions.txt).

## 0.1.0 - 2025-12-29
- TCP LAN fallback transport.
- Camera, level, and progress marker sync.
- Simple overlay and spectator lockdown option.

## 0.1.1 - 2025-12-30
- Main menu co-op panel with host/join controls.
- LAN discovery + in-game join list.
- Session hardening (handshake, keepalive, timeouts, reconnect).
- TCP LAN implicit handshake fallback and level resync retries.
- Per-instance debug logs for troubleshooting.
- Multi-instance toggle (skip Steam bootstrap).
