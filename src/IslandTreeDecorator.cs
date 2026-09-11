using System;
using System.Collections.Generic;
using System.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandTreeDecorator
    {
        private static readonly TreeResource[] TemperateTreeResources =
        {
            new TreeResource("worldmap_tree_acacia_01", 0.21f, 0.29f),
            new TreeResource("worldmap_tree_beech_01", 0.19f, 0.27f),
            new TreeResource("worldmap_tree_high_01", 0.26f, 0.40f)
        };

        private static readonly TreeResource[] NorthernTreeResources =
        {
            new TreeResource("worldmap_tree_spruce", 0.067f, 0.131f)
        };

        private static readonly List<TreeInstance> Instances =
            new List<TreeInstance>();
        private static readonly Dictionary<string, MetaMesh> ResourceCache =
            new Dictionary<string, MetaMesh>(StringComparer.Ordinal);
        private static readonly HashSet<string> FailedResources =
            new HashSet<string>(StringComparer.Ordinal);
        private static Scene _scene;
        private static bool _enabled = true;

        internal static bool IsEnabled => _enabled;
        internal static int InstanceCount => Instances.Count;

        internal static void Apply(Scene scene)
        {
            if (scene == null || ReferenceEquals(scene, _scene))
            {
                return;
            }

            _scene = scene;
            Instances.Clear();
            ResourceCache.Clear();
            FailedResources.Clear();
            if (!_enabled)
            {
                return;
            }

            foreach (IslandRecord island in IslandNavMeshOverlay.Islands)
            {
                if (island.Area < 32f || StableHash(island.Id) % 2 != 0)
                {
                    continue;
                }

                AddTrees(scene, island);
            }

            Debug.Print(
                "[BLWarsailsInhabitableIslands] Added " +
                Instances.Count + " non-blocking island trees.");
        }

        internal static string GetStatus()
        {
            return _enabled
                ? "Inhabitable Isles trees are enabled for this game session. " +
                  Instances.Count + " generated trees are active."
                : "Inhabitable Isles trees are disabled for this game session.";
        }

        internal static string SetEnabled(bool enabled)
        {
            if (_enabled == enabled)
            {
                return GetStatus();
            }

            _enabled = enabled;
            if (!enabled)
            {
                RemoveAll();
                return "Inhabitable Isles trees disabled for this game session.";
            }

            Scene scene = _scene;
            _scene = null;
            Apply(scene);
            return GetStatus();
        }

        internal static void RefreshSettlementClearings()
        {
            if (Instances.Count == 0 || Campaign.Current == null)
            {
                return;
            }

            List<Vec2> settlementPositions = Settlement.All
                .Where(IslandSettlementIntegration.IsPlayerSettlement)
                .Select(settlement => settlement.Position.ToVec2())
                .ToList();
            for (int index = Instances.Count - 1; index >= 0; index--)
            {
                TreeInstance instance = Instances[index];
                bool remove = settlementPositions.Any(
                    position =>
                    {
                        Vec2 delta = position - instance.Position;
                        return delta.LengthSquared <= 25f;
                    });
                if (!remove)
                {
                    continue;
                }

                try
                {
                    instance.Entity.Remove(0);
                }
                catch
                {
                }

                Instances.RemoveAt(index);
            }
        }

        private static void RemoveAll()
        {
            for (int index = Instances.Count - 1; index >= 0; index--)
            {
                try
                {
                    Instances[index].Entity.Remove(0);
                }
                catch
                {
                }
            }

            Instances.Clear();
        }

        private static void AddTrees(Scene scene, IslandRecord island)
        {
            int seed = StableHash(island.Id);
            int desired = Math.Max(
                6,
                Math.Min(18, (int)Math.Round(island.Area / 8f)));
            TreeResource[] treeResources = island.ZoneName.IndexOf(
                "Kachyar",
                StringComparison.OrdinalIgnoreCase) >= 0
                    ? NorthernTreeResources
                    : TemperateTreeResources;

            int attempts = desired * 8;
            for (int attempt = 0;
                 attempt < attempts && CountForIsland(island.Id) < desired;
                 attempt++)
            {
                float along = NextFloat(ref seed, -0.34f, 0.34f) * island.Length;
                float across = NextFloat(ref seed, -0.34f, 0.34f) * island.Width;
                Vec2 position =
                    island.Center +
                    island.Axis * along +
                    island.Perpendicular * across;
                Vec2 centerDelta = position - island.Center;
                if (centerDelta.LengthSquared < 5.25f ||
                    IsNearExistingTree(position, 0.85f))
                {
                    continue;
                }

                TreeResource treeResource =
                    treeResources[
                        Math.Abs(Next(ref seed)) % treeResources.Length];

                float scale = NextFloat(
                    ref seed,
                    treeResource.MinimumScale,
                    treeResource.MaximumScale);
                float angle = NextFloat(
                    ref seed,
                    0f,
                    (float)(Math.PI * 2d));
                float cosine = (float)Math.Cos(angle) * scale;
                float sine = (float)Math.Sin(angle) * scale;
                MatrixFrame frame = MatrixFrame.Identity;
                frame.rotation.s = new Vec3(cosine, sine, 0f);
                frame.rotation.f = new Vec3(-sine, cosine, 0f);
                frame.rotation.u = new Vec3(0f, 0f, scale);
                frame.origin = new Vec3(
                    position.X,
                    position.Y,
                    IslandNavMeshOverlay.GetTerrainHeight(
                        position,
                        island.Height));

                try
                {
                    GameEntity entity = CreateTreeEntity(
                        scene,
                        treeResource.Name);
                    if (entity != null)
                    {
                        entity.SetFrame(ref frame, true);
                        entity.RecomputeBoundingBox();
                        entity.UpdateGlobalBounds();
                        entity.UpdateVisibilityMask();
                        Instances.Add(
                            new TreeInstance(entity, island.Id, position));
                    }
                }
                catch (Exception ex)
                {
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Tree resource failed: " +
                        treeResource.Name + ": " + ex.Message);
                }
            }
        }

        private static GameEntity CreateTreeEntity(
            Scene scene,
            string resourceName)
        {
            MetaMesh resource = ResolveTreeResource(resourceName);
            if (resource == null)
            {
                return null;
            }

            GameEntity entity = GameEntity.CreateEmpty(
                scene,
                false,
                false,
                false);
            if (entity == null)
            {
                return null;
            }

            entity.AddMultiMesh(resource.CreateCopy(), true);
            entity.RecomputeBoundingBox();
            return entity;
        }

        private static MetaMesh ResolveTreeResource(string resourceName)
        {
            if (ResourceCache.TryGetValue(
                    resourceName,
                    out MetaMesh cached))
            {
                return cached;
            }

            if (FailedResources.Contains(resourceName))
            {
                return null;
            }

            try
            {
                MetaMesh resource = MetaMesh.GetMultiMesh(resourceName);
                if (resource != null && resource.MeshCount > 0)
                {
                    ResourceCache[resourceName] = resource;
                    return resource;
                }
            }
            catch (Exception ex)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Could not resolve tree " +
                    "resource " + resourceName + ": " + ex);
            }

            if (FailedResources.Add(resourceName))
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Disabled unavailable tree " +
                    "resource " + resourceName + " for this map session.");
            }

            return null;
        }

        private static int CountForIsland(string islandId)
        {
            int count = 0;
            foreach (TreeInstance instance in Instances)
            {
                if (instance.IslandId == islandId)
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsNearExistingTree(Vec2 position, float minimumDistance)
        {
            float minimumDistanceSquared = minimumDistance * minimumDistance;
            foreach (TreeInstance instance in Instances)
            {
                Vec2 delta = instance.Position - position;
                if (delta.LengthSquared < minimumDistanceSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private static int StableHash(string value)
        {
            unchecked
            {
                int hash = 17;
                foreach (char character in value)
                {
                    hash = hash * 31 + character;
                }

                return hash & int.MaxValue;
            }
        }

        private static int Next(ref int state)
        {
            unchecked
            {
                state = state * 1103515245 + 12345;
                return state & int.MaxValue;
            }
        }

        private static float NextFloat(
            ref int state,
            float minimum,
            float maximum)
        {
            return minimum +
                   (maximum - minimum) *
                   (Next(ref state) / (float)int.MaxValue);
        }

        private sealed class TreeInstance
        {
            public TreeInstance(
                GameEntity entity,
                string islandId,
                Vec2 position)
            {
                Entity = entity;
                IslandId = islandId;
                Position = position;
            }

            public GameEntity Entity { get; }
            public string IslandId { get; }
            public Vec2 Position { get; }
        }

        private sealed class TreeResource
        {
            public TreeResource(
                string name,
                float minimumScale,
                float maximumScale)
            {
                Name = name;
                MinimumScale = minimumScale;
                MaximumScale = maximumScale;
            }

            public string Name { get; }
            public float MinimumScale { get; }
            public float MaximumScale { get; }
        }
    }
}
