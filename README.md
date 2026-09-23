# ClaudeUsageTray

Claude Code の使用量（5 時間 / 週次リミットの消費率）を、**Windows の画面に常時表示**
しておく小さな常駐アプリ。macOS 用に自作したメニューバーアプリの Windows 版。

```
5h 35% 3:13  │  週 89% 7:03  │  Fable 0% 7:03
```

数値の色はサーバーが返す `severity` に従って 青 → 琥珀 → 赤 と変わる。
右の時刻はリセットまでの残り時間。

> 名前に "Tray" とあるが、通知領域（トレイ）は使っていない。
> Windows のトレイは 16×16 のアイコン 1 枚しか置けず、Windows 11 は既定で
> オーバーフローに隠すため、「常に見えていてほしい」という要求を満たせなかった。
> 経緯は [`docs/設計.md`](docs/設計.md) 2 章。

## 手早く始める

```powershell
dotnet publish src\ClaudeUsageTray -c Release -o publish
.\publish\ClaudeUsageTray.exe --install
```

`%LOCALAPPDATA%\ClaudeUsageTray\app\` へ配置し、Windows の自動起動に登録して起動する。
以降はサインインすると勝手に立ち上がる。

操作はパネルを **ドラッグ**（移動）と **右クリック**（メニュー）。
クリック透過を ON にした場合の移動は `Ctrl+Shift+U`。

**.NET Desktop Runtime 9 が必要**（framework-dependent のため）。x64 のみ。

## ドキュメント

**3 つに分けてある。境目は「時間で変わるか」。**

| | 持つもの | |
|---|---|---|
| [`docs/現状.md`](docs/現状.md) | **いまどうなっているか** | 機能の状態・既知の穴・確かめたこと・次にやること。**再開するならここから** |
| [`docs/設計.md`](docs/設計.md) | **なぜそうなっているか** | 調査結果・判断とその理由・実機で踏んだ罠・セキュリティ |
| [`docs/使い方.md`](docs/使い方.md) | **どう使うか** | インストール・操作・困ったときの対処 |

作らないと決めた機能の調査記録は [`docs/調査-JSONL集計.md`](docs/調査-JSONL集計.md) に隔離してある。
**実装が存在しない機能の設計を本編に混ぜない**ための分離で、気が変わったときの出発点になる。

同じ事実を 2 箇所に書かない。状態は `現状.md` だけが持ち、`設計.md` は原因の
分析だけを持つ。**食い違いを作らないための分け方**で、実際に一度食い違わせた。

各ソースの冒頭にも【設計思想】コメントがある。

## 確かめる

```powershell
powershell -ExecutionPolicy Bypass -File tools\verify-log.ps1   # 実機の状態。7 項目
dotnet run --project tests\PollingHarness                        # 取得まわり。14 項目・約 3 分
```

`verify-log.ps1` はログを読んで **前回の Windows 起動以降**を判定する。
「再起動してログを見る」を目視に頼らないための道具で、
**どの行を探せばいいか分からない手順は実質的に実行されない**という反省から作った。

`PollingHarness` は `CLAUDE_CONFIG_DIR` を一時フォルダへ向けるので
**本物の認証情報を使わない**。どちらも終了コード 0 が「問題なし」。

## 設計上の固い制約

変更するときに壊してはいけない不変条件。**理由は [`docs/設計.md`](docs/設計.md) 0 章。**

1. **自前で OAuth トークンをリフレッシュしない**（本体のセッションが壊れる）
2. **`.credentials.json` は `FileShare.ReadWrite | FileShare.Delete` で開く**
3. **`~/.claude` に書き込まない**
4. **送信間隔の下限 60 秒。強制する場所は `UsageEndpointClient.FetchAsync` の中**（唯一の出口）
5. **失効が分かっているトークンを送らない**
6. **`limits[]` の `kind` / `severity` を enum にしない**
7. **しきい値を自前で決めない**（サーバーの `severity` に従う）
8. **未文書化のシェル API に手を出さない**
9. **UI を殺せる設定を作らない**
10. **伏せる処理は出力の直前に置く**（`Log.Redact` の中で強制する）
11. **`Core` / `Config` は `Platform`・WinForms・Drawing を参照しない**

## 構成

```
src/ClaudeUsageTray/
  Core/        使用量の取得（認証情報・HTTP・DTO・間隔の判断・送信可否の判断）
  Ui/          常時表示パネル
  Platform/    Win32 P/Invoke・単一インスタンス・自動起動・配置・OS イベント
  Config/      パス・設定・ログ
  Rendering/   severity の色
tests/PollingHarness/   取得まわりの検証
tools/verify-log.ps1    実機の検証
```

取得まわりは役割を 3 つに割ってある。

| | 持つもの |
|---|---|
| `PollSchedule` | **いつ投げるか**だけ。純粋なので時計も通信も無しでテストできる |
| `ISendGuard` | **投げてよいか**。止める理由と画面に出す案内文をセットで返す |
| `PollingService` | 上の 2 つに従って投げ、結果を表示状態へ変換する |

**取得の入口はループ 1 本だけ。** 「今すぐ更新」や OS イベントは `Wake()` で
待機を打ち切るだけで、取得は必ず同じ経路を通る。

NuGet 依存はゼロ。.NET 9 / WinForms。
`PublishTrimmed` と `PublishAot` は WinForms が非対応なので使わない。
