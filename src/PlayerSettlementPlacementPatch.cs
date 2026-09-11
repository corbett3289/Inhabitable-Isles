using System;
using System.Reflection;
using BannerlordPlayerSettlement.Behaviours;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace BLWarsailsInhabitableIslands
{
    [HarmonyPatch(typeof(PlayerSettlementBehaviour), "OnBeforeTick")]
    internal static class PlayerSettlementPlacementPatch
    {
        private static readonly FieldInfo SettlementPlacementFrameField =
            AccessTools.Field(typeof(PlayerSettlementBehaviour), "settlementPlacementFrame");

        private static readonly FieldInfo PortPlacementFrameField =
            AccessTools.Field(typeof(PlayerSettlementBehaviour), "portPlacementFrame");

        private static bool _loggedSettlementOverride;
        private static bool _loggedPortOverride;

        private static void Prefix(PlayerSettlementBehaviour __instance)
        {
            TryAllowCurrentPlacement(__instance);
        }

        private static void Postfix(PlayerSettlementBehaviour __instance)
        {
            TryAllowCurrentPlacement(__instance);
        }

        public static void TryAllowCurrentPlacement(PlayerSettlementBehaviour instance)
        {
            if (instance == null || instance.PlacementSupported || !BannerlordPlayerSettlement.Main.IsWarSails)
            {
                return;
            }

            try
            {
                if (instance.IsPlacingSettlement && ShouldAllowSettlementPlacement(instance))
                {
                    instance.PlacementSupported = true;
                    SetAllowedCursor();
                    LogOnce(ref _loggedSettlementOverride, "settlement");
                    return;
                }

                if (instance.IsPlacingPort && ShouldAllowPortPlacement(instance))
                {
                    instance.PlacementSupported = true;
                    SetAllowedCursor();
                    LogOnce(ref _loggedPortOverride, "port");
                }
            }
            catch (Exception ex)
            {
                Debug.Print("[BLWarsailsInhabitableIslands] Placement override failed: " + ex);
            }
        }

        private static bool ShouldAllowSettlementPlacement(PlayerSettlementBehaviour instance)
        {
            if (!TryGetFrame(instance, SettlementPlacementFrameField, out MatrixFrame frame))
            {
                return false;
            }

            Vec2 position = frame.origin.AsVec2;
            return IslandZoneSettings.Contains(position) &&
                   !IslandFeatureBehavior.IsActiveHideoutOnIsland(position) &&
                   IsValidForNavigation(position, MobileParty.NavigationType.Default);
        }

        private static bool ShouldAllowPortPlacement(PlayerSettlementBehaviour instance)
        {
            if (!TryGetFrame(instance, PortPlacementFrameField, out MatrixFrame frame))
            {
                return false;
            }

            Vec2 position = frame.origin.AsVec2;
            return IslandZoneSettings.Contains(position) && IsValidForNavigation(position, MobileParty.NavigationType.Naval);
        }

        private static bool TryGetFrame(PlayerSettlementBehaviour instance, FieldInfo field, out MatrixFrame frame)
        {
            frame = MatrixFrame.Identity;
            object value = field == null ? null : field.GetValue(instance);
            if (value is MatrixFrame matrixFrame)
            {
                frame = matrixFrame;
                return true;
            }

            return false;
        }

        private static bool IsValidForNavigation(Vec2 position, MobileParty.NavigationType navigationType)
        {
            Campaign campaign = Campaign.Current;
            if (campaign == null || campaign.MapSceneWrapper == null)
            {
                return false;
            }

            CampaignVec2 campaignPosition = new CampaignVec2(position, navigationType != MobileParty.NavigationType.Naval);
            return campaignPosition.IsValid() && NavigationHelper.IsPositionValidForNavigationType(campaignPosition, navigationType);
        }

        private static void SetAllowedCursor()
        {
            ScreenBase topScreen = ScreenManager.TopScreen;
            ScreenLayer layer = topScreen?.Layers == null || topScreen.Layers.Count == 0 ? null : topScreen.Layers[topScreen.Layers.Count - 1];
            if (layer != null)
            {
                layer.ActiveCursor = CursorType.Default;
            }
        }

        private static void LogOnce(ref bool alreadyLogged, string placementKind)
        {
            if (alreadyLogged)
            {
                return;
            }

            alreadyLogged = true;
            Debug.Print("[BLWarsailsInhabitableIslands] Enabled Player Settlement " + placementKind + " placement in configured War Sails island zone.");
        }
    }
}
