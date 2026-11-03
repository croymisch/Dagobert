using System;

namespace Dagobert
{
  internal sealed class NewPriceEventArgs(int newPrice, long? lowestNqListingPrice = null) : EventArgs
  {
    public int NewPrice { get; } = newPrice;
    public long? LowestNqListingPrice { get; } = lowestNqListingPrice;
  }
}