using System;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;
using SandBox.View.Map;
using SandBox.View.Map.Visuals;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    [HarmonyPatch(typeof(PlayerSettlementBehaviour), "ApplyNow")]
    internal static class PlayerSettlementApplyPatch
    {
        private static void Prefix(PlayerSettlementBehaviour __instance)
        {
            PlayerSettlementPlacementPatch.TryAllowCurrentPlacement(__instance);
        }

        private static void Postfix()
        {
            IslandSettlementIntegration.RepairAll();
            IslandTreeDecorator.RefreshSettlementClearings();
        }
    }

    [HarmonyPatch(typeof(MapScreen), "HandleLeftMouseButtonClick")]
    internal static class PlayerSettlementMapClickPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            MapEntityVisual visualOfSelectedEntity,
            ref CampaignVec2 intersectionPoint)
        {
            PlayerSettlementBehaviour instance = PlayerSettlementBehaviour.Instance;
            if (instance != null &&
                (instance.IsPlacingSettlement || instance.IsPlacingGate))
            {
                if (intersectionPoint == null)
                {
                    return true;
                }

                CampaignVec2 landPoint = intersectionPoint.IsOnLand
                    ? intersectionPoint
                    : new CampaignVec2(intersectionPoint.ToVec2(), true);

                if (IslandZoneSettings.Contains(landPoint.ToVec2()) && landPoint.IsValid())
                {
                    intersectionPoint = landPoint;
                }

                return true;
            }

            if (!(visualOfSelectedEntity is SettlementVisual settlementVisual))
            {
                return true;
            }

            Settlement settlement = settlementVisual.MapEntity?.Settlement;
            MobileParty mainParty = MobileParty.MainParty;
            if (settlement == null ||
                mainParty == null ||
                mainParty.IsTransitionInProgress ||
                !mainParty.HasNavalNavigationCapability ||
                !settlement.HasPort ||
                !IslandSettlementIntegration.IsPlayerSettlement(settlement) ||
                !IslandSettlementIntegration.IsIslandSettlement(
                    settlement,
                    out IslandRecord targetIsland))
            {
                return true;
            }

            bool onSameIsland =
                !mainParty.IsCurrentlyAtSea &&
                IslandNavMeshOverlay.TryGetIsland(
                    mainParty.Position,
                    out IslandRecord currentIsland) &&
                currentIsland.Id == targetIsland.Id;
            if (onSameIsland)
            {
                return true;
            }

            mainParty.SetMoveGoToSettlement(
                settlement,
                mainParty.IsCurrentlyAtSea
                    ? MobileParty.NavigationType.Naval
                    : MobileParty.NavigationType.All,
                true);
            Debug.Print(
                "[BLWarsailsInhabitableIslands] Routed remote map click through port for " +
                settlement.StringId + ".");
            return false;
        }
    }
}
