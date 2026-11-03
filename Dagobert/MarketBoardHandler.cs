using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Network.Structures;
using ECommons;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using System;
using System.Linq;

namespace Dagobert
{
  internal unsafe sealed class MarketBoardHandler : IDisposable
  {
    private const int UnknownRequestId = -1;
    private const int ListingsPerBatch = 10;

    private readonly Lumina.Excel.ExcelSheet<Item> _items;
    private bool _newRequest;
    private bool _useHq;
    private bool _itemHq;
    private bool _itemLevelOneEquippable;
    private bool _marketBoardResponseReceived;
    private long? _lowestNqListingPrice;
    private long? _lowestMarketBoardPrice;
    private int _lastRequestId = UnknownRequestId;
    private int _pendingNoMatchRequestId = UnknownRequestId;
    private long _pendingNoMatchTimeoutAt;
    private long _pendingNoOfferingsTimeoutAt;

    private int NewPrice
    {
      get => _newPrice;
      set
      {
        _newPrice = value;
        NewPriceReceived?.Invoke(
          this,
          new NewPriceEventArgs(
            NewPrice,
            _lowestNqListingPrice,
            _marketBoardResponseReceived,
            _lowestMarketBoardPrice));
        _lowestNqListingPrice = null;
        _lowestMarketBoardPrice = null;
      }
    }
    private int _newPrice;

    public event EventHandler<NewPriceEventArgs>? NewPriceReceived;

    public void SetCurrentItemForPriceCheck(uint itemId, bool isHq)
    {
      _itemHq = isHq;
      _itemLevelOneEquippable = ItemNameResolver.IsLevelOneEquippable(itemId);
      if (_itemLevelOneEquippable)
        Svc.Log.Debug($"Item {itemId} is equippable at item level 1; market-board price check will include HQ and NQ listings");
    }

    public MarketBoardHandler()
    {
      _items = Svc.Data.GetExcelSheet<Item>();

      Plugin.MarketBoard.OfferingsReceived += MarketBoardOnOfferingsReceived;
      Svc.Framework.Update += FrameworkOnUpdate;

      Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
      Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
    }

    public void Dispose()
    {
      Plugin.MarketBoard.OfferingsReceived -= MarketBoardOnOfferingsReceived;
      Svc.Framework.Update -= FrameworkOnUpdate;
      Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerSell", AddonRetainerSellPostSetup);
      Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "ItemSearchResult", ItemSearchResultPostSetup);
    }

    private void MarketBoardOnOfferingsReceived(IMarketBoardCurrentOfferings currentOfferings)
    {
      if (!_newRequest)
        return;

      if (currentOfferings.RequestId == _lastRequestId)
        return;

      ClearPendingNoOfferings();

      var lowestMarketBoardListing = currentOfferings.ItemListings
        .Where(listing => listing.PricePerUnit > 0)
        .MinBy(listing => listing.PricePerUnit);
      if (lowestMarketBoardListing != null)
        _lowestMarketBoardPrice = !_lowestMarketBoardPrice.HasValue
          ? lowestMarketBoardListing.PricePerUnit
          : Math.Min(_lowestMarketBoardPrice.Value, lowestMarketBoardListing.PricePerUnit);

      if (currentOfferings.ItemListings.Count == 0)
      {
        CompletePriceRequest(-1, currentOfferings.RequestId);
        return;
      }

      var skipNq = _useHq && _items.Single(j => j.RowId == currentOfferings.ItemListings[0].ItemId).CanBeHq;
      if (skipNq)
      {
        var lowestNqListing = currentOfferings.ItemListings
          .Where(listing => !listing.IsHq && listing.PricePerUnit > 0)
          .MinBy(listing => listing.PricePerUnit);
        if (lowestNqListing != null)
          _lowestNqListingPrice = !_lowestNqListingPrice.HasValue
            ? lowestNqListing.PricePerUnit
            : Math.Min(_lowestNqListingPrice.Value, lowestNqListing.PricePerUnit);
      }

      var i = 0;
      while (i < currentOfferings.ItemListings.Count)
      {
        if (skipNq && !currentOfferings.ItemListings[i].IsHq)
          i++;
        else
          break;
      }

      // offerings arrive in batches of 10; if no match in this batch, wait for the next
      if (i >= currentOfferings.ItemListings.Count)
      {
        if (currentOfferings.ItemListings.Count < ListingsPerBatch)
        {
          CompletePriceRequest(-1, currentOfferings.RequestId);
          return;
        }

        _pendingNoMatchRequestId = currentOfferings.RequestId;
        _pendingNoMatchTimeoutAt = Environment.TickCount64 + GetMarketBoardResultTimeoutMs();
        return;
      }

      ClearPendingNoMatch();

      int price;

      if (!Plugin.Configuration.UndercutSelf && IsOwnRetainer(currentOfferings.ItemListings[i].RetainerId))
        price = (int)currentOfferings.ItemListings[i].PricePerUnit;
      else if (Plugin.Configuration.UndercutMode == UndercutMode.FixedAmount)
        price = Math.Max((int)currentOfferings.ItemListings[i].PricePerUnit - Plugin.Configuration.UndercutAmount, 1);
      else
        price = Math.Max((100 - Plugin.Configuration.UndercutAmount) * (int)currentOfferings.ItemListings[i].PricePerUnit / 100, 1);

      CompletePriceRequest(price, currentOfferings.RequestId);
    }

    private void FrameworkOnUpdate(object _)
    {
      if (!_newRequest)
        return;

      var now = Environment.TickCount64;
      if (_pendingNoMatchRequestId >= 0 && now >= _pendingNoMatchTimeoutAt)
      {
        Svc.Log.Debug("No matching market board listing received before timeout");
        CompletePriceRequest(-1, _pendingNoMatchRequestId);
        return;
      }

      if (_pendingNoOfferingsTimeoutAt > 0 && now >= _pendingNoOfferingsTimeoutAt)
      {
        var emptySearchResults =
          GenericHelpers.TryGetAddonByName<AddonItemSearchResult>("ItemSearchResult", out var searchResult)
          && GenericHelpers.IsAddonReady(&searchResult->AtkUnitBase)
          && searchResult->Results != null
          && searchResult->Results->GetItemCount() == 0;
        if (emptySearchResults)
          Svc.Log.Information("Market Board search completed with no listings; skipping price-check retries");
        else
          Svc.Log.Debug("No market board offerings received before timeout");

        CompletePriceRequest(-1, UnknownRequestId, marketBoardResponseReceived: emptySearchResults);
      }
    }

    private void ItemSearchResultPostSetup(AddonEvent type, AddonArgs args)
    {
      _newRequest = true;
      _useHq = Plugin.Configuration.HQ && _itemHq && !_itemLevelOneEquippable;
      ClearPendingNoMatch();
      _lowestNqListingPrice = null;
      _lowestMarketBoardPrice = null;
      _pendingNoOfferingsTimeoutAt = Environment.TickCount64 + GetMarketBoardResultTimeoutMs();
    }

    private unsafe void AddonRetainerSellPostSetup(AddonEvent type, AddonArgs args)
    {
      string nodeText = ((AddonRetainerSell*)args.Addon.Address)->ItemName->NodeText.ToString();
      _itemHq = nodeText.Contains('\uE03C');
      _itemLevelOneEquippable =
        ItemNameResolver.TryGetItemId(nodeText, nodeText, out var itemId)
        && ItemNameResolver.IsLevelOneEquippable(itemId);
    }

    public void PopulateRetainerCache()
    {
      bool changed = false;
      var retainerManager = RetainerManager.Instance();

      for (uint i = 0; i < retainerManager->GetRetainerCount(); ++i)
      {
        if (!Plugin.Configuration.SeenRetainers.Contains(retainerManager->GetRetainerBySortedIndex(i)->RetainerId))
        {
          Plugin.Configuration.SeenRetainers.Add(retainerManager->GetRetainerBySortedIndex(i)->RetainerId);
          changed = true;
        }
        
      }

      if (changed)
        Plugin.Configuration.Save();
    }

    private static bool IsOwnRetainer(ulong retainerId) => Plugin.Configuration.SeenRetainers.Contains(retainerId);

    private static int GetMarketBoardResultTimeoutMs() => Math.Max(Plugin.Configuration.MarketBoardKeepOpenMS, 1000);

    private void CompletePriceRequest(int price, int requestId, bool marketBoardResponseReceived = true)
    {
      ClearPendingNoMatch();
      ClearPendingNoOfferings();
      if (requestId >= 0)
        _lastRequestId = requestId;
      _newRequest = false;
      _marketBoardResponseReceived = marketBoardResponseReceived;
      NewPrice = price;
    }

    private void ClearPendingNoMatch()
    {
      _pendingNoMatchRequestId = UnknownRequestId;
      _pendingNoMatchTimeoutAt = 0;
    }

    private void ClearPendingNoOfferings()
    {
      _pendingNoOfferingsTimeoutAt = 0;
    }
  }
}
