#define MyAppName "Trazio Asistente Reunión"
#define MyAppExeName "Trazio.AsistenteReunion.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.2.0-beta.7"
#endif
#ifndef MyReleaseSequence
  #define MyReleaseSequence 8
#endif
#ifndef MyAppGuid
  #define MyAppGuid "8C7AF6E0-31F1-4E2E-BF21-8A9AC6D1DDF1"
#endif
#ifndef MyPayloadDir
  #define MyPayloadDir "..\artifacts\publish"
#endif
#ifndef MyPayloadManifest
  #define MyPayloadManifest "..\artifacts\publish-manifest.json"
#endif
#ifndef MyOutputDir
  #define MyOutputDir "..\artifacts\installer"
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "Trazio-Asistente-Reunion-v" + MyAppVersion + "-Setup"
#endif
#ifndef MyDefaultDirName
  #define MyDefaultDirName "{localappdata}\Programs\Trazio Asistente Reunion"
#endif
#ifndef MyStartMenuGroup
  #define MyStartMenuGroup MyAppName
#endif
#ifndef MyShortcutName
  #error MyShortcutName must be defined by build-installer.ps1
#endif
#ifndef MyInstallerRegistrySubkey
  #define MyInstallerRegistrySubkey "Software\Trazio\AsistenteReunion\Installer"
#endif
#ifndef MyAppMutex
  #define MyAppMutex "Trazio.AsistenteReunion.AppRunning.v1"
#endif
#ifndef MySetupMutex
  #define MySetupMutex "Trazio.AsistenteReunion.Setup.v1"
#endif

[Setup]
AppId={{{#MyAppGuid}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Trazio
DefaultDirName={#MyDefaultDirName}
DefaultGroupName={#MyStartMenuGroup}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#MyOutputDir}
OutputBaseFilename={#MyOutputBaseFilename}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
AppMutex={#MyAppMutex}
SetupMutex={#MySetupMutex}
UsePreviousAppDir=yes
UninstallFilesDir={app}
UninstallLogMode=append
UninstallDisplayIcon={app}\versions\{#MyAppVersion}\{#MyAppExeName}
SetupLogging=yes

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Files]
Source: "{#MyPayloadDir}\*"; DestDir: "{app}\versions\{#MyAppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyPayloadManifest}"; DestDir: "{app}\versions\{#MyAppVersion}"; DestName: "payload-manifest.json"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyShortcutName}"; Filename: "{app}\versions\{#MyAppVersion}\{#MyAppExeName}"; WorkingDir: "{app}\versions\{#MyAppVersion}"
Name: "{autodesktop}\{#MyShortcutName}"; Filename: "{app}\versions\{#MyAppVersion}\{#MyAppExeName}"; WorkingDir: "{app}\versions\{#MyAppVersion}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos adicionales:"

[Registry]
Root: HKCU; Subkey: "{#MyInstallerRegistrySubkey}"; ValueType: dword; ValueName: "ReleaseSequence"; ValueData: "{#MyReleaseSequence}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "{#MyInstallerRegistrySubkey}"; ValueType: string; ValueName: "Version"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "{#MyInstallerRegistrySubkey}"; ValueType: string; ValueName: "ActivePayload"; ValueData: "{app}\versions\{#MyAppVersion}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\versions\{#MyAppVersion}\{#MyAppExeName}"; Description: "Abrir {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  CurrentReleaseSequence = {#MyReleaseSequence};
  CurrentVersion = '{#MyAppVersion}';
  InstallerRegistrySubkey = '{#MyInstallerRegistrySubkey}';
  LegacyUninstallSubkey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{{#MyAppGuid}}_is1';

var
  DetectedInstallMode: String;

function TryMapKnownVersion(const VersionText: String; var Sequence: Integer): Boolean;
begin
  Result := True;
  if VersionText = CurrentVersion then
    Sequence := CurrentReleaseSequence
  else if VersionText = '0.1.1-mvp' then
    Sequence := 1
  else if VersionText = '0.2.0-beta.1' then
    Sequence := 2
  else if VersionText = '0.2.0-beta.2' then
    Sequence := 3
  else if VersionText = '0.2.0-beta.3' then
    Sequence := 4
  else if VersionText = '0.2.0-beta.4' then
    Sequence := 5
  else if VersionText = '0.2.0-beta.5' then
    Sequence := 6
  else if VersionText = '0.2.0-beta.6' then
    Sequence := 7
  else if VersionText = '0.2.0-beta.7' then
    Sequence := 8
  else
    Result := False;
end;

function TryReadLegacyVersion(var VersionText: String): Boolean;
begin
  Result := RegQueryStringValue(HKCU, LegacyUninstallSubkey, 'DisplayVersion', VersionText);
end;

function InitializeSetup(): Boolean;
var
  StoredSequence: Cardinal;
  InstalledSequence: Integer;
  MappedSequence: Integer;
  InstalledVersion: String;
begin
  Result := False;
  DetectedInstallMode := 'clean';

  if RegKeyExists(HKCU, InstallerRegistrySubkey) then
  begin
    if (not RegQueryStringValue(HKCU, InstallerRegistrySubkey, 'Version', InstalledVersion)) or
       (not RegQueryDWordValue(HKCU, InstallerRegistrySubkey, 'ReleaseSequence', StoredSequence)) then
    begin
      SuppressibleMsgBox('El estado de versión de la instalación existente está incompleto. Trazio no modificó ningún archivo.', mbError, MB_OK, IDOK);
      Exit;
    end;

    if (StoredSequence < 1) or (StoredSequence > 2147483647) then
    begin
      SuppressibleMsgBox('La secuencia de la instalación existente no es válida. Trazio no modificó ningún archivo.', mbError, MB_OK, IDOK);
      Exit;
    end;

    InstalledSequence := Integer(StoredSequence);
    if (not TryMapKnownVersion(InstalledVersion, MappedSequence)) or
       (MappedSequence <> InstalledSequence) then
    begin
      SuppressibleMsgBox('La versión y la secuencia de la instalación existente no son coherentes. Trazio no modificó ningún archivo.', mbError, MB_OK, IDOK);
      Exit;
    end;
  end
  else if TryReadLegacyVersion(InstalledVersion) then
  begin
    if not TryMapKnownVersion(InstalledVersion, InstalledSequence) then
    begin
      SuppressibleMsgBox('Se detectó una versión anterior desconocida (' + InstalledVersion + '). Por seguridad, Trazio no modificó ningún archivo.', mbError, MB_OK, IDOK);
      Exit;
    end;
  end
  else
  begin
    Result := True;
    Exit;
  end;

  if InstalledSequence > CurrentReleaseSequence then
  begin
    SuppressibleMsgBox('Hay una versión más nueva de Trazio instalada. No se permite volver a una versión anterior y no se modificó ningún archivo.', mbError, MB_OK, IDOK);
    Exit;
  end;

  if InstalledSequence = CurrentReleaseSequence then
    DetectedInstallMode := 'repair'
  else
    DetectedInstallMode := 'upgrade';

  Result := True;
end;

procedure InitializeWizard();
var
  ModeMessage: String;
begin
  if DetectedInstallMode = 'repair' then
    ModeMessage := 'Se reparará esta misma versión de Trazio. Tus reuniones, audios, modelos, ajustes y claves no se modificarán.'
  else if DetectedInstallMode = 'upgrade' then
    ModeMessage := 'Se actualizará Trazio conservando la versión anterior hasta completar la instalación. Tus reuniones, audios, modelos, ajustes y claves no se modificarán.'
  else
    ModeMessage := 'Trazio se instalará solo para tu usuario de Windows. No se descargarán componentes desde Internet.';

  WizardForm.WelcomeLabel2.Caption := ModeMessage + #13#10 + #13#10 +
    'Cierra Trazio antes de continuar. El instalador nunca forzará el cierre ni reiniciará una grabación activa.';
end;
