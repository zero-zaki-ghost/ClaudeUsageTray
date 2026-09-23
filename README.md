# ClaudeUsageTray

Claude Code の使用量（5 時間 / 週次リミットの消費率）を、**Windows の画面に常時表示**
しておく小さな常駐アプリ。macOS 用に自作したメニューバーアプリの Windows 版。

```
5h 35% 3:13  │  週 89% 7:03  │  Fable 0% 7:03
```

数値の色は サーバーが返す `severity` に従って 青 → 琥珀 → 赤 と変わる。
右の時刻はリセットまでの残り時間。

> 名前に "Tray" とあるが、通知領域（トレイ）は使っていない。
> Windows のトレイは 16×16 のアイコン 1 枚しか置けず、Windows 11 は既定で
> オーバーフローに隠すため、「常に見えていてほしい」という要求を満たせなかった。
> 経緯は [`docs/設計.md`](docs/設計.md) を参照。

## ドキュメント

| | |
|---|---|
| [`docs/使い方.md`](docs/使い方.md) | インストール・操作・困ったときの対処 |
| [`docs/設計.md`](docs/設計.md) | **設計の正本。** なぜこの作りなのか、調査結果、実装フェーズ、懸念点 |

各ソースの冒頭にも【設計思想】コメントがある。

関連: ターミナル下部に同種の情報を出す別実装が
[`ideas/work/claude-code-statusline.md`](../ideas/work/claude-code-statusline.md) にある
（Phase 4 の移植元）。

## 手早く始める

```powershell
dotnet publish src\ClaudeUsageTray -c Release -o publish
.\publish\ClaudeUsageTray.exe --install
```

`--install` で `%LOCALAPPDATA%\ClaudeUsageTray\app\` へ配置し、Windows の
自動起動に登録して起動する。以降はサインインすると勝手に立ち上がる。

操作はパネルを **ドラッグ**（移動）と **右クリック**（メニュー）。
クリック透過を ON にした場合の移動は `Ctrl+Shift+U`。

## 現在の状態

| 機能 | 状態 |
|---|---|
| 5 時間 / 週次 / モデル別の消費率とリセット残り時間 | ✅ |
| 常時表示パネル（移動・位置記憶・クリック透過・全画面時の退避） | ✅ |
| 自動起動 | ✅ |
| 指数バックオフ・`Retry-After` 尊重・失効トークンを送らない | ✅ |
| ネット復帰 / スリープ復帰 / ロック解除で即座に取り直す | ✅ |
| 本体プロセスの検出・401 の ShortWatch・`resets_at` 前倒し取得 | ⬜ |
| ローカル JSONL 集計（トークン数・コスト） | **作らない**（同じ数字を 2 箇所で計算しない） |
| しきい値超過の通知 | **作らない**（常時表示しているものを通知する意味が薄い） |

## 設計上の固い制約

変更するときに壊してはいけない不変条件。詳細は [`docs/設計.md`](docs/設計.md)。

1. **自前で OAuth トークンをリフレッシュしない。** 失敗すると Claude Code 本体の
   セッションが壊れ、被害がこのツールの便益を大きく上回る。毎回
   `.credentials.json` を読み直して本体に追従する
2. **`.credentials.json` は `FileShare.ReadWrite | FileShare.Delete` で開く。**
   `FileShare.Read` だけだと本体のトークン書き戻しをこちらが塞ぐ
3. **`~/.claude` に書き込まない。** 読むだけ
4. **送信間隔の下限は 60 秒。** 本体が 60 秒でスロットルしている以上、
   それより速く叩かない。**強制する場所は `UsageEndpointClient.FetchAsync` の中**
   （唯一の出口）。呼び出し側の作法に任せると、メニューの「今すぐ更新」のように
   必ず抜け道ができる
5. **失効が分かっているトークンを送らない。** `expiresAt` を読めば送る前に分かる。
   送って 401 を確かめると、連投になって 429 を招く（実機で 2 回踏んだ）
6. **`limits[]` の `kind` / `severity` を enum にしない。** 未知の値が 0 番に
   落ちて別リミットと誤合算する
7. **しきい値を自前で決めない。** 色分けも通知もサーバーの `severity` に従う
8. **未文書化のシェル API に手を出さない。** タスクバー埋め込みや Deskband は
   Windows Update で壊れる
9. **伏せる処理は出力の直前に置く。** ログのマスクは `Log.Redact` の中で強制する。
   呼び出し側に「気をつけて書く」を求めると必ずいつか破られる（4 と同じ理屈）

## 構成

```
src/ClaudeUsageTray/
  Core/        使用量の取得（認証情報・HTTP・DTO・間隔の判断・送信可否の判断）
  Ui/          常時表示パネル
  Platform/    Win32 P/Invoke・単一インスタンス・自動起動・配置・OS イベント
  Config/      パス・設定・ログ
  Rendering/   severity の色
```

`Core` / `Config` は **`System.Windows.Forms` / `System.Drawing` も `Platform` も参照しない。**
この規約だけでレイヤを保っている（テストから直接叩けることが保証になる）。

取得まわりは役割を 3 つに割ってある。

| | 持つもの |
|---|---|
| `PollSchedule` | **いつ投げるか**だけ。純粋なので時計も通信も無しでテストできる |
| `ISendGuard` | **投げてよいか**。止める理由と画面に出す案内文をセットで返す |
| `PollingService` | 上の 2 つに従って投げ、結果を表示状態へ変換する |

**取得の入口はループ 1 本だけ。** 「今すぐ更新」や OS イベントは
`Wake()` で待機を打ち切るだけで、取得は必ず同じ経路を通る。

NuGet 依存はゼロ。

## ビルド

```powershell
dotnet build src\ClaudeUsageTray          # 開発用
dotnet publish src\ClaudeUsageTray -c Release -o publish   # 配布用（単一 exe）
dotnet run --project tests\PollingHarness # 取得まわりの検証（約 3 分）
```

検証ハーネスは、失効トークンを送らないこと・起床シグナルで待機を打ち切ること・
`Retry-After` を起床で破らないことを**実測で**確かめる。
`CLAUDE_CONFIG_DIR` を一時フォルダへ向けるので **本物の認証情報は使わない。**
終了コード 0 が全項目 PASS。

.NET 9 / WinForms。framework-dependent なので .NET Desktop Runtime 9 が要る。
`PublishTrimmed` と `PublishAot` は WinForms が非対応なので使わない。
