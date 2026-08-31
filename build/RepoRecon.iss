; RepoRecon Inno Setup script.
; Version and iscc path are supplied by build/build-release.ps1 via /D parameters:
;   AppVersion   - e.g. 1.0.0 (read from RepoRecon.App.csproj <Version>, single source of truth)
;   PublishDir   - path to the dotnet publish output to package
;   OutputDir    - dist/ directory to write RepoRecon-Setup-{version}.exe into

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\src\RepoRecon.App\bin\Release\net8.0-windows\win-x64\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
; Fixed AppId GUID -- generated once for this project. Do NOT change this value;
; changing it breaks upgrade detection and causes a duplicate Programs entry.
AppId={{B9B2450B-C12B-4B6B-A9A2-8C83030C3514}
AppName=RepoRecon
AppVersion={#AppVersion}
AppPublisher=RepoRecon
DefaultDirName={autopf}\RepoRecon
DefaultGroupName=RepoRecon
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
UninstallDisplayIcon={app}\RepoRecon.App.exe
OutputBaseFilename=RepoRecon-Setup-{#AppVersion}
OutputDir={#OutputDir}
SetupIconFile=..\src\RepoRecon.App\Assets\reporecon.ico
WizardStyle=modern

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; GroupDescription: "추가 아이콘:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\RepoRecon"; Filename: "{app}\RepoRecon.App.exe"
Name: "{autodesktop}\RepoRecon"; Filename: "{app}\RepoRecon.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\RepoRecon.App.exe"; Description: "RepoRecon 실행"; Flags: nowait postinstall skipifsilent

[Code]
function GetRepoReconDataDir(): String;
begin
  Result := ExpandConstant('{localappdata}\RepoRecon');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
  Response: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := GetRepoReconDataDir();
    if DirExists(DataDir) then
    begin
      { Default button is "아니오" (No) -- MB_DEFBUTTON2 -- per design §13.3:
        keeping settings/index/tokens on removal is the safer default. }
      Response := MsgBox(
        '설정, 인덱스, 저장된 토큰도 함께 삭제하시겠습니까?' + #13#10 +
        '(기본값은 "아니오"이며, 재설치 시 스캔 루트와 프로파일이 유지됩니다.)',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2);
      if Response = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;
