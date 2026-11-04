using System;
using System.Reflection;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;
using SandBox.View.Map;
using SandBox.View.Map.Visuals;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    [HarmonyPatch(typeof(PlayerSettlementBehaviour), "ApplyNow")]
    internal static class PlayerSettlementApplyPatch
    {
        private static void Prefix(PlayerSettlementBehaviour __instance)
        {
            PlayerSettlementPlacementPatch.TryAllowCurrentPlacement(__instance);
        }

        private static void Postfix()
        {
            IslandSettlementIntegration.RepairAll();
            IslandTreeDecorator.RefreshSettlementClearings();
        }
    }

    [HarmonyPatch(typeof(MapScreen), "HandleLeftMouseButtonClick")]
    internal static class PlayerSettlementMapClickPatch
    {
        private static readonly MethodInfo HandleClickTimeChangeMethod =
            ResolveClickTimeChangeMethod();

        internal static bool SupportsNativeClickTimeControl =>
            HandleClickTimeChangeMethod != null;

        private static MethodInfo ResolveClickTimeChangeMethod()
        {
            MethodInfo method = AccessTools.Method(
                typeof(MapScreen),
                "HandleClickTimeChange",
                new[] { typeof(bool) });
            return method != null &&
                   !method.IsStatic &&
                   method.ReturnType == typeof(void)
                ? method
                : null;
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(
            MapScreen __instance,
            MapEntityVisual visualOfSelectedEntity,
            ref CampaignVec2 intersectionPoint,
            bool isDoubleClick)
        {
            PlayerSettlementBehaviour instance = PlayerSettlementBehaviour.Instance;
            if (instance != null &&
                (instance.IsPlacingSettlement || instance.IsPlacingGate))
            {
                if (!intersectionPoint.ToVec2().IsValid)
                {
                    return true;
                }

                CampaignVec2 landPoint = intersectionPoint.IsOnLand
                    ? intersectionPoint
                    : new CampaignVec2(intersectionPoint.ToVec2(), true);

                if (IslandZoneSettings.Contains(landPoint.ToVec2()) && landPoint.IsValid())
                {
                    intersectionPoint = landPoint;
                }

                return true;
            }

            if (!(visualOfSelectedEntity is SettlementVisual settlementVisual))
            {
                return true;
            }

            // Native HandleMouse dispatches MapParleyModifier clicks separately
            // before this method. Preserve that hotkey (and custom bindings),
            // follow clicks, and Ctrl+click cheats if another caller gets here.
            if (!SupportsNativeClickTimeControl ||
                __instance == null ||
                __instance.SceneLayer?.Input == null ||
                __instance.MapState == null ||
                __instance.MapState.AtMenu ||
                __instance.MapCameraView == null ||
                !__instance.MapCameraView.ProcessCameraInput ||
                __instance.Input.IsControlDown() ||
                __instance.SceneLayer.Input.IsHotKeyDown("MapParleyModifier") ||
                __instance.SceneLayer.Input.IsHotKeyDown("MapFollowModifier"))
            {
                return true;
            }

            Settlement settlement = settlementVisual.MapEntity?.Settlement;
            MobileParty mainParty = MobileParty.MainParty;
            if (settlement == null ||
                mainParty == null ||
                mainParty.MapEvent != null ||
                mainParty.IsTransitionInProgress ||
                !mainParty.HasNavalNavigationCapability ||
                !settlement.HasPort ||
                !IslandSettlementIntegration.IsPlayerSettlement(settlement) ||
                !IslandSettlementIntegration.IsIslandSettlement(
                    settlement,
                    out IslandRecord targetIsland))
            {
                return true;
            }

            bool onSameIsland =
                !mainParty.IsCurrentlyAtSea &&
                IslandNavMeshOverlay.TryGetIsland(
                    mainParty.Position,
                    out IslandRecord currentIsland) &&
                currentIsland.Id == targetIsland.Id;
            if (onSameIsland)
            {
                return true;
            }

            try
            {
                mainParty.SetMoveGoToSettlement(
                    settlement,
                    mainParty.IsCurrentlyAtSea
                        ? MobileParty.NavigationType.Naval
                        : MobileParty.NavigationType.All,
                    true);
                mainParty.ForceAiNoPathMode = false;
                HandleClickTimeChangeMethod.Invoke(__instance, new object[] { isDoubleClick });
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Routed remote map click through port for " +
                    settlement.StringId + ".");
                return false;
            }
            catch (Exception ex)
            {
                Debug.Print(
                    "[BLWarsailsInhabitableIslands] Remote settlement click failed; " +
                    "using native click handling: " + ex);
                return true;
            }
        }
    }
}
