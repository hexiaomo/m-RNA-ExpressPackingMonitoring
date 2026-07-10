using ExpressPackingMonitoring.Services;
using Xunit;

namespace ExpressPackingMonitoring.Tests;

public sealed class WebRequestLimitTests
{
    [Fact]
    public void ValidateOrderInfoItems_AcceptsBoundarySizedBatch()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems)
            .Select(index => new OrderInfo
            {
                TrackingNumber = $"TRACK-{index}",
                BuyerMessage = new string('买', 2000),
                SellerMemo = new string('卖', 2000),
                ProductInfo = new string('商', 4000)
            })
            .ToList();

        WebServer.ValidateOrderInfoItems(items);
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsTooManyOrders()
    {
        var items = Enumerable.Range(0, WebServer.MaxOrderInfoItems + 1)
            .Select(index => new OrderInfo { TrackingNumber = index.ToString() })
            .ToList();

        Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));
    }

    [Fact]
    public void ValidateOrderInfoItems_RejectsOversizedField()
    {
        var items = new List<OrderInfo>
        {
            new() { TrackingNumber = "TRACK-1", BuyerMessage = new string('x', 2001) }
        };

        var error = Assert.Throws<InvalidDataException>(() => WebServer.ValidateOrderInfoItems(items));

        Assert.Contains("买家留言过长", error.Message);
    }
}
