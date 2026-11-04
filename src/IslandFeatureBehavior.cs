using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace BLWarsailsInhabitableIslands
{
    internal sealed class IslandFeatureBehavior : CampaignBehaviorBase
    {
        private const int DesiredActiveHideouts = 4;
        private const int RoamingPartiesPerHideout = 3;
        private const int GracePeriodDays = 30;
        private const float CoastalDiscoveryDistance = 8f;
        private const float HideoutPositionSearchStep = 0.75f;
        private const int HideoutPositionSearchDirections = 16;
        private const string HideoutPrefix = "blwii_hideout_";
        private const string RaiderPrefix = "blwii_raider_";
        private const string GuardPrefix = "blwii_guard_";

        private static readonly MethodInfo SettlementPositionSetter =
            AccessTools.PropertySetter(
                typeof(Settlement),
                nameof(Settlement.Position));
        private static readonly MethodInfo GatePositionSetter =
            AccessTools.PropertySetter(
                typeof(Settlement),
                nameof(Settlement.GatePosition));
        private static readonly HashSet<string> LoggedNavigationRepairs =
            new HashSet<string>();

        private static readonly string[] CandidatePriority =
        {
            "blwii_hideout_01",
            "blwii_hideout_02",
            "blwii_hideout_03",
            "blwii_hideout_04",
            "blwii_hideout_05",
            "blwii_hideout_06",
            "blwii_hideout_07",
            "blwii_hideout_08"
        };

        private static IslandFeatureBehavior _instance;
        private static bool _suppressHideoutEvents;

        private Dictionary<string, int> _hideoutStates =
            new Dictionary<string, int>();
        private Dictionary<string, CampaignTime> _reactivationTimes =
            new Dictionary<string, CampaignTime>();
        private Dictionary<string, int> _temporaryFerryCounts =
            new Dictionary<string, int>();
        private bool _piratesEnabled = true;
        private int _schemaVersion = 3;
        private bool _initialized;
        private PlayerSettlementBehaviour _settlementCreatedSource;
        private bool _settlementCreationRepairFailed;

        internal static string GetSettlementCreationRepairStatus()
        {
            if (_instance == null || _instance._settlementCreatedSource == null)
            {
                return "not attached (campaign not initialized)";
            }

            return _instance._settlementCreationRepairFailed
                ? "attached; repair failure recorded"
                : "attached to confirmed creation event";
        }

        internal static bool IsManagedParty(MobileParty party)
        {
            string id = party?.StringId;
            return id != null &&
                   (id.StartsWith(RaiderPrefix, StringComparison.Ordinal) ||
                    id.StartsWith(GuardPrefix, StringComparison.Ordinal));
        }

        internal static bool IsIslandRaiderParty(MobileParty party)
        {
            if (IsManagedParty(party))
            {
                return true;
            }

            string homeId = party?.HomeSettlement?.StringId;
            return homeId != null &&
                   homeId.StartsWith(HideoutPrefix, StringComparison.Ordinal);
        }

        internal static bool IsActiveHideoutOnIsland(Vec2 position)
        {
            if (_instance == null ||
                !_instance._piratesEnabled ||
                !IslandNavMeshOverlay.TryGetIsland(position, out IslandRecord island))
            {
                return false;
            }

            foreach (string id in CandidatePriority)
            {
                if (_instance.GetState(id) != HideoutState.Active)
                {
                    continue;
                }

                Settlement settlement = Settlement.Find(id);
                if (settlement != null &&
                    IslandNavMeshOverlay.TryGetIsland(
                        settlement.Position,
                        out IslandRecord hideoutIsland) &&
                    hideoutIsland.Id == island.Id)
                {
                    return true;
                }
            }

            return false;
        }

        internal static string GetPirateStatus()
        {
            if (Campaign.Current == null || _instance == null)
            {
                return "Load a campaign before changing Inhabitable Isles pirates.";
            }

            if (!_instance._piratesEnabled)
            {
                return "Inhabitable Isles pirates are disabled for this save.";
            }

            int activeStates = CandidatePriority.Count(
                id => _instance.GetState(id) == HideoutState.Active);
            int activeSettlements = CandidatePriority.Count(
                id => Settlement.Find(id)?.IsActive == true);
            int visibleSettlements = CandidatePriority.Count(
                id => Settlement.Find(id)?.IsVisible == true);
            return "Inhabitable Isles pirates are enabled for this save. " +
                   activeStates + " managed hideout states are active; " +
                   activeSettlements + " settlement objects are active; " +
                   visibleSettlements + " markers are currently visible.";
        }

        internal static string SetPiratesEnabled(bool enabled)
        {
            if (Campaign.Current == null || _instance == null)
            {
                return "Load a campaign before changing Inhabitable Isles pirates.";
            }

            _instance._piratesEnabled = enabled;
            _instance.TryInitialize();
            if (_instance._initialized)
            {
                if (enabled)
                {
                    _instance.EnablePirates();
                }
                else
                {
                    _instance.DisablePirates();
                }
            }

            return enabled
                ? "Inhabitable Isles pirates enabled for this save."
                : "Inhabitable Isles pirates disabled for this save. Managed hideouts and parties were removed.";
        }

        internal static string GetHideoutAudit()
        {
            if (Campaign.Current == null || _instance == null)
            {
                return "Load a campaign before auditing Inhabitable Isles hideouts.";
            }

            StringBuilder result = new StringBuilder(
                "Inhabitable Isles hideout audit:");
            foreach (string id in CandidatePriority)
            {
                Settlement settlement = Settlement.Find(id);
                Hideout hideout = settlement?.Hideout;
                bool positionValid =
                    settlement != null &&
                    IsUsableHideoutPosition(settlement.Position);
                bool gateValid =
                    settlement != null &&
                    IsUsableHideoutPosition(settlement.GatePosition);
                int partyCount = settlement?.Parties?.Count ?? 0;
                result.Append("\n");
                result.Append(id);
                result.Append(": state=");
                result.Append(_instance.GetState(id));
                result.Append(", active=");
                result.Append(settlement?.IsActive == true);
                result.Append(", visible=");
                result.Append(settlement?.IsVisible == true);
                result.Append(", infested=");
                result.Append(hideout?.IsInfested == true);
                result.Append(", spotted=");
                result.Append(settlement?.IsVisible == true);
                result.Append(", position=");
                result.Append(positionValid ? "valid" : "invalid");
                result.Append(", gate=");
                result.Append(gateValid ? "valid" : "invalid");
                result.Append(", parties=");
                result.Append(partyCount);
                result.Append(".");
            }

            return result.ToString();
        }

        public IslandFeatureBehavior()
        {
            _instance = this;
        }

        public override void RegisterEvents()
        {
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(
                this,
                OnCampaignReady);
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(
                this,
                OnCampaignReady);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(
                this,
                DailyTick);
            CampaignEvents.HourlyTickPartyEvent.AddNonSerializedListener(
                this,
                HourlyTickParty);
            CampaignEvents.OnHideoutDeactivatedEvent.AddNonSerializedListener(
                this,
                OnHideoutDeactivated);
            AttachSettlementCreatedListener();
        }

        private void AttachSettlementCreatedListener()
        {
            PlayerSettlementBehaviour source = PlayerSettlementBehaviour.Instance;
            if (source == null || ReferenceEquals(source, _settlementCreatedSource))
            {
                return;
            }

            PlayerSettlementBehaviour.SettlementCreatedEvent.AddNonSerializedListener(
                this,
                OnPlayerSettlementCreated);
            _settlementCreatedSource = source;
        }

        private void OnPlayerSettlementCreated(Settlement settlement)
        {
            if (!IslandSettlementIntegration.IsPlayerSettlement(settlement) ||
                !IslandSettlementIntegration.IsIslandSettlement(settlement, out _))
            {
                return;
            }

            try
            {
                IslandSettlementIntegration.RepairAll();
                IslandTreeDecorator.RefreshSettlementClearings();
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Repaired confirmed island settlement " +
                    settlement.StringId + " before Player Settlement save/reload.");
            }
            catch (Exception ex)
            {
                _settlementCreationRepairFailed = true;
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Confirmed settlement repair failed for " +
                    settlement.StringId + ": " + ex);
            }
        }

        public override void SyncData(IDataStore dataStore)
        {
            dataStore.SyncData(
                "blwii_hideout_states_v1",
                ref _hideoutStates);
            dataStore.SyncData(
                "blwii_hideout_reactivation_v1",
                ref _reactivationTimes);
            if (!dataStore.SyncData(
                    "blwii_pirates_enabled_v1",
                    ref _piratesEnabled) &&
                dataStore.IsLoading)
            {
                _piratesEnabled = true;
            }
            dataStore.SyncData(
                "blwii_feature_schema",
                ref _schemaVersion);
            dataStore.SyncData(
                "blwii_temporary_ferry_counts_v1",
                ref _temporaryFerryCounts);

            _hideoutStates ??= new Dictionary<string, int>();
            _reactivationTimes ??= new Dictionary<string, CampaignTime>();
            _temporaryFerryCounts ??= new Dictionary<string, int>();
            if (_schemaVersion < 3)
            {
                _schemaVersion = 3;
            }

            IslandVillageLogistics.AttachTemporaryFerryLedger(
                _temporaryFerryCounts);
        }

        private void OnCampaignReady(CampaignGameStarter starter)
        {
            AttachSettlementCreatedListener();
            LoggedNavigationRepairs.Clear();
            _initialized = false;
            IslandVillageLogistics.AttachTemporaryFerryLedger(
                _temporaryFerryCounts);
            IslandVillageLogistics.ReconcileTemporaryFerries();
            TryInitialize();
        }

        private void DailyTick()
        {
            TryInitialize();
            if (!_initialized)
            {
                return;
            }

            IslandSettlementIntegration.RepairAll();
            IslandTreeDecorator.RefreshSettlementClearings();
            RepairCandidateNavigation();
            if (_piratesEnabled)
            {
                EnablePirates();
            }
            else
            {
                DisablePirates();
            }
        }

        private void HourlyTickParty(MobileParty party)
        {
            IslandVillageLogistics.RetireTemporaryFerriesIfSafe(party);
            IslandVillageLogistics.PrepareVillagerFerry(party);
            if (!IslandTravelController.TryReleaseStuckIslandVisitor(party))
            {
                IslandTravelController.TryRecoverPartyRoute(party);
            }

            if (party == MobileParty.MainParty)
            {
                TryInitialize();
                if (_initialized)
                {
                    UpdateHideoutDiscovery();
                }
            }

            if (!_initialized ||
                !_piratesEnabled ||
                !IsManagedParty(party) ||
                !party.IsActive)
            {
                return;
            }

            Settlement home = party.HomeSettlement;
            if (home == null ||
                !IsCandidate(home) ||
                GetState(home.StringId) != HideoutState.Active ||
                party.MapEvent != null ||
                party.CurrentSettlement != null)
            {
                return;
            }

            if (!IslandNavMeshOverlay.TryGetIsland(
                    home.Position,
                    out IslandRecord homeIsland))
            {
                return;
            }

            float maximumDistance = party.IsCurrentlyAtSea ? 24f : 12f;
            Vec2 delta = party.Position.ToVec2() - homeIsland.Center;
            if (delta.LengthSquared > maximumDistance * maximumDistance)
            {
                DirectPatrolHome(party, home, homeIsland);
                return;
            }

            MaintainPatrolRole(party, homeIsland);
        }

        private void OnHideoutDeactivated(Settlement settlement)
        {
            if (_suppressHideoutEvents ||
                !_piratesEnabled ||
                !IsCandidate(settlement))
            {
                return;
            }

            SetState(settlement.StringId, HideoutState.Grace);
            _reactivationTimes[settlement.StringId] =
                CampaignTime.DaysFromNow(GracePeriodDays);
            RemoveIslandHideoutParties(settlement.StringId);
            Debug.Print(
                "[BLWarsailsInhabitableIslands] " + settlement.StringId +
                " cleared; starting the 30-day settlement grace period.");
        }

        private void TryInitialize()
        {
            if (_initialized ||
                Campaign.Current == null ||
                IslandNavMeshOverlay.Islands.Count == 0)
            {
                return;
            }

            foreach (string id in CandidatePriority)
            {
                if (!_hideoutStates.ContainsKey(id))
                {
                    _hideoutStates[id] = (int)HideoutState.Dormant;
                }

                RepairCandidateNavigation(Settlement.Find(id));
                ApplyCandidateActivation(
                    id,
                    _piratesEnabled
                        ? GetState(id)
                        : HideoutState.Dormant);
            }

            if (_piratesEnabled)
            {
                EnablePirates();
            }
            else
            {
                DisablePirates();
            }

            IslandSettlementIntegration.RepairAll();
            _initialized = true;
            Debug.Print(
                "[BLWarsailsInhabitableIslands] Island feature migration initialized.");
        }

        private static void RepairCandidateNavigation()
        {
            foreach (string id in CandidatePriority)
            {
                RepairCandidateNavigation(Settlement.Find(id));
            }
        }

        private static void RepairCandidateNavigation(Settlement settlement)
        {
            if (settlement == null ||
                !IslandNavMeshOverlay.TryGetIsland(
                    settlement.Position,
                    out IslandRecord island))
            {
                return;
            }

            bool sessionRefresh =
                !LoggedNavigationRepairs.Contains(settlement.StringId);
            bool positionNeedsRepair =
                !IsUsableHideoutPosition(settlement.Position);
            bool gateNeedsRepair =
                !IsUsableHideoutPosition(settlement.GatePosition);
            if (!sessionRefresh && !positionNeedsRepair && !gateNeedsRepair)
            {
                return;
            }

            if (!TryResolveHideoutLandPosition(
                    settlement.Position.ToVec2(),
                    island,
                    out CampaignVec2 repaired))
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Could not find raised, navigable terrain for island hideout " +
                    settlement.StringId + ".");
                return;
            }

            try
            {
                // Rebuild both CampaignVec2 values against the current dynamic
                // navmesh. A face can look valid after loading while still
                // retaining stale map-face data from before our island surfaces.
                SettlementPositionSetter?.Invoke(
                    settlement,
                    new object[] { repaired });
                GatePositionSetter?.Invoke(
                    settlement,
                    new object[] { repaired });

                settlement.CheckPositionsForMapChangeAndUpdateIfNeeded();
                if (LoggedNavigationRepairs.Add(settlement.StringId))
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Grounded island hideout " +
                        settlement.StringId +
                        " on its imported land face so its marker and gate remain clickable.");
                }
            }
            catch (Exception ex)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Could not repair island hideout navigation for " +
                    settlement.StringId + ": " + ex);
            }
        }

        private static bool TryResolveHideoutLandPosition(
            Vec2 preferred,
            IslandRecord island,
            out CampaignVec2 position)
        {
            Vec2 clamped = island.ClampInside(preferred, 0.5f);
            Vec2[] candidates =
            {
                preferred,
                clamped,
                island.Center,
                island.Center + island.Axis * Math.Min(1.5f, island.Length * 0.15f),
                island.Center - island.Axis * Math.Min(1.5f, island.Length * 0.15f),
                island.Center + island.Perpendicular * Math.Min(1.5f, island.Width * 0.15f),
                island.Center - island.Perpendicular * Math.Min(1.5f, island.Width * 0.15f)
            };

            foreach (Vec2 candidate in candidates)
            {
                if (TryCreateHideoutLandPosition(
                        candidate,
                        island,
                        out position))
                {
                    return true;
                }
            }

            float maximumRadius = Math.Min(
                10f,
                Math.Max(island.Length, island.Width) * 0.45f);
            Vec2[] anchors = { clamped, island.Center };
            for (float radius = HideoutPositionSearchStep;
                 radius <= maximumRadius;
                 radius += HideoutPositionSearchStep)
            {
                for (int directionIndex = 0;
                     directionIndex < HideoutPositionSearchDirections;
                     directionIndex++)
                {
                    float angle =
                        (float)(Math.PI * 2d * directionIndex /
                                HideoutPositionSearchDirections);
                    Vec2 offset =
                        island.Axis * ((float)Math.Cos(angle) * radius) +
                        island.Perpendicular * ((float)Math.Sin(angle) * radius);
                    foreach (Vec2 anchor in anchors)
                    {
                        if (TryCreateHideoutLandPosition(
                                anchor + offset,
                                island,
                                out position))
                        {
                            return true;
                        }
                    }
                }
            }

            position = CampaignVec2.Invalid;
            return false;
        }

        private static bool TryCreateHideoutLandPosition(
            Vec2 candidate,
            IslandRecord island,
            out CampaignVec2 position)
        {
            Vec2 clamped = island.ClampInside(candidate, 0.6f);
            position = CampaignVec2.Invalid;
            if (!IslandNavMeshOverlay.TryGetRaisedTerrainHeightAndNormal(
                    clamped,
                    out float _,
                    out Vec3 _))
            {
                return false;
            }

            return IslandNavMeshOverlay.TryCreateLandPosition(
                clamped,
                out position);
        }

        private static bool IsUsableHideoutPosition(CampaignVec2 position)
        {
            return position.IsValid() &&
                   IslandNavMeshOverlay.IsImportedLandPosition(position) &&
                   IslandNavMeshOverlay.TryGetRaisedTerrainHeightAndNormal(
                       position.ToVec2(),
                       out float _,
                       out Vec3 _);
        }

        private void UpdateHideoutDiscovery()
        {
            MobileParty mainParty = MobileParty.MainParty;
            if (mainParty == null || !mainParty.IsActive)
            {
                return;
            }

            foreach (string id in CandidatePriority)
            {
                if (GetState(id) != HideoutState.Active)
                {
                    continue;
                }

                Settlement settlement = Settlement.Find(id);
                Hideout hideout = settlement?.Hideout;
                if (hideout == null ||
                    !hideout.IsInfested ||
                    !IslandNavMeshOverlay.TryGetIsland(
                        settlement.Position,
                        out IslandRecord hideoutIsland))
                {
                    continue;
                }

                if (settlement.IsVisible)
                {
                    continue;
                }

                bool playerOnSameIsland =
                    !mainParty.IsCurrentlyAtSea &&
                    IslandNavMeshOverlay.TryGetIsland(
                        mainParty.Position,
                        out IslandRecord playerIsland) &&
                    playerIsland.Id == hideoutIsland.Id;
                bool playerNearCoast =
                    DistanceSquaredToIsland(
                        mainParty.Position.ToVec2(),
                        hideoutIsland) <=
                    CoastalDiscoveryDistance * CoastalDiscoveryDistance;
                if (!playerOnSameIsland && !playerNearCoast)
                {
                    continue;
                }

                settlement.IsVisible = true;
                CampaignEventDispatcher.Instance.OnHideoutSpotted(
                    mainParty.Party,
                    settlement.Party);
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Revealed reachable island hideout " +
                    id + " when the player reached its island waters.");
            }
        }

        private static float DistanceSquaredToIsland(
            Vec2 position,
            IslandRecord island)
        {
            Vec2 delta = position - island.Center;
            float along = Math.Max(
                0f,
                Math.Abs(delta.X * island.Axis.X +
                         delta.Y * island.Axis.Y) -
                island.Length * 0.5f);
            float across = Math.Max(
                0f,
                Math.Abs(delta.X * island.Perpendicular.X +
                         delta.Y * island.Perpendicular.Y) -
                island.Width * 0.5f);
            return along * along + across * across;
        }

        private void UpdateHideoutStates()
        {
            foreach (string id in CandidatePriority)
            {
                Settlement hideout = Settlement.Find(id);
                if (hideout == null)
                {
                    continue;
                }

                bool occupied = HasPlayerSettlementOnSameIsland(hideout);
                HideoutState state = GetState(id);
                if (occupied)
                {
                    if (state != HideoutState.SettlementSuppressed)
                    {
                        SetState(id, HideoutState.SettlementSuppressed);
                    }

                    RemoveIslandHideoutParties(id);
                    continue;
                }

                if (state == HideoutState.SettlementSuppressed)
                {
                    SetState(id, HideoutState.Grace);
                    _reactivationTimes[id] =
                        CampaignTime.DaysFromNow(GracePeriodDays);
                    RemoveIslandHideoutParties(id);
                    continue;
                }

                if (state == HideoutState.Grace &&
                    (!_reactivationTimes.TryGetValue(
                         id,
                         out CampaignTime reactivationTime) ||
                     reactivationTime.IsPast))
                {
                    SetState(id, HideoutState.Active);
                }

                if (GetState(id) != HideoutState.Active)
                {
                    RemoveIslandHideoutParties(id);
                }
            }
        }

        private void EnsureDesiredActiveHideouts()
        {
            int allocated = CandidatePriority.Count(
                id =>
                {
                    HideoutState state = GetState(id);
                    return state == HideoutState.Active ||
                           state == HideoutState.Grace;
                });
            if (allocated >= DesiredActiveHideouts)
            {
                return;
            }

            foreach (string id in CandidatePriority)
            {
                if (allocated >= DesiredActiveHideouts)
                {
                    break;
                }

                Settlement settlement = Settlement.Find(id);
                if (settlement == null ||
                    GetState(id) != HideoutState.Dormant ||
                    !IsCandidateNavigationValid(settlement) ||
                    HasPlayerSettlementOnSameIsland(settlement))
                {
                    continue;
                }

                SetState(id, HideoutState.Active);
                allocated++;
            }
        }

        private void EnablePirates()
        {
            UpdateHideoutStates();
            DeactivateInvalidHideouts();
            foreach (string id in CandidatePriority)
            {
                ApplyCandidateActivation(id, GetState(id));
            }

            EnsureDesiredActiveHideouts();
            foreach (string id in CandidatePriority)
            {
                if (GetState(id) == HideoutState.Active)
                {
                    EnsureHideoutPopulation(id);
                }
            }

            UpdateHideoutDiscovery();
        }

        private void DisablePirates()
        {
            foreach (string id in CandidatePriority)
            {
                ApplyCandidateActivation(id, HideoutState.Dormant);
                RemoveIslandHideoutParties(id);
            }
        }

        private void EnsureHideoutPopulation(string hideoutId)
        {
            Settlement settlement = Settlement.Find(hideoutId);
            if (settlement?.Hideout == null ||
                !IsCandidateNavigationValid(settlement) ||
                !IslandNavMeshOverlay.TryGetIsland(
                    settlement.Position,
                    out IslandRecord island))
            {
                return;
            }

            int defenderMinimum = Math.Max(
                1,
                Campaign.Current.Models.BanditDensityModel
                    .NumberOfMinimumBanditPartiesInAHideoutToInfestIt);
            int defenders = settlement.Parties.Count(
                party =>
                    party.StringId != null &&
                    party.StringId.StartsWith(
                        GuardPrefix + hideoutId + "_",
                        StringComparison.Ordinal));
            for (int index = defenders; index < defenderMinimum; index++)
            {
                CreateManagedParty(
                    settlement,
                    GuardPrefix + hideoutId + "_" + index);
            }

            List<MobileParty> roaming = MobileParty.AllBanditParties
                .Where(
                    party =>
                        party.StringId != null &&
                        party.StringId.StartsWith(
                            RaiderPrefix + hideoutId + "_",
                            StringComparison.Ordinal))
                .ToList();
            for (int index = roaming.Count; index < RoamingPartiesPerHideout; index++)
            {
                string role = index == 0 ? "land" : index == 1 ? "sea" : "mixed";
                MobileParty party = CreateManagedParty(
                    settlement,
                    RaiderPrefix + hideoutId + "_" + role + "_" + index);
                if (party != null)
                {
                    LaunchPatrol(party, settlement, island, role);
                    roaming.Add(party);
                }
            }

            foreach (MobileParty party in roaming)
            {
                if (party.CurrentSettlement == settlement)
                {
                    string role =
                        party.StringId.IndexOf("_sea_", StringComparison.Ordinal) >= 0
                            ? "sea"
                            : party.StringId.IndexOf("_mixed_", StringComparison.Ordinal) >= 0
                                ? "mixed"
                                : "land";
                    LaunchPatrol(party, settlement, island, role);
                }
            }

            // In 1.5.6 visibility itself is the native saved discovery state.
            // Retain it here; discovery sets it and dispatches the event once.
        }

        private void DeactivateInvalidHideouts()
        {
            foreach (string id in CandidatePriority)
            {
                HideoutState state = GetState(id);
                if (state != HideoutState.Active &&
                    state != HideoutState.Grace)
                {
                    continue;
                }

                Settlement settlement = Settlement.Find(id);
                if (IsCandidateNavigationValid(settlement))
                {
                    continue;
                }

                _hideoutStates[id] = (int)HideoutState.Dormant;
                ApplyCandidateActivation(id, HideoutState.Dormant);
                RemoveIslandHideoutParties(id);
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Deactivated unreachable " +
                    "island hideout " + id + ".");
            }
        }

        private static bool IsCandidateNavigationValid(Settlement settlement)
        {
            return settlement?.Hideout != null &&
                   IsUsableHideoutPosition(settlement.Position) &&
                   IsUsableHideoutPosition(settlement.GatePosition);
        }

        private static MobileParty CreateManagedParty(
            Settlement hideout,
            string partyId)
        {
            if (MobileParty.AllBanditParties.Any(party => party.StringId == partyId))
            {
                return null;
            }

            Clan clan = Clan.FindFirst(
                candidate =>
                    candidate.StringId == "northern_pirates" ||
                    candidate.StringId == "southern_pirates");
            PartyTemplateObject template =
                Campaign.Current.ObjectManager.GetObject<PartyTemplateObject>(
                    "northern_pirates_template") ??
                clan?.DefaultPartyTemplate;
            if (clan == null || template == null)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Could not resolve native Sea Raider data.");
                return null;
            }

            MobileParty party = BanditPartyComponent.CreateBanditParty(
                partyId,
                clan,
                hideout.Hideout,
                false,
                template,
                hideout.GatePosition);
            party.ActualClan = clan;
            party.SetLandNavigationAccess(true);
            party.Aggressiveness = 0.9f;
            party.Party.SetCustomName(new TextObject("Island Sea Raiders"));
            party.InitializePartyTrade(
                Math.Max(100, party.MemberRoster.TotalManCount * 10));
            party.Party.SetVisualAsDirty();
            EnterSettlementAction.ApplyForParty(party, hideout);
            return party;
        }

        private static void LaunchPatrol(
            MobileParty party,
            Settlement home,
            IslandRecord island,
            string role)
        {
            LeaveSettlementAction.ApplyForParty(party);
            party.Position = new CampaignVec2(
                island.ClampInside(home.Position.ToVec2(), 1f),
                true);

            if (role == "sea")
            {
                SendToSeaPatrol(party, island);
                return;
            }

            Vec2 offset = role == "mixed"
                ? island.Perpendicular * Math.Min(2.5f, island.Width * 0.25f)
                : island.Axis * Math.Min(3f, island.Length * 0.25f);
            CampaignVec2 target = new CampaignVec2(
                island.ClampInside(island.Center + offset, 0.8f),
                true);
            party.SetMovePatrolAroundPoint(
                target,
                MobileParty.NavigationType.Default);
        }

        private static void SendToSeaPatrol(
            MobileParty party,
            IslandRecord island)
        {
            Vec2 preferred = island.Center + island.Perpendicular * 50f;
            if (IslandNavMeshOverlay.TryGetShoreRoute(
                    island,
                    preferred,
                    out CampaignVec2 seaPosition,
                    out CampaignVec2 _))
            {
                party.SetMoveGoToPoint(
                    seaPosition,
                    MobileParty.NavigationType.Naval);
            }
        }

        private static void MaintainPatrolRole(
            MobileParty party,
            IslandRecord island)
        {
            string id = party.StringId ?? string.Empty;
            bool wantsSea =
                id.IndexOf("_sea_", StringComparison.Ordinal) >= 0 ||
                (id.IndexOf("_mixed_", StringComparison.Ordinal) >= 0 &&
                 CampaignTime.Now.GetDayOfYear % 6 >= 3);
            if (wantsSea)
            {
                Vec2 preferred = island.Center + island.Perpendicular * 50f;
                if (!IslandNavMeshOverlay.TryGetShoreRoute(
                        island,
                        preferred,
                        out CampaignVec2 seaPosition,
                        out CampaignVec2 _))
                {
                    return;
                }

                if (party.IsCurrentlyAtSea)
                {
                    party.SetMovePatrolAroundPoint(
                        seaPosition,
                        MobileParty.NavigationType.Naval);
                }
                else if (!IslandTravelController.IsManagedParty(party))
                {
                    party.SetMoveGoToPoint(
                        seaPosition,
                        MobileParty.NavigationType.Naval);
                }

                return;
            }

            CampaignVec2 landTarget = new CampaignVec2(
                island.ClampInside(island.Center, 1f),
                true);
            if (party.IsCurrentlyAtSea)
            {
                if (!IslandTravelController.IsManagedParty(party))
                {
                    party.SetMoveGoToPoint(
                        landTarget,
                        MobileParty.NavigationType.Default);
                }
            }
            else
            {
                party.SetMovePatrolAroundPoint(
                    landTarget,
                    MobileParty.NavigationType.Default);
            }
        }

        private static void DirectPatrolHome(
            MobileParty party,
            Settlement home,
            IslandRecord island)
        {
            if (party.IsCurrentlyAtSea)
            {
                if (IslandNavMeshOverlay.TryGetShoreRoute(
                        island,
                        party.Position.ToVec2(),
                        out CampaignVec2 seaPosition,
                        out CampaignVec2 _))
                {
                    party.SetMovePatrolAroundPoint(
                        seaPosition,
                        MobileParty.NavigationType.Naval);
                }

                return;
            }

            party.SetMovePatrolAroundSettlement(
                home,
                MobileParty.NavigationType.Default,
                false);
        }

        private static void RemoveIslandHideoutParties(string hideoutId)
        {
            List<MobileParty> parties = MobileParty.AllBanditParties
                .Where(
                    party =>
                    {
                        string partyId = party.StringId;
                        string homeId = party.HomeSettlement?.StringId;
                        return string.Equals(
                                   homeId,
                                   hideoutId,
                                   StringComparison.Ordinal) ||
                               (partyId != null &&
                                (partyId.StartsWith(
                                     RaiderPrefix + hideoutId + "_",
                                     StringComparison.Ordinal) ||
                                 partyId.StartsWith(
                                     GuardPrefix + hideoutId + "_",
                                     StringComparison.Ordinal)));
                    })
                .ToList();
            _suppressHideoutEvents = true;
            try
            {
                foreach (MobileParty party in parties)
                {
                    if (party.MapEvent == null)
                    {
                        DestroyPartyAction.Apply(null, party);
                    }
                }
            }
            finally
            {
                _suppressHideoutEvents = false;
            }
        }

        private bool HasPlayerSettlementOnSameIsland(Settlement candidate)
        {
            if (!IslandNavMeshOverlay.TryGetIsland(
                    candidate.Position,
                    out IslandRecord candidateIsland))
            {
                return false;
            }

            return Settlement.All.Any(
                settlement =>
                    IslandSettlementIntegration.IsPlayerSettlement(settlement) &&
                    IslandNavMeshOverlay.TryGetIsland(
                        settlement.Position,
                        out IslandRecord playerIsland) &&
                    playerIsland.Id == candidateIsland.Id);
        }

        internal static bool IsCandidate(Settlement settlement)
        {
            return settlement?.StringId != null &&
                   settlement.StringId.StartsWith(
                       HideoutPrefix,
                       StringComparison.Ordinal);
        }

        private HideoutState GetState(string id)
        {
            return _hideoutStates.TryGetValue(id, out int value)
                ? (HideoutState)value
                : HideoutState.Dormant;
        }

        private void SetState(string id, HideoutState state)
        {
            _hideoutStates[id] = (int)state;
            ApplyCandidateActivation(
                id,
                _piratesEnabled ? state : HideoutState.Dormant);
        }

        private static void ApplyCandidateActivation(
            string id,
            HideoutState state)
        {
            Settlement settlement = Settlement.Find(id);
            if (settlement == null)
            {
                return;
            }

            settlement.IsActive = state == HideoutState.Active;
            if (!settlement.IsActive)
            {
                settlement.IsVisible = false;
            }
        }

        private enum HideoutState
        {
            Dormant = 0,
            Active = 1,
            Grace = 2,
            SettlementSuppressed = 3
        }
    }

    [HarmonyPatch(
        typeof(SettlementHelper),
        nameof(SettlementHelper.FindNearestTownToSettlement))]
    internal static class IslandHideoutNearestTownPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            Settlement fromSettlement,
            Func<Settlement, bool> condition,
            ref Town __result)
        {
            if (__result != null ||
                !IslandFeatureBehavior.IsCandidate(fromSettlement))
            {
                return;
            }

            Vec2 source = fromSettlement.Position.ToVec2();
            float nearestDistanceSquared = float.MaxValue;
            Town nearest = null;
            foreach (Town town in Town.AllTowns)
            {
                Settlement candidate = town?.Settlement;
                if (candidate == null ||
                    !candidate.IsActive ||
                    town.OwnerClan?.Leader == null ||
                    (condition != null && !condition(candidate)))
                {
                    continue;
                }

                Vec2 delta = candidate.Position.ToVec2() - source;
                float distanceSquared = delta.LengthSquared;
                if (distanceSquared >= nearestDistanceSquared)
                {
                    continue;
                }

                nearestDistanceSquared = distanceSquared;
                nearest = town;
            }

            if (nearest == null)
            {
                return;
            }

            __result = nearest;
            Debug.Print(
                "[BLWarsailsInhabitableIslands] Used " +
                nearest.Settlement.StringId +
                " as the post-hideout town fallback for " +
                fromSettlement.StringId + ".");
        }
    }

    [HarmonyPatch]
    internal static class ManagedIslandRaiderVanillaPatrolPatch
    {
        private static MethodBase TargetMethod()
        {
            Type type = AccessTools.TypeByName(
                "NavalDLC.CampaignBehaviors.PiratesCampaignBehavior");
            return type == null ? null : AccessTools.Method(type, "IsPirateParty");
        }

        [HarmonyPostfix]
        private static void Postfix(MobileParty mobileParty, ref bool __result)
        {
            if (__result && IslandFeatureBehavior.IsManagedParty(mobileParty))
            {
                __result = false;
            }
        }
    }

    [HarmonyPatch(
        typeof(SettlementHelper),
        nameof(SettlementHelper.FindNearestSettlementToMobileParty))]
    internal static class ManagedIslandRaiderNearestSettlementPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            MobileParty mobileParty,
            Func<Settlement, bool> condition,
            ref Settlement __result)
        {
            if (__result != null ||
                !IslandFeatureBehavior.IsIslandRaiderParty(mobileParty))
            {
                return;
            }

            Vec2 partyPosition = mobileParty.Position.ToVec2();
            float nearestDistanceSquared = float.MaxValue;
            Settlement nearest = null;
            foreach (Settlement candidate in Settlement.All)
            {
                if (candidate == null ||
                    !candidate.IsActive ||
                    (!candidate.IsTown && !candidate.IsVillage) ||
                    (condition != null && !condition(candidate)))
                {
                    continue;
                }

                Vec2 delta = candidate.Position.ToVec2() - partyPosition;
                float distanceSquared = delta.LengthSquared;
                if (distanceSquared >= nearestDistanceSquared)
                {
                    continue;
                }

                nearestDistanceSquared = distanceSquared;
                nearest = candidate;
            }

            if (nearest == null)
            {
                return;
            }

            __result = nearest;
            Debug.Print(
                "[BLWarsailsInhabitableIslands] Used " +
                nearest.StringId +
                " as the encounter market fallback for " +
                mobileParty.StringId + ".");
        }
    }
}
