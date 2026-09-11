using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Helpers;
using NavalDLC.GameComponents;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandTravelController
    {
        private const float ArrivalDistance = 0.35f;
        private const float MinimumVisitorResidenceHours = 1f;
        private const float MaximumIdleVisitorHours = 6f;
        private const float VisitorReleaseIntervalHours = 0.25f;
        private const int CongestedVisitorCount = 6;
        private const float MaintenanceIntervalSeconds = 0.25f;
        private const float TransitionTimeoutSeconds = 20f;

        private static readonly MethodInfo InitializeNavigationTransitionMethod =
            ResolveInitializeNavigationTransitionMethod();

        private static readonly Dictionary<MobileParty, TravelContext> ActiveTravel =
            new Dictionary<MobileParty, TravelContext>();
        private static readonly HashSet<string> LoggedVisitorRecoveries =
            new HashSet<string>();
        private static readonly HashSet<string> LoggedTransitionFallbacks =
            new HashSet<string>();
        private static readonly Dictionary<MobileParty, CampaignTime>
            VisitorFirstSeen =
                new Dictionary<MobileParty, CampaignTime>();
        private static readonly Dictionary<Settlement, CampaignTime>
            NextVisitorReleaseTime =
                new Dictionary<Settlement, CampaignTime>();

        private static bool _bypassMovePatch;
        private static bool _allowTransitionCancellation;
        private static bool _visitorSweepScheduled;
        private static CampaignTime _nextVisitorSweepTime = CampaignTime.Zero;
        private static float _maintenanceElapsedSeconds;

        internal static bool SupportsAnimatedTransitions =>
            InitializeNavigationTransitionMethod != null;

        internal static bool TryHandleMove(
            MobileParty mobileParty,
            CampaignVec2 point,
            MobileParty.NavigationType navigationType,
            AiBehavior finalBehavior = AiBehavior.GoToPoint,
            Settlement finalSettlement = null,
            MobileParty finalParty = null,
            bool isTargetingPort = false)
        {
            if (_bypassMovePatch ||
                mobileParty == null ||
                !IsCampaignMapReady() ||
                mobileParty.AttachedTo != null ||
                mobileParty.IsTransitionInProgress ||
                !IsValidPosition(point))
            {
                return false;
            }

            IslandRecord currentIsland = null;
            IslandRecord targetIsland = null;
            CampaignVec2 resolvedTarget = point;
            bool currentSettlementOnIsland =
                !mobileParty.IsCurrentlyAtSea &&
                mobileParty.CurrentSettlement != null &&
                IslandNavMeshOverlay.TryGetIsland(
                    mobileParty.CurrentSettlement.Position,
                    out currentIsland);
            bool startsOnIsland =
                !mobileParty.IsCurrentlyAtSea &&
                (currentSettlementOnIsland ||
                 IslandNavMeshOverlay.TryGetIsland(
                     mobileParty.Position,
                     out currentIsland));
            bool targetsIsland =
                IslandNavMeshOverlay.TryGetIsland(point, out targetIsland) &&
                (IslandNavMeshOverlay.IsImportedLandPosition(point) ||
                 IslandNavMeshOverlay.TryCreateLandPosition(
                     point.ToVec2(),
                     out resolvedTarget));
            if (targetsIsland &&
                !IslandNavMeshOverlay.IsImportedLandPosition(point))
            {
                point = resolvedTarget;
            }

            if (startsOnIsland && targetsIsland && currentIsland.Id == targetIsland.Id)
            {
                ActiveTravel.Remove(mobileParty);
                return false;
            }

            if (!startsOnIsland && !targetsIsland)
            {
                ActiveTravel.Remove(mobileParty);
                return false;
            }

            bool crossesWaterFromIsland =
                startsOnIsland &&
                (!targetsIsland || currentIsland.Id != targetIsland.Id);
            if (crossesWaterFromIsland &&
                mobileParty != MobileParty.MainParty &&
                !mobileParty.HasNavalNavigationCapability &&
                !IslandVillageLogistics.EnsureEmergencyFerry(
                    mobileParty,
                    "island departure"))
            {
                ActiveTravel.Remove(mobileParty);
                return false;
            }

            if (startsOnIsland && mobileParty.CurrentSettlement != null)
            {
                LeaveSettlementAction.ApplyForParty(mobileParty);
                if (mobileParty.CurrentSettlement != null)
                {
                    ActiveTravel.Remove(mobileParty);
                    return false;
                }
            }

            if (!mobileParty.IsCurrentlyAtSea &&
                !mobileParty.HasNavalNavigationCapability &&
                mobileParty != MobileParty.MainParty)
            {
                return false;
            }

            if (ActiveTravel.TryGetValue(mobileParty, out TravelContext existing) &&
                ((finalParty != null && existing.FinalParty == finalParty) ||
                 (finalSettlement != null &&
                  existing.FinalSettlement == finalSettlement) ||
                 (IsValidPosition(existing.FinalTarget) &&
                   DistanceSquared(existing.FinalTarget, point) <= 0.25f)))
            {
                existing.FinalTarget = point;
                existing.FinalNavigationType = navigationType;
                existing.FinalBehavior = finalBehavior;
                existing.FinalSettlement = finalSettlement;
                existing.FinalParty = finalParty;
                existing.IsTargetingPort = isTargetingPort;
                return true;
            }

            TravelContext context = new TravelContext
            {
                Party = mobileParty,
                FinalTarget = point,
                FinalNavigationType = navigationType,
                FinalBehavior = finalBehavior,
                FinalSettlement = finalSettlement,
                FinalParty = finalParty,
                IsTargetingPort = isTargetingPort,
                TargetIslandId = targetsIsland ? targetIsland.Id : null
            };
            ActiveTravel[mobileParty] = context;

            if (startsOnIsland)
            {
                return BeginIslandDeparture(context, currentIsland);
            }

            if (targetsIsland)
            {
                return BeginIslandApproach(context, targetIsland);
            }

            ActiveTravel.Remove(mobileParty);
            return false;
        }

        internal static bool TryReleaseStuckIslandVisitor(
            MobileParty party,
            bool forceIdleRelease = false)
        {
            Settlement currentSettlement = party?.CurrentSettlement;
            if (party == null ||
                !party.IsActive ||
                party == MobileParty.MainParty ||
                party.MapEvent != null ||
                party.AttachedTo != null ||
                party.IsGarrison ||
                party.IsMilitia ||
                party.IsBandit ||
                currentSettlement == null ||
                !IslandSettlementIntegration.IsPlayerSettlement(currentSettlement) ||
                !IslandNavMeshOverlay.TryGetIsland(
                    currentSettlement.Position,
                    out IslandRecord currentIsland))
            {
                if (party != null)
                {
                    VisitorFirstSeen.Remove(party);
                }

                return false;
            }

            if (!VisitorFirstSeen.TryGetValue(
                    party,
                    out CampaignTime firstSeen))
            {
                firstSeen = CampaignTime.Now;
                VisitorFirstSeen[party] = firstSeen;
            }

            if (firstSeen.ElapsedHoursUntilNow < MinimumVisitorResidenceHours ||
                MobileParty.MainParty?.CurrentSettlement == currentSettlement ||
                (NextVisitorReleaseTime.TryGetValue(
                     currentSettlement,
                     out CampaignTime nextReleaseTime) &&
                 !nextReleaseTime.IsPast))
            {
                return false;
            }

            Settlement targetSettlement = party.TargetSettlement;
            MobileParty targetParty = party.TargetParty;
            bool hasExternalSettlementTarget =
                targetSettlement != null &&
                targetSettlement != currentSettlement;
            bool hasExternalPartyTarget =
                targetParty != null &&
                targetParty.CurrentSettlement != currentSettlement;
            bool hasStalePointOrder =
                targetSettlement == null &&
                targetParty == null &&
                party.DefaultBehavior != AiBehavior.Hold &&
                party.DefaultBehavior != AiBehavior.None &&
                IsValidPosition(party.MoveTargetPoint) &&
                DistanceSquared(
                    party.MoveTargetPoint,
                    currentSettlement.Position) > 0.25f;
            bool usesIdleFallback =
                !hasExternalSettlementTarget &&
                !hasExternalPartyTarget &&
                !hasStalePointOrder;
            if (usesIdleFallback)
            {
                if (!forceIdleRelease &&
                    firstSeen.ElapsedHoursUntilNow < MaximumIdleVisitorHours)
                {
                    return false;
                }

                targetSettlement = FindEmergencyExitDestination(
                    party,
                    currentSettlement,
                    currentIsland);
                if (targetSettlement == null)
                {
                    return false;
                }

                hasExternalSettlementTarget = true;
            }

            AiBehavior behavior = party.DefaultBehavior;
            MobileParty.NavigationType navigationType =
                party.DesiredAiNavigationType;
            bool isTargetingPort = party.IsTargetingPort;
            CampaignVec2 target = party.MoveTargetPoint;
            if (hasExternalSettlementTarget)
            {
                if (usesIdleFallback)
                {
                    behavior = AiBehavior.GoToSettlement;
                    navigationType = MobileParty.NavigationType.All;
                    isTargetingPort =
                        targetSettlement.HasPort &&
                        targetSettlement.PortPosition != null &&
                        targetSettlement.PortPosition.IsValid();
                }

                target = isTargetingPort
                    ? targetSettlement.PortPosition
                    : targetSettlement.GatePosition;
            }
            else if (hasExternalPartyTarget)
            {
                target = targetParty.Position;
            }

            bool targetIsOnSameIsland =
                IslandNavMeshOverlay.TryGetIsland(
                    target,
                    out IslandRecord targetIsland) &&
                targetIsland.Id == currentIsland.Id;
            if (!targetIsOnSameIsland &&
                !party.HasNavalNavigationCapability &&
                !IslandVillageLogistics.EnsureEmergencyFerry(
                    party,
                    "trapped visitor recovery"))
            {
                return false;
            }

            NextVisitorReleaseTime[currentSettlement] =
                CampaignTime.HoursFromNow(VisitorReleaseIntervalHours);
            LeaveSettlementAction.ApplyForParty(party);
            if (party.CurrentSettlement != null)
            {
                return false;
            }

            VisitorFirstSeen.Remove(party);
            if (LoggedVisitorRecoveries.Add(party.StringId ?? SafePartyName(party)))
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Released trapped visitor " +
                    SafePartyName(party) + " from " +
                    currentSettlement.StringId + ".");
            }

            if (targetIsOnSameIsland)
            {
                ExecuteMove(
                    party,
                    target,
                    MobileParty.NavigationType.Default);
                return true;
            }

            return TryHandleMove(
                party,
                target,
                navigationType,
                behavior,
                targetSettlement,
                targetParty,
                isTargetingPort);
        }

        internal static void Tick(float deltaSeconds)
        {
            if (!IsCampaignMapReady())
            {
                return;
            }

            _maintenanceElapsedSeconds += Math.Max(0f, deltaSeconds);
            if (_maintenanceElapsedSeconds >= MaintenanceIntervalSeconds)
            {
                _maintenanceElapsedSeconds = 0f;
                if (!_visitorSweepScheduled || _nextVisitorSweepTime.IsPast)
                {
                    SweepStuckIslandVisitors();
                    _visitorSweepScheduled = true;
                    _nextVisitorSweepTime = CampaignTime.HoursFromNow(0.25f);
                }

                TryRecoverPartyRoute(MobileParty.MainParty);
            }

            if (ActiveTravel.Count == 0)
            {
                return;
            }

            List<MobileParty> parties = new List<MobileParty>(ActiveTravel.Keys);
            foreach (MobileParty party in parties)
            {
                if (!ActiveTravel.TryGetValue(party, out TravelContext context))
                {
                    continue;
                }

                try
                {
                    Tick(context, deltaSeconds);
                }
                catch (Exception ex)
                {
                    ActiveTravel.Remove(party);
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Island travel failed for " +
                        SafePartyName(party) + ": " + ex);
                }
            }
        }

        internal static void Reset()
        {
            ActiveTravel.Clear();
            VisitorFirstSeen.Clear();
            NextVisitorReleaseTime.Clear();
            LoggedTransitionFallbacks.Clear();
            _visitorSweepScheduled = false;
            _nextVisitorSweepTime = CampaignTime.Zero;
            _maintenanceElapsedSeconds = 0f;
        }

        internal static bool IsManagedParty(MobileParty party)
        {
            if (party == null)
            {
                return false;
            }

            MobileParty root = GetTransitionRoot(party);
            return root != null && ActiveTravel.ContainsKey(root);
        }

        internal static void TryRecoverPartyRoute(MobileParty party)
        {
            if (party == null ||
                !IsCampaignMapReady() ||
                !party.IsActive ||
                party.AttachedTo != null ||
                party.IsTransitionInProgress ||
                ActiveTravel.ContainsKey(party) ||
                party.DefaultBehavior == AiBehavior.Hold)
            {
                return;
            }

            CampaignVec2 target = party.MoveTargetPoint;
            Settlement settlement = party.TargetSettlement;
            if (settlement != null)
            {
                target = party.IsTargetingPort
                    ? settlement.PortPosition
                    : settlement.GatePosition;
            }
            else if (party.TargetParty != null)
            {
                target = party.TargetParty.Position;
            }

            if (!IsValidPosition(target))
            {
                return;
            }

            TryHandleMove(
                party,
                target,
                party.DesiredAiNavigationType,
                party.DefaultBehavior,
                settlement,
                party.TargetParty,
                party.IsTargetingPort);
        }

        internal static bool ShouldPreserveNavigationTransition(MobileParty mobileParty)
        {
            if (_allowTransitionCancellation ||
                mobileParty == null ||
                !mobileParty.IsTransitionInProgress)
            {
                return false;
            }

            MobileParty root = GetTransitionRoot(mobileParty);
            if (root == null ||
                !ActiveTravel.TryGetValue(root, out TravelContext context) ||
                (context.State != TravelState.Disembarking &&
                 context.State != TravelState.Embarking))
            {
                return false;
            }

            if (!context.LoggedPreservedCancellation)
            {
                context.LoggedPreservedCancellation = true;
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Preserved an active island transition for " +
                    SafePartyName(root) + ".");
            }

            return true;
        }

        internal static bool TryGetIslandDistance(
            MobileParty mobileParty,
            IslandRecord island,
            out float distance)
        {
            distance = float.MaxValue;
            if (mobileParty == null ||
                island == null ||
                (!mobileParty.HasNavalNavigationCapability &&
                 !IslandNavMeshOverlay.TryGetIsland(mobileParty.Position, out _)))
            {
                return false;
            }

            Vec2 start = mobileParty.Position.ToVec2();
            Vec2 delta = island.Center - start;
            float direct = (float)Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            distance = direct + Math.Max(1f, island.Length + island.Width) * 0.25f;
            return true;
        }

        private static void SweepStuckIslandVisitors()
        {
            List<MobileParty> parties = new List<MobileParty>(MobileParty.All);
            Dictionary<Settlement, int> visitorCounts =
                new Dictionary<Settlement, int>();

            foreach (MobileParty party in parties)
            {
                if (!IsRecoverableIslandVisitor(
                        party,
                        out Settlement currentSettlement,
                        out IslandRecord _))
                {
                    VisitorFirstSeen.Remove(party);
                    continue;
                }

                visitorCounts.TryGetValue(
                    currentSettlement,
                    out int count);
                visitorCounts[currentSettlement] = count + 1;
            }

            foreach (MobileParty party in parties)
            {
                if (!IsRecoverableIslandVisitor(
                        party,
                        out Settlement currentSettlement,
                        out IslandRecord _))
                {
                    continue;
                }

                bool isPlayerClanParty =
                    party.LeaderHero?.Clan == Clan.PlayerClan;
                bool isCongested =
                    visitorCounts.TryGetValue(
                        currentSettlement,
                        out int count) &&
                    count >= CongestedVisitorCount;
                TryReleaseStuckIslandVisitor(
                    party,
                    isPlayerClanParty || isCongested);
            }
        }

        private static bool IsRecoverableIslandVisitor(
            MobileParty party,
            out Settlement currentSettlement,
            out IslandRecord island)
        {
            currentSettlement = party?.CurrentSettlement;
            island = null;
            return party != null &&
                   party.IsActive &&
                   party != MobileParty.MainParty &&
                   party.MapEvent == null &&
                   party.AttachedTo == null &&
                   !party.IsGarrison &&
                   !party.IsMilitia &&
                   !party.IsBandit &&
                   currentSettlement != null &&
                   IslandSettlementIntegration.IsPlayerSettlement(
                       currentSettlement) &&
                   IslandNavMeshOverlay.TryGetIsland(
                       currentSettlement.Position,
                       out island);
        }

        private static Settlement FindEmergencyExitDestination(
            MobileParty party,
            Settlement currentSettlement,
            IslandRecord currentIsland)
        {
            Settlement home = party.HomeSettlement;
            if (IsSuitableExitDestination(
                    party,
                    home,
                    currentSettlement,
                    currentIsland))
            {
                return home;
            }

            Settlement nearestPort = null;
            Settlement nearestTown = null;
            float nearestPortDistanceSquared = float.MaxValue;
            float nearestTownDistanceSquared = float.MaxValue;
            Vec2 source = currentSettlement.Position.ToVec2();
            foreach (Settlement candidate in Settlement.All)
            {
                if (!IsSuitableExitDestination(
                        party,
                        candidate,
                        currentSettlement,
                        currentIsland))
                {
                    continue;
                }

                Vec2 delta = candidate.Position.ToVec2() - source;
                float distanceSquared = delta.LengthSquared;
                if (candidate.HasPort &&
                    candidate.PortPosition != null &&
                    candidate.PortPosition.IsValid() &&
                    distanceSquared < nearestPortDistanceSquared)
                {
                    nearestPort = candidate;
                    nearestPortDistanceSquared = distanceSquared;
                }

                if (distanceSquared < nearestTownDistanceSquared)
                {
                    nearestTown = candidate;
                    nearestTownDistanceSquared = distanceSquared;
                }
            }

            return nearestPort ?? nearestTown;
        }

        private static bool IsSuitableExitDestination(
            MobileParty party,
            Settlement candidate,
            Settlement currentSettlement,
            IslandRecord currentIsland)
        {
            if (candidate == null ||
                candidate == currentSettlement ||
                !candidate.IsActive ||
                !candidate.IsTown ||
                candidate.GatePosition == null ||
                !candidate.GatePosition.IsValid())
            {
                return false;
            }

            if (IslandNavMeshOverlay.TryGetIsland(
                    candidate.Position,
                    out IslandRecord candidateIsland) &&
                candidateIsland.Id == currentIsland.Id)
            {
                return false;
            }

            IFaction partyFaction = party.MapFaction;
            IFaction candidateFaction = candidate.MapFaction;
            return partyFaction == null ||
                   candidateFaction == null ||
                   !FactionManager.IsAtWarAgainstFaction(
                       partyFaction,
                       candidateFaction);
        }

        private static bool BeginIslandApproach(
            TravelContext context,
            IslandRecord targetIsland)
        {
            MobileParty party = context.Party;
            if (!IslandNavMeshOverlay.TryGetShoreRoute(
                    targetIsland,
                    party.Position.ToVec2(),
                    out CampaignVec2 seaApproach,
                    out CampaignVec2 landEntry))
            {
                ActiveTravel.Remove(party);
                return false;
            }

            context.TargetIslandId = targetIsland.Id;
            context.SeaTransitionPoint = seaApproach;
            context.LandTransitionPoint = landEntry;
            context.State = TravelState.SailingToIsland;
            context.StateElapsedSeconds = 0f;
            MobileParty.NavigationType approachType = party.IsCurrentlyAtSea
                ? MobileParty.NavigationType.Naval
                : MobileParty.NavigationType.All;
            ExecuteMove(party, seaApproach, approachType);
            LogRoute(context, "approaching");
            return true;
        }

        private static bool BeginIslandDeparture(
            TravelContext context,
            IslandRecord currentIsland)
        {
            MobileParty party = context.Party;
            Vec2 preferred = context.FinalTarget.ToVec2();
            if (!IslandNavMeshOverlay.TryGetShoreRoute(
                    currentIsland,
                    preferred,
                    out CampaignVec2 seaApproach,
                    out CampaignVec2 landExit))
            {
                ActiveTravel.Remove(party);
                return false;
            }

            context.SourceIslandId = currentIsland.Id;
            context.LandTransitionPoint = landExit;
            context.SeaTransitionPoint = seaApproach;
            context.State = TravelState.WalkingToShip;
            context.StateElapsedSeconds = 0f;
            ExecuteMove(
                party,
                landExit,
                MobileParty.NavigationType.Default);
            LogRoute(context, "departing");
            return true;
        }

        private static void Tick(TravelContext context, float deltaSeconds)
        {
            MobileParty party = context.Party;
            if (party == null || !party.IsActive)
            {
                ActiveTravel.Remove(party);
                return;
            }

            if (context.State == TravelState.Disembarking ||
                context.State == TravelState.Embarking)
            {
                context.StateElapsedSeconds += Math.Max(0f, deltaSeconds);
            }

            switch (context.State)
            {
                case TravelState.SailingToIsland:
                    TickSailingToIsland(context);
                    break;
                case TravelState.Disembarking:
                    TickDisembarking(context);
                    break;
                case TravelState.WalkingToShip:
                    TickWalkingToShip(context);
                    break;
                case TravelState.Embarking:
                    TickEmbarking(context);
                    break;
            }
        }

        private static void TickSailingToIsland(TravelContext context)
        {
            MobileParty party = context.Party;
            if (!party.IsCurrentlyAtSea && HasArrived(party.Position, context.SeaTransitionPoint))
            {
                ExecuteMove(
                    party,
                    context.SeaTransitionPoint,
                    MobileParty.NavigationType.Naval);
                return;
            }

            if (!party.IsCurrentlyAtSea ||
                !HasArrived(party.Position, context.SeaTransitionPoint))
            {
                return;
            }

            ExecuteMove(
                party,
                context.FinalTarget,
                MobileParty.NavigationType.Default);
            if (!TryStartNavigationTransition(
                    party,
                    context.SeaTransitionPoint,
                    context.LandTransitionPoint))
            {
                AbortAt(context, context.SeaTransitionPoint, MobileParty.NavigationType.Naval);
                return;
            }

            context.State = TravelState.Disembarking;
            context.StateElapsedSeconds = 0f;
        }

        private static void TickDisembarking(TravelContext context)
        {
            MobileParty party = context.Party;
            if (party.IsTransitionInProgress)
            {
                if (context.StateElapsedSeconds < TransitionTimeoutSeconds)
                {
                    return;
                }

                if (!TryCompleteTransitionWithPublicApi(
                        context,
                        context.LandTransitionPoint,
                        false,
                        "disembarkation timeout"))
                {
                    AbortAt(
                        context,
                        context.SeaTransitionPoint,
                        MobileParty.NavigationType.Naval);
                    return;
                }
            }

            if (party.IsCurrentlyAtSea)
            {
                AbortAt(context, context.SeaTransitionPoint, MobileParty.NavigationType.Naval);
                return;
            }

            CampaignVec2 finalTarget = context.FinalTarget;
            ActiveTravel.Remove(party);
            ResumeOriginalBehavior(context);
        }

        private static void TickWalkingToShip(TravelContext context)
        {
            MobileParty party = context.Party;
            if (party.IsCurrentlyAtSea ||
                !HasArrived(party.Position, context.LandTransitionPoint))
            {
                return;
            }

            ExecuteMove(
                party,
                context.SeaTransitionPoint,
                MobileParty.NavigationType.Naval);
            if (!TryStartNavigationTransition(
                    party,
                    context.LandTransitionPoint,
                    context.SeaTransitionPoint))
            {
                AbortAt(context, context.LandTransitionPoint, MobileParty.NavigationType.Default);
                return;
            }

            context.State = TravelState.Embarking;
            context.StateElapsedSeconds = 0f;
        }

        private static void TickEmbarking(TravelContext context)
        {
            MobileParty party = context.Party;
            if (party.IsTransitionInProgress)
            {
                if (context.StateElapsedSeconds < TransitionTimeoutSeconds)
                {
                    return;
                }

                if (!TryCompleteTransitionWithPublicApi(
                        context,
                        context.SeaTransitionPoint,
                        true,
                        "embarkation timeout"))
                {
                    AbortAt(
                        context,
                        context.LandTransitionPoint,
                        MobileParty.NavigationType.Default);
                    return;
                }
            }

            if (!party.IsCurrentlyAtSea)
            {
                AbortAt(context, context.LandTransitionPoint, MobileParty.NavigationType.Default);
                return;
            }

            if (context.TargetIslandId != null &&
                TryFindIsland(context.TargetIslandId, out IslandRecord targetIsland))
            {
                BeginIslandApproach(context, targetIsland);
                return;
            }

            CampaignVec2 finalTarget = context.FinalTarget;
            ActiveTravel.Remove(party);
            ResumeOriginalBehavior(context);
        }

        private static void AbortAt(
            TravelContext context,
            CampaignVec2 position,
            MobileParty.NavigationType navigationType)
        {
            MobileParty party = context.Party;
            ActiveTravel.Remove(party);
            ExecuteMove(party, position, navigationType);
            Debug.Print(
                "[BLWarsailsInhabitableIslands] Island transition was cancelled for " +
                SafePartyName(party) + ".");
        }

        private static bool TryFindIsland(string id, out IslandRecord island)
        {
            foreach (IslandRecord candidate in IslandNavMeshOverlay.Islands)
            {
                if (candidate.Id == id)
                {
                    island = candidate;
                    return true;
                }
            }

            island = null;
            return false;
        }

        private static bool HasArrived(CampaignVec2 current, CampaignVec2 target)
        {
            return IsValidPosition(current) &&
                   IsValidPosition(target) &&
                   DistanceSquared(current, target) <=
                   ArrivalDistance * ArrivalDistance;
        }

        private static float DistanceSquared(CampaignVec2 left, CampaignVec2 right)
        {
            if (left == null || right == null)
            {
                return float.MaxValue;
            }

            Vec2 delta = left.ToVec2() - right.ToVec2();
            return delta.X * delta.X + delta.Y * delta.Y;
        }

        private static void ExecuteMove(
            MobileParty mobileParty,
            CampaignVec2 target,
            MobileParty.NavigationType navigationType)
        {
            if (mobileParty == null || !IsValidPosition(target))
            {
                return;
            }

            _bypassMovePatch = true;
            try
            {
                mobileParty.SetMoveGoToPoint(target, navigationType);
            }
            finally
            {
                _bypassMovePatch = false;
            }
        }

        private static void ResumeOriginalBehavior(TravelContext context)
        {
            MobileParty party = context.Party;
            if (party == null)
            {
                return;
            }

            _bypassMovePatch = true;
            try
            {
                switch (context.FinalBehavior)
                {
                    case AiBehavior.GoToSettlement:
                        if (context.FinalSettlement != null)
                        {
                            party.SetMoveGoToSettlement(
                                context.FinalSettlement,
                                context.FinalNavigationType,
                                context.IsTargetingPort);
                            return;
                        }

                        break;
                    case AiBehavior.RaidSettlement:
                        if (context.FinalSettlement != null)
                        {
                            party.SetMoveRaidSettlement(
                                context.FinalSettlement,
                                context.FinalNavigationType,
                                context.IsTargetingPort);
                            return;
                        }

                        break;
                    case AiBehavior.BesiegeSettlement:
                        if (context.FinalSettlement != null)
                        {
                            party.SetMoveBesiegeSettlement(
                                context.FinalSettlement,
                                context.FinalNavigationType);
                            return;
                        }

                        break;
                    case AiBehavior.DefendSettlement:
                        if (context.FinalSettlement != null)
                        {
                            party.SetMoveDefendSettlement(
                                context.FinalSettlement,
                                context.IsTargetingPort,
                                context.FinalNavigationType);
                            return;
                        }

                        break;
                    case AiBehavior.PatrolAroundPoint:
                        if (IsValidPosition(context.FinalTarget))
                        {
                            party.SetMovePatrolAroundPoint(
                                context.FinalTarget,
                                context.FinalNavigationType);
                        }

                        return;
                    case AiBehavior.EngageParty:
                        if (context.FinalParty != null)
                        {
                            party.SetMoveEngageParty(
                                context.FinalParty,
                                context.FinalNavigationType);
                            return;
                        }

                        break;
                    case AiBehavior.EscortParty:
                        if (context.FinalParty != null)
                        {
                            party.SetMoveEscortParty(
                                context.FinalParty,
                                context.FinalNavigationType,
                                context.IsTargetingPort);
                            return;
                        }

                        break;
                }

                if (!IsValidPosition(context.FinalTarget))
                {
                    return;
                }

                MobileParty.NavigationType navigationType =
                    context.FinalTarget.IsOnLand &&
                    !IslandNavMeshOverlay.IsImportedLandPosition(context.FinalTarget)
                        ? MobileParty.NavigationType.All
                        : context.FinalNavigationType;
                party.SetMoveGoToPoint(context.FinalTarget, navigationType);
            }
            finally
            {
                _bypassMovePatch = false;
            }
        }

        private static bool TryStartNavigationTransition(
            MobileParty mobileParty,
            CampaignVec2 transitionStart,
            CampaignVec2 transitionEnd)
        {
            if (mobileParty == null ||
                mobileParty.IsTransitionInProgress ||
                !IsValidPosition(transitionStart) ||
                !IsValidPosition(transitionEnd))
            {
                return false;
            }

            bool destinationIsAtSea = !transitionEnd.IsOnLand;
            if (InitializeNavigationTransitionMethod == null)
            {
                return TryCompleteTransitionWithPublicApi(
                    mobileParty,
                    transitionEnd,
                    destinationIsAtSea,
                    "animated transition API unavailable");
            }

            try
            {
                AlignTransitionParties(mobileParty, transitionStart);
                object[] arguments =
                {
                    transitionStart,
                    transitionEnd,
                    -1,
                    new MobileParty[Math.Max(
                        1,
                        mobileParty.AttachedParties.Count + 1)]
                };
                InitializeNavigationTransitionMethod.Invoke(
                    mobileParty,
                    arguments);
                if (mobileParty.IsTransitionInProgress)
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Animated island transition " +
                    "failed for " + SafePartyName(mobileParty) + ": " + ex);
            }

            return TryCompleteTransitionWithPublicApi(
                mobileParty,
                transitionEnd,
                destinationIsAtSea,
                "animated transition did not start");
        }

        private static MethodInfo ResolveInitializeNavigationTransitionMethod()
        {
            return AccessTools.Method(
                typeof(MobileParty),
                "InitializeNavigationTransitionParallel",
                new[]
                {
                    typeof(CampaignVec2),
                    typeof(CampaignVec2),
                    typeof(int).MakeByRefType(),
                    typeof(MobileParty[]).MakeByRefType()
                });
        }

        private static bool TryCompleteTransitionWithPublicApi(
            TravelContext context,
            CampaignVec2 destination,
            bool destinationIsAtSea,
            string reason)
        {
            return context != null && TryCompleteTransitionWithPublicApi(
                context.Party,
                destination,
                destinationIsAtSea,
                reason);
        }

        private static bool TryCompleteTransitionWithPublicApi(
            MobileParty mobileParty,
            CampaignVec2 destination,
            bool destinationIsAtSea,
            string reason)
        {
            if (mobileParty == null ||
                !mobileParty.IsActive ||
                mobileParty.MapEvent != null ||
                mobileParty.CurrentSettlement != null ||
                !IsValidPosition(destination))
            {
                return false;
            }

            MobileParty.NavigationType navigationType = destinationIsAtSea
                ? MobileParty.NavigationType.Naval
                : MobileParty.NavigationType.Default;
            if (!NavigationHelper.IsPositionValidForNavigationType(
                    destination,
                    navigationType))
            {
                return false;
            }

            try
            {
                if (mobileParty.IsTransitionInProgress)
                {
                    _allowTransitionCancellation = true;
                    try
                    {
                        mobileParty.CancelNavigationTransition();
                    }
                    finally
                    {
                        _allowTransitionCancellation = false;
                    }
                }

                if (destinationIsAtSea)
                {
                    mobileParty.SetSailAtPosition(destination);
                }
                else
                {
                    mobileParty.DisembarkToPosition(destination);
                }

                string partyId = mobileParty.StringId ?? SafePartyName(mobileParty);
                if (LoggedTransitionFallbacks.Add(partyId + ":" + reason))
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Completed island " +
                        "transition through the public API for " +
                        SafePartyName(mobileParty) + " (" + reason + ").");
                }

                return mobileParty.IsCurrentlyAtSea == destinationIsAtSea;
            }
            catch (Exception ex)
            {
                _allowTransitionCancellation = false;
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Public island transition " +
                    "fallback failed for " + SafePartyName(mobileParty) +
                    ": " + ex);
                return false;
            }
        }

        private static void AlignTransitionParties(
            MobileParty mobileParty,
            CampaignVec2 transitionStart)
        {
            if (mobileParty == null || !IsValidPosition(transitionStart))
            {
                return;
            }

            mobileParty.Position = transitionStart;
            foreach (MobileParty attachedParty in mobileParty.AttachedParties)
            {
                AlignTransitionParties(attachedParty, transitionStart);
            }
        }

        private static MobileParty GetTransitionRoot(MobileParty party)
        {
            MobileParty current = party;
            while (current?.AttachedTo != null)
            {
                current = current.AttachedTo;
            }

            return current;
        }

        private static string SafePartyName(MobileParty party)
        {
            return party?.Name?.ToString() ?? "<unknown party>";
        }

        private static bool IsValidPosition(CampaignVec2 position)
        {
            return position != null &&
                   position.ToVec2().IsValid &&
                   IsCampaignMapReady() &&
                   position.IsValid();
        }

        private static bool IsCampaignMapReady()
        {
            return Campaign.Current != null &&
                   Campaign.Current.MapSceneWrapper != null;
        }

        private static void LogRoute(TravelContext context, string verb)
        {
            if (!IsValidPosition(context.LandTransitionPoint) ||
                !IsValidPosition(context.SeaTransitionPoint))
            {
                return;
            }

            Debug.Print(string.Format(
                CultureInfo.InvariantCulture,
                "[BLWarsailsInhabitableIslands] {0} island route for {1}: land={2:0.##},{3:0.##}; sea={4:0.##},{5:0.##}.",
                verb,
                SafePartyName(context.Party),
                context.LandTransitionPoint.ToVec2().X,
                context.LandTransitionPoint.ToVec2().Y,
                context.SeaTransitionPoint.ToVec2().X,
                context.SeaTransitionPoint.ToVec2().Y));
        }

        private sealed class TravelContext
        {
            public MobileParty Party;
            public TravelState State;
            public CampaignVec2 FinalTarget = CampaignVec2.Invalid;
            public CampaignVec2 LandTransitionPoint = CampaignVec2.Invalid;
            public CampaignVec2 SeaTransitionPoint = CampaignVec2.Invalid;
            public MobileParty.NavigationType FinalNavigationType;
            public AiBehavior FinalBehavior;
            public Settlement FinalSettlement;
            public MobileParty FinalParty;
            public bool IsTargetingPort;
            public string SourceIslandId;
            public string TargetIslandId;
            public bool LoggedPreservedCancellation;
            public float StateElapsedSeconds;
        }

        private enum TravelState
        {
            None,
            SailingToIsland,
            Disembarking,
            WalkingToShip,
            Embarking
        }
    }

    [HarmonyPatch(
        typeof(NavalPartyNavigationModel),
        nameof(NavalPartyNavigationModel.CanPlayerNavigateToPosition))]
    internal static class IslandCanNavigatePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            CampaignVec2 vec2,
            ref MobileParty.NavigationType navigationType,
            ref bool __result)
        {
            if (__result)
            {
                return;
            }

            MobileParty mainParty = MobileParty.MainParty;
            if (mainParty == null ||
                vec2 == null ||
                Campaign.Current == null ||
                Campaign.Current.MapSceneWrapper == null)
            {
                return;
            }

            bool targetsImportedIsland =
                IslandNavMeshOverlay.IsImportedLandPosition(vec2) ||
                IslandNavMeshOverlay.TryCreateLandPosition(
                    vec2.ToVec2(),
                    out CampaignVec2 _);
            if (targetsImportedIsland &&
                (mainParty.IsCurrentlyAtSea ||
                 IslandNavMeshOverlay.IsImportedLandPosition(mainParty.Position)))
            {
                navigationType = MobileParty.NavigationType.Default;
                __result = true;
                return;
            }

            if (!mainParty.IsCurrentlyAtSea &&
                IslandNavMeshOverlay.IsImportedLandPosition(mainParty.Position) &&
                !vec2.IsOnLand &&
                vec2.IsValid() &&
                NavigationHelper.IsPositionValidForNavigationType(
                    vec2,
                    MobileParty.NavigationType.Naval))
            {
                navigationType = MobileParty.NavigationType.Naval;
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMoveGoToPoint))]
    internal static class IslandMoveCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            CampaignVec2 point,
            MobileParty.NavigationType navigationType)
        {
            try
            {
                return !IslandTravelController.TryHandleMove(
                    __instance,
                    point,
                    navigationType,
                    AiBehavior.GoToPoint);
            }
            catch (Exception ex)
            {
                Debug.Print("[BLWarsailsInhabitableIslands] Island move command failed: " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMoveGoToSettlement))]
    internal static class IslandGoToSettlementCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            Settlement settlement,
            ref MobileParty.NavigationType navigationType,
            ref bool isTargetingThePort)
        {
            if (__instance == null || settlement == null)
            {
                return true;
            }

            if (settlement != null &&
                settlement.HasPort &&
                IslandSettlementIntegration.IsIslandSettlement(
                    settlement,
                    out IslandRecord targetIsland))
            {
                bool onSameIsland =
                    !__instance.IsCurrentlyAtSea &&
                    IslandNavMeshOverlay.TryGetIsland(
                        __instance.Position,
                        out IslandRecord currentIsland) &&
                    currentIsland.Id == targetIsland.Id;

                isTargetingThePort = !onSameIsland;
                if (isTargetingThePort)
                {
                    navigationType = __instance.IsCurrentlyAtSea
                        ? MobileParty.NavigationType.Naval
                        : MobileParty.NavigationType.All;
                }
            }

            CampaignVec2 target =
                isTargetingThePort ? settlement.PortPosition : settlement.GatePosition;
            return !IslandTravelController.TryHandleMove(
                __instance,
                target,
                navigationType,
                AiBehavior.GoToSettlement,
                settlement,
                null,
                isTargetingThePort);
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMoveRaidSettlement))]
    internal static class IslandRaidSettlementCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            Settlement settlement,
            MobileParty.NavigationType navigationType,
            bool isTargetingPort)
        {
            if (__instance == null || settlement == null)
            {
                return true;
            }

            CampaignVec2 target =
                isTargetingPort ? settlement.PortPosition : settlement.GatePosition;
            return !IslandTravelController.TryHandleMove(
                __instance,
                target,
                navigationType,
                AiBehavior.RaidSettlement,
                settlement,
                null,
                isTargetingPort);
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMoveBesiegeSettlement))]
    internal static class IslandBesiegeSettlementCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            Settlement settlement,
            MobileParty.NavigationType navigationType)
        {
            if (__instance == null || settlement == null)
            {
                return true;
            }

            return !IslandTravelController.TryHandleMove(
                __instance,
                settlement.GatePosition,
                navigationType,
                AiBehavior.BesiegeSettlement,
                settlement);
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMoveDefendSettlement))]
    internal static class IslandDefendSettlementCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            Settlement settlement,
            bool isTargetingPort,
            MobileParty.NavigationType navigationType)
        {
            if (__instance == null || settlement == null)
            {
                return true;
            }

            CampaignVec2 target =
                isTargetingPort ? settlement.PortPosition : settlement.GatePosition;
            return !IslandTravelController.TryHandleMove(
                __instance,
                target,
                navigationType,
                AiBehavior.DefendSettlement,
                settlement,
                null,
                isTargetingPort);
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMoveEngageParty))]
    internal static class IslandEngagePartyCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            MobileParty party,
            MobileParty.NavigationType navigationType)
        {
            if (__instance == null || party == null || !party.IsActive)
            {
                return true;
            }

            return !IslandTravelController.TryHandleMove(
                __instance,
                party.Position,
                navigationType,
                AiBehavior.EngageParty,
                null,
                party);
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMoveEscortParty))]
    internal static class IslandEscortPartyCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            MobileParty mobileParty,
            MobileParty.NavigationType navigationType,
            bool isTargetingPort)
        {
            if (__instance == null ||
                mobileParty == null ||
                !mobileParty.IsActive)
            {
                return true;
            }

            return !IslandTravelController.TryHandleMove(
                __instance,
                mobileParty.Position,
                navigationType,
                AiBehavior.EscortParty,
                null,
                mobileParty,
                isTargetingPort);
        }
    }

    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SetMovePatrolAroundPoint))]
    internal static class IslandPatrolPointCommandPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            MobileParty __instance,
            CampaignVec2 point,
            MobileParty.NavigationType navigationType)
        {
            return !IslandTravelController.TryHandleMove(
                __instance,
                point,
                navigationType,
                AiBehavior.PatrolAroundPoint);
        }
    }

    [HarmonyPatch(typeof(MobileParty), "CancelNavigationTransitionParallel")]
    internal static class IslandTransitionCancellationPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(MobileParty __instance)
        {
            return !IslandTravelController.ShouldPreserveNavigationTransition(__instance);
        }
    }
}
