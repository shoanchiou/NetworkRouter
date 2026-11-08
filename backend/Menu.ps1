param([switch]$Restore,[string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'UnifiedNetworkRouter'))
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
$OutputEncoding=[Console]::OutputEncoding
function SaveMenuText([string]$Name,[string]$Text,[switch]$Append){
 $folders=@($script:dataRoot,$PSScriptRoot,(Join-Path $env:TEMP 'UnifiedNetworkRouter')) | Where-Object {$_} | Select-Object -Unique
 foreach($folder in $folders){try{
  [void][IO.Directory]::CreateDirectory($folder)
  $path=Join-Path $folder $Name;$encoding=New-Object Text.UTF8Encoding($true)
  if($Append){[IO.File]::AppendAllText($path,$Text+[Environment]::NewLine,$encoding)}else{[IO.File]::WriteAllText($path,$Text,$encoding)}
  return $path
 }catch{}}
 return $null
}
function WriteMenuError($Failure){
 $detail=(Get-Date -Format 'yyyy MM dd HH mm ss')+[Environment]::NewLine+$Failure.Exception.Message+[Environment]::NewLine+$Failure.InvocationInfo.PositionMessage+[Environment]::NewLine+$Failure.ScriptStackTrace
 Write-Host $Failure.Exception.Message -ForegroundColor Red
 $saved=SaveMenuText '錯誤記錄.txt' $detail -Append
 if($saved){Write-Host ('錯誤記錄已保存 '+$saved) -ForegroundColor Yellow}else{Write-Host $detail -ForegroundColor Yellow}
}
trap {WriteMenuError $_;Write-Host '按 Enter 離開';[void][Console]::ReadLine();exit 1}
. (Join-Path $PSScriptRoot 'Router.ps1') -Library -DataRoot $DataRoot
function ReadNumberText([string]$Prompt){Write-Host $Prompt;$s=[Console]::ReadLine();if($null -eq $s){return '0'};return $s.Trim()}
function ReadNumbers([string]$Prompt,[int[]]$Allowed,[int]$All=99){
 while($true){$s=ReadNumberText $Prompt;if($s -eq '0'){return};if($s -eq [string]$All){return $Allowed};if($s -match '^\d+(?:\s+\d+)*$'){
  try{$ids=@($s -split '\s+' | ForEach-Object {[int]$_} | Select-Object -Unique)}catch{$ids=@()}
  if($ids.Count -gt 0 -and @($ids | Where-Object {$_ -notin $Allowed}).Count -eq 0){return $ids}
 };Write-Host '只輸入列出的數字 多選用空格分開' -ForegroundColor Yellow}
}
function AlignText([string]$Text,[int]$Width){$displayWidth=0;foreach($ch in $Text.ToCharArray()){$displayWidth+=if([int]$ch -ge 4352){2}else{1}};return $Text+(' '*[Math]::Max(3,$Width-$displayWidth))}
function WriteResultTable($State,$Status=$null){
 $built=BuildRouterConfig $State @(GetAdapterSnapshot)
 $fresh=$false
 try{$age=((Get-Date)-[datetime]$Status.Time).TotalSeconds;$fresh=$Status.Phase -eq 'Ready' -and [int]$Status.Revision -eq [int]$State.Revision -and $age -ge 0 -and $age -lt 15}catch{}
 $lines=@()
 foreach($row in $built.Rows){
  $reported=$null;if($fresh){$reported=$Status.Rows | Where-Object {[int]$_.Id -eq [int]$row.Id} | Select-Object -First 1}
  $current=if(-not $State.Enabled){'分流未啟動'}elseif($reported){
   if($reported.IPv4 -eq $reported.IPv6){'目前使用 '+$reported.IPv4}else{'IPv4 '+$reported.IPv4+'  IPv6 '+$reported.IPv6}
  }else{'等待分流器回報'}
  $line=(AlignText $row.Name 26)+(AlignText ('可使用 '+$row.Allowed) 49)+$current
  Write-Host $line;$lines+=$line
 }
 if(-not $lines.Count){Write-Host '目前沒有程式限制規則';$lines=@('目前沒有程式限制規則')}
 $saved=SaveMenuText '目前網路允許表.txt' ($lines -join [Environment]::NewLine)
 if($saved){Write-Host ('結果已保存 '+$saved) -ForegroundColor Cyan}else{Write-Host '畫面結果已顯示 結果檔無法保存' -ForegroundColor Yellow}
}
function RecordAction([string]$Text){
 $line=(Get-Date -Format 'yyyy MM dd HH mm ss')+'  '+$Text
 $saved=SaveMenuText '執行記錄.txt' $line -Append
 if(-not $saved){Write-Host '執行記錄無法保存' -ForegroundColor Yellow}
}
function SaveAndApply($State){
 NormalizeRouterState $State
 if(-not @($State.Rules | Where-Object {$_.Enabled}).Count){throw '沒有開啟的規則，請先開啟規則。'}
 $State.Revision=[int]$State.Revision+1;$built=BuildRouterConfig $State @(GetAdapterSnapshot)
 CheckRouterConfig $built.Text
 # Check configuration and register recovery before enabling persistent guard.
 RegisterRouterStartup $State
 $State.Enabled=$true;SaveRouterState $State
 Start-ScheduledTask -TaskName $script:taskName
 Write-Host '正在套用程式分流和斷線保護'
 return WaitRouterRevision ([int]$State.Revision)
}
function PickRouterApps($State){
 Write-Host '第二步 選擇程式' -ForegroundColor Cyan
 foreach($app in @($State.Apps | Sort-Object {[int]$_.Id})){Write-Host ($app.Id.ToString()+' '+$app.Name)}
 Write-Host '99 全部程式';Write-Host '0 返回'
 return @(ReadNumbers '請輸入程式編號 多選用空格分開' @($State.Apps | ForEach-Object {[int]$_.Id}))
}
function AddRouterApps($State){
 . (Join-Path $script:routerRoot 'AppPicker.ps1')
 $selected=@(SelectPickerApplications)
 foreach($entry in $selected){
  $path=ResolvePickerExecutable $entry.Path;if(-not $path){Write-Host ($entry.Name+' 執行檔目前不可用') -ForegroundColor Yellow;continue}
  $duplicate=$State.Apps | Where-Object {$path -match $_.Pattern} | Select-Object -First 1
  if($duplicate){Write-Host ('已在選單中 '+$duplicate.Id+' '+$duplicate.Name);continue}
  $next=GetAvailableAppId $State.Apps
  if(-not $next){throw '新增程式編號已滿'}
  $name=([string]$entry.Name -replace '[\r\n\t]',' ').Trim();if(-not $name){$name=[IO.Path]::GetFileNameWithoutExtension($path)}
  $State.Apps=@($State.Apps)+@([pscustomobject]@{Id=$next;Name=$name;Paths=@($path);Pattern='(?i)^'+(EscapePath $path)+'$'})
  SaveRouterState $State
  Write-Host ('新增成功 '+$next+' '+$name) -ForegroundColor Green
 }
 Write-Host '新增程式只加入選單 尚未開啟限制'
}
function ShowRouterLogs{
 foreach($name in @('錯誤記錄.txt','執行記錄.txt','core-errors.log','core-output.log')){
  $path=Join-Path $script:dataRoot $name;if(Test-Path -LiteralPath $path){Write-Host $name -ForegroundColor Cyan;Get-Content -LiteralPath $path -Encoding UTF8 -Tail 35 | ForEach-Object {Write-Host $_}}
 }
}
function ReadMenuRouterState{
 try{
  $loaded=ReadRouterState
  if(-not $loaded){$loaded=NewRouterState;SaveRouterState $loaded;RecordAction '已建立空白設定 尚未啟動分流'}
  return $loaded
 }catch{WriteMenuError $_}
 while($true){
  Write-Host '設定檔無法讀取 仍可使用恢復選單' -ForegroundColor Yellow
  Write-Host '7 停止分流並恢復網路'
  Write-Host '4 備份損壞設定並重設全部限制'
  Write-Host '8 查看錯誤記錄'
  Write-Host '0 離開'
  $choice=ReadNumberText '請輸入數字後按 Enter'
  if($choice -eq '0'){return $null}
  try{
   if($choice -eq '7'){StopRouterService;Write-Host '網路已恢復' -ForegroundColor Green;RecordAction '恢復選單 停止分流成功'}
   elseif($choice -eq '8'){ShowRouterLogs}
   elseif($choice -eq '4'){
    StopRouterService -DeleteRules
    $raw=$null
    if(Test-Path -LiteralPath $script:statePath){
     $backup=$script:statePath+'.damaged.'+[Guid]::NewGuid().ToString('N')+'.bak'
     [IO.File]::Copy($script:statePath,$backup,$false)
     try{$raw=Get-Content -LiteralPath $backup -Raw -Encoding UTF8 | ConvertFrom-Json}catch{}
    }
    $fresh=NewRouterState -SkipLegacy
    if($raw -and $raw.Apps){
     $originalApps=$fresh.Apps;$fresh.Apps=$raw.Apps
     try{AssertRouterState $fresh}catch{$fresh.Apps=$originalApps}
    }
    SaveRouterState $fresh
    Write-Host '重設完成 可以繼續使用選單' -ForegroundColor Green
    RecordAction '恢復選單 備份損壞設定並重設成功'
    return $fresh
   }
  }catch{WriteMenuError $_;RecordAction ('恢復選單 操作失敗 '+$_.Exception.Message)}
 }
}
function EditRouterOptions($State){
 $schema=ReadSharedText (Join-Path $script:routerRoot 'OptionsSchema.json') | ConvertFrom-Json
 $items=@($schema.Options)
 for($i=0;$i -lt $items.Count;$i++){Write-Host (($i+1).ToString()+' '+$items[$i].Label+'：'+$State.Options.($items[$i].Key))}
 $choice=0;$answer=ReadNumberText '選擇設定編號，0 返回'
 if(-not [int]::TryParse($answer,[ref]$choice) -or $choice -lt 1 -or $choice -gt $items.Count){return}
 $item=$items[$choice-1];Write-Host $item.Description
 if($item.Type -eq 'bool'){$answer=ReadNumberText '1 開啟，2 關閉，0 取消';if($answer -notin @('1','2')){return};$value=($answer -eq '1')}
 elseif($item.Type -eq 'enum'){
  for($i=0;$i -lt $item.Choices.Count;$i++){Write-Host (($i+1).ToString()+' '+$item.Choices[$i])}
  $n=0;$answer=ReadNumberText '選擇數字，0 取消';if(-not [int]::TryParse($answer,[ref]$n) -or $n -lt 1 -or $n -gt $item.Choices.Count){return};$value=[string]$item.Choices[$n-1]
 }elseif($item.Type -eq 'string'){
  Write-Host '每行一個網址；單獨輸入 . 完成，0 取消'
  $lines=New-Object 'Collections.Generic.List[string]'
  while($true){$line=[Console]::ReadLine();if($null -eq $line -or $line -eq '0'){return};if($line -eq '.'){break};$lines.Add($line)}
  $value=$lines -join [Environment]::NewLine
 }else{$n=0;$answer=ReadNumberText ('輸入整數 '+$item.Min+' 至 '+$item.Max+'；其他輸入取消');if(-not [int]::TryParse($answer,[ref]$n) -or $n -lt $item.Min -or $n -gt $item.Max){return};$value=$n}
 $State.Options.($item.Key)=$value
 $token=[Guid]::NewGuid().ToString('N');$inputFile=Join-Path $script:dataRoot ('menu-options-'+$token+'.json');$outputFile=$inputFile+'.reply'
 try{
  [IO.File]::WriteAllText($inputFile,(@{Action='SaveOptions';Revision=$State.Revision;Options=$State.Options}|ConvertTo-Json -Depth 16),(New-Object Text.UTF8Encoding($false)))
  & (Join-Path $script:routerRoot 'GuiBridge.ps1') -InputPath $inputFile -OutputPath $outputFile -DataRoot $script:dataRoot
  $reply=ReadSharedText $outputFile | ConvertFrom-Json
  if(-not $reply.Ok){throw $reply.Message};Write-Host $reply.Message -ForegroundColor Green
 }finally{
  if([IO.File]::Exists($inputFile)){[IO.File]::WriteAllText($inputFile,'')}
  if([IO.File]::Exists($outputFile)){[IO.File]::WriteAllText($outputFile,'')}
 }
}
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){Write-Host '請使用啟動 BAT 並同意 Windows 管理員確認' -ForegroundColor Red;[void](ReadNumberText '按 Enter 離開');exit 1}
try{
 if(-not $Restore){InitializeRouterFolder}
 if($Restore){StopRouterService;Write-Host '程式分流已停止；計費有線額外保護仍保留' -ForegroundColor Green;RecordAction '恢復網路成功';[void](ReadNumberText '按 Enter 離開');exit}
 $state=ReadMenuRouterState
 if(-not $state){exit}
 while($true){
  $state=ReadMenuRouterState;if(-not $state){break};$status=$null
  try{if(Test-Path -LiteralPath $script:statusPath){$status=ReadSharedText $script:statusPath | ConvertFrom-Json}}catch{}
  Clear-Host
  Write-Host '程式網路分流與限制 修正版 3' -ForegroundColor Cyan
  Write-Host ''
  if($state.Enabled -and $status -and $status.Phase -eq 'Ready' -and [int]$status.Revision -eq [int]$state.Revision -and ((Get-Date)-[datetime]$status.Time).TotalSeconds -lt 15){Write-Host '分流與斷線保護已啟用' -ForegroundColor Green}else{Write-Host '分流尚未就緒 查看執行記錄可顯示原因' -ForegroundColor Yellow}
  Write-Host '只允許 所選網路可以使用 其餘網路全部封鎖'
  Write-Host '只封鎖 所選網路不能使用 其餘網路可以使用'
  Write-Host '允許網路都不能使用時就斷線 不會改用被封鎖網路'
  Write-Host '多選允許網路時 輸入的先後就是優先順序'
  Write-Host '套用後請重新開啟所選程式 讓新連線使用指定網路'
  if($state.Options.FailClosed){Write-Host '分流故障時 斷線保護會封鎖繞過限制的流量'}else{Write-Host '斷線保護已關閉' -ForegroundColor Yellow}
  Write-Host '停止分流並恢復網路可以解除斷線保護'
  Write-Host '未設定的程式沿用 Windows 原有網路優先順序'
  Write-Host ''
  Write-Host '第一步 選擇操作'
  Write-Host '1 開啟或調整程式限制'
  Write-Host '2 關閉所選程式限制'
  Write-Host '3 查看網路允許表'
  Write-Host '4 重設預設值 刪除全部限制並恢復網路'
  Write-Host '5 新增程式小工具'
  Write-Host '6 啟動或恢復分流'
  Write-Host '7 停止分流並恢復網路 保留程式設定'
  Write-Host '8 查看執行記錄'
  Write-Host '9 功能設定'
  Write-Host '0 離開選單 分流繼續運作'
  $action=ReadNumberText '請輸入數字後按 Enter'
  if($action -eq '0'){break}
  try{
   if($action -in @('1','2')){
    $ids=@(PickRouterApps $state);if(-not $ids.Count){continue}
    $mode='Allow';$networks=@()
    if($action -eq '1'){
     Write-Host '第三步 選擇限制方式' -ForegroundColor Cyan
     Write-Host '1 只允許所選網路';Write-Host '2 只封鎖所選網路';Write-Host '0 返回'
     $style=ReadNumberText '請輸入數字後按 Enter';if($style -notin @('1','2')){continue}
     $mode=if($style -eq '1'){'Allow'}else{'Deny'}
     Write-Host '第四步 選擇網路' -ForegroundColor Cyan
     [void]@(GetAdapterSnapshot);SetStateNetworkCatalog $state
     foreach($network in $script:networkDefinitions){Write-Host ($network.Id.ToString()+' '+$network.Name)}
     Write-Host '99999 全部網路';Write-Host '0 返回'
     $networks=@(ReadNumbers '請輸入網路編號 多選用空格分開' @($script:networkDefinitions | ForEach-Object {[int]$_.Id}) 99999);if(-not $networks.Count){continue}
    }
    foreach($app in @($state.Apps | Where-Object {[int]$_.Id -in $ids} | Sort-Object {[int]$_.Id})){
     Write-Host ('處理中 '+$app.Name)
     $state.Rules=@($state.Rules | Where-Object {[int]$_.Id -ne [int]$app.Id})
     if($action -eq '1'){$state.Rules+=@(NewRouterRule ([int]$app.Id) $mode ([int[]]$networks))}
    }
    if($state.Rules.Count -eq 0){SaveRouterState $state;StopRouterService -DeleteRules;$state=ReadRouterState;$status=$null}
    else{$status=SaveAndApply $state}
    Write-Host ''
    foreach($app in @($state.Apps | Where-Object {[int]$_.Id -in $ids} | Sort-Object {[int]$_.Id})){
     $result=if($action -eq '1'){'開啟限制成功'}else{'關閉限制成功'}
     Write-Host ($app.Id.ToString()+' '+$app.Name+' '+$result) -ForegroundColor Green
     RecordAction ($app.Name+' '+$result)
    }
    WriteResultTable $state $status
   }elseif($action -eq '3'){WriteResultTable $state $status}
   elseif($action -eq '4'){StopRouterService -DeleteRules;$state=ReadRouterState;Write-Host '重設成功 全部限制已刪除；計費有線額外保護仍保留' -ForegroundColor Green;RecordAction '重設預設值成功';WriteResultTable $state}
   elseif($action -eq '5'){AddRouterApps $state}
   elseif($action -eq '6'){$status=SaveAndApply $state;Write-Host '分流啟動成功' -ForegroundColor Green;RecordAction '啟動分流成功';WriteResultTable $state $status}
   elseif($action -eq '7'){StopRouterService;$state=ReadRouterState;Write-Host '分流已停止 程式設定及 計費有線額外保護仍保留' -ForegroundColor Green;RecordAction '停止分流並恢復網路成功';WriteResultTable $state}
   elseif($action -eq '8'){ShowRouterLogs}
   elseif($action -eq '9'){EditRouterOptions $state}
   else{continue}
  }catch{Write-Host '操作未完成' -ForegroundColor Red;WriteMenuError $_;RecordAction ('操作失敗 '+$_.Exception.Message);Write-Host '可使用選項 7 或獨立恢復網路 BAT'}
  [void](ReadNumberText '按 Enter 回到選單')
 }
}catch{WriteMenuError $_;Write-Host '需要恢復連線時 請使用獨立恢復網路 BAT';[void](ReadNumberText '按 Enter 離開');exit 1}
