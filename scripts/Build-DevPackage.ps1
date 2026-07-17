[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$OutputRoot,

    [switch]$NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectDirectory = Join-Path $repositoryRoot 'src\WinDayFlow.App'
$projectPath = Join-Path $projectDirectory 'WinDayFlow.App.csproj'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot 'artifacts\dev'
}

$outputRootPath = [System.IO.Path]::GetFullPath($OutputRoot, $repositoryRoot)
$repositoryPrefix = $repositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

if (-not $outputRootPath.StartsWith(
        $repositoryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must be inside the repository: $repositoryRoot"
}

$platform = switch ($RuntimeIdentifier) {
    'win-x64' { 'x64' }
    'win-arm64' { 'ARM64' }
}
$architecture = $RuntimeIdentifier.Substring('win-'.Length)
$packageName = "WinDayFlow-dev-$architecture"
$packagePath = Join-Path $outputRootPath $packageName
$stagingPath = Join-Path $outputRootPath ".$packageName.staging-$PID"
$zipPath = Join-Path $outputRootPath "$packageName.zip"
$temporaryZipPath = "$zipPath.tmp-$PID"

$bundledDotnet = Join-Path $repositoryRoot '.dotnet\dotnet.exe'
$dotnetPath = if (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) {
    $bundledDotnet
}
else {
    (Get-Command dotnet -ErrorAction Stop).Source
}

$nativeDllPath = if ($RuntimeIdentifier -eq 'win-x64') {
    Join-Path $repositoryRoot (
        "artifacts\native\x64\$Configuration\WinDayFlow.Capture.Native.dll")
}
else {
    $null
}

$distributionSourceFiles = @(
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE')
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md')
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'DEV_BUNDLE_LOCAL_ONLY.txt')
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'docs\provenance\QiDayflow-capture.md')
    Get-Item -LiteralPath (Join-Path $repositoryRoot 'docs\provenance\QiDayflow-capture.manifest.json')
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'licenses') -Recurse -File
)
$distributionRelativeFiles = @($distributionSourceFiles | ForEach-Object {
    [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
})

$publishArguments = @(
    'publish'
    $projectPath
    '--configuration', $Configuration
    '--runtime', $RuntimeIdentifier
    '--self-contained', 'true'
    '--property', "Platform=$platform"
    '--output', $stagingPath
)
if ($NoRestore) {
    $publishArguments += '--no-restore'
}

try {
    New-Item -ItemType Directory -Path $outputRootPath -Force | Out-Null

    Write-Warning (
        'DEVELOPMENT/TEST USE ONLY: the current WinUI Engineering Preview terms ' +
        'prohibit live use and third-party sharing, publishing, distribution, ' +
        'leasing, or transfer of this development bundle.')

    if ($null -ne $nativeDllPath)
    {
        & (Join-Path $PSScriptRoot 'Build-Native.ps1') -Configuration $Configuration |
            Out-Host
        if (-not (Test-Path -LiteralPath $nativeDllPath -PathType Leaf))
        {
            throw "Native capture build did not produce: $nativeDllPath"
        }
    }

    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }

    & $dotnetPath @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $requiredFiles = @(
        @(
            'WinDayFlow.App.exe'
            'WinDayFlow.App.dll'
            'WinDayFlow.App.deps.json'
            'WinDayFlow.App.runtimeconfig.json'
            'WinDayFlow.App.pri'
            'App.xbf'
            'MainWindow.xbf'
            'coreclr.dll'
            'hostfxr.dll'
            'hostpolicy.dll'
            'Microsoft.ui.xaml.dll'
            if ($null -ne $nativeDllPath) {
                'WinDayFlow.Capture.Native.dll'
            }
        ) + @($distributionRelativeFiles) |
            Sort-Object -Unique
    )

    $missingFiles = $requiredFiles | Where-Object {
        $candidatePath = Join-Path $stagingPath $_
        -not (Test-Path -LiteralPath $candidatePath -PathType Leaf) -or
            (Get-Item -LiteralPath $candidatePath).Length -eq 0
    }

    $sourceXamlFiles = Get-ChildItem -LiteralPath $projectDirectory -Recurse -Filter '*.xaml' -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
    $expectedCompiledXaml = @($sourceXamlFiles | ForEach-Object {
        $relativeXamlPath = [System.IO.Path]::GetRelativePath($projectDirectory, $_.FullName)
        [System.IO.Path]::ChangeExtension($relativeXamlPath, '.xbf')
    })
    $missingCompiledXaml = $expectedCompiledXaml | Where-Object {
        $candidatePath = Join-Path $stagingPath $_
        -not (Test-Path -LiteralPath $candidatePath -PathType Leaf) -or
            (Get-Item -LiteralPath $candidatePath).Length -eq 0
    }

    $missingResources = @(
        @($missingFiles) + @($missingCompiledXaml) |
            Sort-Object -Unique
    )
    if ($missingResources.Count -gt 0) {
        throw "Development package is incomplete. Missing: $($missingResources -join ', ')"
    }

    $mismatchedDistributionFiles = @($distributionSourceFiles | Where-Object {
        $relativePath = [System.IO.Path]::GetRelativePath(
            $repositoryRoot,
            $_.FullName)
        $publishedPath = Join-Path $stagingPath $relativePath
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $publishedPath -Algorithm SHA256).Hash
    } | ForEach-Object {
        [System.IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)
    })
    if ($mismatchedDistributionFiles.Count -gt 0) {
        throw "Development package contains stale notices: $($mismatchedDistributionFiles -join ', ')"
    }

    if (Test-Path -LiteralPath $temporaryZipPath) {
        Remove-Item -LiteralPath $temporaryZipPath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $stagingPath,
        $temporaryZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($temporaryZipPath)
    try {
        $archiveFiles = @($archive.Entries | Where-Object Length -GT 0 | ForEach-Object {
            $_.FullName.Replace('\', '/')
        })
        $requiredArchiveFiles = @($requiredFiles) + @($expectedCompiledXaml) |
            ForEach-Object { $_.Replace('\', '/') } |
            Sort-Object -Unique
        $missingArchiveFiles = @($requiredArchiveFiles | Where-Object {
            $_ -notin $archiveFiles
        })
        if ($missingArchiveFiles.Count -gt 0) {
            throw "Development package ZIP is incomplete. Missing: $($missingArchiveFiles -join ', ')"
        }
    }
    finally {
        $archive.Dispose()
    }

    if (Test-Path -LiteralPath $packagePath) {
        Remove-Item -LiteralPath $packagePath -Recurse -Force
    }
    Move-Item -LiteralPath $stagingPath -Destination $packagePath

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Move-Item -LiteralPath $temporaryZipPath -Destination $zipPath

    $packageFiles = Get-ChildItem -LiteralPath $packagePath -Recurse -File
    $packageBytes = ($packageFiles | Measure-Object -Property Length -Sum).Sum
    $compiledXamlCount = ($packageFiles | Where-Object Extension -EQ '.xbf').Count
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash

    [pscustomobject]@{
        PackagePath = $packagePath
        ZipPath = $zipPath
        ZipSha256 = $zipHash
        FileCount = $packageFiles.Count
        CompiledXamlCount = $compiledXamlCount
        DistributionNoticeCount = $distributionSourceFiles.Count
        Distribution = 'DEVELOPMENT/TEST USE ONLY - NO LIVE USE OR THIRD-PARTY DISTRIBUTION'
        SizeMiB = [Math]::Round($packageBytes / 1MB, 2)
    }
}
finally {
    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
    if (Test-Path -LiteralPath $temporaryZipPath) {
        Remove-Item -LiteralPath $temporaryZipPath -Force
    }
}
