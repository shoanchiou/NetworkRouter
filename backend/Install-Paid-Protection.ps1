param([switch]$NoLaunch,[string]$Guid='')
$ErrorActionPreference='Stop'
$utf8=New-Object Text.UTF8Encoding($false)
$logRoot=Join-Path $env:LOCALAPPDATA 'UnifiedNetworkRouter'
[void][IO.Directory]::CreateDirectory($logRoot)
try {
 $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '請以管理員身分重新開啟 Network Router，再安裝保護。'}
 $native=Join-Path $PSScriptRoot 'paid-network-guard.exe'
 $gui=Join-Path $PSScriptRoot 'NetworkRouter.exe'
 foreach($file in @($native,$gui)){if(-not (Test-Path -LiteralPath $file -PathType Leaf)){throw '檔案不完整，請解壓全部檔案後重試。'}}
 $parsedGuid=[Guid]::Empty
 if(-not [Guid]::TryParse($Guid,[ref]$parsedGuid) -or $parsedGuid -eq [Guid]::Empty){throw '保護網卡 GUID 無效。'}
 $guid=$parsedGuid.ToString()
 . (Join-Path $PSScriptRoot 'Router.ps1') -Library -DataRoot $logRoot
 $savedState=ReadRouterState;$trayAutoStart=if($savedState){[bool](GetRouterOptions $savedState).TrayAutoStart}else{$true}
 $nativeOutput=& $native install $guid 2>&1
 if($LASTEXITCODE -ne 0){throw ('開機保護安裝失敗 '+($nativeOutput -join [Environment]::NewLine))}
 SetRouterTrayStartup $trayAutoStart
 $statusText=& $native status
 if($LASTEXITCODE -ne 0){throw ('無法確認開機保護狀態 '+($statusText -join ' '))}
 $status=($statusText -join [Environment]::NewLine) | ConvertFrom-Json
 $reportedGuid=[Guid]::Empty
 if(-not $status.Installed -or -not $status.Locked -or -not $status.ServiceRunning -or -not [Guid]::TryParse([string]$status.Guid,[ref]$reportedGuid) -or $reportedGuid -ne $parsedGuid){throw '保護尚未完整啟用或網卡不符，請查看記錄並重試。'}
 [IO.File]::AppendAllText((Join-Path $logRoot 'paid-protection.log'),((Get-Date -Format o)+' 已安裝開機保護 所選網卡目前禁止 通知區登入啟動依設定處理'+[Environment]::NewLine),$utf8)
 if(-not $NoLaunch){Start-Process -FilePath $gui -ArgumentList '--tray' -WindowStyle Hidden}
 Write-Output '開機保護已啟用 所選網卡目前禁止 每次重開機需要手動允許'
 exit 0
}catch {
 $message=$_.Exception.Message
 try{[IO.File]::AppendAllText((Join-Path $logRoot 'paid-protection.log'),((Get-Date -Format o)+' '+$message+[Environment]::NewLine),$utf8)}catch{}
 Write-Error $message -ErrorAction Continue
 exit 1
}
