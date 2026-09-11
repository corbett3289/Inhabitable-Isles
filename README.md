# Inhabitable Isles - War Sails & Player Settlement

Compatibility and campaign expansion mod for Mount & Blade II: Bannerlord War Sails and Player Settlement.

[Current Steam Workshop release](https://steamcommunity.com/sharedfiles/filedetails/?id=3767263102) | [Deprecated 1.4.8 Workshop release](https://steamcommunity.com/sharedfiles/filedetails/?id=3799429144) | [Source](https://github.com/corbett3289/Inhabitable-Isles) | [Bannerlord 1.4.8 source](https://github.com/corbett3289/Inhabitable-Isles/tree/bannerlord-1.4.8)

Current release candidate: v0.9.23 for Bannerlord v1.5.2 and War Sails v1.3.2. The launcher compatibility version intentionally remains v0.9.15 so existing saves do not enter Bannerlord's unreliable module-version mismatch flow.

v0.9.23 removes the direct dependency on Bannerlord 1.5's `Hero.CanHaveFleet` getter that caused an hourly villager crash when a mismatched 1.4.8 installation loaded the newer DLL. Fleet policy is now resolved through a validated optional accessor, runtime versions come from Bannerlord module metadata instead of uninformative DLL versions, and `blwii.compat` reports runtime support plus fleet-policy availability. Unsupported or unknown game/DLC combinations receive one campaign warning. Official support remains Bannerlord 1.5.2 with War Sails 1.3.2.

v0.9.22 uses Bannerlord 1.5 and War Sails 1.3 behavior first for ordinary sailing, campaign-map audio, coastal settlement travel, and native convoy handling. Inhabitable Isles intervenes only for dynamically imported island terrain and Player Settlement holdings that the native map cannot know about. Temporary NPC ferries are tracked in save data and retired after their island crossing is safely complete. The native **Have Fleet** setting is authoritative for hero-led clan parties, so the mod will not issue an emergency ferry when that toggle is off. Invalid pirate hideout candidates are deactivated and replaced, generated tree entities are rejected when their map bounds are malformed or wildly oversized, and `blwii.compat`, `blwii.ferries`, and `blwii.hideouts audit` expose release diagnostics.

v0.9.21 recompiles the mod against the current game and War Sails assemblies and isolates Harmony patch registration so one changed optional callback cannot prevent the rest of the mod from loading. Internal embarkation remains preferred, with validated public sailing and disembarkation fallbacks plus a bounded transition watchdog. Imported terrain handling now activates only after island navmesh registration succeeds, and generated tree resources are resolved once per map session. Use `blwii.compat` to report patch status, transition support, imported island surfaces, and active tree state.

Bannerlord v1.4.8 significantly improves authored Settlement Distance Cache generation and error reporting. Inhabitable Isles intentionally retains its runtime navigation and settlement integration because its island surfaces, Player Settlement holdings, ports, and parties are created or repaired after the authored `Main_map` cache has loaded.

v0.9.20 safely closes stale Player Settlement menus on managed islands when another transition has already cleared the player's current-settlement state. This prevents Bannerlord's native Leave consequence from dereferencing a missing settlement while preserving the normal map position, hold state, encounter cleanup, and autosave behavior.

v0.9.19 removes a redundant native scene-height probe from Sea Raider shoreline patrol routing. Naval route validation now relies on the campaign naval navmesh, preventing the reported protected-memory crash during hourly party ticks without changing save data.

v0.9.18 prevents War Sails' caravan ship-upgrade callback from dereferencing a missing shipyard when a caravan or convoy enters an island town created by Player Settlement. The guard is limited to managed Player Settlement island ports with incomplete naval building data and adds no serialized save fields.

The mod detects complete raised island terrain in its configured sea zones and adds fitted land navigation surfaces. The player and naval-capable NPC parties can embark, disembark, travel between islands, and retain their original campaign objective after crossing.

Player Settlement towns, castles, and villages built on these islands receive valid coastal ports. Island fortifications are exposed to Bannerlord's AI target scoring, and siege camp positions are kept on island land so naval-capable enemy armies can attack them.

Remote island settlement clicks target their ports automatically while same-island travel continues over land. Player Settlement island villages retain a suitable trade town and cross-water villager parties receive a non-tradeable fishing vessel at home so they can visibly sail, dock, deliver goods, and return.

Four Sea Raider hideouts are allocated from eight candidate islands. Each active hideout supports three roaming bands across its island and nearby waters, plus native hideout defenders. Hideout markers and gates are grounded on raised island terrain and are discovered when the player reaches the island or its nearby waters. Clearing a hideout starts a 30-day settlement grace period. Building a Player Settlement suppresses the hideout while that settlement remains.

Island pirates can be disabled per save from Bannerlord's developer console with `blwii.pirates off`. Use `blwii.pirates on` to restore them or `blwii.pirates status` to check the current setting and active/visible hideout counts. Disabling them removes managed island hideouts and parties without changing native War Sails pirates. Re-enabling them restores the saved active hideouts immediately, including within the same play session.

Sparse, deterministic, non-blocking tree clusters decorate suitable islands. Trees avoid shore approaches and settlement space and are removed around newly built settlements.

Generated foliage now refreshes its campaign-map visibility mask and bounds after placement. If a GPU or graphics configuration still produces flashing, use `blwii.trees off` to remove generated trees for the current game session. Use `blwii.trees on` or `blwii.trees status` to restore or inspect them.

Existing saves are migrated without duplicating managed hideouts, parties, or villager ferries. General NPCs still require native naval capability and ships to cross; qualifying island villagers receive a dedicated trade ferry.

Emergency visitor recovery gives newly arrived parties time to finish native settlement behavior, releases congested traffic gradually, and pauses while the player is inside the affected settlement. This reduces callback collisions with other campaign mods while still clearing stranded caravans and companion parties.

Edit `Module/ModuleData/IslandZones.xml` to tune island search bounds.

Load order: War Sails, Harmony, Player Settlement, then Inhabitable Isles.

This is a separate module and does not edit Player Settlement's files.

## Version branches

- `main` is the active v0.9.23 line for Bannerlord 1.5.2 and War Sails 1.3.2.
- `bannerlord-1.4.8` preserves the historical v0.9.21 source for Bannerlord 1.4.8 and War Sails 1.2.8. Its [standalone Workshop package](https://steamcommunity.com/sharedfiles/filedetails/?id=3799429144) is deprecated, frozen, and unsupported.

The module manifest intentionally remains at launcher version v0.9.15 for save compatibility. The current DLL file and informational versions are v0.9.23; the assembly identity remains v0.9.21.0.

## Installation

The supported player installation is the [current Steam Workshop release](https://steamcommunity.com/sharedfiles/filedetails/?id=3767263102). Enable War Sails, Harmony, Player Settlement, and then Inhabitable Isles in that order. Back up an existing save before changing game, DLC, or mod versions. Do not subscribe to or enable both the current and legacy Workshop items; they use the same module identity and target different game APIs.

This repository contains source and module data. Generated DLL and PDB files are deliberately not committed.

## Building from source

The default build expects:

- Bannerlord 1.5.2 and War Sails 1.3.2 in the standard Steam location.
- Player Settlement 7.5.0 in its standard Workshop location.
- .NET Framework 4.7.1 reference assemblies.
- The Visual Studio 2022 Community Roslyn C# compiler in its default location.

From PowerShell, run:

```powershell
.\BuildLocal.ps1
.\VerifyFleetCompatibility.ps1
.\VerifyBeta.ps1
```

`BuildLocal.ps1` accepts `-GameRoot` and `-PlayerSettlementRoot` when those dependencies are installed elsewhere. It writes the compiled module to `Module\bin\Win64_Shipping_Client`. Local deployment requires both `-Deploy` and an explicit `-DeployRoot`; the script never selects a Workshop destination automatically.

## Reporting issues

Use [GitHub Issues](https://github.com/corbett3289/Inhabitable-Isles/issues) or the Steam Workshop comments. Include the output from `blwii.compat`, the nearby island or settlement, whether the affected party was on land or at sea, exact reproduction steps, and the crash identifier or relevant log excerpt when applicable. Do not upload save files or full crash archives unless specifically requested.
