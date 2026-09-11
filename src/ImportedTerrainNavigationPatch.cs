using HarmonyLib;
using NavalDLC.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace BLWarsailsInhabitableIslands
{
    [HarmonyPatch(
        typeof(NavalPartyNavigationModel),
        nameof(NavalPartyNavigationModel.IsTerrainTypeValidForNavigationType))]
    internal static class ImportedTerrainNavigationPatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            TerrainType terrainType,
            MobileParty.NavigationType navigationType,
            ref bool __result)
        {
            if (__result ||
                (int)terrainType != 0 ||
                !IslandNavMeshOverlay.HasImportedLandSurfaces)
            {
                return;
            }

            if (navigationType == MobileParty.NavigationType.Default ||
                navigationType == MobileParty.NavigationType.All)
            {
                __result = true;
            }
        }
    }
}
