$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent
$root = [IO.Path]::GetFullPath((Join-Path $repository ('TestResults/release-gate-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $runtime = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testId="a" outcome="Passed"/><UnitTestResult testId="b" outcome="Passed"/></Results>
  <TestDefinitions>
    <UnitTest id="a"><TestMethod name="Runtime_RendersSceneAndUsesSharedNextPreviousStateTransitions"/></UnitTest>
    <UnitTest id="b"><TestMethod name="Runtime_RendersSceneAndUsesSharedNextPreviousStateTransitions"/></UnitTest>
  </TestDefinitions><ResultSummary outcome="Completed"/>
</TestRun>
'@
    $editor = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testId="c" outcome="Passed"/></Results>
  <TestDefinitions><UnitTest id="c"><TestMethod name="NativeInstallerInstallsRunsGameAndUninstallsWithoutRemovingSavesOrForeignFiles"/></UnitTest></TestDefinitions>
  <ResultSummary outcome="Completed"/>
</TestRun>
'@
    function Check([string]$Name, [string]$Runtime, [bool]$Pass) {
        $path = Join-Path $root 'Runtime.trx'
        if ($Runtime) { Set-Content -LiteralPath $path -Value $Runtime -Encoding utf8 }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        $accepted = $true
        try { & (Join-Path $repository 'scripts/verify-release-results.ps1') -ResultsDirectory $root | Out-Null }
        catch { $accepted = $false }
        if ($accepted -ne $Pass) { throw "Release gate regression: $Name" }
    }
    Set-Content -LiteralPath (Join-Path $root 'Editor.trx') -Value $editor -Encoding utf8
    Check 'complete passing run' $runtime $true
    Check 'missing report' '' $false
    Check 'empty run' ($runtime -replace '<UnitTestResult[^>]*/>', '') $false
    Check 'skipped/inconclusive run' ($runtime.Replace('outcome="Passed"', 'outcome="NotExecuted"')) $false
    Check 'failed run' ($runtime.Replace('outcome="Passed"', 'outcome="Failed"')) $false
    Check 'aborted run' ($runtime.Replace('outcome="Completed"', 'outcome="Aborted"')) $false
    Check 'missing data row' ($runtime.Replace('<UnitTestResult testId="b" outcome="Passed"/>', '')) $false
    Check 'wrong test inventory' ($runtime.Replace('Runtime_RendersSceneAndUsesSharedNextPreviousStateTransitions', 'OtherTest')) $false
    Check 'duplicate result hides missing row' ($runtime.Replace('testId="b" outcome="Passed"', 'testId="a" outcome="Passed"')) $false
    Write-Output 'Release gate regression cases passed.'
}
finally {
    $parent = [IO.Path]::GetFullPath((Join-Path $repository 'TestResults'))
    if ([IO.Path]::GetDirectoryName($root) -ne $parent -or [IO.Path]::GetFileName($root) -notlike 'release-gate-*') { throw 'Unexpected test fixture path.' }
    Remove-Item -LiteralPath $root -Recurse -Force
}
