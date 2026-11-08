param([Parameter(Mandatory=$true)][string]$InstallRoot,[string]$ResultPath,[switch]$Library)
$ErrorActionPreference='Stop'
function GetCleanupTask {
 @(Get-ScheduledTask -ErrorAction Stop | Where-Object {$_.TaskName -ceq 'UnifiedNetworkRouter' -and $_.TaskPath -ceq '\'})
}
function ResolveCleanupSid([string]$Account){
 if(-not $Account){throw '背景工作沒有可辨識的使用者。'}
 try {
  if($Account -match '^S-\d(-\d+)+$'){$sid=New-Object Security.Principal.SecurityIdentifier($Account)}
  else{$sid=(New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier])}
  [void]$sid.Translate([Security.Principal.NTAccount]);return $sid.Value
 }catch{throw '無法辨識背景工作的使用者，未移除任何檔案。'}
}
function GetCleanupProfileRoot([string]$Sid){
 $key='HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\'+$Sid
 $profile=[Environment]::ExpandEnvironmentVariables([string](Get-ItemProperty -LiteralPath $key -Name ProfileImagePath -ErrorAction Stop).ProfileImagePath)
 if(-not [IO.Path]::IsPathRooted($profile)){throw '背景工作使用者的設定位置無法確認。'}
 return (Join-Path $profile 'AppData\Local\UnifiedNetworkRouter')
}
function GetCleanupHash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash}
function TestCleanupPayload([string]$Root){
 $full=[IO.Path]::GetFullPath($Root).TrimEnd('\')
 $expected=Join-Path ${env:ProgramFiles} 'NetworkRouter'
 if(-not [string]::Equals($full,$expected,[StringComparison]::OrdinalIgnoreCase)){throw '安裝位置無法確認；請先以原程式恢復網路。'}
 $marker=Join-Path $full 'networkrouter-install.json'
 if(-not (Test-Path -LiteralPath $marker -PathType Leaf)){throw '缺少安裝識別資料；請先以原程式恢復網路。'}
 $m=Get-Content -LiteralPath $marker -Raw -Encoding UTF8 | ConvertFrom-Json
 if($m.Product -cne 'NetworkRouter' -or $m.Version -cne '1.0.0'){throw '安裝識別資料不符。'}
 foreach($key in @('Router.ps1','router-guard.exe','paid-network-guard.exe','core\mihomo.exe')){
  $file=Join-Path $full $key;$expectedHash=[string]$m.Hashes.$key
  if(-not (Test-Path -LiteralPath $file -PathType Leaf) -or $expectedHash -notmatch '^[A-F0-9]{64}$' -or (GetCleanupHash $file) -cne $expectedHash){throw ('安裝檔案無法驗證：'+$key)}
 }
 return $full
}
function GetCleanupPlan([string]$Root){
 $root=TestCleanupPayload $Root
 $tasks=@(GetCleanupTask)
 if($tasks.Count -gt 1){throw '發現重複背景工作，請先以原程式恢復網路。'}
 if(-not $tasks.Count){return [pscustomobject]@{Root=$root;HasTask=$false;DataRoot='';Sid=''}}
 $task=$tasks[0];$sid=ResolveCleanupSid ([string]$task.Principal.UserId)
 $actions=@($task.Actions)
 if($actions.Count -ne 1){throw '背景工作動作無法確認，請先以原程式恢復網路。'}
 $systemPowerShell=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
 if(-not [string]::Equals([string]$actions[0].Execute,$systemPowerShell,[StringComparison]::OrdinalIgnoreCase)){throw '背景工作不屬於此安裝，請先以原程式恢復網路。'}
 # Only accept the exact command layout emitted by RegisterRouterStartup; reject extra switches and arguments.
 $pattern='^\s*-NoProfile\s+-ExecutionPolicy\s+Bypass\s+-WindowStyle\s+Hidden\s+-File\s+"([^"]+)"\s+-Service\s+-DataRoot\s+"([^"]+)"\s*$'
 if([string]$actions[0].Arguments -notmatch $pattern){throw '背景工作參數無法確認，請先以原程式恢復網路。'}
 $scriptPath=$matches[1];$dataRoot=$matches[2]
 if(-not [IO.Path]::IsPathRooted($scriptPath) -or -not [IO.Path]::IsPathRooted($dataRoot)){throw '背景工作路徑不是絕對路徑。'}
 if(-not [string]::Equals([IO.Path]::GetFullPath($scriptPath),(Join-Path $root 'Router.ps1'),[StringComparison]::OrdinalIgnoreCase)){throw '背景工作屬於另一份安裝，請先以舊程式恢復網路。'}
 if(-not [string]::Equals([IO.Path]::GetFullPath($dataRoot).TrimEnd('\'),[IO.Path]::GetFullPath((GetCleanupProfileRoot $sid)).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){throw '背景工作設定位置與使用者不符，無法安全恢復。'}
 if(-not (Test-Path -LiteralPath $dataRoot -PathType Container)){throw '背景工作設定資料夾不存在，無法安全恢復。'}
 $dataItem=Get-Item -LiteralPath $dataRoot -Force
 if(($dataItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '背景工作設定資料夾無法安全驗證。'}
 return [pscustomobject]@{Root=$root;HasTask=$true;DataRoot=[IO.Path]::GetFullPath($dataRoot);Sid=$sid}
}
function GetCleanupTrayTasks {@(Get-ScheduledTask -ErrorAction Stop | Where-Object {$_.TaskName -ceq 'UnifiedNetworkRouterTray' -and $_.TaskPath -ceq '\'})}
function AssertCleanupTrayOwner($Plan){
 $tray=@(GetCleanupTrayTasks)
 if($tray.Count -gt 1){throw '通知區背景工作重複，請先以原程式恢復網路。'}
 if($tray.Count){
  $actions=@($tray[0].Actions);$expected=Join-Path $Plan.Root 'NetworkRouter.exe'
  if($actions.Count -ne 1 -or -not [string]::Equals([string]$actions[0].Execute,$expected,[StringComparison]::OrdinalIgnoreCase)){throw '通知區背景工作屬於另一份安裝，請先以原程式關閉該功能。'}
 }
}
function TestCleanupServiceHeld {
 $mutex=$null;$held=$false
 try{
  try{$mutex=[Threading.Mutex]::OpenExisting('Global\UnifiedNetworkRouterService')}
  catch [Threading.WaitHandleCannotBeOpenedException] {$mutex=$null}
  if($mutex){
   try{$acquired=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$acquired=$true}
   if($acquired){$mutex.ReleaseMutex()}else{$held=$true}
  }
 }finally{if($mutex){$mutex.Dispose()}}
 return $held
}
function GetCleanupCoreProcesses {@(Get-CimInstance Win32_Process -Filter "Name='mihomo.exe'" -ErrorAction Stop)}
function AssertCleanupInactive($Plan){
 if(TestCleanupServiceHeld){throw '分流背景程序仍在執行，請從原程式恢復網路後再移除。'}
 $core=Join-Path $Plan.Root 'core\mihomo.exe'
 foreach($process in @(GetCleanupCoreProcesses)){
  if(-not $process.ExecutablePath){throw '無法確認執行中的核心歸屬，請先恢復網路。'}
  if([string]::Equals([IO.Path]::GetFullPath([string]$process.ExecutablePath),$core,[StringComparison]::OrdinalIgnoreCase)){throw '此安裝的核心仍在執行，請先恢復網路。'}
 }
}
function InvokeCleanupRouting($Plan){
 . (Join-Path $Plan.Root 'Router.ps1') -Library -DataRoot $Plan.DataRoot
 StopRouterService
}
function InvokeCleanupGuard($Plan){
 & (Join-Path $Plan.Root 'router-guard.exe') stop
 if($LASTEXITCODE -ne 0){throw '無法解除本工具分流保護。'}
}
function InvokeCleanupPaid($Plan){
 & (Join-Path $Plan.Root 'paid-network-guard.exe') uninstall
 if($LASTEXITCODE -ne 0){throw '無法移除計費網路保護，檔案已保留。'}
}
function InvokeCleanupTray($Plan){
 $data=if($Plan.HasTask){$Plan.DataRoot}else{Join-Path $env:TEMP ('networkrouter-uninstall-'+[guid]::NewGuid().ToString('N'))}
 . (Join-Path $Plan.Root 'Router.ps1') -Library -DataRoot $data
 SetRouterTrayStartup $false
}
function InvokeCleanupPlan($Plan){
 AssertCleanupTrayOwner $Plan
 if($Plan.HasTask){InvokeCleanupRouting $Plan;AssertCleanupInactive $Plan}else{AssertCleanupInactive $Plan;InvokeCleanupGuard $Plan}
 InvokeCleanupPaid $Plan
 InvokeCleanupTray $Plan
}
if($Library){return}
try{$plan=GetCleanupPlan $InstallRoot;InvokeCleanupPlan $plan;$reply=[ordered]@{Ok=$true;Code='CLEANED';Message='網路保護已恢復，使用者規則保留。'};$code=0}
catch{$reply=[ordered]@{Ok=$false;Code='CLEANUP_FAILED';Message=$_.Exception.Message};$code=1}
if($ResultPath){[IO.File]::WriteAllText($ResultPath,($reply | ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))}else{$reply|ConvertTo-Json -Compress}
exit $code
