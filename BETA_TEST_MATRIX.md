# Inhabitable Isles 1.5 Release Test Matrix

This release targets Bannerlord 1.5.2, War Sails 1.3.2, and Player Settlement 7.5.0. It is an open Workshop playtest, with player reports used to validate campaign behavior across saves and mod lists.

## Static Gate

1. Confirm Bannerlord and the War Sails Modding Kit are both on Steam's `beta - Beta v1.5.2` branch and all downloads have finished.
2. Run `BuildLocal.ps1` without `-AllowVersionMismatch`. A mismatch must stop the build.
3. Run `VerifyFleetCompatibility.ps1`; all optional-getter and version-rule fixtures must pass.
4. Run `VerifyBeta.ps1` against the explicit local deployment path. It must confirm the compiled DLL has no direct `Hero.get_CanHaveFleet` reference.
5. Confirm the `bannerlord-1.4.8` Git branch still targets Bannerlord 1.4.8 and War Sails 1.2.8.
6. Confirm any Steam description-only update omitted `ModuleFolder` and did not change the Workshop content manifest.

## New Campaign

1. Start a new campaign with War Sails, Harmony, Player Settlement, and Inhabitable Isles enabled.
2. Run `blwii.compat`. Expect `runtime compatibility: supported`, the `1.5 native-first` profile, `Hero.CanHaveFleet available`, no failed patches, and either 22 imported surfaces or a reviewed map-delta warning.
3. Run `blwii.hideouts audit`. Every active hideout must have valid navigation and a visible, clickable marker after discovery.
4. Run `blwii.ferries`. The count should begin at zero and increase only when an eligible NPC needs an island crossing.
5. Sail to an imported island, disembark, walk, re-embark, and return to a native port. There must be no teleport movement or stuck transition.
6. Build unfinished and finished Player Settlement village, castle, and town variants. Check gate clicks, automatic coastal ports, settlement menus, and grounded siege staging.
7. Let villagers, caravans, convoys, companions, lords, and an army visit and leave. Their objectives must survive embarkation, and visitors must not accumulate inside the holding.
8. Disable **Have Fleet** for a hero-led clan party and confirm Inhabitable Isles does not issue it an emergency ferry. Enable the toggle and confirm an imported-island route can receive ferry support when needed.
9. Allow an enemy raid and siege. Confirm attackers reach the island, siege assets remain grounded, and the campaign resumes after battle.
10. Discover, attack, clear, and revisit an island Sea Raider hideout. Confirm its patrols can move on land and sea and the 30-day settlement grace behavior remains intact.
11. Zoom and rotate through day, night, and weather changes. Trees must not flash or produce giant bounds. Repeat with `blwii.trees off` and `blwii.trees on`.

## Existing Save

1. Back up the save, then load it once with the v0.9.23 release candidate.
2. Confirm tracked temporary ferries reconcile without deleting ordinary owned ships.
3. Save while idle on an island, during an NPC crossing, and after a hideout state change; reload each save.
4. Repeat settlement entry/leave, convoy departure, companion recruitment, and island siege checks.
5. On an intentionally mismatched test installation only, confirm a single unsupported-version warning appears and hourly villager processing does not throw `MissingMethodException`. This does not qualify that version for support.

## Logs

Review the newest `rgl_log_*.txt` and crash HTML before release. A caught startup exception whose stack is entirely inside `PlayerSettlementFixes.PartyVisualPatch.FixOnStartup` is external and should remain visible in the report. Any stack containing `BLWarsailsInhabitableIslands` must be fixed or explicitly understood before publishing.
