# Inhabitable Isles 1.5.6 Beta Release Test Matrix

This release targets exactly Bannerlord 1.5.6, War Sails 1.3.6, and Player Settlement 7.5.0. Initial verified Steam build: 26242440. The separate Workshop candidate remains private until the campaign and downloaded-artifact gates pass. Unperformed checks are not passes.

## Static Gate

1. Confirm the Steam download/staging has finished and installed Native/NavalDLC manifests are 1.5.6/1.3.6. Record the build ID; do not silently target a later rolling beta. The modding kit is not required unless asset editing becomes necessary.
2. Run `BuildLocal.ps1` without `-AllowVersionMismatch`. A mismatch must stop the build.
3. Run `VerifyFleetCompatibility.ps1`; all optional-getter and version-rule fixtures must pass.
4. Run `VerifyBeta.ps1` against the explicit local deployment path. It must confirm the compiled DLL has no direct `Hero.get_CanHaveFleet` reference.
5. Resolve every external API and all Harmony target signatures/parameters. The untouched PlayerSettlementFixes Objective token is allowlisted only when the exact-binary caller redirect is verified active. No other unresolved dependency call is acceptable.
6. Confirm main/legacy package hashes are preserved. Confirm description-only cross-links omit `ModuleFolder`. Verify stable module ID, launcher v0.9.15, assembly 0.9.21.0, file 0.9.24.0, and informational 0.9.24-beta.1.

## New Campaign

1. Use standard Bannerlord.exe and a backed-up clean configuration. Enable only the official campaign modules/War Sails, one Harmony distribution (2.4.2.248 tested), ButterLib 2.12.0, UIExtenderEx 3.0.3, MCM 5.12.3, Player Settlement 7.5.0, and this beta. Keep subscribed Workshop manifests readable when isolating versions. Disable other Isles variants and outdated achievement-enabler mods.
2. Run `blwii.compat`. Expect `runtime compatibility: supported`, the `1.5 native-first` profile, `Hero.CanHaveFleet available`, no failed patches, an active Player Settlement AI caller redirect, an attached confirmed-creation listener with no repair failure, and 22 imported surfaces. Any surface-count change requires inspecting the affected islands and documenting a new baseline.
3. Run `blwii.hideouts audit`. Every active hideout must have valid navigation and a visible, clickable marker after discovery.
4. Run `blwii.ferries`. The count should begin at zero and increase only when an eligible NPC needs an island crossing.
5. From sea, test paused/unpaused single- and double-click raw island destinations, including red-marker areas. Disembark, walk on the same island, cross to another island, re-embark, and return to a native port. Accepted clicks must use native time controls, preserve objectives, and avoid stuck transitions. Test remote settlement clicks, native mainland travel, Ctrl+Click parley on settlements and enemy parties, and follow modifiers.
6. Build unfinished/finished Player Settlement village, castle, and town variants through confirmation. Confirm the creation-repair log precedes automatic save/reload, then exercise first hourly/daily ticks, gate/port clicks, settlement menus, grounded siege staging, village trade, and fisherman production. Compare same-island town-bound and castle-bound villages.
7. Let villagers, caravans, convoys, companions, lords, and an army visit and leave. Their objectives must survive embarkation, and visitors must not accumulate inside the holding.
8. Disable **Have Fleet** for a hero-led clan party and confirm Inhabitable Isles does not issue it an emergency ferry. Enable the toggle and confirm an imported-island route can receive ferry support when needed.
9. Allow an enemy raid and siege. Confirm a weak player-built fortification is eligible for enemy scoring/attack, island attackers and village-port army leaders can sail correctly, siege assets remain grounded, and the campaign resumes after battle. Test native ferries and blockades.
10. Discover, attack, clear, and revisit an island Sea Raider hideout. Confirm its patrols can move on land and sea and the 30-day settlement grace behavior remains intact.
11. Zoom and rotate through day, night, and weather changes. Trees must not flash or produce giant bounds. Repeat with `blwii.trees off` and `blwii.trees on`.

## Existing Save

1. Back up the save, then load a separate copy with v0.9.24-beta.1. Never overwrite the original or an ironman campaign during validation.
2. Confirm tracked temporary ferries reconcile without deleting ordinary owned ships.
3. Save while idle on an island, during an NPC crossing, and after a hideout state change; reload each save.
4. Repeat settlement entry/leave, convoy departure, companion recruitment, and island siege checks.
5. On an intentionally mismatched test installation only, confirm a single unsupported-version warning appears and hourly villager processing does not throw `MissingMethodException`. This does not qualify that version for support.

## Workshop and Publication

1. Package only the manifest, ModuleData, DLL, and PDB. Do not include Harmony/Player Settlement binaries, logs, saves, or local uploader tasks.
2. Create the new 1.5.6 item privately and add Harmony/Player Settlement required-item links. Download it, verify every file hash, and repeat critical landing, creation, AI, and save/reload checks using the downloaded artifact.
3. Run several in-game days; require no unhandled exceptions, missing-method errors, stuck parties, or transition loops. A main-menu launch alone is insufficient.
4. Flip the new item public only after all gates pass; tag v0.9.24-beta.1 and cross-link the three versions without changing older content packages. Verify downloaded hashes, public visibility, exact requirements, and required-item links again.

## Logs

Review the newest `rgl_log_*.txt` and crash HTML before release. A caught startup exception whose stack is entirely inside `PlayerSettlementFixes.PartyVisualPatch.FixOnStartup` is external and should remain visible in the report. Any stack containing `BLWarsailsInhabitableIslands` must be fixed or explicitly understood before publishing.
