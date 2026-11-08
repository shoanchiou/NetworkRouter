param([Parameter(Mandatory=$true)][string]$Backend)
$ErrorActionPreference='Stop'
$root=Join-Path $PSScriptRoot ('fixture-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
function Check($Condition,[string]$Message){if(-not $Condition){throw "FAIL $Message"};Write-Host "PASS $Message"}
try {
 . (Join-Path $Backend 'Router.ps1') -Library -DataRoot $root
 $state=NewRouterState
 Check ((@($state.Apps).Count -eq 0) -and (@($state.Rules).Count -eq 0) -and (@(ReadNetworkCatalog).Count -eq 0) -and (-not $state.Enabled)) 'fresh install has empty apps and catalog, routing disabled'
 SaveRouterState $state
 Check (@(ReadNetworkCatalog).Count -eq 0) 'empty saved catalog remains valid'
 $fixture=@([pscustomobject]@{InterfaceGuid='11111111-1111-4111-8111-111111111111';Name='fixture adapter';InterfaceDescription='fixture'})
 $catalog=@(SyncNetworkCatalog $fixture)
 Check (($catalog.Count -eq 1) -and ($catalog[0].Id -eq 1) -and ($catalog[0].Guid -eq '11111111-1111-4111-8111-111111111111')) 'discovery creates first stable ID'
 Check (@(ReadNetworkCatalog).Count -eq 1) 'catalog survives reload'
 Remove-Item -LiteralPath (Join-Path $root 'network-adapters.json') -Force
 $old=NewRouterState
 $old.NetworkCatalog=@([pscustomobject]@{Id=1;Guid='11111111-1111-4111-8111-111111111111';Name='old 1'},[pscustomobject]@{Id=2;Guid='22222222-2222-4222-8222-222222222222';Name='old 2'},[pscustomobject]@{Id=3;Guid='33333333-3333-4333-8333-333333333333';Name='old 3'})
 $old.Apps=@([pscustomobject]@{Id=2;Name='old app';Paths=@('C:\Fixture\old.exe');Pattern='^C:\\Fixture\\old\.exe$'})
 $old.Rules=@([pscustomobject]@{Id=2;Mode='Allow';Networks=@(1,3);Priority=@(3,1);Enabled=$true})
 SaveRouterState $old
 $loaded=ReadRouterState;$catalog=@(ReadNetworkCatalog)
 Check (($catalog.Count -eq 3) -and ($catalog[2].Id -eq 3) -and ($loaded.Rules[0].Networks -join ',' -eq '1,3') -and ($loaded.Apps[0].Paths[0] -eq 'C:\Fixture\old.exe')) 'upgrade preserves saved IDs, apps and rules'
 $new=@(SyncNetworkCatalog @([pscustomobject]@{InterfaceGuid='44444444-4444-4444-8444-444444444444';Name='new adapter';InterfaceDescription='fixture'}))
 Check (($new.Count -eq 4) -and ($new[3].Id -eq 4)) 'migration assigns new ID without reusing old IDs'
 $sources=Get-ChildItem -LiteralPath $Backend -File | Where-Object {$_.Extension -in '.ps1','.json'}
 Check (@($sources | Select-String -Pattern '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}').Count -eq 0) 'no fixed adapter GUID in backend scripts'
 Check (@($sources | Select-String -Pattern '(?<![A-Za-z])[A-Za-z]:\\').Count -eq 0) 'no hardcoded drive paths in backend scripts'
 Check (@(Get-ChildItem -LiteralPath $Backend -Filter '*.tsv' -File).Count -eq 0) 'no app catalog bundled'
 $native=Join-Path (Split-Path -Parent $Backend) 'native'
 Check ((Test-Path -LiteralPath (Join-Path $native 'router-guard.cpp')) -and (Test-Path -LiteralPath (Join-Path $native 'paid-network-guard.cpp'))) 'native sources present'
 Check (@(Get-ChildItem -LiteralPath $Backend -Filter '*.cpp' -File).Count -eq 0) 'native sources kept outside runtime backend'
 Check (@(Get-ChildItem -LiteralPath $native -Filter '*.cpp' -File).Count -eq 2) 'only required native sources included'

} finally {
 if((Split-Path -Parent $root) -ne $PSScriptRoot -or (Split-Path -Leaf $root) -notmatch '^fixture-[0-9a-f]{32}$'){throw 'Unsafe fixture path'}
 if(Test-Path -LiteralPath $root){Remove-Item -LiteralPath $root -Recurse -Force}
}
