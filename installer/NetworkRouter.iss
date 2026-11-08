#define AppName "NetworkRouter"
#define AppVersion "1.0.0"
#ifndef PayloadDir
  #error PayloadDir must name a verified complete build payload
#endif
[Setup]
AppId={{E1C55F68-83B5-45D7-A600-3B1959982505}
AppName=Network Router 網路分流管理
AppVersion={#AppVersion}
DefaultDirName={autopf}\NetworkRouter
DefaultGroupName=NetworkRouter
DisableDirPage=yes
OutputBaseFilename=NetworkRouter-Setup-1.0.0-x64
OutputDir=..\dist
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
Compression=lzma2
SolidCompression=yes
UninstallDisplayIcon={app}\NetworkRouter.exe
[Languages]
Name: "chinesetraditional"; MessagesFile: "compiler:Languages\ChineseTraditional.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Tasks]
Name: "desktopicon"; Description: "建立桌面捷徑"; Flags: unchecked
[Icons]
Name: "{autoprograms}\NetworkRouter"; Filename: "{app}\NetworkRouter.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\NetworkRouter"; Filename: "{app}\NetworkRouter.exe"; WorkingDir: "{app}"; Tasks: desktopicon
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{autopf}\NetworkRouter')) <> 0 then
    Result := '此版本必須安裝在受保護的預設資料夾。';
end;
function InitializeUninstall(): Boolean;
var ExitCode: Integer; Params, ResultFile, Detail: String; Lines: TArrayOfString;
begin
  Result := False;
  ResultFile := ExpandConstant('{tmp}\networkrouter-uninstall-result.json');
  DeleteFile(ResultFile);
  Params := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\Uninstall-Cleanup.ps1') + '" -InstallRoot "' + ExpandConstant('{app}') + '" -ResultPath "' + ResultFile + '"';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then begin
    MsgBox('無法啟動網路恢復，已保留程式檔案。', mbError, MB_OK); exit;
  end;
  if ExitCode <> 0 then begin
    Detail := '';
    if LoadStringsFromFile(ResultFile, Lines) and (GetArrayLength(Lines) > 0) then Detail := Lines[0] else Detail := '無法取得恢復詳細資料。';
    MsgBox('網路恢復尚未完成，已保留程式檔案。' + #13#10 + Detail, mbError, MB_OK); exit;
  end;
  Result := True;
end;
