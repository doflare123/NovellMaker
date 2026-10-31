param([Parameter(Mandatory)][string]$ResultsDirectory)

$ErrorActionPreference = 'Stop'
$rootDirectory = (Resolve-Path -LiteralPath $ResultsDirectory).Path
# VSTest also retains pre-merge copies under TRX/In. Use only final collector attachments.
$reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter '*.cobertura.xml' |
    Where-Object { [System.IO.Path]::GetRelativePath($rootDirectory, $_.FullName) -notmatch '(^|[\\/])In[\\/]' })
if ($reports.Count -eq 0) { throw "No Cobertura reports in $ResultsDirectory." }
$culture = [System.Globalization.CultureInfo]::InvariantCulture
function Percent([string]$Value) {
    $number = [double]::Parse($Value, $culture)
    if (-not [double]::IsFinite($number) -or $number -lt 0 -or $number -gt 1) { throw "Invalid coverage rate: $Value" }
    return ($number * 100).ToString('F2', $culture) + '%'
}
$rows = [System.Collections.Generic.List[string]]::new()
$rows.Add('# Coverage reports')
$rows.Add('')
$rows.Add('Microsoft.CodeCoverage 18.8.1 (locked via Microsoft.NET.Test.Sdk). Final collector attachments; raw TRX/In copies are omitted. Rates are not averaged or added by this script.')
$rows.Add('')
$rows.Add('| Report | Module | Lines | Branches |')
$rows.Add('| --- | --- | --- | --- |')
foreach ($report in $reports) {
    [xml]$document = Get-Content -Raw -LiteralPath $report.FullName
    $packages = @($document.coverage.packages.package)
    if ($packages.Count -eq 0) { throw "No modules in $($report.FullName)." }
    foreach ($package in $packages) {
        if ($null -eq $package -or $package.name -notmatch '^NovellMaker\.(Core|Runtime|Editor)$') { throw 'Unexpected coverage module.' }
        $label = [System.IO.Path]::GetRelativePath($rootDirectory, $report.FullName)
        $rows.Add("| $label | $($package.name) | $(Percent $package.'line-rate') | $(Percent $package.'branch-rate') |")
    }
}
$rows.Add('')
$rows.Add('Child worker coverage is not guaranteed: production disables CLR diagnostics. Unobserved worker paths remain in the denominator. Native/GUI behaviour requires the corresponding smoke tests. See docs/lua-reference/execution-model.md.')
$summary = Join-Path $ResultsDirectory 'coverage-summary.md'
Set-Content -LiteralPath $summary -Value $rows -Encoding utf8
$rows | Write-Output
Write-Output "Saved: $summary"
