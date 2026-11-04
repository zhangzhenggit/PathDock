param([switch]$Run,[switch]$Package,[switch]$SelfContained,[ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64')
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$version=([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'PathDock.csproj') -Raw)).Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1
$flavor=if($SelfContained){'portable'}else{'framework-dependent'}
$output=Join-Path $projectRoot "artifacts\publish\$Runtime-$flavor"
$dist=Join-Path $projectRoot 'dist'
$backup=$null
function Copy-ReleaseFiles([string]$From,[string]$To) {
  [void][IO.Directory]::CreateDirectory($To)
  foreach($entry in Get-ChildItem -LiteralPath $From){
    for($attempt=0;;$attempt++){
      try{Copy-Item -LiteralPath $entry.FullName -Destination $To -Recurse -Force -ErrorAction Stop;break}
      catch{if($attempt -ge 19){throw};Start-Sleep -Milliseconds 250}
    }
  }
}
Push-Location $projectRoot
try {
  # Always publish to a fresh staging directory; do not touch the running app.
  if(Test-Path -LiteralPath $output){
    $resolved=[IO.Path]::GetFullPath($output)
    if(-not $resolved.StartsWith((Join-Path $projectRoot 'artifacts\publish\'),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe staging directory.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
  }
  $publishArgs=@('publish','PathDock.csproj','-c','Release','-r',$Runtime,'--self-contained',([bool]$SelfContained).ToString().ToLowerInvariant(),'-p:PathDockDiagnostics=false','-p:DebugType=None','-p:DebugSymbols=false','-o',$output,'--nologo')
  & dotnet @publishArgs
  if($LASTEXITCODE -ne 0){throw 'PathDock build failed.'}
  $metadata=[Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $output 'PathDock.dll')))
  if($metadata.Contains('DiagnosticRunner') -or $metadata.Contains('CheckMotion') -or $metadata.Contains('SelfTest')){throw 'Diagnostics leaked into release output.'}
  Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\USAGE.md') -Destination (Join-Path $output 'README.md')
  if($SelfContained){
    $cache=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'}
    foreach($runtimePackageName in @("microsoft.netcore.app.runtime.$Runtime","microsoft.windowsdesktop.app.runtime.$Runtime")){
      $directory=Get-ChildItem (Join-Path $cache $runtimePackageName) -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
      if($directory){
        foreach($notice in Get-ChildItem $directory.FullName -File | Where-Object {$_.Name -in @('LICENSE','LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')}){
          $licenses=Join-Path $output 'licenses';[void][IO.Directory]::CreateDirectory($licenses)
          Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenses ($runtimePackageName+'-'+$notice.Name))
        }
      }
    }
  }
  if($Package){
    $release=Join-Path $projectRoot 'releases'
    [void][IO.Directory]::CreateDirectory($release)
    $zip=Join-Path $release "PathDock-$version-$Runtime-$flavor.zip"
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -Force
    $hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip+'.sha256',"$hash  $([IO.Path]::GetFileName($zip))"+[Environment]::NewLine)
    Write-Output "Package: $zip"
  }
  if($Run){
    $exe=Join-Path $dist 'PathDock.exe'
    $running=@(Get-Process PathDock -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $exe})
    foreach($activeProcess in $running){
      Stop-Process -Id $activeProcess.Id -ErrorAction SilentlyContinue
      if(-not $activeProcess.WaitForExit(5000)){throw 'PathDock did not exit in time; running files were not replaced.'}
    }
    if(Test-Path -LiteralPath $dist){
      $backup=Join-Path $projectRoot ('artifacts\backup\dist-'+[datetime]::Now.ToString('yyyyMMdd-HHmmssfff'))
      [void][IO.Directory]::CreateDirectory((Split-Path $backup -Parent))
      Copy-Item -LiteralPath $dist -Destination $backup -Recurse
    }
    try {
      Copy-ReleaseFiles $output $dist
      $oldSymbols=Join-Path $dist 'PathDock.pdb'
      if(Test-Path -LiteralPath $oldSymbols){Remove-Item -LiteralPath $oldSymbols -Force}
      $process=Start-Process -FilePath $exe -ArgumentList '--startup' -WorkingDirectory $projectRoot -WindowStyle Normal -PassThru
      if($process.WaitForExit(1500)){throw "PathDock exited during startup ($($process.ExitCode))."}
      Write-Output "Running: $exe (PID $($process.Id))"
    }catch{
      if($backup){Copy-ReleaseFiles $backup $dist;Start-Process -FilePath $exe -ArgumentList '--startup' -WorkingDirectory $projectRoot -WindowStyle Normal}
      throw
    }
  }
} finally {Pop-Location}
