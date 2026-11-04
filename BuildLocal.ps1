param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord",
    [string]$PlayerSettlementRoot = "C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3720376888",
    [string]$ExpectedBannerlordVersion = "1.5.6",
    [string]$ExpectedWarSailsVersion = "1.3.6",
    [string]$ExpectedPlayerSettlementVersion = "7.5.0",
    [string]$HarmonyRoot = "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules\Bannerlord.Harmony",
    [switch]$AllowVersionMismatch,
    [switch]$Deploy,
    [string]$DeployRoot
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$game = $GameRoot
$framework = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1"
$csc = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
$moduleId = "BLWarsailsInhabitableIslands"
$output = Join-Path $repo "Module\bin\Win64_Shipping_Client\$moduleId.dll"

function Get-ModuleVersion($moduleRoot) {
    $subModulePath = Join-Path $moduleRoot "SubModule.xml"
    if (-not (Test-Path -LiteralPath $subModulePath)) {
        throw "Missing module manifest: $subModulePath"
    }

    [xml]$manifest = Get-Content -LiteralPath $subModulePath -Raw
    $versionNode = $manifest.SelectSingleNode("/Module/Version")
    if ($null -eq $versionNode) {
        throw "No module version found in $subModulePath"
    }

    return $versionNode.GetAttribute("value").TrimStart("v")
}

function Assert-ModuleVersion($name, $moduleRoot, $expectedVersion) {
    $actualVersion = Get-ModuleVersion $moduleRoot
    if ($actualVersion -ne $expectedVersion) {
        $message = "$name version mismatch: expected $expectedVersion, found $actualVersion at $moduleRoot"
        if ($AllowVersionMismatch) {
            Write-Warning $message
        } else {
            throw $message
        }
    }
}

function Add-ManagedDlls($dir, $pattern = "*.dll") {
    if (Test-Path -LiteralPath $dir) {
        foreach ($dll in Get-ChildItem -LiteralPath $dir -Filter $pattern -File) {
            try {
                [void][Reflection.AssemblyName]::GetAssemblyName($dll.FullName)
                $dll.FullName
            } catch {
            }
        }
    }
}

New-Item -ItemType Directory -Force -Path (Split-Path $output) | Out-Null

Assert-ModuleVersion "Bannerlord" (Join-Path $game "Modules\Native") $ExpectedBannerlordVersion
Assert-ModuleVersion "War Sails" (Join-Path $game "Modules\NavalDLC") $ExpectedWarSailsVersion
Assert-ModuleVersion "Player Settlement" $PlayerSettlementRoot $ExpectedPlayerSettlementVersion

$refFiles = @()
$refFiles += Add-ManagedDlls $framework
$refFiles += Add-ManagedDlls (Join-Path $framework "Facades")

$gameBin = Join-Path $game "bin\Win64_Shipping_Client"
$refFiles += Add-ManagedDlls $gameBin "TaleWorlds*.dll" | Where-Object { [IO.Path]::GetFileName($_) -ne "TaleWorlds.Native.dll" }
$refFiles += Join-Path $gameBin "Newtonsoft.Json.dll"

foreach ($module in @("Native", "SandBox", "SandBoxCore", "StoryMode", "CustomBattle", "BirthAndDeath", "NavalDLC")) {
    $dir = Join-Path $game "Modules\$module\bin\Win64_Shipping_Client"
    $refFiles += Add-ManagedDlls $dir | Where-Object {
        [IO.Path]::GetFileName($_) -notmatch "^(System|Microsoft)\." -and
        [IO.Path]::GetFileName($_) -ne "TaleWorlds.Native.dll"
    }
}

$harmonyBin = Join-Path $HarmonyRoot "bin\Win64_Shipping_Client"
if (-not (Test-Path -LiteralPath (Join-Path $harmonyBin "0Harmony.dll"))) {
    throw "Missing Harmony library in selected HarmonyRoot: $HarmonyRoot"
}
$refFiles += Add-ManagedDlls $harmonyBin | Where-Object {
    [IO.Path]::GetFileName($_) -notmatch "^(System|Microsoft)\."
}

$playerSettlementBin = Join-Path $PlayerSettlementRoot "bin\Win64_Shipping_Client"
$refFiles += Join-Path $playerSettlementBin "PlayerSettlement.dll"
$refFiles += Join-Path $playerSettlementBin "PlayerSettlementFixes.dll"

$refs = $refFiles |
    Where-Object { Test-Path -LiteralPath $_ } |
    Sort-Object -Unique |
    ForEach-Object { '/reference:"{0}"' -f $_ }

$sources = Get-ChildItem -LiteralPath (Join-Path $repo "src") -Recurse -Filter "*.cs" -File
$sources += Get-ChildItem -LiteralPath (Join-Path $repo "Properties") -Recurse -Filter "*.cs" -File
$sourceArgs = $sources | ForEach-Object { '"{0}"' -f $_.FullName }

$rsp = Join-Path $env:TEMP ("bl-islands-csc-" + [guid]::NewGuid().ToString("n") + ".rsp")
$args = @(
    "/nologo",
    "/target:library",
    "/platform:x64",
    "/langversion:12.0",
    "/warn:4",
    "/optimize+",
    "/debug:full",
    "/deterministic+",
    "/nostdlib+",
    ('/out:"{0}"' -f $output)
) + $refs + $sourceArgs

$args | Set-Content -LiteralPath $rsp -Encoding ASCII
try {
    & $csc ("@" + $rsp)
    if ($LASTEXITCODE -ne 0) {
        throw "csc failed with exit code $LASTEXITCODE"
    }
} finally {
    Remove-Item -LiteralPath $rsp -Force -ErrorAction SilentlyContinue
}

if ($Deploy) {
    if ([string]::IsNullOrWhiteSpace($DeployRoot)) {
        throw "-Deploy requires an explicit -DeployRoot. No Workshop destination is selected automatically."
    }

    $deployModule = [IO.Path]::GetFullPath($DeployRoot)
    New-Item -ItemType Directory -Force -Path $deployModule | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $deployModule "bin\Win64_Shipping_Client") | Out-Null
    Copy-Item -LiteralPath (Join-Path $repo "Module\SubModule.xml") -Destination (Join-Path $deployModule "SubModule.xml") -Force
    Copy-Item -LiteralPath (Join-Path $repo "Module\ModuleData") -Destination $deployModule -Recurse -Force
    try {
        Copy-Item -LiteralPath (Join-Path $repo "Module\bin\Win64_Shipping_Client\$moduleId.dll") -Destination (Join-Path $deployModule "bin\Win64_Shipping_Client\$moduleId.dll") -Force
        Copy-Item -LiteralPath (Join-Path $repo "Module\bin\Win64_Shipping_Client\$moduleId.pdb") -Destination (Join-Path $deployModule "bin\Win64_Shipping_Client\$moduleId.pdb") -Force
    } catch {
        Write-Warning "Could not deploy $moduleId binaries to $deployModule. The game or launcher may have the module loaded. $($_.Exception.Message)"
    }
}
