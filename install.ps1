#Requires -Version 5.1
<#
.SYNOPSIS
Installs sk and seiko-mcp (Seiko) from GitHub Releases.

.DESCRIPTION
Downloads the NativeAOT sk.exe and seiko-mcp.exe for this Windows
architecture, verifies them against SHA256SUMS, and installs them into the
user profile by default.

.PARAMETER Version
Install a specific version, for example v0.1.0.

.PARAMETER Dir
Install directory. Defaults to %LOCALAPPDATA%\Seiko\bin.

.PARAMETER ModifyPath
Append the install directory to the user PATH.

.PARAMETER DryRun
Resolve the release and checksum without installing.

.PARAMETER BaseUrl
Use a custom release asset base URL (advanced).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Dir,
    [switch]$ModifyPath,
    [switch]$DryRun,
    [string]$BaseUrl
)

$ErrorActionPreference = "Stop"
$Repo = "Hosihikari/Seiko"

function Fail([string]$Message) {
    Write-Error "sk-install: $Message"
    exit 1
}

if ($env:OS -ne "Windows_NT") {
    Fail "this script is for Windows; use install.sh on Linux or macOS"
}

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64" -or $env:PROCESSOR_ARCHITEW6432 -eq "ARM64") {
    "ARM64"
} elseif ($env:PROCESSOR_ARCHITECTURE -eq "AMD64" -or $env:PROCESSOR_ARCHITEW6432 -eq "AMD64") {
    "AMD64"
} else {
    Fail "unsupported Windows architecture: $env:PROCESSOR_ARCHITECTURE"
}

$rid = if ($arch -eq "ARM64") { "win-arm64" } else { "win-x64" }

if (-not $Dir) {
    $Dir = Join-Path $env:LOCALAPPDATA "Seiko\bin"
}

if ($BaseUrl -and -not $Version) {
    Fail "--BaseUrl requires -Version"
}

if (-not $Version) {
    $release = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$Repo/releases/latest" `
        -Headers @{ "User-Agent" = "seiko-install"; "Accept" = "application/vnd.github+json" }
    $tag = $release.tag_name
} elseif ($Version.StartsWith("v")) {
    $tag = $Version
} else {
    $tag = "v$Version"
}

if ($tag -notmatch '^v\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    Fail "invalid version tag: $tag"
}
$versionNumber = $tag.Substring(1)

if (-not $BaseUrl) {
    $BaseUrl = "https://github.com/$Repo/releases/download/$tag"
}
$BaseUrl = $BaseUrl.TrimEnd("/")

$asset = "sk-$versionNumber-$rid.zip"
$assetUrl = "$BaseUrl/$asset"
$sumsUrl = "$BaseUrl/SHA256SUMS"
$target = Join-Path $Dir "sk.exe"
$mcpTarget = Join-Path $Dir "seiko-mcp.exe"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ("seiko-install-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $sumsFile = Join-Path $tmp "SHA256SUMS"
    Invoke-WebRequest -Uri $sumsUrl -OutFile $sumsFile -UseBasicParsing
    $expected = Get-Content $sumsFile | ForEach-Object {
        $parts = $_ -split '\s+', 2
        if ($parts.Count -eq 2 -and ($parts[1] -eq $asset -or $parts[1] -eq "*$asset")) {
            $parts[0]
        }
    } | Select-Object -First 1
    if (-not $expected) {
        Fail "SHA256SUMS does not contain $asset"
    }

    if ($DryRun) {
        Write-Output "version: $versionNumber"
        Write-Output "rid: $rid"
        Write-Output "asset: $asset"
        Write-Output "url: $assetUrl"
        Write-Output "target: $target"
        Write-Output "mcp-target: $mcpTarget"
        Write-Output "sha256: $expected"
        exit 0
    }

    $archive = Join-Path $tmp $asset
    Invoke-WebRequest -Uri $assetUrl -OutFile $archive -UseBasicParsing
    $actual = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected.ToLowerInvariant()) {
        Fail "checksum mismatch for $asset"
    }

    $extract = Join-Path $tmp "extract"
    Expand-Archive -Path $archive -DestinationPath $extract -Force
    $binary = Join-Path $extract "sk.exe"
    if (-not (Test-Path $binary)) {
        Fail "archive does not contain sk.exe"
    }
    $mcpBinary = Join-Path $extract "seiko-mcp.exe"
    if (-not (Test-Path $mcpBinary)) {
        Fail "archive does not contain seiko-mcp.exe"
    }

    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    foreach ($item in @(
        @{ Source = $binary; Target = $target; Name = "sk" },
        @{ Source = $mcpBinary; Target = $mcpTarget; Name = "seiko-mcp" }
    )) {
        $tmpTarget = Join-Path $Dir ("." + $item.Name + ".tmp." + $PID + ".exe")
        Copy-Item -Path $item.Source -Destination $tmpTarget -Force
        Move-Item -Path $tmpTarget -Destination $item.Target -Force
        Write-Output "installed $($item.Name) $versionNumber ($rid) to $($item.Target)"
    }

    $pathEntries = [Environment]::GetEnvironmentVariable("Path", "User") -split ";"
    if ($pathEntries -notcontains $Dir) {
        if ($ModifyPath) {
            $newPath = (@($pathEntries | Where-Object { $_ }) + $Dir) -join ";"
            [Environment]::SetEnvironmentVariable("Path", $newPath, "User")
            Write-Output "updated the user PATH; restart the terminal"
        } else {
            Write-Output "add this directory to PATH if needed:"
            Write-Output "  $Dir"
        }
    }
} finally {
    Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
