using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Dagobert;

internal sealed class UniversalisClient : IDisposable
{
  private const int ListingCount = 10;
  private const int BatchSize = 100;
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNameCaseInsensitive = true,
  };

  private readonly HttpClient _client;

  public UniversalisClient()
  {
    _client = new HttpClient
    {
      BaseAddress = new Uri("https://universalis.app/api/v2/"),
    };

    _client.DefaultRequestHeaders.UserAgent.ParseAdd($"Dagobert/{Plugin.PluginInterface.Manifest.AssemblyVersion}");
  }

  public async Task<UniversalisMarketDataResponse> GetMarketData(uint itemId, string worldDcRegion, bool hqOnly, CancellationToken cancellationToken)
  {
    var query = hqOnly ? $"?listings={ListingCount}&entries=0&hq=true" : $"?listings={ListingCount}&entries=0";
    var requestUri = new Uri($"{Uri.EscapeDataString(worldDcRegion)}/{itemId}{query}", UriKind.Relative);

    using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
    request.Headers.CacheControl = new CacheControlHeaderValue
    {
      NoCache = true,
      NoStore = true,
    };
    request.Headers.Pragma.ParseAdd("no-cache");

    using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();
    using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    return await JsonSerializer.DeserializeAsync<UniversalisMarketDataResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
      ?? throw new InvalidOperationException($"Failed to parse Universalis market data for item {itemId} on {worldDcRegion}.");
  }

  public async Task<Dictionary<uint, UniversalisMarketDataResponse>> GetMarketData(
    IEnumerable<uint> itemIds,
    string worldDcRegion,
    CancellationToken cancellationToken)
  {
    var distinctItemIds = itemIds.Distinct().ToArray();
    var marketDataByItemId = new Dictionary<uint, UniversalisMarketDataResponse>();

    foreach (var itemIdBatch in distinctItemIds.Chunk(BatchSize))
    {
      var requestUri = new Uri(
        $"{Uri.EscapeDataString(worldDcRegion)}/{string.Join(",", itemIdBatch)}?listings=100&entries=0",
        UriKind.Relative);
      using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
      request.Headers.CacheControl = new CacheControlHeaderValue
      {
        NoCache = true,
        NoStore = true,
      };
      request.Headers.Pragma.ParseAdd("no-cache");

      using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
      response.EnsureSuccessStatusCode();
      using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
      var batchResponse = await JsonSerializer.DeserializeAsync<UniversalisBatchMarketDataResponse>(
        stream,
        JsonOptions,
        cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Failed to parse Universalis batch market data for {worldDcRegion}.");

      foreach (var (itemIdText, marketData) in batchResponse.Items)
      {
        if (uint.TryParse(itemIdText, out var itemId))
          marketDataByItemId[itemId] = marketData;
      }
    }

    return marketDataByItemId;
  }

  public void Dispose()
  {
    _client.Dispose();
  }
}

internal sealed class UniversalisMarketDataResponse
{
  [JsonPropertyName("listings")]
  public List<UniversalisMarketDataListing> Listings { get; set; } = [];

  [JsonPropertyName("hasData")]
  public bool HasData { get; set; }
}

internal sealed class UniversalisBatchMarketDataResponse
{
  [JsonPropertyName("items")]
  public Dictionary<string, UniversalisMarketDataResponse> Items { get; set; } = [];
}

internal sealed class UniversalisMarketDataListing
{
  [JsonPropertyName("hq")]
  public bool Hq { get; set; }

  [JsonPropertyName("pricePerUnit")]
  public long PricePerUnit { get; set; }

  [JsonPropertyName("retainerID")]
  public string? RetainerId { get; set; }

  [JsonPropertyName("worldName")]
  public string? WorldName { get; set; }
}
