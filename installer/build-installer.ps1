$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'TrafficMonitor.csproj'
$publishPath = Join-Path $repoRoot 'bin\Release\net8.0-windows\win-x64\publish'
$setupPath = Join-Path $PSScriptRoot 'Monita.iss'
$artifactsPath = Join-Path $repoRoot 'artifacts'

dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$compiler = Get-Command ISCC.exe -ErrorAction SilentlyContinue
if (-not $compiler) {
    $knownPaths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $compilerPath = $knownPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $compilerPath) {
        throw 'Inno Setup 6 was not found. Install it or add ISCC.exe to PATH, then rerun this script.'
    }

} else {
    $compilerPath = $compiler.Source
}

New-Item -ItemType Directory -Force -Path $artifactsPath | Out-Null
& $compilerPath $setupPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

Write-Output "Installer created: $(Join-Path $artifactsPath 'Monita-Setup-x64.exe')"
