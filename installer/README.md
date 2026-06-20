# インストーラー（Inno Setup）

ケイラボAIラジオ Windows版の簡易インストーラー定義（`AIRadio.iss`）。

- **自分専用・全部入り**: `config/*.local.yaml`（APIキー等）と `native/`（AquesTalk1 DLL・CONFIDENTIAL）も同梱する。
- **per-user**: `%LOCALAPPDATA%\Programs\KLabAIRadio` にインストール（管理者/UAC 不要）。アプリが `config/artists.yaml` 等を書き込むため、書込可能なユーザー領域に置く。
- **framework-dependent**: 実行に .NET 10 デスクトップランタイムが必要（別途 VOICEVOX も手動起動）。
- レジストリは使わない（アンインストール登録のみ Inno が標準で行う）。

## ビルド手順

1. **ペイロードを publish**（アプリを終了してから実行）:
   ```
   dotnet publish AIRadio.App/AIRadio.App.csproj -c Release
   ```
   → `AIRadio.App/bin/Release/net10.0-windows/publish/` に exe・config・native が揃う。

2. **Inno Setup 6 を導入**（未導入時のみ・一度きり）:
   ```
   winget install JRSoftware.InnoSetup
   ```
   または https://jrsoftware.org/isdl.php からダウンロード。

3. **コンパイル**:
   ```
   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\AIRadio.iss
   ```
   または `installer\AIRadio.iss` を Inno Setup IDE で開いて Build（Ctrl+F9）。

4. 出力: `installer\Output\AIRadio-Setup-1.0.0.exe`

## 注意

- 生成される `setup.exe` は **APIキー（`*.local.yaml`）と CONFIDENTIAL な AquesTalk DLL を含む**ため、**第三者へ配布しない**（自分の PC 専用）。
  他者への配布は GitHub のソースからクローンしてビルド＋各人が自前のライセンス/ライブラリを導入する運用とする。
- `installer/Output/` は `.gitignore` 済み（秘密同梱の成果物をコミットしない）。
- バージョンは `AIRadio.iss` の `#define AppVersion` で変更する。
