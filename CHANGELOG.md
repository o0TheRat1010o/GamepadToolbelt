# Changelog

All notable changes to this mod are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).
The version in `GamepadToolbelt/ModInfo.xml` is the single source of truth.

## [1.0.0] - 2026-10-18

First release, built and tested against 7 Days to Die **V 3.3.0 (b18)**.

### Added
- **Controller chords for the toolbelt:** tap LB = slot 1, hold LB + A/X/Y/B = slots 2-5, LB + RB = slot 6,
  hold RB + A/X/Y/B = slots 7-10, tap RB = slot 11. While a bumper is held, face buttons switch as often as you like;
  a bumper held longer than `TapMaxSeconds` without a chord cancels. The buttons of a chord are taken away from the
  game until they are released, so nothing else (jump, crouch, reload, vanilla toolbelt radial) fires. Only active while
  playing on foot - menus, inventory, vehicles, radial menus, console, chat, map and editors stay vanilla.
- **11th toolbelt slot**, offered by the server or host (`ToolbeltSlots`). Players with the mod follow the server; players
  without it keep 10 slots and stay fully compatible (the server never sends them a slot number above 10, neither for
  held items nor for shots and other weapon effects). Old saves get an empty 11th slot. Keyboard key `-` for slot 11,
  also while sprinting (`KeyboardSlot11Key`).
- **Button hints** on the toolbelt slots with the game's Xbox/PlayStation glyphs: reachable slots lift and glow while a
  bumper is held, others fade, a timer ring shows the tap window of slot 1/11, selections pop and flash; the XP bar slides
  up while hints are shown. Shown only in controller mode (`HintMode`, `HintIdleOpacity`).
- Short haptic pulse on a chord (`Haptics`, `HapticStrength`, `HapticMs`), following the game's vibration options.
- Console command `gtb` (`gamepadtoolbelt`): `status`, `selftest`, `reload`, `loglevel`, `chords`, `screenshot`; it
  runs on your own game, also as a guest on servers without the mod.
- JSON-lines audit log with automatic selftests at game start and after spawning, runtime invariants (no button may
  leak to the game while owned, every selection must reach its slot, network guards) and optional self-check
  screenshots of every hint state.
- Safety: every patch group is verified before installation and rolls back completely on any failure (the feature then
  stays vanilla); the 11th slot is only offered when both the toolbelt and the network patches are active; the
  controller layer switches itself off after repeated errors.
