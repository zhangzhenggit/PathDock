param([string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'PathDock'))
# Optional session-local PathDock integration. No changes to prompt or Enter bindings.
if($global:PathDockSession){return}
$global:PathDockRoot=$DataRoot
foreach($directory in @('sessions','requests','replies','activity')){[void][IO.Directory]::CreateDirectory((Join-Path $global:PathDockRoot $directory))}
$global:PathDockSession=[ordered]@{Id=[guid]::NewGuid().ToString('N');Pid=$PID;StartedTicks=[Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks;Generation=0L;Cwd=$PWD.Path;State='Busy';UpdatedUtc=[datetime]::UtcNow.ToString('o')}
$global:PathDockSession.TitleMarker='[PD:'+$global:PathDockSession.Id.Substring(0,6)+']'
$global:PathDockSession.BridgeVersion=3
if(-not ('PathDockBridgeFocus' -as [type])){
  Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PathDockBridgeFocus {
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr w, StringBuilder b, int n);
  public static bool Matches(long window, string title) {
    if(window==0) return false;
    var active=GetForegroundWindow();
    if(active.ToInt64()!=window) return false;
    var text=new StringBuilder(1024);GetWindowText(active,text,text.Capacity);
    return text.ToString()==title;
  }
}
'@ -ErrorAction SilentlyContinue
}
function global:Write-PathDockSession([string]$State){
  try{
    try{
      $title=$Host.UI.RawUI.WindowTitle
      $marker=$global:PathDockSession.TitleMarker
      if($State -eq 'Idle' -and -not $title.Contains($marker)){$Host.UI.RawUI.WindowTitle=$title+' '+$marker}
      elseif($State -ne 'Idle' -and $title.Contains($marker)){$Host.UI.RawUI.WindowTitle=$title.Replace(' '+$marker,'').Replace($marker,'')}
    }catch{}
    $global:PathDockSession.State=$State
    $global:PathDockSession.Cwd=$PWD.Path
    $global:PathDockSession.UpdatedUtc=[datetime]::UtcNow.ToString('o')
    $path=Join-Path $global:PathDockRoot ('sessions\'+$global:PathDockSession.Id+'.json')
    [IO.File]::WriteAllText($path+'.tmp',($global:PathDockSession|ConvertTo-Json -Compress))
    [IO.File]::Move($path+'.tmp',$path,$true)
  }catch{}
}
$global:PathDockPreviousLocationHook=$ExecutionContext.InvokeCommand.LocationChangedAction
$global:PathDockLocationHook=[EventHandler[System.Management.Automation.LocationChangedEventArgs]]{
  param($sender,$eventArgs)
  if($eventArgs.NewPath.Provider.Name -ne 'FileSystem'){return}
  try{
    $path=Join-Path $global:PathDockRoot ('activity\'+$global:PathDockSession.Id+'.json')
    $item=@{Path=$eventArgs.NewPath.Path;Utc=[datetime]::UtcNow.ToString('o');Source='PowerShell'}
    [IO.File]::WriteAllText($path+'.tmp',($item|ConvertTo-Json -Compress));[IO.File]::Move($path+'.tmp',$path,$true)
  }catch{}
}
$ExecutionContext.InvokeCommand.LocationChangedAction=[Delegate]::Combine($global:PathDockPreviousLocationHook,$global:PathDockLocationHook)
Import-Module PSReadLine -ErrorAction SilentlyContinue
$original=Get-Command PSConsoleHostReadLine -CommandType Function -ErrorAction SilentlyContinue
if($original){
  $global:PathDockOriginalReadLine=$original.ScriptBlock
  function global:PSConsoleHostReadLine {
    $global:PathDockSession.Generation++
    try{ & $global:PathDockOriginalReadLine }
    finally{
      $global:PathDockSession.Generation++
      Write-PathDockSession 'Busy'
      $request=Join-Path $global:PathDockRoot ('requests\'+$global:PathDockSession.Id+'.json')
      try{[IO.File]::Delete($request)}catch{}
    }
  }
  $null=Register-EngineEvent -SourceIdentifier PowerShell.OnIdle -Action {
    $line=$null;$cursor=0
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line,[ref]$cursor)
    if($line.Length -ne 0){return}
    Write-PathDockSession 'Idle'
    $path=Join-Path $global:PathDockRoot ('requests\'+$global:PathDockSession.Id+'.json')
    if(-not [IO.File]::Exists($path)){return}
    $request=$null
    try{
      $request=[IO.File]::ReadAllText($path)|ConvertFrom-Json
      [IO.File]::Delete($path)
      $result='Rejected';$code='InvalidRequest';$message='Invalid request';$warning=$null
      $focusMatches=(-not $request.RequireForeground) -or (('PathDockBridgeFocus' -as [type]) -and [PathDockBridgeFocus]::Matches($request.TargetWindow,$request.TargetTitle))
      $expires=if($request.ExpiresUtc -is [datetime]){[DateTimeOffset]::new($request.ExpiresUtc.ToUniversalTime())}else{[DateTimeOffset]::Parse([string]$request.ExpiresUtc,[Globalization.CultureInfo]::InvariantCulture)}
      if($request.Id -notmatch '^[a-f0-9]{32}$'){$code='InvalidRequest'}
      elseif($request.Session -cne $global:PathDockSession.Id){$code='WrongSession';$message='Session no longer matches'}
      elseif($request.Generation -ne $global:PathDockSession.Generation){$code='GenerationChanged';$message='Command input state has changed'}
      elseif([DateTimeOffset]::UtcNow -ge $expires -or ($expires-[DateTimeOffset]::UtcNow).TotalSeconds -gt 3){$code='Expired';$message='Request expired'}
      elseif(-not $focusMatches){$code='FocusChanged';$message='Foreground window or tab has changed'}
      elseif(-not [IO.Path]::IsPathFullyQualified($request.Path) -or $request.Path -match '[\r\n\x00]'){$code='InvalidPath';$message='Invalid directory path'}
      else{
        try{
          Set-Location -LiteralPath $request.Path -ErrorAction Stop
          # A repaint failure cannot undo a successful Set-Location.
          $result='Applied';$code='Applied';$message=''
        }catch{$code='NavigationFailed';$message=$_.Exception.Message}
        if($result -eq 'Applied'){
          Write-PathDockSession 'Idle'
          # A numeric second argument is a screen row, not a redraw flag. Passing
          # zero bypasses PSReadLine's old-prompt clearing and causes overlap.
          try{[Microsoft.PowerShell.PSConsoleReadLine]::InvokePrompt($null,$null)}
          catch{$warning='PromptRefreshFailed'}
        }
      }
      if($request.Id -match '^[a-f0-9]{32}$'){
        $reply=Join-Path $global:PathDockRoot ('replies\'+$request.Id+'.json')
        [IO.File]::WriteAllText($reply+'.tmp',(@{Result=$result;Code=$code;Message=$message;Warning=$warning;Path=$PWD.Path}|ConvertTo-Json -Compress));[IO.File]::Move($reply+'.tmp',$reply,$true)
        if($result -ne 'Applied' -or $warning){
          try{[IO.File]::AppendAllText((Join-Path $global:PathDockRoot 'bridge-events.jsonl'),((@{Utc=[datetime]::UtcNow.ToString('o');RequestId=$request.Id;Code=$code;Warning=$warning;Message=$message}|ConvertTo-Json -Compress)+[Environment]::NewLine))}catch{}
        }
      }
    }catch{}
  }
}
Write-PathDockSession 'Busy'
