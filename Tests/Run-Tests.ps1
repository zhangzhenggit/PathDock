param([switch]$SkipMotion)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$output=Join-Path $projectRoot 'artifacts\diagnostics'
$results=Join-Path $projectRoot 'artifacts\test-results'
$reports=@('self-test-results.json','integration-test-results.json','bridge-request-test-results.json')
[void][IO.Directory]::CreateDirectory($results)
Push-Location $projectRoot
try {
  dotnet publish .\PathDock.csproj -c Release -p:PathDockDiagnostics=true --self-contained false -o $output --nologo
  if($LASTEXITCODE -ne 0){throw 'Diagnostic build failed.'}
  $exe=Join-Path $output 'PathDock.exe'
  $process=Start-Process -FilePath $exe -ArgumentList @('--self-test',('"'+(Join-Path $results 'self-test-results.json')+'"')) -Wait -PassThru -WindowStyle Hidden
  if($process.ExitCode -ne 0){throw 'Core tests failed.'}
  foreach($script in @('Test-Integration.ps1','Test-BridgeRequest.ps1')){
    & pwsh -NoLogo -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot $script)
    if($LASTEXITCODE -ne 0){throw "$script failed."}
  }
  if(-not $SkipMotion){
    $reports+=@('tail-test-results.json','motion-test-results.json')
    $process=Start-Process -FilePath $exe -ArgumentList @('--tail-test',('"'+(Join-Path $results 'tail-test-results.json')+'"')) -Wait -PassThru -WindowStyle Normal
    if($process.ExitCode -ne 0){throw 'Tail rendering tests failed.'}
    $process=Start-Process -FilePath $exe -ArgumentList @('--motion-test',('"'+(Join-Path $results 'motion-test-results.json')+'"')) -Wait -PassThru -WindowStyle Normal
    if($process.ExitCode -ne 0){throw 'Motion tests failed.'}
    $report=Get-Content (Join-Path $results 'motion-test-results.json') -Raw|ConvertFrom-Json
    if(-not $report.Passed){throw 'Motion assertions failed.'}
  }
  foreach($name in $reports){$r=Get-Content (Join-Path $results $name) -Raw|ConvertFrom-Json;[pscustomobject]@{Report=$name;Passed=$r.Passed;Failed=$r.Failed}}
}finally{Pop-Location}
