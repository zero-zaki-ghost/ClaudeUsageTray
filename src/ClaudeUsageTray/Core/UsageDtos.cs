using System.Text.Json.Serialization;

namespace ClaudeUsageTray.Core;

/// <summary>
/// /api/oauth/usage のレスポンス。
///
/// ★ サーバー側スキーマは passthrough（未知フィールドが増えうる）。
///   System.Text.Json は既定で未知プロパティを無視するので、そのまま耐える。
///
/// ★ five_hour / seven_day / nimbus_quill / cinder_cove などのトップレベル
///   コードネーム窓は「あえて」持たない。分類は limits[] だけで行う
///   （内部スキーマに "Classify a row on this, never on a label." と明記されている）。
/// </summary>
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
