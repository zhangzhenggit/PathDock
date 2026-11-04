$projectRoot=Split-Path $PSScriptRoot -Parent
$resultsRoot=Join-Path $projectRoot 'artifacts\test-results'
[void][IO.Directory]::CreateDirectory($resultsRoot)
$ErrorActionPreference='Stop'
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ('PathDock-IntegrationTest-'+[guid]::NewGuid().ToString('N'))
$results=[Collections.Generic.List[object]]::new()
$realProfilePath=$PROFILE.CurrentUserCurrentHost
$realProfileBefore=if(Test-Path -LiteralPath $realProfilePath){(Get-FileHash -LiteralPath $realProfilePath).Hash}else{'missing'}
$originalProfileValue=$PROFILE
$originalLocation=(Get-Location).ProviderPath
$originalHook=$ExecutionContext.InvokeCommand.LocationChangedAction
function Assert-Test([bool]$condition,[string]$message){if(-not $condition){throw $message}}
function Check-Test([string]$name,[scriptblock]$action){try{& $action;$results.Add(@{Name=$name;Passed=$true;Error=''})}catch{$results.Add(@{Name=$name;Passed=$false;Error=$_.Exception.Message})}}
try{
  [void][IO.Directory]::CreateDirectory($testRoot)
  foreach($case in @('utf8','utf8-bom','utf16')){
    Check-Test "Install/remove preserves $case profile bytes" {
      $directory=Join-Path $testRoot $case
      [void][IO.Directory]::CreateDirectory($directory)
      $fakeProfile=Join-Path $directory 'profile.ps1'
      $encoding=switch($case){'utf8'{[Text.UTF8Encoding]::new($false)}'utf8-bom'{[Text.UTF8Encoding]::new($true)}'utf16'{[Text.Encoding]::Unicode}}
      [IO.File]::WriteAllText($fakeProfile,"# 用户原配置`r`nfunction prompt { 'custom> ' }`r`n",$encoding)
      $before=[IO.File]::ReadAllBytes($fakeProfile)
      $PROFILE=[pscustomobject]@{CurrentUserCurrentHost=$fakeProfile}
      $null=& (Join-Path $projectRoot 'Integration\manage.ps1') -Mode Install -Root $directory
      Assert-Test ((Get-Content -LiteralPath $fakeProfile -Raw).Contains('# >>> PathDock integration >>>')) 'Install block missing'
      $null=& (Join-Path $projectRoot 'Integration\manage.ps1') -Mode Remove -Root $directory
      Assert-Test ([Convert]::ToBase64String($before) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($fakeProfile))) 'Profile bytes differ after removal'
    }
  }
  Check-Test 'Removal preserves edits made after installation' {
    $directory=Join-Path $testRoot 'edited';[void][IO.Directory]::CreateDirectory($directory)
    $fakeProfile=Join-Path $directory 'profile.ps1';[IO.File]::WriteAllText($fakeProfile,"# original`r`n")
    $PROFILE=[pscustomobject]@{CurrentUserCurrentHost=$fakeProfile}
    $null=& (Join-Path $projectRoot 'Integration\manage.ps1') -Mode Install -Root $directory
    [IO.File]::AppendAllText($fakeProfile,"# later edit`r`n")
    $null=& (Join-Path $projectRoot 'Integration\manage.ps1') -Mode Remove -Root $directory
    $text=[IO.File]::ReadAllText($fakeProfile)
    Assert-Test ($text.Contains('# original') -and $text.Contains('# later edit') -and -not $text.Contains('>>> PathDock')) 'User edits not preserved'
  }
  $PROFILE=$originalProfileValue
  Import-Module PSReadLine
  $readLineBefore=(Get-Command PSConsoleHostReadLine).ScriptBlock
  $enterBefore=(Get-PSReadLineKeyHandler -Chord Enter).Function
  $promptBefore=(Get-Command prompt).ScriptBlock.ToString()
  $activityRoot=Join-Path $testRoot 'runtime'
  . (Join-Path $projectRoot 'Integration\PathDock.ps1') -DataRoot $activityRoot
  Check-Test 'Integration preserves prompt and Enter binding' {
    Assert-Test ((Get-Command prompt).ScriptBlock.ToString() -ceq $promptBefore) 'Prompt changed'
    Assert-Test ((Get-PSReadLineKeyHandler -Chord Enter).Function -ceq $enterBefore) 'Enter binding changed'
    Assert-Test ($global:PathDockOriginalReadLine.ToString() -ceq $readLineBefore.ToString()) 'Original ReadLine implementation lost'
  }
  Check-Test 'Real location event writes exact Unicode directory' {
    $target=Join-Path $testRoot "中文 [项目] O'Brien"
    [void][IO.Directory]::CreateDirectory($target)
    Set-Location -LiteralPath $target
    $activityPath=Join-Path $activityRoot ('activity\'+$global:PathDockSession.Id+'.json')
    $activity=Get-Content -LiteralPath $activityPath -Raw|ConvertFrom-Json
    Assert-Test ($activity.Path -ceq $target) 'Activity path mismatch'
    Assert-Test ($activity.Source -eq 'PowerShell') 'Activity source missing'
  }
  Check-Test 'Session identity includes PID and process start time' {
    $session=Get-Content -LiteralPath (Join-Path $activityRoot ('sessions\'+$global:PathDockSession.Id+'.json')) -Raw|ConvertFrom-Json
    Assert-Test ($session.Pid -eq $PID -and $session.StartedTicks -eq [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks -and $session.State -eq 'Busy') 'Invalid session identity or false idle state'
  }
  Check-Test 'Session marker is unique and an invalid foreground target is rejected' {
    Assert-Test ($global:PathDockSession.TitleMarker -ceq ('[PD:'+$global:PathDockSession.Id.Substring(0,6)+']')) 'Missing session title marker'
    Assert-Test (-not [PathDockBridgeFocus]::Matches(0,'')) 'Invalid foreground target accepted'
  }
  Check-Test 'PowerShell event actions can change the shared runspace location' {
    $target=Join-Path $testRoot 'event-target';[void][IO.Directory]::CreateDirectory($target)
    $eventJob=Register-EngineEvent -SourceIdentifier PathDock.TestLocation -Action {Set-Location -LiteralPath $event.MessageData}
    try{
      $null=New-Event -SourceIdentifier PathDock.TestLocation -MessageData $target
      for($i=0;$i -lt 10 -and $PWD.Path -cne $target;$i++){Start-Sleep -Milliseconds 20}
      Assert-Test ($PWD.Path -ceq $target) 'Event action did not update shared cwd'
    }finally{Unregister-Event -SourceIdentifier PathDock.TestLocation;Remove-Job $eventJob -Force}
  }
  Check-Test 'Repeated source does not register duplicate idle handlers' {
    $before=@(Get-EventSubscriber -SourceIdentifier PowerShell.OnIdle).Count
    . (Join-Path $projectRoot 'Integration\PathDock.ps1') -DataRoot $activityRoot
    Assert-Test (@(Get-EventSubscriber -SourceIdentifier PowerShell.OnIdle).Count -eq $before) 'Duplicate event subscription'
  }
}finally{
  $PROFILE=$originalProfileValue
  $ExecutionContext.InvokeCommand.LocationChangedAction=$originalHook
  if($readLineBefore){Set-Item -Path Function:global:PSConsoleHostReadLine -Value $readLineBefore}
  Get-EventSubscriber -SourceIdentifier PowerShell.OnIdle -ErrorAction SilentlyContinue | Unregister-Event
  Set-Location -LiteralPath $originalLocation
  if(Test-Path -LiteralPath $testRoot){$resolved=[IO.Path]::GetFullPath($testRoot);if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved).StartsWith('PathDock-IntegrationTest-')){Remove-Item -LiteralPath $resolved -Recurse -Force}}
  Check-Test 'Real user profile remains unchanged' {
    $after=if(Test-Path -LiteralPath $realProfilePath){(Get-FileHash -LiteralPath $realProfilePath).Hash}else{'missing'}
    Assert-Test ($after -ceq $realProfileBefore) 'Actual user profile was modified'
  }
  @{Passed=@($results|Where-Object Passed).Count;Failed=@($results|Where-Object {-not $_.Passed}).Count;Tests=@($results.ToArray());InteractiveIdleLoopTested=$false}|ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $resultsRoot 'integration-test-results.json') -Encoding utf8
  Get-Content -LiteralPath (Join-Path $resultsRoot 'integration-test-results.json') -Raw
}
if(@($results|Where-Object {-not $_.Passed}).Count){exit 1}
