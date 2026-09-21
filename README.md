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
| [`docs/設計.md`](docs/設計.md) | なぜこの作りなのか。各ソースの冒頭にも同じ観点のコメントがある |

設計の正本は `ideas` リポジトリの
[`work/claude-usage-tray.md`](../ideas/work/claude-usage-tray.md)。
ターミナル下部に出す別実装が [`work/claude-code-statusline.md`](../ideas/work/claude-code-statusline.md) にある。

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
| 401 リトライ・指数バックオフ・`resets_at` 前倒し取得 | ⬜ |
| ローカル JSONL 集計（トークン数・コスト・現在セッション） | ⬜ |
| しきい値超過の通知 | ⬜ |

## 設計上の固い制約

変更するときに壊してはいけない不変条件。詳細は [`docs/設計.md`](docs/設計.md)。

1. **自前で OAuth トークンをリフレッシュしない。** 失敗すると Claude Code 本体の
   セッションが壊れ、被害がこのツールの便益を大きく上回る。毎回
   `.credentials.json` を読み直して本体に追従する
2. **`.credentials.json` は `FileShare.ReadWrite | FileShare.Delete` で開く。**
   `FileShare.Read` だけだと本体のトークン書き戻しをこちらが塞ぐ
3. **`~/.claude` に書き込まない。** 読むだけ
4. **ポーリング間隔の下限は 60 秒。** 本体が 60 秒でスロットルしている以上、
   それより速く叩かない。引数でも設定でも破れないようにする
5. **`limits[]` の `kind` / `severity` を enum にしない。** 未知の値が 0 番に
   落ちて別リミットと誤合算する
6. **しきい値を自前で決めない。** 色分けも通知もサーバーの `severity` に従う
7. **未文書化のシェル API に手を出さない。** タスクバー埋め込みや Deskband は
   Windows Update で壊れる

## 構成

```
src/ClaudeUsageTray/
  Core/        使用量の取得（認証情報の読み取り・HTTP・DTO・ポーリング）
  Ui/          常時表示パネル
  Platform/    Win32 P/Invoke・単一インスタンス・自動起動・配置
  Config/      パス・設定・ログ
  Rendering/   severity の色
```

`Core` / `Config` は `System.Windows.Forms` と `System.Drawing` を参照しない。
この規約だけでレイヤを保っている（テストから直接叩けることが保証になる）。

NuGet 依存はゼロ。

## ビルド

```powershell
dotnet build src\ClaudeUsageTray          # 開発用
dotnet publish src\ClaudeUsageTray -c Release -o publish   # 配布用（単一 exe）
```

.NET 9 / WinForms。framework-dependent なので .NET Desktop Runtime 9 が要る。
`PublishTrimmed` と `PublishAot` は WinForms が非対応なので使わない。
