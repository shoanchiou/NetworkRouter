$ErrorActionPreference='Stop'
try {
 $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
 if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '移除開機保護需要管理員權限。'}
 $native=Join-Path $PSScriptRoot 'paid-network-guard.exe'
 $reply=& $native uninstall 2>&1
 if($LASTEXITCODE -ne 0){throw ($reply -join [Environment]::NewLine)}
 # Tray startup is an independent user preference and is not changed here.
 Write-Output '計費有線開機保護已移除；程式限制與通知區啟動設定保留'
}catch{Write-Error $_.Exception.Message -ErrorAction Continue;exit 1}
