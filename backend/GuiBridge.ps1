param([Parameter(Mandatory=$true)][string]$InputPath,[Parameter(Mandatory=$true)][string]$OutputPath,[string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'UnifiedNetworkRouter'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Router.ps1') -Library -DataRoot $DataRoot
function EnterGuiStateLock {
 $hash=[Security.Cryptography.SHA256]::Create()
 try{$key=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($script:dataRoot).TrimEnd('\').ToUpperInvariant())))).Replace('-','')}finally{$hash.Dispose()}
 $mutex=New-Object Threading.Mutex($false,('Global\UnifiedNetworkRouterState.'+$key))
 $owned=$false
 try{
  try{$owned=$mutex.WaitOne([TimeSpan]::FromSeconds(45))}catch [Threading.AbandonedMutexException]{$owned=$true}
  if(-not $owned){throw '另一個視窗正在保存或恢復網路，請稍後重新整理再操作。'}
  return $mutex
 }catch{$mutex.Dispose();throw}
}
function ExitGuiStateLock($Mutex) {if($Mutex){try{$Mutex.ReleaseMutex()}finally{$Mutex.Dispose()}}}
function GetGuiState {
 InitializeRouterFolder
 $state=ReadRouterState
 if(-not $state){
  $initializationLock=$null
  try{
   if(-not $script:guiMutationLockHeld){$initializationLock=EnterGuiStateLock}
   $state=ReadRouterState
   if(-not $state){$state=NewRouterState;SaveRouterState $state}
  }finally{ExitGuiStateLock $initializationLock}
 }
 return $state
}
function GetGuiResponse($State,[string]$Message='') {
 $status=$null
 try { if(Test-Path -LiteralPath $script:statusPath){$status=ReadSharedText $script:statusPath | ConvertFrom-Json} } catch {}
 $snapshot=@(GetAdapterSnapshot)
 SetStateNetworkCatalog $State
 $catalog=[object[]]@();if($State -and $State.NetworkCatalog){$catalog=[object[]]@($State.NetworkCatalog)}
 return @{Ok=$true;Message=$Message;State=$State;Snapshot=$snapshot;Status=$status;NetworkCatalog=$catalog;AppHealth=@(GetRouterAppHealth $State)}
}
function ApplyGuiState($State,[switch]$Enable) {
 $State.Revision=[int]$State.Revision+1
 if($Enable -and -not @($State.Rules | Where-Object {$_.Enabled}).Count){throw '請先新增規則，再啟用限制。'}
 if($State.Enabled -and -not @($State.Rules | Where-Object {$_.Enabled}).Count){SaveRouterState $State;StopRouterService;$stopped=ReadRouterState;$State.Enabled=$false;$State.Revision=[int]$stopped.Revision;return}
 if($Enable -or $State.Enabled){
  $built=BuildRouterConfig $State @(GetAdapterSnapshot)
  CheckRouterConfig $built.Text
  RegisterRouterStartup $State
  $State.Enabled=$true
  SaveRouterState $State
  Start-ScheduledTask -TaskName $script:taskName
  [void](WaitRouterRevision ([int]$State.Revision))
 } else { SaveRouterState $State }
}
function SetGuiRule($State,$Request) {
 [void]@(GetAdapterSnapshot);SetStateNetworkCatalog $State
 $app=$State.Apps | Where-Object {[int]$_.Id -eq [int]$Request.Id} | Select-Object -First 1
 if(-not $app){throw '所選程式已不存在，請重新整理清單。'}
 $nets=[int[]]@($Request.Networks)
 if($Request.Mode -eq 'Deny' -and -not $nets.Count){throw '請至少勾選一個要封鎖的網路，或按「關閉此限制」移除規則。'}
 if(@($nets | Select-Object -Unique).Count -ne $nets.Count){throw '網路選項重複。'}
 $oldRule=$State.Rules | Where-Object {[int]$_.Id -eq [int]$app.Id} | Select-Object -First 1
 $rule=NewRouterRule ([int]$app.Id) ([string]$Request.Mode) $nets
 $rule|Add-Member -NotePropertyName Enabled -NotePropertyValue $(if($oldRule){[bool]$oldRule.Enabled}else{$true})
 $State.Rules=@($State.Rules | Where-Object {[int]$_.Id -ne [int]$app.Id})+@($rule)
 AssertRouterState $State
}
$stateMutex=$null
$script:guiMutationLockHeld=$false
try {
 $request=Get-Content -LiteralPath $InputPath -Raw -Encoding UTF8 | ConvertFrom-Json
 $action=[string]$request.Action
 $reply=$null
 if($action -in @('Restore','Reset','Save','Apply','Enable','RemoveRule','Delete','Add','SetRuleEnabled','SaveOptions','RebindApplication')){
  $stateMutex=EnterGuiStateLock
  $script:guiMutationLockHeld=$true
 }
 if($action -eq 'Restore') {
  StopRouterService
  try { $state=ReadRouterState; $reply=GetGuiResponse $state '程式分流已停止，保存的規則與 計費有線額外保護仍保留。' }
  catch { $reply=@{Ok=$true;Message='程式分流已停止，計費有線額外保護仍保留；設定檔損壞，請使用「重設全部規則」修復。'} }
 } elseif($action -eq 'Reset') {
  StopRouterService -DeleteRules
  try { $state=ReadRouterState } catch {
   if(Test-Path -LiteralPath $script:statePath){Copy-Item -LiteralPath $script:statePath -Destination ($script:statePath+'.damaged.'+[Guid]::NewGuid().ToString('N')+'.bak')}
   $state=$null
  }
  if(-not $state){$state=NewRouterState -SkipLegacy}
  $state.Enabled=$false; $state.Rules=@(); $state.Revision=[int]$state.Revision+1
  SaveRouterState $state
  $reply=GetGuiResponse $state '全部規則已刪除，程式分流已停止；計費有線額外保護仍保留。'
 } elseif($action -eq 'Applications') {
  . (Join-Path $PSScriptRoot 'AppPicker.ps1')
  $reply=@{Ok=$true;Applications=@(GetPickerApplications)}
 } elseif($action -eq 'Logs') {
  $text=New-Object 'Collections.Generic.List[string]'
  try{
   if(Test-Path -LiteralPath $script:statusPath){
    $status=ReadSharedText $script:statusPath | ConvertFrom-Json
    $text.Add('=== 分流器狀態 ===')
    foreach($field in @('Phase','Message','Time')){$text.Add($field+' '+[string]$status.$field)}
   }
  }catch{$text.Add('無法讀取分流器狀態 '+$_.Exception.Message)}
  foreach($name in @('gui-errors.log','gui-startup-errors.log','service-errors.txt','core-errors.log','core-output.log','core-stderr.log','core-stdout.log','錯誤記錄.txt','執行記錄.txt')) {
   $file=Join-Path $DataRoot $name
   try{if(Test-Path -LiteralPath $file){$text.Add('=== '+$name+' ===');$text.Add((Get-Content -LiteralPath $file -Tail 100 -Encoding UTF8) -join [Environment]::NewLine)}}catch{$text.Add('無法讀取 '+$name+' '+$_.Exception.Message)}
  }
  $reply=@{Ok=$true;Message=($text -join [Environment]::NewLine)}
 } else {
  $state=GetGuiState
  if($action -notin @('Read','Check') -and [int]$request.Revision -ne [int]$state.Revision){throw '設定已在其他視窗變更，請重新整理後再套用。'}
  switch($action) {
   'Read' { $reply=GetGuiResponse $state }
   'Save' { SetGuiRule $state $request;ApplyGuiState $state;$message=if($state.Enabled){'規則已保存，正在使用的限制已更新。'}else{'規則已保存，目前尚未啟用限制。'};$reply=GetGuiResponse $state $message }
   'Apply' { SetGuiRule $state $request;($state.Rules | Where-Object {[int]$_.Id -eq [int]$request.Id} | Select-Object -First 1).Enabled=$true;ApplyGuiState $state -Enable;$reply=GetGuiResponse $state '限制已啟用，正常開啟程式即可。' }
   'Enable' { ApplyGuiState $state -Enable;$reply=GetGuiResponse $state '已啟用目前開啟的規則。' }
   'SaveOptions' {
    AssertRouterOptions $request.Options
    $candidate=GetRouterOptions ([pscustomobject]@{Options=$request.Options})
    $state.Options=$candidate;AssertRouterState $state
    $snapshot=GetRouterTrayStartupSnapshot
    $taskMatches=if($candidate.TrayAutoStart){$snapshot -and [string]$snapshot.State -ne 'Disabled'}else{-not $snapshot}
    $taskChanged=$false;$originalRevision=[int]$state.Revision
    try{
     if(-not $taskMatches){$taskChanged=$true;SetRouterTrayStartup ([bool]$candidate.TrayAutoStart)}
     ApplyGuiState $state
    }catch{
     $failure=$_;$rollbackMessage='';$saved=$false;$readKnown=$true
     try{$current=ReadRouterState;$saved=($current -and [int]$current.Revision -gt $originalRevision -and [bool]$current.Options.TrayAutoStart -eq [bool]$candidate.TrayAutoStart)}catch{$readKnown=$false;$rollbackMessage='；無法確認設定是否已保存：'+$_.Exception.Message}
     if($taskChanged -and $readKnown -and -not $saved){try{RestoreRouterTrayStartup $snapshot}catch{$rollbackMessage+='；恢復通知區排程失敗：'+$_.Exception.Message}}
     throw ($failure.Exception.Message+$rollbackMessage)
    }
    $reply=GetGuiResponse $state '設定已保存。'
   }
   'RebindApplication' {
    . (Join-Path $PSScriptRoot 'AppPicker.ps1')
    SetRouterApplicationPath $state ([int]$request.Id) ([string]$request.Path)
    ApplyGuiState $state
    $reply=GetGuiResponse $state '執行檔位置已更新，原有規則與啟用狀態保留。'
   }
   'SetRuleEnabled' {
    if($request.Enabled -isnot [bool]){throw '規則啟用狀態必須為布林值。'}
    $rule=$state.Rules | Where-Object {[int]$_.Id -eq [int]$request.Id} | Select-Object -First 1
    if(-not $rule){throw '找不到所選規則。'}
    $rule.Enabled=$request.Enabled;ApplyGuiState $state;$reply=GetGuiResponse $state '規則啟用狀態已更新。'
   }
   'RemoveRule' { $state.Rules=@($state.Rules | Where-Object {[int]$_.Id -ne [int]$request.Id});ApplyGuiState $state;$reply=GetGuiResponse $state '已關閉這個程式的限制。' }
   'Delete' {
    $state.Rules=@($state.Rules | Where-Object {[int]$_.Id -ne [int]$request.Id})
    $state.Apps=@($state.Apps | Where-Object {[int]$_.Id -ne [int]$request.Id})
    ApplyGuiState $state;$reply=GetGuiResponse $state '程式及其規則已移除。'
   }
   'Add' {
    . (Join-Path $PSScriptRoot 'AppPicker.ps1')
    foreach($entry in $request.Applications) {
     $path=ResolvePickerExecutable ([string]$entry.Path)
     if(-not $path){throw ('無法使用這個程式的執行檔：'+$entry.Name)}
     if(@($state.Apps | Where-Object {$path -match $_.Pattern}).Count){continue}
     $next=GetAvailableAppId $state.Apps
     if(-not $next){throw '程式清單已滿。'}
     $name=([string]$entry.Name -replace '[\r\n\t]',' ').Trim();if(-not $name){$name=[IO.Path]::GetFileNameWithoutExtension($path)}
     $state.Apps=@($state.Apps)+@([pscustomobject]@{Id=$next;Name=$name;Paths=@($path);Pattern='(?i)^'+(EscapePath $path)+'$'})
    }
    ApplyGuiState $state;$reply=GetGuiResponse $state '程式已加入，請選擇程式設定規則。'
   }
   'Check' {
    if($request.Id){SetGuiRule $state $request}
    $built=BuildRouterConfig $state @(GetAdapterSnapshot)
    CheckRouterConfig $built.Text
    $reply=@{Ok=$true;Message='設定檢查通過，未套用限制，也未進行連線測試。'}
   }
   default { throw '無法辨識這項操作。' }
  }
 }
} catch {
 $failure=$_
 $reply=@{Ok=$false;Message=$failure.Exception.Message}
 try{$reply.State=ReadRouterState;if($reply.State){$reply.AppHealth=@(GetRouterAppHealth $reply.State)};if($reply.State -and $reply.State.NetworkCatalog){$reply.NetworkCatalog=@($reply.State.NetworkCatalog)}}catch{}
 try{$reply.Snapshot=@(GetAdapterSnapshot)}catch{}
 try{if(Test-Path -LiteralPath $script:statusPath){$reply.Status=ReadSharedText $script:statusPath | ConvertFrom-Json}}catch{}
 try{[void][IO.Directory]::CreateDirectory($DataRoot);[IO.File]::AppendAllText((Join-Path $DataRoot 'gui-errors.log'),((Get-Date -Format s)+' '+$failure.Exception.Message+[Environment]::NewLine+$failure.ScriptStackTrace+[Environment]::NewLine),(New-Object Text.UTF8Encoding($false)))}catch{}
}finally{
 $script:guiMutationLockHeld=$false
 ExitGuiStateLock $stateMutex
}
[IO.File]::WriteAllText($OutputPath,($reply | ConvertTo-Json -Depth 20),(New-Object Text.UTF8Encoding($false)))
