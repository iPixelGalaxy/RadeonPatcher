param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$PackageRoot,

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\RadeonPatcher\Resources\GpuModels.json'),

    [string]$ExistingDatabasePath = $OutputPath
)

$ErrorActionPreference = 'Stop'

function Get-PropertyValue {
    param($Object, [string]$Name)

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-PackageMappings {
    param([string]$Root)

    $displayRoot = Join-Path $Root 'Packages\Drivers\Display'
    if (-not (Test-Path -LiteralPath $displayRoot -PathType Container)) {
        throw "Display driver directory not found: $displayRoot"
    }

    $modelPattern = '"?%([^%]+)%"?\s*=.*?,\s*(PCI\\VEN_1002&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2})?)\s*$'
    $stringPattern = '^\s*([^;=\s]+)\s*=\s*"([^"]+)"\s*$'
    $devices = @{}
    $sources = [System.Collections.Generic.List[object]]::new()

    foreach ($inf in Get-ChildItem -LiteralPath $displayRoot -Recurse -Filter '*.inf' -File | Sort-Object FullName) {
        $lines = Get-Content -LiteralPath $inf.FullName
        if (-not ($lines | Where-Object { $_ -match '^\s*Class\s*=\s*Display\s*$' })) {
            continue
        }

        $modelLines = @($lines | Where-Object { $_ -match $modelPattern })
        if ($modelLines.Count -eq 0) {
            continue
        }

        $strings = @{}
        foreach ($line in $lines) {
            if ($line -match $stringPattern) {
                $strings[$matches[1]] = $matches[2].Trim()
            }
        }

        $resolvedFromInf = 0
        foreach ($line in $modelLines) {
            if ($line -notmatch $modelPattern) {
                continue
            }

            $token = $matches[1]
            $hardwareId = $matches[2].ToUpperInvariant()
            $name = $strings[$token]
            if ([string]::IsNullOrWhiteSpace($name)) {
                throw "Model token '$token' has no resolved string in $($inf.FullName)."
            }

            if ($devices.ContainsKey($hardwareId) -and $devices[$hardwareId] -ne $name) {
                throw "Conflicting names for ${hardwareId}: '$($devices[$hardwareId])' and '$name'."
            }

            $devices[$hardwareId] = $name
            $resolvedFromInf++
        }

        if ($resolvedFromInf -gt 0) {
            $sources.Add([ordered]@{
                inf = $inf.Name
                sha256 = (Get-FileHash -LiteralPath $inf.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                mappings = $resolvedFromInf
            })
        }
    }

    if ($devices.Count -eq 0) {
        throw "No AMD display hardware mappings were found below $displayRoot."
    }

    $packageName = Split-Path -Leaf (Resolve-Path -LiteralPath $Root)
    $versionMatch = [regex]::Match($packageName, '\d+\.\d+\.\d+')
    return [pscustomobject]@{
        Name = $packageName
        Version = if ($versionMatch.Success) { $versionMatch.Value } else { $null }
        Devices = $devices
        Sources = $sources
    }
}

$devices = @{}
$sources = [System.Collections.Generic.List[object]]::new()
$sourceKeys = @{}
$sourcePackages = [System.Collections.Generic.List[object]]::new()
$sourcePackageKeys = @{}
$legacySourcePackage = $null
$legacySourceVersion = $null

if (Test-Path -LiteralPath $ExistingDatabasePath -PathType Leaf) {
    $existing = Get-Content -LiteralPath $ExistingDatabasePath -Raw | ConvertFrom-Json
    if ($existing.schemaVersion -ne 1 -or $null -eq $existing.devices) {
        throw "Existing GPU database has an unsupported schema: $ExistingDatabasePath"
    }

    $legacySourcePackage = $existing.sourcePackage
    $legacySourceVersion = $existing.sourceVersion
    foreach ($property in $existing.devices.PSObject.Properties) {
        $devices[$property.Name.ToUpperInvariant()] = [string]$property.Value
    }

    foreach ($source in @($existing.sources)) {
        $package = Get-PropertyValue $source 'package'
        if ([string]::IsNullOrWhiteSpace($package)) { $package = $legacySourcePackage }
        $key = "$package|$(Get-PropertyValue $source 'inf')|$(Get-PropertyValue $source 'sha256')"
        if (-not $sourceKeys.ContainsKey($key)) {
            $sources.Add($source)
            $sourceKeys[$key] = $true
        }
    }

    $existingSourcePackages = Get-PropertyValue $existing 'sourcePackages'
    if ($null -ne $existingSourcePackages) {
        foreach ($sourcePackage in @($existingSourcePackages)) {
            $package = Get-PropertyValue $sourcePackage 'package'
            $version = Get-PropertyValue $sourcePackage 'version'
            $key = "$package|$version"
            if (-not [string]::IsNullOrWhiteSpace($package) -and -not $sourcePackageKeys.ContainsKey($key)) {
                $sourcePackages.Add($sourcePackage)
                $sourcePackageKeys[$key] = $true
            }
        }
    }

    if ($sourcePackages.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($legacySourcePackage)) {
        $key = "$legacySourcePackage|$legacySourceVersion"
        $sourcePackages.Add([ordered]@{ package = $legacySourcePackage; version = $legacySourceVersion })
        $sourcePackageKeys[$key] = $true
    }
}

$existingCount = $devices.Count
$addedCount = 0
$sameCount = 0
$conflicts = [System.Collections.Generic.List[object]]::new()

foreach ($root in $PackageRoot) {
    $package = Get-PackageMappings $root
    $packageAddedCount = 0
    foreach ($hardwareId in $package.Devices.Keys | Sort-Object) {
        $name = $package.Devices[$hardwareId]
        if (-not $devices.ContainsKey($hardwareId)) {
            $devices[$hardwareId] = $name
            $addedCount++
            $packageAddedCount++
            continue
        }

        if ($devices[$hardwareId] -eq $name) {
            $sameCount++
            continue
        }

        $conflicts.Add([pscustomobject]@{
            HardwareId = $hardwareId
            Existing = $devices[$hardwareId]
            Incoming = $name
            Package = $package.Name
        })
    }

    $packageKey = "$($package.Name)|$($package.Version)"
    if (-not $sourcePackageKeys.ContainsKey($packageKey)) {
        $sourcePackages.Add([ordered]@{
            package = $package.Name
            version = $package.Version
            mappings = $package.Devices.Count
            addedMappings = $packageAddedCount
        })
        $sourcePackageKeys[$packageKey] = $true
    }

    foreach ($source in $package.Sources) {
        $sourceKey = "$($package.Name)|$($source.inf)|$($source.sha256)"
        if (-not $sourceKeys.ContainsKey($sourceKey)) {
            $sources.Add([ordered]@{
                package = $package.Name
                inf = $source.inf
                sha256 = $source.sha256
                mappings = $source.mappings
            })
            $sourceKeys[$sourceKey] = $true
        }
    }
}

if ($devices.Count -eq 0) {
    throw 'No AMD display hardware mappings were available to write.'
}

$sortedDevices = [ordered]@{}
foreach ($hardwareId in $devices.Keys | Sort-Object) {
    $sortedDevices[$hardwareId] = $devices[$hardwareId]
}

if ([string]::IsNullOrWhiteSpace($legacySourcePackage)) {
    $legacySourcePackage = $sourcePackages[0].package
    $legacySourceVersion = $sourcePackages[0].version
}

$database = [ordered]@{
    schemaVersion = 1
    sourcePackage = $legacySourcePackage
    sourceVersion = $legacySourceVersion
    sourcePackages = $sourcePackages
    sources = $sources
    devices = $sortedDevices
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$database | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8

Write-Host "Merged $addedCount new AMD GPU mappings. Existing mappings kept: $existingCount. Same-name mappings skipped: $sameCount. Name conflicts kept from existing database: $($conflicts.Count). Total: $($devices.Count)."
foreach ($conflict in $conflicts) {
    Write-Warning "Kept existing mapping for $($conflict.HardwareId): '$($conflict.Existing)' (incoming '$($conflict.Incoming)' from $($conflict.Package))."
}
