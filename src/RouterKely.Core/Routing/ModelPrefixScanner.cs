using System.Text.Json;

namespace RouterKely.Core.Routing;

public enum ModelScanStatus
{
    NeedMoreData,
    Found,
    InvalidJson,
    MissingModel,
    UnknownModel
}

public readonly record struct ModelRewrite(
    int ValueStart,
    int ValueLength,
    ModelRoute Route);

public static class ModelPrefixScanner
{
    public static ModelScanStatus Scan(
        ReadOnlySpan<byte> json,
        bool isFinalBlock,
        IReadOnlyList<ModelRoute> routes,
        out ModelRewrite rewrite)
    {
        rewrite = default;

        try
        {
            var reader = new Utf8JsonReader(json, isFinalBlock, default);
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName ||
                    reader.CurrentDepth != 1 ||
                    !reader.ValueTextEquals("model"u8))
                {
                    continue;
                }

                if (!reader.Read())
                    return isFinalBlock ? ModelScanStatus.InvalidJson : ModelScanStatus.NeedMoreData;

                if (reader.TokenType != JsonTokenType.String)
                    return ModelScanStatus.InvalidJson;

                foreach (ModelRoute route in routes)
                {
                    if (!reader.ValueTextEquals(route.AliasUtf8))
                        continue;

                    rewrite = new ModelRewrite(
                        checked((int)reader.TokenStartIndex),
                        checked((int)(reader.BytesConsumed - reader.TokenStartIndex)),
                        route);
                    return ModelScanStatus.Found;
                }

                return ModelScanStatus.UnknownModel;
            }

            return isFinalBlock ? ModelScanStatus.MissingModel : ModelScanStatus.NeedMoreData;
        }
        catch (JsonException)
        {
            return ModelScanStatus.InvalidJson;
        }
    }
}

