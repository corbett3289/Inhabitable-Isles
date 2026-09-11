using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    [HarmonyPatch]
    internal static class IslandSettlementLeaveSafetyPatch
    {
        private static readonly HashSet<string> LoggedRecoveries =
            new HashSet<string>(StringComparer.Ordinal);

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(PlayerTownVisitCampaignBehavior),
                "game_menu_settlement_leave_on_consequence");
        }

        [HarmonyPrefix]
        private static bool Prefix()
        {
            MobileParty mainParty = MobileParty.MainParty;
            if (mainParty == null || mainParty.CurrentSettlement != null)
            {
                return true;
            }

            Settlement settlement = ResolveEncounterSettlement();
            if (!IslandSettlementIntegration.IsPlayerSettlement(settlement) ||
                !IslandSettlementIntegration.IsIslandSettlement(
                    settlement,
                    out IslandRecord _))
            {
                return true;
            }

            try
            {
                CampaignVec2 exitPosition = ResolveExitPosition(
                    settlement,
                    mainParty.Position);
                if (exitPosition != null && exitPosition.IsValid())
                {
                    mainParty.Position = exitPosition;
                    foreach (MobileParty attachedParty in mainParty.AttachedParties)
                    {
                        attachedParty.Position = exitPosition;
                    }
                }

                // The party already left; only the encounter/menu state is stale.
                PlayerEncounter.Finish(true);
                mainParty.SetMoveModeHold();
                Campaign.Current?.SaveHandler?.SignalAutoSave();

                if (LoggedRecoveries.Add(settlement.StringId))
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Closed a stale island " +
                        "settlement menu for " + settlement.StringId + ".");
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Stale settlement menu " +
                    "recovery failed for " + settlement.StringId + ": " + ex);

                // Last resort: restore the state expected by Bannerlord's native
                // leave consequence and allow it to perform the normal cleanup.
                mainParty.CurrentSettlement = settlement;
                return true;
            }
        }

        private static Settlement ResolveEncounterSettlement()
        {
            Settlement settlement = PlayerEncounter.EncounterSettlement;
            if (settlement != null)
            {
                return settlement;
            }

            PartyBase encounteredParty = PlayerEncounter.EncounteredParty;
            return encounteredParty?.Settlement;
        }

        private static CampaignVec2 ResolveExitPosition(
            Settlement settlement,
            CampaignVec2 currentPosition)
        {
            if (settlement.GatePosition != null &&
                settlement.GatePosition.IsValid())
            {
                return settlement.GatePosition;
            }

            if (IslandNavMeshOverlay.TryCreateLandPosition(
                    settlement.Position.ToVec2(),
                    out CampaignVec2 landPosition))
            {
                return landPosition;
            }

            return currentPosition;
        }
    }
}
