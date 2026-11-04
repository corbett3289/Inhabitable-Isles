using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace BLWarsailsInhabitableIslands
{
    // Redirect the single caller instead of prefixing the helper itself: Harmony's
    // reader cannot resolve the helper's removed MobileParty.get_Objective token.
    [HarmonyPatch]
    internal static class PlayerSettlementAiCompatibilityPatch
    {
        private const string FixesAssemblyName = "PlayerSettlementFixes";
        private const string FixesTypeName =
            "PlayerSettlementFixes.PlayerBuiltFortificationAiFix";
        private const string ExpectedFixesHash =
            "A74DD0ED13470240074DFE2A52295A33CB6235A1B6A0AE739FCD0B0D8DD8D1AC";
        private const string ExpectedHelperIlHash =
            "93586E5FF503276F4AD1AAD7E0AC089EC97ED8266C08B3CE5424FB3F1EC58399";
        private const string ExpectedCallerIlHash =
            "7B555C9BA2B9E20894118E9063CBE021F183DEAC0D101FF4200A9FF6CD2F1494";

        private static MethodInfo _brokenHelper;
        private static MethodInfo _caller;
        private static bool _checked;
        private static bool _required;
        private static bool _applied;
        private static int _redirectedCalls;
        private static string _status = "not checked";

        internal static string Status => _status;
        internal static bool IsReleaseReady => _checked && (!_required || _applied);

        internal static bool ShouldApply(out string reason)
        {
            if (_applied)
            {
                reason = null;
                return true;
            }

            _checked = true;
            _required = true;
            _caller = null;
            _brokenHelper = null;
            _redirectedCalls = 0;
            try
            {
                if (IslandCompatibility.RuntimeBannerlordVersion != "1.5.6" ||
                    IslandCompatibility.RuntimeWarSailsVersion != "1.3.6")
                {
                    return Block("requires Bannerlord 1.5.6 / War Sails 1.3.6", out reason);
                }

                Assembly playerSettlement = typeof(PlayerSettlementBehaviour).Assembly;
                if (!IsExactVersion(playerSettlement, new Version(7, 5, 0, 0)))
                {
                    return Block("unrecognized Player Settlement binary version", out reason);
                }

                Assembly[] candidates = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => assembly.GetName().Name == FixesAssemblyName)
                    .ToArray();
                if (candidates.Length != 1 ||
                    !IsExactVersion(candidates[0], new Version(1, 0, 0, 0)))
                {
                    return Block("requires one loaded PlayerSettlementFixes 1.0.0.0 assembly", out reason);
                }

                Assembly fixes = candidates[0];
                using (FileStream stream = File.OpenRead(fixes.Location))
                using (SHA256 hash = SHA256.Create())
                {
                    if (FormatHash(hash.ComputeHash(stream)) != ExpectedFixesHash)
                    {
                        return Block("unrecognized PlayerSettlementFixes binary fingerprint", out reason);
                    }
                }

                Type targetType = fixes.GetType(FixesTypeName, false);
                _brokenHelper = targetType?.GetMethod(
                    "IsUsableEnemyWarParty", BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(MobileParty) }, null);
                _caller = targetType?.GetMethod(
                    "TryAddPlayerBuiltSiegeScores", BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(MobileParty), typeof(PartyThinkParams),
                        typeof(float), typeof(bool), typeof(float) }, null);
                if (_brokenHelper == null || _brokenHelper.ReturnType != typeof(bool) ||
                    _caller == null || _caller.ReturnType != typeof(void) ||
                    GetIlHash(_brokenHelper) != ExpectedHelperIlHash ||
                    GetIlHash(_caller) != ExpectedCallerIlHash)
                {
                    return Block("unrecognized siege-scoring helper/caller signature or IL", out reason);
                }

                if (typeof(MobileParty).GetProperty("Objective",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null)
                {
                    _required = false;
                    _status = "not required (native Objective API is present)";
                    reason = _status;
                    return false;
                }

                _status = "required; awaiting verified caller redirect";
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                return Block(ex.GetType().Name + ": " + ex.Message, out reason);
            }
        }

        internal static bool VerifyApplied(Harmony harmony)
        {
            if (!_required)
            {
                return IsReleaseReady;
            }

            if (_caller == null)
            {
                return false;
            }

            try
            {
                Patches patches = Harmony.GetPatchInfo(_caller);
                _applied = _redirectedCalls == 1 && harmony != null && patches != null &&
                    patches.Transpilers.Any(patch => patch.owner == harmony.Id &&
                        patch.PatchMethod.DeclaringType == typeof(PlayerSettlementAiCompatibilityPatch) &&
                        patch.PatchMethod.Name == nameof(Transpiler));
                _status = _applied
                    ? "active (verified 1.5.6 siege-scoring caller redirect)"
                    : "blocked: required siege-scoring caller redirect was not installed";
            }
            catch (Exception ex)
            {
                _applied = false;
                _status = "blocked: patch verification failed: " + ex.GetType().Name;
            }

            return _applied;
        }

        [HarmonyPrepare]
        private static bool Prepare()
        {
            return ShouldApply(out string _);
        }

        private static MethodBase TargetMethod()
        {
            return _caller ?? throw new InvalidOperationException(
                "Player Settlement AI compatibility target was not validated.");
        }

        [HarmonyTranspiler]
        [HarmonyPriority(Priority.First)]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> source)
        {
            List<CodeInstruction> instructions = source.ToList();
            List<CodeInstruction> calls = instructions
                .Where(instruction => instruction.Calls(_brokenHelper))
                .ToList();
            if (calls.Count != 1)
            {
                throw new InvalidOperationException(
                    "Expected exactly one Player Settlement AI helper call; found " + calls.Count + ".");
            }

            calls[0].opcode = OpCodes.Call;
            calls[0].operand = AccessTools.Method(
                typeof(PlayerSettlementAiCompatibilityPatch), nameof(IsUsableEnemyWarParty));
            _redirectedCalls = 1;
            return instructions;
        }

        private static bool IsUsableEnemyWarParty(MobileParty party)
        {
            // These are the original helper's filters. Only its Defensive
            // PartyObjective exclusion is omitted because 1.5.6 removed that API.
            if (party == null || party.IsMilitia || party.IsCaravan || party.IsVillager ||
                party.IsBandit || party.IsPatrolParty || party.IsDisbanding ||
                party.LeaderHero == null || party.MapFaction == null ||
                !party.MapFaction.IsKingdomFaction || party.CurrentSettlement?.SiegeEvent != null)
            {
                return false;
            }

            return party.Army == null || party.Army.LeaderParty == party;
        }

        private static bool IsExactVersion(Assembly assembly, Version expected)
        {
            return assembly.GetName().Version == expected &&
                Version.TryParse(FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion,
                    out Version fileVersion) && fileVersion == expected;
        }

        private static string GetIlHash(MethodInfo method)
        {
            byte[] bytes = method.GetMethodBody()?.GetILAsByteArray();
            if (bytes == null)
            {
                return null;
            }

            using (SHA256 hash = SHA256.Create())
            {
                return FormatHash(hash.ComputeHash(bytes));
            }
        }

        private static string FormatHash(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        private static bool Block(string reason, out string skipReason)
        {
            _status = "blocked: " + reason;
            skipReason = _status;
            return false;
        }
    }
}
