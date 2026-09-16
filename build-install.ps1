<#
.SYNOPSIS
  Builds dnSpy-automate and installs it into a dnSpy build.

.DESCRIPTION
  Building needs no dnSpy installation: the project compiles against the contract assemblies
  vendored in lib/ (see lib/NOTICE), and every other reference comes from NuGet. This script
  is the convenience path — it builds, stages the exact shipping file set with the project's
  Package target, checks the destination is a dnSpy it can actually run in, and copies the
  staged folder into <DnSpyBinDir>\Extensions\dnSpy-automate\.

  To install by hand instead, run with -NoInstall (or 'dotnet build -c Release -t:Package')
  and copy artifacts\dnSpy-automate\ yourself; see README.md.

  dnSpy must be closed before installing: it locks the loaded extension DLL.

.PARAMETER DnSpyBinDir
  The dnSpy directory to install into — for the .NET build, the 'bin' folder next to
  dnSpy.exe. Defaults to $env:DNSPY_BIN_DIR, else '..\dnSpy-net-win64\bin' relative to this
  script, i.e. a dnSpy sitting next to this repo. Not needed with -NoInstall.

.PARAMETER Configuration
  Build configuration. Defaults to Release.

.PARAMETER NoInstall
  Build and stage into artifacts\dnSpy-automate\ only. Skips the install, and with it every
  need for a dnSpy on this machine.

.EXAMPLE
  .\build-install.ps1
  .\build-install.ps1 -NoInstall
  .\build-install.ps1 -DnSpyBinDir C:\path\to\dnSpy\bin
#>
[CmdletBinding()]
param(
  [string] $DnSpyBinDir,
  [string] $Configuration = 'Release',
  [switch] $NoInstall
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $scriptDir 'dnSpy-automate.csproj'
$stageDir = Join-Path $scriptDir 'artifacts\dnSpy-automate'

# The host runtime our TargetFramework (net10.0-windows) needs, as dnSpy's runtimeconfig
# spells it. A .NET Framework dnSpy has no runtimeconfig.json at all.
$requiredHostTfm = 'net10.0'

# ---------------------------------------------------------------- build (no dnSpy needed)

Write-Host "==> Building $Configuration" -ForegroundColor Cyan
dotnet build $project -c $Configuration -t:Package
if ($LASTEXITCODE -ne 0) {
  throw "Build failed with exit code $LASTEXITCODE"
}

$stagedDll = Join-Path $stageDir 'dnSpy-automate.x.dll'
if (-not (Test-Path $stagedDll)) {
  throw "Expected staged output missing: $stagedDll"
}

if ($NoInstall) {
  Write-Host "==> Staged in $stageDir (not installed)" -ForegroundColor Green
  return
}

# ------------------------------------------------- resolve and vet the install destination

if (-not $DnSpyBinDir) {
  $DnSpyBinDir = if ($env:DNSPY_BIN_DIR) { $env:DNSPY_BIN_DIR } else { Join-Path $scriptDir '..\dnSpy-net-win64\bin' }
}
if (-not (Test-Path $DnSpyBinDir)) {
  throw ("dnSpy not found at '$DnSpyBinDir'. Pass -DnSpyBinDir <path>, set " + '$env:DNSPY_BIN_DIR' +
         ", or re-run with -NoInstall and copy '$stageDir' into the extensions folder yourself.")
}
# Normalise '..' away so everything printed below is canonical.
$DnSpyBinDir = (Resolve-Path -LiteralPath $DnSpyBinDir).Path

# Is this a dnSpy directory at all? The contracts assembly is the one file that says so, and
# it doubles as the version to check against.
$targetContracts = Join-Path $DnSpyBinDir 'dnSpy.Contracts.DnSpy.dll'
if (-not (Test-Path $targetContracts)) {
  throw "'$DnSpyBinDir' does not look like a dnSpy directory: no dnSpy.Contracts.DnSpy.dll. For the .NET build, this should be the 'bin' folder next to dnSpy.exe."
}

# Is it the right flavour? dnSpy ships a .NET and a .NET Framework build; an extension built
# for one will not load in the other, and dnSpy reports nothing when it skips an extension.
$runtimeConfig = Join-Path $DnSpyBinDir 'dnSpy.runtimeconfig.json'
if (-not (Test-Path $runtimeConfig)) {
  throw "'$DnSpyBinDir' is the .NET Framework dnSpy (no dnSpy.runtimeconfig.json), but this extension is built for $requiredHostTfm and will not load there. Install into a dnSpy-net-* build, or retarget the project to net48 first."
}
$hostTfm = (Get-Content $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions.tfm
if ($hostTfm -ne $requiredHostTfm) {
  throw "dnSpy at '$DnSpyBinDir' runs on '$hostTfm' but this extension is built for '$requiredHostTfm'. Retarget TargetFramework in dnSpy-automate.csproj to match."
}

# Is it new enough? We compile against lib/ (dnSpyEx 6.6.0) unless told otherwise, so an
# older dnSpy can be missing contract members this build already calls.
$targetVersion = [System.Reflection.AssemblyName]::GetAssemblyName($targetContracts).Version
$libContracts = Join-Path $scriptDir 'lib\dnSpy.Contracts.DnSpy.dll'
if (Test-Path $libContracts) {
  $builtVersion = [System.Reflection.AssemblyName]::GetAssemblyName($libContracts).Version
  if ($targetVersion -lt $builtVersion) {
    Write-Warning "dnSpy at '$DnSpyBinDir' is contracts $targetVersion, but this was compiled against $builtVersion. Anything the extension calls that was added after $targetVersion will fail at run time. Build against that dnSpy instead with: dotnet build -c $Configuration -p:DnSpyContractsDir='$DnSpyBinDir'"
  }
}

# dnSpy holds an exclusive lock on a loaded extension, so a copy over a running instance
# fails halfway and leaves a half-updated extension folder behind.
$running = @(Get-Process -Name 'dnSpy' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
  $pids = ($running | ForEach-Object { $_.Id }) -join ', '
  throw "dnSpy is running (PID $pids) and locks the loaded extension DLL. Close it and re-run."
}

# ------------------------------------------------------------------------------- install

$destDir = Join-Path $DnSpyBinDir 'Extensions\dnSpy-automate'
Write-Host "==> Installing to $destDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $destDir | Out-Null

foreach ($file in Get-ChildItem -Path $stageDir -File) {
  try {
    Copy-Item $file.FullName $destDir -Force
  }
  catch [System.IO.IOException] {
    throw "Could not overwrite $($file.Name) in $destDir - the file is in use. Close dnSpy (and anything else holding the extension, e.g. a debugger attached to it) and re-run. Underlying error: $($_.Exception.Message)"
  }
  Write-Host "    copied $($file.Name)"
}

Write-Host "==> Done. Launch dnSpy to load the extension." -ForegroundColor Green
