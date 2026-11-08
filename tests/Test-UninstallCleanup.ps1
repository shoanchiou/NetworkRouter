$ErrorActionPreference='Stop'
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('networkrouter-uninstall-test-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$data=Join-Path $fixture 'data';[void][IO.Directory]::CreateDirectory($data)
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\Uninstall-Cleanup.ps1') -InstallRoot $fixture -Library
$originalProgramFiles=$env:ProgramFiles;$env:ProgramFiles=$fixture
$install=Join-Path $fixture 'NetworkRouter';[void][IO.Directory]::CreateDirectory((Join-Path $install 'core'))
$hash='A'*64
$hashes=[ordered]@{};foreach($name in @('Router.ps1','router-guard.exe','paid-network-guard.exe','core\mihomo.exe')){$hashes[$name]=$hash;[IO.File]::WriteAllText((Join-Path $install $name),'fixture')}
[IO.File]::WriteAllText((Join-Path $install 'networkrouter-install.json'),(@{Product='NetworkRouter';Version='1.0.0';Hashes=$hashes}|ConvertTo-Json -Depth 4))
function GetCleanupHash([string]$Path){return $hash}
$verified=TestCleanupPayload $install
if($verified -ne $install){throw 'Valid marker not accepted'}
$hashes['router-guard.exe']='B'*64
[IO.File]::WriteAllText((Join-Path $install 'networkrouter-install.json'),(@{Product='NetworkRouter';Version='1.0.0';Hashes=$hashes}|ConvertTo-Json -Depth 4))
$blocked=$false;try{[void](TestCleanupPayload $install)}catch{$blocked=$true};if(-not $blocked){throw 'Changed guard was accepted'}
Write-Output 'PASS Payload marker and hash checked before cleanup'
$env:ProgramFiles=$originalProgramFiles
function TestCleanupPayload([string]$Root){return $fixture}
function GetCleanupProfileRoot([string]$Sid){return $data}
function ResolveCleanupSid([string]$Account){if($Account -eq 'bad'){throw 'unresolvable principal'};return 'S-1-5-21-123'}
function GetCleanupTask {if($script:task){return $script:task}}
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw "FAIL $Message"};Write-Output "PASS $Message"}
$system=Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$argsText='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $fixture 'Router.ps1')+'" -Service -DataRoot "'+$data+'"'
$script:task=[pscustomobject]@{Principal=[pscustomobject]@{UserId='test'};Actions=@([pscustomobject]@{Execute=$system;Arguments=$argsText})}
$plan=GetCleanupPlan $fixture;Assert ($plan.HasTask -and $plan.DataRoot -eq $data -and $plan.Sid -eq 'S-1-5-21-123') 'Owned task resolves explicit data and SID'
$script:task.Actions[0].Arguments=$argsText.Replace((Join-Path $fixture 'Router.ps1'),(Join-Path $fixture 'other\Router.ps1'))
$blocked=$false;try{[void](GetCleanupPlan $fixture)}catch{$blocked=$true};Assert $blocked 'Other installation task refused'
$script:task.Actions[0].Arguments=$argsText;$script:task.Principal.UserId='bad'
$blocked=$false;try{[void](GetCleanupPlan $fixture)}catch{$blocked=$true};Assert $blocked 'Unknown principal refused'
$script:task=$null;$plan=GetCleanupPlan $fixture;Assert (-not $plan.HasTask -and -not $plan.DataRoot) 'No task does not infer another user data root'
function GetCleanupTrayTasks {if($script:tray){return $script:tray}}
function TestCleanupServiceHeld {return $script:serviceHeld}
function GetCleanupCoreProcesses {if($script:core){return $script:core}}
$script:tray=[pscustomobject]@{Actions=@([pscustomobject]@{Execute=(Join-Path $fixture 'NetworkRouterUI.exe')})}
$blocked=$false;try{AssertCleanupTrayOwner $plan}catch{$blocked=$true};Assert $blocked 'Foreign tray is rejected before cleanup'
$script:tray=$null;$script:serviceHeld=$true;$blocked=$false;try{AssertCleanupInactive $plan}catch{$blocked=$true};Assert $blocked 'Active supervisor is rejected'
$script:serviceHeld=$false;$script:core=[pscustomobject]@{ExecutablePath=(Join-Path $fixture 'core\mihomo.exe')};$blocked=$false;try{AssertCleanupInactive $plan}catch{$blocked=$true};Assert $blocked 'Active own core is rejected'
$script:core=[pscustomobject]@{ExecutablePath=''};$blocked=$false;try{AssertCleanupInactive $plan}catch{$blocked=$true};Assert $blocked 'Unidentifiable core is rejected'
$script:core=$null;AssertCleanupInactive $plan;Assert $true 'Inactive supervisor and own core are accepted'
$script:calls=@()
function AssertCleanupTrayOwner($Plan){$script:calls+='pretray';if($script:failTray){throw 'fixture foreign tray'}}
function AssertCleanupInactive($Plan){$script:calls+='inactive';if($script:failActive){throw 'fixture active'}}
function InvokeCleanupRouting($Plan){$script:calls+='route'}
function InvokeCleanupGuard($Plan){$script:calls+='guard'}
function InvokeCleanupPaid($Plan){$script:calls+='paid';if($script:failPaid){throw 'fixture failure'}}
function InvokeCleanupTray($Plan){$script:calls+='tray'}
$script:failPaid=$false;InvokeCleanupPlan $plan;Assert (($script:calls -join ',') -eq 'pretray,inactive,guard,paid,tray') 'No task releases only own guard, paid protection and tray'
$script:calls=@();$script:failPaid=$true;$blocked=$false;try{InvokeCleanupPlan $plan}catch{$blocked=$true};Assert ($blocked -and ($script:calls -join ',') -eq 'pretray,inactive,guard,paid') 'Recovery failure prevents following cleanup and deletion'
$script:calls=@();$script:failPaid=$false;$plan.HasTask=$true;InvokeCleanupPlan $plan;Assert (($script:calls -join ',') -eq 'pretray,route,inactive,paid,tray') 'Owned task uses routing recovery before paid and tray'
$script:calls=@();$script:failActive=$true;$blocked=$false;try{InvokeCleanupPlan $plan}catch{$blocked=$true};Assert ($blocked -and ($script:calls -join ',') -eq 'pretray,route,inactive') 'Active supervisor after routing recovery blocks paid cleanup and removal'
$script:calls=@();$script:failActive=$false;$script:failTray=$true;$blocked=$false;try{InvokeCleanupPlan $plan}catch{$blocked=$true};Assert ($blocked -and ($script:calls -join ',') -eq 'pretray') 'Foreign tray blocks before any recovery'
Write-Output 'All cleanup operations were mocked; no services, tasks, rules or network settings changed.'
