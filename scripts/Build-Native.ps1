[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$BuildRoot,

    [string]$CMakePath,

    [string]$Generator,

    [switch]$Fresh
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-NativeTool {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter()]
        [string[]]$ArgumentList = @()
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $repositoryRoot

    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }

    # Some hosts expose both Path and PATH in the raw Windows environment.
    # .NET Framework MSBuild rejects that duplicate when it starts cl.exe.
    $startInfo.Environment.Clear()
    Get-ChildItem Env: |
        Where-Object { $_.Name -notlike 'CMAKE_GENERATOR*' } |
        ForEach-Object {
            $startInfo.Environment[$_.Name] = $_.Value
        }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start native tool: $FilePath"
    }
    try {
        $process.WaitForExit()
        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$sourcePath = Join-Path $repositoryRoot 'src\WinDayFlow.Capture.Native'
$vsWherePath = Join-Path ${env:ProgramFiles(x86)} `
    'Microsoft Visual Studio\Installer\vswhere.exe'

if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = Join-Path $repositoryRoot 'artifacts\native\x64'
}

$buildPath = [System.IO.Path]::GetFullPath($BuildRoot, $repositoryRoot)
$repositoryPrefix = $repositoryRoot.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $buildPath.StartsWith(
        $repositoryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "BuildRoot must be inside the repository: $repositoryRoot"
}

if ([string]::IsNullOrWhiteSpace($CMakePath)) {
    $cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
    if ($null -ne $cmakeCommand) {
        $CMakePath = $cmakeCommand.Source
    }
}

if ([string]::IsNullOrWhiteSpace($CMakePath)) {
    if (Test-Path -LiteralPath $vsWherePath -PathType Leaf) {
        $visualStudioPath = & $vsWherePath `
            -latest `
            -products '*' `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -property installationPath
        if ($LASTEXITCODE -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($visualStudioPath)) {
            $candidate = Join-Path $visualStudioPath `
                'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                $CMakePath = $candidate
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($CMakePath)) {
    $knownCandidates = @(
        'D:\program\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\2022\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe')
    )
    $CMakePath = $knownCandidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($CMakePath) -or
    -not (Test-Path -LiteralPath $CMakePath -PathType Leaf)) {
    throw 'CMake was not found. Install the Visual Studio C++ CMake tools or pass -CMakePath.'
}

$ctestPath = Join-Path (Split-Path -Parent $CMakePath) 'ctest.exe'
if (-not (Test-Path -LiteralPath $ctestPath -PathType Leaf)) {
    throw "CTest was not found beside CMake: $ctestPath"
}

$cmakeHelpLines = & $CMakePath --help
if ($LASTEXITCODE -ne 0) {
    throw "CMake could not report its supported generators: $CMakePath"
}
$cmakeHelp = $cmakeHelpLines -join [System.Environment]::NewLine
$generatorPattern = '(?m)^(?<marker>[* ])\s*' +
    '(?<name>Visual Studio (?<major>\d+) \d+)\s+='
$supportedVisualStudioGenerators = @(
    [regex]::Matches($cmakeHelp, $generatorPattern) | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Groups['name'].Value
            Major = [int]$_.Groups['major'].Value
            IsDefault = $_.Groups['marker'].Value -eq '*'
        }
    }
)
if ($supportedVisualStudioGenerators.Count -eq 0) {
    throw "CMake does not report any supported Visual Studio generators: $CMakePath"
}

$installedVisualStudioMajors = @()
if (Test-Path -LiteralPath $vsWherePath -PathType Leaf) {
    $installationVersions = & $vsWherePath `
        -all `
        -products '*' `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -property installationVersion
    if ($LASTEXITCODE -eq 0) {
        $installedVisualStudioMajors = @(
            $installationVersions | ForEach-Object {
                if ($_ -match '^(?<major>\d+)\.') {
                    [int]$Matches['major']
                }
            } | Sort-Object -Descending -Unique
        )
    }
}

if (-not [string]::IsNullOrWhiteSpace($Generator)) {
    $requestedGenerator = $supportedVisualStudioGenerators |
        Where-Object { $_.Name -eq $Generator } |
        Select-Object -First 1
    if ($null -eq $requestedGenerator) {
        throw "CMake does not support the requested Visual Studio generator: $Generator"
    }
    if ($installedVisualStudioMajors.Count -gt 0 -and
        $requestedGenerator.Major -notin $installedVisualStudioMajors) {
        throw "The requested Visual Studio generator is not installed: $Generator"
    }
    $Generator = $requestedGenerator.Name
}
else {
    foreach ($major in $installedVisualStudioMajors) {
        $matchingGenerator = $supportedVisualStudioGenerators |
            Where-Object { $_.Major -eq $major } |
            Select-Object -First 1
        if ($null -ne $matchingGenerator) {
            $Generator = $matchingGenerator.Name
            break
        }
    }

    if ([string]::IsNullOrWhiteSpace($Generator) -and
        $CMakePath -match '[\\/]Microsoft Visual Studio[\\/](?<major>\d+)[\\/]') {
        $bundledMajor = [int]$Matches['major']
        $bundledGenerator = $supportedVisualStudioGenerators |
            Where-Object { $_.Major -eq $bundledMajor } |
            Select-Object -First 1
        if ($null -ne $bundledGenerator) {
            $Generator = $bundledGenerator.Name
        }
    }

    if ([string]::IsNullOrWhiteSpace($Generator)) {
        $Generator = $supportedVisualStudioGenerators |
            Where-Object IsDefault |
            Select-Object -ExpandProperty Name -First 1
    }
}

if ([string]::IsNullOrWhiteSpace($Generator)) {
    throw 'An installed Visual Studio C++ generator could not be selected. Pass -Generator explicitly.'
}

$configureArguments = @(
    '-S', $sourcePath,
    '-B', $buildPath,
    '-G', $Generator,
    '-A', 'x64',
    '-D', 'BUILD_TESTING=ON'
)
if ($Fresh) {
    $configureArguments += '--fresh'
}

$configureExitCode = Invoke-NativeTool `
    -FilePath $CMakePath `
    -ArgumentList $configureArguments
if ($configureExitCode -ne 0) {
    throw "Native configure failed with exit code $configureExitCode."
}

$buildExitCode = Invoke-NativeTool `
    -FilePath $CMakePath `
    -ArgumentList @('--build', $buildPath, '--config', $Configuration)
if ($buildExitCode -ne 0) {
    throw "Native build failed with exit code $buildExitCode."
}

$testExitCode = Invoke-NativeTool `
    -FilePath $ctestPath `
    -ArgumentList @(
        '--test-dir', $buildPath,
        '-C', $Configuration,
        '--no-tests=error',
        '--output-on-failure'
    )
if ($testExitCode -ne 0) {
    throw "Native tests failed with exit code $testExitCode."
}

$nativeDll = Join-Path $buildPath "$Configuration\WinDayFlow.Capture.Native.dll"
if (-not (Test-Path -LiteralPath $nativeDll -PathType Leaf)) {
    throw "Native build did not produce the expected DLL: $nativeDll"
}

[pscustomobject]@{
    Configuration = $Configuration
    Architecture = 'x64'
    Generator = $Generator
    BuildPath = $buildPath
    NativeDll = $nativeDll
    NativeDllSha256 = (Get-FileHash -LiteralPath $nativeDll -Algorithm SHA256).Hash
}
