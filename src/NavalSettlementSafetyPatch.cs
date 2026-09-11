using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NavalDLC;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    [HarmonyPatch]
    internal static class NavalSettlementShipUpgradeSafetyPatch
    {
        private static readonly HashSet<string> LoggedSkips =
            new HashSet<string>(StringComparer.Ordinal);

        private static MethodBase TargetMethod()
        {
            Type behaviorType = AccessTools.TypeByName(
                "NavalDLC.CampaignBehaviors.ShipUpgradeCampaignBehavior");
            return behaviorType == null
                ? null
                : AccessTools.Method(behaviorType, "OnSettlementEntered");
        }

        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty mobileParty,
            Settlement settlement)
        {
            if (mobileParty == null ||
                settlement == null ||
                !mobileParty.IsCaravan ||
                !settlement.HasPort ||
                !settlement.IsTown ||
                !IslandSettlementIntegration.IsPlayerSettlement(settlement) ||
                !IslandSettlementIntegration.IsIslandSettlement(
                    settlement,
                    out IslandRecord _))
            {
                return true;
            }

            string invalidReason = GetInvalidUpgradeReason(
                mobileParty,
                settlement);
            if (invalidReason == null)
            {
                return true;
            }

            string logKey = settlement.StringId + ":" + invalidReason;
            if (LoggedSkips.Add(logKey))
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Skipped War Sails ship " +
                    "upgrades for caravan " + mobileParty.StringId +
                    " at " + settlement.StringId + ": " + invalidReason + ".");
            }

            return false;
        }

        private static string GetInvalidUpgradeReason(
            MobileParty mobileParty,
            Settlement settlement)
        {
            if (mobileParty.ActualClan == null)
            {
                return "the custom caravan has no clan";
            }

            Town town = settlement.Town;
            if (town == null)
            {
                return "the custom town component is missing";
            }

            if (NavalDLCExtensions.GetShipyard(town) == null)
            {
                return "Player Settlement did not create a War Sails shipyard";
            }

            foreach (Ship ship in mobileParty.Ships)
            {
                if (ship == null ||
                    ship.ShipHull == null ||
                    ship.ShipHull.AvailableSlots == null ||
                    ship.Owner == null)
                {
                    return "the caravan contains an incomplete ship record";
                }
            }

            return null;
        }
    }
}
