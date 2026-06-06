# A.I.VOICE-API

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
- **A.I.VOICE Editor** インストール済み・起動中
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
    "HostName": ""
  }
}
```

環境変数でも上書き可能：`PORT` / `AIVOICE_DLL_PATH` / `AIVOICE_HOST_NAME`

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

### 合成キュー

合成は内部キューで1件ずつ逐次処理。`priority` が高い順、同値は先着順。
