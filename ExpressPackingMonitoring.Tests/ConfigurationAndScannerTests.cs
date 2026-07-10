using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Input;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class ConfigurationAndScannerTests
{
    [Fact]
    public void NormalizeAfterLoad_ResolvesConflictingScannerModesAndBounds()
    {
        var config = new AppConfig
        {
            EnableGlobalKeyboard = true,
            EnableScannerAutoSubmit = true,
            ScannerAutoSubmitMinLength = 1,
            ScannerAutoSubmitQuietMs = 5000,
            ScannerAutoSubmitMaxAverageIntervalMs = 1,
            ScannerAutoSubmitMaxKeyIntervalMs = 1000
        };

        bool changed = AppConfig.NormalizeAfterLoad(config);

        Assert.True(changed);
        Assert.False(config.EnableGlobalKeyboard);
        Assert.Equal(4, config.ScannerAutoSubmitMinLength);
        Assert.Equal(600, config.ScannerAutoSubmitQuietMs);
        Assert.Equal(10, config.ScannerAutoSubmitMaxAverageIntervalMs);
        Assert.Equal(150, config.ScannerAutoSubmitMaxKeyIntervalMs);
    }

    [Theory]
    [InlineData(new double[] { 20, 25, 30, 20 }, 5, true)]
    [InlineData(new double[] { 20, 250, 20, 250 }, 5, false)]
    [InlineData(new double[] { 20 }, 3, false)]
    public void IsFastSequence_DistinguishesScannerAndManualTyping(
        double[] intervals,
        int characterCount,
        bool expected)
    {
        bool actual = ScannerAutoSubmitPolicy.IsFastSequence(
            intervals,
            characterCount,
            maxAverageIntervalMs: 60,
            maxKeyIntervalMs: 100);

        Assert.Equal(expected, actual);
    }
}
