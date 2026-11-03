using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Dagobert;

internal sealed class SkippedInventorySaleCatalog
{
  private const string FileName = "SkippedInventorySaleItems.json";
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
  };

  private readonly string _filePath;
  private readonly SkippedInventorySaleFile _data;

  public SkippedInventorySaleCatalog()
  {
    _filePath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), FileName);
    _data = Load();
    if (_migrateOnLoad)
    {
      try
      {
        Save();
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
        Svc.Log.Error(ex, $"Failed to migrate Dagobert skipped inventory sale data at {_filePath}");
      }
    }
  }

  private bool _migrateOnLoad;

  public bool IsCoolingDown(
    uint itemId,
    DateTimeOffset nowUtc,
    int cooldownMinutes)
  {
    if (cooldownMinutes <= 0)
      return false;

    var item = _data.Items.FirstOrDefault(entry => entry.ItemId == itemId);
    return item != null
           && nowUtc - item.LastCheckedUtc.ToUniversalTime() < TimeSpan.FromMinutes(cooldownMinutes);
  }

  public void RecordSkippedItem(
    uint itemId,
    string itemName,
    bool isHq,
    long? marketBoardLowestPrice)
  {
    var matchingItems = _data.Items.Where(entry => entry.ItemId == itemId).ToArray();
    var item = matchingItems.FirstOrDefault();
    if (item == null)
    {
      item = new SkippedInventorySaleItem { ItemId = itemId, IsHq = isHq };
      _data.Items.Add(item);
    }
    else
    {
      foreach (var duplicate in matchingItems.Skip(1))
        _data.Items.Remove(duplicate);
    }

    item.Name = itemName;
    item.IsHq = isHq;
    item.MarketBoardLowestPrice = marketBoardLowestPrice;
    item.LastCheckedUtc = DateTimeOffset.UtcNow;
    Save();
  }

  public void RemoveItem(uint itemId)
  {
    if (_data.Items.RemoveAll(item => item.ItemId == itemId) > 0)
      Save();
  }

  private SkippedInventorySaleFile Load()
  {
    if (!File.Exists(_filePath))
      return new SkippedInventorySaleFile();

    try
    {
      var json = File.ReadAllText(_filePath);
      if (string.IsNullOrWhiteSpace(json))
        return new SkippedInventorySaleFile();

      using var document = JsonDocument.Parse(json);
      if (document.RootElement.TryGetProperty("Items", out _))
      {
        var data = JsonSerializer.Deserialize<SkippedInventorySaleFile>(json, JsonOptions) ?? new SkippedInventorySaleFile();
        data.Items ??= [];
        return new SkippedInventorySaleFile
        {
          Items = data.Items
            .Where(item => item.ItemId != 0)
            .GroupBy(item => item.ItemId)
            .Select(group => group.MaxBy(item => item.LastCheckedUtc)!)
            .ToList(),
        };
      }

      if (document.RootElement.TryGetProperty("Characters", out _))
      {
        var legacyData = JsonSerializer.Deserialize<LegacySkippedInventorySaleFile>(json, JsonOptions)
                         ?? new LegacySkippedInventorySaleFile();
        _migrateOnLoad = true;
        return new SkippedInventorySaleFile
        {
          Items = (legacyData.Characters ?? [])
            .Where(character => character.Retainers != null)
            .SelectMany(character => character.Retainers!)
            .Where(retainer => retainer.Items != null)
            .SelectMany(retainer => retainer.Items!)
            .Where(item => item.ItemId != 0)
            .GroupBy(item => item.ItemId)
            .Select(group => group.MaxBy(item => item.LastCheckedUtc)!)
            .ToList(),
        };
      }

      return new SkippedInventorySaleFile();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
    {
      Svc.Log.Error(ex, $"Failed to load Dagobert skipped inventory sale data from {_filePath}; no skipped items will be deferred");
      return new SkippedInventorySaleFile();
    }
  }

  private void Save()
  {
    var temporaryPath = $"{_filePath}.tmp";
    Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_data, JsonOptions));
    File.Move(temporaryPath, _filePath, true);
  }
}

internal sealed class SkippedInventorySaleFile
{
  public List<SkippedInventorySaleItem> Items { get; set; } = [];
}

internal sealed class LegacySkippedInventorySaleFile
{
  public List<LegacySkippedInventorySaleCharacter>? Characters { get; set; } = [];
}

internal sealed class LegacySkippedInventorySaleCharacter
{
  public List<LegacySkippedInventorySaleRetainer>? Retainers { get; set; } = [];
}

internal sealed class LegacySkippedInventorySaleRetainer
{
  public List<SkippedInventorySaleItem>? Items { get; set; } = [];
}

internal sealed class SkippedInventorySaleItem
{
  public string Name { get; set; } = string.Empty;
  public uint ItemId { get; set; }
  public bool IsHq { get; set; }
  public long? MarketBoardLowestPrice { get; set; }
  public DateTimeOffset LastCheckedUtc { get; set; }
}
