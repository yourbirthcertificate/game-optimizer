<#
  Builds the OptiGame+ setup program.
    pwsh installer\build.ps1                 publish the app and the command line tool, then run Inno Setup
    pwsh installer\build.ps1 -SkipPublish    reuse what is already in dist\
  Needs the .NET 10 SDK and Inno Setup 6 (ISCC.exe; found in PATH, Program Files, or %LOCALAPPDATA%\Programs).
  Writes dist\OptiGame.exe, dist\optigame.exe, dist\gameopt.exe and dist\OptiGame-Setup-<version>.exe.
#>
param([switch]$SkipPublish, [string]$Dist = 'dist')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$props = [xml](Get-Content (Join-Path $root 'Directory.Build.props'))
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).Trim()

if (-not $SkipPublish) {
    dotnet publish src/OptiGame.App -c Release -r win-x64 -o $Dist
    if ($LASTEXITCODE) { throw 'publish of the app failed' }
    dotnet publish src/OptiGame.Cli -c Release -r win-x64 -o $Dist
    if ($LASTEXITCODE) { throw 'publish of the command line tool failed' }
}

$iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php' }

$distFull = (Resolve-Path $Dist).Path
& $iscc "/DAppVersion=$version" "/DDistDir=$distFull" (Join-Path $root 'installer\OptiGame.iss')
if ($LASTEXITCODE) { throw 'Inno Setup failed' }
Get-ChildItem (Join-Path $distFull 'OptiGame-Setup-*.exe') | ForEach-Object { "{0}  {1:N1} MB" -f $_.FullName, ($_.Length / 1MB) }
