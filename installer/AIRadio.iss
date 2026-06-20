; ============================================================================
;  ケイラボAIラジオ Windows版 — 簡易インストーラー（Inno Setup 6）
; ----------------------------------------------------------------------------
;  ・自分専用 / 全部入り：config の *.local.yaml（APIキー等）と native の
;    AquesTalk1 DLL（CONFIDENTIAL ライセンス品）も同梱する。
;    → 生成される setup.exe は秘密情報・ライセンス品を含むため第三者へ配布しない。
;  ・per-user インストール（%LOCALAPPDATA%・管理者/UAC 不要）。
;    アプリが artists.yaml 等を実行ファイル隣の config\ に書くため、書込可能な
;    ユーザー領域に置く（Program Files だと「アーティスト一覧を生成」が失敗する）。
;  ・framework-dependent：.NET 10 デスクトップランタイムが別途必要（このPCは導入済み）。
;  ・レジストリは使わない（アンインストール登録のみ Inno が標準で行う）。
;
;  ペイロード:  ..\AIRadio.App\bin\Release\net10.0-windows\publish\
;              （事前に  dotnet publish AIRadio.App/AIRadio.App.csproj -c Release  を実行しておく）
;  コンパイル:  ISCC.exe AIRadio.iss      （または Inno Setup IDE で開いて Build）
;  出力:        Output\AIRadio-Setup-<バージョン>.exe
; ============================================================================

#define AppName "ケイラボAIラジオ"
#define AppVersion "1.0.0"
#define AppPublisher "KLab"
#define AppExeName "AIRadio.App.exe"
#define PublishDir "..\AIRadio.App\bin\Release\net10.0-windows\publish"

[Setup]
; AppId はアプリの一意キー（アップグレード/アンインストールの同一性判定）。一度決めたら変更しない。
AppId={{B0F2E6C4-7A3D-4E91-9C2B-3F5A1D8E64A2}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; per-user（管理者不要）。%LOCALAPPDATA%\Programs\KLabAIRadio に配置。
DefaultDirName={localappdata}\Programs\KLabAIRadio
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=AIRadio-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english";  MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Windows ログオン時に自動起動する"; GroupDescription: "起動オプション:"; Flags: unchecked

[Files]
; publish フォルダを丸ごと配置（exe / 依存 DLL / config\ / native\ を含む）。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}";        Filename: "{app}\{#AppExeName}"
Name: "{userdesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}";  Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; インストール後に生成・更新される config（artists.yaml 等）まで含めて掃除する。
; ※ Spotify トークンや番組長等は %LOCALAPPDATA%\AIRadio に別途保存され、ここでは消さない（ユーザーデータ保全）。
Type: filesandordirs; Name: "{app}\config"
