$projectRoot=Split-Path $PSScriptRoot -Parent
$resultsRoot=Join-Path $projectRoot 'artifacts\test-results'
[void][IO.Directory]::CreateDirectory($resultsRoot)
$ErrorActionPreference='Stop'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('PathDock-RequestTest-'+[guid]::NewGuid().ToString('N'))
$originalPath=$PWD.Path
try{
  [void][IO.Directory]::CreateDirectory($testRoot)
  . (Join-Path $projectRoot 'Integration\PathDock.ps1') -DataRoot $testRoot
  $destination=Join-Path $testRoot 'destination'
  [void][IO.Directory]::CreateDirectory($destination)
  $results=[Collections.Generic.List[object]]::new()
  foreach($case in @('success-refresh-fails','invalid-directory','expired','wrong-generation','wrong-session','focus-changed','new-timeout-window')){
    $id=[guid]::NewGuid().ToString('N')
    $before=$PWD.Path
    $target=Join-Path $testRoot ('directory-'+$case)
    if($case -ne 'invalid-directory'){[void][IO.Directory]::CreateDirectory($target)}
    $request=@{Id=$id;Session=$global:PathDockSession.Id;Generation=$global:PathDockSession.Generation;Path=$target;ExpiresUtc=[datetime]::UtcNow.AddSeconds(1).ToString('o');RequireForeground=$false}
    $expected=switch($case){
      'invalid-directory'{'NavigationFailed'}
      'expired'{$request.ExpiresUtc=[datetime]::UtcNow.AddSeconds(-1).ToString('o');'Expired'}
      'wrong-generation'{$request.Generation++;'GenerationChanged'}
      'wrong-session'{$request.Session='another';'WrongSession'}
      'focus-changed'{$request.RequireForeground=$true;$request.TargetWindow=0L;$request.TargetTitle='';'FocusChanged'}
      'new-timeout-window'{$request.ExpiresUtc=[datetime]::UtcNow.AddMilliseconds(2500).ToString('o');'Applied'}
      default{'Applied'}
    }
    [IO.File]::WriteAllText((Join-Path $testRoot ('requests\'+$global:PathDockSession.Id+'.json')),($request|ConvertTo-Json -Compress))
    $null=New-Event -SourceIdentifier PowerShell.OnIdle
    Start-Sleep -Milliseconds 100
    $response=Get-Content -LiteralPath (Join-Path $testRoot ('replies\'+$id+'.json')) -Raw | ConvertFrom-Json
    $passed=$response.Code -eq $expected
    if($expected -eq 'Applied'){$passed=$passed -and $PWD.Path -ceq $target -and $response.Result -eq 'Applied'}
    else{$passed=$passed -and $PWD.Path -ceq $before -and $response.Result -eq 'Rejected'}
    $results.Add(@{Case=$case;Expected=$expected;Actual=$response.Code;Passed=[bool]$passed;Warning=$response.Warning})
  }
  $report=@{Passed=@($results|Where-Object Passed).Count;Failed=@($results|Where-Object {-not $_.Passed}).Count;Tests=$results.ToArray();ActualIdleHandlerUsed=$true;InteractiveConsoleUsed=$false}
  $report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $resultsRoot 'bridge-request-test-results.json') -Encoding utf8
  $report|ConvertTo-Json -Depth 5
  if($report.Failed){throw 'Bridge request regression failed'}
}finally{
  $ExecutionContext.InvokeCommand.LocationChangedAction=$global:PathDockPreviousLocationHook
  Get-EventSubscriber -SourceIdentifier PowerShell.OnIdle -ErrorAction SilentlyContinue|Unregister-Event
  Set-Location -LiteralPath $originalPath
  if([IO.Path]::GetFullPath($testRoot).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($testRoot).StartsWith('PathDock-RequestTest-')){Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue}
}
