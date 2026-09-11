using System;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.ObjectSystem;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandVillageLogistics
    {
        private const string FerryHullId = "fishing_ship";
        private const int FerryCrewCapacity = 35;
        private const int MaximumEmergencyFerries = 12;
        private const float HomeReplacementDistance = 1.5f;

        internal static void RepairTradeBounds()
        {
            if (Campaign.Current == null || IslandNavMeshOverlay.Islands.Count == 0)
            {
                return;
            }

            foreach (Settlement settlement in Settlement.All)
            {
                if (!IsManagedIslandVillage(settlement, out Village village, out IslandRecord _))
                {
                    continue;
                }

                Settlement tradeBound = SelectTradeBound(settlement, village);
                if (tradeBound != null && village.TradeBound != tradeBound)
                {
                    village.TradeBound = tradeBound;
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Assigned " +
                        settlement.StringId + " trade to " + tradeBound.StringId + ".");
                }
            }
        }

        internal static void PrepareVillagerFerry(MobileParty party)
        {
            if (party == null ||
                !party.IsActive ||
                !party.IsVillager ||
                party.VillagerPartyComponent?.Village == null ||
                party.MapEvent != null)
            {
                return;
            }

            Village village = party.VillagerPartyComponent.Village;
            Settlement home = village.Settlement;
            if (!IsManagedIslandVillage(home, out Village _, out IslandRecord homeIsland) ||
                !RequiresSeaRoute(homeIsland, village.TradeBound) ||
                !IsAtHome(party, home))
            {
                return;
            }

            EnsureEmergencyFerry(party, "island village trade");
        }

        internal static bool EnsureEmergencyFerry(
            MobileParty party,
            string reason)
        {
            if (party == null ||
                !party.IsActive ||
                party == MobileParty.MainParty ||
                party.MapEvent != null ||
                party.AttachedTo != null ||
                party.IsGarrison ||
                party.IsMilitia ||
                party.IsBandit)
            {
                return false;
            }

            if (party.HasNavalNavigationCapability)
            {
                return true;
            }

            ShipHull hull = MBObjectManager.Instance.GetObject<ShipHull>(FerryHullId);
            if (hull == null)
            {
                return false;
            }

            int troopCount = Math.Max(1, party.MemberRoster.TotalManCount);
            int requiredFerries = Math.Max(
                1,
                (troopCount + FerryCrewCapacity - 1) / FerryCrewCapacity);
            int maximumAdditions = Math.Min(
                MaximumEmergencyFerries,
                requiredFerries + 1);
            int added = 0;
            while (!party.HasNavalNavigationCapability &&
                   added < maximumAdditions)
            {
                try
                {
                    Ship ferry = new Ship(hull)
                    {
                        IsTradeable = false,
                        IsInvulnerable = false
                    };
                    ChangeShipOwnerAction.ApplyByMobilePartyCreation(
                        party.Party,
                        ferry);
                    added++;
                }
                catch (Exception ex)
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Could not issue an " +
                        "emergency island ferry to " + party.StringId +
                        ": " + ex);
                    break;
                }
            }

            if (added > 0)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Issued " +
                    added + " emergency island ferry ship(s) to " +
                    party.StringId + " for " + reason + ".");
            }

            return party.HasNavalNavigationCapability;
        }

        private static Settlement SelectTradeBound(
            Settlement villageSettlement,
            Village village)
        {
            Settlement selectedBound = village.Bound;
            if (IsSuitableTradeTown(villageSettlement, selectedBound))
            {
                return selectedBound;
            }

            if (selectedBound != null && selectedBound.IsCastle)
            {
                Settlement castlePort = FindNearestFriendlyPortTown(
                    villageSettlement,
                    villageSettlement.MapFaction);
                if (castlePort != null)
                {
                    return castlePort;
                }
            }

            if (IsSuitableTradeTown(villageSettlement, village.TradeBound))
            {
                return village.TradeBound;
            }

            return FindNearestFriendlyPortTown(
                villageSettlement,
                villageSettlement.MapFaction);
        }

        private static Settlement FindNearestFriendlyPortTown(
            Settlement village,
            IFaction faction)
        {
            Settlement nearest = null;
            float nearestDistanceSquared = float.MaxValue;
            foreach (Settlement candidate in Settlement.All)
            {
                if (candidate == null ||
                    !candidate.IsActive ||
                    !candidate.IsTown ||
                    !candidate.HasPort ||
                    !IsFriendly(faction, candidate.MapFaction))
                {
                    continue;
                }

                Vec2 delta = candidate.Position.ToVec2() - village.Position.ToVec2();
                float distanceSquared = delta.LengthSquared;
                if (distanceSquared >= nearestDistanceSquared)
                {
                    continue;
                }

                nearest = candidate;
                nearestDistanceSquared = distanceSquared;
            }

            return nearest;
        }

        private static bool IsSuitableTradeTown(
            Settlement village,
            Settlement candidate)
        {
            if (candidate == null ||
                !candidate.IsActive ||
                !candidate.IsTown ||
                !IsFriendly(village.MapFaction, candidate.MapFaction))
            {
                return false;
            }

            bool sameIsland =
                IslandNavMeshOverlay.TryGetIsland(
                    village.Position,
                    out IslandRecord villageIsland) &&
                IslandNavMeshOverlay.TryGetIsland(
                    candidate.Position,
                    out IslandRecord townIsland) &&
                villageIsland.Id == townIsland.Id;
            return sameIsland || candidate.HasPort;
        }

        private static bool IsManagedIslandVillage(
            Settlement settlement,
            out Village village,
            out IslandRecord island)
        {
            village = settlement?.Village;
            island = null;
            return village != null &&
                   settlement.IsVillage &&
                   IslandSettlementIntegration.IsPlayerSettlement(settlement) &&
                   IslandNavMeshOverlay.TryGetIsland(settlement.Position, out island);
        }

        private static bool RequiresSeaRoute(
            IslandRecord homeIsland,
            Settlement tradeBound)
        {
            if (homeIsland == null || tradeBound == null || !tradeBound.IsTown)
            {
                return false;
            }

            return !IslandNavMeshOverlay.TryGetIsland(
                       tradeBound.Position,
                       out IslandRecord tradeIsland) ||
                   tradeIsland.Id != homeIsland.Id;
        }

        private static bool IsAtHome(MobileParty party, Settlement home)
        {
            if (party.CurrentSettlement == home)
            {
                return true;
            }

            Vec2 delta = party.Position.ToVec2() - home.Position.ToVec2();
            return delta.LengthSquared <=
                   HomeReplacementDistance * HomeReplacementDistance;
        }

        private static bool IsFriendly(IFaction left, IFaction right)
        {
            return left != null && right != null && left == right;
        }
    }
}
