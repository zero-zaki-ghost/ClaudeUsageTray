using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsageTray.Core;

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
    public TimeSpan Remaining => ExpiresAt - DateTimeOffset.UtcNow;

    public bool IsExpired => ExpiresAt != default && Remaining <= TimeSpan.Zero;

    /// <summary>
    /// 「前回と同じトークンか」の判定用。生のトークンを常駐オブジェクトに
    /// 持ち続けないための指紋。
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
