using System;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    public static class IslandConsoleCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("pirates", "blwii")]
        public static string Pirates(List<string> arguments)
        {
            string option = arguments != null && arguments.Count > 0
                ? arguments[0].Trim().ToLowerInvariant()
                : "status";

            switch (option)
            {
                case "on":
                case "enable":
                case "enabled":
                case "true":
                case "1":
                    return IslandFeatureBehavior.SetPiratesEnabled(true);

                case "off":
                case "disable":
                case "disabled":
                case "false":
                case "0":
                    return IslandFeatureBehavior.SetPiratesEnabled(false);

                case "status":
                    return IslandFeatureBehavior.GetPirateStatus();

                default:
                    return "Usage: blwii.pirates on|off|status";
            }
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("trees", "blwii")]
        public static string Trees(List<string> arguments)
        {
            string option = arguments != null && arguments.Count > 0
                ? arguments[0].Trim().ToLowerInvariant()
                : "status";

            switch (option)
            {
                case "on":
                case "enable":
                case "enabled":
                case "true":
                case "1":
                    return IslandTreeDecorator.SetEnabled(true);

                case "off":
                case "disable":
                case "disabled":
                case "false":
                case "0":
                    return IslandTreeDecorator.SetEnabled(false);

                case "status":
                    return IslandTreeDecorator.GetStatus();

                default:
                    return "Usage: blwii.trees on|off|status";
            }
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("compat", "blwii")]
        public static string Compatibility(List<string> arguments)
        {
            return IslandCompatibility.GetStatus();
        }
    }
}
