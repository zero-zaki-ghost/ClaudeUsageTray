using System.Text.Json;
using ClaudeUsageTray.Config;

namespace ClaudeUsageTray.Core;

internal static class CredentialsReader
{
    /// <summary>直前に読めた値。書き込み途中を掴んだときのフォールバックに使う。</summary>
    private static Credentials? _last;

    /// <summary>
    /// 毎ポーリングで呼ぶ。FileSystemWatcher は使わない
    /// （本体が tmp+rename で書く場合に Changed が来ない／書き込み途中を掴む／
    ///   .claude 配下の他の書き込みでイベント嵐になる、の 3 点による）。
    /// ファイルは 576 バイト程度なので毎回読んでも実質ゼロコスト。
    /// </summary>
    public static Credentials Read()
    {
        string path = AppPaths.CredentialsFile;
        string raw;

        try
        {
            raw = ReadRaw(path);
        }
        catch (FileNotFoundException)
        {
            throw new CredentialsUnavailableException(
                "認証情報が見つかりません。Claude Code にサインインしてください。");
        }
        catch (Exception ex)
        {
            if (_last is not null) return _last;
            throw new CredentialsUnavailableException("認証情報を読めませんでした。", ex);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                throw new CredentialsUnavailableException(
                    "claudeAiOauth がありません。API キー構成の可能性があります。");

            string? token = oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
                throw new CredentialsUnavailableException("accessToken が空です。");

            // expiresAt は epoch "ミリ秒"。秒と取り違えると 1970 年扱いになる。
            DateTimeOffset expires = default;
            if (oauth.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out long ms) && ms > 0)
                expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);

            string? sub = oauth.TryGetProperty("subscriptionType", out var s) ? s.GetString() : null;

            var creds = new Credentials(token!, expires, sub);
            _last = creds;
            return creds;
        }
        catch (JsonException)
        {
            // 書き込み途中を掴んだだけの可能性が高い。前回値で走り続ける。
            if (_last is not null)
            {
                Log.Warn("認証情報の JSON が不正でした。前回のトークンを使い続けます。");
                return _last;
            }
            throw new CredentialsUnavailableException("認証情報の JSON が不正です。");
        }
    }

    /// <summary>
    /// ★ FileShare.ReadWrite | FileShare.Delete が必須。
    ///   FileShare.Read だけで開くと、こちらが Claude Code 本体のトークン書き戻しを
    ///   ブロックしてしまう（＝本体の認証を壊す）。これが最悪のシナリオ。
    /// </summary>
    private static string ReadRaw(string path)
    {
        const int attempts = 4;
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < attempts - 1)
            {
                Thread.Sleep(120);
            }
        }
    }
}
