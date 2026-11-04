using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.ModuleManager;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandCompatibility
    {
        internal const string ReleaseVersion = "0.9.24-beta.1";
        internal const string TargetBannerlordVersion = "1.5.6";
        internal const string TargetWarSailsVersion = "1.3.6";
        internal const string TargetPlayerSettlementVersion = "7.5.0";

        private static readonly List<string> FailedPatchTypes =
            new List<string>();
        private static readonly List<string> SkippedPatchTypes =
            new List<string>();

        private static int _expectedPatchTypes;
        private static int _appliedPatchTypes;
        private static ApplicationVersion? _runtimeBannerlordVersion;
        private static ApplicationVersion? _runtimeWarSailsVersion;
        private static string _versionResolutionIssue;
        private static bool _loggedVersionResolutionFailure;
        private static bool _compatibilityWarningShown;

        internal static void RefreshRuntimeVersions()
        {
            _runtimeBannerlordVersion = ResolveModuleVersion(
                "Native",
                out string bannerlordFailure);
            _runtimeWarSailsVersion = ResolveModuleVersion(
                "NavalDLC",
                out string warSailsFailure);

            List<string> failures = new List<string>();
            if (!string.IsNullOrWhiteSpace(bannerlordFailure))
            {
                failures.Add(bannerlordFailure);
            }

            if (!string.IsNullOrWhiteSpace(warSailsFailure))
            {
                failures.Add(warSailsFailure);
            }

            _versionResolutionIssue = failures.Count == 0
                ? null
                : string.Join("; ", failures);
            if (_versionResolutionIssue != null &&
                !_loggedVersionResolutionFailure)
            {
                _loggedVersionResolutionFailure = true;
                TaleWorlds.Library.Debug.Print(
                    "[BLWarsailsInhabitableIslands] Could not fully resolve " +
                    "runtime module versions: " + _versionResolutionIssue);
            }
        }

        internal static void WarnIfRuntimeUnsupported()
        {
            RefreshRuntimeVersions();
            string compatibility = RuntimeCompatibility;
            if (_compatibilityWarningShown || compatibility == "supported")
            {
                return;
            }

            _compatibilityWarningShown = true;
            InformationManager.DisplayMessage(
                new InformationMessage(
                    "Inhabitable Isles v" + ReleaseVersion + " supports " +
                    "Bannerlord " + TargetBannerlordVersion + " / War Sails " +
                    TargetWarSailsVersion + ". Detected Bannerlord " +
                    RuntimeBannerlordVersion + " / War Sails " +
                    RuntimeWarSailsVersion + " (" + compatibility +
                    "). Back up this save and match the required versions " +
                    "before continuing."));
        }

        internal static void BeginPatchRegistration(int expectedPatchTypes)
        {
            _expectedPatchTypes = expectedPatchTypes;
            _appliedPatchTypes = 0;
            FailedPatchTypes.Clear();
            SkippedPatchTypes.Clear();
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

        internal static void RecordPatchSkipped(Type patchType, string reason)
        {
            string name = ShortName(patchType?.FullName ?? "<unknown patch>");
            string entry = string.IsNullOrWhiteSpace(reason)
                ? name
                : name + " (" + reason + ")";
            if (!SkippedPatchTypes.Contains(entry))
            {
                SkippedPatchTypes.Add(entry);
            }
        }

        internal static bool ShouldApplyPatch(Type patchType, out string reason)
        {
            reason = null;
            if (patchType == null)
            {
                return true;
            }

            if (patchType == typeof(PlayerSettlementAiCompatibilityPatch))
            {
                return PlayerSettlementAiCompatibilityPatch.ShouldApply(out reason);
            }

            if (patchType == typeof(IslandSailingSoundTickPatch) ||
                patchType == typeof(IslandHideNavalVisualAudioPatch))
            {
                if (!RuntimeVersionsKnown)
                {
                    reason = "unknown runtime versions; optional campaign-audio patch skipped";
                    return false;
                }

                if (UseNativeSailingAudio)
                {
                    reason = "native Bannerlord 1.5 campaign-audio handling";
                    return false;
                }
            }

            return true;
        }

        internal static string RuntimeBannerlordVersion =>
            ModuleVersionRules.Format(_runtimeBannerlordVersion);

        internal static string RuntimeWarSailsVersion =>
            ModuleVersionRules.Format(_runtimeWarSailsVersion);

        internal static bool RuntimeVersionsKnown =>
            ModuleVersionRules.IsKnown(_runtimeBannerlordVersion) &&
            ModuleVersionRules.IsKnown(_runtimeWarSailsVersion);

        internal static string RuntimeCompatibility =>
            !RuntimeVersionsKnown
                ? "unknown"
                : ModuleVersionRules.Matches(_runtimeBannerlordVersion, 1, 5, 6) &&
                  ModuleVersionRules.Matches(_runtimeWarSailsVersion, 1, 3, 6)
                    ? "supported"
                    : "unsupported";

        internal static bool UseNativeSailingAudio =>
            ModuleVersionRules.IsAtLeast(
                _runtimeBannerlordVersion,
                1,
                5,
                0);

        internal static bool UsesNativeFirstTravel =>
            ModuleVersionRules.IsAtLeast(
                _runtimeBannerlordVersion,
                1,
                5,
                0) &&
            ModuleVersionRules.IsAtLeast(
                _runtimeWarSailsVersion,
                1,
                3,
                0);

        internal static string GetStatus()
        {
            string failures = FailedPatchTypes.Count == 0
                ? "none"
                : string.Join(", ", FailedPatchTypes.Select(ShortName));
            string skipped = SkippedPatchTypes.Count == 0
                ? "none"
                : string.Join(", ", SkippedPatchTypes);
            string transitionMode = IslandTravelController.SupportsAnimatedTransitions
                ? "animated with public fallback"
                : "public fallback only";
            string profile = UsesNativeFirstTravel
                ? "1.5 native-first"
                : "legacy island compatibility";

            return string.Format(
                "Inhabitable Isles v{0}; compiled target: Bannerlord {1}, " +
                "War Sails {2}; runtime Bannerlord {3}, War Sails {4}; " +
                "runtime compatibility: {5}; runtime Player Settlement {6} " +
                "(target {7}), Harmony {8}; profile: {9}; fleet policy: {10}; " +
                "patches applied {11}/{12}; skipped patches: {13}; failed " +
                "patches: {14}; imported island surfaces: {15}; transitions: " +
                "{16}; temporary ferries: {17}; trees: {18} ({19} active); " +
                "version resolution: {20}; Player Settlement AI: {21}; " +
                "settlement creation repair: {22}.",
                ReleaseVersion,
                TargetBannerlordVersion,
                TargetWarSailsVersion,
                RuntimeBannerlordVersion,
                RuntimeWarSailsVersion,
                RuntimeCompatibility,
                GetFileVersion(typeof(PlayerSettlementBehaviour)),
                TargetPlayerSettlementVersion,
                GetFileVersion(typeof(Harmony)),
                profile,
                IslandVillageLogistics.GetFleetPolicyStatus(),
                _appliedPatchTypes,
                _expectedPatchTypes,
                skipped,
                failures,
                IslandNavMeshOverlay.ImportedSurfaceCount,
                transitionMode,
                IslandVillageLogistics.GetTrackedFerryCount(),
                IslandTreeDecorator.IsEnabled ? "enabled" : "disabled",
                IslandTreeDecorator.InstanceCount,
                _versionResolutionIssue ?? "none",
                PlayerSettlementAiCompatibilityPatch.Status,
                IslandFeatureBehavior.GetSettlementCreationRepairStatus());
        }

        private static ApplicationVersion? ResolveModuleVersion(
            string moduleId,
            out string failureReason)
        {
            try
            {
                ModuleInfo module = ModuleHelper.GetModuleInfo(moduleId);
                if (module == null)
                {
                    failureReason = moduleId + " metadata is unavailable";
                    return null;
                }

                ApplicationVersion version = module.Version;
                if (!ModuleVersionRules.IsKnown(version))
                {
                    failureReason = moduleId + " version is unavailable";
                    return null;
                }

                failureReason = null;
                return version;
            }
            catch (Exception ex)
            {
                failureReason = moduleId + " version lookup failed: " +
                                ex.GetType().Name + ": " + ex.Message;
                return null;
            }
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
