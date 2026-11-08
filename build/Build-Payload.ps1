param(
 [Parameter(Mandatory=$true)][string]$DependencyRoot,
 [string]$OutputRoot='',
 [string]$NuGetConfig='',
 [string]$PackagesPath=''
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(-not $OutputRoot){$OutputRoot=Join-Path $repo 'artifacts'}
if(-not $NuGetConfig){$NuGetConfig=Join-Path $repo 'NuGet.Config'}
$NuGetConfig=(Resolve-Path -LiteralPath $NuGetConfig).Path
if($PackagesPath){$env:NUGET_PACKAGES=(Resolve-Path -LiteralPath $PackagesPath).Path}
function RequireHash($path,$hash){
 if(-not (Test-Path -LiteralPath $path -PathType Leaf)){throw "Missing dependency: $path"}
 $stream=[IO.File]::OpenRead((Resolve-Path -LiteralPath $path));$sha=[Security.Cryptography.SHA256]::Create()
 try{$actual=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
 if($actual -cne $hash){throw "Incorrect dependency hash: $path"}
}
$core=Join-Path $DependencyRoot 'core\mihomo.exe'
$mihomoSource=Join-Path $DependencyRoot 'licenses\mihomo-v1.19.32-source.zip'
$bindSource=Join-Path $DependencyRoot 'licenses\BindRouter-v1.2.0-source.zip'
RequireHash $core '04F8D7FC2B314771E1ECBF15951D59F0E1CB7914503C88CFB6972FF6A824E314'
RequireHash $mihomoSource '135F16D1E309F8C0CE2303C86FD38A38624A229E7185CEB74D1D3A4F0D4B66F1'
RequireHash $bindSource 'CEE5BFB6379E178181C069F6EFDB0D658ECED927E2D6AF9D8D94C3DDBD69C294'
if(-not (Get-Command cl.exe -ErrorAction SilentlyContinue)){throw 'Run from a Visual Studio 2022 x64 Native Tools environment.'}
if(-not (Get-Command dotnet.exe -ErrorAction SilentlyContinue)){throw '.NET SDK is required.'}
$payload=Join-Path $OutputRoot ('payload-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($payload)
[void][IO.Directory]::CreateDirectory($OutputRoot)
$env:DOTNET_CLI_HOME=Join-Path $OutputRoot '.cli-state'
$env:APPDATA=Join-Path $OutputRoot '.appdata'
[void][IO.Directory]::CreateDirectory($env:APPDATA)
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
$publish=Join-Path $OutputRoot ('gui-publish-'+[guid]::NewGuid().ToString('N'))
& dotnet publish (Join-Path $repo 'gui-source\RouterUI.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true '-p:TargetPlatformMoniker=Windows%2CVersion=7.0' -p:TargetPlatformDisplayName=Windows "-p:RestoreConfigFile=$NuGetConfig" -o $publish
if($LASTEXITCODE -ne 0){throw 'GUI publish failed'}
$gui=Join-Path $publish 'NetworkRouter.exe'
if(-not (Test-Path -LiteralPath $gui)){throw 'Published GUI executable missing'}
Copy-Item -LiteralPath $gui -Destination $payload
foreach($name in @('Router.ps1','GuiBridge.ps1','AppPicker.ps1','Menu.ps1','OptionsSchema.json','Install-Paid-Protection.ps1','Remove-Paid-Protection.ps1')){
 Copy-Item -LiteralPath (Join-Path $repo ('backend\'+$name)) -Destination $payload
}
foreach($name in @('router-guard','paid-network-guard')){
 $exe=Join-Path $payload ($name+'.exe');$obj=Join-Path $payload ($name+'.obj')
 & cl.exe /nologo /EHsc /std:c++17 /O2 /utf-8 /MT (Join-Path $repo ('native\'+$name+'.cpp')) "/Fo$obj" "/Fe$exe"
 if($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)){throw "Native build failed: $name"}
 Remove-Item -LiteralPath $obj -Force
}
& (Join-Path $payload 'paid-network-guard.exe') --selftest
if($LASTEXITCODE -ne 0){throw 'Native paid guard self-test failed'}
[void][IO.Directory]::CreateDirectory((Join-Path $payload 'core'))
[void][IO.Directory]::CreateDirectory((Join-Path $payload 'licenses'))
Copy-Item -LiteralPath $core -Destination (Join-Path $payload 'core\mihomo.exe')
Copy-Item -LiteralPath $mihomoSource,$bindSource -Destination (Join-Path $payload 'licenses')
Get-ChildItem -LiteralPath (Join-Path $repo 'licenses') -Filter '*.txt' -File | Copy-Item -Destination (Join-Path $payload 'licenses')
Copy-Item -LiteralPath (Join-Path $repo 'installer\Uninstall-Cleanup.ps1') -Destination $payload
$hashes=[ordered]@{}
foreach($key in @('Router.ps1','router-guard.exe','paid-network-guard.exe','core\mihomo.exe')){
 $file=Join-Path $payload $key;$stream=[IO.File]::OpenRead($file);$sha=[Security.Cryptography.SHA256]::Create()
 try{$hashes[$key]=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
}
$marker=[ordered]@{Product='NetworkRouter';Version='1.0.0';Hashes=$hashes}
[IO.File]::WriteAllText((Join-Path $payload 'networkrouter-install.json'),($marker|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
$checks=Start-Process -FilePath (Join-Path $payload 'NetworkRouter.exe') -ArgumentList '--check-interface' -PassThru -Wait -WindowStyle Hidden
if($checks.ExitCode -ne 0){throw "GUI interface check failed: $($checks.ExitCode)"}
Write-Output "Payload: $payload"
