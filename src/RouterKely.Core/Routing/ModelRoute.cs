using System.Text;

namespace RouterKely.Core.Routing;

public sealed class ModelRoute
{
    public ModelRoute(
        int statisticsIndex,
        string alias,
        string upstreamModel,
        long inputNanoUsdPerMillion = 0,
        long cachedInputNanoUsdPerMillion = 0,
        long outputNanoUsdPerMillion = 0,
        int? maxInputTokens = null,
        int? maxOutputTokens = null,
        bool supportsReasoning = false,
        bool supportsVision = false,
        string[]? reasoningEffortLevels = null,
        int? maxContextTokens = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamModel);

        StatisticsIndex = statisticsIndex;
        Alias = alias;
        UpstreamModel = upstreamModel;
        InputNanoUsdPerMillion = inputNanoUsdPerMillion;
        CachedInputNanoUsdPerMillion = cachedInputNanoUsdPerMillion;
        OutputNanoUsdPerMillion = outputNanoUsdPerMillion;
        MaxInputTokens = maxInputTokens;
        MaxOutputTokens = maxOutputTokens;
        SupportsReasoning = supportsReasoning;
        SupportsVision = supportsVision;
        ReasoningEffortLevels = reasoningEffortLevels;
        MaxContextTokens = maxContextTokens;
        AliasUtf8 = Encoding.UTF8.GetBytes(alias);
        ReplacementJsonUtf8 = Encoding.UTF8.GetBytes($"\"{upstreamModel}\"");
    }

    public int StatisticsIndex { get; }

    public string Alias { get; }

    public string UpstreamModel { get; }

    public long InputNanoUsdPerMillion { get; }

    public long CachedInputNanoUsdPerMillion { get; }

    public long OutputNanoUsdPerMillion { get; }

    public int? MaxInputTokens { get; }

    public int? MaxOutputTokens { get; }

    public bool SupportsReasoning { get; }

    public bool SupportsVision { get; }

    /// <summary>The reasoning-effort levels advertised for this route, or null to omit them.</summary>
    public string[]? ReasoningEffortLevels { get; }

    /// <summary>
    /// The input budget that stays jointly satisfiable with <see cref="MaxOutputTokens"/>.
    /// The upstream counts prompt and completion tokens against one context window, so a client
    /// that reserves <see cref="MaxOutputTokens"/> as its completion allowance may only send
    /// <c>context - output</c> prompt tokens. Clients derive their request budget from the
    /// advertised pair, so the advertised pair must not exceed the upstream window.
    /// </summary>
    public int? EffectiveMaxInputTokens
    {
        get
        {
            if (MaxContextTokens is not { } context || MaxOutputTokens is not { } output)
                return MaxInputTokens;
            int affordable = Math.Max(1, context - output);
            return MaxInputTokens is int input ? Math.Min(input, affordable) : affordable;
        }
    }

    /// <summary>The upstream context window shared by prompt and completion tokens.</summary>
    public int? MaxContextTokens { get; }

    internal byte[] AliasUtf8 { get; }

    public ReadOnlyMemory<byte> ReplacementJsonUtf8 { get; }
}
