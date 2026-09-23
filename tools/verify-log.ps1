<#
.SYNOPSIS
  ClaudeUsageTray のログを読んで、未検証だった項目に「できている / できていない」を出す。

.DESCRIPTION
  ★ このスクリプトの存在理由
    「再起動したあとログを見てください」という手順は、**どの行を見ればいいか
    分からない**ので実質的に機能しない。人間が目視で探す代わりに、
    判定そのものをここに書いておく。

    既定では **前回の Windows 起動以降** だけを見る。再起動後の検証は
    「再起動してから、このコマンドを 1 回叩く」だけで済む。

.PARAMETER All
  起動以降ではなく、ログ全体を対象にする。

.PARAMETER LogPath
  ログの場所。既定は %LOCALAPPDATA%\ClaudeUsageTray\logs\tray.log

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\verify-log.ps1

.NOTES
  終了コード 0 = 問題なし / 1 = 要確認の項目あり
#>
[CmdletBinding()]
param(
    [switch]$All,
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'ClaudeUsageTray\logs\tray.log')
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- ログの読み込み
if (-not (Test-Path $LogPath)) {
    Write-Host "ログが見つかりません: $LogPath" -ForegroundColor Red
    Write-Host "アプリを一度も起動していないか、配置先が違います。"
    exit 1
}

$boot = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
$since = if ($All) { [datetime]::MinValue } else { $boot }

# 行頭の "yyyy-MM-dd HH:mm:ss.fff +09:00 [LEVEL] 本文" を分解する。
$pattern = '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\.\d{3} \S+ \[(?<lv>\w+)\] (?<msg>.*)$'

$entries = @(
    foreach ($line in (Get-Content -LiteralPath $LogPath -Encoding UTF8)) {
        if ($line -notmatch $pattern) { continue }
        $ts = [datetime]::ParseExact($Matches.ts, 'yyyy-MM-dd HH:mm:ss', $null)
        if ($ts -lt $since) { continue }
        [pscustomobject]@{ Time = $ts; Level = $Matches.lv; Message = $Matches.msg }
    }
)

Write-Host ''
Write-Host '=== ClaudeUsageTray 検証 ===' -ForegroundColor Cyan
if ($All) {
    Write-Host ('  対象: ログ全体（{0} 行）' -f $entries.Count)
} else {
    Write-Host ('  対象: 前回の Windows 起動 {0:yyyy-MM-dd HH:mm} 以降（{1} 行）' -f $boot, $entries.Count)
}
Write-Host ''

$issues = 0

# いま動いているプロセスの開始時刻。これより前の失敗は「直す前の記録」なので、
# そう分かるように添える。付けないと、修正済みの問題をいつまでも警告し続ける。
$runningSince = $null
$proc = Get-Process ClaudeUsageTray -ErrorAction SilentlyContinue |
        Sort-Object StartTime | Select-Object -Last 1
if ($proc) { $runningSince = $proc.StartTime }

function OldNote {
    param([datetime]$Last)

    if ($runningSince -and $Last -lt $runningSince) {
        return "`n※ これは現在動いているビルド（$($runningSince.ToString('HH:mm')) 起動）より前の記録。`n   再起動すると対象期間が切り替わり、直っていれば消える。"
    }
    return ''
}

function Report {
    param([string]$Verdict, [string]$Title, [string]$Detail)

    $color = switch ($Verdict) {
        'OK'   { 'Green' }
        'NG'   { 'Red' }
        default { 'Yellow' }
    }
    $mark = switch ($Verdict) { 'OK' { '[ OK ]' } 'NG' { '[ NG ]' } default { '[ ?? ]' } }

    Write-Host ('  {0} {1}' -f $mark, $Title) -ForegroundColor $color
    foreach ($d in ($Detail -split "`n")) { Write-Host ('         ' + $d) }
    Write-Host ''

    if ($Verdict -eq 'NG') { $script:issues++ }
}

# ---------------------------------------------------------------- 1. 自動起動
$startup = @($entries | Where-Object { $_.Message -like '*startup=True*' })

if ($startup.Count -gt 0) {
    Report 'OK' '自動起動' ('Run キー経由で起動した: {0:HH:mm:ss}' -f $startup[-1].Time)
} elseif ($All) {
    Report 'NG' '自動起動' 'startup=True の記録が 1 度も無い。自動起動が効いていない可能性がある。'
} else {
    Report '??' '自動起動' @'
今回の起動では startup=True が出ていない。
手動で起動したか、--install を叩き直した直後ならこれで正常。
'@
}

# ---------------------------------------------------------------- 2. 429 の型
# 暴走型 = 失効トークンを投げ続けてサーバーに締められた状態。
#          実測では「401 が 3 回 → 429 が連続」という形で出る。
# 単発型 = サーバー都合。1 回だけで次の周期に戻る。こちらは出ても正常。
$http429 = @($entries | Where-Object { $_.Message -like '*429*' })
$http401 = @($entries | Where-Object { $_.Message -like '*401 を受け取りました*' })

$runaway = $false
for ($i = 0; $i -lt $http429.Count; $i++) {
    # 20 分の窓に 3 回以上 429 が入っていたら暴走とみなす
    $window = @($http429 | Where-Object {
        $_.Time -ge $http429[$i].Time -and $_.Time -lt $http429[$i].Time.AddMinutes(20)
    })
    if ($window.Count -ge 3) { $runaway = $true; break }
}

if ($runaway) {
    Report 'NG' '429（暴走型）' @"
20 分以内に 429 が 3 回以上出ている。失効トークンを投げ続けている疑い。
401: $($http401.Count) 回 / 429: $($http429.Count) 回
docs\設計.md の 16 章「懸念 4」を参照。$(OldNote $http429[-1].Time)
"@
} elseif ($http429.Count -gt 0) {
    Report 'OK' '429（単発型のみ）' @"
429 は $($http429.Count) 回だが連続していない。サーバー都合の単発で、
バックオフが受け止めている。これは正常。
最後: $($http429[-1].Time.ToString('HH:mm:ss'))
"@
} else {
    Report 'OK' '429' '1 度も出ていない。'
}

# ---------------------------------------------------------------- 3. 失効トークン
# 失効しているのに送ってしまうと暴走型 429 を招く。送らずに待てていればこの行が出る。
$held = @($entries | Where-Object { $_.Message -like '*失効しています*' })

if ($held.Count -gt 0 -and $http401.Count -eq 0) {
    Report 'OK' '失効トークンを送っていない' @"
失効を検出して送信を見送った: $($held.Count) 回
その間 401 は 0 回。狙いどおり。
"@
} elseif ($http401.Count -gt 0) {
    Report 'NG' '401 が出ている' @"
401: $($http401.Count) 回。失効チェックを通ったのに拒否されている。
失効以外の理由（サインアウト等）か、判定が効いていない。
最後: $($http401[-1].Time.ToString('HH:mm:ss'))$(OldNote $http401[-1].Time)
"@
} else {
    Report '??' '失効トークンの扱い' 'まだトークンが失効していないので判定材料が無い（正常）。'
}

# ---------------------------------------------------------------- 4. 起床シグナル
$wake = @($entries | Where-Object { $_.Message -like '起床:*' })

# ★ 「起床した」だけでは OS イベントの検証にならない。
#   手動更新・テスト・ハーネスからの起床は、こちらが呼んだだけで
#   OS が投げてきた証拠にはならない。ここを混ぜると
#   「確認できていないものを確認済みと表示する」ことになる。
$selfMade = @('手動', 'テスト', 'ハーネス')
$fromOs = @($wake | Where-Object {
    $reason = ($_.Message -split ':', 2)[1].Trim()
    $reason -notin $selfMade
})

if ($fromOs.Count -gt 0) {
    $reasons = ($fromOs | ForEach-Object { ($_.Message -split ':', 2)[1].Trim() } |
                Sort-Object -Unique) -join ' / '
    Report 'OK' '起床シグナル（OS 由来）' "$($fromOs.Count) 回。内訳: $reasons"
} elseif ($wake.Count -gt 0) {
    Report '??' '起床シグナル（OS 由来）' @"
起床は $($wake.Count) 回あるが、すべて手動・テスト由来。
**OS イベントはまだ観測されていない。**
スリープ復帰・ロック解除・ネットワーク再接続のいずれかが起きれば出る。
"@
} else {
    Report '??' '起床シグナル（OS 由来）' @'
まだ観測されていない。スリープ復帰・ロック解除・ネットワーク再接続の
いずれかが起きると「起床: <理由>」が出る。起きていないだけかもしれない。
'@
}

# ---------------------------------------------------------------- 5. 本体プロセスの検出
$idle = @($entries | Where-Object { $_.Message -like '*間隔を伸ばします*' -or $_.Message -like '*間隔を戻します*' })

if ($idle.Count -gt 0) {
    Report 'OK' '本体プロセスの検出' @"
$($idle.Count) 回切り替わっている。
最後: $($idle[-1].Time.ToString('HH:mm:ss')) $($idle[-1].Message)
"@
} else {
    Report '??' '本体プロセスの検出' @'
切り替わっていない。Claude Code をずっと起動したままなら、これで正常
（状態が変わったときだけ記録するため）。
'@
}

# ---------------------------------------------------------------- 6. 個人情報の漏れ
$leak = @($entries | Where-Object { $_.Message -match 'C:\\Users\\' })

if ($leak.Count -gt 0) {
    Report 'NG' 'ログにユーザー名が出ている' @"
$($leak.Count) 行。Log.Redact が効いていないか、古いビルドの記録。
ログを人に見せる前に確認すること。
最後: $($leak[-1].Time.ToString('HH:mm:ss'))$(OldNote $leak[-1].Time)
"@
} else {
    Report 'OK' 'ログにユーザー名が出ていない' '絶対パスは %USERPROFILE% に畳まれている。'
}

# ---------------------------------------------------------------- 7. いま取れているか
$cache = Join-Path $env:LOCALAPPDATA 'ClaudeUsageTray\cache\last-usage.json'

if (Test-Path $cache) {
    $age = (Get-Date) - (Get-Item $cache).LastWriteTime
    if ($age.TotalMinutes -le 15) {
        Report 'OK' 'いま取れている' ('最後の取得: {0:F0} 分前' -f $age.TotalMinutes)
    } else {
        Report 'NG' 'しばらく取れていない' @"
最後の取得: $([math]::Round($age.TotalMinutes)) 分前。
Claude Code にサインインしているか、ネットワークが生きているか確認する。
"@
    }
} else {
    Report '??' 'いま取れているか' '一度も取得できていない（キャッシュが無い）。'
}

# ----------------------------------------------------------------
Write-Host '============================' -ForegroundColor Cyan
if ($issues -eq 0) {
    Write-Host '  問題なし' -ForegroundColor Green
} else {
    Write-Host ('  要確認: {0} 件' -f $issues) -ForegroundColor Red
}
Write-Host ''
Write-Host '  [ ?? ] は「まだ材料が無い」であって異常ではない。'
Write-Host '  ログ全体を見るなら -All を付ける。'
Write-Host ''

# ⚠ 三項演算子（? :）は PowerShell 7 以降にしか無い。
#   Windows に最初から入っているのは 5.1 なので、ここでは使わない。
if ($issues -gt 0) { exit 1 } else { exit 0 }
