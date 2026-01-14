# Changelog

## 0.1.5 - 2026-01-14
- Remote avatar now loads the MWCMP mpdata AssetBundle by default (Unity 5.0.0f4 build).
- Added auto-ground offset using mesh bounds and tuned scale defaults (Scale 3.5, YOffset 0.6).
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
