param(
    [string]$GameRoot = "C:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord",
    [string]$PlayerSettlementRoot = "C:\Program Files (x86)\Steam\steamapps\workshop\content\261550\3720376888",
    [string]$ExpectedBannerlordVersion = "1.5.2",
    [string]$ExpectedWarSailsVersion = "1.3.2",
    [string]$ExpectedPlayerSettlementVersion = "7.5.0",
    [string]$DeployRoot
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$moduleId = "BLWarsailsInhabitableIslands"

function Get-ModuleVersion($moduleRoot) {
    $manifestPath = Join-Path $moduleRoot "SubModule.xml"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Missing manifest: $manifestPath"
    }

    [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
    $versionNode = $manifest.SelectSingleNode("/Module/Version")
    if ($null -eq $versionNode) {
        throw "No module version in $manifestPath"
    }

    return $versionNode.GetAttribute("value").TrimStart("v")
}

function Assert-Equal($label, $expected, $actual) {
    if ($actual -ne $expected) {
        throw "$label mismatch: expected $expected, found $actual"
    }

    Write-Output "${label}: $actual"
}

function Get-LoadedType([string]$typeName) {
    foreach ($assembly in [AppDomain]::CurrentDomain.GetAssemblies()) {
        $type = $assembly.GetType($typeName, $false)
        if ($null -ne $type) {
            return $type
        }
    }

    throw "Missing runtime type: $typeName"
}

function Assert-UniqueMethod([string]$typeName, [string]$methodName) {
    $type = Get-LoadedType $typeName
    $methods = @($type.GetMethods(
        [Reflection.BindingFlags]"Public,NonPublic,Instance,Static") |
        Where-Object Name -eq $methodName)
    if ($methods.Count -ne 1) {
        throw "$typeName.$methodName resolved to $($methods.Count) overloads"
    }

    Write-Output "API method: $typeName.$methodName"
}

function Assert-MethodSignature(
    [string]$typeName,
    [string]$methodName,
    [string[]]$parameterTypeNames
) {
    $type = Get-LoadedType $typeName
    $expected = $parameterTypeNames -join "|"
    $method = $type.GetMethods(
            [Reflection.BindingFlags]"Public,NonPublic,Instance,Static") |
        Where-Object {
            $_.Name -eq $methodName -and
            (($_.GetParameters() |
                ForEach-Object { $_.ParameterType.FullName }) -join "|") -eq
                $expected
        } |
        Select-Object -First 1
    if ($null -eq $method) {
        throw "Missing API signature: $typeName.$methodName($($parameterTypeNames -join ', '))"
    }

    Write-Output "API signature: $typeName.$methodName"
}

function Assert-BooleanProperty([string]$typeName, [string]$propertyName) {
    $type = Get-LoadedType $typeName
    $property = $type.GetProperty(
        $propertyName,
        [Reflection.BindingFlags]"Public,NonPublic,Instance,Static")
    if ($null -eq $property -or $property.PropertyType -ne [bool]) {
        throw "Missing Boolean API property: $typeName.$propertyName"
    }

    Write-Output "API property: $typeName.$propertyName"
}

Assert-Equal "Bannerlord" $ExpectedBannerlordVersion `
    (Get-ModuleVersion (Join-Path $GameRoot "Modules\Native"))
Assert-Equal "War Sails" $ExpectedWarSailsVersion `
    (Get-ModuleVersion (Join-Path $GameRoot "Modules\NavalDLC"))
Assert-Equal "Player Settlement" $ExpectedPlayerSettlementVersion `
    (Get-ModuleVersion $PlayerSettlementRoot)

[xml]$betaManifest = Get-Content -LiteralPath `
    (Join-Path $repo "Module\SubModule.xml") -Raw
Assert-Equal "Beta module ID" $moduleId $betaManifest.Module.Id.value
Assert-Equal "Beta Native dependency" "v$ExpectedBannerlordVersion" `
    (($betaManifest.Module.DependedModules.DependedModule | Where-Object Id -eq "Native").DependentVersion)
Assert-Equal "Beta War Sails dependency" "v$ExpectedWarSailsVersion" `
    (($betaManifest.Module.DependedModules.DependedModule | Where-Object Id -eq "NavalDLC").DependentVersion)

$binary = Join-Path $repo "Module\bin\Win64_Shipping_Client\$moduleId.dll"
if (-not (Test-Path -LiteralPath $binary)) {
    throw "Missing beta binary: $binary"
}

Write-Output "Beta binary: $binary"

& (Join-Path $repo "VerifyFleetCompatibility.ps1") -GameRoot $GameRoot
if ($LASTEXITCODE -ne 0) {
    throw "Fleet compatibility regression verification failed"
}

$gameBin = Join-Path $GameRoot "bin\Win64_Shipping_Client"
$script:ApiProbeDirectories = @(
    $gameBin,
    (Join-Path $GameRoot "Modules\Native\bin\Win64_Shipping_Client"),
    (Join-Path $GameRoot "Modules\SandBox\bin\Win64_Shipping_Client"),
    (Join-Path $GameRoot "Modules\SandBoxCore\bin\Win64_Shipping_Client"),
    (Join-Path $GameRoot "Modules\StoryMode\bin\Win64_Shipping_Client"),
    (Join-Path $GameRoot "Modules\NavalDLC\bin\Win64_Shipping_Client"),
    (Join-Path $PlayerSettlementRoot "bin\Win64_Shipping_Client")
)
$resolver = [ResolveEventHandler] {
    param($sender, $eventArgs)

    $fileName = ([Reflection.AssemblyName]$eventArgs.Name).Name + ".dll"
    foreach ($directory in $script:ApiProbeDirectories) {
        $candidate = Join-Path $directory $fileName
        if (Test-Path -LiteralPath $candidate) {
            return [Reflection.Assembly]::LoadFrom($candidate)
        }
    }

    return $null
}

[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
try {
    $apiAssemblies = @(
        (Join-Path $gameBin "TaleWorlds.CampaignSystem.dll"),
        (Join-Path $GameRoot "Modules\SandBox\bin\Win64_Shipping_Client\SandBox.dll"),
        (Join-Path $GameRoot "Modules\SandBox\bin\Win64_Shipping_Client\SandBox.View.dll"),
        (Join-Path $GameRoot "Modules\NavalDLC\bin\Win64_Shipping_Client\NavalDLC.dll"),
        (Join-Path $PlayerSettlementRoot "bin\Win64_Shipping_Client\PlayerSettlement.dll")
    )
    foreach ($assemblyPath in $apiAssemblies) {
        if (-not (Test-Path -LiteralPath $assemblyPath)) {
            throw "Missing API assembly: $assemblyPath"
        }

        [void][Reflection.Assembly]::LoadFrom($assemblyPath)
    }

    Assert-MethodSignature `
        "TaleWorlds.CampaignSystem.Party.MobileParty" `
        "InitializeNavigationTransitionParallel" `
        @(
            "TaleWorlds.CampaignSystem.CampaignVec2",
            "TaleWorlds.CampaignSystem.CampaignVec2",
            "System.Int32&",
            "TaleWorlds.CampaignSystem.Party.MobileParty[]&"
        )
    Assert-MethodSignature `
        "TaleWorlds.CampaignSystem.Party.MobileParty" `
        "SetSailAtPosition" `
        @("TaleWorlds.CampaignSystem.CampaignVec2")
    Assert-MethodSignature `
        "TaleWorlds.CampaignSystem.Party.MobileParty" `
        "DisembarkToPosition" `
        @("TaleWorlds.CampaignSystem.CampaignVec2")
    Assert-UniqueMethod `
        "TaleWorlds.CampaignSystem.Party.MobileParty" `
        "CancelNavigationTransitionParallel"
    Assert-BooleanProperty "TaleWorlds.CampaignSystem.Hero" "CanHaveFleet"

    $uniqueMethods = @(
        @("SandBox.MapScene", "Load"),
        @("NavalDLC.GameComponents.NavalPartyNavigationModel", "IsTerrainTypeValidForNavigationType"),
        @("NavalDLC.GameComponents.NavalPartyNavigationModel", "CanPlayerNavigateToPosition"),
        @("NavalDLC.CampaignBehaviors.PiratesCampaignBehavior", "IsPirateParty"),
        @("NavalDLC.CampaignBehaviors.ShipUpgradeCampaignBehavior", "OnSettlementEntered"),
        @("SandBox.View.Map.Visuals.SettlementVisual", "PopulateSiegeEngineFrameListsFromChildren"),
        @("SandBox.View.Map.Visuals.SettlementVisual", "OnStartup"),
        @("SandBox.View.Map.MapScreen", "HandleLeftMouseButtonClick"),
        @("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "UpdateSettlementVisualEntity"),
        @("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "OnBeforeTick"),
        @("BannerlordPlayerSettlement.Behaviours.PlayerSettlementBehaviour", "ApplyNow"),
        @("TaleWorlds.CampaignSystem.CampaignBehaviors.PlayerTownVisitCampaignBehavior", "game_menu_settlement_leave_on_consequence")
    )
    foreach ($entry in $uniqueMethods) {
        Assert-UniqueMethod $entry[0] $entry[1]
    }
} finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolver)
}

$cecilPath = Join-Path $GameRoot `
    "Modules\Bannerlord.Harmony\bin\Win64_Shipping_Client\Mono.Cecil.dll"
if (-not (Test-Path -LiteralPath $cecilPath)) {
    throw "Missing Mono.Cecil for binary inspection: $cecilPath"
}

[void][Reflection.Assembly]::LoadFrom($cecilPath)
$moduleAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($binary)
try {
    $unsafeFleetCalls = @(
        $moduleAssembly.MainModule.Types |
        ForEach-Object { $_.Methods } |
        Where-Object { $_.HasBody } |
        ForEach-Object { $_.Body.Instructions } |
        Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            $_.Operand.DeclaringType.FullName -eq "TaleWorlds.CampaignSystem.Hero" -and
            $_.Operand.Name -eq "get_CanHaveFleet"
        }
    )
    if ($unsafeFleetCalls.Count -ne 0) {
        throw "Compiled binary still directly references Hero.get_CanHaveFleet"
    }

    Write-Output "Binary fleet policy reference: reflection-only"
} finally {
    $moduleAssembly.Dispose()
}

if (-not [string]::IsNullOrWhiteSpace($DeployRoot)) {
    $deployedManifest = Join-Path $DeployRoot "SubModule.xml"
    $deployedBinary = Join-Path $DeployRoot "bin\Win64_Shipping_Client\$moduleId.dll"
    if (-not (Test-Path -LiteralPath $deployedManifest) -or
        -not (Test-Path -LiteralPath $deployedBinary)) {
        throw "Local beta deployment is incomplete at $DeployRoot"
    }

    $sourceHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
    $deployedHash = (Get-FileHash -LiteralPath $deployedBinary -Algorithm SHA256).Hash
    Assert-Equal "Deployed binary SHA256" $sourceHash $deployedHash
}

Write-Output "Static beta verification passed. Campaign checks remain user-operated."
