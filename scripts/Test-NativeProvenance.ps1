#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ProvenanceCondition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw [System.IO.InvalidDataException]::new($Message)
    }
}

function Assert-JsonProperties {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [object]$Object,

        [Parameter(Mandatory)]
        [string[]]$Expected,

        [Parameter(Mandatory)]
        [string]$Context
    )

    Assert-ProvenanceCondition `
        -Condition ($Object -is [System.Management.Automation.PSCustomObject]) `
        -Message "$Context must be a JSON object."

    [string[]]$actual = @($Object.PSObject.Properties.Name)
    $expectedSet = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($name in $Expected) {
        [void]$expectedSet.Add($name)
    }

    Assert-ProvenanceCondition `
        -Condition ($actual.Count -eq $expectedSet.Count) `
        -Message "$Context must contain exactly: $($Expected -join ', ')."
    foreach ($name in $actual) {
        Assert-ProvenanceCondition `
            -Condition $expectedSet.Contains($name) `
            -Message "$Context contains an unsupported property: $name."
    }
}

function Assert-RequiredString {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Context
    )

    Assert-ProvenanceCondition `
        -Condition ($Value -is [string] -and
            -not [string]::IsNullOrWhiteSpace([string]$Value)) `
        -Message "$Context must be a non-empty string."
}

function Assert-Sha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Context
    )

    Assert-RequiredString -Value $Value -Context $Context
    Assert-ProvenanceCondition `
        -Condition ([string]$Value -cmatch '\A[0-9A-F]{64}\z') `
        -Message "$Context must be a 64-character uppercase SHA-256 value."
}

function Assert-RelativePath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Context
    )

    Assert-RequiredString -Value $Path -Context $Context
    Assert-ProvenanceCondition `
        -Condition (-not [System.IO.Path]::IsPathRooted($Path)) `
        -Message "$Context must be repository-relative: $Path."
    Assert-ProvenanceCondition `
        -Condition (-not $Path.Contains('\')) `
        -Message "$Context must use forward slashes: $Path."

    [string[]]$segments = @($Path.Split('/'))
    Assert-ProvenanceCondition `
        -Condition ($segments.Count -gt 0 -and
            -not ($segments | Where-Object {
                [string]::IsNullOrEmpty($_) -or $_ -eq '.' -or $_ -eq '..'
            })) `
        -Message "$Context contains an empty or traversal segment: $Path."
}

function Resolve-RepositoryFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Root,

        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    Assert-RelativePath -Path $RelativePath -Context 'local.path'

    $candidate = [System.IO.Path]::GetFullPath(
        (Join-Path $Root ($RelativePath.Replace(
            '/', [System.IO.Path]::DirectorySeparatorChar))))
    $comparison = if ($IsWindows) {
        [System.StringComparison]::OrdinalIgnoreCase
    }
    else {
        [System.StringComparison]::Ordinal
    }
    $rootPrefix = $Root.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar

    Assert-ProvenanceCondition `
        -Condition $candidate.StartsWith($rootPrefix, $comparison) `
        -Message "Local path escapes the repository: $RelativePath."
    Assert-ProvenanceCondition `
        -Condition (Test-Path -LiteralPath $candidate -PathType Leaf) `
        -Message "Derived file does not exist: $RelativePath."

    $resolved = [System.IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $candidate).ProviderPath)
    Assert-ProvenanceCondition `
        -Condition $resolved.StartsWith($rootPrefix, $comparison) `
        -Message "Resolved local path escapes the repository: $RelativePath."
    return $resolved
}

try {
    $root = [System.IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $RepositoryRoot).ProviderPath)
    $manifestPath = Join-Path `
        $root 'docs\provenance\QiDayflow-capture.manifest.json'
    Assert-ProvenanceCondition `
        -Condition (Test-Path -LiteralPath $manifestPath -PathType Leaf) `
        -Message "Native provenance manifest does not exist: $manifestPath."

    $manifest = Get-Content -Raw -LiteralPath $manifestPath -Encoding utf8 |
        ConvertFrom-Json -Depth 16

    $provenancePath = Join-Path `
        $root 'docs\provenance\QiDayflow-capture.md'
    Assert-ProvenanceCondition `
        -Condition (Test-Path -LiteralPath $provenancePath -PathType Leaf) `
        -Message "Native provenance record does not exist: $provenancePath."
    $provenanceText = Get-Content `
        -Raw `
        -LiteralPath $provenancePath `
        -Encoding utf8
    Assert-ProvenanceCondition `
        -Condition (-not $provenanceText.Contains(
            'WORKTREE (pre-initial commit)',
            [System.StringComparison]::Ordinal)) `
        -Message 'The Markdown provenance ledger still contains a pre-initial-commit marker.'

    $ledgerPattern = '(?m)^\| `(?<local>[^`]+)` \| [^|\r\n]+ \| `(?<hash>[0-9A-F]{64})` \| `(?<commit>[^`]+)` \| [^|\r\n]+ \|\r?$'
    $ledgerRows = [System.Collections.Generic.Dictionary[string, object]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($ledgerMatch in [regex]::Matches($provenanceText, $ledgerPattern)) {
        $localPath = $ledgerMatch.Groups['local'].Value
        Assert-ProvenanceCondition `
            -Condition $ledgerRows.TryAdd(
                $localPath,
                [pscustomobject]@{
                    Hash = $ledgerMatch.Groups['hash'].Value
                    Commit = $ledgerMatch.Groups['commit'].Value
                }) `
            -Message "Duplicate Markdown provenance ledger path: $localPath."
    }

    Assert-JsonProperties `
        -Object $manifest `
        -Expected @('schemaVersion', 'source', 'derivedFiles') `
        -Context 'manifest'
    Assert-ProvenanceCondition `
        -Condition ($manifest.schemaVersion -is [long] -and
            $manifest.schemaVersion -eq 1) `
        -Message 'manifest.schemaVersion must be the integer 1.'

    Assert-JsonProperties `
        -Object $manifest.source `
        -Expected @('repository', 'pinnedCommit') `
        -Context 'manifest.source'
    Assert-RequiredString `
        -Value $manifest.source.repository `
        -Context 'manifest.source.repository'
    Assert-ProvenanceCondition `
        -Condition ($manifest.source.repository -ceq
            'https://github.com/liujiaqi7998/QiDayflow.git') `
        -Message 'manifest.source.repository does not match the reviewed source.'

    Assert-RequiredString `
        -Value $manifest.source.pinnedCommit `
        -Context 'manifest.source.pinnedCommit'
    Assert-ProvenanceCondition `
        -Condition ($manifest.source.pinnedCommit -cmatch
            '\A[0-9a-f]{40}\z') `
        -Message 'manifest.source.pinnedCommit must be a lowercase 40-character Git commit.'
    Assert-ProvenanceCondition `
        -Condition ($manifest.source.pinnedCommit -ceq
            '8b82f8a3b23cb29f2b86ee1a6eff19b9343e2e1e') `
        -Message 'manifest.source.pinnedCommit does not match the reviewed revision.'

    $licenseRelativePath = 'licenses/QiDayflow-LICENSE.txt'
    $licenseFile = Resolve-RepositoryFile `
        -Root $root `
        -RelativePath $licenseRelativePath
    $licenseHash = (Get-FileHash `
        -LiteralPath $licenseFile `
        -Algorithm SHA256).Hash
    Assert-ProvenanceCondition `
        -Condition ($licenseHash -ceq
            '8534461B0B8263F5145B229F0C1BA4F4B5BF8A535278C2B3124F289AD10926CB') `
        -Message ("Pinned QiDayflow MIT license SHA-256 mismatch: {0}." -f
            $licenseHash)

    Assert-ProvenanceCondition `
        -Condition ($manifest.derivedFiles -is [System.Array]) `
        -Message 'manifest.derivedFiles must be a JSON array.'
    Assert-ProvenanceCondition `
        -Condition ($manifest.derivedFiles.Count -eq 14) `
        -Message 'manifest.derivedFiles must contain the fourteen active reviewed derived files.'
    Assert-ProvenanceCondition `
        -Condition ($ledgerRows.Count -eq $manifest.derivedFiles.Count) `
        -Message 'The Markdown provenance ledger must contain exactly the manifest derived files.'

    $null = Get-Command git -CommandType Application -ErrorAction Stop

    $verifiedCommits = @(
        $ledgerRows.Values |
            ForEach-Object { [string]$_.Commit } |
            Where-Object { $_ -cne 'WORKTREE (pending commit)' } |
            Sort-Object -Unique
    )
    $isShallowRepository = (
        & git -C $root rev-parse --is-shallow-repository 2>$null
    ) -ceq 'true'
    foreach ($verifiedCommit in $verifiedCommits) {
        $commitObject = '{0}^{{commit}}' -f $verifiedCommit
        $null = & git -C $root cat-file -e $commitObject 2>$null
        if ($LASTEXITCODE -ne 0) {
            $historyHint = if ($isShallowRepository) {
                ' The repository is shallow; checkout the full history (for GitHub Actions, set fetch-depth: 0).'
            }
            else {
                ' Fetch the missing commit before running provenance verification.'
            }
            throw [System.IO.InvalidDataException]::new(
                "Last-verified commit is unavailable: $verifiedCommit.$historyHint")
        }
    }

    $localPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $verifiedCount = 0

    foreach ($entry in $manifest.derivedFiles) {
        $entryContext = "manifest.derivedFiles[$verifiedCount]"
        Assert-JsonProperties `
            -Object $entry `
            -Expected @('local', 'upstream') `
            -Context $entryContext
        Assert-JsonProperties `
            -Object $entry.local `
            -Expected @('path', 'sha256') `
            -Context "$entryContext.local"
        Assert-JsonProperties `
            -Object $entry.upstream `
            -Expected @('path', 'sha256', 'pinnedCommit') `
            -Context "$entryContext.upstream"

        Assert-RequiredString `
            -Value $entry.local.path `
            -Context "$entryContext.local.path"
        Assert-Sha256 `
            -Value $entry.local.sha256 `
            -Context "$entryContext.local.sha256"
        Assert-RelativePath `
            -Path $entry.upstream.path `
            -Context "$entryContext.upstream.path"
        Assert-Sha256 `
            -Value $entry.upstream.sha256 `
            -Context "$entryContext.upstream.sha256"
        Assert-RequiredString `
            -Value $entry.upstream.pinnedCommit `
            -Context "$entryContext.upstream.pinnedCommit"
        Assert-ProvenanceCondition `
            -Condition ($entry.upstream.pinnedCommit -ceq
                $manifest.source.pinnedCommit) `
            -Message "$entryContext upstream commit differs from the pinned source."
        Assert-ProvenanceCondition `
            -Condition $localPaths.Add([string]$entry.local.path) `
            -Message "Duplicate local provenance path: $($entry.local.path)."
        Assert-ProvenanceCondition `
            -Condition $ledgerRows.ContainsKey([string]$entry.local.path) `
            -Message "Markdown provenance ledger entry is missing: $($entry.local.path)."
        $ledgerRow = $ledgerRows[[string]$entry.local.path]
        Assert-ProvenanceCondition `
            -Condition ($ledgerRow.Hash -ceq [string]$entry.local.sha256) `
            -Message "Markdown provenance hash differs from the manifest for $($entry.local.path)."

        $verifiedCommit = [string]$ledgerRow.Commit
        if ($verifiedCommit -ceq 'WORKTREE (pending commit)') {
            $pendingStatus = & git -C $root status `
                --porcelain=v1 `
                --untracked-files=all `
                -- `
                $entry.local.path
            $gitExitCode = $LASTEXITCODE
            Assert-ProvenanceCondition `
                -Condition ($gitExitCode -eq 0 -and
                    -not [string]::IsNullOrWhiteSpace(
                        ($pendingStatus -join [System.Environment]::NewLine))) `
                -Message ("The pending provenance marker for {0} requires an uncommitted file change; git exited {1}." -f
                    $entry.local.path, $gitExitCode)
        }
        else {
            Assert-ProvenanceCondition `
                -Condition ($verifiedCommit -cmatch '\A[0-9a-f]{40}\z') `
                -Message "Invalid last-verified commit for $($entry.local.path): $verifiedCommit."
            $gitObject = '{0}:{1}' -f $verifiedCommit, $entry.local.path
            $null = & git -C $root cat-file -e $gitObject
            Assert-ProvenanceCondition `
                -Condition ($LASTEXITCODE -eq 0) `
                -Message "The last-verified commit does not contain $($entry.local.path): $verifiedCommit."
            $null = & git -C $root diff --quiet $verifiedCommit -- $entry.local.path
            Assert-ProvenanceCondition `
                -Condition ($LASTEXITCODE -eq 0) `
                -Message "The current file differs from its last-verified commit: $($entry.local.path)."
        }

        $localFile = Resolve-RepositoryFile `
            -Root $root `
            -RelativePath $entry.local.path
        $actualHash = (Get-FileHash `
            -LiteralPath $localFile `
            -Algorithm SHA256).Hash
        Assert-ProvenanceCondition `
            -Condition ($actualHash -ceq $entry.local.sha256) `
            -Message ("SHA-256 mismatch for {0}: expected {1}, actual {2}." -f
                $entry.local.path, $entry.local.sha256, $actualHash)

        ++$verifiedCount
    }

    Write-Host (
        'Verified the pinned MIT license, synchronized Markdown ledger, and {0} native derived files against {1}@{2}.' -f
        $verifiedCount,
        $manifest.source.repository,
        $manifest.source.pinnedCommit)
}
catch {
    [Console]::Error.WriteLine(
        "Native provenance verification failed: $($_.Exception.Message)")
    exit 1
}
