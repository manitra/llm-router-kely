using System.Text;

namespace RouterKely.Core.Routing;

public sealed class ModelRoute
{
    public ModelRoute(string alias, string upstreamModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        ArgumentException.ThrowIfNullOrWhiteSpace(upstreamModel);

        Alias = alias;
        UpstreamModel = upstreamModel;
        AliasUtf8 = Encoding.UTF8.GetBytes(alias);
        ReplacementJsonUtf8 = Encoding.UTF8.GetBytes($"\"{upstreamModel}\"");
    }

    public string Alias { get; }

    public string UpstreamModel { get; }

    internal byte[] AliasUtf8 { get; }

    public ReadOnlyMemory<byte> ReplacementJsonUtf8 { get; }
}
