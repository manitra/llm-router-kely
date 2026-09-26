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
        bool supportsReasoning = false)
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

    internal byte[] AliasUtf8 { get; }

    public ReadOnlyMemory<byte> ReplacementJsonUtf8 { get; }
}
