# A.I.VOICE-API

A.I.VOICE Editorの内部APIをHTTP REST APIでラップしたWindows向け音声合成サーバー

## アーキテクチャ

```
HTTPクライアント → Node.js(Express) → 標準入出力(JSON) → PowerShell → AI.Talk.Editor.Api.dll → A.I.VOICE Editor
```

3層構造になってます

| 層 | 役割 |
|---|---|
| `server.js` | Express HTTPサーバー。REST APIのルーティング・リクエスト検証 |
| `lib/aivoice.js` | バックエンドとのJSONメッセージングブリッジ (標準入出力で子プロセスと通信) |
| `scripts/aivoice_synth.ps1` | A.I.VOICE DLLを直接ロードして音声合成を実行 |

## 前提

- **Windows** (PowerShell + A.I.VOICE Editor が動作する環境)
- **A.I.VOICE Editor** がインストールされ、起動していること
- **Node.js**

## セットアップ

`C:\A.I.VOICE-API` に配置すること前提で作ってます。さぼりました

```powershell
cd C:\A.I.VOICE-API
npm install
scripts\register-task.ps1   # ログオン時自動起動のタスクスケジューラ登録。やらなくてもいい (初回のみ)
```

## 設定

`config/config.yml`:

```yaml
server:
  port: 58080

aivoice:
  dll_path: "C:\\Program Files\\AI\\AIVoice\\AIVoiceEditor\\AI.Talk.Editor.Api.dll"
  host_name: "" # 空=自動検出、特定ホスト指定可
  node_exe: "C:\\Program Files\\nodejs\\node.exe"
```

環境変数による上書きも可能:
| 変数 | 対応項目 |
|---|---|
| `PORT` | サーバーポート |
| `AIVOICE_DLL_PATH` | DLL のパス |
| `AIVOICE_HOST_NAME` | 接続先ホスト名 |

## 起動・停止

```batch
start.bat      # サービス起動 (タスクスケジューラ経由)
stop.bat       # サービス停止
status.bat     # 状態確認 (PID/リソース/A.I.VOICE接続/API死活)
update.bat     # 停止→git pull→npm install→起動
```

`npm start` で直接起動することも可能です。

## APIリファレンス

### `GET /health`

ヘルスチェック。常時応答。

```json
{"status": "ok"}
```

---

### `GET /api/status`

現在の接続状態と利用可能な声質一覧を返します。

```json
{
  "connected": true,
  "hostName": "A.I.VOICE Editor",
  "version": "1.4.11.0",
  "presetNames": ["足立 レイ"]
}
```

---

### `GET /api/presets`

声質プリセット名の一覧のみを返します。未接続時は503。

```json
{"presets": ["足立 レイ"]}
```

---

### `POST /api/reconnect`

A.I.VOICE Editorに再接続します。接続が切れた場合や起動後にEditorを立ち上げ直した場合に使用。

レスポンス (成功):
```json
{
  "connected": true,
  "hostName": "A.I.VOICE Editor",
  "presetNames": ["足立 レイ"]
}
```

失敗時 (503):
```json
{"error": "A.I.VOICE connection failed"}
```

---

### `POST /api/synthesize`

**メインの音声合成エンドポイント**。テキストからWAV音声を生成します。

リクエストボディ (JSON):

| フィールド | 型 | 必須 | デフォルト | 説明 |
|---|---|---|---|---|
| `text` | string | 必須 | - | 読み上げるテキスト |
| `preset` | string | - | 先頭のプリセット | 声質プリセット名 |
| `speed` | number | - | `1.0` | 話速 (0.5〜4.0) |
| `pitch` | number | - | `1.0` | 音高 (0.5〜2.0) |
| `pitchRange` | number | - | `1.0` | 抑揚 (0〜2.0) |
| `volume` | number | - | `1.0` | 音量 (0〜2.0) |
| `middlePause` | number | - | `150` | 読点のポーズ (ms) |
| `longPause` | number | - | `370` | 長ポーズ (ms) |
| `sentencePause` | number | - | `800` | 文末ポーズ (ms) |

リクエスト例:

```bash
curl -X POST http://localhost:58080/api/synthesize \
  -H "Content-Type: application/json" \
  -d '{"text":"こんにちは、元気ですか？","preset":"足立 レイ","speed":1.2}' \
  --output voice.wav
```

```powershell
Invoke-RestMethod -Uri http://localhost:58080/api/synthesize `
  -Method Post `
  -ContentType 'application/json' `
  -Body '{"text":"こんにちは","preset":"足立 レイ","speed":1.0}' `
  -OutFile voice.wav
```

成功時: `Content-Type: audio/wav` で WAV バイナリが返ります。

エラー:
| ステータス | 意味 |
|---|---|
| 400 | `text` フィールドがない、または文字列でない |
| 500 | 音声合成処理に失敗 |
| 503 | A.I.VOICE Editorに未接続 |

---

### 合成キュー

`/api/synthesize` は内部キューで逐次処理されます。同時に複数リクエストを送っても、1つずつ順番に合成され結果が返ります。

## プロセス管理

### runner.ps1 (サービスランナー)

- プロセスを監視し、クラッシュ時に自動再起動
- 60秒以内に10回クラッシュした場合は安全のため再起動を停止
- PID ファイル (`api.pid`) で二重起動を防止

### タスクスケジューラ

`register-task.ps1` で **AIVoiceAPI** という名前のタスクが登録され、Windowsログオン時に自動起動します。

手動管理:
```powershell
Start-ScheduledTask -TaskName 'AIVoiceAPI'
Stop-ScheduledTask -TaskName 'AIVoiceAPI'
```

## 内部メッセージプロトコル

Node.js ↔ PowerShell間は標準入出力でJSONを1行ずつやり取りします

| メッセージ種別 | 方向 | 説明 |
|---|---|---|
| `init` | Node → PS | 初期化。DLLパスとホスト名を渡す |
| `synth` | Node → PS | 音声合成リクエスト |
| `keepalive` | Node → PS | 30秒間隔。ホスト状態を確認し必要なら再接続 |
| `quit` | Node → PS | 終了指示 |

応答フォーマット
```json
{"ok": true, "data": {...}}
{"ok": false, "error": "エラーメッセージ"}
```

## ログ

`logs/api-YYYYMMDD.log` に日付別で出力されます。`status.bat` で末尾10行を確認できます。