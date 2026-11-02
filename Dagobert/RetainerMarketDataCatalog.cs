using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Dagobert;

internal sealed class RetainerMarketDataCatalog
{
  private const string FileName = "RetainerMarketData.json";
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
  };

  private readonly string _filePath;
  private readonly RetainerMarketDataFile _data;

  public RetainerMarketDataCatalog()
  {
    _filePath = Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), FileName);
    _data = Load();
  }

  public IReadOnlyList<RetainerMarketItem> GetItems(string characterKey)
  {
    return _data.Characters
      .FirstOrDefault(character => character.Key.Equals(characterKey, StringComparison.OrdinalIgnoreCase))?
      .Retainers
      .SelectMany(retainer => retainer.Items)
      .Where(item => item.ItemId != 0)
      .ToList() ?? [];
  }

  public IReadOnlyList<RetainerMarketItem> GetItems(string characterKey, ulong retainerId)
  {
    return _data.Characters
      .FirstOrDefault(character => character.Key.Equals(characterKey, StringComparison.OrdinalIgnoreCase))?
      .Retainers
      .FirstOrDefault(retainer => retainer.RetainerId == retainerId)?
      .Items
      .Where(item => item.ItemId != 0)
      .ToList() ?? [];
  }

  public void RecordItem(
    string characterKey,
    string characterName,
    string worldName,
    ulong retainerId,
    string retainerName,
    uint itemId,
    string itemName,
    int price,
    bool isHq)
  {
    var character = _data.Characters.FirstOrDefault(entry =>
      entry.Key.Equals(characterKey, StringComparison.OrdinalIgnoreCase));
    if (character == null)
    {
      character = new RetainerMarketCharacter
      {
        Key = characterKey,
        Name = characterName,
        World = worldName,
      };
      _data.Characters.Add(character);
    }

    character.Name = characterName;
    character.World = worldName;

    var retainer = character.Retainers.FirstOrDefault(entry => entry.RetainerId == retainerId);
    if (retainer == null)
    {
      retainer = new RetainerMarketRetainer { RetainerId = retainerId, Name = retainerName };
      character.Retainers.Add(retainer);
    }

    retainer.Name = retainerName;
    var item = retainer.Items.FirstOrDefault(entry => entry.ItemId == itemId && entry.IsHq == isHq);
    if (item == null)
    {
      item = new RetainerMarketItem { ItemId = itemId, IsHq = isHq };
      retainer.Items.Add(item);
    }

    item.Name = itemName;
    item.Price = price;
    Save();
  }

  public void ReplaceRetainerItems(
    string characterKey,
    string characterName,
    string worldName,
    ulong retainerId,
    string retainerName,
    IEnumerable<RetainerMarketItem> items)
  {
    var character = _data.Characters.FirstOrDefault(entry =>
      entry.Key.Equals(characterKey, StringComparison.OrdinalIgnoreCase));
    if (character == null)
    {
      character = new RetainerMarketCharacter
      {
        Key = characterKey,
        Name = characterName,
        World = worldName,
      };
      _data.Characters.Add(character);
    }

    character.Name = characterName;
    character.World = worldName;

    var retainer = character.Retainers.FirstOrDefault(entry => entry.RetainerId == retainerId);
    if (retainer == null)
    {
      retainer = new RetainerMarketRetainer { RetainerId = retainerId, Name = retainerName };
      character.Retainers.Add(retainer);
    }

    retainer.Name = retainerName;
    retainer.Items = items
      .Select(item => new RetainerMarketItem
      {
        Name = item.Name,
        ItemId = item.ItemId,
        Price = item.Price,
        IsHq = item.IsHq,
      })
      .ToList();
    Save();
  }

  private RetainerMarketDataFile Load()
  {
    if (!File.Exists(_filePath))
      return new RetainerMarketDataFile();

    try
    {
      var json = File.ReadAllText(_filePath);
      if (string.IsNullOrWhiteSpace(json))
        return new RetainerMarketDataFile();

      var data = JsonSerializer.Deserialize<RetainerMarketDataFile>(json, JsonOptions) ?? new RetainerMarketDataFile();
      data.Characters ??= [];
      foreach (var character in data.Characters)
      {
        character.Retainers ??= [];
        foreach (var retainer in character.Retainers)
          retainer.Items ??= [];
      }

      return data;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
    {
      Svc.Log.Error(ex, $"Failed to load Dagobert retainer market data from {_filePath}; Auto Pinch will use in-game market-board prices");
      return new RetainerMarketDataFile();
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

internal sealed class RetainerMarketDataFile
{
  public List<RetainerMarketCharacter> Characters { get; set; } = [];
}

internal sealed class RetainerMarketCharacter
{
  public string Key { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string World { get; set; } = string.Empty;
  public List<RetainerMarketRetainer> Retainers { get; set; } = [];
}

internal sealed class RetainerMarketRetainer
{
  public ulong RetainerId { get; set; }
  public string Name { get; set; } = string.Empty;
  public List<RetainerMarketItem> Items { get; set; } = [];
}

internal sealed class RetainerMarketItem
{
  public string Name { get; set; } = string.Empty;
  public uint ItemId { get; set; }
  public int Price { get; set; }
  public bool IsHq { get; set; }
}
