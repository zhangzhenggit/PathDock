param([ValidateSet('Install','Remove')][string]$Mode,[string]$Root)
$ErrorActionPreference='Stop'
$path=$PROFILE.CurrentUserCurrentHost
$metaPath=Join-Path $Root 'integration.json'
$start='# >>> PathDock integration >>>'
$end='# <<< PathDock integration <<<'
if($Mode -eq 'Install'){
  $bytes=if([IO.File]::Exists($path)){[IO.File]::ReadAllBytes($path)}else{[byte[]]@()}
  $encoding=[Text.UTF8Encoding]::new($false)
  if($bytes.Length -ge 2 -and $bytes[0] -eq 255 -and $bytes[1] -eq 254){$encoding=[Text.Encoding]::Unicode}
  elseif($bytes.Length -ge 2 -and $bytes[0] -eq 254 -and $bytes[1] -eq 255){$encoding=[Text.Encoding]::BigEndianUnicode}
  $text=$encoding.GetString($bytes)
  if($text.Contains($start)){Write-Output 'Already installed';exit 0}
  [void][IO.Directory]::CreateDirectory($Root)
  $backup=Join-Path $Root ('profile-backup-'+[datetime]::Now.ToString('yyyyMMddHHmmssfff')+'.bin')
  [IO.File]::WriteAllBytes($backup,$bytes)
  $scriptPath=(Join-Path $PSScriptRoot 'PathDock.ps1').Replace("'","''")
  $block="`r`n$start`r`nif (Test-Path -LiteralPath '$scriptPath') { . '$scriptPath' }`r`n$end`r`n"
  [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
  $combined=[byte[]]($bytes+$encoding.GetBytes($block))
  $existed=[IO.File]::Exists($path)
  [IO.File]::WriteAllBytes($path,$combined)
  @{Profile=$path;Backup=$backup;Existed=$existed;InstalledHash=(Get-FileHash -LiteralPath $path).Hash;Block=$block;Encoding=$encoding.WebName}|ConvertTo-Json | Set-Content -LiteralPath $metaPath -Encoding utf8
  Write-Output 'Installed'
}else{
  if(-not [IO.File]::Exists($metaPath)){Write-Output 'Not installed';exit 0}
  $meta=Get-Content -LiteralPath $metaPath -Raw|ConvertFrom-Json
  if([IO.File]::Exists($meta.Profile)){
    if((Get-FileHash -LiteralPath $meta.Profile).Hash -eq $meta.InstalledHash){
      if($meta.Existed){[IO.File]::WriteAllBytes($meta.Profile,[IO.File]::ReadAllBytes($meta.Backup))}else{[IO.File]::Delete($meta.Profile)}
    }else{
      $encoding=[Text.Encoding]::GetEncoding($meta.Encoding)
      $bytes=[IO.File]::ReadAllBytes($meta.Profile)
      $text=$encoding.GetString($bytes)
      if(-not $text.Contains($meta.Block)){throw 'Profile changed and integration block no longer matches. Backup is preserved.'}
      [IO.File]::WriteAllBytes($meta.Profile,$encoding.GetBytes($text.Replace($meta.Block,'')))
    }
  }
  [IO.File]::Delete($metaPath)
  Write-Output 'Removed'
}
