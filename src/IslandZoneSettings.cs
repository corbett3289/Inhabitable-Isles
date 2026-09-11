using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Xml;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandZoneSettings
    {
        private static readonly List<IslandZone> Defaults = new List<IslandZone>
        {
            new IslandZone("Western Gulf of Charas Islands", 120f, 320f, 265f, 415f),
            new IslandZone("Southern Gulf of Charas Islands", 235f, 380f, 205f, 325f),
            new IslandZone("Biscan Bay Western Islets", 110f, 280f, 380f, 470f),
            new IslandZone("Northern Ebor Offshore Islands", 190f, 320f, 575f, 655f),
            new IslandZone("Kachyar Strait Islands", 300f, 430f, 635f, 725f)
        };

        private static List<IslandZone> _zones;

        public static IReadOnlyList<IslandZone> Zones
        {
            get
            {
                if (_zones == null)
                {
                    _zones = Load();
                }

                return _zones;
            }
        }

        public static bool Contains(Vec2 position)
        {
            return TryGetContainingZone(position, out _);
        }

        public static bool TryGetContainingZone(Vec2 position, out IslandZone containingZone)
        {
            foreach (IslandZone zone in Zones)
            {
                if (zone.Contains(position))
                {
                    containingZone = zone;
                    return true;
                }
            }

            containingZone = default(IslandZone);
            return false;
        }

        private static List<IslandZone> Load()
        {
            string path = GetConfigPath();
            if (!File.Exists(path))
            {
                return new List<IslandZone>(Defaults);
            }

            try
            {
                XmlDocument document = new XmlDocument();
                document.Load(path);

                List<IslandZone> zones = new List<IslandZone>();
                XmlNodeList nodes = document.SelectNodes("/IslandZones/Zone");
                if (nodes != null)
                {
                    foreach (XmlNode node in nodes)
                    {
                        IslandZone? zone = TryReadZone(node);
                        if (zone.HasValue)
                        {
                            zones.Add(zone.Value);
                        }
                    }
                }

                return zones.Count > 0 ? zones : new List<IslandZone>(Defaults);
            }
            catch (Exception ex)
            {
                Debug.Print("[BLWarsailsInhabitableIslands] Failed to load IslandZones.xml: " + ex.Message);
                return new List<IslandZone>(Defaults);
            }
        }

        private static IslandZone? TryReadZone(XmlNode node)
        {
            XmlAttributeCollection attributes = node.Attributes;
            if (attributes == null)
            {
                return null;
            }

            string name = GetAttribute(attributes, "name", "Island Zone");
            if (!TryGetFloat(attributes, "minX", out float minX) ||
                !TryGetFloat(attributes, "maxX", out float maxX) ||
                !TryGetFloat(attributes, "minY", out float minY) ||
                !TryGetFloat(attributes, "maxY", out float maxY))
            {
                return null;
            }

            return new IslandZone(name, Math.Min(minX, maxX), Math.Max(minX, maxX), Math.Min(minY, maxY), Math.Max(minY, maxY));
        }

        private static bool TryGetFloat(XmlAttributeCollection attributes, string name, out float value)
        {
            value = 0f;
            string raw = GetAttribute(attributes, name, null);
            return raw != null && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string GetAttribute(XmlAttributeCollection attributes, string name, string fallback)
        {
            XmlAttribute attribute = attributes[name];
            return attribute == null ? fallback : attribute.Value;
        }

        private static string GetConfigPath()
        {
            string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string moduleRoot = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", ".."));
            return Path.Combine(moduleRoot, "ModuleData", "IslandZones.xml");
        }
    }
}
