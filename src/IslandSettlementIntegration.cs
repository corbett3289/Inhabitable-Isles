using System;
using System.Collections.Generic;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandSettlementIntegration
    {
        private const string PlayerSettlementPrefix = "player_settlement_";
        private static readonly HashSet<string> LoggedPorts = new HashSet<string>();

        internal static void RepairAll()
        {
            if (Campaign.Current == null || IslandNavMeshOverlay.Islands.Count == 0)
            {
                return;
            }

            foreach (Settlement settlement in Settlement.All)
            {
                if (IsPlayerSettlement(settlement) &&
                    IslandNavMeshOverlay.TryGetIsland(settlement.Position, out IslandRecord island))
                {
                    RepairPort(settlement, island);
                    RepairSiegeCampPositions(settlement, island);
                }
            }

            IslandVillageLogistics.RepairTradeBounds();
        }

        internal static bool IsPlayerSettlement(Settlement settlement)
        {
            return settlement?.StringId != null &&
                   settlement.StringId.StartsWith(
                       PlayerSettlementPrefix,
                       StringComparison.Ordinal);
        }

        internal static bool IsIslandSettlement(
            Settlement settlement,
            out IslandRecord island)
        {
            island = null;
            return settlement != null &&
                   IslandNavMeshOverlay.TryGetIsland(settlement.Position, out island);
        }

        internal static void RepairPort(Settlement settlement, IslandRecord island)
        {
            if (settlement == null ||
                island == null ||
                Campaign.Current == null ||
                Campaign.Current.MapSceneWrapper == null)
            {
                return;
            }

            Vec2 preferred = GetPreferredPortDirection(settlement, island);
            if (!IslandNavMeshOverlay.TryGetShoreRoute(
                    island,
                    preferred,
                    out CampaignVec2 seaPosition,
                    out CampaignVec2 _))
            {
                return;
            }

            if (!settlement.HasPort ||
                settlement.PortPosition == null ||
                !settlement.PortPosition.IsValid() ||
                DistanceSquared(settlement.PortPosition.ToVec2(), seaPosition.ToVec2()) > 0.25f)
            {
                settlement.SetPortPosition(seaPosition);
                if (LoggedPorts.Add(settlement.StringId))
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Assigned an island port to " +
                        settlement.StringId + ".");
                }
            }
        }

        internal static void RepairSiegeCampPositions(
            Settlement settlement,
            IslandRecord island)
        {
            Town town = settlement?.Town;
            if (town == null || !settlement.IsFortification || island == null)
            {
                return;
            }

            MatrixFrame[] positions = GenerateLandCampPositions(settlement, island, 8);
            if (positions.Length < 4)
            {
                return;
            }

            MatrixFrame[] first = new MatrixFrame[4];
            MatrixFrame[] second = new MatrixFrame[4];
            for (int index = 0; index < 4; index++)
            {
                first[index] = positions[index];
                second[index] = positions[(index + 4) % positions.Length];
            }

            town.BesiegerCampPositions1 = first;
            town.BesiegerCampPositions2 = second;
        }

        private static MatrixFrame[] GenerateLandCampPositions(
            Settlement settlement,
            IslandRecord island,
            int count)
        {
            List<MatrixFrame> frames = new List<MatrixFrame>();
            Vec2 center = settlement.Position.ToVec2();
            float radius = Math.Max(
                1.25f,
                Math.Min(5.5f, Math.Min(island.Length, island.Width) * 0.34f));

            for (int index = 0; index < 48 && frames.Count < count; index++)
            {
                float angle = (float)(Math.PI * 2d * index / 48d);
                Vec2 candidate = center + new Vec2(
                    (float)Math.Cos(angle) * radius,
                    (float)Math.Sin(angle) * radius);
                candidate = island.ClampInside(candidate, 0.65f);
                if (!IslandNavMeshOverlay.TryCreateLandPosition(
                        candidate,
                        out CampaignVec2 landPosition) ||
                    IsTooClose(frames, landPosition.ToVec2(), 0.75f))
                {
                    continue;
                }

                MatrixFrame frame = MatrixFrame.Identity;
                frame.origin = new Vec3(
                    candidate.X,
                    candidate.Y,
                    IslandNavMeshOverlay.GetTerrainHeight(candidate, island.Height) + 0.08f);
                Vec2 facing = center - candidate;
                float facingLength = facing.Length;
                if (facingLength > 0.001f)
                {
                    facing /= facingLength;
                    frame.rotation.f = new Vec3(facing.X, facing.Y, 0f);
                    frame.rotation.s = new Vec3(-facing.Y, facing.X, 0f);
                    frame.rotation.u = new Vec3(0f, 0f, 1f);
                }

                frames.Add(frame);
            }

            return frames.ToArray();
        }

        private static bool IsTooClose(
            List<MatrixFrame> frames,
            Vec2 position,
            float minimumDistance)
        {
            float minimumDistanceSquared = minimumDistance * minimumDistance;
            foreach (MatrixFrame frame in frames)
            {
                Vec2 delta = frame.origin.AsVec2 - position;
                if (delta.LengthSquared < minimumDistanceSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private static Vec2 GetPreferredPortDirection(
            Settlement settlement,
            IslandRecord island)
        {
            if (settlement?.GatePosition != null &&
                settlement.GatePosition.IsValid() &&
                DistanceSquared(
                    settlement.GatePosition.ToVec2(),
                    island.Center) > 0.0625f)
            {
                return settlement.GatePosition.ToVec2();
            }

            if (settlement?.Position != null &&
                settlement.Position.IsValid() &&
                DistanceSquared(
                    settlement.Position.ToVec2(),
                    island.Center) > 0.0625f)
            {
                return settlement.Position.ToVec2();
            }

            if (settlement?.PortPosition != null &&
                settlement.PortPosition.IsValid())
            {
                return settlement.PortPosition.ToVec2();
            }

            return GetPreferredCoastDirection(island);
        }

        private static Vec2 GetPreferredCoastDirection(IslandRecord island)
        {
            Vec2 mapCenter = new Vec2(330f, 400f);
            Vec2 direction = mapCenter - island.Center;
            if (direction.LengthSquared < 0.01f)
            {
                direction = island.Axis;
            }

            return island.Center + direction;
        }

        private static float DistanceSquared(Vec2 left, Vec2 right)
        {
            Vec2 delta = left - right;
            return delta.LengthSquared;
        }
    }

    [HarmonyPatch(
        typeof(AiHelper),
        nameof(AiHelper.GetBestNavigationTypeAndAdjustedDistanceOfSettlementForMobileParty))]
    internal static class IslandSettlementNavigationScorePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            MobileParty mobileParty,
            Settlement settlement,
            bool isTargetingPort,
            ref MobileParty.NavigationType bestNavigationType,
            ref float bestNavigationDistance,
            ref bool isFromPort)
        {
            if (mobileParty == null ||
                !IslandSettlementIntegration.IsIslandSettlement(
                    settlement,
                    out IslandRecord island))
            {
                return;
            }

            bool onSameIsland =
                IslandNavMeshOverlay.TryGetIsland(
                    mobileParty.Position,
                    out IslandRecord currentIsland) &&
                currentIsland.Id == island.Id;
            if (!onSameIsland && !mobileParty.HasNavalNavigationCapability)
            {
                bestNavigationType = MobileParty.NavigationType.None;
                bestNavigationDistance = float.MaxValue;
                isFromPort = false;
                return;
            }

            if ((bestNavigationType == MobileParty.NavigationType.None ||
                 bestNavigationDistance >= Campaign.MapDiagonal * 5f) &&
                IslandTravelController.TryGetIslandDistance(
                    mobileParty,
                    island,
                    out float distance))
            {
                bestNavigationType = onSameIsland
                    ? MobileParty.NavigationType.Default
                    : MobileParty.NavigationType.All;
                bestNavigationDistance = distance;
                isFromPort =
                    mobileParty.CurrentSettlement != null &&
                    mobileParty.CurrentSettlement.HasPort;
            }
        }
    }

    [HarmonyPatch(
        typeof(AiHelper),
        nameof(AiHelper.GetBestNavigationTypeAndDistanceOfMobilePartyForMobileParty))]
    internal static class IslandPartyNavigationScorePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            MobileParty mobileParty,
            MobileParty toMobileParty,
            ref MobileParty.NavigationType bestNavigationType,
            ref float bestNavigationDistance)
        {
            if (mobileParty == null || toMobileParty == null)
            {
                return;
            }

            bool sourceOnIsland =
                IslandNavMeshOverlay.TryGetIsland(
                    mobileParty.Position,
                    out IslandRecord sourceIsland);
            bool targetOnIsland =
                IslandNavMeshOverlay.TryGetIsland(
                    toMobileParty.Position,
                    out IslandRecord targetIsland);
            if (!sourceOnIsland && !targetOnIsland)
            {
                return;
            }

            bool sameIsland =
                sourceOnIsland &&
                targetOnIsland &&
                sourceIsland.Id == targetIsland.Id;
            if (!sameIsland && !mobileParty.HasNavalNavigationCapability)
            {
                return;
            }

            if (bestNavigationType == MobileParty.NavigationType.None ||
                bestNavigationDistance >= Campaign.MapDiagonal * 5f)
            {
                Vec2 delta =
                    mobileParty.Position.ToVec2() -
                    toMobileParty.Position.ToVec2();
                bestNavigationDistance = delta.Length;
                bestNavigationType = sameIsland
                    ? MobileParty.NavigationType.Default
                    : MobileParty.NavigationType.All;
            }
        }
    }
}
