# v0.9.24-beta.1 validation

Target: Bannerlord 1.5.6 / War Sails 1.3.6, Steam build 26242440.
Release: Workshop item [3833378968](https://steamcommunity.com/sharedfiles/filedetails/?id=3833378968), public beta. After being informed that landing, hideouts, construction repair and AI behavior changed, the maintainer requested publication without completing the remaining campaign matrix. The items below remain outstanding rather than being marked passed.

## Completed

- Exact-version build succeeded; no version-mismatch override.
- VerifyBeta.ps1 and optional fleet-policy regression fixtures passed.
- Independent compiled API audit: 116 external types and 299 members resolve; zero unresolved references.
- All 28 ordinary Harmony targets resolve uniquely; 70 target-bound parameters match. The additional dynamically resolved AI shim is covered by its separate runtime fixture.
- Player Settlement 7.5.0 references resolve. PlayerSettlementFixes 1.0.0 retains exactly the known removed Objective call; no dependency binary is altered.
- The real AI shim class passed a .NET Framework runtime fixture: a mismatched game version is rejected, the exact binary's caller redirect installs and executes, patch ownership is verified, and unpatching makes the readiness check fail.
- A direct prefix on the broken helper was proven unusable because Harmony resolves the missing token while reading it. The shipped shim instead replaces exactly one verified call in the caller, retaining its other instructions and all remaining eligibility filters.
- Created the candidate privately; linked Harmony 2859188632 and Player Settlement 3720376888 as required Workshop items.
- Downloaded the private item through Steam and verified all five payload hashes against the staged package.
- Launched the downloaded artifact through standard Bannerlord.exe: runtime versions supported, 27/29 patches applied, two intentional native-audio skips, no patch failures, and the AI caller redirect active.
- User started a new Sandbox campaign and ran blwii.compat. A supplied photo confirms the beta version/target; the engine log confirms NavalMapScreen loaded, island features initialized, and all 22 island surfaces imported. No full gameplay-regression result is inferred from that initialization.
- Stable identity: module BLWarsailsInhabitableIslands, launcher v0.9.15, assembly 0.9.21.0. File version 0.9.24.0; informational version 0.9.24-beta.1. Save schema unchanged.

## Remaining regression checks (not completed)

- Confirm the creation listener in campaign diagnostics and load a compatible copied existing save; preserve the original.
- Paused/unpaused single/double-click landing, same-island walk, island crossings, embarkation, remote settlement clicks, native mainland travel, Ctrl+Click parley and follow modifiers.
- Confirmed town/castle/village creation, first hourly/daily ticks, trade, fishing production, visitor/ferry handling, and save/reload.
- Hideout discovery/clearing/reactivation; raids, naval visitors, village-port army sailing, enemy scoring and siege of a player-built fortification, native ferries/blockades.
- Several in-game days without unhandled exceptions, missing-method errors, stuck parties, or transition loops.
- Repeat critical checks using the downloaded artifact; verify public download hashes after publication.

## Current UI limitation

Windows graphics capture has repeatedly returned `window capture timed out` / `FrameArrived timed out`, including after reconnecting and using fresh window handles. The user confirmed the local desktop is awake and unlocked. Accessibility inspection exposes only a pane for Steam/the launcher, without usable game controls. No blind mouse actions are used. This limitation does not waive the campaign acceptance gates.

The maintainer explicitly elected public-beta publication despite incomplete campaign validation. The listing discloses that limitation. Existing 1.5.2 and 1.4.8 content packages remain preserved; only their descriptions receive cross-links.
