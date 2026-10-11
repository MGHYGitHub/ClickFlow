[CmdletBinding()]
param([switch]$Publish, [string]$NuGetSource)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    $buildArtifacts = Join-Path $PSScriptRoot 'artifacts/build'
    $restoreArgs = @()
    if ($NuGetSource) { $restoreArgs = @('--source', $NuGetSource) }
    & dotnet build ClickFlow.sln -c Release --artifacts-path $buildArtifacts @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    foreach ($testProject in @('tests/ClickFlow.Tests', 'tests/ClickFlow.UpdateTests')) {
        & dotnet run --project $testProject -c Release --no-build --artifacts-path $buildArtifacts
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $testProject" }
    }
    if ($Publish) {
        & dotnet publish src/ClickFlow.Desktop -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true --artifacts-path $buildArtifacts `
            @restoreArgs -o (Join-Path $PSScriptRoot 'artifacts/publish/win-x64')
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    }
} finally { Pop-Location }
