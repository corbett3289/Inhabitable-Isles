using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandCompatibility
    {
        internal const string ReleaseVersion = "0.9.21";
        internal const string TargetBannerlordVersion = "1.4.8";
        internal const string TargetWarSailsVersion = "1.2.8";
        internal const string TargetPlayerSettlementVersion = "7.5.0";

        private static readonly List<string> FailedPatchTypes =
            new List<string>();

        private static int _expectedPatchTypes;
        private static int _appliedPatchTypes;

        internal static void BeginPatchRegistration(int expectedPatchTypes)
        {
            _expectedPatchTypes = expectedPatchTypes;
            _appliedPatchTypes = 0;
            FailedPatchTypes.Clear();
        }

        internal static void RecordPatchSuccess(Type patchType)
        {
            _appliedPatchTypes++;
        }

        internal static void RecordPatchFailure(Type patchType)
        {
            string name = patchType?.FullName ?? "<unknown patch>";
            if (!FailedPatchTypes.Contains(name))
            {
                FailedPatchTypes.Add(name);
            }
        }

        internal static string GetStatus()
        {
            string failures = FailedPatchTypes.Count == 0
                ? "none"
                : string.Join(", ", FailedPatchTypes.Select(ShortName));
            string transitionMode = IslandTravelController.SupportsAnimatedTransitions
                ? "animated with public fallback"
                : "public fallback only";

            return string.Format(
                "Inhabitable Isles v{0}; compiled target: Bannerlord {1}, " +
                "War Sails {2}; runtime Player Settlement {3} (target {4}), " +
                "Harmony {5}; patches {6}/{7}; failed patches: {8}; " +
                "imported island surfaces: {9}; transitions: {10}; trees: {11} " +
                "({12} active).",
                ReleaseVersion,
                TargetBannerlordVersion,
                TargetWarSailsVersion,
                GetFileVersion(typeof(PlayerSettlementBehaviour)),
                TargetPlayerSettlementVersion,
                GetFileVersion(typeof(Harmony)),
                _appliedPatchTypes,
                _expectedPatchTypes,
                failures,
                IslandNavMeshOverlay.ImportedSurfaceCount,
                transitionMode,
                IslandTreeDecorator.IsEnabled ? "enabled" : "disabled",
                IslandTreeDecorator.InstanceCount);
        }

        private static string GetFileVersion(Type type)
        {
            try
            {
                string location = type?.Assembly?.Location;
                string version = string.IsNullOrEmpty(location)
                    ? null
                    : FileVersionInfo.GetVersionInfo(location).FileVersion;
                return string.IsNullOrWhiteSpace(version)
                    ? type?.Assembly?.GetName()?.Version?.ToString() ?? "unknown"
                    : version;
            }
            catch
            {
                return "unknown";
            }
        }

        private static string ShortName(string fullName)
        {
            int separator = fullName.LastIndexOf('.');
            return separator < 0 ? fullName : fullName.Substring(separator + 1);
        }
    }
}
