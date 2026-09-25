using System.Text;
using RouterKely.Core.Routing;
using Xunit;

namespace RouterKely.Unit.Routing;

public sealed class ModelPrefixScannerTests
{
    private static readonly ModelRoute[] Routes =
    [
        new("deepseek-fast", "deepseek-chat"),
        new("deepseek-pro", "deepseek-reasoner")
    ];

    [Fact]
    public void ScanFindsTopLevelModel()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"metadata\":{\"model\":\"ignored\"},\"model\":\"deepseek-fast\",\"stream\":true}");

        ModelScanStatus status = ModelPrefixScanner.Scan(json, true, Routes, out ModelRewrite rewrite);

        Assert.Equal(ModelScanStatus.Found, status);
        Assert.Equal("deepseek-fast", rewrite.Route.Alias);
        Assert.Equal("\"deepseek-fast\"", Encoding.UTF8.GetString(json.AsSpan(rewrite.ValueStart, rewrite.ValueLength)));
    }

    [Fact]
    public void ScanRequestsMoreDataForIncompleteModel()
    {
        byte[] json = "{\"model\":\"deep"u8.ToArray();

        ModelScanStatus status = ModelPrefixScanner.Scan(json, false, Routes, out _);

        Assert.Equal(ModelScanStatus.NeedMoreData, status);
    }

    [Fact]
    public void ScanRejectsUnknownModel()
    {
        byte[] json = "{\"model\":\"unknown\"}"u8.ToArray();

        ModelScanStatus status = ModelPrefixScanner.Scan(json, true, Routes, out _);

        Assert.Equal(ModelScanStatus.UnknownModel, status);
    }
}

