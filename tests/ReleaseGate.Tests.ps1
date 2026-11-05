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
  <Results><UnitTestResult testId="c" outcome="Passed"/>
    <UnitTestResult testId="r1" outcome="Passed"/><UnitTestResult testId="r2" outcome="Passed"/>
    <UnitTestResult testId="a1" outcome="Passed"/><UnitTestResult testId="a2" outcome="Passed"/>
    <UnitTestResult testId="a3" outcome="Passed"/><UnitTestResult testId="a4" outcome="Passed"/>
    <UnitTestResult testId="v1" outcome="Passed"/><UnitTestResult testId="v2" outcome="Passed"/>
  </Results>
  <TestDefinitions><UnitTest id="c"><TestMethod name="NativeInstallerInstallsRunsGameAndUninstallsWithoutRemovingSavesOrForeignFiles"/></UnitTest>
    <UnitTest id="r1"><TestMethod name="ReadingControlsHistoryVoiceAndOldSlotWorkInPreviewAndExport"/></UnitTest>
    <UnitTest id="r2"><TestMethod name="ReadingControlsHistoryVoiceAndOldSlotWorkInPreviewAndExport"/></UnitTest>
    <UnitTest id="a1"><TestMethod name="LayeredAndFlipbookRenderAndRestoreInPreviewAndSelfContainedGame"/></UnitTest>
    <UnitTest id="a2"><TestMethod name="LayeredAndFlipbookRenderAndRestoreInPreviewAndSelfContainedGame"/></UnitTest>
    <UnitTest id="a3"><TestMethod name="LayeredAndFlipbookRenderAndRestoreInPreviewAndSelfContainedGame"/></UnitTest>
    <UnitTest id="a4"><TestMethod name="LayeredAndFlipbookRenderAndRestoreInPreviewAndSelfContainedGame"/></UnitTest>
    <UnitTest id="v1"><TestMethod name="VoiceCursorPauseResumeCompletionAndNavigationUseNativePlayback"/></UnitTest>
    <UnitTest id="v2"><TestMethod name="VoiceCursorPauseResumeCompletionAndNavigationUseNativePlayback"/></UnitTest>
  </TestDefinitions>
  <ResultSummary outcome="Completed"/>
</TestRun>
'@
    function Check([string]$Name, [string]$Runtime, [bool]$Pass, [string]$Editor = $editor,
                   [string]$Profile = 'Delivery', [string]$Core = '') {
        $path = Join-Path $root 'Runtime.trx'
        if ($Runtime) { Set-Content -LiteralPath $path -Value $Runtime -Encoding utf8 }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        $path = Join-Path $root 'Editor.trx'
        if ($Editor) { Set-Content -LiteralPath $path -Value $Editor -Encoding utf8 }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        $path = Join-Path $root 'Core.trx'
        if ($Core) { Set-Content -LiteralPath $path -Value $Core -Encoding utf8 }
        elseif (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path }
        $accepted = $true
        try { & (Join-Path $repository 'scripts/verify-release-results.ps1') -ResultsDirectory $root -Profile $Profile | Out-Null }
        catch { $accepted = $false }
        if ($accepted -ne $Pass) { throw "Release gate regression: $Name" }
    }
    Check 'complete passing run' $runtime $true
    Check 'missing report' '' $false
    Check 'empty run' ($runtime -replace '<UnitTestResult[^>]*/>', '') $false
    Check 'skipped/inconclusive run' ($runtime.Replace('outcome="Passed"', 'outcome="NotExecuted"')) $false
    Check 'failed run' ($runtime.Replace('outcome="Passed"', 'outcome="Failed"')) $false
    Check 'aborted run' ($runtime.Replace('outcome="Completed"', 'outcome="Aborted"')) $false
    Check 'missing data row' ($runtime.Replace('<UnitTestResult testId="b" outcome="Passed"/>', '')) $false
    Check 'wrong test inventory' ($runtime.Replace('Runtime_RendersSceneAndUsesSharedNextPreviousStateTransitions', 'OtherTest')) $false
    Check 'duplicate result hides missing row' ($runtime.Replace('testId="b" outcome="Passed"', 'testId="a" outcome="Passed"')) $false
    Check 'missing editor report' $runtime $false ''
    foreach ($id in @('r1', 'r2', 'a1', 'a2', 'a3', 'a4', 'v1', 'v2')) {
        $result = "<UnitTestResult testId=`"$id`" outcome=`"Passed`"/>"
        Check "missing feature row $id" $runtime $false ($editor.Replace($result, ''))
        Check "skipped feature row $id" $runtime $false ($editor.Replace($result, $result.Replace('Passed', 'NotExecuted')))
        Check "failed feature row $id" $runtime $false ($editor.Replace($result, $result.Replace('Passed', 'Failed')))
        Check "duplicate feature row $id" $runtime $false ($editor.Replace($result, $result + $result))
    }
    foreach ($method in @('ReadingControlsHistoryVoiceAndOldSlotWorkInPreviewAndExport',
        'LayeredAndFlipbookRenderAndRestoreInPreviewAndSelfContainedGame', 'VoiceCursorPauseResumeCompletionAndNavigationUseNativePlayback')) {
        Check "missing feature definitions $method" $runtime $false ($editor.Replace($method, 'OtherTest'))
    }
    $portableRuntime = $runtime.Replace('</Results>', '<UnitTestResult testId="s1" outcome="Passed"/><UnitTestResult testId="s2" outcome="Passed"/></Results>').Replace(
        '</TestDefinitions>', '<UnitTest id="s1"><TestMethod name="NativeScreenStackPausesTimerMovementAndEveryAudioGroupWithoutReplayingCommands"/></UnitTest><UnitTest id="s2"><TestMethod name="NativeScreenStackPausesTimerMovementAndEveryAudioGroupWithoutReplayingCommands"/></UnitTest></TestDefinitions>')
    $core = @'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult testId="w1" outcome="Passed"/><UnitTestResult testId="w2" outcome="Passed"/></Results>
  <TestDefinitions>
    <UnitTest id="w1"><TestMethod name="RealWorkerHandlesAsyncSyntaxRequestAndCancellationKillsActualChild"/></UnitTest>
    <UnitTest id="w2"><TestMethod name="PublishedWorkerExecutesCommandsAndRecoversAfterInstructionBudgetFailure"/></UnitTest>
  </TestDefinitions><ResultSummary outcome="Completed"/>
</TestRun>
'@
    Check 'passing native portability' $portableRuntime $true '' 'NativePortability' $core
    Check 'missing portability report' $portableRuntime $false '' 'NativePortability'
    Check 'behavior run cannot replace native portability' $runtime $false '' 'NativePortability' $core
    Check 'skipped native UI worker' ($portableRuntime.Replace('testId="s2" outcome="Passed"', 'testId="s2" outcome="NotExecuted"')) $false '' 'NativePortability' $core
    Check 'missing published worker budget case' $portableRuntime $false '' 'NativePortability' ($core.Replace('<UnitTestResult testId="w2" outcome="Passed"/>', ''))
    Write-Output 'Release gate regression cases passed.'
}
finally {
    $parent = [IO.Path]::GetFullPath((Join-Path $repository 'TestResults'))
    if ([IO.Path]::GetDirectoryName($root) -ne $parent -or [IO.Path]::GetFileName($root) -notlike 'release-gate-*') { throw 'Unexpected test fixture path.' }
    Remove-Item -LiteralPath $root -Recurse -Force
}
