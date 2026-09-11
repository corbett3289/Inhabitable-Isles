using HarmonyLib;
using NavalDLC.View.Map.Visuals;
using TaleWorlds.Engine;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class IslandSailingAudio
    {
        private static readonly AccessTools.FieldRef<
            NavalMobilePartyVisual,
            SoundEvent> SailingSoundEvent =
            AccessTools.FieldRefAccess<NavalMobilePartyVisual, SoundEvent>(
                "_sailingSoundEvent");

        private static bool _loggedFailure;

        internal static bool ShouldPlay(NavalMobilePartyVisual visual)
        {
            try
            {
                var party = visual?.MapEntity?.MobileParty;
                return party == null || party.IsCurrentlyAtSea;
            }
            catch (System.Exception ex)
            {
                LogFailure(ex);
                return true;
            }
        }

        internal static void Stop(NavalMobilePartyVisual visual)
        {
            if (visual == null)
            {
                return;
            }

            try
            {
                SailingSoundEvent(visual)?.Stop();
            }
            catch (System.Exception ex)
            {
                LogFailure(ex);
            }
        }

        private static void LogFailure(System.Exception ex)
        {
            if (_loggedFailure)
            {
                return;
            }

            _loggedFailure = true;
            Debug.Print(
                "[BLWarsailsInhabitableIslands] Could not synchronize sailing audio: " +
                ex);
        }
    }

    [HarmonyPatch(typeof(NavalMobilePartyVisual), "TickSailingSound")]
    internal static class IslandSailingSoundTickPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(NavalMobilePartyVisual __instance)
        {
            if (IslandSailingAudio.ShouldPlay(__instance))
            {
                return true;
            }

            IslandSailingAudio.Stop(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(NavalMobilePartyVisual), "HideNavalVisual")]
    internal static class IslandHideNavalVisualAudioPatch
    {
        [HarmonyPostfix]
        private static void Postfix(NavalMobilePartyVisual __instance)
        {
            IslandSailingAudio.Stop(__instance);
        }
    }
}
