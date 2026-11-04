using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace BLWarsailsInhabitableIslands
{
    internal sealed class SubModule : MBSubModuleBase
    {
        private const string HarmonyId = "corbeau.bannerlord.warsails.inhabitableislands";
        private Harmony _harmony;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();

            try
            {
                IslandCompatibility.RefreshRuntimeVersions();
                _harmony = new Harmony(HarmonyId);
                ApplyHarmonyPatches(typeof(SubModule).Assembly);
            }
            catch (Exception ex)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Patch bootstrap failed: " +
                    ex);
            }
        }

        protected override void OnSubModuleUnloaded()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch (Exception ex)
            {
                Debug.Print("[BLWarsailsInhabitableIslands] Failed to remove Harmony patches: " + ex);
            }

            base.OnSubModuleUnloaded();
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            IslandTravelController.Tick(dt);
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);

            if (gameStarterObject is CampaignGameStarter campaignStarter)
            {
                IslandCompatibility.WarnIfRuntimeUnsupported();
                campaignStarter.AddBehavior(new IslandFeatureBehavior());
            }
        }

        private void ApplyHarmonyPatches(Assembly assembly)
        {
            Type[] patchTypes = assembly
                .GetTypes()
                .Where(
                    type => type.GetCustomAttributes(
                        typeof(HarmonyPatch),
                        false).Length > 0)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToArray();

            IslandCompatibility.BeginPatchRegistration(patchTypes.Length);
            foreach (Type patchType in patchTypes)
            {
                if (!IslandCompatibility.ShouldApplyPatch(
                        patchType,
                        out string skipReason))
                {
                    IslandCompatibility.RecordPatchSkipped(
                        patchType,
                        skipReason);
                    continue;
                }

                try
                {
                    _harmony.CreateClassProcessor(patchType).Patch();
                    IslandCompatibility.RecordPatchSuccess(patchType);
                }
                catch (Exception ex)
                {
                    IslandCompatibility.RecordPatchFailure(patchType);
                    Debug.Print(
                        "[BLWarsailsInhabitableIslands] Disabled incompatible patch " +
                        patchType.FullName + ": " + ex);
                }
            }

            PlayerSettlementAiCompatibilityPatch.VerifyApplied(_harmony);
            Debug.Print(
                "[BLWarsailsInhabitableIslands] " +
                IslandCompatibility.GetStatus());
        }
    }
}
