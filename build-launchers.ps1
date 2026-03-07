[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$InstallDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "launcher\WindowsTerminalLauncher.csproj"
$outputDir = Join-Path $projectRoot "dist"
$buildDir = Join-Path $projectRoot "launcher\bin\$Configuration\net10.0-windows"
$launcherNames = @("cmd2", "cmd3", "cmd21", "cmd22")
$sourceBaseName = "WindowsTerminalLauncher"
$sharedExtensions = @(".dll", ".deps.json", ".runtimeconfig.json")

$env:DOTNET_CLI_HOME = Join-Path $projectRoot ".dotnet"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

dotnet build $projectPath -c $Configuration --ignore-failed-sources

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
Get-ChildItem -Force $outputDir | Remove-Item -Force -Recurse

foreach ($name in $launcherNames) {
    $sourcePath = Join-Path $buildDir ($sourceBaseName + ".exe")
    $destinationPath = Join-Path $outputDir ($name + ".exe")
    Copy-Item -Force $sourcePath $destinationPath
}

foreach ($extension in $sharedExtensions) {
    $sourcePath = Join-Path $buildDir ($sourceBaseName + $extension)
    $destinationPath = Join-Path $outputDir ($sourceBaseName + $extension)
    Copy-Item -Force $sourcePath $destinationPath
}

if ($InstallDirectory) {
    New-Item -ItemType Directory -Force -Path $InstallDirectory | Out-Null

    foreach ($name in $launcherNames) {
        $sourcePath = Join-Path $outputDir ($name + ".exe")
        $destinationPath = Join-Path $InstallDirectory ($name + ".exe")
        Copy-Item -Force $sourcePath $destinationPath
    }

    foreach ($extension in $sharedExtensions) {
        $sourcePath = Join-Path $outputDir ($sourceBaseName + $extension)
        $destinationPath = Join-Path $InstallDirectory ($sourceBaseName + $extension)
        Copy-Item -Force $sourcePath $destinationPath
    }
}
