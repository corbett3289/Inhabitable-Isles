using System;
using System.Collections.Generic;
using System.Linq;
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
        private static Dictionary<string, int> _temporaryFerryCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoggedFleetPolicyBlocks =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly OptionalBooleanPropertyAccessor FleetPolicyAccessor =
            new OptionalBooleanPropertyAccessor(typeof(Hero), "CanHaveFleet");
        private static bool _loggedFleetPolicyFailure;

        internal static void AttachTemporaryFerryLedger(
            Dictionary<string, int> temporaryFerryCounts)
        {
            _temporaryFerryCounts = temporaryFerryCounts ??
                new Dictionary<string, int>(StringComparer.Ordinal);
        }

        internal static int GetTrackedFerryCount()
        {
            return _temporaryFerryCounts?.Values.Sum() ?? 0;
        }

        internal static string GetTemporaryFerryStatus()
        {
            int partyCount = _temporaryFerryCounts?.Count ?? 0;
            return "Inhabitable Isles tracks " + GetTrackedFerryCount() +
                   " temporary ferry ship(s) across " + partyCount +
                   " party record(s).";
        }

        internal static string GetFleetPolicyStatus()
        {
            return "Hero.CanHaveFleet " + FleetPolicyAccessor.Availability;
        }

        internal static void ReconcileTemporaryFerries()
        {
            if (_temporaryFerryCounts == null ||
                _temporaryFerryCounts.Count == 0 ||
                Campaign.Current == null)
            {
                return;
            }

            Dictionary<string, MobileParty> parties = MobileParty.All
                .Where(party => party?.StringId != null)
                .GroupBy(party => party.StringId)
                .ToDictionary(group => group.Key, group => group.First());
            foreach (string partyId in _temporaryFerryCounts.Keys.ToList())
            {
                if (!parties.TryGetValue(partyId, out MobileParty party) ||
                    party == null ||
                    !party.IsActive)
                {
                    _temporaryFerryCounts.Remove(partyId);
                    continue;
                }

                int ownedTemporaryCandidates = party.Ships.Count(IsTemporaryFerryCandidate);
                int tracked = Math.Max(0, _temporaryFerryCounts[partyId]);
                int reconciled = Math.Min(tracked, ownedTemporaryCandidates);
                if (reconciled == 0)
                {
                    _temporaryFerryCounts.Remove(partyId);
                }
                else
                {
                    _temporaryFerryCounts[partyId] = reconciled;
                }
            }
        }

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

            Hero leader = party.LeaderHero;
            bool canHaveFleet = true;
            if (leader != null &&
                !FleetPolicyAccessor.TryGetValue(
                    leader,
                    out canHaveFleet,
                    out string fleetPolicyFailure))
            {
                LogFleetPolicyFailure(fleetPolicyFailure, party, reason);
                return false;
            }

            if (leader != null && !canHaveFleet)
            {
                string partyId = party.StringId ?? "<unnamed party>";
                if (LoggedFleetPolicyBlocks.Add(partyId))
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Did not issue an " +
                        "emergency island ferry to " + partyId + " for " +
                        reason + " because its native clan fleet toggle is off.");
                }

                return false;
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
                TrackTemporaryFerries(party, added);
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Issued " +
                    added + " emergency island ferry ship(s) to " +
                    party.StringId + " for " + reason + ".");
            }

            return party.HasNavalNavigationCapability;
        }

        private static void LogFleetPolicyFailure(
            string failureReason,
            MobileParty party,
            string ferryReason)
        {
            if (_loggedFleetPolicyFailure)
            {
                return;
            }

            _loggedFleetPolicyFailure = true;
            Debug.Print(
                "[BLWarsailsInhabitableIslands] Could not read the native " +
                "fleet policy, so no emergency ferry was issued to " +
                (party?.StringId ?? "<unnamed party>") + " for " +
                ferryReason + ": " + failureReason);
        }

        internal static void RetireTemporaryFerriesIfSafe(MobileParty party)
        {
            string partyId = party?.StringId;
            if (partyId == null ||
                _temporaryFerryCounts == null ||
                !_temporaryFerryCounts.TryGetValue(partyId, out int tracked))
            {
                return;
            }

            if (!party.IsActive)
            {
                _temporaryFerryCounts.Remove(partyId);
                return;
            }

            if (party.IsCurrentlyAtSea ||
                party.IsTransitionInProgress ||
                party.MapEvent != null ||
                IslandTravelController.IsManagedParty(party))
            {
                return;
            }

            List<Ship> candidates = party.Ships
                .Where(IsTemporaryFerryCandidate)
                .Take(Math.Max(0, tracked))
                .ToList();
            int removed = 0;
            foreach (Ship ferry in candidates)
            {
                try
                {
                    DestroyShipAction.ApplyByDiscard(ferry);
                    removed++;
                }
                catch (Exception ex)
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Could not retire a " +
                        "temporary ferry from " + partyId + ": " + ex);
                    break;
                }
            }

            int remaining = Math.Max(0, tracked - removed);
            if (remaining == 0)
            {
                _temporaryFerryCounts.Remove(partyId);
            }
            else
            {
                _temporaryFerryCounts[partyId] = remaining;
            }

            if (removed > 0)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Retired " + removed +
                    " temporary ferry ship(s) from " + partyId + ".");
            }
        }

        private static void TrackTemporaryFerries(MobileParty party, int added)
        {
            if (party?.StringId == null || added <= 0)
            {
                return;
            }

            _temporaryFerryCounts.TryGetValue(party.StringId, out int existing);
            _temporaryFerryCounts[party.StringId] = existing + added;
        }

        private static bool IsTemporaryFerryCandidate(Ship ship)
        {
            return ship != null &&
                   !ship.IsTradeable &&
                   ship.ShipHull?.StringId == FerryHullId;
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
