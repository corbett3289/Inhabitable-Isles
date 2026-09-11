using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using Helpers;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    [HarmonyPatch(typeof(MapScene), nameof(MapScene.Load))]
    internal static class MapSceneLoadPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MapScene __instance)
        {
            try
            {
                IslandNavMeshOverlay.Apply(__instance);
            }
            catch (Exception ex)
            {
                Debug.Print("[BLWarsailsInhabitableIslands] Island navmesh overlay failed: " + ex);
            }
        }
    }

    internal static class IslandNavMeshOverlay
    {
        private const string SurfacePrefabName = "ship_connection_plank_navmesh";
        private const float GridStep = 1f;
        private const float MinimumHeightAboveWater = 0.45f;
        private const int MinimumComponentSamples = 4;
        private const int MaximumComponentSamples = 12000;
        private const int MaximumSurfaces = 128;

        private const float PrefabMinX = -3.349699f;
        private const float PrefabMaxX = 2.904692f;
        private const float PrefabMinY = -0.2512205f;
        private const float PrefabMaxY = 0.2393253f;
        private const float PrefabZ = -0.08992501f;

        private static readonly int[] NeighborX = { -1, 1, 0, 0 };
        private static readonly int[] NeighborY = { 0, 0, -1, 1 };

        private static Scene _appliedScene;
        private static MapScene _activeMapScene;
        private static List<IslandSurface> _activeSurfaces = new List<IslandSurface>();
        private static List<IslandRecord> _activeIslands = new List<IslandRecord>();
        private static bool _terrainRegistrationSucceeded;

        internal static IReadOnlyList<IslandRecord> Islands => _activeIslands;
        internal static MapScene ActiveMapScene => _activeMapScene;
        internal static int ImportedSurfaceCount => _activeSurfaces.Count;
        internal static bool HasImportedLandSurfaces =>
            _terrainRegistrationSucceeded && _activeSurfaces.Count > 0;

        public static void Apply(MapScene mapScene)
        {
            Scene scene = mapScene?.Scene;
            if (scene == null || ReferenceEquals(scene, _appliedScene))
            {
                return;
            }

            _appliedScene = scene;
            IslandTravelController.Reset();
            _activeMapScene = mapScene;
            _activeSurfaces = new List<IslandSurface>();
            _activeIslands = new List<IslandRecord>();
            _terrainRegistrationSucceeded = false;

            Dictionary<long, IslandSample> candidates;
            HashSet<long> raisedCells;
            int sampledCellCount;
            SampleCandidateCells(mapScene, scene, out candidates, out raisedCells, out sampledCellCount);

            List<IslandComponent> components = BuildComponents(candidates, raisedCells);
            List<IslandSurface> surfaces = new List<IslandSurface>();
            foreach (IslandComponent component in components)
            {
                if (surfaces.Count >= MaximumSurfaces)
                {
                    break;
                }

                IslandSurface rotatedSurface;
                IslandSurface axisAlignedSurface;
                bool hasRotatedSurface = TryFitSurface(component, out rotatedSurface);
                bool hasAxisAlignedSurface = TryFitAxisAlignedSurface(
                    component,
                    out axisAlignedSurface);

                if (hasRotatedSurface || hasAxisAlignedSurface)
                {
                    IslandSurface surface =
                        hasRotatedSurface &&
                        (!hasAxisAlignedSurface ||
                         rotatedSurface.Length * rotatedSurface.Width >=
                         axisAlignedSurface.Length * axisAlignedSurface.Width)
                            ? rotatedSurface
                            : axisAlignedSurface;
                    surfaces.Add(surface);
                }
            }

            int faceCountBefore = scene.GetNavMeshFaceCount();
            int existingSurfaceTerrainFaces = CountFacesWithTerrainId(scene, 0);
            List<IslandSurface> importedSurfaces = new List<IslandSurface>();
            foreach (IslandSurface surface in surfaces)
            {
                try
                {
                    MatrixFrame frame = CreateFrame(surface);
                    scene.ImportNavigationMeshPrefabWithFrame(SurfacePrefabName, frame);
                    importedSurfaces.Add(surface);
                }
                catch (Exception ex)
                {
                    Debug.Print("[BLWarsailsInhabitableIslands] Failed to add island land surface " + surface.Name + ": " + ex);
                }
            }

            if (importedSurfaces.Count > 0)
            {
                _terrainRegistrationSucceeded =
                    RegisterImportedSurfaceTerrainAsLand(scene);
                if (!_terrainRegistrationSucceeded)
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Imported island faces " +
                        "were disabled because terrain registration failed.");
                    importedSurfaces.Clear();
                }
            }

            _activeSurfaces = new List<IslandSurface>(importedSurfaces);
            foreach (IslandSurface surface in importedSurfaces)
            {
                _activeIslands.Add(ToRecord(surface));
            }
            IslandTreeDecorator.Apply(scene);

            for (int index = 0; index < importedSurfaces.Count; index++)
            {
                IslandSurface surface = importedSurfaces[index];
                int faceIndex = faceCountBefore + index;
                CampaignVec2 centerPosition = new CampaignVec2(surface.Center, true);
                PathFaceRecord centerFace = mapScene.GetFaceIndex(in centerPosition);
                bool navigationValid = centerPosition.IsValid() &&
                                       NavigationHelper.IsPositionValidForNavigationType(
                                           centerPosition,
                                           MobileParty.NavigationType.Default);
                Debug.Print(string.Format(
                    CultureInfo.InvariantCulture,
                    "[BLWarsailsInhabitableIslands] Added island land surface {0}: center={1:0.##},{2:0.##}; size={3:0.##}x{4:0.##}; samples={5}; importedFace={6}; group={7}; centerLandFaceValid={8}; navigationValid={9}.",
                    surface.Name,
                    surface.Center.X,
                    surface.Center.Y,
                    surface.Length,
                    surface.Width,
                    surface.SampleCount,
                    faceIndex,
                    scene.GetIdOfNavMeshFace(faceIndex),
                    centerFace.IsValid(),
                    navigationValid));
            }

            Debug.Print(string.Format(
                CultureInfo.InvariantCulture,
                "[BLWarsailsInhabitableIslands] Island scan complete: sampled={0}; raised-island-cells={1}; components={2}; importedSurfaces={3}; navmeshFaces={4}->{5}; preexistingTerrainId0Faces={6}.",
                sampledCellCount,
                candidates.Count,
                components.Count,
                importedSurfaces.Count,
                faceCountBefore,
                scene.GetNavMeshFaceCount(),
                existingSurfaceTerrainFaces));
        }

        private static bool TryFindShorelineNavalPosition(
            Vec2 landEdge,
            Vec2 outwardDirection,
            Vec2 preferredPosition,
            out CampaignVec2 seaPosition)
        {
            if (_activeMapScene == null || _activeMapScene.Scene == null)
            {
                seaPosition = CampaignVec2.Invalid;
                return false;
            }

            float directionLength = (float)Math.Sqrt(
                outwardDirection.X * outwardDirection.X +
                outwardDirection.Y * outwardDirection.Y);
            if (directionLength < 0.001f)
            {
                outwardDirection = new Vec2(1f, 0f);
            }
            else
            {
                outwardDirection /= directionLength;
            }

            float baseAngle = (float)Math.Atan2(outwardDirection.Y, outwardDirection.X);
            for (float radius = 0.25f; radius <= 48f; radius += 0.25f)
            {
                bool foundAtRadius = false;
                float bestScore = float.MaxValue;
                CampaignVec2 bestPosition = CampaignVec2.Invalid;
                for (int angleIndex = 0; angleIndex < 64; angleIndex++)
                {
                    float angle = baseAngle + (float)(Math.PI * 2d * angleIndex / 64d);
                    Vec2 candidateDirection = new Vec2(
                        (float)Math.Cos(angle),
                        (float)Math.Sin(angle));
                    Vec2 candidatePoint = landEdge + candidateDirection * radius;

                    CampaignVec2 candidate = new CampaignVec2(candidatePoint, false);
                    if (candidate.IsValid() &&
                        NavigationHelper.IsPositionValidForNavigationType(
                            candidate,
                            MobileParty.NavigationType.Naval))
                    {
                        float alignment = Dot(candidateDirection, outwardDirection);
                        float deltaX = candidatePoint.X - preferredPosition.X;
                        float deltaY = candidatePoint.Y - preferredPosition.Y;
                        float preferredDistanceSquared = deltaX * deltaX + deltaY * deltaY;
                        float score =
                            (1f - alignment) * 0.1f +
                            preferredDistanceSquared * 0.00001f;
                        if (score < bestScore)
                        {
                            foundAtRadius = true;
                            bestScore = score;
                            bestPosition = candidate;
                        }
                    }
                }

                if (foundAtRadius)
                {
                    seaPosition = bestPosition;
                    return true;
                }
            }

            seaPosition = CampaignVec2.Invalid;
            return false;
        }

        internal static bool IsImportedLandPosition(CampaignVec2 position)
        {
            if (position == null ||
                Campaign.Current == null ||
                Campaign.Current.MapSceneWrapper == null)
            {
                return false;
            }

            PathFaceRecord face = position.Face;
            return position.IsOnLand &&
                   face.IsValid() &&
                   face.FaceGroupIndex == 0 &&
                   IslandZoneSettings.Contains(position.ToVec2());
        }

        internal static bool TryGetIsland(CampaignVec2 position, out IslandRecord island)
        {
            if (position == null)
            {
                island = null;
                return false;
            }

            return TryGetIsland(position.ToVec2(), out island);
        }

        internal static bool TryGetIsland(Vec2 position, out IslandRecord island)
        {
            foreach (IslandRecord candidate in _activeIslands)
            {
                if (candidate.Contains(position))
                {
                    island = candidate;
                    return true;
                }
            }

            island = null;
            return false;
        }

        internal static bool TryGetNearestIsland(Vec2 position, float maximumDistance, out IslandRecord island)
        {
            island = null;
            float bestDistanceSquared = maximumDistance * maximumDistance;
            foreach (IslandRecord candidate in _activeIslands)
            {
                Vec2 delta = candidate.Center - position;
                float distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
                if (distanceSquared <= bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    island = candidate;
                }
            }

            return island != null;
        }

        internal static bool TryGetShoreRoute(
            IslandRecord island,
            Vec2 preferredPosition,
            out CampaignVec2 seaPosition,
            out CampaignVec2 landPosition)
        {
            if (island == null)
            {
                seaPosition = CampaignVec2.Invalid;
                landPosition = CampaignVec2.Invalid;
                return false;
            }

            Vec2 direction = preferredPosition - island.Center;
            Vec2 edgePoint = island.GetEdgePoint(direction);
            CampaignVec2 land = new CampaignVec2(edgePoint, true);
            if (!IsImportedLandPosition(land) ||
                !TryFindShorelineNavalPosition(
                    edgePoint,
                    direction,
                    preferredPosition,
                    out CampaignVec2 sea))
            {
                seaPosition = CampaignVec2.Invalid;
                landPosition = CampaignVec2.Invalid;
                return false;
            }

            seaPosition = sea;
            landPosition = land;
            return true;
        }

        internal static bool TryCreateLandPosition(Vec2 position, out CampaignVec2 landPosition)
        {
            landPosition = new CampaignVec2(position, true);
            return IsImportedLandPosition(landPosition);
        }

        internal static float GetTerrainHeight(Vec2 position, float fallback)
        {
            if (_activeMapScene == null)
            {
                return fallback;
            }

            _activeMapScene.GetTerrainHeightAndNormal(
                position,
                out float height,
                out Vec3 _);
            return IsUsableHeight(height) ? height : fallback;
        }

        internal static bool TryGetRaisedTerrainHeightAndNormal(
            Vec2 position,
            out float height,
            out Vec3 normal)
        {
            height = float.NaN;
            normal = new Vec3(0f, 0f, 1f);
            if (_activeMapScene == null || _activeMapScene.Scene == null)
            {
                return false;
            }

            _activeMapScene.GetTerrainHeightAndNormal(
                position,
                out height,
                out normal);
            return IsRaisedTerrain(_activeMapScene.Scene, position, height);
        }

        internal static bool TryGetLandingRoute(
            CampaignVec2 landTarget,
            Vec2 approachFrom,
            out CampaignVec2 seaApproach,
            out CampaignVec2 landEntry)
        {
            IslandSurface surface;
            if (landTarget == null ||
                !TryGetSurface(landTarget.ToVec2(), out surface))
            {
                seaApproach = CampaignVec2.Invalid;
                landEntry = CampaignVec2.Invalid;
                return false;
            }

            Vec2 direction = approachFrom - surface.Center;
            Vec2 entryPoint = GetInsetRectangleEdgePoint(surface, direction);
            CampaignVec2 entry = new CampaignVec2(entryPoint, true);
            if (!IsImportedLandPosition(entry))
            {
                seaApproach = CampaignVec2.Invalid;
                landEntry = CampaignVec2.Invalid;
                return false;
            }

            CampaignVec2 approach;
            if (!TryFindShorelineNavalPosition(
                    entryPoint,
                    direction,
                    approachFrom,
                    out approach))
            {
                seaApproach = CampaignVec2.Invalid;
                landEntry = CampaignVec2.Invalid;
                return false;
            }

            seaApproach = approach;
            landEntry = entry;
            return true;
        }

        internal static bool TryGetEmbarkRoute(
            CampaignVec2 currentLandPosition,
            CampaignVec2 finalSeaTarget,
            out CampaignVec2 landExit,
            out CampaignVec2 seaApproach)
        {
            IslandSurface surface;
            if (currentLandPosition == null ||
                finalSeaTarget == null ||
                !TryGetSurface(currentLandPosition.ToVec2(), out surface))
            {
                landExit = CampaignVec2.Invalid;
                seaApproach = CampaignVec2.Invalid;
                return false;
            }

            Vec2 direction = finalSeaTarget.ToVec2() - surface.Center;
            Vec2 exitPoint = GetInsetRectangleEdgePoint(surface, direction);
            CampaignVec2 exitPosition = new CampaignVec2(exitPoint, true);
            if (!IsImportedLandPosition(exitPosition))
            {
                landExit = CampaignVec2.Invalid;
                seaApproach = CampaignVec2.Invalid;
                return false;
            }

            CampaignVec2 approach;
            if (!TryFindShorelineNavalPosition(
                    exitPoint,
                    direction,
                    finalSeaTarget.ToVec2(),
                    out approach))
            {
                landExit = CampaignVec2.Invalid;
                seaApproach = CampaignVec2.Invalid;
                return false;
            }

            landExit = exitPosition;
            seaApproach = approach;
            return true;
        }

        private static Vec2 GetInsetRectangleEdgePoint(
            IslandSurface surface,
            Vec2 direction)
        {
            float directionLength = (float)Math.Sqrt(
                direction.X * direction.X +
                direction.Y * direction.Y);
            if (directionLength < 0.001f)
            {
                direction = surface.Axis;
            }
            else
            {
                direction /= directionLength;
            }

            float along = Math.Abs(Dot(direction, surface.Axis));
            float across = Math.Abs(Dot(direction, surface.Perpendicular));
            float alongDistance = along < 0.001f
                ? float.MaxValue
                : surface.Length * 0.48f / along;
            float acrossDistance = across < 0.001f
                ? float.MaxValue
                : surface.Width * 0.48f / across;
            float exitDistance = Math.Min(alongDistance, acrossDistance);
            return surface.Center + direction * exitDistance;
        }

        private static IslandRecord ToRecord(IslandSurface surface)
        {
            string id = string.Format(
                CultureInfo.InvariantCulture,
                "island_{0}_{1}",
                (int)Math.Round(surface.Center.X * 100f),
                (int)Math.Round(surface.Center.Y * 100f));
            return new IslandRecord(
                id,
                surface.Name,
                surface.Center,
                surface.Axis,
                surface.Perpendicular,
                surface.Length,
                surface.Width,
                surface.Height,
                surface.SampleCount);
        }

        private static bool TryGetSurface(Vec2 position, out IslandSurface surface)
        {
            foreach (IslandSurface candidate in _activeSurfaces)
            {
                Vec2 delta = position - candidate.Center;
                float along = Math.Abs(Dot(delta, candidate.Axis));
                float across = Math.Abs(Dot(delta, candidate.Perpendicular));
                if (along <= candidate.Length * 0.5f + 0.25f &&
                    across <= candidate.Width * 0.5f + 0.25f)
                {
                    surface = candidate;
                    return true;
                }
            }

            surface = default(IslandSurface);
            return false;
        }

        private static bool RegisterImportedSurfaceTerrainAsLand(Scene scene)
        {
            PartyNavigationModel navigationModel = Campaign.Current?.Models?.PartyNavigationModel;
            if (navigationModel == null)
            {
                Debug.Print("[BLWarsailsInhabitableIslands] Could not register imported terrain: the party navigation model is unavailable.");
                return false;
            }

            bool[] regionMap = BuildRegionMap(navigationModel, true);
            try
            {
                scene.SetNavMeshRegionMap(regionMap);
                int enabledFaces = scene.SetAbilityOfFacesWithId(0, true);
                Debug.Print(string.Format(
                    CultureInfo.InvariantCulture,
                    "[BLWarsailsInhabitableIslands] Registered imported terrain id 0 as land (engine ability result={0}).",
                    enabledFaces));
                return true;
            }
            catch (Exception ex)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Could not register imported " +
                    "terrain id 0: " + ex);
                RestoreNativeTerrainRegistration(scene, navigationModel);
                return false;
            }
        }

        private static void RestoreNativeTerrainRegistration(
            Scene scene,
            PartyNavigationModel navigationModel)
        {
            try
            {
                scene.SetNavMeshRegionMap(
                    BuildRegionMap(navigationModel, false));
                scene.SetAbilityOfFacesWithId(0, false);
            }
            catch (Exception restoreException)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Could not restore the " +
                    "native terrain map after registration failure: " +
                    restoreException);
            }
        }

        private static bool[] BuildRegionMap(
            PartyNavigationModel navigationModel,
            bool enableImportedTerrain)
        {
            int highestTerrainId = 0;
            foreach (TerrainType terrainType in Enum.GetValues(typeof(TerrainType)))
            {
                highestTerrainId = Math.Max(highestTerrainId, (int)terrainType);
            }

            bool[] regionMap = new bool[highestTerrainId + 1];
            foreach (TerrainType terrainType in Enum.GetValues(typeof(TerrainType)))
            {
                regionMap[(int)terrainType] =
                    navigationModel.IsTerrainTypeValidForNavigationType(
                        terrainType,
                        MobileParty.NavigationType.Default);
            }

            regionMap[0] = enableImportedTerrain;
            return regionMap;
        }

        private static int CountFacesWithTerrainId(Scene scene, int terrainId)
        {
            int matchingFaces = 0;
            int faceCount = scene.GetNavMeshFaceCount();
            for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
            {
                if (scene.GetIdOfNavMeshFace(faceIndex) == terrainId)
                {
                    matchingFaces++;
                }
            }

            return matchingFaces;
        }

        private static void SampleCandidateCells(
            MapScene mapScene,
            Scene scene,
            out Dictionary<long, IslandSample> candidates,
            out HashSet<long> raisedCells,
            out int sampledCellCount)
        {
            candidates = new Dictionary<long, IslandSample>();
            raisedCells = new HashSet<long>();
            HashSet<long> sampledCells = new HashSet<long>();

            foreach (IslandZone zone in IslandZoneSettings.Zones)
            {
                int minGridX = (int)Math.Floor(zone.MinX / GridStep);
                int maxGridX = (int)Math.Ceiling(zone.MaxX / GridStep);
                int minGridY = (int)Math.Floor(zone.MinY / GridStep);
                int maxGridY = (int)Math.Ceiling(zone.MaxY / GridStep);

                for (int gridX = minGridX; gridX <= maxGridX; gridX++)
                {
                    for (int gridY = minGridY; gridY <= maxGridY; gridY++)
                    {
                        long key = GetKey(gridX, gridY);
                        if (!sampledCells.Add(key))
                        {
                            continue;
                        }

                        Vec2 position = new Vec2(gridX * GridStep, gridY * GridStep);
                        float height;
                        Vec3 normal;
                        mapScene.GetTerrainHeightAndNormal(position, out height, out normal);

                        if (!IsRaisedTerrain(scene, position, height))
                        {
                            continue;
                        }

                        raisedCells.Add(key);
                        candidates.Add(key, new IslandSample(gridX, gridY, position, height));
                    }
                }
            }

            sampledCellCount = sampledCells.Count;
        }

        private static List<IslandComponent> BuildComponents(
            Dictionary<long, IslandSample> candidates,
            HashSet<long> raisedCells)
        {
            List<IslandComponent> components = new List<IslandComponent>();
            HashSet<long> visited = new HashSet<long>();

            foreach (KeyValuePair<long, IslandSample> pair in candidates)
            {
                if (!visited.Add(pair.Key))
                {
                    continue;
                }

                Queue<long> pending = new Queue<long>();
                List<IslandSample> samples = new List<IslandSample>();
                HashSet<long> keys = new HashSet<long>();
                bool touchesConfiguredBoundary = false;
                int outsideEdges = 0;
                int existingLandEdges = 0;

                pending.Enqueue(pair.Key);
                while (pending.Count > 0)
                {
                    long key = pending.Dequeue();
                    IslandSample sample = candidates[key];
                    samples.Add(sample);
                    keys.Add(key);

                    for (int direction = 0; direction < NeighborX.Length; direction++)
                    {
                        int neighborX = sample.GridX + NeighborX[direction];
                        int neighborY = sample.GridY + NeighborY[direction];
                        long neighborKey = GetKey(neighborX, neighborY);

                        if (candidates.ContainsKey(neighborKey))
                        {
                            if (visited.Add(neighborKey))
                            {
                                pending.Enqueue(neighborKey);
                            }

                            continue;
                        }

                        outsideEdges++;
                        if (raisedCells.Contains(neighborKey))
                        {
                            existingLandEdges++;
                        }

                        Vec2 neighborPosition = new Vec2(neighborX * GridStep, neighborY * GridStep);
                        if (!IslandZoneSettings.Contains(neighborPosition))
                        {
                            touchesConfiguredBoundary = true;
                        }
                    }
                }

                if (samples.Count < MinimumComponentSamples ||
                    samples.Count > MaximumComponentSamples ||
                    touchesConfiguredBoundary)
                {
                    continue;
                }

                float existingLandEdgeRatio = outsideEdges == 0 ? 0f : (float)existingLandEdges / outsideEdges;
                if (existingLandEdgeRatio > 0.25f)
                {
                    continue;
                }

                components.Add(new IslandComponent(samples, keys));
            }

            return components;
        }

        private static bool TryFitSurface(IslandComponent component, out IslandSurface surface)
        {
            surface = default(IslandSurface);

            Vec2 mean = Vec2.Zero;
            foreach (IslandSample sample in component.Samples)
            {
                mean += sample.Position;
            }

            mean /= component.Samples.Count;

            double covarianceX = 0d;
            double covarianceY = 0d;
            double covarianceXY = 0d;
            foreach (IslandSample sample in component.Samples)
            {
                double deltaX = sample.Position.X - mean.X;
                double deltaY = sample.Position.Y - mean.Y;
                covarianceX += deltaX * deltaX;
                covarianceY += deltaY * deltaY;
                covarianceXY += deltaX * deltaY;
            }

            double angle = 0.5d * Math.Atan2(2d * covarianceXY, covarianceX - covarianceY);
            Vec2 axis = new Vec2((float)Math.Cos(angle), (float)Math.Sin(angle));
            Vec2 perpendicular = new Vec2(-axis.Y, axis.X);

            float minAxis;
            float maxAxis;
            float minPerpendicular;
            float maxPerpendicular;
            GetProjectedBounds(
                component.Samples,
                mean,
                axis,
                perpendicular,
                out minAxis,
                out maxAxis,
                out minPerpendicular,
                out maxPerpendicular);

            float fullLength = maxAxis - minAxis + GridStep;
            float fullWidth = maxPerpendicular - minPerpendicular + GridStep;
            if (fullWidth > fullLength)
            {
                Vec2 oldAxis = axis;
                axis = perpendicular;
                perpendicular = new Vec2(-oldAxis.X, -oldAxis.Y);
                GetProjectedBounds(
                    component.Samples,
                    mean,
                    axis,
                    perpendicular,
                    out minAxis,
                    out maxAxis,
                    out minPerpendicular,
                    out maxPerpendicular);
                fullLength = maxAxis - minAxis + GridStep;
                fullWidth = maxPerpendicular - minPerpendicular + GridStep;
            }

            Vec2 center = mean +
                          axis * ((minAxis + maxAxis) * 0.5f) +
                          perpendicular * ((minPerpendicular + maxPerpendicular) * 0.5f);

            float chosenLength = 0f;
            float chosenWidth = 0f;
            for (float lengthFactor = 0.95f; lengthFactor >= 0.60f && chosenLength <= 0f; lengthFactor -= 0.05f)
            {
                for (float widthFactor = 0.95f; widthFactor >= 0.30f; widthFactor -= 0.05f)
                {
                    float candidateLength = fullLength * lengthFactor;
                    float candidateWidth = fullWidth * widthFactor;
                    if (candidateLength < GridStep * 2f || candidateWidth < GridStep * 1.5f)
                    {
                        continue;
                    }

                    if (RectangleFitsComponent(component.Keys, center, axis, perpendicular, candidateLength, candidateWidth))
                    {
                        chosenLength = candidateLength;
                        chosenWidth = candidateWidth;
                        break;
                    }
                }
            }

            if (chosenLength <= 0f || chosenWidth <= 0f)
            {
                return false;
            }

            float heightTotal = 0f;
            int heightSamples = 0;
            foreach (IslandSample sample in component.Samples)
            {
                Vec2 delta = sample.Position - center;
                float along = Dot(delta, axis);
                float across = Dot(delta, perpendicular);
                if (Math.Abs(along) <= chosenLength * 0.5f &&
                    Math.Abs(across) <= chosenWidth * 0.5f)
                {
                    heightTotal += sample.Height;
                    heightSamples++;
                }
            }

            float surfaceHeight = heightSamples == 0 ? component.Samples[0].Height : heightTotal / heightSamples;
            IslandZone zone;
            string name = IslandZoneSettings.TryGetContainingZone(center, out zone)
                ? zone.Name
                : "Configured Island";

            surface = new IslandSurface(
                name,
                center,
                axis,
                perpendicular,
                chosenLength,
                chosenWidth,
                surfaceHeight,
                component.Samples.Count);
            return true;
        }

        private static bool TryFitAxisAlignedSurface(
            IslandComponent component,
            out IslandSurface surface)
        {
            surface = default(IslandSurface);
            if (component.Samples.Count == 0)
            {
                return false;
            }

            int minimumGridX = int.MaxValue;
            int maximumGridX = int.MinValue;
            int minimumGridY = int.MaxValue;
            int maximumGridY = int.MinValue;
            foreach (IslandSample sample in component.Samples)
            {
                minimumGridX = Math.Min(minimumGridX, sample.GridX);
                maximumGridX = Math.Max(maximumGridX, sample.GridX);
                minimumGridY = Math.Min(minimumGridY, sample.GridY);
                maximumGridY = Math.Max(maximumGridY, sample.GridY);
            }

            int columnCount = maximumGridX - minimumGridX + 1;
            int[] heights = new int[columnCount];
            int bestArea = 0;
            int bestLeft = 0;
            int bestRight = 0;
            int bestBottom = 0;
            int bestTop = 0;

            for (int gridY = minimumGridY; gridY <= maximumGridY; gridY++)
            {
                for (int column = 0; column < columnCount; column++)
                {
                    int gridX = minimumGridX + column;
                    heights[column] = component.Keys.Contains(GetKey(gridX, gridY))
                        ? heights[column] + 1
                        : 0;
                }

                Stack<int> pendingColumns = new Stack<int>();
                for (int column = 0; column <= columnCount; column++)
                {
                    int currentHeight = column == columnCount ? 0 : heights[column];
                    while (pendingColumns.Count > 0 &&
                           heights[pendingColumns.Peek()] > currentHeight)
                    {
                        int rectangleColumn = pendingColumns.Pop();
                        int rectangleHeight = heights[rectangleColumn];
                        int left = pendingColumns.Count == 0
                            ? 0
                            : pendingColumns.Peek() + 1;
                        int right = column - 1;
                        int area = rectangleHeight * (right - left + 1);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            bestLeft = minimumGridX + left;
                            bestRight = minimumGridX + right;
                            bestTop = gridY;
                            bestBottom = gridY - rectangleHeight + 1;
                        }
                    }

                    if (column < columnCount)
                    {
                        pendingColumns.Push(column);
                    }
                }
            }

            if (bestArea <= 0)
            {
                return false;
            }

            float xLength = (bestRight - bestLeft + 1) * GridStep * 0.95f;
            float yLength = (bestTop - bestBottom + 1) * GridStep * 0.95f;
            Vec2 center = new Vec2(
                (bestLeft + bestRight) * GridStep * 0.5f,
                (bestBottom + bestTop) * GridStep * 0.5f);
            Vec2 axis;
            Vec2 perpendicular;
            float length;
            float width;
            if (xLength >= yLength)
            {
                axis = new Vec2(1f, 0f);
                perpendicular = new Vec2(0f, 1f);
                length = xLength;
                width = yLength;
            }
            else
            {
                axis = new Vec2(0f, 1f);
                perpendicular = new Vec2(-1f, 0f);
                length = yLength;
                width = xLength;
            }

            float heightTotal = 0f;
            int heightSamples = 0;
            foreach (IslandSample sample in component.Samples)
            {
                if (sample.GridX >= bestLeft &&
                    sample.GridX <= bestRight &&
                    sample.GridY >= bestBottom &&
                    sample.GridY <= bestTop)
                {
                    heightTotal += sample.Height;
                    heightSamples++;
                }
            }

            IslandZone zone;
            string name = IslandZoneSettings.TryGetContainingZone(center, out zone)
                ? zone.Name
                : "Configured Island";
            surface = new IslandSurface(
                name,
                center,
                axis,
                perpendicular,
                length,
                width,
                heightSamples == 0
                    ? component.Samples[0].Height
                    : heightTotal / heightSamples,
                component.Samples.Count);
            return true;
        }

        private static void GetProjectedBounds(
            List<IslandSample> samples,
            Vec2 origin,
            Vec2 axis,
            Vec2 perpendicular,
            out float minAxis,
            out float maxAxis,
            out float minPerpendicular,
            out float maxPerpendicular)
        {
            minAxis = float.MaxValue;
            maxAxis = float.MinValue;
            minPerpendicular = float.MaxValue;
            maxPerpendicular = float.MinValue;

            foreach (IslandSample sample in samples)
            {
                Vec2 delta = sample.Position - origin;
                float along = Dot(delta, axis);
                float across = Dot(delta, perpendicular);
                minAxis = Math.Min(minAxis, along);
                maxAxis = Math.Max(maxAxis, along);
                minPerpendicular = Math.Min(minPerpendicular, across);
                maxPerpendicular = Math.Max(maxPerpendicular, across);
            }
        }

        private static bool RectangleFitsComponent(
            HashSet<long> componentKeys,
            Vec2 center,
            Vec2 axis,
            Vec2 perpendicular,
            float length,
            float width)
        {
            for (int alongIndex = -2; alongIndex <= 2; alongIndex++)
            {
                float along = length * alongIndex * 0.25f;
                for (int acrossIndex = -1; acrossIndex <= 1; acrossIndex++)
                {
                    float across = width * acrossIndex * 0.5f;
                    Vec2 point = center + axis * along + perpendicular * across;
                    if (!IsNearComponent(componentKeys, point))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsNearComponent(HashSet<long> componentKeys, Vec2 point)
        {
            int centerGridX = (int)Math.Round(point.X / GridStep);
            int centerGridY = (int)Math.Round(point.Y / GridStep);
            float maximumDistanceSquared = GridStep * GridStep * 0.8f;

            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                for (int offsetY = -1; offsetY <= 1; offsetY++)
                {
                    int gridX = centerGridX + offsetX;
                    int gridY = centerGridY + offsetY;
                    if (!componentKeys.Contains(GetKey(gridX, gridY)))
                    {
                        continue;
                    }

                    float deltaX = point.X - gridX * GridStep;
                    float deltaY = point.Y - gridY * GridStep;
                    if (deltaX * deltaX + deltaY * deltaY <= maximumDistanceSquared)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static MatrixFrame CreateFrame(IslandSurface surface)
        {
            float prefabLength = PrefabMaxX - PrefabMinX;
            float prefabWidth = PrefabMaxY - PrefabMinY;
            float localCenterX = (PrefabMinX + PrefabMaxX) * 0.5f;
            float localCenterY = (PrefabMinY + PrefabMaxY) * 0.5f;
            float scaleX = surface.Length / prefabLength;
            float scaleY = surface.Width / prefabWidth;

            MatrixFrame frame = MatrixFrame.Identity;
            frame.rotation.s = new Vec3(surface.Axis.X * scaleX, surface.Axis.Y * scaleX, 0f);
            frame.rotation.f = new Vec3(surface.Perpendicular.X * scaleY, surface.Perpendicular.Y * scaleY, 0f);
            frame.rotation.u = new Vec3(0f, 0f, 1f);
            frame.origin = new Vec3(
                surface.Center.X - frame.rotation.s.x * localCenterX - frame.rotation.f.x * localCenterY,
                surface.Center.Y - frame.rotation.s.y * localCenterX - frame.rotation.f.y * localCenterY,
                surface.Height - PrefabZ);
            return frame;
        }

        private static float GetWaterLevel(Scene scene, Vec2 position)
        {
            float waterLevel = scene.GetWaterLevelAtPosition(position, false, true);
            if (!IsUsableHeight(waterLevel))
            {
                waterLevel = scene.GetWaterLevelAtPosition(position, true, true);
            }

            if (!IsUsableHeight(waterLevel))
            {
                waterLevel = scene.GetWaterLevel();
            }

            return IsUsableHeight(waterLevel) ? waterLevel : float.NaN;
        }

        private static bool IsRaisedTerrain(Scene scene, Vec2 position, float terrainHeight)
        {
            if (!IsUsableHeight(terrainHeight))
            {
                return false;
            }

            float physicalSurfaceHeight = float.NaN;
            if (scene.GetHeightAtPoint(position, BodyFlags.None, ref physicalSurfaceHeight) &&
                IsUsableHeight(physicalSurfaceHeight) &&
                physicalSurfaceHeight > terrainHeight + 0.2f)
            {
                return false;
            }

            float waterLevel = GetWaterLevel(scene, position);
            if (IsUsableHeight(waterLevel))
            {
                return terrainHeight >= waterLevel + MinimumHeightAboveWater;
            }

            return IsUsableHeight(physicalSurfaceHeight) &&
                   physicalSurfaceHeight <= terrainHeight + 0.2f;
        }

        private static bool IsUsableHeight(float value)
        {
            return !float.IsNaN(value) &&
                   !float.IsInfinity(value) &&
                   value > -10000f &&
                   value < 10000f;
        }

        private static float Dot(Vec2 left, Vec2 right)
        {
            return left.X * right.X + left.Y * right.Y;
        }

        private static long GetKey(int gridX, int gridY)
        {
            return ((long)gridX << 32) ^ (uint)gridY;
        }

        private sealed class IslandSample
        {
            public IslandSample(int gridX, int gridY, Vec2 position, float height)
            {
                GridX = gridX;
                GridY = gridY;
                Position = position;
                Height = height;
            }

            public int GridX { get; }
            public int GridY { get; }
            public Vec2 Position { get; }
            public float Height { get; }
        }

        private sealed class IslandComponent
        {
            public IslandComponent(List<IslandSample> samples, HashSet<long> keys)
            {
                Samples = samples;
                Keys = keys;
            }

            public List<IslandSample> Samples { get; }
            public HashSet<long> Keys { get; }
        }

        private readonly struct IslandSurface
        {
            public IslandSurface(
                string name,
                Vec2 center,
                Vec2 axis,
                Vec2 perpendicular,
                float length,
                float width,
                float height,
                int sampleCount)
            {
                Name = name;
                Center = center;
                Axis = axis;
                Perpendicular = perpendicular;
                Length = length;
                Width = width;
                Height = height;
                SampleCount = sampleCount;
            }

            public string Name { get; }
            public Vec2 Center { get; }
            public Vec2 Axis { get; }
            public Vec2 Perpendicular { get; }
            public float Length { get; }
            public float Width { get; }
            public float Height { get; }
            public int SampleCount { get; }
        }
    }
}
