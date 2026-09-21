# ClaudeUsageTray

Claude Code の使用量（5時間 / 週次リミットの消費率）を **Windows のタスクバー（通知領域）に常時表示**する常駐アプリ。
macOS 用に自作したメニューバーアプリの Windows 版。

- 設計の正本: `ideas` リポジトリの [`work/claude-usage-tray.md`](../ideas/work/claude-usage-tray.md)
- ターミナル版（別実装・稼働中）: 同 [`work/claude-code-statusline.md`](../ideas/work/claude-code-statusline.md)

## 現在の状態

**Phase 1 完了** — トレイに代表値が数字で出る。

| Phase | 内容 | 状態 |
|---|---|---|
| 0 | 骨組み・単一インスタンス・終了処理 | ✅ |
| 1 | `/api/oauth/usage` を叩いて代表値をトレイに描画 | ✅ |
| 1.5 | ローカルスタブサーバ | ⬜ |
| 2 | 401 リトライ・バックオフ・DPI 追従・TaskbarCreated 再登録 | ⬜ |
| 3 | 詳細フライアウト（全リミット + Fable 等モデル別） | ⬜ |
| 4 | ローカル JSONL 集計（トークン数・コスト・現在セッション） | ⬜ |
| 5 | 設定・自動起動・常時表示・通知 | ⬜ |

## ビルドと実行

```bash
dotnet build src/ClaudeUsageTray/ClaudeUsageTray.csproj
dotnet run   --project src/ClaudeUsageTray
```

配布:

```bash
dotnet publish src/ClaudeUsageTray -c Release -o publish
```

framework-dependent。`Microsoft.WindowsDesktop.App` が必要（導入済み）。
`PublishTrimmed` / `PublishAot` は WinForms 非対応なので使わない。

## 操作

画面の右下に常時表示されるパネルが主たる表示。

| 操作 | 効果 |
|---|---|
| **パネルをドラッグ** | 移動。位置は自動で保存される |
| **パネルを右クリック** | メニュー（更新 / 常時表示 / クリック透過 / 終了） |
| **Ctrl+Shift+U** | 移動モード。クリック透過を ON にしているときでも掴めるようになる |

「クリックを下へ透過する」を ON にすると、パネルの上のクリックが下のウィンドウへ
素通りするようになる代わりに**掴めなくなる**ので、移動は Ctrl+Shift+U から行う。

## コマンドライン引数

| 引数 | 効果 |
|---|---|
| `--endpoint <url>` | 使用量エンドポイントを差し替える（スタブサーバでの検証用） |
| `--interval <秒>` | ポーリング間隔。**60 秒未満は無視して 60 秒に丸める** |
| `--icon-stress` | アイコンを 50ms 間隔で 10,000 回再生成する（GDI リーク検証用） |

## 置き場所

| | パス |
|---|---|
| ログ | `%LOCALAPPDATA%\ClaudeUsageTray\logs\tray.log` |
| キャッシュ | `%LOCALAPPDATA%\ClaudeUsageTray\cache\` |

`cache\last-usage.json` には直近成功レスポンスの生 JSON が入る。
API のスキーマが変わったときに diff を取るための唯一の手がかりなので消さないこと。

## ⚠ Windows 11 ではアイコンが隠れる

Windows 11 は**新規のトレイアイコンを既定でオーバーフロー（∧）に入れる**。
初回起動時は タスクバーの `∧` を開き、アイコンをタスクバーへドラッグしてピン留めすること。
（自動でピン留めする `IsPromoted` 対応は Phase 5）

## 設計上の固い制約

実装を変更するときに壊してはいけない不変条件。

1. **自前で OAuth トークンをリフレッシュしない。** 失敗すると Claude Code 本体のセッションが壊れ、
   被害がこのツールの便益を大きく上回る。毎ポーリングで `.credentials.json` を読み直して追従する。
2. **`.credentials.json` は `FileShare.ReadWrite | FileShare.Delete` で開く。**
   `FileShare.Read` だけだと本体のトークン書き戻しをこちらがブロックしてしまう。
3. **`Bitmap.GetHicon()` の呼び出しは `Rendering/TrayIconSlot.cs` の 1 箇所だけ。**
   返るハンドルは呼び出し側の所有で、`Icon.Dispose()` では解放されない。`DestroyIcon` が必須。
4. **ポーリング間隔の下限は 60 秒。** 本体が 60 秒でスロットルしている以上、それより速く叩かない。
5. **`limits[]` の `kind` / `severity` を enum にしない。** 未知の値が 0 番に落ちて別リミットと誤合算する。
6. **しきい値を自前で決めない。** 色分けも通知もサーバーの `severity` に従う（公式 UI と食い違わせない）。
