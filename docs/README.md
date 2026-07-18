# A.I.VOICE-API

**複数人が同時に接続し利用することが可能な状態にしないでください。**

A.I.VOICE Editorの内部APIをHTTP REST APIでラップしたWindows向け音声合成サーバー

## アーキテクチャ

```
HTTPクライアント → ASP.NET Core → COM → AI.Talk.Editor.Api.dll → A.I.VOICE Editor
```

| ファイル | 役割 |
|---|---|
| `Program.cs` | ASP.NET Core Minimal API。ルーティング・リクエスト検証 |
| `Services/AiVoiceService.cs` | COM経由でTtsControl を生成・接続管理・合成・キープアライブ |
| `Services/UpdateService.cs` | GitHubリリースの確認・ダウンロード・アップデータ起動 |
| `Services/SynthesisQueue.cs` | 優先度付き合成キュー（高優先度→同値は先着順） |
| `Services/SynthesisParams.cs` | リクエストパラメータ定義 |
| `update.bat` | アップデータ。サーバー停止待ち→ファイル差し替え→再起動 |
| `watchdog.bat` | クラッシュ時自動再起動（アップデート中は待機） |

## バージョン管理

バージョンは `AIVoiceApi.csproj` の `<Version>` で管理し、ビルド時にアセンブリへ埋め込まれる（ビルド後の書き換え不可）。リリース時はここを上げてビルドする。

## 前提

- **Windows**
- **A.I.VOICE Editor** インストール済み（未起動でもサーバーが自動でエディタを起動します）
- **.NET 8 SDK**（ビルド時）

## セットアップ

```powershell
cd C:\A.I.VOICE-API
build.bat
```

`publish\` に実行ファイル一式、リポジトリ直下にリリース用の `A.I.VOICE-API_v<version>.zip` が生成される。GitHubリリースにはこのzipをそのまま添付する。

## 設定

`appsettings.json`（すべて省略可）：

```json
{
  "Server": { "Port": 58080 },
  "AiVoice": {
    "DllPath": "C:\\Program Files\\AI\\AIVoice\\AIVoiceEditor\\AI.Talk.Editor.Api.dll",
    "HostName": "",
    "ProcessName": "AIVoiceEditor",
    "EditorPath": "C:\\Program Files\\AI\\AIVoice\\AIVoiceEditor\\AIVoiceEditor.exe"
  },
  "Update": {
    "GitHubRepo": "yh2237/A.I.VOICE-API"
  }
}
```

| キー | 説明 |
|---|---|
| `DllPath` | A.I.VOICE Editor API のDLLパス |
| `HostName` | 接続先ホスト名（空なら先頭のホスト） |
| `ProcessName` | エディタのプロセス名（強制終了・起動判定に使用） |
| `EditorPath` | エディタ実行ファイルのパス（自動起動に使用） |
| `GitHubRepo` | アップデート取得元のGitHubリポジトリ（`owner/repo`） |

環境変数でも上書き可能：`PORT` / `AIVOICE_DLL_PATH` / `AIVOICE_HOST_NAME` / `AIVOICE_PROCESS_NAME` / `AIVOICE_EDITOR_PATH` / `UPDATE_GITHUB_REPO`

アップデート時に `appsettings.json` は上書きされない（ユーザー設定が保持される）。

## 自動接続・自動復旧

サーバー起動中はバックグラウンドのスーパーバイザーが10秒間隔で接続を監視し、切断を検知すると接続が回復するまで無限に復旧を試みる：

1. 再接続を試行
2. 失敗したらエディタを終了（`TerminateHost` → 残存プロセスを強制終了）
3. `EditorPath` からエディタを起動して再接続
4. それでも失敗したらバックオフ（最大60秒）を挟んで 1 から繰り返し

起動時にエディタが未起動・接続不能の場合も同じフローで自動復旧する。

## 起動

```batch
publish\AIVOICE-API.exe      # 直接起動
publish\watchdog.bat          # クラッシュ時自動再起動
```

## API

### `GET /health`

```json
{"status": "ok"}
```

### `GET /api/info`

サーバー情報を返す。

```json
{
  "name": "A.I.VOICE-API",
  "version": "1.1.0",
  "pid": 12345,
  "startedAt": "2026-07-19T12:00:00+09:00",
  "uptimeSec": 3600,
  "editor": {
    "connected": true,
    "hostName": "A.I.VOICE Editor",
    "version": "1.4.11.0"
  }
}
```

### `GET /api/update/check`

GitHubの最新リリースと現在のバージョンを比較する。

```json
{
  "currentVersion": "1.1.0",
  "latestVersion": "1.2.0",
  "updateAvailable": true,
  "assetName": "A.I.VOICE-API_v1.2.0.zip",
  "assetUrl": "https://github.com/.../A.I.VOICE-API_v1.2.0.zip"
}
```

GitHubに到達できない場合は 502。

### `POST /api/update`

最新リリースへアップデートする。`?force=true` で同バージョンでも強制実行。

流れ（ダウンタイムはファイル差し替え〜再起動の数秒のみ）：

1. 最新リリースの `A.I.VOICE-API_v*.zip` を**稼働したまま**ダウンロード・展開・検証
2. `update.lock` を作成しアップデータ（`update.bat`）を起動、レスポンス返却後にサーバーが正常終了
3. アップデータがプロセス終了を待ってファイルを差し替え（`appsettings.json` は保持）
4. watchdog 稼働時は watchdog が再起動（`update.lock` が消えるまで待機）、直接起動時はアップデータが起動
5. A.I.VOICE Editor は終了しないため、再起動後すぐ再接続される

```json
{
  "updating": true,
  "currentVersion": "1.1.0",
  "targetVersion": "1.2.0",
  "message": "Update started. Server will restart shortly."
}
```

最新版の場合は `"updating": false`。実行中の重複呼び出しは 409、GitHub到達不能・DL失敗は 502。
ログは `%TEMP%\aivoice-api-update\update.log` に出力される。

```powershell
Invoke-RestMethod -Uri http://localhost:58080/api/update -Method Post
```

### `GET /api/status`

```json
{
  "connected": true,
  "hostName": "A.I.VOICE Editor",
  "version": "1.4.11.0",
  "presetNames": ["足立 レイ"]
}
```

### `GET /api/presets`

```json
{"presets": ["足立 レイ"]}
```

未接続時は 503。

### `POST /api/reconnect`

A.I.VOICE Editorに再接続。成功時は `/api/status` と同形式。失敗時 503。

### `POST /api/restart`

A.I.VOICE Editorを再起動して再接続。

1. `TerminateHost` でエディタを終了（最大15秒待機）
2. 終了しない場合は `ProcessName` のプロセスを強制終了
3. エディタを起動し再接続（最大5回リトライ）

成功時は `/api/status` と同形式。失敗時・再起動中の重複呼び出し時は 503。
エディタ起動を待つため応答に数十秒かかることがある。再起動中に投入された合成リクエストは失敗する。

```powershell
Invoke-RestMethod -Uri http://localhost:58080/api/restart -Method Post
```

### `POST /api/synthesize`

テキストを音声合成し WAV を返す。

| パラメータ | 型 | デフォルト | 説明 |
|---|---|---|---|
| `text` | string | (必須) | 読み上げるテキスト |
| `preset` | string | 先頭プリセット | 声質プリセット名 |
| `speed` | number | `1.0` | 話速 (0.5〜4.0) |
| `pitch` | number | `1.0` | 音高 (0.5〜2.0) |
| `pitchRange` | number | `1.0` | 抑揚 (0〜2.0) |
| `volume` | number | `1.0` | 音量 (0〜2.0) |
| `middlePause` | number | `150` | 読点ポーズ (ms) |
| `longPause` | number | `370` | 長ポーズ (ms) |
| `sentencePause` | number | `800` | 文末ポーズ (ms) |
| `priority` | number | `0` | キュー優先度（高いほど優先） |

```powershell
Invoke-RestMethod -Uri http://localhost:58080/api/synthesize `
  -Method Post -ContentType 'application/json' `
  -Body '{"text":"こんにちは","preset":"足立 レイ"}' `
  -OutFile voice.wav
```

成功時 `audio/wav`。エラー時400/500/503。

### `POST` / `GET` `/api/synthesize/benchmark`

`/api/synthesize` と同じパラメータを受け付け、音声合成のベンチマーク結果を JSON で返す。

| メソッド | パラメータ |
|---|---|
| POST | JSON Body（`/api/synthesize` と同形式） |
| GET | クエリパラメータ（例: `?text=こんにちは&speed=1.2`） |

```json
{
  "elapsedMs":    1234,
  "queueWaitMs":  234,
  "synthMs":      1000,
  "hardware": {
    "cpuName":     "Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz",
    "cpuCores":    8,
    "osDescription": "Microsoft Windows 10.0.19045",
    "architecture": "X64"
  }
}
```

```powershell
Invoke-RestMethod -Uri http://localhost:58080/api/synthesize/benchmark `
  -Method Post -ContentType 'application/json' `
  -Body '{"text":"こんにちは"}'

Invoke-RestMethod -Uri 'http://localhost:58080/api/synthesize/benchmark?text=こんにちは&speed=1.2'
```

エラー時は `/api/synthesize` と同じ 400/500/503 形式。

### 合成キュー

合成は内部キューで1件ずつ逐次処理。`priority` が高い順、同値は先着順。
