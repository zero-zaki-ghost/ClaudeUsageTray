# 調査記録: ローカル JSONL からの集計

> ## ⚠ これは**作らないと決めた機能**の調査記録
>
> トークン数・コスト・現在セッションの消費を `~/.claude/projects/**/*.jsonl` から
> 集計する構想（旧 Phase 4）。**2026-09-23 に「当面やらない」と決めた**
> （[`設計.md`](設計.md) 0 章）。
>
> 理由: 同じ数字をターミナルの statusline が既に出しており、**2 箇所で計算すると
> 食い違ったときにどちらが正しいか分からなくなる。**
>
> ## なぜ消さずに残すのか
>
> ここに書いてあるのは**実測で判明した事実**で、調べ直すには同じ手間がかかる。
> とくに「同一 usage が複数行に重複する」（約 2 倍ずれる）は、知らずに実装すると
> **間違った数字を自信満々に表示する**たちの悪い罠。
>
> 気が変わって着手するとき、ここが出発点になる。
> **`設計.md` からは外してある。** 存在しない機能の設計が本編に混ざっていると、
> 何が実装されているのか読み取れなくなるため。
>
> ## 着手するなら
>
> **ゼロから書かないこと。** `ideas` リポジトリの `work/claude-code-statusline/statusline.mjs`
> が同じ集計を実装済み（増分キャッシュ・dedup・コスト計算。実測 初回 494ms / 増分 130ms）。
> 移植が最短で、かつ動作確認済みの正解データにもなる。
>
> ⚠ **`npx ccusage@latest` と ±5% 以内に一致するまでコスト表示を出さないこと。**
> 間違った金額を出すほうが、出さないより有害。

---

## 1. 実測で判明した落とし穴

### 5-1. ★★ 同一 usage が複数行に重複する（未対応だと約 2 倍ずれる）

実測（`9d058499-...jsonl` / 8.7MB / 2,102 行）:

- `type:"assistant"` の行 = **570**
- ユニークな `requestId` = **287**

同一 requestId の 2 行は `uuid` だけが違い、`message.id` と usage は**完全に同一**:

```
requestId=req_011CfCq3iiYmY8dG4pyVxczS  (2行)
  msgId=msg_011CfCq3jCLFxaQ4gKqDz3xv uuid=0b86a2ec in=2 cc=45112 cr=0 out=137 ts=...12:52:03
  msgId=msg_011CfCq3jCLFxaQ4gKqDz3xv uuid=3941e7ef in=2 cc=45112 cr=0 out=137 ts=...12:52:04
```

→ **dedup キーは `message.id` → `requestId` → `uuid` の優先順**。ccusage が `requestId + message.id` をキーにしているのはこのため。

> ✅ **既存実装と独立に一致した**。[`claude-code-statusline.md`](../../ideas/work/claude-code-statusline.md) が同じ現象を別途実測しており、数字も一致する（単純合計 570 行 244,220,933 → `requestId` 重複排除 287 行 **123,093,271**）。
>
> ★ さらに有用な知見として、**重複は必ず連続して現れる**（実測 3 ファイルで非連続の重複 0 件）。
> → **全 ID の `HashSet` を持つ必要はなく、直前の `requestId` を 1 つ覚えておくだけで排除できる。** 本アプリもこの簡略版を採用し、`_seenIds` の永続化設計（11-2）は不要になる。ただし増分 tail と組み合わせるため、**「前回読み終えた最後の requestId」だけはカーソルと一緒に永続化**すること。

### 5-2. ★ `apiBlockIndex` は 5 時間ブロックではない

名前から誤解しやすいが、実測で否定された:

```
block 0  count 287  from 2026-09-19T12:52:03Z  to 2026-09-21T03:00:34Z
block 1  count 162  from 2026-09-19T12:52:04Z  to 2026-09-21T02:59:50Z
block 2  count 114  from 2026-09-19T12:52:40Z  to 2026-09-21T02:52:35Z
```

**全ブロックが同じ秒に開始し、時間範囲が完全に重なっている。** 5 時間ウィンドウの算出には使えない。

正体は **1 レスポンス内の content block のインデックス**。1 回の API レスポンスが複数の content block を持つと、同じ `usage` を載せた行が **ブロック数だけ**書かれ、`requestId` は同じまま `apiBlockIndex` が 0,1,2... と振られる（5-1 の重複の正体がこれ）。

→ 5h ブロックは ccusage 方式（UTC 基準、前ブロック終了後のターンで `floor(timestamp, 1時間)` を開始点に新ブロックを開き 5h で閉じる）で自前算出する。

### 5-3. サブエージェントのログは別ディレクトリ（重複はしない）

```
projects/<proj>/<sessionId>.jsonl                        ← メイン
projects/<proj>/<sessionId>/subagents/agent-XXXX.jsonl   ← サブエージェント（最大 6.7MB）
```

メインとサブの requestId 集合の**重なりは 0 件**。完全に別枠の実消費なので `SearchOption.AllDirectories` で**再帰列挙が必須**。逆に言えば重複カウントの心配はない。

### 5-4. `usage.iterations[]` を足すと二重計上

```json
"usage": { "input_tokens":2, "cache_creation_input_tokens":12445, "cache_read_input_tokens":33025,
  "output_tokens":1661, "output_tokens_details":{"thinking_tokens":1133},
  "cache_creation":{"ephemeral_1h_input_tokens":12445,"ephemeral_5m_input_tokens":0},
  "iterations":[ { ... } ],        // ← トップレベル usage の内訳。足すな
  "service_tier":"standard", "speed":"standard" }
```

`Utf8JsonReader` で `iterations` に来たら `reader.Skip()` する。

### 5-5. ★ `cache_read` を別単価で計算しないと約 10 倍ずれる

実測（1 セッション・2026-09-19〜09-21・assistant 570 件）:

```
input = 1,140 / output = 893,269 / cache_creation = 6,383,986 / cache_read = 236,942,538
```

**cache_read が圧倒的多数。** 通常入力単価で誤計算すると 1 桁違う金額になる。

`cache_creation_input_tokens` は `cache_creation.{ephemeral_1h, ephemeral_5m}` の合計と一致する（実測: `cc=12445, 1h=12445, 5m=0`）。**内訳があれば内訳を使い、無い場合のみ 5m 単価で計上**する。

> ⚠ **各モデルの単価の実数値は本ドキュメントでは未確定。** 実装時に公式ドキュメントから input / output / cache write(5m,1h) / cache read を取得すること。`cache/model-catalog/*.json` に価格フィールドは **0 件**（grep で確認済み）なので、ハードコード ＋ `pricing.json` での上書きしか手がない。

**未知モデルを黙って 0 円にしない。** `unknown` バケットに集計して「未計上: `<model>` / N トークン」と赤字表示する（黙って 0 にすると「安いな」と誤解して危険）。

### 5-6. ✅ アクティブセッションは推測不要（公式のレジストリがある）

`~/.claude/sessions/<pid>.json`（数百バイト）:

```json
{"pid":14984,"sessionId":"62ae6e1d-...","cwd":"<プロジェクトの作業ディレクトリ>",
 "startedAt":1789967877059,"version":"2.1.278","kind":"interactive","entrypoint":"cli",
 "name":"ideas-af","status":"busy","updatedAt":1789968673242}
```

**プロセスごとに 1 ファイル**なので複数の Claude Code が同時起動していても全部拾える。`version` は User-Agent の自動追従にも使える。

`sessionId` → JSONL パスの逆引きは、**`cwd` からディレクトリ名を自前変換せず `projects/**/<sessionId>.jsonl` をファイル名一致で再帰検索する**。実測の `F--old-d-drive--F---work-ideas` のように非 ASCII の `■F` が `-` に潰れ `_` も `-` になる複雑な規則を推測するのはリスクが高い。

### 5-7. ✅ 走査コストは安い（設計をシンプルにできる）

- `~/.claude/projects` 全体 = **42MB**
- 8.7MB / 2,102 行のファイルを `[IO.File]::ReadLines` + `string.Contains` で **73ms**

→ **「全走査は重い」という前提は覆る。** 起動時フル走査（8 日フィルタ付き）は 1 秒未満で終わる。複雑な機構は不要。

### 5-8. ❌ `stats-cache.json` はフォールバック源として使えない

`lastComputedDate: "2026-01-24"` で **8 ヶ月止まっている**（`totalSessions: 4`、`costUSD` は 0 のまま）。`history.jsonl` はプロンプト文字列のみでトークン数を含まない。`policy-limits.json` はコンプライアンス設定で使用量とは無関係。
---

## 2. 実装方針（構想のまま）

### 11-1. 探索と増分読み

```csharp
Directory.EnumerateFiles(projectsRoot, "*.jsonl", SearchOption.AllDirectories)  // 再帰必須（5-3）
    .Select(p => new FileInfo(p))
    .Where(f => f.LastWriteTimeUtc >= UtcNow - TimeSpan.FromDays(8));  // 週次 7 日 + 境界マージン
```

ファイルごとに `(Path, Offset, KnownLength, LastWriteTicks, LastRequestId)` を `ledger.json` に永続化。

### 全部読み直す条件（3 つ）

```
① キャッシュが無い / SchemaVersion が違う
② len < Offset                      切り詰め
③ mtime が巻き戻った                ★ 同サイズ以上での置き換え。
                                       サイズだけ見ていると取りこぼす
それ以外 → Offset..len を読む
```

③は既存実装が実際に踏んだもの。**サイズ比較だけでは不十分**。

### ★ 窓読み（一括バッファしない）

差分を一度に全部バッファすると、巨大なトランスクリプトでバイト配列＋文字列＋行配列が**数百 MB** になり UI がブロックする。**8MB ずつの窓**で進める。

```
・窓の境界にまたがる不完全な行は「バイト列のまま」持ち越す
  ★ 文字列にするとマルチバイト文字が割れて U+FFFD になり、バイト数も狂う（日本語パスで即死）
・処理しきれなかった末尾の分は Offset を巻き戻して次回読み直す
・末尾が '\n' で終わっていなければ最後の '\n' までしか処理しない
  （Claude Code は追記中なので最終行が途中で切れている可能性が常にある）
```

### ⚠ 短い読み取りを必ず考慮する

`stat` と `read` の間にファイルが切り詰められると、**要求バイト数より少なく読める**。

- ❌ `ReadExactly` — EOF で例外を投げる。追記中のファイルでは踏む
- ✅ `Read()` の**戻り値でスライスする**。バッファは `GC.AllocateUninitializedArray` ではなくゼロ初期化のものを使う（未初期化メモリをパースしないため）

`FileShare.ReadWrite | FileShare.Delete` で開き `Seek` → `Read` → `ReadOnlySpan<byte>` 上で `IndexOf((byte)'\n')` ループ。`StreamReader.ReadLine()` は使わない（オフセット管理が困難）。

**JSONL に `FileSystemWatcher` は使わない**（書き込み頻度が高くイベント嵐になる）。

> ✅ **検証方法（既存実装で実証済み）**: 窓サイズを **64KB に落として窓分割を強制**し、「全走査 / 増分 / 再全走査」の 3 通りで集計値が**完全に一致**することを確認する。窓境界のバグはこれをやらないと表に出ない。Phase 4 の必須テストに入れること。

### 11-2. パース

```csharp
if (line.IndexOf("\"usage\""u8) < 0) return null;               // ① バイト列で高速フィルタ
// （"type":"assistant" ではなく "usage" で弾く。巨大な attachment 行を JSON.parse せず素通りできる）
// ② Utf8JsonReader で必要フィールドのみ。iterations に来たら reader.Skip()
string key = requestId ?? messageId ?? uuid;                    // ③ 5-1 の対策
if (key == _lastRequestId) return null;                         //    重複は必ず連続して現れる
_lastRequestId = key;
if (model is null or "" or "<synthetic>") return null;
```

**`HashSet` は要らない**（5-1 参照）。重複は必ず連続して現れるので、**直前の `requestId` を 1 つ覚えるだけ**で排除できる。増分 tail と組み合わせるため、この「最後に見た `requestId`」をファイルカーソルと一緒に `ledger.json` へ永続化する。

`ledger.json` に `SchemaVersion` を持たせ、パーサを変えたらバージョンを上げて**全再計算**させる（集計ロジックのバグを引きずらない）。

> ⚠ **この簡略化は「重複は必ず連続」という実測（3 ファイル・非連続の重複 0 件）に依存している。** Phase 4 の検証で ccusage と ±5% 以内に収まらなかった場合は、まずここを `HashSet` 版に戻して切り分けること。

### 11-3. 更新タイミング

| タイマー | 間隔 | 対象 |
|---|---|---|
| 軽量 tail | **20 秒** | アクティブセッションの JSONL のみ（通常 1〜2 ファイル） |
| 全体スキャン | 120 秒 | 8 日窓（mtime フィルタで実際に読むのは変化分のみ） |
| 起動時 | 1 回 | `ledger.json` の差分。無ければフル走査（実測 1 秒未満） |

### 11-4. 永続キャッシュ

`%LOCALAPPDATA%\ClaudeUsageTray\cache\`:

| ファイル | 用途 |
|---|---|
| `last-usage.json` | 直近成功レスポンスの生 JSON。**起動直後に即アイコンを出す**（起動 → 空白数秒、を避ける）。スキーマ変更時の diff 用も兼ねる |
| `utilization-history.jsonl` | `{ts, session, weekly, scoped[]}`。予測用。8 日でローテート |
| `ledger.json` | JSONL 集計の中間状態 |
| `last-error-body.txt` | パース失敗時の生ボディ |

**書き込みは必ず atomic**（`WriteAllText(tmp)` → `File.Move(tmp, dst, overwrite: true)`）。読み込み側も try/catch で失敗したら黙って捨てて作り直す。