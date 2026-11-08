function GetPickerVersion([string]$Name) {
 $parsed=$null
 if($Name -match '^app-(\d+(?:\.\d+){1,3})(?:[-+].*)?$' -and [version]::TryParse($matches[1],[ref]$parsed)){return $parsed}
 return [version]'0.0'
}
function ResolvePickerExecutable([string]$Value) {
    if([string]::IsNullOrWhiteSpace($Value)){return $null}
    $value=[Environment]::ExpandEnvironmentVariables($Value.Trim())
    if($value -match '^"([^"\r\n]+\.exe)"'){$value=$matches[1]}
    elseif($value -match '^(.+\.exe)\s*,\s*-?\d+$'){$value=$matches[1]}
    if([IO.Path]::GetExtension($value) -ine '.exe'){return $null}
    if(-not [IO.Path]::IsPathRooted($value)){return $null}
    if(-not (Test-Path -LiteralPath $value -PathType Leaf)){return $null}
    $filename=[IO.Path]::GetFileName($value)
    if($filename -match '^(unins.*|uninstall.*|msiexec|rundll32|explorer|applicationframehost|cmd|powershell|pwsh|wscript|cscript|dllhost)\.exe$'){return $null}
    return [IO.Path]::GetFullPath($value)
}

function GetPickerApplications {
    $entries=New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    $appNames=@{}
    $pending=New-Object 'Collections.Generic.List[object]'
    function AddPickerEntry([string]$Name,[string]$Path,[string]$Source) {
        $resolved=ResolvePickerExecutable $Path
        if(-not $resolved){return}
        $name=($Name -replace '[\t\r\n]',' ').Trim()
        if(-not $name){$name=[IO.Path]::GetFileNameWithoutExtension($resolved)}
        if($entries.ContainsKey($resolved)) {
            $entry=$entries[$resolved]
            if($entry.Source -notlike ('*'+$Source+'*')){$entry.Source+='  '+$Source}
            return
        }
        $entries.Add($resolved,[pscustomobject]@{Name=$name;Path=$resolved;Source=$Source;Available=$true})
    }

    # Read the same virtual application folder that Windows exposes as AppsFolder.
    $shell=$null
    $folder=$null
    $items=$null
    try {
        $shell=New-Object -ComObject Shell.Application
        $folder=$shell.Namespace('shell:AppsFolder')
        if($folder) {
            $items=$folder.Items()
            for($index=0;$index -lt $items.Count;$index++) {
                $item=$null
                try {
                    $item=$items.Item($index)
                    $name=[string]$item.Name
                    $appId=[string]$item.ExtendedProperty('System.AppUserModel.ID')
                    if($appId){$appNames[$appId]=$name}
                    $target=[string]$item.ExtendedProperty('System.Link.TargetParsingPath')
                    $resolved=ResolvePickerExecutable $target
                    if(-not $resolved){$resolved=ResolvePickerExecutable ([string]$item.Path)}
                    if($resolved){AddPickerEntry $name $resolved 'Windows 應用程式'}
                    else{[void]$pending.Add([pscustomobject]@{Name=$name;AppId=$appId})}
                } catch {
                    # One unavailable shell item must not hide the other applications.
                } finally {if($item){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($item)}}
            }
        }
    } catch {
        # Start menu and application registration remain available as fallbacks.
    } finally {
        foreach($com in @($items,$folder,$shell)){if($com){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com)}}
    }
    try {Get-StartApps -ErrorAction Stop | ForEach-Object {if($_.AppID){$appNames[[string]$_.AppID]=[string]$_.Name}}} catch {}

    $shortcutShell=$null
    try {
        $shortcutShell=New-Object -ComObject WScript.Shell
        $roots=@([Environment]::GetFolderPath('StartMenu'),[Environment]::GetFolderPath('CommonStartMenu'),[Environment]::GetFolderPath('Desktop'),[Environment]::GetFolderPath('CommonDesktopDirectory')) | Select-Object -Unique
        foreach($root in $roots) {
            if(-not $root -or -not (Test-Path -LiteralPath $root)){continue}
            foreach($link in (Get-ChildItem -LiteralPath $root -Filter '*.lnk' -Recurse -File -ErrorAction SilentlyContinue)) {
                $shortcut=$null
                try {
                    $shortcut=$shortcutShell.CreateShortcut($link.FullName)
                    $target=[string]$shortcut.TargetPath
                    # Electron shortcuts sometimes point at their updater rather than the app.
                    if([IO.Path]::GetFileName($target) -ieq 'Update.exe') {
                        if($shortcut.Arguments -match '--processStart\s+"?([^"\s]+\.exe)') {
                            $executableName=[IO.Path]::GetFileName($matches[1])
                            $base=[IO.Path]::GetDirectoryName($target)
                            $version=Get-ChildItem -LiteralPath $base -Directory -Filter 'app-*' -ErrorAction SilentlyContinue | Sort-Object @{Expression={GetPickerVersion $_.Name};Descending=$true},@{Expression={$_.Name};Descending=$true} | Where-Object {Test-Path -LiteralPath (Join-Path $_.FullName $executableName)} | Select-Object -First 1
                            if($version){$target=Join-Path $version.FullName $executableName}else{continue}
                        } else {continue}
                    }
                    AddPickerEntry $link.BaseName $target '開始選單與捷徑'
                } catch {} finally {if($shortcut){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)}}
            }
        }
    } catch {} finally {if($shortcutShell){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcutShell)}}

    foreach($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths','HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths')) {
        foreach($key in (Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            try {
                $path=[string]$key.GetValue('')
                $resolved=ResolvePickerExecutable $path
                if(-not $resolved){continue}
                $name=[Diagnostics.FileVersionInfo]::GetVersionInfo($resolved).ProductName
                AddPickerEntry $name $resolved '已安裝程式'
            } catch {} finally {if($key){$key.Close()}}
        }
    }
    foreach($root in @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        foreach($key in (Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
            try {
                if($key.GetValue('SystemComponent') -eq 1){continue}
                $name=[string]$key.GetValue('DisplayName')
                if(-not $name -or $name -match '(?i)redistributable|runtime|sdk|driver|驅動|執行階段'){continue}
                AddPickerEntry $name ([string]$key.GetValue('DisplayIcon')) '已安裝程式'
            } catch {} finally {if($key){$key.Close()}}
        }
    }

    try {
        foreach($package in (Get-AppxPackage -ErrorAction Stop | Where-Object {-not $_.IsFramework -and -not $_.IsResourcePackage})) {
            try {
                $manifestPath=Join-Path $package.InstallLocation 'AppxManifest.xml'
                [xml]$manifest=Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop
                foreach($application in $manifest.SelectNodes('//*[local-name()="Applications"]/*[local-name()="Application"]')) {
                    $executable=$application.GetAttribute('Executable')
                    if(-not $executable){continue}
                    $appId=$package.PackageFamilyName+'!'+$application.GetAttribute('Id')
                    $name=$appNames[$appId]
                    if(-not $name) {
                        $visual=$application.SelectSingleNode('./*[local-name()="VisualElements"]')
                        if($visual){$name=$visual.GetAttribute('DisplayName')}
                    }
                    if(-not $name -or $name -like 'ms-resource:*'){$name=$package.Name}
                    AddPickerEntry $name (Join-Path $package.InstallLocation $executable) 'Windows 應用程式'
                }
            } catch {}
        }
    } catch {}

    foreach($process in (Get-Process -ErrorAction SilentlyContinue)) {
        try {
            $path=$process.Path
            if(-not $path){continue}
            if($process.MainWindowHandle -eq 0 -and $path.StartsWith($env:WINDIR,[StringComparison]::OrdinalIgnoreCase)){continue}
            $name=$process.FileVersionInfo.ProductName
            if(-not $name){$name=$process.ProcessName}
            AddPickerEntry $name $path '正在執行'
        } catch {} finally {$process.Dispose()}
    }
    foreach($app in $script:apps){if($app.Path){AddPickerEntry $app.Name $app.Path '已加入選單'}}

    $all=@($entries.Values)
    $knownNames=@{}
    foreach($entry in $all){$knownNames[$entry.Name]=$true}
    foreach($entry in $pending) {
        if($entry.Name -and -not $knownNames.ContainsKey($entry.Name)) {
            $all+=[pscustomobject]@{Name=$entry.Name;Path='';Source='Windows 應用程式';Available=$false}
            $knownNames[$entry.Name]=$true
        }
    }
    return @($all | Sort-Object Name,Path)
}

function SelectPickerApplications {
    Add-Type -AssemblyName System.Windows.Forms
    $form=New-Object Windows.Forms.Form
    $form.Text='新增程式 直接點選應用程式'
    $form.Width=830
    $form.Height=640
    $form.MinimumSize=New-Object Drawing.Size(640,460)
    $form.StartPosition='CenterScreen'
    $form.AutoScaleMode='Dpi'
    $form.Font=New-Object Drawing.Font('Microsoft JhengHei UI',11)
    $form.ShowIcon=$false

    $layout=New-Object Windows.Forms.TableLayoutPanel
    $layout.Dock='Fill'
    $layout.ColumnCount=1
    $layout.RowCount=5
    [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute',42)))
    [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute',38)))
    [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Percent',100)))
    [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute',50)))
    [void]$layout.RowStyles.Add((New-Object Windows.Forms.RowStyle('Absolute',42)))
    $layout.Padding=New-Object Windows.Forms.Padding(12)
    $form.Controls.Add($layout)

    $intro=New-Object Windows.Forms.Label
    $intro.Text='勾選應用程式名稱 再按加入所選程式 可以一次選多個'
    $intro.Dock='Fill'
    $intro.TextAlign='MiddleLeft'
    $layout.Controls.Add($intro,0,0)
    $search=New-Object Windows.Forms.TextBox
    $search.Dock='Fill'
    $search.AccessibleName='搜尋應用程式名稱'
    $layout.Controls.Add($search,0,1)

    $list=New-Object Windows.Forms.ListView
    $list.Dock='Fill'
    $list.View='Details'
    $list.CheckBoxes=$true
    $list.FullRowSelect=$true
    $list.MultiSelect=$true
    $list.HideSelection=$false
    $list.ShowItemToolTips=$true
    [void]$list.Columns.Add('應用程式',340)
    [void]$list.Columns.Add('來源',210)
    [void]$list.Columns.Add('狀態',220)
    $layout.Controls.Add($list,0,2)

    $status=New-Object Windows.Forms.Label
    $status.Dock='Fill'
    $status.TextAlign='MiddleLeft'
    $status.Text='正在讀取 Windows 應用程式清單'
    $layout.Controls.Add($status,0,3)
    $buttons=New-Object Windows.Forms.FlowLayoutPanel
    $buttons.Dock='Fill'
    foreach($label in @('加入所選程式','重新整理','找不到時選檔案','取消')) {
        $button=New-Object Windows.Forms.Button
        $button.Text=$label
        $button.AutoSize=$true
        [void]$buttons.Controls.Add($button)
    }
    $layout.Controls.Add($buttons,0,4)
    $context=@{Entries=@();Checked=@{};Selection=@();Manual=$false;Rendering=$false;Loading=$false}
    $render={
        $context.Rendering=$true
        $list.BeginUpdate()
        try {
            $list.Items.Clear()
            $query=$search.Text.Trim()
            foreach($entry in $context.Entries) {
                if($query -and $entry.Name.IndexOf($query,[StringComparison]::OrdinalIgnoreCase) -lt 0){continue}
                $item=New-Object Windows.Forms.ListViewItem($entry.Name)
                [void]$item.SubItems.Add($entry.Source)
                $state=if($entry.Available){'可以加入'}else{'先開啟程式再重新整理'}
                $existing=$null
                if($entry.Available){
                    $builtin=FindBuiltin $entry.Path
                    $existing=$script:apps | Where-Object {($builtin -gt 0 -and $_.Id -eq $builtin) -or ($_.Path -and [string]::Equals($_.Path,$entry.Path,[StringComparison]::OrdinalIgnoreCase))} | Select-Object -First 1
                }
                if($existing){$state='已在選單 '+$existing.Id}
                [void]$item.SubItems.Add($state)
                $item.Tag=$entry
                $item.ToolTipText=$entry.Path
                if(-not $entry.Available){$item.ForeColor=[Drawing.Color]::Gray}
                $item.Checked=$entry.Available -and $context.Checked.ContainsKey($entry.Path)
                [void]$list.Items.Add($item)
            }
            $status.Text='搜尋可直接輸入程式名稱  找不到的程式可以先開啟再按重新整理'+[Environment]::NewLine+'已勾選 '+$context.Checked.Count+' 個  新增後仍需回主選單開啟限制'
        } finally {$list.EndUpdate();$context.Rendering=$false}
    }
    $load={
        if($context.Loading){return}
        $context.Loading=$true
        $form.UseWaitCursor=$true
        $status.Text='正在讀取 Windows 應用程式清單'
        $form.Refresh()
        try {$context.Entries=@(GetPickerApplications);& $render}
        catch {$status.Text='無法讀取清單  可以重新整理或選擇檔案';Write-Host $_.Exception.Message -ForegroundColor Yellow}
        finally {$form.UseWaitCursor=$false;$context.Loading=$false}
    }
    $search.Add_TextChanged({& $render})
    $list.Add_DoubleClick({
        if($list.SelectedItems.Count -eq 1 -and $list.SelectedItems[0].Tag.Available){$list.SelectedItems[0].Checked=-not $list.SelectedItems[0].Checked}
    })
    $list.Add_ItemCheck({
        param($sender,$event)
        if($context.Rendering){return}
        $entry=$list.Items[$event.Index].Tag
        if(-not $entry.Available){$event.NewValue=[Windows.Forms.CheckState]::Unchecked;return}
        if($event.NewValue -eq [Windows.Forms.CheckState]::Checked){$context.Checked[$entry.Path]=$entry}else{$context.Checked.Remove($entry.Path)}
        $status.Text='已勾選 '+$context.Checked.Count+' 個  按加入所選程式完成新增'
    })
    $buttons.Controls[0].Add_Click({
        if($context.Checked.Count -eq 0){$status.Text='請先勾選要加入的程式';return}
        $context.Selection=@($context.Checked.Values | Sort-Object Name)
        $form.DialogResult=[Windows.Forms.DialogResult]::OK
        $form.Close()
    })
    $buttons.Controls[1].Add_Click({& $load})
    $buttons.Controls[2].Add_Click({$context.Manual=$true;$form.DialogResult=[Windows.Forms.DialogResult]::OK;$form.Close()})
    $buttons.Controls[3].Add_Click({$form.DialogResult=[Windows.Forms.DialogResult]::Cancel;$form.Close()})
    $form.CancelButton=$buttons.Controls[3]
    $form.Add_Shown({& $load;$search.Focus() | Out-Null})
    try {
        if($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK){return}
        if($context.Manual) {
            $dialog=New-Object Windows.Forms.OpenFileDialog
            $dialog.Title='選擇其他執行檔'
            $dialog.Filter='執行檔|*.exe'
            $dialog.Multiselect=$true
            try {
                if($dialog.ShowDialog() -eq [Windows.Forms.DialogResult]::OK){
                    foreach($path in $dialog.FileNames){[pscustomobject]@{Name=[IO.Path]::GetFileNameWithoutExtension($path);Path=$path}}
                }
            } finally {$dialog.Dispose()}
        } else {return $context.Selection}
    } finally {$form.Dispose()}
}
