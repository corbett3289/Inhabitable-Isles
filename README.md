# Inhabitable Isles - War Sails & Player Settlement

Compatibility and campaign expansion mod for Mount & Blade II: Bannerlord War Sails and Player Settlement.

Current Workshop candidate: v0.9.21 for Bannerlord v1.4.8 and War Sails v1.2.8. The launcher compatibility version intentionally remains v0.9.15 so existing saves do not enter Bannerlord's unreliable module-version mismatch flow.

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
