param(
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$game = "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord"
$framework = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.1"
$csc = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe"
$moduleId = "BLWarsailsInhabitableIslands"
$output = Join-Path $repo "Module\bin\Win64_Shipping_Client\$moduleId.dll"
$deployModule = "C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3767263102"

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

$refFiles = @()
$refFiles += Add-ManagedDlls $framework
$refFiles += Add-ManagedDlls (Join-Path $framework "Facades")

$gameBin = Join-Path $game "bin\Win64_Shipping_Client"
$refFiles += Add-ManagedDlls $gameBin "TaleWorlds*.dll" | Where-Object { [IO.Path]::GetFileName($_) -ne "TaleWorlds.Native.dll" }
$refFiles += Join-Path $gameBin "Newtonsoft.Json.dll"

foreach ($module in @("Native", "SandBox", "SandBoxCore", "StoryMode", "CustomBattle", "BirthAndDeath", "NavalDLC", "Bannerlord.Harmony")) {
    $dir = Join-Path $game "Modules\$module\bin\Win64_Shipping_Client"
    $refFiles += Add-ManagedDlls $dir | Where-Object {
        [IO.Path]::GetFileName($_) -notmatch "^(System|Microsoft)\." -and
        [IO.Path]::GetFileName($_) -ne "TaleWorlds.Native.dll"
    }
}

$playerSettlementBin = "C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3720376888\bin\Win64_Shipping_Client"
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
