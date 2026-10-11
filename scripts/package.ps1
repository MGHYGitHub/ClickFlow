[CmdletBinding()]
param([switch]$SkipBuild, [string]$NuGetSource, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path $PSScriptRoot -Parent
[xml]$versionFile = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$versionFile.Project.PropertyGroup.Version
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw 'Version must be a stable major.minor.patch value.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/release' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $SkipBuild) { & (Join-Path $projectRoot 'build.ps1') -Publish -NuGetSource $NuGetSource }

$publishedExe = Join-Path $projectRoot 'artifacts/publish/win-x64/ClickFlow.exe'
if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) { throw 'Published EXE missing. Run build.ps1 -Publish first.' }
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe).FileVersion
if ($fileVersion -ne "$version.0") { throw "Published EXE version $fileVersion does not match $version.0. Rebuild before packaging." }

# Fresh staging excludes older releases and local user profiles.
$stageRoot = Join-Path $projectRoot ('artifacts/package/' + [Guid]::NewGuid().ToString('N'))
$portableName = "ClickFlow-$version-win-x64"
$portableRoot = Join-Path $stageRoot $portableName
$null = New-Item -ItemType Directory -Path $portableRoot -Force
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $portableRoot 'ClickFlow.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $portableRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $portableRoot 'LICENSE')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination (Join-Path $portableRoot 'docs') -Recurse

# Exact runtime package versions/locations come from the publish restore metadata.
$assetsPath = Join-Path $projectRoot 'artifacts/build/obj/ClickFlow.Desktop/project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$licenseRoot = Join-Path $portableRoot 'licenses'
$null = New-Item -ItemType Directory -Path $licenseRoot
$runtimePackages = @{
    'Microsoft.NETCore.App.Runtime.win-x64' = @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')
    'Microsoft.WindowsDesktop.App.Runtime.win-x64' = @('LICENSE')
}
foreach ($packageId in $runtimePackages.Keys) {
    $dependencies = @($assets.project.frameworks.PSObject.Properties | ForEach-Object {
        if ($_.Value.PSObject.Properties.Name -contains 'downloadDependencies') { $_.Value.downloadDependencies }
    })
    $dependency = @($dependencies | Where-Object { $_.name -eq $packageId }) | Select-Object -First 1
    if (-not $dependency) { throw "Runtime package not found in restore metadata: $packageId" }
    $runtimeVersion = ([string]$dependency.version -replace '[\[\]\(\)]', '').Split(',')[0].Trim()
    $packagePath = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder ($packageId.ToLowerInvariant() + '/' + $runtimeVersion)
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packagePath = $candidate; break }
    }
    if (-not $packagePath) { throw "Restored runtime package directory not found: $packageId/$runtimeVersion" }
    foreach ($licenseName in $runtimePackages[$packageId]) {
        Copy-Item -LiteralPath (Join-Path $packagePath $licenseName) -Destination (Join-Path $licenseRoot ($packageId + '-' + $licenseName))
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Write-Zip {
    param([string]$Path, [string]$BaseDirectory, [IO.FileInfo[]]$Files, [string]$Prefix)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::Write)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in $Files) {
            $relative = $file.FullName.Substring($BaseDirectory.TrimEnd([char[]]'\/').Length + 1).Replace('\', '/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, "$Prefix/$relative", [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose(); $stream.Dispose() }
}

$exeAsset = Join-Path $OutputDirectory "$portableName.exe"
$portableAsset = Join-Path $OutputDirectory "$portableName.zip"
$sourceAsset = Join-Path $OutputDirectory "ClickFlow-$version-source.zip"
Copy-Item -LiteralPath $publishedExe -Destination $exeAsset -Force
Write-Zip -Path $portableAsset -BaseDirectory $portableRoot -Files @(Get-ChildItem -LiteralPath $portableRoot -File -Recurse -Force | Sort-Object FullName) -Prefix $portableName

# Only repository source roots are included. Local configuration and build data stay out.
$sourceFiles = @(foreach ($sourceDirectory in @('src', 'tests', 'docs', 'scripts', '.github')) {
    $sourcePath = Join-Path $projectRoot $sourceDirectory
    Get-ChildItem -LiteralPath $sourcePath -File -Recurse -Force | Where-Object {
        $relative = $_.FullName.Substring($projectRoot.Length + 1)
        $relative -notmatch '(^|[\\/])(bin|obj|artifacts|\.git|\.vs|preview-data)([\\/]|$)' -and
        $_.Name -notmatch '(_wpftmp|\.user$|\.log$|\.tmp$|\.partial$)'
    }
})
$sourceFiles += @(Get-ChildItem -LiteralPath $projectRoot -File -Force | Where-Object {
    $_.Name -match '\.(sln|slnx|props|targets|md)$' -or $_.Name -in @('LICENSE', '.gitignore', 'build.ps1', 'global.json')
})
Write-Zip -Path $sourceAsset -BaseDirectory $projectRoot -Files @($sourceFiles | Sort-Object FullName) -Prefix 'ClickFlow'

$manifestAsset = Join-Path $OutputDirectory 'update.json'
$releaseBase = "https://github.com/MGHYGitHub/ClickFlow/releases/download/v$version"
$releaseNotes = Get-Content -LiteralPath (Join-Path $projectRoot "docs/releases/v$version.md") -Raw -Encoding UTF8
$updateManifest = [ordered]@{
    tag_name = "v$version"
    name = "ClickFlow $version"
    body = $releaseNotes
    html_url = "https://github.com/MGHYGitHub/ClickFlow/releases/tag/v$version"
    draft = $false
    prerelease = $false
    assets = @(
        [ordered]@{ name = "$portableName.exe"; size = (Get-Item -LiteralPath $exeAsset).Length; browser_download_url = "$releaseBase/$portableName.exe" },
        [ordered]@{ name = 'SHA256SUMS.txt'; browser_download_url = "$releaseBase/SHA256SUMS.txt" }
    )
}
[IO.File]::WriteAllText($manifestAsset, ($updateManifest | ConvertTo-Json -Depth 5) + "`n", (New-Object Text.UTF8Encoding($false)))

$lines = @(foreach ($asset in @($exeAsset, $portableAsset, $sourceAsset, $manifestAsset)) {
    $hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($asset))"
})
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
Get-Item -LiteralPath $exeAsset, $portableAsset, $sourceAsset, $manifestAsset, (Join-Path $OutputDirectory 'SHA256SUMS.txt') | Select-Object Name, Length, FullName
