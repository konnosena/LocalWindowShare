# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## プロジェクト概要

Windows のトップレベルウィンドウをスマートフォンから閲覧・操作するための Web ポータル。ASP.NET Core (Kestrel) + Windows Forms GUI のハイブリッドアプリケーション。

主な機能:
- ウィンドウ列挙・JPEG/PNG フレームキャプチャ配信
- WebRTC (VP8) によるリアルタイムビデオストリーミング
- リモートからのクリック・テキスト・キー入力送信 (Win32 SendInput)
- トークン認証・ネットワークアクセス制限 (ローカル/VPN のみ)

## ビルドと実行

```bash
# Window Share Portal のビルド (Release)
dotnet build window-share-portal/WindowSharePortal.csproj -c Release --ignore-failed-sources

# 実行
./window-share-portal/bin/Release/net10.0-windows/WindowSharePortal.exe

# または起動スクリプト (ビルド済みでなければ自動ビルド)
./start-window-share-portal.cmd

# ポート変更
WINDOW_SHARE_PORTAL_PORT=48341 ./window-share-portal/bin/Release/net10.0-windows/WindowSharePortal.exe
```

ランチャー群のビルド（`cmd2`, `cmd3`, `cmd21`, `cmd22`）:
```powershell
pwsh ./build-launchers.ps1 -Configuration Release [-InstallDirectory <path>]
```

## 技術スタック

- **ランタイム**: .NET 10 (`net10.0-windows10.0.19041.0`)
- **Web**: ASP.NET Core Minimal API (Kestrel)、静的ファイル配信、WebSocket
- **GUI**: Windows Forms (`PortalControlForm`)
- **WebRTC**: SIPSorcery + SIPSorceryMedia.Encoders (VP8)
- **キャプチャ**: Windows Graphics Capture API (WGC) + PrintWindow/CopyFromScreen フォールバック
- **入力注入**: Win32 SendInput (マウス・キーボード)
- **フロントエンド**: Vanilla JS (`wwwroot/app.js`)、CSS、HTML（フレームワークなし）

## アーキテクチャ

### エントリポイントと起動フロー
`Program.cs` が STAThread で起動し、`PortalSettingsStore` → `PortalRuntimeState` → `PortalServer` → `PortalControlForm` の順に初期化。`Application.Run(form)` で WinForms メッセージループに入り、サーバーは GUI の「接続をON」ボタンで起動する。

### 主要クラス

| クラス | 役割 |
|---|---|
| `PortalServer` | Kestrel サーバー管理。API ルート定義、認証ミドルウェア、WebSocket エンドポイント |
| `WindowBroker` | ウィンドウ列挙・キャプチャ・入力送信。Win32 API 経由の全操作を集約 |
| `WebRtcWindowStreamSession` | WebSocket シグナリング + WebRTC ビデオ配信セッション。キャプチャループ管理 |
| `WindowsGraphicsCaptureSource` | WGC (Direct3D11 ベース) によるフレーム取得。COM interop で初期化 |
| `NetworkAccessPolicy` | 起動時にネットワークアダプタを走査し、bind アドレスと許可ネットワークを決定 |
| `PortalRuntimeState` | トークン・ポート・アクセスポリシーのランタイム状態管理（スレッドセーフ） |
| `PortalSettingsStore` | `%LOCALAPPDATA%/WindowSharePortal/settings.json` への永続化 |
| `PortalControlForm` | WinForms GUI。サーバー状態・接続クライアント・ログの表示と操作 |
| `NativeMethods` | P/Invoke 宣言（user32, dwmapi, d3d11, combase） |

### API エンドポイント (PortalServer.cs)
- `POST /api/login` / `POST /api/logout` - トークン認証
- `GET /api/server-info` - サーバー情報
- `GET /api/windows` / `GET /api/windows/{handle}` - ウィンドウ一覧/詳細
- `GET /api/windows/{handle}/frame` - 静止画フレーム取得 (JPEG/PNG)
- `POST /api/windows/{handle}/activate` - ウィンドウ前面化
- `POST /api/windows/{handle}/input/click` - クリック
- `POST /api/windows/{handle}/input/pointer` - ポインタ操作 (move/down/up/click/wheel)
- `POST /api/windows/{handle}/input/text` - テキスト入力
- `POST /api/windows/{handle}/input/key` - キー送信
- `WS /ws/webrtc?handle={handle}` - WebRTC シグナリング

### キャプチャ戦略
`WindowBroker.CaptureBitmap` は対象ウィンドウに応じて方式を選択:
1. フォアグラウンドや Terminal/Explorer 系 → `CopyFromScreen` を優先
2. それ以外 → `PrintWindow` を試行、黒フレーム検出時は `CopyFromScreen` にフォールバック
3. WebRTC セッションでは WGC (`WindowsGraphicsCaptureSource`) を優先利用し、使えない場合は bitmap キャプチャにフォールバック

### フロントエンド (wwwroot/)
`app.js` は単一ファイルで全 UI ロジックを実装。WebRTC 接続、タッチ操作（ドラッグ、ピンチズーム、二本指スクロール、右クリック長押し）、フレームレート/品質設定を管理。

## 開発上の注意

- `AllowUnsafeBlocks` が有効。`VideoFrameBuffer` と `WindowsGraphicsCaptureSource` でポインタ操作あり
- `.dotnet` / `.dotnet-home` はプロジェクトローカルの dotnet 環境。`build-launchers.ps1` が `DOTNET_CLI_HOME` を設定
- `.verify/` ディレクトリは各機能のビルド検証用出力で、直接変更しない
- `.scratch-api/` はプロトタイピング用の別プロジェクト
- 既定ポートは `48331`。環境変数 `WINDOW_SHARE_PORTAL_PORT` または `ASPNETCORE_URLS` で変更可能
- 設定は `%LOCALAPPDATA%/WindowSharePortal/settings.json` に保存
