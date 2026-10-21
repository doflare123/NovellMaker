param([Parameter(Mandatory)][string]$ResultsDirectory)
$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$gitRepository = $repository.Replace('\', '/')
$results = (Resolve-Path -LiteralPath $ResultsDirectory).Path
$reports = @(Get-ChildItem -LiteralPath $results -Recurse -Filter '*.cobertura.xml' |
    Where-Object { [IO.Path]::GetRelativePath($results, $_.FullName) -notmatch '(^|[\\/])In[\\/]' })
if ($reports.Count -ne 1) { throw 'Expected one final merged coverage report; reports must not be averaged.' }
$changed = @{}
$file = $null
$diff = git -c "safe.directory=$gitRepository" -C $repository diff --unified=0 -- src/NovellMaker.Core
if ($LASTEXITCODE -ne 0) { throw 'Cannot read Core diff.' }
foreach ($line in $diff) {
    if ($line -match '^\+\+\+ b/(.+)$') { $file = [IO.Path]::GetFullPath((Join-Path $repository $Matches[1])); $changed[$file] = [Collections.Generic.HashSet[int]]::new() }
    elseif ($null -ne $file -and $line -match '^@@ .* \+(\d+)(?:,(\d+))? @@') {
        $start = [int]$Matches[1]; $count = if ($Matches.ContainsKey(2)) { [int]$Matches[2] } else { 1 }
        for ($i = 0; $i -lt $count; $i++) { [void]$changed[$file].Add($start + $i) }
    }
}
$untracked = git -c "safe.directory=$gitRepository" -C $repository ls-files --others --exclude-standard -- src/NovellMaker.Core
if ($LASTEXITCODE -ne 0) { throw 'Cannot read new Core files.' }
foreach ($path in $untracked) {
    if (-not $path.EndsWith('.cs')) { continue }
    $file = [IO.Path]::GetFullPath((Join-Path $repository $path)); $changed[$file] = [Collections.Generic.HashSet[int]]::new()
    $count = @(Get-Content -LiteralPath $file).Count
    for ($i = 1; $i -le $count; $i++) { [void]$changed[$file].Add($i) }
}
[xml]$document = Get-Content -LiteralPath $reports[0].FullName -Raw
$core = $document.coverage.packages.package | Where-Object name -eq 'NovellMaker.Core'
$rows = [Collections.Generic.List[string]]::new()
$rows.Add('# Changed Core coverage'); $rows.Add('')
$rows.Add('One merged Microsoft.CodeCoverage attachment. Changed executable source lines against HEAD plus new Core files; generated closures are included. Lines are deduplicated by file/number; branch counts come from the collector for every matching class/line. No reports or rates are averaged.')
$rows.Add(''); $rows.Add('| File | Covered lines | Covered branches |'); $rows.Add('| --- | --- | --- |')
$totalLines = 0; $hitLines = 0; $totalBranches = 0; $hitBranches = 0
foreach ($file in $changed.Keys | Sort-Object) {
    $lines = @{}; $branches = 0; $covered = 0
    foreach ($class in @($core.classes.class | Where-Object { $_.filename -eq $file })) {
        foreach ($line in $class.lines.line) {
            $number = [int]$line.number
            if (-not $changed[$file].Contains($number)) { continue }
            $lines[$number] = $lines[$number] -or [int]$line.hits -gt 0
            if ($line.'condition-coverage' -match '\((\d+)/(\d+)\)') { $covered += [int]$Matches[1]; $branches += [int]$Matches[2] }
        }
    }
    if ($lines.Count -eq 0) { continue }
    $hits = @($lines.Values | Where-Object { $_ }).Count
    $totalLines += $lines.Count; $hitLines += $hits; $totalBranches += $branches; $hitBranches += $covered
    $rows.Add("| $([IO.Path]::GetRelativePath($repository, $file)) | $hits/$($lines.Count) | $covered/$branches |")
}
if ($totalLines -eq 0 -or $totalBranches -eq 0) { throw 'No changed executable Core lines/branches found.' }
$lineRate = 100 * $hitLines / $totalLines; $branchRate = 100 * $hitBranches / $totalBranches
$rows.Add(''); $rows.Add(('Total: {0}/{1} lines ({2:F2}%), {3}/{4} branches ({5:F2}%).' -f $hitLines, $totalLines, $lineRate, $hitBranches, $totalBranches, $branchRate))
$rows.Add(''); $rows.Add('Worker diagnostics remain disabled in production. Unobserved worker entry points stay in coverage; direct fixed-fixture worker tests supplement real isolated-process tests.')
Set-Content -LiteralPath (Join-Path $results 'changed-core-coverage.md') -Value $rows -Encoding utf8
$rows | Write-Output
if ($lineRate -lt 90 -or $branchRate -lt 85) { throw 'Changed Core coverage threshold failed (90% lines / 85% branches).' }
