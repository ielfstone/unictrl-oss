#ifndef AppVersion
  #define AppVersion "0.7.2"
#endif
[Setup]
AppId={{91F76CD4-F102-4F50-95BA-5D6682CC32AF}
AppName=Unictrl
AppVersion={#AppVersion}
AppPublisher=ielfstone / Unictrl contributors
AppPublisherURL=https://github.com/ielfstone/unictrl-oss
DefaultDirName={localappdata}\Programs\Unictrl
PrivilegesRequired=lowest
UsePreviousAppDir=yes
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Unictrl.exe
OutputDir={#OutputDir}
OutputBaseFilename=Unictrl-{#AppVersion}-windows-x64-Setup
SetupIconFile={#PayloadDir}\Assets\Unictrl.ico
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
AppMutex=Local\UnictrlSingleInstance
WizardStyle=modern
[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Unictrl.portable,Data\*"
[InstallDelete]
Type: files; Name: "{app}\Unictrl.portable"
[Icons]
Name: "{userprograms}\Unictrl"; Filename: "{app}\Unictrl.exe"; WorkingDir: "{app}"
[Run]
Filename: "{app}\Unictrl.exe"; Description: "Open Unictrl"; Flags: nowait postinstall skipifsilent
[Code]
var MigrationPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  MigrationPage := CreateInputDirPage(wpSelectDir, 'Keep portable settings',
    'Optional: select your old portable Unictrl folder',
    'Choose the folder containing Unictrl.exe and Data to keep pairing and preferences. Leave blank for a new installation. Your old folder is preserved.', False, '');
  MigrationPage.Add('Old portable folder:');
  MigrationPage.Values[0] := ExpandConstant('{param:PORTABLESOURCE|}');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = MigrationPage.ID) and (ExpandConstant('{param:UPDATE|0}') = '1');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = MigrationPage.ID) and (MigrationPage.Values[0] <> '') then begin
    Result := FileExists(AddBackslash(MigrationPage.Values[0]) + 'Data\settings.json');
    if not Result then MsgBox('This folder has no Data\settings.json. Select the old portable folder, or leave this field blank.', mbError, MB_OK);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var Source, Dest, Backup: String;
begin
  Result := '';
  Source := MigrationPage.Values[0];
  if Source = '' then exit;
  Source := AddBackslash(Source) + 'Data\settings.json';
  Dest := ExpandConstant('{localappdata}\Unictrl\settings.json');
  if not FileExists(Source) then begin Result := 'Portable settings were not found. No settings changed.'; exit; end;
  if CompareText(ExpandFileName(Source), ExpandFileName(Dest)) = 0 then exit;
  ForceDirectories(ExtractFileDir(Dest));
  if FileExists(Dest) then begin
    Backup := Dest + '.before-migration-' + GetDateTimeString('yyyymmddhhnnss', '', '');
    if not FileCopy(Dest, Backup, True) then begin Result := 'Could not back up existing settings.'; exit; end;
  end;
  if not FileCopy(Source, Dest, False) then Result := 'Could not import portable settings. Your old portable folder is preserved.';
end;
