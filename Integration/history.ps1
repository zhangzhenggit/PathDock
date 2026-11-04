$ErrorActionPreference='Stop'
Import-Module PSReadLine
$path=(Get-PSReadLineOption).HistorySavePath
$found=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if(Test-Path -LiteralPath $path){
  foreach($line in [IO.File]::ReadLines($path)){
    if($line -notmatch '(?i)\b(cd|chdir|sl|set-location|push-location|pushd)\b'){continue}
    $tokens=$null;$errors=$null
    $ast=[System.Management.Automation.Language.Parser]::ParseInput($line,[ref]$tokens,[ref]$errors)
    foreach($command in $ast.FindAll({param($n) $n -is [System.Management.Automation.Language.CommandAst]},$true)){
      if($command.GetCommandName() -notin @('cd','chdir','sl','Set-Location','Push-Location','pushd')){continue}
      $arguments=@($command.CommandElements | Select-Object -Skip 1 | Where-Object {$_ -isnot [System.Management.Automation.Language.CommandParameterAst]})
      if($arguments.Count -ne 1 -or $arguments[0] -isnot [System.Management.Automation.Language.StringConstantExpressionAst]){continue}
      $value=$arguments[0].Value
      if([IO.Path]::IsPathFullyQualified($value) -and -not $value.StartsWith('\\') -and [IO.Directory]::Exists($value)){
        [void]$found.Add([IO.Path]::GetFullPath($value))
      }
    }
  }
}
ConvertTo-Json -InputObject @($found) -Compress
