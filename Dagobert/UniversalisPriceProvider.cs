using Dalamud.Game.Text.SeStringHandling;
using ECommons;
using ECommons.DalamudServices;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace Dagobert;

internal sealed class UniversalisPriceProvider : IDisposable
{
  private readonly Lumina.Excel.ExcelSheet<Item> _items;
  private readonly UniversalisClient _client;

  public UniversalisPriceProvider()
  {
    _items = Svc.Data.GetExcelSheet<Item>();
    _client = new UniversalisClient();
  }

  public bool CanResolveItem(string itemName, string rawItemName) => TryGetItem(itemName, rawItemName, out _, out _);

  public async Task<UniversalisPriceResult?> GetPrices(string itemName, string rawItemName, int currentPrice, CancellationToken cancellationToken)
  {
    if (!TryGetItem(itemName, rawItemName, out var itemId, out var hqOnly))
    {
      Svc.Log.Warning($"Could not resolve item id for Universalis price check: {NormalizeItemName(itemName)}");
      return null;
    }

    var dataCenterName = Svc.Objects.LocalPlayer?.CurrentWorld.ValueNullable?.DataCenter.ValueNullable?.Name.ToString();
    if (string.IsNullOrWhiteSpace(dataCenterName))
    {
      Svc.Log.Warning("Could not resolve current data center for Universalis price check");
      return null;
    }

    var marketData = await _client.GetMarketData(itemId, dataCenterName, hqOnly, cancellationToken).ConfigureAwait(false);
    if (!marketData.HasData || marketData.Listings.Count == 0)
      return null;

    var listing = marketData.Listings
      .Where(listing => listing.PricePerUnit > 0 && (!hqOnly || listing.Hq))
      .OrderBy(listing => listing.PricePerUnit)
      .FirstOrDefault(listing => listing.PricePerUnit != currentPrice);

    if (listing == null)
      return null;

    var ownRetainer = ulong.TryParse(listing.RetainerId, out var retainerId)
                      && Plugin.Configuration.SeenRetainers.Contains(retainerId);
    Svc.Log.Debug($"Universalis lowest data center price: {listing.PricePerUnit} on {listing.WorldName ?? dataCenterName}");
    return new UniversalisPriceResult(listing.PricePerUnit, CalculateNewPrice(listing.PricePerUnit, ownRetainer));
  }

  public async Task<Dictionary<UniversalisItemKey, List<UniversalisPriceResult>>> GetPrices(
    IReadOnlyCollection<RetainerMarketItem> items,
    CancellationToken cancellationToken)
  {
    var worldName = Svc.Objects.LocalPlayer?.CurrentWorld.ValueNullable?.Name.ToString();
    if (string.IsNullOrWhiteSpace(worldName))
      throw new InvalidOperationException("Could not resolve current world for Universalis batch price check.");

    var itemIds = items.Select(item => item.ItemId).Where(itemId => itemId != 0).Distinct().ToArray();
    if (itemIds.Length == 0)
      return [];

    Svc.Log.Information($"Refreshing Universalis prices for {itemIds.Length} catalog items on world {worldName}");
    var marketDataByItemId = await _client.GetMarketData(itemIds, worldName, cancellationToken).ConfigureAwait(false);
    var prices = new Dictionary<UniversalisItemKey, List<UniversalisPriceResult>>();

    foreach (var item in items)
    {
      if (!marketDataByItemId.TryGetValue(item.ItemId, out var marketData)
          || !marketData.HasData
          || marketData.Listings.Count == 0)
        continue;

      var levelOneEquippable = ItemNameResolver.IsLevelOneEquippable(item.ItemId);
      var hqOnly = Plugin.Configuration.HQ && item.IsHq && !levelOneEquippable;
      if (levelOneEquippable)
        Svc.Log.Debug($"Item {item.ItemId} is equippable at item level 1; Universalis batch pricing will include HQ and NQ listings");
      var listings = marketData.Listings
        .Where(candidate => candidate.PricePerUnit > 0 && (!hqOnly || candidate.Hq))
        .OrderBy(candidate => candidate.PricePerUnit)
        .ToArray();
      if (listings.Length == 0)
        continue;

      var key = new UniversalisItemKey(item.ItemId, hqOnly);
      prices[key] = listings
        .Select(listing =>
        {
          var ownRetainer = ulong.TryParse(listing.RetainerId, out var retainerId)
                            && Plugin.Configuration.SeenRetainers.Contains(retainerId);
          return new UniversalisPriceResult(
            listing.PricePerUnit,
            CalculateNewPrice(listing.PricePerUnit, ownRetainer));
        })
        .ToList();
    }

    Svc.Log.Information($"Universalis batch price refresh completed: {prices.Count} item qualities priced on world {worldName}");
    return prices;
  }

  public void Dispose()
  {
    _client.Dispose();
  }

  private bool TryGetItem(string itemName, string rawItemName, out uint itemId, out bool hqOnly)
  {
    var itemHq = itemName.Contains('\uE03C') || rawItemName.Contains('\uE03C');
    var normalizedItemName = NormalizeItemName(itemName);

    itemId = ResolveItemId(normalizedItemName, itemName);
    if (itemId == 0)
      itemId = ResolveItemId(normalizedItemName, rawItemName);

    var levelOneEquippable = ItemNameResolver.IsLevelOneEquippable(itemId);
    hqOnly = Plugin.Configuration.HQ && itemHq && !levelOneEquippable;
    if (levelOneEquippable)
      Svc.Log.Debug($"Item {itemId} is equippable at item level 1; Universalis pricing will include HQ and NQ listings");
    return itemId != 0;
  }

  private uint ResolveItemId(string normalizedItemName, string rawItemName)
  {
    var exactMatch = _items
      .Where(item => item.Name.GetText().Equals(normalizedItemName, StringComparison.OrdinalIgnoreCase))
      .Select(item => item.RowId)
      .FirstOrDefault();

    if (exactMatch != 0)
      return exactMatch;

    return _items
      .Select(item => new
      {
        item.RowId,
        Name = item.Name.GetText(),
      })
      .Where(item => item.Name.Length > 0 && rawItemName.Contains(item.Name, StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(item => item.Name.Length)
      .Select(item => item.RowId)
      .FirstOrDefault();
  }

  private static int CalculateNewPrice(long pricePerUnit, bool ownRetainer)
  {
    var price = (int)Math.Min(pricePerUnit, int.MaxValue);

    if (!Plugin.Configuration.UndercutSelf && ownRetainer)
      return price;

    if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
      return Math.Max(price - Plugin.Configuration.UndercutAmount, 1);

    return (int)Math.Max((100L - Plugin.Configuration.UndercutAmount) * price / 100L, 1);
  }

  private static string NormalizeItemName(string itemName)
  {
    var normalizedItemName = itemName.Replace("\uE03C", string.Empty).Trim();

    try
    {
      var text = SeString.Parse(Encoding.UTF8.GetBytes(normalizedItemName)).GetText().Trim();
      if (!string.IsNullOrEmpty(text))
        normalizedItemName = text;
    }
    catch
    {
      // If this is already plain text, or malformed SeString text, fall back to the raw visible substring matching.
    }

    return normalizedItemName;
  }
}

internal readonly record struct UniversalisPriceResult(long LowestListingPrice, int SuggestedPrice);

internal readonly record struct UniversalisItemKey(uint ItemId, bool HqOnly);
