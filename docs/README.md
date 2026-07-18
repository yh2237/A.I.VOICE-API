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
| `Services/SynthesisQueue.cs` | 優先度付き合成キュー（高優先度→同値は先着順） |
| `Services/SynthesisParams.cs` | リクエストパラメータ定義 |

## 前提

- **Windows**
- **A.I.VOICE Editor** インストール済み（未起動でもサーバーが自動起動する）
- **.NET 8 SDK**（ビルド時のみ）

## セットアップ

```powershell
cd C:\A.I.VOICE-API
build.bat
```

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
  }
}
```

| キー | 説明 |
|---|---|
| `DllPath` | A.I.VOICE Editor API のDLLパス |
| `HostName` | 接続先ホスト名（空なら先頭のホスト） |
| `ProcessName` | エディタのプロセス名（強制終了・起動判定に使用） |
| `EditorPath` | エディタ実行ファイルのパス（自動起動に使用） |

環境変数でも上書き可能：`PORT` / `AIVOICE_DLL_PATH` / `AIVOICE_HOST_NAME` / `AIVOICE_PROCESS_NAME` / `AIVOICE_EDITOR_PATH`

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
