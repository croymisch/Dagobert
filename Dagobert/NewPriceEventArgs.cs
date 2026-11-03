using System;

namespace Dagobert
{
  internal sealed class NewPriceEventArgs(
    int newPrice,
    long? lowestNqListingPrice = null,
    bool marketBoardResponseReceived = true,
    long? marketBoardLowestPrice = null) : EventArgs
  {
    public int NewPrice { get; } = newPrice;
    public long? LowestNqListingPrice { get; } = lowestNqListingPrice;
    public bool MarketBoardResponseReceived { get; } = marketBoardResponseReceived;
    public long? MarketBoardLowestPrice { get; } = marketBoardLowestPrice;
  }
}