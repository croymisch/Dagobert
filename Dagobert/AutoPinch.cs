using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using static ECommons.UIHelpers.AtkReaderImplementations.ReaderContextMenu;

namespace Dagobert
{
  internal sealed class AutoPinch : Window, IDisposable
  {
    private const int PriceRequestTimeoutMS = 8000;
    private const int BulkPriceRequestTimeoutMS = 60000;
    private const int ContextMenuTimeoutMS = 5000;

    private readonly MarketBoardHandler _mbHandler;
    private readonly UniversalisPriceProvider _universalisPriceProvider;
    private readonly RetainerMarketDataCatalog _retainerMarketDataCatalog;
    private int? _oldPrice;
    private int? _newPrice;
    private bool _newPriceFromUniversalis;
    private bool _skipCurrentItem = false;
    private bool _autoSellingEnabledForRun;
    private bool _autoPinchRun;
    private bool _bulkPriceWorkflow;
    private uint? _currentAutoSellItemId;
    private bool _currentAutoSellItemCanStack;
    private bool _currentPriceUsingUniversalis;
    private UniversalisPriceResult? _universalisPriceResult;
    private Dictionary<UniversalisItemKey, List<UniversalisPriceResult>> _bulkUniversalisPrices = [];
    private CancellationTokenSource? _bulkUniversalisRequestCts;
    private bool _bulkUniversalisRequestCompleted;
    private bool _bulkUniversalisRequestSucceeded;
    private long _bulkUniversalisRequestStartedAt;
    private int _bulkUniversalisRequestId;
    private string _currentCharacterKey = string.Empty;
    private string _currentCharacterName = string.Empty;
    private string _currentCharacterWorld = string.Empty;
    private ulong? _currentRetainerId;
    private string _currentRetainerName = string.Empty;
    private long _currentPriceRequestStartedAt;
    private HashSet<uint> _autoSellProtectedItemIds = [];
    private readonly HashSet<uint> _autoSellSkippedItemIds = [];
    private Dictionary<(uint ItemId, bool IsHq), RetainerMarketItem> _currentRetainerObservedItems = [];
    private bool _currentRetainerCatalogSyncComplete;
    private int _currentRetainerListingIndex = -1;
    private int _currentRetainerQueuedItemIndex = -1;
    private readonly HashSet<int> _retainerQueueIndicesReturnedToInventory = [];
    private long _retainerListingRetrievalStartedAt;
    private uint? _pendingRetrievedListingItemId;
    private bool _pendingRetrievedListingIsHq;
    private bool _retainerListingRetrievalConfirmed;
    private long _itemContextMenuRequestedAt;
    private int _currentRetainerExpectedItemCount;
    private int _currentRetainerObservedItemCount;
    private readonly TaskManager _taskManager;
    private Dictionary<string, CachedPrice> _cachedPrices = [];
    private bool _cachedPricesUseUniversalisDataCenterPrices;
    private int _universalisPriceRequestId;
    private bool _disposed;
    private bool _autoRetainerStateCaptured;
    private bool _previousAutoRetainerSuppressed;
    private bool _previousMultiModeEnabled;
    private CancellationTokenSource? _universalisPriceRequestCts;

    public AutoPinch()
      : base("Dagobert", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.AlwaysAutoResize, true)
    {
      _mbHandler = new MarketBoardHandler();
      _mbHandler.NewPriceReceived += MBHandler_NewPriceReceived;
      _universalisPriceProvider = new UniversalisPriceProvider();
      _retainerMarketDataCatalog = new RetainerMarketDataCatalog();
      _cachedPricesUseUniversalisDataCenterPrices = Plugin.Configuration.UseUniversalisDataCenterPrices;

      // window
      Position = new System.Numerics.Vector2(0, 0);
      IsOpen = true;
      ShowCloseButton = false;
      RespectCloseHotkey = false;
      DisableWindowSounds = true;
      SizeConstraints = new WindowSizeConstraints()
      {
        MaximumSize = new System.Numerics.Vector2(0, 0),
      };

      _taskManager = new TaskManager
      {
        TimeLimitMS = 10000,
        AbortOnTimeout = true
      };
      // Fails on non-windows
      try
      {
        var tts = new SpeechSynthesizer();
        tts.SelectVoice(tts.Voice.Name);
        Plugin.Configuration.DontUseTTS = false;
        Plugin.Configuration.Save();
      }
      catch
      {
        Plugin.Configuration.DontUseTTS = true;
        Plugin.Configuration.Save();
      }

      Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, RetainerSellPostSetup);
    }

    public void Dispose()
    {
      _disposed = true;
      _taskManager.Abort();
      FinishPinching();
      CancelUniversalisPriceRequest();
      CancelBulkUniversalisPriceRefresh();
      _universalisPriceProvider.Dispose();
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, RetainerSellPostSetup);
      _mbHandler.NewPriceReceived -= MBHandler_NewPriceReceived;
      _mbHandler.Dispose();
    }

    public override void Draw()
    {
      try
      {
        ClearCachedPricesIfUniversalisSettingChanged();
        DrawForRetainerList();
        DrawForRetainerSellList();
        if ((_autoRetainerStateCaptured || _autoSellingEnabledForRun || _autoPinchRun) && !_taskManager.IsBusy)
          FinishPinching();
      }
      catch (Exception ex)
      {
        _taskManager.Abort();
        FinishPinching();
        Svc.Log.Error(ex, "Error while auto pinching");
        if (Plugin.Configuration.ShowErrorsInChat)
          Svc.Chat.PrintError($"Error while auto pinching: {ex.Message}");

        RemoveTalkAddonListeners();
      }
    }

    private void DrawForRetainerList()
    {
      unsafe
      {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
          if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
            PinchAllRetainers();

          var node = addon->UldManager.NodeList[27];

          if (node == null)
            return;

          var oldSize = ImGuiSetup(node);
          DrawAutoPinchButton(PinchAllRetainers);
          ImGuiPostSetup(oldSize);
        }
      }
    }

    private void DrawForRetainerSellList()
    {
      unsafe
      {
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
        {
          if (Plugin.Configuration.EnablePinchKey && Plugin.KeyState[Plugin.Configuration.PinchKey])
            PinchAllRetainerItems();

          var node = addon->UldManager.NodeList[17];

          if (node == null)
            return;

          var oldSize = ImGuiSetup(node);
          DrawAutoPinchButton(PinchAllRetainerItems);
          ImGuiPostSetup(oldSize);
        }
      }
    }

    private unsafe float ImGuiSetup(AtkResNode* node)
    {
      var position = GetNodePosition(node);
      var scale = GetNodeScale(node);
      var size = new Vector2(node->Width, node->Height) * scale;

      ImGuiHelpers.ForceNextWindowMainViewport();
      ImGuiHelpers.SetNextWindowPosRelativeMainViewport(position);

      ImGui.PushStyleColor(ImGuiCol.WindowBg, 0);
      var oldSize = ImGui.GetFont().Scale;
      ImGui.GetFont().Scale *= scale.X;
      ImGui.PushFont(ImGui.GetFont());
      ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f.Scale());
      ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(3f.Scale(), 3f.Scale()));
      ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f.Scale(), 0f.Scale()));
      ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f.Scale());
      ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, size);
      ImGui.Begin($"###AutoPinch{node->NodeId}", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNavFocus
          | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoSavedSettings);

      return oldSize;
    }

    private static void ImGuiPostSetup(float oldSize)
    {
      ImGui.End();
      ImGui.PopStyleVar(5);
      ImGui.GetFont().Scale = oldSize;
      ImGui.PopFont();
      ImGui.PopStyleColor();
    }

    private void DrawAutoPinchButton(Action specificPinchFunction)
    {
      if (_taskManager.IsBusy)
      {
        if (ImGui.Button("Cancel"))
        {
          _taskManager.Abort();
          FinishPinching();
        }
        if (ImGui.IsItemHovered())
        {
          ImGui.BeginTooltip();
          ImGui.SetTooltip("Cancels the auto pinching process");
          ImGui.EndTooltip();
        }
      }
      else
      {
        if (ImGui.Button("Auto Pinch"))
          specificPinchFunction();
        if (ImGui.IsItemHovered())
        {
          ImGui.BeginTooltip();
          ImGui.SetTooltip("Starts auto pinching\r\n" +
                           "Please do not interact with the game while this process is running");
          ImGui.EndTooltip();
        }
      }
    }

    public void StartPinchingRetainers()
    {
      Svc.Log.Information("AutoPinch start requested");
      PinchAllRetainers();
    }

    public void StartPinchingRetainers(bool previousSuppressed, bool previousMultiModeEnabled)
    {
      Svc.Log.Information("AutoPinch start requested by AutoRetainer completion event");
      PinchAllRetainers(previousSuppressed, previousMultiModeEnabled);
    }

    private unsafe void PinchAllRetainers()
    {
      PinchAllRetainers(null, null);
    }

    private unsafe void PinchAllRetainers(bool? previousSuppressed, bool? previousMultiModeEnabled)
    {
      if (_taskManager.IsBusy)
      {
        Svc.Log.Warning("AutoPinch did not start because its task queue is already busy");
        if (previousSuppressed.HasValue && previousMultiModeEnabled.HasValue)
          RestoreAutoRetainerState(previousSuppressed.Value, previousMultiModeEnabled.Value);
        return;
      }
  
      ClearState();
      _autoPinchRun = true;
      _bulkPriceWorkflow = true;
      CaptureCurrentCharacterIdentity();
      _previousAutoRetainerSuppressed = previousSuppressed ?? AutoRetainerIPC.Suppressed();
      _previousMultiModeEnabled = previousMultiModeEnabled ?? AutoRetainerIPC.MultiModeEnabled();
      _autoRetainerStateCaptured = true;
      if (!InitializeAutoSellingForRun())
      {
        FinishPinching();
        return;
      }

      if (!previousSuppressed.HasValue || !previousMultiModeEnabled.HasValue)
      {
        var suppressionSet = AutoRetainerIPC.Suppressed(true);
        var multiModeSet = AutoRetainerIPC.MultiModeEnabled(false);
        if (!suppressionSet || !multiModeSet)
        {
          FinishPinching();
          return;
        }
      }

      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        QueueRetainerPinch(addon);
      }
      else
      {
        Svc.Log.Information("AutoPinch is waiting for the Retainer List addon to become ready");
        _taskManager.Enqueue(WaitForRetainerList, 10000, "WaitForRetainerList");
      }
    }

    private unsafe bool? WaitForRetainerList()
    {
      if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) || !GenericHelpers.IsAddonReady(addon))
        return false;

      Svc.Log.Information("Retainer List is ready; queuing AutoPinch work");
      QueueRetainerPinch(addon);
      return true;
    }

    private unsafe void QueueRetainerPinch(AtkUnitBase* addon)
    {
      Svc.Log.Information($"AutoPinch started: suppressed={AutoRetainerIPC.Suppressed()}, MultiMode={AutoRetainerIPC.MultiModeEnabled()}");

      var retainerList = new AddonMaster.RetainerList(addon);
      var retainers = retainerList.Retainers;
      var retainerCount = retainers.Length;
      var selectedRetainerIndices = new List<int>();

      var allDisabled = Plugin.Configuration.EnabledRetainerNames.Contains(Configuration.ALL_DISABLED_SENTINEL);
      if (allDisabled)
      {
        Communicator.PrintAllRetainersDisabled();
      }
      else
      {
        var allEnabled = Plugin.Configuration.EnabledRetainerNames.Count == 0;
        for (var index = 0; index < retainerCount; index++)
        {
          var retainerName = retainers[index].Name;
          if (!allEnabled && !Plugin.Configuration.EnabledRetainerNames.Contains(retainerName))
          {
            Svc.Log.Debug($"Skipping retainer '{retainerName}' (excluded by user configuration)");
            continue;
          }

          selectedRetainerIndices.Add(index);
        }

        if (selectedRetainerIndices.Count > 0)
        {
          BeginBulkUniversalisPriceRefresh();
          Svc.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
          Svc.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);
          foreach (var index in selectedRetainerIndices)
            EnqueueSingleRetainer(index);
        }

        _taskManager.Enqueue(RemoveTalkAddonListeners);
        if (Plugin.Configuration.TTSWhenAllDone)
          _taskManager.Enqueue(() => SpeakTTS(Plugin.Configuration.TTSWhenAllDoneMsg), "SpeakTTSAll");
      }

      _taskManager.Enqueue(FinishPinching);
      Svc.Log.Information($"AutoPinch queued processing for {selectedRetainerIndices.Count} of {retainerCount} retainers");
    }

    private void FinishPinching()
    {
      RemoveTalkAddonListeners();
      if (_autoRetainerStateCaptured)
      {
        _autoRetainerStateCaptured = false;
        RestoreAutoRetainerState(_previousAutoRetainerSuppressed, _previousMultiModeEnabled);
      }

      _autoPinchRun = false;
      _bulkPriceWorkflow = false;
      CancelBulkUniversalisPriceRefresh();
      _bulkUniversalisPrices.Clear();
      ClearCurrentPriceRequest();
      ClearAutoSellingState();
      _currentRetainerObservedItems.Clear();
      _currentRetainerCatalogSyncComplete = false;
      _currentRetainerExpectedItemCount = 0;
      _currentRetainerObservedItemCount = 0;
    }

    private static void RestoreAutoRetainerState(bool suppressed, bool multiModeEnabled)
    {
      AutoRetainerIPC.MultiModeEnabled(multiModeEnabled);
      AutoRetainerIPC.Suppressed(suppressed);
    }

    private void EnqueueSingleRetainer(int index)
    {
      _taskManager.Enqueue(() => ClickRetainer(index), $"ClickRetainer{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(ClickSellItems, $"ClickSellItems{index}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(() => EnqueueAllRetainerItems(InsertSingleItem, true), $"EnqueueAllRetainerItems{index}");
      _taskManager.Enqueue(SyncCurrentRetainerMarketData, $"SyncRetainerMarketData{index}");
      _taskManager.DelayNext(500);
      _taskManager.Enqueue(CloseRetainerSellList, $"CloseRetainerSellList{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(CloseRetainer, $"CloseRetainer{index}");
      _taskManager.DelayNext(100);
    }

    private unsafe bool? ClickRetainer(int index)
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        var retainerManager = RetainerManager.Instance();
        var retainer = retainerManager == null ? null : retainerManager->GetRetainerBySortedIndex((uint)index);
        var retainerName = new AddonMaster.RetainerList(addon).Retainers[index].Name;
        if (retainer == null)
        {
          _currentRetainerId = null;
          _currentRetainerName = retainerName;
          if (_autoSellingEnabledForRun)
            Svc.Log.Error($"Could not read retainer ID at index {index}; catalog tracking is unavailable for this retainer");
        }
        else
        {
          _currentRetainerId = retainer->RetainerId;
          _currentRetainerName = retainerName;
        }

        BeginRetainerMarketDataSync();
        Communicator.PrintRetainerName(retainerName);
        ECommons.Automation.Callback.Fire(addon, true, 2, index);
        return true;
      }

      return false;
    }

    private static unsafe bool? ClickSellItems()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        new AddonMaster.SelectString(addon).Entries[2].Select();
        return true;
      }

      return false;
    }

    private static unsafe bool? CloseRetainerSellList()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        addon->Close(true);
        return true;
      }

      return false;
    }

    private static unsafe bool? CloseRetainer()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectString", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        addon->Close(true);
        return true;
      }

      return false;
    }

    private unsafe void PinchAllRetainerItems()
    {
      _mbHandler.PopulateRetainerCache();
      if (_taskManager.IsBusy)
        return;

      if (_autoRetainerStateCaptured)
      {
        Svc.Log.Warning("Current-retainer Universalis refresh skipped because the summoning-bell Auto Pinch run is already processing retainers");
        return;
      }

      ClearState();
      _autoPinchRun = true;
      CaptureCurrentCharacterIdentity();
      _currentRetainerId = null;
      _currentRetainerName = string.Empty;
      if (!InitializeAutoSellingForRun())
        return;

      var retainerManager = RetainerManager.Instance();
      var retainer = retainerManager == null ? null : retainerManager->GetActiveRetainer();
      if (retainer == null)
      {
        if (_autoSellingEnabledForRun)
          Svc.Log.Error("Could not read the active retainer's ID; catalog tracking is unavailable");
      }
      else
      {
        _currentRetainerId = retainer->RetainerId;
        _currentRetainerName = $"Retainer {_currentRetainerId.Value}";
      }

      BeginRetainerMarketDataSync();
      _bulkPriceWorkflow = true;
      if (_currentRetainerId.HasValue && !string.IsNullOrWhiteSpace(_currentCharacterKey))
      {
        var retainerItems = _retainerMarketDataCatalog.GetItems(_currentCharacterKey, _currentRetainerId.Value);
        Svc.Log.Information($"Starting standalone Auto Pinch for active retainer '{_currentRetainerName}'; only this retainer's catalog will be refreshed");
        BeginBulkUniversalisPriceRefresh(
          retainerItems,
          $"active retainer '{_currentRetainerName}'");
      }
      else
      {
        Svc.Log.Warning("Cannot refresh Universalis prices for the active retainer because its character or retainer ID is unavailable; using in-game market-board prices");
      }

      EnqueueAllRetainerItems(EnqueueSingleItem, false);
      _taskManager.Enqueue(SyncCurrentRetainerMarketData, "SyncRetainerMarketData");
      _taskManager.Enqueue(FinishPinching, "FinishPinching");
    }

    private unsafe bool? EnqueueAllRetainerItems(Action<int> enqueueFunc, bool reverseOrder)
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
        var listComponent = (AtkComponentList*)listNode->Component;
        var itemCount = listComponent->ListLength;
        _currentRetainerExpectedItemCount = itemCount;
        _currentRetainerObservedItemCount = 0;
        if (reverseOrder)
        {
          for (var index = itemCount - 1; index >= 0; index--)
            enqueueFunc(index);
        }
        else
        {
          for (var index = 0; index < itemCount; index++)
            enqueueFunc(index);
        }

        if (Plugin.Configuration.TTSWhenEachDone)
          _taskManager.Enqueue(() => SpeakTTS(Plugin.Configuration.TTSWhenEachDoneMsg), "SpeakTTSEach");

        return true;
      }

      return false;
    }

    private void EnqueueSingleItem(int index)
    {
      _taskManager.Enqueue(() => OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(ClickAdjustPrice, ContextMenuTimeoutMS + 2000, false, $"ClickAdjustPrice{index}");
      _taskManager.DelayNext(100);
      _taskManager.Enqueue(DelayMarketBoard, $"DelayMB{index}");
      _taskManager.Enqueue(ClickComparePrice, $"ClickComparePrice{index}");
      _taskManager.Enqueue(SetNewPrice, PriceRequestTimeoutMS + 1000, false, $"SetNewPrice{index}");
    }

    private void InsertSingleItem(int index)
    {
      // reverse order because we INSERT
      _taskManager.Insert(SetNewPrice, PriceRequestTimeoutMS + 1000, false, $"SetNewPrice{index}");
      _taskManager.Insert(ClickComparePrice, $"ClickComparePrice{index}");
      _taskManager.Insert(DelayMarketBoard, $"DelayMB{index}");
      _taskManager.InsertDelayNext(100);
      _taskManager.Insert(ClickAdjustPrice, ContextMenuTimeoutMS + 2000, false, $"ClickAdjustPrice{index}");
      _taskManager.InsertDelayNext(100);
      _taskManager.Insert(() => OpenItemContextMenu(index), $"OpenItemContextMenu{index}");
    }

    private unsafe bool? OpenItemContextMenu(int itemIndex)
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        var indexAdjustment = _retainerQueueIndicesReturnedToInventory.Count(returnedIndex => returnedIndex < itemIndex);
        var currentListIndex = itemIndex - indexAdjustment;
        if (!TryGetRetainerListingCount(out var currentItemCount)
            || currentListIndex < 0
            || currentListIndex >= currentItemCount)
        {
          Svc.Log.Warning($"Retainer item at original index {itemIndex} is no longer available at adjusted index {currentListIndex}; skipping it");
          _currentRetainerCatalogSyncComplete = false;
          _skipCurrentItem = true;
          return true;
        }

        _currentRetainerQueuedItemIndex = itemIndex;
        _currentRetainerListingIndex = currentListIndex;
        _itemContextMenuRequestedAt = Environment.TickCount64;
        Svc.Log.Debug($"Clicking retainer item {currentListIndex} (original queue index {itemIndex})");
        ECommons.Automation.Callback.Fire(addon, true, 0, currentListIndex, 1); // click item
        return true;
      }

      return false;
    }

    private unsafe bool? ClickAdjustPrice()
    {
      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        _itemContextMenuRequestedAt = 0;
        var reader = new ReaderContextMenu(addon);
        if (!IsItemMannequin(reader.Entries))
        {
          Svc.Log.Debug($"Clicking adjust price");
          ECommons.Automation.Callback.Fire(addon, true, 0, 0, 0, 0, 0); // click adjust price
        }
        else
        {
          Svc.Log.Debug("Current item is a mannequin item and will be skipped");
          _currentRetainerCatalogSyncComplete = false;
          _skipCurrentItem = true;
          addon->Close(true);
        }

        return true;
      }

      if (_itemContextMenuRequestedAt != 0
          && Environment.TickCount64 - _itemContextMenuRequestedAt >= ContextMenuTimeoutMS)
      {
        Svc.Log.Warning($"Retainer item context menu did not open within {ContextMenuTimeoutMS} ms; skipping this item and continuing Auto Pinch");
        _currentRetainerCatalogSyncComplete = false;
        _skipCurrentItem = true;
        _itemContextMenuRequestedAt = 0;
        return true;
      }

      return false;
    }

    /// <summary>
    /// Checks if an item is a mannequin item, by checking if there is
    /// the "adjust price" entry in the given <paramref name="contextMenuEntries"/>.
    /// </summary>
    /// <param name="contextMenuEntries">Context menu entries to check.</param>
    /// <returns>True if item is a mannequin item, false otherwise.</returns>
    private static bool IsItemMannequin(List<ContextMenuEntry> contextMenuEntries)
    {
      return !contextMenuEntries.Any((e) => e.Name.Equals("adjust price", StringComparison.CurrentCultureIgnoreCase)
                                        || e.Name.Equals("preis ändern", StringComparison.CurrentCultureIgnoreCase)
                                        || e.Name.Equals("価格を変更する", StringComparison.CurrentCultureIgnoreCase)
                                        || e.Name.Equals("changer le prix", StringComparison.CurrentCultureIgnoreCase));
    }

    private unsafe bool? DelayMarketBoard()
    {
      if (_skipCurrentItem)
        return true;

      if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
      {
        var itemName = GetRetainerSellItemName(addon);
        var rawItemName = GetRetainerSellRawItemName(addon);
        if (_bulkPriceWorkflow && TryGetBulkUniversalisPrice(itemName, rawItemName, addon->AskingPrice->Value, out _))
          return true;

        if (!_bulkPriceWorkflow
            && Plugin.Configuration.UseUniversalisDataCenterPrices
            && _universalisPriceProvider.CanResolveItem(itemName, rawItemName))
          return true;

        if (!_cachedPrices.TryGetValue(itemName, out var cachedPrice) || cachedPrice.Value <= 0)
        {
          Svc.Log.Debug($"{itemName} has no cached price (or that price was <= 0), delaying next mb open");
          _taskManager.InsertDelayNext(Plugin.Configuration.GetMBPricesDelayMS);
        }

        return true;
      }

      return false;
    }

    private unsafe bool? ClickComparePrice()
    {
      if (_skipCurrentItem)
        return true;

      if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var addon) && GenericHelpers.IsAddonReady(&addon->AtkUnitBase))
      {
        ObserveCurrentRetainerListing(addon, addon->AskingPrice->Value);
        if (!PrepareAutoSaleItem(addon))
          return true;

        var itemName = GetRetainerSellItemName(addon);
        var rawItemName = GetRetainerSellRawItemName(addon);
        _newPrice = null;
        _newPriceFromUniversalis = false;
        _universalisPriceResult = null;
        _currentPriceUsingUniversalis = false;
        _currentPriceRequestStartedAt = Environment.TickCount64;
        if (_bulkPriceWorkflow && TryGetBulkUniversalisPrice(itemName, rawItemName, addon->AskingPrice->Value, out var bulkPrice))
        {
          Svc.Log.Debug($"{itemName}: using prefetched Universalis price (lowest={bulkPrice.LowestListingPrice}, suggested={bulkPrice.SuggestedPrice})");
          _newPrice = bulkPrice.SuggestedPrice;
          _newPriceFromUniversalis = true;
          _universalisPriceResult = bulkPrice;
          _currentPriceUsingUniversalis = true;
          _currentPriceRequestStartedAt = 0;
          return true;
        }

        if ((_bulkPriceWorkflow || !Plugin.Configuration.UseUniversalisDataCenterPrices)
            && _cachedPrices.TryGetValue(itemName, out var cachedPrice)
            && cachedPrice.Value > 0)
        {
          Svc.Log.Debug($"{itemName}: using cached price");
          _newPrice = cachedPrice.Value;
          _newPriceFromUniversalis = cachedPrice.FromUniversalis;
          _currentPriceRequestStartedAt = 0;
          return true;
        }

        if (!_bulkPriceWorkflow
            && Plugin.Configuration.UseUniversalisDataCenterPrices
            && _universalisPriceProvider.CanResolveItem(itemName, rawItemName))
        {
          Svc.Log.Debug($"{itemName}: requesting fresh Universalis data center price");
          _currentPriceUsingUniversalis = true;
          StartUniversalisPriceRequest(itemName, rawItemName, addon->AskingPrice->Value);
          return true;
        }

        if (_currentAutoSellItemId.HasValue)
        {
          var isHq = itemName.Contains('\uE03C') || rawItemName.Contains('\uE03C');
          _mbHandler.SetCurrentItemForPriceCheck(_currentAutoSellItemId.Value, isHq);
        }

        Svc.Log.Debug("Clicking compare prices");
        _taskManager.InsertDelayNext(Plugin.Configuration.MarketBoardKeepOpenMS);
        ECommons.Automation.Callback.Fire(&addon->AtkUnitBase, true, 4);
        return true;
      }

      return false;
    }

    private unsafe bool? SetNewPrice()
    {
      var completed = false;
      try
      {
        if (_skipCurrentItem)
        {
          completed = true;
          return true;
        }

        if (!_newPrice.HasValue)
        {
          if (_currentPriceRequestStartedAt == 0
              || Environment.TickCount64 - _currentPriceRequestStartedAt < PriceRequestTimeoutMS)
            return false;

          Svc.Log.Warning("Price check timed out; leaving the current item unchanged");
          if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var pendingSearch))
            pendingSearch->Close(true);
          if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var pendingRetainerSell)
              && GenericHelpers.IsAddonReady(&pendingRetainerSell->AtkUnitBase))
            CancelCurrentRetainerSale(pendingRetainerSell);

          completed = true;
          return true;
        }

        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("ItemSearchResult", out var addon))
          addon->Close(true);

        if (GenericHelpers.TryGetAddonByName<AddonRetainerSell>("RetainerSell", out var retainerSell) && GenericHelpers.IsAddonReady(&retainerSell->AtkUnitBase))
        {
          var ui = &retainerSell->AtkUnitBase;
          var itemName = GetRetainerSellItemName(retainerSell);
          var rawItemName = GetRetainerSellRawItemName(retainerSell);
          var isHq = itemName.Contains('\uE03C') || rawItemName.Contains('\uE03C');
          var isLevelOneHqEquippable =
            isHq
            && ItemNameResolver.TryGetItemId(itemName, rawItemName, out var itemId)
            && ItemNameResolver.IsLevelOneEquippable(itemId);
          _oldPrice = retainerSell->AskingPrice->Value;
          if (isLevelOneHqEquippable)
            Svc.Log.Information(
              $"{itemName}: confirmed HQ item-level-1 equippable; current price {_oldPrice.Value}, suggested price {_newPrice.Value}, minimum {Plugin.Configuration.MinimumAutoSellPrice}, max undercut {Plugin.Configuration.MaxUndercutPercentage:F2}%");

          if (!_currentAutoSellItemCanStack
              && _oldPrice.Value < Plugin.Configuration.MinimumAutoSellPrice)
          {
            QueueUnderMinimumItemRetrieval(retainerSell, itemName, _oldPrice.Value);
            completed = true;
            return true;
          }

          if (_currentPriceUsingUniversalis && !_universalisPriceResult.HasValue)
          {
            Svc.Log.Warning($"{itemName}: Universalis returned no usable listings; leaving the current price unchanged");
            CancelCurrentRetainerSale(retainerSell);
            completed = true;
            return true;
          }

          if (_autoPinchRun
              && _currentPriceUsingUniversalis
              && _universalisPriceResult.HasValue
              && _oldPrice.Value <= _universalisPriceResult.Value.LowestListingPrice)
          {
            Svc.Log.Debug($"{itemName}: current retainer price {_oldPrice.Value} is already at or below Universalis lowest price {_universalisPriceResult.Value.LowestListingPrice}; skipping price adjustment");
            CancelCurrentRetainerSale(retainerSell);
            completed = true;
            return true;
          }

          if (!(_newPrice > 0))
          {
            if (Plugin.Configuration.DefaultAmount == 0)
            {
              Svc.Log.Warning("SetNewPrice: No price to set");
              Communicator.PrintNoPriceToSetError(itemName);
              if (_autoSellingEnabledForRun && _currentAutoSellItemId.HasValue)
                _autoSellSkippedItemIds.Add(_currentAutoSellItemId.Value);
              CancelCurrentRetainerSale(retainerSell);
              completed = true;
              return true;
            }

            Svc.Log.Warning("SetNewPrice: Using default amount");
            _newPrice = Plugin.Configuration.DefaultAmount;
            _newPriceFromUniversalis = false;
            Communicator.PrintUsingDefaultAmountWarning(itemName, _newPrice.Value);
          }

          var limitedPrice = ApplyItemPriceLimits(itemName, rawItemName, _newPrice.Value);
          if (limitedPrice != _newPrice.Value)
          {
            Svc.Log.Debug($"{itemName}: item price limit adjusted {_newPrice.Value} to {limitedPrice}");
            _newPrice = limitedPrice;
          }

          if (!_currentAutoSellItemCanStack
              && _newPrice.Value < Plugin.Configuration.MinimumAutoSellPrice)
          {
            QueueUnderMinimumItemRetrieval(retainerSell, itemName, _newPrice.Value);
            completed = true;
            return true;
          }

          if (_autoSellingEnabledForRun
              && (!_currentAutoSellItemId.HasValue || !_autoSellProtectedItemIds.Contains(_currentAutoSellItemId.Value))
              && !isLevelOneHqEquippable)
          {
            Svc.Log.Information($"{itemName}: not in AutoRetainer's protection list; skipping automatic sale");
            CancelCurrentRetainerSale(retainerSell);
            completed = true;
            return true;
          }

          var cutPercentage = ((float)_newPrice.Value - _oldPrice.Value) / _oldPrice.Value * 100f;
          if (cutPercentage >= -Plugin.Configuration.MaxUndercutPercentage || isLevelOneHqEquippable)
          {
            if (isLevelOneHqEquippable && cutPercentage < -Plugin.Configuration.MaxUndercutPercentage)
              Svc.Log.Information(
                $"{itemName}: applying the all-quality level-1 gear price {_newPrice.Value} despite the {Math.Abs(cutPercentage):F2}% difference from its HQ price {_oldPrice.Value}");
            Svc.Log.Information($"{itemName}: applying price {_newPrice.Value} (was {_oldPrice.Value})");
            _cachedPrices.TryAdd(itemName, new CachedPrice(_newPrice.Value, _newPriceFromUniversalis));
            retainerSell->AskingPrice->SetValue(_newPrice.Value);
            Communicator.PrintPriceUpdate(itemName, _oldPrice.Value, _newPrice.Value, cutPercentage, _newPriceFromUniversalis);
          }
          else
          {
            Svc.Log.Warning(
              $"{itemName}: suggested price {_newPrice.Value} would undercut the current price {_oldPrice.Value} by {Math.Abs(cutPercentage):F2}%, exceeding the configured maximum of {Plugin.Configuration.MaxUndercutPercentage:F2}%; leaving the item unchanged");
            Communicator.PrintAboveMaxCutError(itemName);
            CancelCurrentRetainerSale(retainerSell);
            completed = true;
            return true;
          }

          if (!_currentAutoSellItemCanStack
              && retainerSell->AskingPrice->Value < Plugin.Configuration.MinimumAutoSellPrice)
          {
            QueueUnderMinimumItemRetrieval(retainerSell, itemName, retainerSell->AskingPrice->Value);
            completed = true;
            return true;
          }

          RecordListedItem(retainerSell, retainerSell->AskingPrice->Value);

          ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 0);
          ui->Close(true);
          completed = true;
          return true;
        }

        if (_currentPriceRequestStartedAt > 0
            && Environment.TickCount64 - _currentPriceRequestStartedAt >= PriceRequestTimeoutMS)
        {
          Svc.Log.Warning("Retainer sell window did not become ready after the price check; continuing without changing this item");
          completed = true;
          return true;
        }

        return false;
      }
      finally
      {
        if (completed)
          ClearCurrentPriceRequest();
      }
    }

    private unsafe bool PrepareAutoSaleItem(AddonRetainerSell* retainerSell)
    {
      var itemName = GetRetainerSellItemName(retainerSell);
      var rawItemName = GetRetainerSellRawItemName(retainerSell);
      if (!ItemNameResolver.TryGetItemId(itemName, rawItemName, out var itemId))
      {
        Svc.Log.Error($"Could not resolve item ID for {itemName}; checking its price but it cannot be automatically listed");
        _currentAutoSellItemId = null;
        _currentAutoSellItemCanStack = false;
      }
      else
      {
        _currentAutoSellItemId = itemId;
        _currentAutoSellItemCanStack = ItemNameResolver.CanStack(itemId);

        if (_autoSellingEnabledForRun && _autoSellSkippedItemIds.Contains(itemId))
        {
          CancelCurrentRetainerSale(retainerSell);
          _skipCurrentItem = true;
          return false;
        }

        if (_autoSellingEnabledForRun
            && _currentAutoSellItemCanStack
            && !_autoSellProtectedItemIds.Contains(itemId))
        {
          CancelCurrentRetainerSale(retainerSell);
          _skipCurrentItem = true;
          return false;
        }
      }

      return true;
    }

    private unsafe void QueueUnderMinimumItemRetrieval(
      AddonRetainerSell* retainerSell,
      string itemName,
      int price)
    {
      var freeInventorySlots = GetFreeInventorySlotCount();
      if (freeInventorySlots <= 1)
      {
        Svc.Log.Warning(
          $"{itemName}: price {price} is below the minimum listing price {Plugin.Configuration.MinimumAutoSellPrice}, but it will remain listed because only {freeInventorySlots} inventory slots are free");
        CancelCurrentRetainerSale(retainerSell);
        return;
      }

      Svc.Log.Information(
        $"{itemName}: price {price} is below the minimum listing price {Plugin.Configuration.MinimumAutoSellPrice}; removing this retainer's listing and returning it to inventory");
      var isHq = itemName.Contains('\uE03C') || GetRetainerSellRawItemName(retainerSell).Contains('\uE03C');
      CancelCurrentRetainerSale(retainerSell);
      QueueRetainerListingRetrieval(itemName, _currentAutoSellItemId, isHq);
    }

    private static unsafe int GetFreeInventorySlotCount()
    {
      var inventoryManager = InventoryManager.Instance();
      if (inventoryManager == null)
      {
        Svc.Log.Warning("Could not inspect inventory space before returning a retainer item; retrieval will be skipped");
        return 0;
      }

      var freeSlots = 0;
      InventoryType[] inventoryTypes =
      [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
      ];

      foreach (var inventoryType in inventoryTypes)
      {
        var container = inventoryManager->GetInventoryContainer(inventoryType);
        if (container == null)
        {
          Svc.Log.Warning($"Could not inspect {inventoryType} before returning a retainer item; retrieval will be skipped");
          return 0;
        }

        for (var slotIndex = 0; slotIndex < container->GetSize(); slotIndex++)
        {
          if (container->GetInventorySlot(slotIndex)->GetItemId() == 0)
            freeSlots++;
        }
      }

      return freeSlots;
    }

    private static unsafe void CancelCurrentRetainerSale(AddonRetainerSell* retainerSell)
    {
      ECommons.Automation.Callback.Fire(&retainerSell->AtkUnitBase, true, 1);
      retainerSell->AtkUnitBase.Close(true);
    }

    private void QueueRetainerListingRetrieval(string itemName, uint? itemId, bool isHq)
    {
      if (_currentRetainerListingIndex < 0)
      {
        Svc.Log.Warning($"{itemName}: cannot retrieve the under-minimum listing because its retainer-list index is unknown");
        return;
      }

      var index = _currentRetainerListingIndex;
      _retainerListingRetrievalStartedAt = 0;
      _pendingRetrievedListingItemId = itemId;
      _pendingRetrievedListingIsHq = isHq;
      _retainerListingRetrievalConfirmed = false;
      _taskManager.Insert(ConfirmRetainerListingRetrieval, ContextMenuTimeoutMS + 2000, false, $"ConfirmRetrieveItem{index}");
      _taskManager.InsertDelayNext(250);
      _taskManager.Insert(ClickRetrieveItemContextMenu, ContextMenuTimeoutMS + 2000, false, $"ClickRetrieveItem{index}");
      _taskManager.InsertDelayNext(100);
      _taskManager.Insert(() => OpenItemContextMenu(index), ContextMenuTimeoutMS + 2000, false, $"OpenRetrieveContextMenu{index}");
    }

    private unsafe bool? ClickRetrieveItemContextMenu()
    {
      if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("ContextMenu", out var addon) || !GenericHelpers.IsAddonReady(addon))
        return false;

      var contextMenu = new AddonMaster.ContextMenu(addon);
      var entries = contextMenu.Entries;
      var entryIndex = Array.FindIndex(entries, menuEntry =>
        string.Equals(menuEntry.Text.Trim(), "Return Items to Inventory", StringComparison.OrdinalIgnoreCase));

      if (entryIndex < 0)
      {
        Svc.Log.Warning(
          $"Could not find the 'Return Items to Inventory' action in the retainer listing context menu; available actions: {string.Join(" | ", entries.Select(menuEntry => menuEntry.Text))}");
        _pendingRetrievedListingItemId = null;
        addon->Close(true);
        return true;
      }

      var entry = entries[entryIndex];
      if (!entry.Select())
      {
        Svc.Log.Warning($"Could not select the 'Return Items to Inventory' action; listing action was '{entry.Text}'");
        _pendingRetrievedListingItemId = null;
        addon->Close(true);
        return true;
      }

      _retainerListingRetrievalStartedAt = Environment.TickCount64;
      Svc.Log.Information($"Selected 'Return Items to Inventory' for retainer listing at index {_currentRetainerListingIndex}");
      return true;
    }

    private unsafe bool? ConfirmRetainerListingRetrieval()
    {
      if (_retainerListingRetrievalStartedAt == 0)
        return true;

      if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var addon) && GenericHelpers.IsAddonReady(addon))
      {
        if (!_retainerListingRetrievalConfirmed)
        {
          new AddonMaster.SelectYesno(addon).Yes();
          _retainerListingRetrievalConfirmed = true;
          Svc.Log.Information("Accepted the retainer listing retrieval confirmation");
        }
      }

      if (TryGetRetainerListingCount(out var itemCount) && itemCount < _currentRetainerExpectedItemCount)
      {
        CompleteRetainerListingRetrieval();
        Svc.Log.Information("Confirmed the under-minimum listing was removed from the retainer market");
        return true;
      }

      if (Environment.TickCount64 - _retainerListingRetrievalStartedAt < ContextMenuTimeoutMS)
        return false;

      Svc.Log.Warning("Retainer listing retrieval was not confirmed; the listing may still be for sale");
      _pendingRetrievedListingItemId = null;
      return true;
    }

    private void CompleteRetainerListingRetrieval()
    {
      if (_currentRetainerQueuedItemIndex >= 0)
        _retainerQueueIndicesReturnedToInventory.Add(_currentRetainerQueuedItemIndex);
      if (_pendingRetrievedListingItemId.HasValue)
        _currentRetainerObservedItems.Remove((_pendingRetrievedListingItemId.Value, _pendingRetrievedListingIsHq));
      _currentRetainerExpectedItemCount = Math.Max(0, _currentRetainerExpectedItemCount - 1);
      _currentRetainerObservedItemCount = Math.Max(0, _currentRetainerObservedItemCount - 1);
      _pendingRetrievedListingItemId = null;
    }

    private unsafe bool TryGetRetainerListingCount(out int itemCount)
    {
      itemCount = 0;
      if (!GenericHelpers.TryGetAddonByName<AtkUnitBase>("RetainerSellList", out var addon)
          || !GenericHelpers.IsAddonReady(addon))
        return false;

      var listNode = (AtkComponentNode*)addon->UldManager.NodeList[10];
      if (listNode == null || listNode->Component == null)
        return false;

      itemCount = ((AtkComponentList*)listNode->Component)->ListLength;
      return true;
    }

    private bool InitializeAutoSellingForRun()
    {
      _autoSellingEnabledForRun = Plugin.Configuration.EnableAutoSelling;
      if (!_autoSellingEnabledForRun)
      {
        Svc.Log.Information("Automatic selling is disabled; AutoRetainer's protection list will not be loaded");
        return true;
      }

      if (AutoRetainerIPC.TryGetProtectionList(out var protectedItemIds))
      {
        _autoSellProtectedItemIds = [.. protectedItemIds];
        Svc.Log.Information($"Loaded {_autoSellProtectedItemIds.Count} item IDs from AutoRetainer's protection list before Auto Pinch");
        if (_autoSellProtectedItemIds.Count == 0)
          Svc.Log.Warning("AutoRetainer's protection list is empty; automatic selling has no eligible items");
        else
          Svc.Log.Debug($"AutoRetainer protected item IDs passed to Dagobert: {string.Join(", ", _autoSellProtectedItemIds)}");
        return true;
      }

      if (Plugin.Configuration.ShowErrorsInChat)
        Svc.Chat.PrintError("Automatic selling was not started because AutoRetainer's protection list could not be retrieved.");
      return false;
    }

    private void MBHandler_NewPriceReceived(object? sender, NewPriceEventArgs e)
    {
      Svc.Log.Debug($"New price received: {e.NewPrice}");
      _newPrice = e.NewPrice;
      _newPriceFromUniversalis = false;
    }

    private static unsafe string GetRetainerSellItemName(AddonRetainerSell* addon)
    {
      return addon->ItemName->NodeText.GetText();
    }

    private static unsafe string GetRetainerSellRawItemName(AddonRetainerSell* addon)
    {
      return addon->ItemName->NodeText.ToString();
    }

    private static int ApplyItemPriceLimits(string itemName, string rawItemName, int price)
    {
      if (!ItemNameResolver.TryGetItemId(itemName, rawItemName, out var itemId))
        return price;

      var limit = Plugin.Configuration.GetItemPriceLimit(itemId);
      return limit?.Apply(price) ?? price;
    }

    private void StartUniversalisPriceRequest(string itemName, string rawItemName, int currentPrice)
    {
      CancelUniversalisPriceRequest();

      var requestId = ++_universalisPriceRequestId;
      _newPriceFromUniversalis = false;
      _universalisPriceResult = null;
      _universalisPriceRequestCts = new CancellationTokenSource();
      _ = CompleteUniversalisPriceRequest(itemName, rawItemName, currentPrice, requestId, _universalisPriceRequestCts.Token);
    }

    private void CaptureCurrentCharacterIdentity()
    {
      var player = Svc.Objects.LocalPlayer;
      var world = player?.HomeWorld.ValueNullable?.Name.ToString();
      if (player == null || string.IsNullOrWhiteSpace(world))
      {
        Svc.Log.Warning("Could not identify the logged-in character; retainer market data will not be saved or prefetched");
        _currentCharacterKey = string.Empty;
        _currentCharacterName = string.Empty;
        _currentCharacterWorld = string.Empty;
        return;
      }

      _currentCharacterName = player.Name.ToString();
      _currentCharacterWorld = world;
      _currentCharacterKey = $"{_currentCharacterName}@{_currentCharacterWorld}";
    }

    private void BeginBulkUniversalisPriceRefresh()
    {
      if (string.IsNullOrWhiteSpace(_currentCharacterKey))
        return;

      var items = _retainerMarketDataCatalog.GetItems(_currentCharacterKey);
      BeginBulkUniversalisPriceRefresh(items, "selected retainers");
    }

    private void BeginBulkUniversalisPriceRefresh(
      IReadOnlyCollection<RetainerMarketItem> items,
      string refreshScope)
    {
      if (items.Count == 0)
      {
        Svc.Log.Information($"Universalis batch request skipped because there are no catalog entries for {refreshScope}; Auto Pinch will use in-game market-board prices");
        return;
      }

      Svc.Log.Information($"Starting Universalis batch request for {items.Count} retainer catalog entries ({refreshScope}); Auto Pinch will wait for the response before processing");
      _bulkUniversalisRequestCts?.Cancel();
      _bulkUniversalisRequestCts?.Dispose();
      _bulkUniversalisRequestCts = new CancellationTokenSource();
      var requestId = ++_bulkUniversalisRequestId;
      _bulkUniversalisRequestCompleted = false;
      _bulkUniversalisRequestSucceeded = false;
      _bulkUniversalisRequestStartedAt = Environment.TickCount64;
      _bulkUniversalisPrices.Clear();
      _taskManager.Enqueue(WaitForBulkUniversalisPriceRefresh, BulkPriceRequestTimeoutMS, false, "WaitForUniversalisRefresh");
      _ = CompleteBulkUniversalisPriceRefresh(items, requestId, _bulkUniversalisRequestCts.Token);
    }

    private async Task CompleteBulkUniversalisPriceRefresh(
      IReadOnlyCollection<RetainerMarketItem> items,
      int requestId,
      CancellationToken cancellationToken)
    {
      Dictionary<UniversalisItemKey, List<UniversalisPriceResult>> prices = [];
      var requestSucceeded = false;
      try
      {
        prices = await _universalisPriceProvider.GetPrices(items, cancellationToken).ConfigureAwait(false);
        requestSucceeded = true;
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        Svc.Log.Warning(ex, "Failed to refresh the retainer catalog from Universalis; Auto Pinch will use in-game market-board prices for missing items");
      }

      try
      {
        await Svc.Framework.RunOnFrameworkThread(() =>
        {
          if (_disposed || requestId != _bulkUniversalisRequestId)
            return;

          _bulkUniversalisPrices = prices;
          _bulkUniversalisRequestCompleted = true;
          _bulkUniversalisRequestSucceeded = requestSucceeded;
          if (!requestSucceeded)
            Svc.Log.Warning("Universalis batch request failed; Auto Pinch will use in-game market-board prices for missing items");
          else if (prices.Count > 0)
            Svc.Log.Information($"Universalis batch response received with usable prices for {prices.Count} item qualities");
          else
            Svc.Log.Information("Universalis batch response received with no usable item prices; Auto Pinch will use in-game market-board prices for missing items");
        }).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
      {
      }
      catch (Exception ex)
      {
        Svc.Log.Warning(ex, "Failed to return the Universalis batch result to Auto Pinch");
      }
    }

    private bool? WaitForBulkUniversalisPriceRefresh()
    {
      if (_bulkUniversalisRequestCompleted)
      {
        if (_bulkUniversalisRequestSucceeded && _bulkUniversalisPrices.Count > 0)
          Svc.Log.Information($"Universalis batch request was awaited and completed with {_bulkUniversalisPrices.Count} priced item qualities; starting Auto Pinch");
        else if (_bulkUniversalisRequestSucceeded)
          Svc.Log.Information("Universalis batch request was awaited and completed without usable prices; starting Auto Pinch with in-game market-board fallback");
        else
          Svc.Log.Warning("Universalis batch request was awaited but failed; starting Auto Pinch with in-game market-board fallback");
        CancelBulkUniversalisPriceRefresh();
        return true;
      }

      if (Environment.TickCount64 - _bulkUniversalisRequestStartedAt < BulkPriceRequestTimeoutMS)
        return false;

      Svc.Log.Warning("Universalis batch price refresh timed out; Auto Pinch will use in-game market-board prices");
      CancelBulkUniversalisPriceRefresh();
      return true;
    }

    private bool TryGetBulkUniversalisPrice(
      string itemName,
      string rawItemName,
      int currentItemPrice,
      out UniversalisPriceResult price)
    {
      price = default;
      if (!ItemNameResolver.TryGetItemId(itemName, rawItemName, out var itemId))
      {
        Svc.Log.Debug($"{itemName}: Universalis item check skipped because the item ID could not be resolved");
        return false;
      }

      var isHq = itemName.Contains('\uE03C') || rawItemName.Contains('\uE03C');
      var key = new UniversalisItemKey(
        itemId,
        Plugin.Configuration.HQ
        && isHq
        && !ItemNameResolver.IsLevelOneEquippable(itemId));
      if (!_bulkUniversalisPrices.TryGetValue(key, out var candidates))
      {
        Svc.Log.Debug($"{itemName} (item {itemId}, HQ={key.HqOnly}): no Universalis batch data available; using in-game market-board fallback");
        return false;
      }

      var selectedCandidate = candidates.FirstOrDefault(candidate => candidate.LowestListingPrice != currentItemPrice);
      var candidateDetails = string.Join(
        ", ",
        candidates.Select(candidate => $"{candidate.LowestListingPrice} (suggested {candidate.SuggestedPrice})"));
      if (selectedCandidate != default)
      {
        price = selectedCandidate;
        Svc.Log.Information(
          $"{itemName} (item {itemId}, HQ={key.HqOnly}): Universalis listings [{candidateDetails}]; current retainer price {currentItemPrice}; using {price.LowestListingPrice} (suggested {price.SuggestedPrice})");
        return true;
      }

      Svc.Log.Information(
        $"{itemName} (item {itemId}, HQ={key.HqOnly}): Universalis listings [{candidateDetails}] all match the current retainer price {currentItemPrice}; no Universalis price will be used");
      return false;
    }

    private void BeginRetainerMarketDataSync()
    {
      _currentRetainerObservedItems.Clear();
      _retainerQueueIndicesReturnedToInventory.Clear();
      _currentRetainerListingIndex = -1;
      _currentRetainerQueuedItemIndex = -1;
      _currentRetainerExpectedItemCount = 0;
      _currentRetainerObservedItemCount = 0;
      _currentRetainerCatalogSyncComplete =
        _autoPinchRun
        && !string.IsNullOrWhiteSpace(_currentCharacterKey)
        && _currentRetainerId.HasValue;
    }

    private unsafe void ObserveCurrentRetainerListing(
      AddonRetainerSell* retainerSell,
      int price,
      bool countAsObserved = true)
    {
      if (!_currentRetainerCatalogSyncComplete)
        return;

      if (countAsObserved)
        _currentRetainerObservedItemCount++;

      var itemName = GetRetainerSellItemName(retainerSell);
      var rawItemName = GetRetainerSellRawItemName(retainerSell);
      if (!ItemNameResolver.TryGetItemId(itemName, rawItemName, out var itemId))
      {
        _currentRetainerCatalogSyncComplete = false;
        Svc.Log.Warning($"{itemName}: could not resolve item ID while syncing retainer market data; existing catalog entries will be preserved");
        return;
      }

      var isHq = itemName.Contains('\uE03C') || rawItemName.Contains('\uE03C');
      _currentRetainerObservedItems[(itemId, isHq)] = new RetainerMarketItem
      {
        ItemId = itemId,
        Name = ItemNameResolver.GetItemName(itemId),
        Price = price,
        IsHq = isHq,
      };
    }

    private bool? SyncCurrentRetainerMarketData()
    {
      if (!_autoPinchRun || string.IsNullOrWhiteSpace(_currentCharacterKey) || !_currentRetainerId.HasValue)
        return true;

      if (_currentRetainerObservedItemCount != _currentRetainerExpectedItemCount)
      {
        _currentRetainerCatalogSyncComplete = false;
        Svc.Log.Warning(
          $"Observed {_currentRetainerObservedItemCount} of {_currentRetainerExpectedItemCount} listings for '{_currentRetainerName}'; preserving existing catalog entries that may not have been checked");
      }

      try
      {
        if (_currentRetainerCatalogSyncComplete)
        {
          _retainerMarketDataCatalog.ReplaceRetainerItems(
            _currentCharacterKey,
            _currentCharacterName,
            _currentCharacterWorld,
            _currentRetainerId.Value,
            _currentRetainerName,
            _currentRetainerObservedItems.Values);
          Svc.Log.Information(
            $"Synced retainer market data for '{_currentRetainerName}': {_currentRetainerObservedItems.Count} currently listed items");
        }
        else
        {
          foreach (var item in _currentRetainerObservedItems.Values)
          {
            _retainerMarketDataCatalog.RecordItem(
              _currentCharacterKey,
              _currentCharacterName,
              _currentCharacterWorld,
              _currentRetainerId.Value,
              _currentRetainerName,
              item.ItemId,
              item.Name,
              item.Price,
              item.IsHq);
          }

          Svc.Log.Warning(
            $"Retainer market data for '{_currentRetainerName}' was only partially observed; updated known prices without removing existing catalog entries");
        }
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        Svc.Log.Warning(ex, $"Failed to sync Dagobert retainer market data for '{_currentRetainerName}'");
      }

      return true;
    }

    private unsafe void RecordListedItem(AddonRetainerSell* retainerSell, int price)
    {
      if (!_autoPinchRun
          || string.IsNullOrWhiteSpace(_currentCharacterKey)
          || !_currentRetainerId.HasValue)
        return;

      var itemName = GetRetainerSellItemName(retainerSell);
      var rawItemName = GetRetainerSellRawItemName(retainerSell);
      if (!ItemNameResolver.TryGetItemId(itemName, rawItemName, out var itemId))
      {
        Svc.Log.Warning($"Could not resolve item ID for {itemName}; it will not be added to the retainer market data file");
        return;
      }

      ObserveCurrentRetainerListing(retainerSell, price, false);

      try
      {
        _retainerMarketDataCatalog.RecordItem(
          _currentCharacterKey,
          _currentCharacterName,
          _currentCharacterWorld,
          _currentRetainerId.Value,
          _currentRetainerName,
          itemId,
          ItemNameResolver.GetItemName(itemId),
          price,
          itemName.Contains('\uE03C') || rawItemName.Contains('\uE03C'));
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        Svc.Log.Warning(ex, $"Failed to update Dagobert retainer market data for item {itemId}");
      }
    }

    private void CancelBulkUniversalisPriceRefresh()
    {
      _bulkUniversalisRequestId++;
      _bulkUniversalisRequestCts?.Cancel();
      _bulkUniversalisRequestCts?.Dispose();
      _bulkUniversalisRequestCts = null;
      _bulkUniversalisRequestCompleted = true;
    }

    private async Task CompleteUniversalisPriceRequest(
      string itemName,
      string rawItemName,
      int currentPrice,
      int requestId,
      CancellationToken cancellationToken)
    {
      UniversalisPriceResult? priceResult = null;

      try
      {
        priceResult = await _universalisPriceProvider.GetPrices(itemName, rawItemName, currentPrice, cancellationToken).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (Exception ex)
      {
        Svc.Log.Warning(ex, $"Failed to fetch Universalis price for {itemName}");
      }

      await Svc.Framework.RunOnFrameworkThread(() =>
      {
        if (_disposed || requestId != _universalisPriceRequestId)
          return;

        if (priceResult.HasValue)
        {
          Svc.Log.Debug($"Universalis price received: lowest={priceResult.Value.LowestListingPrice}, suggested={priceResult.Value.SuggestedPrice}");
          _newPrice = priceResult.Value.SuggestedPrice;
          _newPriceFromUniversalis = true;
        }
        else
        {
          Svc.Log.Warning($"No Universalis listing price available for {itemName}");
          _newPrice = -1;
          _newPriceFromUniversalis = false;
        }

        _universalisPriceResult = priceResult;
      });
    }

    private void CancelUniversalisPriceRequest()
    {
      _universalisPriceRequestId++;
      _universalisPriceRequestCts?.Cancel();
      _universalisPriceRequestCts?.Dispose();
      _universalisPriceRequestCts = null;
    }

    private void ClearCurrentPriceRequest()
    {
      CancelUniversalisPriceRequest();
      _oldPrice = null;
      _newPrice = null;
      _newPriceFromUniversalis = false;
      _skipCurrentItem = false;
      _currentAutoSellItemId = null;
      _currentAutoSellItemCanStack = false;
      _currentPriceUsingUniversalis = false;
      _universalisPriceResult = null;
      _currentPriceRequestStartedAt = 0;
    }

    private unsafe void SkipRetainerDialog(AddonEvent type, AddonArgs args)
    {
      // fallback for when something was improperly cleaned up
      if (!_taskManager.IsBusy)
        RemoveTalkAddonListeners();
      else
      {
        if (((AtkUnitBase*)args.Addon.Address)->IsVisible)
          new AddonMaster.Talk(args.Addon).Click();
      }
    }

    private void RetainerSellPostSetup(AddonEvent type, AddonArgs args)
    {
      if (_taskManager.IsBusy)
        return;

      if (Plugin.Configuration.EnablePostPinchkey && Plugin.KeyState[Plugin.Configuration.PostPinchKey])
      {
        _taskManager.Enqueue(ClickComparePrice, $"ClickComparePricePosted");
        _taskManager.Enqueue(SetNewPrice, $"SetNewPricePosted");
      }
    }
    private void RemoveTalkAddonListeners()
    {
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, "Talk", SkipRetainerDialog);
      Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, "Talk", SkipRetainerDialog);
    }

    private static unsafe Vector2 GetNodePosition(AtkResNode* node)
    {
      var pos = new Vector2(node->X, node->Y);
      var par = node->ParentNode;
      while (par != null)
      {
        pos *= new Vector2(par->ScaleX, par->ScaleY);
        pos += new Vector2(par->X, par->Y);
        par = par->ParentNode;
      }

      return pos;
    }

    private static unsafe Vector2 GetNodeScale(AtkResNode* node)
    {
      if (node == null) return new Vector2(1, 1);
      var scale = new Vector2(node->ScaleX, node->ScaleY);
      while (node->ParentNode != null)
      {
        node = node->ParentNode;
        scale *= new Vector2(node->ScaleX, node->ScaleY);
      }

      return scale;
    }

    private static bool? SpeakTTS(string msg)
    {
      if (!Plugin.Configuration.DontUseTTS)
      {
        SpeechSynthesizer tts = new()
        {
          Volume = Plugin.Configuration.TTSVolume
        };
        tts.SpeakAsync(msg);
        tts.SpeakCompleted += (o, e) =>
        {
          tts.Dispose();
          Svc.Log.Verbose($"Finished message: {msg} - tts disposed");
        };
      }
      return true;
    }

    private void ClearState()
    {
      CancelBulkUniversalisPriceRefresh();
      _newPrice = null;
      _newPriceFromUniversalis = false;
      _cachedPrices = [];
      _cachedPricesUseUniversalisDataCenterPrices = Plugin.Configuration.UseUniversalisDataCenterPrices;
      _skipCurrentItem = false;
      _autoPinchRun = false;
      _bulkPriceWorkflow = false;
      _bulkUniversalisPrices.Clear();
      _currentCharacterKey = string.Empty;
      _currentCharacterName = string.Empty;
      _currentCharacterWorld = string.Empty;
      _currentRetainerId = null;
      _currentRetainerName = string.Empty;
      _currentRetainerObservedItems.Clear();
      _currentRetainerCatalogSyncComplete = false;
      _currentRetainerExpectedItemCount = 0;
      _currentRetainerObservedItemCount = 0;
      ClearAutoSellingState();
      ClearCurrentPriceRequest();
    }

    private void ClearAutoSellingState()
    {
      _autoSellingEnabledForRun = false;
      _currentAutoSellItemId = null;
      _currentAutoSellItemCanStack = false;
      _autoSellProtectedItemIds.Clear();
      _autoSellSkippedItemIds.Clear();
    }

    private void ClearCachedPricesIfUniversalisSettingChanged()
    {
      var useUniversalisDataCenterPrices = Plugin.Configuration.UseUniversalisDataCenterPrices;
      if (_cachedPricesUseUniversalisDataCenterPrices == useUniversalisDataCenterPrices)
        return;

      _cachedPrices.Clear();
      _cachedPricesUseUniversalisDataCenterPrices = useUniversalisDataCenterPrices;
      Svc.Log.Debug("Use Universalis data center prices setting changed; cleared cached prices");
    }

    private readonly record struct CachedPrice(int Value, bool FromUniversalis);
  }
}
