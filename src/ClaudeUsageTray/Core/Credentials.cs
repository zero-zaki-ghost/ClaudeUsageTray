using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  認証情報
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  このアプリは Claude Code 本体の認証に "相乗り" する。自分では認証周りの
//  状態を一切持たないし、書き換えもしない。読むだけ。
//
//  理由は被害の非対称性にある。このツールが数字を出せなくても困るのは
//  「今どれくらい使ったか分からない」程度だが、本体の認証を壊すと
//  Claude Code そのものが使えなくなる。得られるものと失うものが釣り合わない。
//
//  この方針から自動的に決まること:
//    ・トークンのリフレッシュをしない（下記の注記）
//    ・ファイルを書かない。読むときもロックしない
//    ・トークンをメモリに持ち続けない。比較には指紋（ハッシュ先頭 8 バイト）を使う
//    ・ログにも UI にもトークンを出さない
// =============================================================================

/// <summary>
/// .credentials.json から読んだ最小限。
///
/// ★★ 固い制約: このアプリは自前で OAuth リフレッシュを行わない。
///
/// 技術的には POST /v1/oauth/token で可能だが、
///   ・リフレッシュのレート制限（4 時間に 1 回で 429）
///   ・Cloudflare WAF ブロックの既知バグ
///   ・失敗すると Claude Code 本体のセッションを壊す
/// があり、壊れたときの被害（Claude Code が使えなくなる）が、
/// このツールの便益を大きく上回る。
///
/// 代わりに毎ポーリングでファイルを読み直し、本体が書き戻した新しい
/// トークンに追従する。
/// </summary>
internal sealed record Credentials(string AccessToken, DateTimeOffset ExpiresAt, string? SubscriptionType)
{
    // ⚠ 以下 3 つは現時点でどこからも呼ばれていない。Phase 2 の 401 フロー
    //    （ShortWatch: 401 を受けたらトークンが入れ替わるまで .credentials.json の
    //    更新を短間隔で見張る）で使う前提で先に置いてある。
    //    Phase 2 に着手しないと決めたら消すこと。

    public TimeSpan Remaining => ExpiresAt - DateTimeOffset.UtcNow;

    public bool IsExpired => ExpiresAt != default && Remaining <= TimeSpan.Zero;

    /// <summary>
    /// 「前回と同じトークンか」の判定用。生のトークンを常駐オブジェクトに
    /// 持ち続けないための指紋。Phase 2 の 401 リトライ判定で使う。
    /// </summary>
    public string Fingerprint
    {
        get
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(Encoding.UTF8.GetBytes(AccessToken), hash);
            return Convert.ToHexString(hash[..8]);
        }
    }
}

internal sealed class CredentialsUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
