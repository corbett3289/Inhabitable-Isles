param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$framework = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1"
$csc = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
$testRoot = Join-Path $env:TEMP ("blwii-fleet-tests-" + [guid]::NewGuid().ToString("n"))
$testSource = Join-Path $testRoot "FleetCompatibilityTests.cs"
$testBinary = Join-Path $testRoot "FleetCompatibilityTests.exe"
$responseFile = Join-Path $testRoot "FleetCompatibilityTests.rsp"
$librarySource = Join-Path $repo "src\OptionalBooleanPropertyAccessor.cs"
$versionSource = Join-Path $repo "src\ModuleVersionRules.cs"
$taleWorldsLibrary = Join-Path $GameRoot "bin\Win64_Shipping_Client\TaleWorlds.Library.dll"

New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    @'
using System;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal static class FleetCompatibilityTests
    {
        private sealed class PresentTrue
        {
            public bool CanHaveFleet => true;
        }

        private sealed class PresentFalse
        {
            public bool CanHaveFleet => false;
        }

        private sealed class Missing
        {
        }

        private sealed class WrongType
        {
            public int CanHaveFleet => 1;
        }

        private sealed class StaticGetter
        {
            public static bool CanHaveFleet => true;
        }

        private sealed class ThrowingGetter
        {
            public bool CanHaveFleet => throw new InvalidOperationException("fixture failure");
        }

        private static int Main()
        {
            AssertValue(typeof(PresentTrue), new PresentTrue(), true);
            AssertValue(typeof(PresentFalse), new PresentFalse(), false);
            AssertUnavailable(typeof(Missing));
            AssertUnavailable(typeof(WrongType));
            AssertUnavailable(typeof(StaticGetter));
            AssertThrowingGetterIsContained();
            AssertVersionRules();
            Console.WriteLine("Fleet compatibility regression tests passed.");
            return 0;
        }

        private static void AssertValue(Type type, object instance, bool expected)
        {
            var accessor = new OptionalBooleanPropertyAccessor(type, "CanHaveFleet");
            if (!accessor.IsAvailable ||
                !accessor.TryGetValue(instance, out bool actual, out string failure) ||
                actual != expected || failure != null)
            {
                throw new InvalidOperationException("Expected readable Boolean getter on " + type.Name);
            }

            if (!accessor.TryGetValue(instance, out actual, out failure) ||
                actual != expected)
            {
                throw new InvalidOperationException("Repeated cached getter read failed on " + type.Name);
            }
        }

        private static void AssertUnavailable(Type type)
        {
            var accessor = new OptionalBooleanPropertyAccessor(type, "CanHaveFleet");
            if (accessor.IsAvailable ||
                accessor.TryGetValue(Activator.CreateInstance(type), out _, out string failure) ||
                string.IsNullOrWhiteSpace(failure))
            {
                throw new InvalidOperationException("Expected unavailable getter on " + type.Name);
            }
        }

        private static void AssertThrowingGetterIsContained()
        {
            var accessor = new OptionalBooleanPropertyAccessor(
                typeof(ThrowingGetter),
                "CanHaveFleet");
            if (!accessor.IsAvailable ||
                accessor.TryGetValue(new ThrowingGetter(), out _, out string failure) ||
                failure == null ||
                !failure.Contains("fixture failure"))
            {
                throw new InvalidOperationException("Throwing getter was not contained");
            }
        }

        private static void AssertVersionRules()
        {
            var supported = new ApplicationVersion(
                ApplicationVersionType.Release,
                1,
                5,
                2,
                999999);
            var legacy = new ApplicationVersion(
                ApplicationVersionType.Release,
                1,
                4,
                8,
                0);
            var invalid = new ApplicationVersion(
                ApplicationVersionType.Invalid,
                1,
                5,
                2,
                0);

            if (!ModuleVersionRules.Matches(supported, 1, 5, 2) ||
                !ModuleVersionRules.IsAtLeast(supported, 1, 5, 0) ||
                ModuleVersionRules.Matches(legacy, 1, 5, 2) ||
                ModuleVersionRules.IsAtLeast(legacy, 1, 5, 0) ||
                ModuleVersionRules.IsKnown(null) ||
                ModuleVersionRules.IsKnown(invalid) ||
                ModuleVersionRules.Format(null) != "unknown")
            {
                throw new InvalidOperationException("Module version rules failed");
            }
        }
    }
}
'@ | Set-Content -LiteralPath $testSource -Encoding ASCII

    Copy-Item -LiteralPath $taleWorldsLibrary -Destination $testRoot
    $references = @(
        (Join-Path $framework "mscorlib.dll"),
        (Join-Path $framework "System.dll"),
        (Join-Path $framework "System.Core.dll"),
        (Join-Path $framework "Facades\netstandard.dll"),
        $taleWorldsLibrary
    ) | ForEach-Object { '/reference:"{0}"' -f $_ }
    $compilerArguments = @(
        "/nologo",
        "/target:exe",
        "/platform:x64",
        "/langversion:12.0",
        "/nostdlib+",
        ('/out:"{0}"' -f $testBinary)
    ) + $references + @(
        ('"{0}"' -f $librarySource),
        ('"{0}"' -f $versionSource),
        ('"{0}"' -f $testSource)
    )

    $compilerArguments | Set-Content -LiteralPath $responseFile -Encoding ASCII
    & $csc ("@" + $responseFile)
    if ($LASTEXITCODE -ne 0) {
        throw "Fleet compatibility test compilation failed with exit code $LASTEXITCODE"
    }

    & $testBinary
    if ($LASTEXITCODE -ne 0) {
        throw "Fleet compatibility tests failed with exit code $LASTEXITCODE"
    }
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
