param([Parameter(Mandatory)][string]$ResultsDirectory)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$expected = @{
    'Runtime.trx' = @{ 'Runtime_RendersSceneAndUsesSharedNextPreviousStateTransitions' = 2 }
    'Editor.trx' = @{ 'NativeInstallerInstallsRunsGameAndUninstallsWithoutRemovingSavesOrForeignFiles' = 1 }
}
foreach ($file in $expected.Keys) {
    $path = Join-Path $ResultsDirectory $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release test report: $file" }
    [xml]$report = Get-Content -LiteralPath $path -Raw
    $ns = [Xml.XmlNamespaceManager]::new($report.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $tests = @($report.SelectNodes('//t:UnitTestResult', $ns))
    if ($tests.Count -eq 0 -or @($tests | Where-Object { $_.outcome -ne 'Passed' }).Count -gt 0 -or
        $report.TestRun.ResultSummary.outcome -ne 'Completed') { throw "$file contains missing, skipped or failed release checks." }
    foreach ($method in $expected[$file].Keys) {
        $definitions = @($report.SelectNodes('//t:UnitTest', $ns) | Where-Object { $_.TestMethod.name -eq $method })
        if ($definitions.Count -ne $expected[$file][$method]) { throw "Missing or duplicate release test: $method" }
        $ids = @($definitions | ForEach-Object { $_.id })
        $executions = @($tests | Where-Object { $_.testId -in $ids })
        if ($executions.Count -ne $expected[$file][$method]) { throw "Unexpected release case count: $method" }
        foreach ($id in $ids) {
            if (@($executions | Where-Object { $_.testId -eq $id }).Count -ne 1) { throw "Missing or duplicate execution: $method" }
        }
    }
}
Write-Output 'All required delivery cases passed without skips.'
