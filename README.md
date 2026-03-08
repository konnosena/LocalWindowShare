# Window Share Portal

Windows PC の画面をスマートフォンやタブレットからリアルタイムで閲覧・操作する Web ポータルです。
ローカルネットワークまたは VPN 経由で、ブラウザだけで利用できます。

## 主な機能

- **ウィンドウ単位のキャプチャ**: トップレベルウィンドウを列挙し、選択したウィンドウだけを配信
- **WebRTC リアルタイムストリーミング**: VP8 / VP9 / AV1 コーデック対応
- **リモート入力**: タップ(クリック)、ダブルクリック、長押し(右クリック)、テキスト入力、キー送信、スクロール、ドラッグ
- **ピンチズーム**: 二本指操作で画面の拡大・縮小
- **クライアント承認**: 新規接続は管理画面でインライン承認、承認済みリスト管理、全許可モード
- **ネットワーク制限**: ローカル / VPN アドレスのみに自動 bind。外部からのアクセスを遮断
- **トークン認証**: パスワードでログイン、セッション Cookie で継続認証
- **WPF 管理 GUI**: 接続状況、ネットワーク設定、承認管理、ログをダークテーマ UI で表示

## スクリーンショット

| 管理 GUI (WPF) | モバイル Web UI |
|:-:|:-:|
| *起動後の管理画面* | *スマホブラウザからの操作画面* |

## 必要環境

- Windows 10 (19041) 以降
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## ビルドと実行

```powershell
# ビルド
dotnet build window-share-portal/WindowSharePortal.csproj -c Release

# 実行
.\window-share-portal\bin\Release\net10.0-windows10.0.19041.0\WindowSharePortal.exe
```

起動すると WPF の管理ウィンドウが開き、サーバーが自動的に開始されます。
既定のポートは **48331** です。

### ポート変更

```powershell
$env:WINDOW_SHARE_PORTAL_PORT = "48341"
.\window-share-portal\bin\Release\net10.0-windows10.0.19041.0\WindowSharePortal.exe
```

## 使い方

1. アプリを起動すると、管理画面にアクセス URL が表示されます
2. スマートフォンのブラウザからその URL にアクセスします
3. 管理画面に表示されているパスワードでログインします
4. 初回接続時はクライアント承認が必要です（管理画面で許可/拒否）
5. ウィンドウ一覧から操作したいウィンドウを選択します

### タッチ操作

| 操作 | 動作 |
|------|------|
| タップ | クリック |
| ダブルタップ | ダブルクリック |
| 長押し (400ms) | 右クリック |
| 一本指スワイプ | スクロール |
| 長押し → ドラッグ | ドラッグ&ドロップ |
| 二本指ピンチ | ズーム |
| 二本指スクロール | スクロール |

## アーキテクチャ

```
┌─────────────────────────────────┐
│  WPF GUI (MainWindow)          │  管理画面・承認・設定
├─────────────────────────────────┤
│  ASP.NET Core (Kestrel)        │  REST API / WebSocket
├──────────────┬──────────────────┤
│  WindowBroker│ WebRTC Session   │  キャプチャ / ストリーミング
│  (Win32 API) │ (SIPSorcery)     │
└──────────────┴──────────────────┘
        ↕                ↕
   PrintWindow       WebSocket
   CopyFromScreen    シグナリング
   WGC (Direct3D)
```

### 技術スタック

| 層 | 技術 |
|---|---|
| ランタイム | .NET 10 |
| Web サーバー | ASP.NET Core Minimal API (Kestrel) |
| GUI | WPF (Windows Presentation Foundation) |
| WebRTC | SIPSorcery + SIPSorceryMedia.Encoders / MixedReality.WebRTC |
| キャプチャ | Windows Graphics Capture API, PrintWindow, CopyFromScreen |
| 入力注入 | Win32 SendInput |
| フロントエンド | Vanilla JavaScript (フレームワーク不使用) |

### 主要クラス

| クラス | 役割 |
|---|---|
| `PortalServer` | Kestrel サーバー管理、API ルート定義、認証 |
| `WindowBroker` | ウィンドウ列挙・キャプチャ・入力送信 |
| `WebRtcWindowStreamSession` | WebRTC ビデオ配信セッション |
| `ClientApprovalService` | クライアント承認フロー管理 |
| `NetworkAccessPolicy` | bind アドレス / 許可ネットワーク自動判定 |
| `PortalRuntimeState` | ランタイム状態管理 (スレッドセーフ) |
| `MainWindow` | WPF 管理 GUI |

## セキュリティ

- **ローカル / VPN 専用**: 公開インターネット向けアドレスには bind しません
- **TLS なし**: 信頼済みネットワーク内での使用を前提としています
- **トークン認証**: ログイン後は 12 時間のセッション Cookie で管理
- **DPAPI 暗号化**: 保存パスワードは Windows の現在ユーザー向け DPAPI で保護
- **クライアント承認**: 新規クライアントは管理者の明示的な許可が必要 (無効化可能)
- **設定保存先**: `%LOCALAPPDATA%\WindowSharePortal\settings.json`

## 既知の制限

- 最小化ウィンドウは正常にキャプチャできないことがあります
- 一部の GPU アプリ / 動画系アプリは黒画面になることがあります
- 管理者権限アプリや保護 UI への入力注入は Windows の保護により失敗することがあります
- タップ入力は実カーソルを一時的に移動させる方式です

## ライセンス

MIT
