using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class ModuleVersionRules
    {
        internal static bool IsKnown(ApplicationVersion? version)
        {
            return version.HasValue &&
                   version.Value.ApplicationVersionType !=
                       ApplicationVersionType.Invalid &&
                   version.Value.Major > 0 &&
                   version.Value.Minor >= 0 &&
                   version.Value.Revision >= 0;
        }

        internal static bool Matches(
            ApplicationVersion? version,
            int major,
            int minor,
            int revision)
        {
            return IsKnown(version) &&
                   version.Value.Major == major &&
                   version.Value.Minor == minor &&
                   version.Value.Revision == revision;
        }

        internal static bool IsAtLeast(
            ApplicationVersion? version,
            int major,
            int minor,
            int revision)
        {
            if (!IsKnown(version))
            {
                return false;
            }

            ApplicationVersion actual = version.Value;
            if (actual.Major != major)
            {
                return actual.Major > major;
            }

            if (actual.Minor != minor)
            {
                return actual.Minor > minor;
            }

            return actual.Revision >= revision;
        }

        internal static string Format(ApplicationVersion? version)
        {
            return IsKnown(version)
                ? version.Value.Major + "." +
                  version.Value.Minor + "." +
                  version.Value.Revision
                : "unknown";
        }
    }
}
