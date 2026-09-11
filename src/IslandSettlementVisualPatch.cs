using System;
using System.Collections.Generic;
using System.Reflection;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;
using SandBox.View.Map.Visuals;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandSettlementVisualRepair
    {
        private const string SiegeStagingEntityName = "map_icon_siege";
        private const string RepairedTag = "blwii_grounded_attacker_siege";
        private const float CandidateStep = 0.45f;
        private const int RotationSteps = 16;

        private static readonly HashSet<string> LoggedSettlementIds =
            new HashSet<string>(StringComparer.Ordinal);

        private static bool _loggedPreviewRepair;

        internal static void RepairSettlementVisual(
            Settlement settlement,
            GameEntity visualRoot)
        {
            if (!IslandSettlementIntegration.IsPlayerSettlement(settlement) ||
                visualRoot == null ||
                !IslandNavMeshOverlay.TryGetIsland(
                    settlement.Position,
                    out IslandRecord island))
            {
                return;
            }

            int repaired = GroundAttackerSiegeStaging(
                visualRoot,
                island,
                settlement.Position.ToVec2(),
                true);
            if (repaired > 0 && LoggedSettlementIds.Add(settlement.StringId))
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Grounded " + repaired +
                    " attacker siege staging props for " + settlement.StringId +
                    ". Siege visuals and mechanics remain active.");
            }
        }

        internal static void RepairPlacementPreview(GameEntity visualRoot)
        {
            if (visualRoot == null)
            {
                return;
            }

            Vec2 position = visualRoot.GetGlobalFrame().origin.AsVec2;
            if (!IslandNavMeshOverlay.TryGetIsland(position, out IslandRecord island))
            {
                return;
            }

            int repaired = GroundAttackerSiegeStaging(
                visualRoot,
                island,
                position,
                false);
            if (repaired > 0 && !_loggedPreviewRepair)
            {
                _loggedPreviewRepair = true;
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Grounded attacker siege staging in the Player Settlement island preview.");
            }
        }

        private static int GroundAttackerSiegeStaging(
            GameEntity visualRoot,
            IslandRecord island,
            Vec2 settlementCenter,
            bool markAsRepaired)
        {
            int repaired = 0;
            foreach (GameEntity stagingEntity in FindSiegeStagingEntities(visualRoot))
            {
                if (markAsRepaired && stagingEntity.HasTag(RepairedTag))
                {
                    continue;
                }

                repaired += GroundStagingEntity(
                    stagingEntity,
                    island,
                    settlementCenter);
                if (markAsRepaired)
                {
                    stagingEntity.AddTag(RepairedTag);
                }
            }

            return repaired;
        }

        private static int GroundStagingEntity(
            GameEntity stagingEntity,
            IslandRecord island,
            Vec2 settlementCenter)
        {
            List<StagingElement> elements = GetAttackerStagingElements(stagingEntity);
            if (elements.Count == 0)
            {
                return 0;
            }

            Vec2 sourceCenter = GetElementCenter(elements);
            List<GroundCandidate> candidates = BuildGroundCandidates(island);
            if (candidates.Count == 0)
            {
                return 0;
            }

            LayoutChoice layout = FindBestLayout(
                elements,
                candidates,
                island,
                settlementCenter,
                sourceCenter);
            elements.Sort((left, right) => right.Priority.CompareTo(left.Priority));

            int repaired = 0;
            List<ReservedPosition> reserved = new List<ReservedPosition>();
            foreach (StagingElement element in elements)
            {
                Vec2 relative = element.SourceFrame.origin.AsVec2 - sourceCenter;
                Vec2 desired = layout.Center + Rotate(relative, layout.Rotation);
                float supportRadius = GetSupportRadius(element);
                GroundCandidate ground;
                if (!TryGetSupportedGround(
                        desired,
                        supportRadius,
                        island,
                        true,
                        out ground) ||
                    !IsAvailable(
                        ground.Position,
                        GetMinimumSpacing(element),
                        reserved))
                {
                    if (!TryFindFallbackGround(
                            desired,
                            element,
                            candidates,
                            island,
                            reserved,
                            out ground))
                    {
                        continue;
                    }
                }

                MatrixFrame frame = element.SourceFrame;
                frame.rotation.RotateAboutUp(layout.Rotation);
                Vec2 facing = element.FacesSettlement
                    ? settlementCenter - ground.Position
                    : frame.rotation.f.AsVec2;
                AlignFrameToGround(ref frame, ground, facing);
                frame.origin = new Vec3(
                    ground.Position.X,
                    ground.Position.Y,
                    ground.Height + 0.025f);
                element.Entity.SetGlobalFrame(in frame, true);

                reserved.Add(new ReservedPosition(
                    ground.Position,
                    GetMinimumSpacing(element)));
                repaired++;
            }

            return repaired;
        }

        private static List<StagingElement> GetAttackerStagingElements(
            GameEntity stagingEntity)
        {
            List<GameEntity> descendants = new List<GameEntity>();
            stagingEntity.GetChildrenRecursive(ref descendants);

            List<StagingElement> elements = new List<StagingElement>();
            foreach (GameEntity entity in descendants)
            {
                if (!IsAttackerStagingElement(entity) ||
                    HasSelectedAncestor(entity, stagingEntity))
                {
                    continue;
                }

                string name = GetEntityName(entity);
                bool isEngine = name.StartsWith(
                    "attacker_",
                    StringComparison.Ordinal);
                elements.Add(new StagingElement(
                    entity,
                    entity.GetGlobalFrame(),
                    isEngine,
                    isEngine ? 2 : 1));
            }

            return elements;
        }

        private static bool HasSelectedAncestor(
            GameEntity entity,
            GameEntity stagingEntity)
        {
            GameEntity parent = entity.Parent;
            while (parent != null && parent != stagingEntity)
            {
                if (IsAttackerStagingElement(parent))
                {
                    return true;
                }

                parent = parent.Parent;
            }

            return false;
        }

        private static bool IsAttackerStagingElement(GameEntity entity)
        {
            if (entity == null)
            {
                return false;
            }

            string name = GetEntityName(entity);
            return name.StartsWith("attacker_", StringComparison.Ordinal) ||
                   name.StartsWith("map_icon_siege_camp_", StringComparison.Ordinal) ||
                   name.StartsWith("map_icons_market_tent", StringComparison.Ordinal) ||
                   name.StartsWith("map_barrel", StringComparison.Ordinal) ||
                   entity.HasTag("siege_preparation");
        }

        private static string GetEntityName(GameEntity entity)
        {
            string name = entity?.Name;
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }

            return entity?.GetOldPrefabName() ?? string.Empty;
        }

        private static Vec2 GetElementCenter(List<StagingElement> elements)
        {
            Vec2 total = Vec2.Zero;
            foreach (StagingElement element in elements)
            {
                total += element.SourceFrame.origin.AsVec2;
            }

            return total / elements.Count;
        }

        private static List<GroundCandidate> BuildGroundCandidates(IslandRecord island)
        {
            List<GroundCandidate> candidates = new List<GroundCandidate>();
            float halfLength = Math.Max(0.1f, island.Length * 0.5f - 0.2f);
            float halfWidth = Math.Max(0.1f, island.Width * 0.5f - 0.2f);

            for (float along = -halfLength; along <= halfLength + 0.01f; along += CandidateStep)
            {
                for (float across = -halfWidth; across <= halfWidth + 0.01f; across += CandidateStep)
                {
                    Vec2 position =
                        island.Center +
                        island.Axis * along +
                        island.Perpendicular * across;
                    if (TryGetSupportedGround(
                            position,
                            0f,
                            island,
                            false,
                            out GroundCandidate candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            if (candidates.Count == 0 &&
                TryGetSupportedGround(
                    island.Center,
                    0f,
                    island,
                    false,
                    out GroundCandidate center))
            {
                candidates.Add(center);
            }

            return candidates;
        }

        private static LayoutChoice FindBestLayout(
            List<StagingElement> elements,
            List<GroundCandidate> candidates,
            IslandRecord island,
            Vec2 settlementCenter,
            Vec2 sourceCenter)
        {
            LayoutChoice best = new LayoutChoice(
                candidates[0].Position,
                0f,
                float.MinValue);
            float preferredDistance = Math.Max(
                1.5f,
                Math.Min(5f, Math.Min(island.Length, island.Width) * 0.42f));

            foreach (GroundCandidate candidate in candidates)
            {
                for (int rotationIndex = 0; rotationIndex < RotationSteps; rotationIndex++)
                {
                    float rotation =
                        (float)(Math.PI * 2d * rotationIndex / RotationSteps);
                    int grounded = 0;
                    int support = 0;
                    foreach (StagingElement element in elements)
                    {
                        Vec2 relative =
                            element.SourceFrame.origin.AsVec2 - sourceCenter;
                        Vec2 position = candidate.Position + Rotate(relative, rotation);
                        if (TryGetSupportedGround(
                                position,
                                GetSupportRadius(element),
                                island,
                                true,
                                out GroundCandidate _,
                                out int elementSupport))
                        {
                            grounded++;
                        }

                        support += elementSupport;
                    }

                    float distanceToSettlement =
                        (candidate.Position - settlementCenter).Length;
                    float preferredDistancePenalty =
                        Math.Abs(distanceToSettlement - preferredDistance);
                    float movementPenalty =
                        (candidate.Position - sourceCenter).Length;
                    float score =
                        grounded * 100000f +
                        support * 100f -
                        preferredDistancePenalty * 5f -
                        movementPenalty * 0.05f;
                    if (score > best.Score)
                    {
                        best = new LayoutChoice(
                            candidate.Position,
                            rotation,
                            score);
                    }
                }
            }

            return best;
        }

        private static bool TryFindFallbackGround(
            Vec2 desired,
            StagingElement element,
            List<GroundCandidate> candidates,
            IslandRecord island,
            List<ReservedPosition> reserved,
            out GroundCandidate ground)
        {
            float supportRadius = GetSupportRadius(element);
            float spacing = GetMinimumSpacing(element);
            if (TryChooseFallback(
                    desired,
                    supportRadius,
                    spacing,
                    candidates,
                    island,
                    reserved,
                    true,
                    out ground) ||
                TryChooseFallback(
                    desired,
                    supportRadius,
                    spacing * 0.55f,
                    candidates,
                    island,
                    reserved,
                    false,
                    out ground) ||
                TryChooseFallback(
                    desired,
                    0f,
                    0f,
                    candidates,
                    island,
                    reserved,
                    false,
                    out ground))
            {
                return true;
            }

            ground = default(GroundCandidate);
            return false;
        }

        private static bool TryChooseFallback(
            Vec2 desired,
            float supportRadius,
            float spacing,
            List<GroundCandidate> candidates,
            IslandRecord island,
            List<ReservedPosition> reserved,
            bool requireFullSupport,
            out GroundCandidate ground)
        {
            bool found = false;
            float bestScore = float.MaxValue;
            ground = default(GroundCandidate);
            foreach (GroundCandidate candidate in candidates)
            {
                if (!IsAvailable(candidate.Position, spacing, reserved) ||
                    !TryGetSupportedGround(
                        candidate.Position,
                        supportRadius,
                        island,
                        requireFullSupport,
                        out GroundCandidate supported))
                {
                    continue;
                }

                float score = (candidate.Position - desired).LengthSquared;
                if (score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    ground = supported;
                }
            }

            return found;
        }

        private static bool TryGetSupportedGround(
            Vec2 position,
            float radius,
            IslandRecord island,
            bool requireFullSupport,
            out GroundCandidate ground)
        {
            return TryGetSupportedGround(
                position,
                radius,
                island,
                requireFullSupport,
                out ground,
                out int _);
        }

        private static bool TryGetSupportedGround(
            Vec2 position,
            float radius,
            IslandRecord island,
            bool requireFullSupport,
            out GroundCandidate ground,
            out int support)
        {
            ground = default(GroundCandidate);
            support = 0;
            if (!IsRaisedIslandPoint(
                    position,
                    island,
                    out float height,
                    out Vec3 normal))
            {
                return false;
            }

            support = 1;
            if (radius > 0.01f)
            {
                Vec2[] offsets =
                {
                    new Vec2(radius, 0f),
                    new Vec2(-radius, 0f),
                    new Vec2(0f, radius),
                    new Vec2(0f, -radius)
                };
                foreach (Vec2 offset in offsets)
                {
                    if (IsRaisedIslandPoint(
                        position + offset,
                        island,
                        out float _,
                        out Vec3 _))
                    {
                        support++;
                    }
                }
            }

            if (requireFullSupport && support < 4)
            {
                return false;
            }

            ground = new GroundCandidate(position, height, normal);
            return true;
        }

        private static bool IsRaisedIslandPoint(
            Vec2 position,
            IslandRecord island,
            out float height,
            out Vec3 normal)
        {
            height = float.NaN;
            normal = Vec3.Up;
            return island.Contains(position, 0f) &&
                   IslandNavMeshOverlay.TryGetRaisedTerrainHeightAndNormal(
                       position,
                       out height,
                       out normal);
        }

        private static bool IsAvailable(
            Vec2 position,
            float spacing,
            List<ReservedPosition> reserved)
        {
            foreach (ReservedPosition used in reserved)
            {
                float required = Math.Max(spacing, used.Spacing);
                if ((position - used.Position).LengthSquared < required * required)
                {
                    return false;
                }
            }

            return true;
        }

        private static float GetSupportRadius(StagingElement element)
        {
            return element.FacesSettlement ? 0.32f : 0.2f;
        }

        private static float GetMinimumSpacing(StagingElement element)
        {
            return element.FacesSettlement ? 0.62f : 0.3f;
        }

        private static void AlignFrameToGround(
            ref MatrixFrame frame,
            GroundCandidate ground,
            Vec2 facing)
        {
            Vec3 scale = frame.rotation.GetScaleVector();
            Vec3 up = ground.Normal;
            if (!up.IsValid || up.LengthSquared < 0.01f)
            {
                up = Vec3.Up;
            }
            else
            {
                up.Normalize();
            }

            if (facing.LengthSquared < 0.001f)
            {
                facing = Vec2.Forward;
            }
            else
            {
                facing /= facing.Length;
            }

            Vec3 forward = new Vec3(facing.X, facing.Y, 0f);
            forward -= up * Dot(forward, up);
            if (forward.LengthSquared < 0.001f)
            {
                forward = new Vec3(-up.Y, up.X, 0f);
            }

            forward.Normalize();
            Vec3 side = Cross(forward, up);
            side.Normalize();
            forward = Cross(up, side);
            forward.Normalize();

            frame.rotation.s = side * Math.Max(0.001f, Math.Abs(scale.X));
            frame.rotation.f = forward * Math.Max(0.001f, Math.Abs(scale.Y));
            frame.rotation.u = up * Math.Max(0.001f, Math.Abs(scale.Z));
        }

        private static Vec2 Rotate(Vec2 value, float angle)
        {
            float cosine = (float)Math.Cos(angle);
            float sine = (float)Math.Sin(angle);
            return new Vec2(
                value.X * cosine - value.Y * sine,
                value.X * sine + value.Y * cosine);
        }

        private static float Dot(Vec3 left, Vec3 right)
        {
            return left.X * right.X + left.Y * right.Y + left.Z * right.Z;
        }

        private static Vec3 Cross(Vec3 left, Vec3 right)
        {
            return new Vec3(
                left.Y * right.Z - left.Z * right.Y,
                left.Z * right.X - left.X * right.Z,
                left.X * right.Y - left.Y * right.X);
        }

        private static IEnumerable<GameEntity> FindSiegeStagingEntities(
            GameEntity visualRoot)
        {
            List<GameEntity> entities = new List<GameEntity>();
            if (IsSiegeStagingEntity(visualRoot))
            {
                entities.Add(visualRoot);
            }

            visualRoot.GetChildrenRecursive(ref entities);
            foreach (GameEntity entity in entities)
            {
                if (IsSiegeStagingEntity(entity))
                {
                    yield return entity;
                }
            }
        }

        private static bool IsSiegeStagingEntity(GameEntity entity)
        {
            if (entity == null)
            {
                return false;
            }

            return string.Equals(
                       entity.Name,
                       SiegeStagingEntityName,
                       StringComparison.Ordinal) ||
                   string.Equals(
                       entity.GetOldPrefabName(),
                       SiegeStagingEntityName,
                       StringComparison.Ordinal);
        }

        private sealed class StagingElement
        {
            internal StagingElement(
                GameEntity entity,
                MatrixFrame sourceFrame,
                bool facesSettlement,
                int priority)
            {
                Entity = entity;
                SourceFrame = sourceFrame;
                FacesSettlement = facesSettlement;
                Priority = priority;
            }

            internal GameEntity Entity { get; }
            internal MatrixFrame SourceFrame { get; }
            internal bool FacesSettlement { get; }
            internal int Priority { get; }
        }

        private struct GroundCandidate
        {
            internal GroundCandidate(Vec2 position, float height, Vec3 normal)
            {
                Position = position;
                Height = height;
                Normal = normal;
            }

            internal Vec2 Position { get; }
            internal float Height { get; }
            internal Vec3 Normal { get; }
        }

        private struct LayoutChoice
        {
            internal LayoutChoice(Vec2 center, float rotation, float score)
            {
                Center = center;
                Rotation = rotation;
                Score = score;
            }

            internal Vec2 Center { get; }
            internal float Rotation { get; }
            internal float Score { get; }
        }

        private struct ReservedPosition
        {
            internal ReservedPosition(Vec2 position, float spacing)
            {
                Position = position;
                Spacing = spacing;
            }

            internal Vec2 Position { get; }
            internal float Spacing { get; }
        }
    }

    [HarmonyPatch(
        typeof(SettlementVisual),
        "PopulateSiegeEngineFrameListsFromChildren")]
    internal static class IslandSettlementSiegeStagingPatch
    {
        private static void Prefix(SettlementVisual __instance)
        {
            Settlement settlement = __instance?.MapEntity?.Settlement;
            IslandSettlementVisualRepair.RepairSettlementVisual(
                settlement,
                __instance?.StrategicEntity);
        }
    }

    [HarmonyPatch(typeof(SettlementVisual), "OnStartup")]
    internal static class IslandSettlementVisualStartupPatch
    {
        private static void Postfix(SettlementVisual __instance)
        {
            Settlement settlement = __instance?.MapEntity?.Settlement;
            if (IslandSettlementIntegration.IsPlayerSettlement(settlement) &&
                IslandNavMeshOverlay.TryGetIsland(
                    settlement.Position,
                    out IslandRecord island))
            {
                IslandSettlementVisualRepair.RepairSettlementVisual(
                    settlement,
                    __instance?.StrategicEntity);
                IslandSettlementIntegration.RepairSiegeCampPositions(
                    settlement,
                    island);
            }
        }
    }

    [HarmonyPatch(typeof(PlayerSettlementBehaviour), "UpdateSettlementVisualEntity")]
    internal static class IslandSettlementPlacementVisualPatch
    {
        private static readonly FieldInfo SettlementVisualEntityField =
            AccessTools.Field(
                typeof(PlayerSettlementBehaviour),
                "settlementVisualEntity");

        private static void Postfix(PlayerSettlementBehaviour __instance)
        {
            GameEntity visualRoot =
                SettlementVisualEntityField?.GetValue(__instance) as GameEntity;
            IslandSettlementVisualRepair.RepairPlacementPreview(visualRoot);
        }
    }
}
