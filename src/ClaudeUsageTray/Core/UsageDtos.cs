using System.Text.Json.Serialization;

namespace ClaudeUsageTray.Core;

// =============================================================================
//  レスポンス DTO
// -----------------------------------------------------------------------------
//  【設計思想】
//
//  非公開 API を相手にするので「知らないものは黙って捨てる」姿勢で作る。
//
//  ・未知のプロパティは無視する（System.Text.Json の既定動作をそのまま使う）。
//    サーバー側スキーマは passthrough で、フィールドは今後も増える。
//
//  ・トップレベルのコードネーム窓（five_hour / nimbus_quill / cedar_ember …）は
//    あえて 1 つも定義しない。定義すると使いたくなり、名前に依存した分岐が
//    生まれる。分類は limits[] だけで行うという方針を、型の側から強制する。
//
//  ・数値はすべて nullable。percent が null で返ることを許容する。
//    0 と「値なし」を混同すると「使っていない」と誤表示してしまう。
// =============================================================================
/// <summary>/api/oauth/usage のレスポンス。</summary>
internal sealed class UsageResponseDto
{
    [JsonPropertyName("limits")]
    public List<LimitRowDto>? Limits { get; set; }
}

internal sealed class LimitRowDto
{
    /// <summary>session / weekly_all / weekly_scoped / 将来増えるもの。enum にしないこと。</summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }

    [JsonPropertyName("group")] public string? Group { get; set; }

    [JsonPropertyName("percent")] public double? Percent { get; set; }

    /// <summary>normal / warning / … 自前しきい値は作らずこれに従う。</summary>
    [JsonPropertyName("severity")] public string? Severity { get; set; }

    /// <summary>ISO8601（+00:00 オフセット付き）。DateTimeOffset.Parse で読む。</summary>
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }

    [JsonPropertyName("is_active")] public bool? IsActive { get; set; }

    [JsonPropertyName("scope")] public ScopeDto? Scope { get; set; }
}

internal sealed class ScopeDto
{
    [JsonPropertyName("model")] public ScopeNameDto? Model { get; set; }
    [JsonPropertyName("surface")] public ScopeNameDto? Surface { get; set; }
}

internal sealed class ScopeNameDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }

    /// <summary>"Fable" など。ハードコードせずここから拾う。</summary>
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
}
