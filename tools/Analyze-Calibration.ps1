param(
    [Parameter(Mandatory = $true)]
    [string]$DataDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

function ConvertTo-Hashtable {
    param([object]$Value)
    $result = @{}
    if ($null -eq $Value) { return $result }
    foreach ($property in $Value.PSObject.Properties) {
        $result[$property.Name] = [long]$property.Value
    }
    return $result
}

function Get-PropertyValue {
    param([object]$Object, [string]$Name, [object]$Default = $null)
    if ($null -eq $Object) { return $Default }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $Default }
    return $property.Value
}

function Write-JsonAtomic {
    param([string]$Path, [object]$Value, [int]$Depth = 8)
    $temporaryPath = $Path + '.tmp'
    $json = $Value | ConvertTo-Json -Depth $Depth
    [System.IO.File]::WriteAllText($temporaryPath, $json, [System.Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
}

function Get-ProfileName {
    param([object]$BaseProfile)
    switch ([int]$BaseProfile) {
        0 { return 'Lite' }
        1 { return 'Turbo' }
        2 { return 'Ultimate' }
        default { return "Unknown:$BaseProfile" }
    }
}

function Normalize-ProfileKey {
    param([object]$ProfileKey)
    if ($null -eq $ProfileKey) { return $null }
    switch ([string]$ProfileKey) {
        'builtin:Daily' { return 'builtin:Lite' }
        'builtin:Gaming' { return 'builtin:Turbo' }
        'builtin:Extreme' { return 'builtin:Ultimate' }
        default { return [string]$ProfileKey }
    }
}

function Get-CoverageLabel {
    param([int]$Families, [int]$Runs)
    if ($Families -eq 0 -or $Runs -eq 0) { return 'missing' }
    if ($Families -lt 10 -or $Runs -lt 5) { return 'limited' }
    return 'broader'
}

function Get-FilePrefixHash {
    param([string]$Path)
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $length = [int][Math]::Min(4096, $stream.Length)
        $buffer = New-Object byte[] $length
        [void]$stream.Read($buffer, 0, $length)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { return [Convert]::ToBase64String($sha.ComputeHash($buffer)) }
        finally { $sha.Dispose() }
    } finally {
        $stream.Dispose()
    }
}

$sourcePath = [System.IO.Path]::GetFullPath((Join-Path $DataDirectory 'calibration-metrics.jsonl'))
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Calibration file not found: $sourcePath"
}
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null

$statePath = Join-Path $outputPath 'analysis-state.json'
$eventCachePath = Join-Path $outputPath 'analysis-events.jsonl'
$reportJsonPath = Join-Path $outputPath 'calibration-report.json'
$reportMarkdownPath = Join-Path $outputPath 'calibration-report.md'

if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if (-not [string]::Equals($state.SourcePath, $sourcePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output directory already belongs to another source: $($state.SourcePath)"
    }
    $byKind = ConvertTo-Hashtable $state.ByKind
    $bySchema = ConvertTo-Hashtable $state.BySchema
} else {
    $state = [pscustomobject]@{
        Version = 1
        SourcePath = $sourcePath
        Offset = 0L
        TotalEvents = 0L
        InvalidLines = 0L
        RotationCount = 0
        SourcePrefixHash = $null
        ByKind = @{}
        BySchema = @{}
        LastProcessedAtUtc = $null
    }
    $byKind = @{}
    $bySchema = @{}
}

$sourceLength = (Get-Item -LiteralPath $sourcePath).Length
$sourcePrefixHash = Get-FilePrefixHash $sourcePath
$knownPrefixHash = Get-PropertyValue $state 'SourcePrefixHash'
if ($null -eq $state.PSObject.Properties['SourcePrefixHash']) {
    $state | Add-Member -NotePropertyName SourcePrefixHash -NotePropertyValue $null
}
if ($sourceLength -lt [long]$state.Offset -or
    (-not [string]::IsNullOrWhiteSpace($knownPrefixHash) -and $knownPrefixHash -ne $sourcePrefixHash)) {
    $state.Offset = 0L
    $state.RotationCount = [int]$state.RotationCount + 1
}

$newEventCount = 0L
$normalized = [System.Text.StringBuilder]::new()
$startOffset = [long]$state.Offset
$completeByteCount = 0L
$stream = [System.IO.FileStream]::new(
    $sourcePath,
    [System.IO.FileMode]::Open,
    [System.IO.FileAccess]::Read,
    [System.IO.FileShare]::ReadWrite)
try {
    [void]$stream.Seek($startOffset, [System.IO.SeekOrigin]::Begin)
    $remainingLength = $stream.Length - $startOffset
    if ($remainingLength -gt [int]::MaxValue) {
        throw "Unprocessed calibration tail is too large: $remainingLength bytes"
    }
    $buffer = New-Object byte[] ([int]$remainingLength)
    $bytesRead = 0
    while ($bytesRead -lt $buffer.Length) {
        $read = $stream.Read($buffer, $bytesRead, $buffer.Length - $bytesRead)
        if ($read -le 0) { break }
        $bytesRead += $read
    }
    $tailText = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $bytesRead)
    $lastNewline = $tailText.LastIndexOf("`n", [StringComparison]::Ordinal)
    if ($lastNewline -ge 0) {
        $completeText = $tailText.Substring(0, $lastNewline + 1)
        $completeByteCount = [System.Text.Encoding]::UTF8.GetByteCount($completeText)
        foreach ($lineWithCarriageReturn in $completeText.Split("`n")) {
            $line = $lineWithCarriageReturn.TrimEnd("`r")
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try {
                $envelope = $line | ConvertFrom-Json
            } catch {
                $state.InvalidLines = [long]$state.InvalidLines + 1
                continue
            }

            $schema = [string]$envelope.SchemaVersion
            $kind = [string]$envelope.Kind
            if (-not $bySchema.ContainsKey($schema)) { $bySchema[$schema] = 0L }
            if (-not $byKind.ContainsKey($kind)) { $byKind[$kind] = 0L }
            $bySchema[$schema]++
            $byKind[$kind]++
            $state.TotalEvents = [long]$state.TotalEvents + 1
            $newEventCount++

            if ($kind -notin @('candidate-plan', 'application-outcome', 'optimization-run')) { continue }
            $payload = $envelope.Payload
            $context = Get-PropertyValue $payload 'RunContext'
            $common = [ordered]@{
                Type = $kind
                Schema = [int]$envelope.SchemaVersion
                SessionId = Get-PropertyValue $envelope 'SessionId'
                Sequence = Get-PropertyValue $envelope 'Sequence'
                WrittenAtUtc = Get-PropertyValue $envelope 'WrittenAtUtc'
                ProfileKey = Normalize-ProfileKey (Get-PropertyValue $context 'ProfileKey')
                BaseProfile = Get-PropertyValue $context 'BaseProfile' -1
                Trigger = Get-PropertyValue $context 'Trigger' -1
                RunId = Get-PropertyValue $context 'RunId'
            }

            if ($kind -eq 'candidate-plan') {
                $idleSamples = @()
                foreach ($shadow in @(Get-PropertyValue $payload 'IdleScoreShadows' @())) {
                    $actual = [bool](Get-PropertyValue $shadow 'ActualPolicyEligible' $false)
                    $experimental = [bool](Get-PropertyValue $shadow 'ExperimentalMeetsThreshold' $false)
                    $idleSamples += [ordered]@{
                        FamilyId = Get-PropertyValue $shadow 'FamilyId'
                        SamplingReason = Get-PropertyValue $shadow 'SamplingReason'
                        ActualEligible = $actual
                        ExperimentalEligible = $experimental
                        LegacyIdleScore = Get-PropertyValue $shadow 'LegacyIdleScore' 0
                        IdleConfidenceScore = Get-PropertyValue $shadow 'IdleConfidenceScore' 0
                        ExperimentalIdleScore = Get-PropertyValue $shadow 'ExperimentalIdleScore' 0
                        IdleThreshold = Get-PropertyValue $shadow 'IdleThreshold' 0
                        IdleForSeconds = Get-PropertyValue $shadow 'IdleForSeconds' 0
                        ProcessInputs = @(Get-PropertyValue $shadow 'ProcessInputs' @())
                    }
                }
                $parameterShadows = @()
                foreach ($shadow in @(Get-PropertyValue $payload 'ProfileParameterShadows' @())) {
                    $parameterShadows += [ordered]@{
                        Source = 'profile'
                        Key = Get-PropertyValue $shadow 'Key'
                        IsBaseline = [bool](Get-PropertyValue $shadow 'IsBaseline' $false)
                        ComparisonKind = Get-PropertyValue $shadow 'ComparisonKind' ''
                        ParameterName = Get-PropertyValue $shadow 'ParameterName'
                        BaselineValue = Get-PropertyValue $shadow 'BaselineValue'
                        ShadowValue = Get-PropertyValue $shadow 'ShadowValue'
                        AddedFamilyIds = @(Get-PropertyValue $shadow 'AddedFamilyIds' @())
                        RemovedFamilyIds = @(Get-PropertyValue $shadow 'RemovedFamilyIds' @())
                    }
                }
                foreach ($shadow in @(Get-PropertyValue $payload 'ActivityThresholdShadows' @())) {
                    $parameterShadows += [ordered]@{
                        Source = 'activity'
                        Key = Get-PropertyValue $shadow 'Key'
                        IsBaseline = [bool](Get-PropertyValue $shadow 'IsBaseline' $false)
                        ComparisonKind = Get-PropertyValue $shadow 'ComparisonKind' ''
                        ParameterName = Get-PropertyValue $shadow 'ParameterName'
                        BaselineValue = Get-PropertyValue $shadow 'BaselineValue'
                        ShadowValue = Get-PropertyValue $shadow 'ShadowValue'
                        AddedFamilyIds = @(Get-PropertyValue $shadow 'AddedFamilyIds' @())
                        RemovedFamilyIds = @(Get-PropertyValue $shadow 'RemovedFamilyIds' @())
                    }
                }
                $record = $common + [ordered]@{
                    RecordedAt = Get-PropertyValue $payload 'RecordedAt'
                    EligibleFamilyCount = Get-PropertyValue $payload 'EligibleFamilyCount' 0
                    SelectedFamilyCount = Get-PropertyValue $payload 'SelectedFamilyCount' 0
                    IdleSamples = $idleSamples
                    ParameterShadows = $parameterShadows
                }
            } elseif ($kind -eq 'application-outcome') {
                $record = $common + [ordered]@{
                    FamilyId = Get-PropertyValue $payload 'FamilyId'
                    StartedAt = Get-PropertyValue $payload 'StartedAt'
                    ReboundPercent = Get-PropertyValue $payload 'ReboundPercent' 0
                    BackoffTriggered = Get-PropertyValue $payload 'BackoffTriggered' $false
                    LateWorkingSetBytes = Get-PropertyValue $payload 'LateWorkingSetBytes'
                }
            } else {
                $record = $common + [ordered]@{
                    PayloadRunId = Get-PropertyValue $payload 'RunId'
                    StartedAt = Get-PropertyValue $payload 'StartedAt'
                    CompletedAt = Get-PropertyValue $payload 'CompletedAt'
                    SucceededProcessCount = Get-PropertyValue $payload 'SucceededProcessCount' 0
                }
            }
            [void]$normalized.AppendLine(($record | ConvertTo-Json -Depth 8 -Compress))
        }
    }
    $state.Offset = $startOffset + $completeByteCount
} finally {
    $stream.Dispose()
}

if ($normalized.Length -gt 0) {
    [System.IO.File]::AppendAllText($eventCachePath, $normalized.ToString(), [System.Text.UTF8Encoding]::new($false))
}
$state.ByKind = $byKind
$state.BySchema = $bySchema
$state.SourcePrefixHash = $sourcePrefixHash
$state.LastProcessedAtUtc = [DateTimeOffset]::UtcNow
Write-JsonAtomic $statePath $state

$events = if (Test-Path -LiteralPath $eventCachePath) {
    @(Get-Content -LiteralPath $eventCachePath | ForEach-Object { $_ | ConvertFrom-Json })
} else { @() }
foreach ($event in $events) {
    $event.ProfileKey = Normalize-ProfileKey $event.ProfileKey
}
$plans = @($events | Where-Object Type -eq 'candidate-plan')
$outcomes = @($events | Where-Object Type -eq 'application-outcome')
$runs = @($events | Where-Object Type -eq 'optimization-run')

$planRunIds = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($plan in $plans) {
    if (-not [string]::IsNullOrWhiteSpace($plan.RunId)) { [void]$planRunIds.Add($plan.RunId) }
}
$linkedOutcomes = @($outcomes | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.RunId) -and $planRunIds.Contains($_.RunId)
})
$outcomesWithLateWorkingSet = @($outcomes | Where-Object { $null -ne $_.LateWorkingSetBytes })

$profiles = foreach ($profileNumber in 0..2) {
    $profilePlans = @($plans | Where-Object { [int]$_.BaseProfile -eq $profileNumber })
    $profileOutcomes = @($outcomes | Where-Object { [int]$_.BaseProfile -eq $profileNumber })
    $families = @($profileOutcomes.FamilyId | Where-Object { $_ } | Sort-Object -Unique)
    $runIds = @($profileOutcomes.RunId | Where-Object {
        $_ -and $planRunIds.Contains($_)
    } | Sort-Object -Unique)
    [pscustomobject]@{
        Profile = Get-ProfileName $profileNumber
        Plans = $profilePlans.Count
        Outcomes = $profileOutcomes.Count
        OutcomeFamilies = $families.Count
        CorrelatedRuns = $runIds.Count
        Coverage = Get-CoverageLabel $families.Count $runIds.Count
    }
}

$outcomesByRunId = @{}
$outcomesByRunFamily = @{}
foreach ($outcome in $outcomes) {
    if ([string]::IsNullOrWhiteSpace($outcome.RunId)) { continue }
    if (-not $outcomesByRunId.ContainsKey($outcome.RunId)) {
        $outcomesByRunId[$outcome.RunId] = [System.Collections.Generic.List[object]]::new()
    }
    $outcomesByRunId[$outcome.RunId].Add($outcome)
    $runFamilyKey = "$($outcome.RunId)|$($outcome.FamilyId)"
    if (-not $outcomesByRunFamily.ContainsKey($runFamilyKey)) {
        $outcomesByRunFamily[$runFamilyKey] = [System.Collections.Generic.List[object]]::new()
    }
    $outcomesByRunFamily[$runFamilyKey].Add($outcome)
}

$variantMap = @{}
$baselineParityPlans = 0
$baselineDriftPlans = 0
$legacyBaselinePlans = 0
$indistinguishableVariantGroups = 0
$indistinguishableByVariant = @{}
foreach ($plan in $plans) {
    $planShadows = @($plan.ParameterShadows)
    $planHasBaselineDrift = $false
    foreach ($baselineShadow in @($planShadows | Where-Object {
        [bool](Get-PropertyValue $_ 'IsBaseline' $false) -or $_.ParameterName -eq 'baseline'
    })) {
        if ((Get-PropertyValue $baselineShadow 'ComparisonKind' '') -ne 'formal-plan-drift') {
            $legacyBaselinePlans++
            continue
        }
        $baselineParityPlans++
        if (@($baselineShadow.AddedFamilyIds).Count + @($baselineShadow.RemovedFamilyIds).Count -gt 0) {
            $baselineDriftPlans++
            $planHasBaselineDrift = $true
        }
    }
    if (-not $planHasBaselineDrift) {
        foreach ($sourceGroup in @($planShadows | Where-Object {
            -not ([bool](Get-PropertyValue $_ 'IsBaseline' $false) -or $_.ParameterName -eq 'baseline')
        } | Group-Object Source)) {
            if ($sourceGroup.Count -lt 2) { continue }
            $signatures = @($sourceGroup.Group | ForEach-Object {
                $addedSignature = (@($_.AddedFamilyIds) | Sort-Object) -join ','
                $removedSignature = (@($_.RemovedFamilyIds) | Sort-Object) -join ','
                "$addedSignature|$removedSignature"
            } | Sort-Object -Unique)
            if ($signatures.Count -eq 1) {
                $indistinguishableVariantGroups++
                foreach ($shadow in $sourceGroup.Group) {
                    $source = if ([string]::IsNullOrWhiteSpace($shadow.Source)) { 'profile' } else { $shadow.Source }
                    $identity = "$source|$($shadow.Key)"
                    if (-not $indistinguishableByVariant.ContainsKey($identity)) { $indistinguishableByVariant[$identity] = 0 }
                    $indistinguishableByVariant[$identity]++
                }
            }
        }
    }
    foreach ($shadow in @($plan.ParameterShadows)) {
        if ($null -eq $shadow -or [string]::IsNullOrWhiteSpace($shadow.Key)) { continue }
        $isBaseline = [bool](Get-PropertyValue $shadow 'IsBaseline' ($shadow.ParameterName -eq 'baseline'))
        if ($planHasBaselineDrift -and -not $isBaseline) { continue }
        $source = if ([string]::IsNullOrWhiteSpace($shadow.Source)) { 'profile' } else { $shadow.Source }
        $identity = "$source|$($shadow.Key)"
        if (-not $variantMap.ContainsKey($identity)) {
            $variantMap[$identity] = [pscustomobject]@{
                Source = $source
                Key = $shadow.Key
                ParameterName = $shadow.ParameterName
                BaselineValue = $shadow.BaselineValue
                ShadowValue = $shadow.ShadowValue
                IsBaseline = $isBaseline
                Plans = 0
                ChangedPlans = 0
                AddedOccurrences = 0
                RemovedOccurrences = 0
                Families = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
                LinkedOutcomes = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            }
        }
        $entry = $variantMap[$identity]
        $entry.Plans++
        $added = @($shadow.AddedFamilyIds)
        $removed = @($shadow.RemovedFamilyIds)
        $currentFamilies = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        if ($added.Count + $removed.Count -gt 0) { $entry.ChangedPlans++ }
        $entry.AddedOccurrences += $added.Count
        $entry.RemovedOccurrences += $removed.Count
        foreach ($familyId in @($added + $removed)) {
            if (-not [string]::IsNullOrWhiteSpace($familyId)) {
                [void]$entry.Families.Add($familyId)
                [void]$currentFamilies.Add($familyId)
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($plan.RunId)) {
            foreach ($outcome in @($outcomesByRunId[$plan.RunId])) {
                if ($currentFamilies.Contains($outcome.FamilyId)) {
                    [void]$entry.LinkedOutcomes.Add("$($outcome.RunId)|$($outcome.FamilyId)|$($outcome.StartedAt)")
                }
            }
        }
    }
}
$variants = @($variantMap.Values | ForEach-Object {
    $identity = "$($_.Source)|$($_.Key)"
    $indistinguishablePlans = if ($indistinguishableByVariant.ContainsKey($identity)) {
        [int]$indistinguishableByVariant[$identity]
    } else { 0 }
    [pscustomobject]@{
        Source = $_.Source
        Key = $_.Key
        ParameterName = $_.ParameterName
        BaselineValue = $_.BaselineValue
        ShadowValue = $_.ShadowValue
        IsBaseline = $_.IsBaseline
        Plans = $_.Plans
        ChangedPlans = $_.ChangedPlans
        AddedOccurrences = $_.AddedOccurrences
        RemovedOccurrences = $_.RemovedOccurrences
        UniqueChangedFamilies = $_.Families.Count
        CorrelatedOutcomes = $_.LinkedOutcomes.Count
        IndistinguishablePlans = $indistinguishablePlans
        Feedback = if ($_.IsBaseline) {
            if ($_.ChangedPlans -eq 0) { 'parity' } else { 'drift' }
        } elseif ($indistinguishablePlans -eq $_.Plans) { 'invalid-identical' }
        elseif ($_.ChangedPlans -eq 0) { 'zero-feedback' }
        elseif ($indistinguishablePlans -gt 0) { 'mixed-feedback' }
        else { 'responsive' }
    }
} | Sort-Object Source, Key)

$idleSamples = @()
foreach ($plan in $plans) {
    foreach ($sample in @($plan.IdleSamples)) {
        if ($null -eq $sample) { continue }
        $idleSamples += [pscustomobject]@{
            RunId = $plan.RunId
            FamilyId = $sample.FamilyId
            SamplingReason = $sample.SamplingReason
            ActualEligible = [bool]$sample.ActualEligible
            ExperimentalEligible = [bool]$sample.ExperimentalEligible
            TargetProcessCount = [int](Get-PropertyValue $sample 'TargetProcessCount' 0)
            ProcessInputCount = @($sample.ProcessInputs).Count
        }
    }
}
$idleFormulaSamples = @($idleSamples | Where-Object {
    $_.SamplingReason -in @('Disagreement', 'NearThreshold')
})
$idleDisagreements = @($idleFormulaSamples | Where-Object {
    $_.ActualEligible -ne $_.ExperimentalEligible
})
$idleLinkedOutcomeKeys = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($sample in $idleDisagreements | Where-Object { -not [string]::IsNullOrWhiteSpace($_.RunId) }) {
    $runFamilyKey = "$($sample.RunId)|$($sample.FamilyId)"
    foreach ($outcome in @($outcomesByRunFamily[$runFamilyKey])) {
        [void]$idleLinkedOutcomeKeys.Add("$($outcome.RunId)|$($outcome.FamilyId)|$($outcome.StartedAt)")
    }
}

$benefitSummary = $null
$benefitPath = Join-Path $DataDirectory 'benefit-learning.json'
if (Test-Path -LiteralPath $benefitPath) {
    $benefit = Get-Content -LiteralPath $benefitPath -Raw | ConvertFrom-Json
    $records = @($benefit.Records)
    $benefitSummary = [pscustomobject]@{
        Components = $records.Count
        Families = @($records.FamilyKey | Sort-Object -Unique).Count
        ValidSamples = [long](($records | Measure-Object ValidSampleCount -Sum).Sum)
        ComponentsWithAtLeastFiveValidSamples = @($records | Where-Object { $_.ValidSampleCount -ge 5 }).Count
    }
}

$reboundSummary = $null
$reboundPath = Join-Path $DataDirectory 'rebound-history.json'
if (Test-Path -LiteralPath $reboundPath) {
    $rebound = Get-Content -LiteralPath $reboundPath -Raw | ConvertFrom-Json
    $reboundRuns = @($rebound.Runs)
    $reboundSummary = [pscustomobject]@{
        Runs = $reboundRuns.Count
        CompletedRuns = @($reboundRuns | Where-Object { $_.State -eq 1 }).Count
        ReplacedRuns = @($reboundRuns | Where-Object { $_.State -ne 1 }).Count
        CompletedApplications = @($reboundRuns | Where-Object { $_.State -eq 1 } | ForEach-Object { @($_.Details).Count } | Measure-Object -Sum).Sum
    }
}

$ioEventCount = if ($byKind.ContainsKey('process-io-sample')) { [long]$byKind['process-io-sample'] } else { 0L }
$cpuEventCount = if ($byKind.ContainsKey('process-cpu-sample')) { [long]$byKind['process-cpu-sample'] } else { 0L }
$activityEvents = $ioEventCount + $cpuEventCount
$noisePercent = if ([long]$state.TotalEvents -gt 0) {
    [math]::Round(100 * $activityEvents / [long]$state.TotalEvents, 1)
} else { 0 }
$gaps = [System.Collections.Generic.List[string]]::new()
if (-not $bySchema.ContainsKey('10')) { $gaps.Add('No schema 10 events: direct run correlation is unavailable.') }
if ($outcomes.Count -gt 0 -and $linkedOutcomes.Count -lt $outcomes.Count) {
    $gaps.Add("Only $($linkedOutcomes.Count) of $($outcomes.Count) outcomes have a direct candidate-plan RunId link.")
}
$schema10Outcomes = @($outcomes | Where-Object Schema -ge 10)
if ($schema10Outcomes.Count -gt 0 -and
    @($schema10Outcomes | Where-Object { $null -eq $_.LateWorkingSetBytes }).Count -gt 0) {
    $gaps.Add('One or more schema 10 outcomes are missing the late Working Set result.')
}
foreach ($profile in $profiles) {
    if ($profile.Coverage -eq 'missing') { $gaps.Add("$($profile.Profile) has no directly correlated outcome coverage.") }
    elseif ($profile.Coverage -eq 'limited') { $gaps.Add("$($profile.Profile) outcome coverage is limited: $($profile.OutcomeFamilies) families, $($profile.CorrelatedRuns) correlated runs.") }
}
foreach ($variant in $variants | Where-Object { $_.ChangedPlans -gt 0 -and $_.CorrelatedOutcomes -eq 0 }) {
    $gaps.Add("$($variant.Key) changes candidates but has no directly correlated outcome.")
}
if (@($idleFormulaSamples | Where-Object ProcessInputCount -eq 0).Count -gt 0 -and $bySchema.ContainsKey('10')) {
    $gaps.Add('One or more schema 10 disagreement/near-threshold idle samples are missing per-process formula inputs.')
}
if ($idleDisagreements.Count -gt 0 -and $idleLinkedOutcomeKeys.Count -eq 0) {
    $gaps.Add("$($idleDisagreements.Count) formal-policy versus local-shadow eligibility differences have no directly correlated outcome.")
}
if ($baselineDriftPlans -gt 0) {
    $gaps.Add("Read-only baseline drifted from the formal plan in $baselineDriftPlans of $baselineParityPlans parity checks; affected parameter results must be treated as invalid.")
}
if ($indistinguishableVariantGroups -gt 0) {
    $gaps.Add("All variants produced the same candidate delta in $indistinguishableVariantGroups plan/source groups; those groups provide no parameter-specific evidence.")
}

$report = [pscustomobject]@{
    GeneratedAtUtc = [DateTimeOffset]::UtcNow
    SourcePath = $sourcePath
    Incremental = [pscustomobject]@{
        NewEvents = $newEventCount
        ProcessedOffset = [long]$state.Offset
        SourceLength = $sourceLength
        RotationCount = [int]$state.RotationCount
    }
    Events = [pscustomobject]@{
        Total = [long]$state.TotalEvents
        InvalidLines = [long]$state.InvalidLines
        BySchema = $bySchema
        ByKind = $byKind
        ActivityEpisodeEvents = $activityEvents
        ActivityEpisodePercent = $noisePercent
    }
    Correlation = [pscustomobject]@{
        CandidatePlans = $plans.Count
        Outcomes = $outcomes.Count
        OutcomesWithDirectPlanLink = $linkedOutcomes.Count
        OutcomesWithLateWorkingSet = $outcomesWithLateWorkingSet.Count
        OptimizationRuns = $runs.Count
    }
    IdleScore = [pscustomobject]@{
        Samples = $idleSamples.Count
        SamplesWithProcessInputs = @($idleSamples | Where-Object ProcessInputCount -gt 0).Count
        FormulaSamples = $idleFormulaSamples.Count
        FormulaSamplesWithProcessInputs = @($idleFormulaSamples | Where-Object ProcessInputCount -gt 0).Count
        ControlSamples = @($idleSamples | Where-Object SamplingReason -eq 'ControlSample').Count
        EligibilityDisagreements = $idleDisagreements.Count
        DisagreementOutcomesWithDirectLink = $idleLinkedOutcomeKeys.Count
    }
    ShadowDiagnostics = [pscustomobject]@{
        BaselineParityChecks = $baselineParityPlans
        BaselineDriftPlans = $baselineDriftPlans
        LegacyBaselinePlansWithoutParityCheck = $legacyBaselinePlans
        IndistinguishableVariantGroups = $indistinguishableVariantGroups
        ZeroFeedbackVariants = @($variants | Where-Object { -not $_.IsBaseline -and $_.Feedback -eq 'zero-feedback' }).Count
        InvalidIdenticalVariants = @($variants | Where-Object Feedback -eq 'invalid-identical').Count
    }
    Profiles = $profiles
    ParameterVariants = $variants
    BenefitLearning = $benefitSummary
    ReboundHistory = $reboundSummary
    CoverageGaps = @($gaps)
    CoverageNote = 'Coverage labels are operational collection checks, not proof that a formula or parameter is optimal.'
}
Write-JsonAtomic $reportJsonPath $report 10

$markdown = [System.Text.StringBuilder]::new()
[void]$markdown.AppendLine('# MuseRAM calibration report')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("Generated: $($report.GeneratedAtUtc.ToString('o'))")
[void]$markdown.AppendLine("Source: $sourcePath")
[void]$markdown.AppendLine("Incremental events: $newEventCount; total events: $($state.TotalEvents)")
[void]$markdown.AppendLine("CPU/I/O episode share: $noisePercent%")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Profile coverage')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Profile | Plans | Outcomes | Families | Correlated runs | Coverage |')
[void]$markdown.AppendLine('|---|---:|---:|---:|---:|---|')
foreach ($profile in $profiles) {
    [void]$markdown.AppendLine("| $($profile.Profile) | $($profile.Plans) | $($profile.Outcomes) | $($profile.OutcomeFamilies) | $($profile.CorrelatedRuns) | $($profile.Coverage) |")
}
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Correlation')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("Directly linked outcomes: $($linkedOutcomes.Count) / $($outcomes.Count)")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Idle-score evidence')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("Formula samples with process inputs: $($report.IdleScore.FormulaSamplesWithProcessInputs) / $($report.IdleScore.FormulaSamples); control samples: $($report.IdleScore.ControlSamples); eligibility disagreements with linked outcomes: $($report.IdleScore.DisagreementOutcomesWithDirectLink) / $($report.IdleScore.EligibilityDisagreements)")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Shadow validity')
[void]$markdown.AppendLine()
[void]$markdown.AppendLine("Verified recomputed baseline parity: $($baselineParityPlans - $baselineDriftPlans) / $baselineParityPlans; drifted plans: $baselineDriftPlans; legacy baselines without a parity check: $legacyBaselinePlans")
[void]$markdown.AppendLine("Indistinguishable variant groups: $indistinguishableVariantGroups")
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('| Source | Variant | Parameter | Plans | Changed | Identical groups | Feedback |')
[void]$markdown.AppendLine('|---|---|---|---:|---:|---:|---|')
foreach ($variant in $variants) {
    [void]$markdown.AppendLine("| $($variant.Source) | $($variant.Key) | $($variant.ParameterName) | $($variant.Plans) | $($variant.ChangedPlans) | $($variant.IndistinguishablePlans) | $($variant.Feedback) |")
}
[void]$markdown.AppendLine()
[void]$markdown.AppendLine('## Coverage gaps')
[void]$markdown.AppendLine()
if ($gaps.Count -eq 0) { [void]$markdown.AppendLine('- No structural collection gaps detected.') }
else { foreach ($gap in $gaps) { [void]$markdown.AppendLine("- $gap") } }
[void]$markdown.AppendLine()
[void]$markdown.AppendLine($report.CoverageNote)
[System.IO.File]::WriteAllText($reportMarkdownPath, $markdown.ToString(), [System.Text.UTF8Encoding]::new($false))

Write-Output "Report: $reportMarkdownPath"
Write-Output "New events: $newEventCount"
Write-Output "Total events: $($state.TotalEvents)"
