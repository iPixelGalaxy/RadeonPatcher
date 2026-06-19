param(
    [Parameter(Mandatory = $true)]
    [string]$PackageRoot,

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\RadeonPatcher\Resources\GpuModels.json')
)

$ErrorActionPreference = 'Stop'
$displayRoot = Join-Path $PackageRoot 'Packages\Drivers\Display'
if (-not (Test-Path -LiteralPath $displayRoot -PathType Container)) {
    throw "Display driver directory not found: $displayRoot"
}

$modelPattern = '"?%([^%]+)%"?\s*=.*?,\s*(PCI\\VEN_1002&DEV_[0-9A-F]{4}(?:&SUBSYS_[0-9A-F]{8})?(?:&REV_[0-9A-F]{2})?)\s*$'
$stringPattern = '^\s*([^;=\s]+)\s*=\s*"([^"]+)"\s*$'
$devices = @{}
$sources = [System.Collections.Generic.List[object]]::new()

foreach ($inf in Get-ChildItem -LiteralPath $displayRoot -Recurse -Filter '*.inf' -File | Sort-Object FullName) {
    $lines = Get-Content -LiteralPath $inf.FullName
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

$packageName = Split-Path -Leaf (Resolve-Path -LiteralPath $PackageRoot)
$versionMatch = [regex]::Match($packageName, '\d+\.\d+\.\d+')
$sortedDevices = [ordered]@{}
foreach ($hardwareId in $devices.Keys | Sort-Object) {
    $sortedDevices[$hardwareId] = $devices[$hardwareId]
}

$database = [ordered]@{
    schemaVersion = 1
    sourcePackage = $packageName
    sourceVersion = if ($versionMatch.Success) { $versionMatch.Value } else { $null }
    sources = $sources
    devices = $sortedDevices
}

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$database | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Generated $($devices.Count) AMD GPU mappings at $OutputPath."
