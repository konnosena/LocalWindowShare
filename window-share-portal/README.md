# Local Window Share

スマートフォンから Windows のトップレベルウィンドウを 1 つずつ選んで表示し、前面化と入力送信を行うための Web ポータルです。

## What it does

- shareable window list: 現在表示中のトップレベルウィンドウを列挙
- per-window preview: 選んだウィンドウだけを JPEG で定期配信
- remote control: Activate、タップによるクリック、文字入力、主要キー送信

## Run

```powershell
.\window-share-portal\bin\Release\net10.0-windows\WindowSharePortal.exe
```

起動すると GUI が開き、そこで次を確認・変更できます。

- 接続パスワードの保存
- 現在許可している Access URL / IP / ネットワーク
- 現在接続しているクライアント環境

既定ポートは `48331` です。変更する場合は `WINDOW_SHARE_PORTAL_PORT` を指定してください。

```powershell
$env:WINDOW_SHARE_PORTAL_PORT = "48341"
.\window-share-portal\bin\Release\net10.0-windows\WindowSharePortal.exe
```

既存の `start-window-share-portal.cmd` を使っても、内部では Release ビルド済みの EXE を起動します。

待受アドレスは起動時に自動判定され、次だけに限定されます。

- `127.0.0.1` / `::1`
- この PC のローカルネットワーク用アドレス
- VPN アダプタに付いているアドレス

`0.0.0.0` や公開インターネット向けアドレスには bind しません。許可外の通信元は `403 Forbidden` で拒否します。ネットワーク構成が変わった場合はポータルを再起動してください。

## Smartphone access

スマホから、起動ログまたは `/api/server-info` に出る `Access URLs` のいずれかへアクセスし、共有トークンでログインします。VPN 越しで使う場合は VPN 側アドレスを使ってください。

## Security notes

- このツールは信頼済みのローカルネットワーク / VPN 専用です。既定では TLS は有効化していません。
- 共有トークンはログイン時にだけ使い、その後は 12 時間のセッション Cookie で認証します。
- GUI で保存した共有トークンは `%LOCALAPPDATA%\\WindowSharePortal\\settings.json` に、Windows の現在ユーザー向け DPAPI で暗号化して保存します。
- `logs/`, `bin/`, `obj/`, `.verify/`, `.scratch-api/`, `.dotnet/` はローカル生成物です。Git へ含めないでください。

## Important limitations

- 最小化ウィンドウは `PrintWindow` で正常に取れないことが多いです。
- 一部の GPU アプリや動画系アプリは黒画面または不完全なフレームになることがあります。
- 入力注入は Windows の保護により失敗することがあります。特に管理者権限アプリや保護 UI が対象だと起きます。
- タップ入力は実カーソルを一時的に動かして送る方式です。
