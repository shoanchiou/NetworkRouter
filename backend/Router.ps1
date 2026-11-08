param([switch]$Service,[switch]$Library,[string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'UnifiedNetworkRouter'))
$ErrorActionPreference='Stop'
$script:routerRoot=$PSScriptRoot
$script:dataRoot=$DataRoot
$script:statePath=Join-Path $DataRoot 'rules.json'
$script:statusPath=Join-Path $DataRoot 'status.json'
$script:configPath=Join-Path $DataRoot 'config.yaml'
$script:corePath=Join-Path $PSScriptRoot 'core\mihomo.exe'
$script:guardPath=Join-Path $PSScriptRoot 'router-guard.exe'
$script:taskName='UnifiedNetworkRouter'
$script:networkDefinitions=@()
function ReadNetworkCatalog {
 $catalogPath=Join-Path $script:dataRoot 'network-adapters.json'
 $catalog=@();$broken=$false
 if(Test-Path -LiteralPath $catalogPath){try{$loaded=ReadSharedText $catalogPath | ConvertFrom-Json;$catalog=@($loaded | Where-Object {$null -ne $_})}catch [IO.IOException]{throw}catch{$broken=$true}}
 if(-not $catalog.Count -and (Test-Path -LiteralPath $script:statePath)){
  try{$saved=ReadSharedText $script:statePath | ConvertFrom-Json;if($saved.NetworkCatalog){$catalog=@($saved.NetworkCatalog)}}catch [IO.IOException]{throw}catch{}
 }
 if(-not $catalog.Count -and $broken){throw '網卡清單損壞，請先恢復網路後重新設定。'}
 $ids=@{};$guids=@{};$result=@()
 foreach($item in $catalog){
  $id=[int]$item.Id;$guid=[Guid]::Empty
  if($id -lt 1 -or $id -gt 65535 -or $ids.ContainsKey($id) -or -not [Guid]::TryParse([string]$item.Guid,[ref]$guid) -or $guid -eq [Guid]::Empty -or $guids.ContainsKey($guid.ToString())){throw '網卡識別清單不完整或重複，請先恢復網路後重新設定。'}
  $ids[$id]=$true;$guids[$guid.ToString()]=$true
  $result+=[pscustomobject]@{Id=$id;Guid=$guid.ToString();Name=[string]$item.Name;Alias=[string]$item.Alias;Description=[string]$item.Description;Present=$false;Up4=$false;Up6=$false}
 }
 return $result
}
function SyncNetworkCatalog($Adapters){
 [void][IO.Directory]::CreateDirectory($script:dataRoot)
 $sha=[Security.Cryptography.SHA256]::Create();try{$key=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:dataRoot.ToUpperInvariant())))).Replace('-','')}finally{$sha.Dispose()}
 $mutex=New-Object Threading.Mutex($false,('Global\UnifiedNetworkRouterAdapters.'+$key));$owned=$false
 try{
  try{$owned=$mutex.WaitOne([TimeSpan]::FromSeconds(10))}catch [Threading.AbandonedMutexException]{$owned=$true}
  if(-not $owned){throw '網卡清單正在更新，請稍後重新整理。'}
  $catalog=@(ReadNetworkCatalog)
  foreach($adapter in $Adapters){
   $guid=([string]$adapter.InterfaceGuid).Trim('{}').ToLowerInvariant();$parsed=[Guid]::Empty
   if(-not [Guid]::TryParse($guid,[ref]$parsed) -or $parsed -eq [Guid]::Empty){continue}
   $entry=$catalog | Where-Object {$_.Guid -eq $guid} | Select-Object -First 1
   if(-not $entry){
    $next=0;foreach($known in $catalog){$next=[Math]::Max($next,[int]$known.Id)};$next++
    if($next -gt 65535){throw '網卡清單已滿。'}
    $entry=[pscustomobject]@{Id=$next;Guid=$guid;Name=[string]$adapter.Name;Alias='';Description='';Present=$false;Up4=$false;Up6=$false};$catalog+=@($entry)
   }
   $entry.Alias=[string]$adapter.Name;$entry.Description=[string]$adapter.InterfaceDescription;$entry.Present=$true
   if(-not $entry.Name){$entry.Name=$entry.Alias}
  }
  $catalogPath=Join-Path $script:dataRoot 'network-adapters.json';$serialized=ConvertTo-Json -InputObject ([object[]]$catalog) -Depth 8
  $previous=if(Test-Path -LiteralPath $catalogPath){ReadSharedText $catalogPath}else{''}
  if($previous -ne $serialized){WriteAtomic $catalogPath $serialized}
  $script:networkDefinitions=@($catalog)
  return $catalog
 }finally{if($owned){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
function SetStateNetworkCatalog($State) {
 if($State){$State | Add-Member -NotePropertyName NetworkCatalog -NotePropertyValue ([object[]]@($script:networkDefinitions)) -Force}
}
function GetAvailableAppId($Apps){
 $used=@{};foreach($app in $Apps){$used[[int]$app.Id]=$true}
 foreach($id in 12..98){if(-not $used.ContainsKey($id)){return $id}}
 return 0
}
function ReadSharedText([string]$Path){
 $stream=New-Object IO.FileStream($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
 try{$reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8,$true);try{return $reader.ReadToEnd()}finally{$reader.Dispose()}}finally{$stream.Dispose()}
}
function WriteAtomic([string]$Path,[string]$Text){
 $tmp=$Path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
 try{[IO.File]::WriteAllText($tmp,$Text,(New-Object Text.UTF8Encoding($false))); for($attempt=0;;$attempt++){try{if(Test-Path -LiteralPath $Path){[IO.File]::Replace($tmp,$Path,($Path+'.previous'))}else{[IO.File]::Move($tmp,$Path)};break}catch [IO.IOException]{if($attempt -ge 4 -or (($_.Exception.HResult -band 0xffff) -notin @(32,33))){throw};Start-Sleep -Milliseconds 50}}}finally{if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Force}}
}
function InitializeRouterFolder{
 [void][IO.Directory]::CreateDirectory($script:dataRoot)
 foreach($path in @($script:corePath,$script:guardPath)){if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw '執行包不完整 請先解壓全部檔案'}}
}
function InvokeRouterNative([string]$Executable,[string[]]$Arguments,[int]$TimeoutMilliseconds=30000){
 $info=New-Object Diagnostics.ProcessStartInfo
 $info.FileName=$Executable
 $info.Arguments=(@($Arguments | ForEach-Object {'"'+$_.Replace('"','\"')+'"'}) -join ' ')
 $info.UseShellExecute=$false;$info.CreateNoWindow=$true
 $info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
 $info.StandardOutputEncoding=New-Object Text.UTF8Encoding($false)
 $info.StandardErrorEncoding=New-Object Text.UTF8Encoding($false)
 $info.EnvironmentVariables['NETWORK_RULE_CATALOG']=Join-Path $env:LOCALAPPDATA 'NetworkRuleManager\custom-apps.tsv'
 $info.EnvironmentVariables['SKIP_SYSTEM_IPV6_CHECK']='1'
 $proc=New-Object Diagnostics.Process;$proc.StartInfo=$info
 try{
  [void]$proc.Start();$stdout=$proc.StandardOutput.ReadToEndAsync();$stderr=$proc.StandardError.ReadToEndAsync()
  if(-not $proc.WaitForExit($TimeoutMilliseconds)){try{$proc.Kill()}catch{};throw ('工具回應逾時 '+[IO.Path]::GetFileName($Executable))}
  return [pscustomobject]@{Code=$proc.ExitCode;Text=($stdout.Result+$stderr.Result).Trim()}
 }finally{$proc.Dispose()}
}
function EscapePath([string]$Path){return [regex]::Escape($Path).Replace(',','\x2c')}
function NewRouterState([switch]$SkipLegacy){
 # Existing saved state remains authoritative; new installations start empty.
 $bytes=New-Object byte[] 32;$rng=New-Object Security.Cryptography.RNGCryptoServiceProvider;try{$rng.GetBytes($bytes)}finally{$rng.Dispose()}
 return [pscustomobject]@{Version=3;Revision=1;Enabled=$false;Secret=[Convert]::ToBase64String($bytes);Apps=@();Rules=@();NetworkCatalog=@();ControllerPort=17931}
}
function GetRouterAppHealth($State){
 $result=@()
 foreach($app in @($State.Apps)){
  $paths=@($app.Paths);$exact=($app.Id -ge 12 -and $paths.Count -eq 1 -and [string]$app.Pattern -ceq ('(?i)^'+(EscapePath ([string]$paths[0]))+'$'))
  $pathInfo=@(foreach($path in $paths){$exists=[IO.File]::Exists([string]$path);[pscustomobject]@{Path=[string]$path;Exists=$exists;Status=if($exists){'Exists'}else{'Unknown'}}})
  $result+=@([pscustomobject]@{Id=[int]$app.Id;ExactOnly=$exact;Scope=if($exact){'Exact'}else{'Group'};Paths=$pathInfo})
 }
 return $result
}
function SetRouterApplicationPath($State,[int]$Id,[string]$Path){
 $app=$State.Apps | Where-Object {[int]$_.Id -eq $Id} | Select-Object -First 1
 if(-not $app){throw '所選程式已不存在。'}
 $health=GetRouterAppHealth $State | Where-Object {$_.Id -eq $Id} | Select-Object -First 1
 if(-not $health.ExactOnly){throw '此程式是群組或內建規則，無法以單一執行檔重新綁定。'}
 $resolved=ResolvePickerExecutable $Path
 if(-not $resolved){throw '新的執行檔無法使用。'}
 foreach($other in @($State.Apps | Where-Object {[int]$_.Id -ne $Id})){
  if([regex]::IsMatch($resolved,[string]$other.Pattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase,[TimeSpan]::FromMilliseconds(200))){throw ('新的執行檔已被其他程式涵蓋：'+$other.Name)}
 }
 $app.Paths=@($resolved);$app.Pattern='(?i)^'+(EscapePath $resolved)+'$'
}
function NewRouterRule([int]$Id,[string]$Mode,[int[]]$Networks){
 if($Mode -notin @('Allow','Deny')){throw '限制方式不正確'}
 $rule=[pscustomobject]@{Id=$Id;Mode=$Mode;Networks=[int[]]@($Networks);Priority=[int[]]@()}
 if($Mode -eq 'Allow'){$rule.Priority=[int[]]@($Networks)}
 return $rule
}
function GetRouterOptions($State){
 $schema=ReadSharedText (Join-Path $script:routerRoot 'OptionsSchema.json') | ConvertFrom-Json
 $values=[ordered]@{}
 foreach($item in $schema.Options){$values[[string]$item.Key]=$item.Default}
 if($State -and $State.Options){foreach($prop in $State.Options.PSObject.Properties){$values[$prop.Name]=$prop.Value}}
 return [pscustomobject]$values
}
function AssertRouterOptions($Options){
 if(-not $Options){throw '選項資料不可為空。'}
 $schema=ReadSharedText (Join-Path $script:routerRoot 'OptionsSchema.json') | ConvertFrom-Json
 $known=@{};foreach($item in $schema.Options){$known[[string]$item.Key]=$item}
 foreach($prop in $Options.PSObject.Properties){
  if(-not $known.ContainsKey($prop.Name)){throw ('未知選項 '+$prop.Name)}
  $item=$known[$prop.Name];$value=$prop.Value
  switch($item.Type){
   'bool' {if($value -isnot [bool]){throw ($prop.Name+' 必須為布林值')}}
   'int' {if($value -isnot [int] -and $value -isnot [long]){throw ($prop.Name+' 必須為整數')};if($value -lt $item.Min -or $value -gt $item.Max){throw ($prop.Name+' 超出範圍')}}
   'enum' {if($value -isnot [string] -or $value -cnotin @($item.Choices)){throw ($prop.Name+' 選項無效')}}
   'string' {if($value -isnot [string] -or $value.Length -gt [int]$item.MaxLength){throw ($prop.Name+' 字串無效或過長')}}
   default {throw ('不支援的選項類型 '+$item.Type)}
  }
 }
}
function GetRouterDnsServers([string]$Text,[int]$Family){
 if([string]::IsNullOrWhiteSpace($Text)){return @()}
 $result=New-Object 'Collections.Generic.List[string]'
 foreach($line in ($Text -split "`n",0,'SimpleMatch')){
  $value=$line.Trim();if(-not $value){continue};$uri=$null;$address=$null
  if($value -match '[\r\n]' -or $value -match '^https://[^/]+:' -and $value -match '^https://[^/]+:($|/|\?)' -or -not [Uri]::TryCreate($value,[UriKind]::Absolute,[ref]$uri)){throw 'DNS 端點必須是有效的 HTTPS 網址，每行一個。'}
  if($uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Fragment -or $uri.Host -match '%' -or -not [Net.IPAddress]::TryParse($uri.Host.Trim('[',']'),[ref]$address)){throw 'DNS 端點必須使用 HTTPS 和 IP 位址，不能包含使用者資訊或片段。'}
  if(($Family -eq 4 -and $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) -or ($Family -eq 6 -and ($address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetworkV6 -or $address.IsIPv4MappedToIPv6))){throw 'DNS 端點 IP 版本不符。'}
  [void]$result.Add($value+'#DNS-DEFAULT')
 }
 return $result.ToArray()
}
function NormalizeRouterState($State){
 if($State.Options){AssertRouterOptions $State.Options}
 $State|Add-Member -NotePropertyName Options -NotePropertyValue (GetRouterOptions $State) -Force
 foreach($rule in $State.Rules){
  if(-not $rule.PSObject.Properties['Enabled']){$rule|Add-Member -NotePropertyName Enabled -NotePropertyValue $true}
  # Deny rules never use priority. Windows PowerShell 5 previously persisted
  # their null pipeline output as an empty object instead of an empty array.
  if($rule.Mode -eq 'Deny'){$rule.Priority=[int[]]@()}
  elseif($null -eq $rule.Priority){$rule.Priority=[int[]]@($rule.Networks)}
 }
}
function AssertRouterState($State){
 NormalizeRouterState $State
 if($State.Version -ne 3){throw '設定檔版本不相容'}
 $seen=@{};foreach($app in $State.Apps){$id=[int]$app.Id;if($id -lt 1 -or $id -gt 98 -or $seen.ContainsKey($id)){throw '程式編號重複或超出範圍'};$seen[$id]=$true;[void][regex]::new($app.Pattern)}
 $used=@{};foreach($rule in $State.Rules){$id=[int]$rule.Id;if(-not $seen.ContainsKey($id) -or $used.ContainsKey($id) -or $rule.Mode -notin @('Allow','Deny')){throw '程式規則不完整或重複'};$used[$id]=$true
  if($rule.Enabled -isnot [bool]){throw '規則啟用狀態必須為布林值'}
  $networkIds=@($State.NetworkCatalog | ForEach-Object {[int]$_.Id})
  foreach($n in $rule.Networks){if([int]$n -notin $networkIds){throw '網路選項不在已保存的網卡清單'}}
  foreach($n in $rule.Priority){if([int]$n -notin @($rule.Networks)){throw '優先順序包含未允許網路'}}
 }
 $o=GetRouterOptions $State
 $dns4=@(GetRouterDnsServers $o.DnsIPv4Servers 4);$dns6=@(GetRouterDnsServers $o.DnsIPv6Servers 6)
 if($o.DnsEnabled -and -not (($o.DnsIPv4Upstreams -and $dns4.Count -gt 0) -or ($o.IPv6 -and $o.DnsIPv6Upstreams -and $dns6.Count -gt 0))){throw '啟用 DNS 時至少需要一個可用的上游端點。'}
 if($o.DefaultNetworkId -ne 0 -and [int]$o.DefaultNetworkId -notin @(@($State.NetworkCatalog | ForEach-Object {[int]$_.Id}))){throw '預設網卡編號不在已保存的網卡清單。'}
 if($o.DnsEnabled -and -not $o.DnsIPv4Upstreams -and (-not $o.IPv6 -or -not $o.DnsIPv6Upstreams)){throw '啟用 DNS 時至少需要一組上游伺服器。'}
 if($o.DnsNetworkId -ne 0 -and [int]$o.DnsNetworkId -notin @(@($State.NetworkCatalog | ForEach-Object {[int]$_.Id}))){throw 'DNS 網卡編號不在已保存的網卡清單。'}
 if($State.Secret.Length -lt 32 -or [int]$State.ControllerPort -lt 1024 -or [int]$State.ControllerPort -gt 65535){throw '本機控制設定不完整'}
}
function ReadRouterState{
 if(-not (Test-Path -LiteralPath $script:statePath)){return $null}
 $state=ReadSharedText $script:statePath | ConvertFrom-Json
 NormalizeRouterState $state
 AssertRouterState $state;return $state
}
function SaveRouterState($State){NormalizeRouterState $State;AssertRouterState $State;WriteAtomic $script:statePath ($State | ConvertTo-Json -Depth 16)}
function GetPaidNetworkGate {
 $configuration=$null;$readError=$false
 try{$configuration=Get-ItemProperty -LiteralPath 'HKLM:\Software\UnifiedNetworkRouter\PaidNetworkGuard' -ErrorAction Stop}
 catch [Management.Automation.ItemNotFoundException]{$script:paidGateCache=$null;return $null}
 catch{$readError=$true}
 if(-not $configuration -and -not $readError){$script:paidGateCache=$null;return $null}
 $guid=if($configuration -and $configuration.Guid){([string]$configuration.Guid).Trim('{}').ToLowerInvariant()}elseif($script:paidGateCache){[string]$script:paidGateCache.Guid}else{''}
 if(-not $guid){throw '無法確認計費網卡保護的識別資訊；已停止網路選擇。'}
 try{
  $native=Join-Path $script:routerRoot 'paid-network-guard.exe'
  if(-not $readError -and (Test-Path -LiteralPath $native -PathType Leaf)){
   $reply=InvokeRouterNative $native @('status') 10000
   if($reply.Code -eq 0){
    $status=$reply.Text | ConvertFrom-Json
    # Even a pipe error can contain a fresh, verified kernel filter snapshot.
    if($status.Installed -and $status.Locked -is [bool] -and [int]$status.PersistentFilterCount -eq 12 -and [int]$status.BootFilterCount -eq 4 -and [int]$status.PermitFilterCount -in @(0,12)){
     if($status.Guid){$guid=([string]$status.Guid).Trim('{}').ToLowerInvariant()}
     $script:paidGateCache=[pscustomobject]@{Guid=$guid;Locked=[bool]$status.Locked;ConfirmedUtc=[DateTime]::UtcNow}
     return [pscustomobject]@{Guid=$guid;Locked=[bool]$status.Locked;Evidence='Confirmed'}
    }
   }
  }
 }catch{}
 # A brief status-query failure must not bounce an otherwise healthy game to
 # another ISP. The independent kernel filters still revoke paid access at once.
 if($script:paidGateCache -and $script:paidGateCache.Guid -eq $guid -and ([DateTime]::UtcNow-$script:paidGateCache.ConfirmedUtc).TotalSeconds -le 10){return [pscustomobject]@{Guid=$guid;Locked=[bool]$script:paidGateCache.Locked;Evidence='Cached'}}
 return [pscustomobject]@{Guid=$guid;Locked=$true;Evidence='Unknown'}
}
function GetAdapterSnapshot{
 $adapters=@(Get-NetAdapter -ErrorAction Stop | Where-Object {$_.Name -ne 'NetworkRouter' -and $_.Name -notlike '*Loopback*' -and $_.InterfaceDescription -notmatch 'Kernel Debug|WAN Miniport|Wi-Fi Direct|Teredo|IP-HTTPS|6to4'})
 [void](SyncNetworkCatalog $adapters)
 $routes=@(Get-NetRoute -ErrorAction Stop | Where-Object {$_.DestinationPrefix -in @('0.0.0.0/0','::/0','0.0.0.0/1','128.0.0.0/1','::/1','8000::/1')})
 $interfaces=@(Get-NetIPInterface -ErrorAction Stop)
 $paidGate=GetPaidNetworkGate
 $result=@()
 foreach($adapter in $adapters){
  $guid=([string]$adapter.InterfaceGuid).Trim('{}').ToLowerInvariant()
  $known=@($script:networkDefinitions | Where-Object {$_.Guid -eq $guid})
  if(-not $known.Count){continue};$id=[int]$known[0].Id
  $name=if($known.Count){$known[0].Name}else{[string]$adapter.Name}
  $best4=[int]::MaxValue;$best6=[int]::MaxValue;$prefix4=0;$prefix6=0
  $adapterRoutes=@($routes | Where-Object {$_.InterfaceIndex -eq $adapter.ifIndex})
  $fullRoutes=@($adapterRoutes | Where-Object {$_.DestinationPrefix -in @('0.0.0.0/0','::/0')})
  if('0.0.0.0/1' -in $adapterRoutes.DestinationPrefix -and '128.0.0.0/1' -in $adapterRoutes.DestinationPrefix){$prefix4=1;$fullRoutes=@($fullRoutes | Where-Object {$_.DestinationPrefix -ne '0.0.0.0/0'})+@($adapterRoutes | Where-Object {$_.DestinationPrefix -in @('0.0.0.0/1','128.0.0.0/1')})}
  if('::/1' -in $adapterRoutes.DestinationPrefix -and '8000::/1' -in $adapterRoutes.DestinationPrefix){$prefix6=1;$fullRoutes=@($fullRoutes | Where-Object {$_.DestinationPrefix -ne '::/0'})+@($adapterRoutes | Where-Object {$_.DestinationPrefix -in @('::/1','8000::/1')})}
  foreach($route in $fullRoutes){
   $family=if($route.DestinationPrefix -like '*:*'){'IPv6'}else{'IPv4'}
   $ipif=$interfaces | Where-Object {$_.InterfaceIndex -eq $adapter.ifIndex -and [string]$_.AddressFamily -eq $family} | Select-Object -First 1
   if(-not $ipif){continue};$metric=[int]$route.RouteMetric+[int]$ipif.InterfaceMetric
   if($family -eq 'IPv4'){$best4=[Math]::Min($best4,$metric)}else{$best6=[Math]::Min($best6,$metric)}
  }
  $physicalUp=([string]$adapter.Status -eq 'Up');$up=$physicalUp
  $gateApplies=($paidGate -and $paidGate.Locked -and $guid -eq $paidGate.Guid)
  if($paidGate -and $paidGate.Locked -and $guid -eq $paidGate.Guid){$up=$false}
  $known[0].Up4=($up -and $best4 -lt [int]::MaxValue);$known[0].Up6=($up -and $best6 -lt [int]::MaxValue)
  $result+=[pscustomobject]@{Id=$id;Name=$name;Alias=[string]$adapter.Name;Index=[int]$adapter.ifIndex;Guid=$guid;PhysicalUp=$physicalUp;HasRoute4=($best4 -lt [int]::MaxValue);HasRoute6=($best6 -lt [int]::MaxValue);GateBlocked=[bool]$gateApplies;GateEvidence=if($gateApplies){[string]$paidGate.Evidence}else{''};Up4=($up -and $best4 -lt [int]::MaxValue);Up6=($up -and $best6 -lt [int]::MaxValue);Metric4=$best4;Metric6=$best6;RoutePrefix4=$prefix4;RoutePrefix6=$prefix6}
 }
 return @($result | Sort-Object {[int]$_.Id})
}
function SelectRoute($Rule,$Snapshot,[string]$Family='4'){
 $up='Up'+$Family;$metric='Metric'+$Family;$candidates=@($Snapshot | Where-Object {$_.$up})
 if($Rule){if($Rule.Mode -eq 'Allow'){
   $eligible=@($candidates | Where-Object {[int]$_.Id -in @($Rule.Networks)})
   foreach($id in @($Rule.Priority)){$a=$eligible | Where-Object {$_.Id -eq [int]$id} | Select-Object -First 1;if($a){return $a}}
   $candidates=$eligible
  }else{$candidates=@($candidates | Where-Object {[int]$_.Id -notin @($Rule.Networks)})}}
 $prefix='RoutePrefix'+$Family
 return $candidates | Sort-Object {-1*[int]$_.$prefix},{$_.$metric},{[int]$_.Index} | Select-Object -First 1
}
function SelectProgramRoute($Rule,$Snapshot){
 $candidates=@($Snapshot | Where-Object {$_.Up4 -or $_.Up6})
 if($Rule -and $Rule.Mode -eq 'Allow'){
  $candidates=@($candidates | Where-Object {[int]$_.Id -in @($Rule.Networks)})
  foreach($id in @($Rule.Priority)){$selected=$candidates | Where-Object {[int]$_.Id -eq [int]$id} | Select-Object -First 1;if($selected){return $selected}}
 }elseif($Rule){$candidates=@($candidates | Where-Object {[int]$_.Id -notin @($Rule.Networks)})}
 # Prefer the ordinary IPv4 route where one exists, then IPv6-only networks.
 # A program's two address families never independently choose different ISPs.
 $selected=SelectRoute $null $candidates '4';if($selected){return $selected}
 return SelectRoute $null $candidates '6'
}
function GetRouterRouteReason($Rule,$Snapshot,$Options,[string]$Family,$Selected,$OtherSelected){
 if($Family -eq '6' -and -not $Options.IPv6){return [pscustomobject]@{Code='IPV6_DISABLED';Reason='IPv6 已在設定中關閉。'}}
 if($Selected){return [pscustomobject]@{Code='ROUTED';Reason='已選定網卡；尚未測試實際連線。'}}
 if($Options.SingleAdapter -and $OtherSelected){return [pscustomobject]@{Code='FAMILY_UNAVAILABLE';Reason=('所選網卡沒有可用的 IPv'+$Family+' 路由。')}}
 $eligible=@($Snapshot)
 if($Rule.Mode -eq 'Allow'){
  if(-not @($Rule.Networks).Count){return [pscustomobject]@{Code='NO_ALLOWED_NETWORK';Reason='規則沒有允許的網卡。'}}
  $eligible=@($eligible | Where-Object {[int]$_.Id -in @($Rule.Networks)})
  if(-not $Options.AllowBackup -and @($Rule.Priority).Count){$primary=[int]$Rule.Priority[0];$eligible=@($eligible | Where-Object {[int]$_.Id -eq $primary})}
  if(-not $eligible.Count){return [pscustomobject]@{Code='ADAPTER_ABSENT';Reason='規則允許的網卡目前未出現。'}}
 }else{$eligible=@($eligible | Where-Object {[int]$_.Id -notin @($Rule.Networks)})}
 if(-not $eligible.Count){return [pscustomobject]@{Code='NO_ALLOWED_NETWORK';Reason='目前沒有符合規則的網卡。'}}
 if($eligible.Count -eq 1){
  $adapter=$eligible[0]
  if($adapter.GateBlocked){if($adapter.GateEvidence -eq 'Confirmed'){return [pscustomobject]@{Code='PAID_BLOCKED';Reason='獨立計費網卡保護回報此網卡已封鎖。'}};return [pscustomobject]@{Code='GATE_UNCONFIRMED';Reason='計費網卡保護未能確認可用，暫時按封鎖處理。'}}
  if($adapter.PSObject.Properties['PhysicalUp'] -and -not $adapter.PhysicalUp){return [pscustomobject]@{Code='ADAPTER_DOWN';Reason='符合規則的網卡目前未連線。'}}
  $routeProperty='HasRoute'+$Family
  if($adapter.PSObject.Properties[$routeProperty] -and -not $adapter.$routeProperty){return [pscustomobject]@{Code='NO_ROUTE';Reason=('網卡沒有可用的 IPv'+$Family+' 預設路由。')}}
 }
 return [pscustomobject]@{Code='NO_AVAILABLE_NETWORK';Reason=('沒有符合規則且可用的 IPv'+$Family+' 網卡路由。')}
}
function GetRouterDnsReason($Options,$Selection,$Snapshot){
 if(-not $Options.DnsEnabled){return [pscustomobject]@{Route='';Code='DNS_DISABLED';Reason='核心 DNS 已關閉。'}}
 if($Selection -match '^NIC(\d+)$'){$id=[int]$matches[1];$adapter=$Snapshot | Where-Object {[int]$_.Id -eq $id} | Select-Object -First 1;$name=if($adapter){[string]$adapter.Name}else{$Selection};return [pscustomobject]@{Route=$name;Code='DNS_ROUTE_SELECTED';Reason='已選定 DNS 使用的網卡；尚未測試 DNS 解析。'}}
 return [pscustomobject]@{Route='';Code='DNS_NO_ROUTE';Reason='目前沒有可用的 DNS 網卡路由；尚未測試 DNS 解析。'}
}
function BuildRouterConfig($State,$Snapshot,$PreviousState=$null,$PreviousBuilt=$null){
 AssertRouterState $State
 $options=GetRouterOptions $State
 $diagnosticSnapshot=$Snapshot
 if(-not $options.IPv6){$Snapshot=@($Snapshot | ForEach-Object {$copy=$_ | Select-Object *;$copy.Up6=$false;$copy})}
 $dnsDefault4=SelectRoute $null $Snapshot '4';$dnsDefault6=if($options.IPv6){SelectRoute $null $Snapshot '6'}else{$null}
 $default4=$dnsDefault4;$default6=$dnsDefault6
 if($options.DefaultNetworkId -ne 0){$default4=$Snapshot | Where-Object {[int]$_.Id -eq $options.DefaultNetworkId -and $_.Up4} | Select-Object -First 1;$default6=$Snapshot | Where-Object {[int]$_.Id -eq $options.DefaultNetworkId -and $_.Up6} | Select-Object -First 1}
 if(-not $options.AllowUnconfiguredPrograms){$default4=$null;$default6=$null}
 $proxies=@();$nicNames=@();$nicIds=@{}
 foreach($network in @($script:networkDefinitions | Sort-Object {[int]$_.Id})){
  $alias=[string]$network.Alias
  if(-not $alias){
   $present=$Snapshot | Where-Object {[int]$_.Id -eq [int]$network.Id} | Select-Object -First 1
   if($present -and $present.Alias){$alias=[string]$present.Alias;$network|Add-Member -NotePropertyName Alias -NotePropertyValue $alias -Force}
  }
  if(-not $alias){$alias='NetworkRouterUnavailableNIC'+$network.Id}
  $name='NIC'+[int]$network.Id;$nicNames+=@($name);$nicIds[[int]$network.Id]=$true
  $proxies+=@([ordered]@{'name'=$name;'type'='direct';'interface-name'=$alias;'udp'=$true})
 }
 $choices=@('REJECT')+@($nicNames)
 $groups=@();$selections=[ordered]@{}
 foreach($name in @('DEFAULT-V4','DEFAULT-V6','DNS-DEFAULT')){$groups+=@([ordered]@{'name'=$name;'type'='select';'proxies'=$choices})}
 $selections['DEFAULT-V4']=if($default4 -and $nicIds.ContainsKey([int]$default4.Id)){'NIC'+[int]$default4.Id}else{'REJECT'}
 $selections['DEFAULT-V6']=if($default6 -and $nicIds.ContainsKey([int]$default6.Id)){'NIC'+[int]$default6.Id}else{'REJECT'}
 $dns4Servers=@(GetRouterDnsServers $options.DnsIPv4Servers 4);$dns6Servers=@(GetRouterDnsServers $options.DnsIPv6Servers 6)
 $canDns4=($options.DnsIPv4Upstreams -and $dns4Servers.Count -gt 0);$canDns6=($options.IPv6 -and $options.DnsIPv6Upstreams -and $dns6Servers.Count -gt 0)
 $selections['DNS-DEFAULT']=if($canDns4 -and $dnsDefault4 -and $nicIds.ContainsKey([int]$dnsDefault4.Id)){'NIC'+[int]$dnsDefault4.Id}elseif($canDns6 -and $dnsDefault6 -and $nicIds.ContainsKey([int]$dnsDefault6.Id)){'NIC'+[int]$dnsDefault6.Id}else{'REJECT'}
 if($options.DnsNetworkId -ne 0){
  $dnsAdapter=$Snapshot | Where-Object {[int]$_.Id -eq $options.DnsNetworkId -and (($canDns4 -and $_.Up4) -or ($canDns6 -and $_.Up6))} | Select-Object -First 1
  $selections['DNS-DEFAULT']=if($dnsAdapter -and $nicIds.ContainsKey([int]$dnsAdapter.Id)){'NIC'+[int]$dnsAdapter.Id}else{'REJECT'}
 }
 $dnsServers=@();if($canDns4){$dnsServers+=$dns4Servers};if($canDns6){$dnsServers+=$dns6Servers}
 $routing=@();if($options.BlockMulticast){$routing=@('IP-CIDR,224.0.0.0/4,REJECT,no-resolve','IP-CIDR,255.255.255.255/32,REJECT,no-resolve','IP-CIDR6,ff00::/8,REJECT,no-resolve')};$effective=@();$routeRules=@()
 foreach($rule in $State.Rules){
  if(-not $rule.Enabled){continue}
  $app=$State.Apps | Where-Object {$_.Id -eq $rule.Id} | Select-Object -First 1
  $routeSnapshot=$Snapshot
  if(-not $options.AllowBackup -and $rule.Mode -eq 'Allow' -and @($rule.Priority).Count){$primary=[int]$rule.Priority[0];$routeSnapshot=@($Snapshot | Where-Object {[int]$_.Id -eq $primary})}
  if($options.SingleAdapter){$selected=SelectProgramRoute $rule $routeSnapshot;$a4=if($selected -and $selected.Up4){$selected}else{$null};$a6=if($selected -and $selected.Up6){$selected}else{$null}}
  else{$a4=SelectRoute $rule $routeSnapshot '4';$a6=SelectRoute $rule $routeSnapshot '6'}
  if(-not $options.AutoReturnPreferred -and $PreviousState -and $PreviousBuilt){
   $oldRule=$PreviousState.Rules | Where-Object {[int]$_.Id -eq [int]$rule.Id} | Select-Object -First 1
   $oldOptions=GetRouterOptions $PreviousState
   $sameOptions=($oldOptions.SingleAdapter -eq $options.SingleAdapter -and $oldOptions.AllowBackup -eq $options.AllowBackup -and $oldOptions.IPv6 -eq $options.IPv6)
   $sameRule=($oldRule -and (ConvertTo-Json -InputObject $oldRule -Depth 8 -Compress) -ceq (ConvertTo-Json -InputObject $rule -Depth 8 -Compress))
   if($sameOptions -and $sameRule){
    $old4=[string](GetRouterObjectValue $PreviousBuilt.Selections ('APP'+[int]$app.Id+'-V4'))
    $old6=[string](GetRouterObjectValue $PreviousBuilt.Selections ('APP'+[int]$app.Id+'-V6'))
    if($options.SingleAdapter){
     $old=if($old4 -match '^NIC(\d+)$'){$old4}else{$old6}
     if($old -match '^NIC(\d+)$'){$oldId=[int]$matches[1];$candidate=$routeSnapshot | Where-Object {[int]$_.Id -eq $oldId -and ($_.Up4 -or $_.Up6)} | Select-Object -First 1;if($candidate){$a4=if($candidate.Up4){$candidate}else{$null};$a6=if($candidate.Up6){$candidate}else{$null}}}
    }else{
     if($old4 -match '^NIC(\d+)$'){$oldId=[int]$matches[1];$candidate=$routeSnapshot | Where-Object {[int]$_.Id -eq $oldId -and $_.Up4} | Select-Object -First 1;if($candidate){$a4=$candidate}}
     if($old6 -match '^NIC(\d+)$'){$oldId=[int]$matches[1];$candidate=$routeSnapshot | Where-Object {[int]$_.Id -eq $oldId -and $_.Up6} | Select-Object -First 1;if($candidate){$a6=$candidate}}
    }
   }
  }
  if(-not $options.IPv6){$a6=$null}
  $target4=if($a4 -and $nicIds.ContainsKey([int]$a4.Id)){'NIC'+[int]$a4.Id}else{'REJECT'};$target6=if($a6 -and $nicIds.ContainsKey([int]$a6.Id)){'NIC'+[int]$a6.Id}else{'REJECT'}
  $group4='APP'+[int]$app.Id+'-V4';$group6='APP'+[int]$app.Id+'-V6'
  foreach($name in @($group4,$group6)){$groups+=@([ordered]@{'name'=$name;'type'='select';'proxies'=$choices})}
  $selections[$group4]=$target4;$selections[$group6]=$target6
  $routing+='AND,((PROCESS-PATH-REGEX,'+$app.Pattern+'),(IP-CIDR6,::/0)),'+$group6
  $routing+='PROCESS-PATH-REGEX,'+$app.Pattern+','+$group4
  $routeRules+=@([pscustomobject]@{Id=[int]$app.Id;Pattern=[string]$app.Pattern;V4Group=$group4;V6Group=$group6})
  $names=@();foreach($n in $script:networkDefinitions){if(($rule.Mode -eq 'Allow' -and $n.Id -in @($rule.Networks)) -or ($rule.Mode -eq 'Deny' -and $n.Id -notin @($rule.Networks))){$names+=$n.Name}}
  if($rule.Mode -eq 'Deny'){$names+='其他網路'}
  $current4=if($a4){$a4.Name}else{'斷線'};$current6=if($a6){$a6.Name}else{'斷線'}
  $diag4=if($target4 -ne 'REJECT'){$a4}else{$null};$diag6=if($target6 -ne 'REJECT'){$a6}else{$null}
  $reason4=GetRouterRouteReason $rule $diagnosticSnapshot $options '4' $diag4 $diag6;$reason6=GetRouterRouteReason $rule $diagnosticSnapshot $options '6' $diag6 $diag4
  $effective+=[pscustomobject]@{Id=[int]$app.Id;Name=$app.Name;Allowed=if($names.Count){$names -join '  '}else{'全部封鎖'};IPv4=$current4;IPv6=$current6;Code4=$reason4.Code;Reason4=$reason4.Reason;Code6=$reason6.Code;Reason6=$reason6.Reason}
 }
 # Unknown process ownership is rejected instead of silently using the default.
 $routing+='AND,((PROCESS-PATH-REGEX,.+),(IP-CIDR6,::/0)),DEFAULT-V6'
 $routing+='PROCESS-PATH-REGEX,.+,DEFAULT-V4'
 if($options.BlockUnknownProcess){$routing+='MATCH,REJECT'}else{$routing+='IP-CIDR6,::/0,DEFAULT-V6,no-resolve';$routing+='MATCH,DEFAULT-V4'}
 $config=[ordered]@{
  'mode'='rule';'allow-lan'=$false;'bind-address'='127.0.0.1';'log-level'=$options.LogLevel;'ipv6'=$options.IPv6;'find-process-mode'='always'
  'external-controller'=('127.0.0.1:'+$State.ControllerPort);'secret'=$State.Secret
  'profile'=@{'store-selected'=$false;'store-fake-ip'=$false}
  'tun'=@{'enable'=$true;'device'='NetworkRouter';'stack'='mips';'auto-route'=$options.TunAutoRoute;'auto-detect-interface'=$false;'strict-route'=$options.TunStrictRoute;'mtu'=$options.TunMtu;'dns-hijack'=@($(if($options.DnsEnabled -and $options.DnsIntercept){'any:53';'tcp://any:53'}));'inet6-address'=@('fdfe:dcba:9876::1/126');'udp-timeout'=$options.UdpTimeout}
  'dns'=[ordered]@{'enable'=$options.DnsEnabled;'ipv6'=$options.IPv6;'enhanced-mode'='redir-host';'fake-ip-range'='198.18.0.1/16';'respect-rules'=$false;'default-nameserver'=@('1.1.1.1','2606:4700:4700::1111');'nameserver'=@($dnsServers);'proxy-server-nameserver'=@($dnsServers);'fallback-filter'=@{'geoip'=$false};'use-system-hosts'=$options.DnsUseHosts}
  'proxies'=@($proxies);'proxy-groups'=@($groups);'rules'=@($routing)
 }
 return [pscustomobject]@{Text=($config | ConvertTo-Json -Depth 20);Selections=$selections;RouteRules=@($routeRules);Rows=@($effective);Dns=(GetRouterDnsReason $options $selections['DNS-DEFAULT'] $diagnosticSnapshot);BlockUnknownProcess=$options.BlockUnknownProcess;Default4=if($default4){$default4.Name}else{'斷線'};Default6=if($default6){$default6.Name}else{'斷線'}}
}
function CheckRouterConfig([string]$Text){
 $testPath=Join-Path $script:dataRoot 'check-config.yaml';WriteAtomic $testPath $Text
 $r=InvokeRouterNative $script:corePath @('-t','-d',$script:dataRoot,'-f',$testPath)
 if($r.Code -ne 0){throw ('設定檔檢查失敗 '+$r.Text)}
}
function RouterApi($State,[string]$Method,[string]$Endpoint,$Body=$null){
 $parameters=@{Uri=('http://127.0.0.1:'+$State.ControllerPort+$Endpoint);Method=$Method;Headers=@{Authorization='Bearer '+$State.Secret};TimeoutSec=4;ErrorAction='Stop';UseBasicParsing=$true}
 if($null -ne $Body){$parameters.ContentType='application/json';$parameters.Body=($Body | ConvertTo-Json -Compress -Depth 8)}
 return Invoke-RestMethod @parameters
}
function GetRouterObjectValue($Object,[string]$Name){
 if($null -eq $Object){return $null}
 if($Object -is [Collections.IDictionary]){return $Object[$Name]}
 $property=$Object.PSObject.Properties[$Name];if($property){return $property.Value}
 return $null
}
function GetRouterSelectionTarget($Built,[string]$Group){
 $target=[string](GetRouterObjectValue $Built.Selections $Group)
 if($target -match '^NIC[1-9][0-9]*$'){return $target}
 return 'REJECT'
}
function SetRouterSelections($State,$Built,[switch]$Force){
 try{$current=RouterApi $State 'GET' '/proxies'}catch{$_.Exception.Data['RouterReadOnly']=$true;throw}
 $changed=New-Object 'Collections.Generic.List[string]'
 foreach($entry in $Built.Selections.GetEnumerator()){
  $name=[string]$entry.Key;$target=[string]$entry.Value
  $group=GetRouterObjectValue $current.proxies $name
  if(-not $group){throw ('找不到網路選擇器 '+$name)}
  if(-not $Force -and [string]$group.now -ceq $target){continue}
  $endpoint='/proxies/'+[Uri]::EscapeDataString($name)
  try{[void](RouterApi $State 'PUT' $endpoint @{name=$target})}
  catch{
   # A reply can time out after the controller has applied the choice. Read
   # back before treating that as a failed safety update and restarting TUN.
   $applyError=$_;$confirmed=$false
   try{$actual=RouterApi $State 'GET' $endpoint;$confirmed=([string]$actual.now -ceq $target)}catch{}
   if(-not $confirmed){throw $applyError}
  }
  [void]$changed.Add($name)
 }
 return [pscustomobject]@{Changed=($changed.Count -gt 0);Count=$changed.Count;Groups=[string[]]$changed.ToArray()}
}
function GetRouterConnectionTarget($Built,$Connection){
 $metadata=$Connection.metadata;$chains=@($Connection.chains)
 if($metadata -and [string]$metadata.type -ieq 'Inner' -and 'DNS-DEFAULT' -cin $chains){return GetRouterSelectionTarget $Built 'DNS-DEFAULT'}
 if(-not $metadata -or [string]::IsNullOrWhiteSpace([string]$metadata.processPath)){
  if($Built.BlockUnknownProcess -eq $false -and $metadata){$unknownAddress=$null;if([Net.IPAddress]::TryParse([string]$metadata.destinationIP,[ref]$unknownAddress)){if($unknownAddress.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6 -and -not $unknownAddress.IsIPv4MappedToIPv6){return GetRouterSelectionTarget $Built 'DEFAULT-V6'};return GetRouterSelectionTarget $Built 'DEFAULT-V4'}}
  return 'REJECT'
 }
 $destination=$null
 if(-not [Net.IPAddress]::TryParse([string]$metadata.destinationIP,[ref]$destination)){return 'REJECT'}
 $family=if($destination.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6 -and -not $destination.IsIPv4MappedToIPv6){'V6'}else{'V4'}
 foreach($rule in @($Built.RouteRules)){
  if([regex]::IsMatch([string]$metadata.processPath,[string]$rule.Pattern)){
   $group=if($family -eq 'V6'){[string]$rule.V6Group}else{[string]$rule.V4Group}
   return GetRouterSelectionTarget $Built $group
  }
 }
 return GetRouterSelectionTarget $Built ('DEFAULT-'+$family)
}
function CloseInvalidRouterConnections($State,$Built,[bool]$CloseAll=$false){
 try{$snapshot=RouterApi $State 'GET' '/connections'}catch{$_.Exception.Data['RouterReadOnly']=$true;throw}
 # Mihomo serializes an empty Go slice as connections:null. In Windows
 # PowerShell @($null) is one null entry, not an empty list.
 if($null -eq $snapshot){throw '核心回報的連線清單格式錯誤'}
 if($snapshot -is [Collections.IDictionary]){
  if(-not $snapshot.Contains('connections')){throw '核心回報的連線清單格式錯誤'}
  $connections=$snapshot['connections']
 }else{
  $connectionProperty=$snapshot.PSObject.Properties['connections']
  if(-not $connectionProperty){throw '核心回報的連線清單格式錯誤'}
  $connections=$connectionProperty.Value
 }
 if($null -eq $connections){$connections=@()}
 elseif($connections -isnot [Collections.IList]){throw '核心回報的連線清單格式錯誤'}
 $closed=New-Object 'Collections.Generic.List[string]';$kept=New-Object 'Collections.Generic.List[string]'
 foreach($connection in $connections){
  if($null -eq $connection){continue}
  $id=[string]$connection.id
  if([string]::IsNullOrWhiteSpace($id)){throw '核心回報了無法辨識的連線'}
  $target=GetRouterConnectionTarget $Built $connection
  $actual=@($connection.chains | Where-Object {[string]$_ -match '^NIC[1-9][0-9]*$'})
  if(-not $CloseAll -and $target -ne 'REJECT' -and $actual.Count -eq 1 -and [string]$actual[0] -ceq $target){[void]$kept.Add($id);continue}
  [void](RouterApi $State 'DELETE' ('/connections/'+[Uri]::EscapeDataString($id)))
  [void]$closed.Add($id)
 }
 return [pscustomobject]@{ClosedIds=[string[]]$closed.ToArray();KeptIds=[string[]]$kept.ToArray();ClosedCount=$closed.Count;KeptCount=$kept.Count}
}
function WriteRouterStatus($State,[string]$Phase,[string]$Message,$Built=$null){
 $status=[ordered]@{Revision=[int]$State.Revision;Phase=$Phase;Message=$Message;Time=(Get-Date -Format 'yyyy-MM-dd HH:mm:ss');ServicePid=$PID;CorePid=if($script:coreProcess){$script:coreProcess.Id}else{0};CorePath=$script:corePath;Rows=@();Default4='';Default6='';Dns=$null}
 if($Built){$status.Rows=@($Built.Rows);$status.Default4=$Built.Default4;$status.Default6=$Built.Default6;$status.Dns=$Built.Dns}
 try{WriteAtomic $script:statusPath ($status | ConvertTo-Json -Depth 12)}catch{Write-Warning ('狀態檔無法保存 '+$_.Exception.Message)}
}
function LockRouter([string]$Guid='0'){
 $r=InvokeRouterNative $script:guardPath @('lock',$Guid,$script:corePath);if($r.Code -ne 0){throw ('斷線保護設定失敗 '+$r.Text)}
}
function ApplyRouterGuard($State,[string]$Guid='0'){
 if((GetRouterOptions $State).FailClosed){LockRouter $Guid;return}
 $r=InvokeRouterNative $script:guardPath @('stop');if($r.Code -ne 0){throw ('解除斷線保護失敗 '+$r.Text)}
}
function GetRouterTrayStartupSnapshot{
 $task=Get-ScheduledTask -ErrorAction Stop | Where-Object {$_.TaskName -eq 'UnifiedNetworkRouterTray' -and $_.TaskPath -eq '\'} | Select-Object -First 1
 if($task){
  $expected=Join-Path $script:routerRoot 'NetworkRouter.exe'
  $actions=@($task.Actions)
  if($actions.Count -ne 1 -or -not [string]::Equals([string]$actions[0].Execute,$expected,[StringComparison]::OrdinalIgnoreCase)){throw '通知區排程名稱已被其他程式使用，無法變更。'}
 }
 return $task
}
function SetRouterTrayStartup([bool]$Enabled){
 $existing=GetRouterTrayStartupSnapshot
 if(-not $Enabled){if($existing){Unregister-ScheduledTask -TaskName 'UnifiedNetworkRouterTray' -Confirm:$false -ErrorAction Stop};return}
 $gui=Join-Path $script:routerRoot 'NetworkRouter.exe'
 if(-not (Test-Path -LiteralPath $gui -PathType Leaf)){throw '找不到通知區程式。'}
 $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
 $action=New-ScheduledTaskAction -Execute $gui -Argument '--tray' -WorkingDirectory $script:routerRoot
 $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
 $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
 $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
 Register-ScheduledTask -TaskName 'UnifiedNetworkRouterTray' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force -ErrorAction Stop | Out-Null
}
function RestoreRouterTrayStartup($Snapshot){
 if($Snapshot){Register-ScheduledTask -TaskName 'UnifiedNetworkRouterTray' -InputObject $Snapshot -Force -ErrorAction Stop | Out-Null}
 else{SetRouterTrayStartup $false}
}
function RegisterRouterStartup($State){
 $scriptPath=Join-Path $script:routerRoot 'Router.ps1'
 $action=New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+$scriptPath+'" -Service -DataRoot "'+$script:dataRoot+'"') -WorkingDirectory $script:routerRoot
 $user=[Security.Principal.WindowsIdentity]::GetCurrent().Name
 $trigger=New-ScheduledTaskTrigger -AtLogOn -User $user
 $principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
 $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
 $taskArgs=@{TaskName=$script:taskName;Action=$action;Principal=$principal;Settings=$settings;Force=$true}
 if(-not $State -or (GetRouterOptions $State).AutoStart){$taskArgs.Trigger=$trigger}
 Register-ScheduledTask @taskArgs | Out-Null
}
function StartRouterService{
 RegisterRouterStartup (ReadRouterState)
 Start-ScheduledTask -TaskName $script:taskName
}
function CloseRouterCore($State){
 if($State){try{[void](RouterApi $State 'PATCH' '/configs' @{tun=@{enable=$false}})}catch{}}
 $ownerStatus=$null
 try{if(Test-Path -LiteralPath $script:statusPath){$ownerStatus=ReadSharedText $script:statusPath | ConvertFrom-Json}}catch{}
 if($ownerStatus -and $ownerStatus.CorePid -and $ownerStatus.CorePath){
  $ownedCore=Get-Process -Id ([int]$ownerStatus.CorePid) -ErrorAction SilentlyContinue
  if($ownedCore -and [string]::Equals($ownedCore.Path,$ownerStatus.CorePath,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($ownedCore.Path) -ieq 'mihomo.exe'){
   Stop-Process -Id $ownedCore.Id -Force -ErrorAction Stop
  }
 }
 foreach($p in @(Get-Process -Name mihomo -ErrorAction SilentlyContinue)){
  if([string]::Equals($p.Path,$script:corePath,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $p.Id -Force -ErrorAction Stop}
 }
 # Forced exits may leave route entries. Only clean routes on our named TUN.
 $tun=Get-NetAdapter -Name NetworkRouter -ErrorAction SilentlyContinue
 if($tun -and [string]$tun.InterfaceDescription -match 'Wintun|Mihomo|Meta'){
  Get-NetRoute -InterfaceIndex $tun.ifIndex -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction Stop
 }
}
function StopRouterService([switch]$DeleteRules){
 $issues=New-Object 'Collections.Generic.List[string]'
 $state=$null
 try{$state=ReadRouterState}catch{Write-Host '設定檔無法讀取 正在使用獨立恢復方式' -ForegroundColor Yellow}
 if($state){
  $state.Enabled=$false;if($DeleteRules){$state.Rules=@()};$state.Revision=[int]$state.Revision+1
  try{SaveRouterState $state}catch{$issues.Add('保存停止設定 '+$_.Exception.Message)}
 }
 # Stop supervision first so it cannot restart a core being shut down.
 $task=$null
 try{$task=Get-ScheduledTask -TaskName $script:taskName -ErrorAction SilentlyContinue}catch{$issues.Add('讀取自動啟動工作 '+$_.Exception.Message)}
 if($task){
  try{Disable-ScheduledTask -TaskName $script:taskName -ErrorAction Stop | Out-Null}catch{$issues.Add('停用自動啟動 '+$_.Exception.Message)}
  try{Stop-ScheduledTask -TaskName $script:taskName -ErrorAction Stop}catch{$issues.Add('停止背景工作 '+$_.Exception.Message)}
  try{Unregister-ScheduledTask -TaskName $script:taskName -Confirm:$false -ErrorAction Stop}catch{$issues.Add('移除自動啟動 '+$_.Exception.Message)}
 }
 # Every recovery step is attempted even if an earlier step fails.
 try{CloseRouterCore $state}catch{$issues.Add('清理分流核心與路由 '+$_.Exception.Message)}
 $released=$false
 try{
  $r=InvokeRouterNative $script:guardPath @('stop')
  if($r.Code -ne 0){throw $r.Text};$released=$true
 }catch{$issues.Add('解除斷線保護 '+$_.Exception.Message)}
 if($issues.Count){
  $message=if($released){'已解除本工具封鎖 但恢復仍有未完成項目'}else{'恢復未完成'}
  $message+=[Environment]::NewLine+($issues -join [Environment]::NewLine)
  if($state){try{WriteRouterStatus $state 'Error' $message}catch{}}
  throw $message
 }
 if($state){try{WriteRouterStatus $state 'Stopped' '程式分流已停止；計費有線額外保護仍保留'}catch{Write-Host '網路已恢復 狀態檔無法保存' -ForegroundColor Yellow}}
}
function WaitRouterRevision([int]$Revision,[int]$Seconds=35){
 $deadline=(Get-Date).AddSeconds($Seconds)
 while((Get-Date) -lt $deadline){
  $status=$null
  try{if(Test-Path -LiteralPath $script:statusPath){$status=ReadSharedText $script:statusPath | ConvertFrom-Json}}catch{}
   if($status -and [int]$status.Revision -eq $Revision){
    $fresh=$false
    try{$age=((Get-Date)-[datetime]$status.Time).TotalSeconds;$fresh=$age -ge -5 -and $age -lt 15}catch{}
    if($fresh){if($status.Phase -eq 'Ready'){return $status};if($status.Phase -eq 'Error'){throw $status.Message}}
   }
  Start-Sleep -Milliseconds 450
 }
 throw '分流器尚未回報完成 請查看執行結果或使用恢復網路'
}
function RunRouterService{
 $mutex=New-Object Threading.Mutex($false,'Global\UnifiedNetworkRouterService')
 $owned=$false;$script:coreProcess=$null
 try{
  try{$owned=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true}
  if(-not $owned){return}
  $previous='';$previousSelections='';$lastGuard='';$state=ReadRouterState
  $lastBuilt=$null;$lastAppliedState=$null;$lastAppliedPolicySignature='';$discoveryFailureStarted=$null
  $lastSelectionCheckUtc=[DateTime]::MinValue;$lastSweepUtc=[DateTime]::MinValue;$sweepUntilUtc=[DateTime]::MinValue;$stateReadFailureStarted=$null
  if(-not $state -or -not $state.Enabled){return}
  ApplyRouterGuard $state '0';WriteRouterStatus $state 'Starting' '正在啟動分流與斷線保護'
  while($true){
   try{$nextState=ReadRouterState;$stateReadFailureStarted=$null}
   catch [IO.IOException]{
    if(-not $stateReadFailureStarted){$stateReadFailureStarted=[DateTime]::UtcNow}
    if($lastBuilt -and $script:coreProcess -and -not $script:coreProcess.HasExited -and ([DateTime]::UtcNow-$stateReadFailureStarted).TotalSeconds -lt 2){
     WriteRouterStatus $lastAppliedState 'Retrying' '暫時無法讀取設定，正在重新確認' $lastBuilt
     Start-Sleep -Milliseconds 250;continue
    }
    throw
   }
   $state=$nextState;if(-not $state -or -not $state.Enabled){break}
   $pendingPolicy=$true
   try{
    $coreAlive=($script:coreProcess -and -not $script:coreProcess.HasExited)
    try{
     $snapshot=@(GetAdapterSnapshot)
     $tun=if($coreAlive){Get-NetAdapter -Name NetworkRouter -ErrorAction Stop}else{$null}
     if($coreAlive -and (-not $tun -or [string]$tun.Status -ne 'Up')){throw '正在等候分流網卡重新就緒'}
     $discoveryFailureStarted=$null
    }catch{
     if(-not $discoveryFailureStarted){$discoveryFailureStarted=[DateTime]::UtcNow}
     # Physical adapter enumeration can briefly fail while Windows installs or
     # removes an iPhone. Explicit NIC bindings keep the last policy safe; do
     # not destroy unrelated live sockets just because a read failed once.
     $unchangedPolicy=($lastAppliedState -and [int]$state.Revision -eq [int]$lastAppliedState.Revision -and (ConvertTo-Json -InputObject $state -Depth 16 -Compress) -ceq $lastAppliedPolicySignature)
     if($coreAlive -and $lastBuilt -and $unchangedPolicy -and ([DateTime]::UtcNow-$discoveryFailureStarted).TotalSeconds -le 15){
      WriteRouterStatus $lastAppliedState 'Retrying' '網卡狀態正在更新 保留既有連線並重新偵測' $lastBuilt
      Start-Sleep -Seconds 2;continue
     }
     throw
    }
    $built=BuildRouterConfig $state $snapshot $lastAppliedState $lastBuilt
    $signature=$built.Text
    $selectionSignature=ConvertTo-Json -InputObject $built.Selections -Depth 4 -Compress
    $reloaded=$false
    if(-not $script:coreProcess -or $script:coreProcess.HasExited){
     ApplyRouterGuard $state '0';$lastGuard='';WriteRouterStatus $state 'Starting' '正在啟動分流核心' $built
     CheckRouterConfig $built.Text;WriteAtomic $script:configPath $built.Text
     $env:SKIP_SYSTEM_IPV6_CHECK='1'
     $script:coreProcess=Start-Process -FilePath $script:corePath -ArgumentList ('-d "'+$script:dataRoot+'" -f "'+$script:configPath+'"') -WorkingDirectory $script:routerRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $script:dataRoot 'core-output.log') -RedirectStandardError (Join-Path $script:dataRoot 'core-errors.log') -PassThru
     WriteRouterStatus $state 'Starting' '核心已啟動 等待本機介面' $built
     $ready=$false;for($i=0;$i -lt 70;$i++){
      if($script:coreProcess.HasExited){throw '分流核心啟動失敗 請查看核心記錄'}
      try{$v=RouterApi $state 'GET' '/version';$tun=Get-NetAdapter -Name NetworkRouter -ErrorAction Stop;if($tun -and [string]$tun.Status -eq 'Up' -and $v.version){$ready=$true;break}}catch{}
      Start-Sleep -Milliseconds 400
     }
     if(-not $ready){throw '分流核心未能建立本機介面'}
     $reloaded=$true
    }elseif($signature -ne $previous){
     CheckRouterConfig $built.Text;WriteAtomic $script:configPath $built.Text
     # Only structural changes (new programs/adapters or renamed interfaces)
     # reload config. Availability and priorities change selectors in place.
     [void](RouterApi $state 'PUT' '/configs?force=false' @{path=$script:configPath})
     $reloaded=$true
    }
    $pendingPolicy=($reloaded -or $signature -ne $previous -or $selectionSignature -ne $previousSelections)
    $now=[DateTime]::UtcNow
    if($pendingPolicy -or ($now-$lastSelectionCheckUtc).TotalSeconds -ge 10){
     $selectionResult=SetRouterSelections $state $built -Force:$reloaded
     $lastSelectionCheckUtc=[DateTime]::UtcNow
     if($selectionResult.Changed){$pendingPolicy=$true}
    }
    if($pendingPolicy){$sweepUntilUtc=[DateTime]::UtcNow.AddSeconds(20)}
    # Keep sockets whose physical NIC is still the selected one. Recheck for a
    # short period to catch a dial that was in flight during the policy change.
    if($pendingPolicy -or [DateTime]::UtcNow -le $sweepUntilUtc -or ([DateTime]::UtcNow-$lastSweepUtc).TotalSeconds -ge 10){
     [void](CloseInvalidRouterConnections $state $built ($pendingPolicy -and -not (GetRouterOptions $state).PreserveValidConnections));$lastSweepUtc=[DateTime]::UtcNow
    }
    if(-not $tun){$tun=Get-NetAdapter -Name NetworkRouter -ErrorAction Stop}
    $guid=[string]$tun.InterfaceGuid
    $guardKey=$guid+':'+[string](GetRouterOptions $state).FailClosed
    if($guardKey -ne $lastGuard){ApplyRouterGuard $state $guid;$lastGuard=$guardKey}
    $previous=$signature;$previousSelections=$selectionSignature;$lastBuilt=$built;$lastAppliedState=$state;$lastAppliedPolicySignature=ConvertTo-Json -InputObject $state -Depth 16 -Compress
    WriteRouterStatus $state 'Ready' $(if((GetRouterOptions $state).FailClosed){'分流與斷線保護已啟用'}else{'分流已啟用，斷線保護已關閉'}) $built
   }catch{
    $errorMessage=$_.Exception.Message
    if(-not $pendingPolicy -and $_.Exception.Data['RouterReadOnly'] -and $lastBuilt -and $script:coreProcess -and -not $script:coreProcess.HasExited -and ([DateTime]::UtcNow-$lastSelectionCheckUtc).TotalSeconds -le 30){
     WriteRouterStatus $lastAppliedState 'Retrying' '正在重新確認核心狀態 保留既有連線' $lastBuilt
     Start-Sleep -Seconds 2;continue
    }
    try{ApplyRouterGuard $state '0'}catch{$errorMessage+=' '+$_.Exception.Message}
    $lastGuard='';$previous='';$previousSelections='';$lastBuilt=$null;$lastAppliedState=$null;$lastAppliedPolicySignature=''
    if($script:coreProcess -and -not $script:coreProcess.HasExited){try{CloseRouterCore $state}catch{Write-Warning $_.Exception.Message}}
    $script:coreProcess=$null;WriteRouterStatus $state 'Error' $errorMessage
    if(-not (GetRouterOptions $state).AutoRetry){break}
    Start-Sleep -Seconds 3
   }
   Start-Sleep -Seconds 2
  }
 }catch{if($state){WriteRouterStatus $state 'Error' $_.Exception.Message;if(-not (GetRouterOptions $state).AutoRetry){return}};throw}finally{
  if($script:coreProcess -and -not $script:coreProcess.HasExited){try{CloseRouterCore $state}catch{Write-Warning $_.Exception.Message}}
  if($owned){$mutex.ReleaseMutex()};$mutex.Dispose()
  # Do not release persistent protection on crashes or closed service windows.
 }
}
if($Library){return}
InitializeRouterFolder
if($Service){RunRouterService;exit}
