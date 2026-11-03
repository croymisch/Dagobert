# Dagobert

FFXIV Dalamud plugin for automatically adjusting retainer Market Board prices.

## Installation

Add this repository to your Dalamud custom plugin repositories:

```text
https://raw.githubusercontent.com/SHOEGAZEssb/DalamudPluginRepo/master/pluginmaster.json
```

Open the configuration window with `/dagobert`.

## Configuration

The `/dagobert` configuration window has two tabs:

- `General` contains the normal pricing, timing, retainer, hotkey, chat, and TTS settings.
- `Min/Max Prices` contains per-item minimum and maximum prices.

### Pricing

| Option | Default | Description |
| --- | --- | --- |
| Use HQ price | On | Uses HQ listings for HQ items. If an HQ item has no HQ Market Board listing but does have NQ listings, Dagobert uses 110% of the lowest NQ listing price. |
| Undercut Mode | FixedAmount | Choose between subtracting a fixed gil amount or a percentage from the lowest price. |
| Undercut amount | 1 gil | How much to undercut by. In Percentage mode this is 1-99%. |
| Max Undercut percentage | 100% | Safety limit. Price changes that would cut more than this percentage are skipped. |
| Undercut Self | Off | When off, Dagobert will not undercut listings from your own retainers. |
| Use Universalis data center prices | Off | Controls data-center Universalis pricing for manual/post-pinch checks. Before Auto Pinch, Dagobert refreshes saved retainer listings and then eligible items in character inventory from Universalis using the current world; it waits for both requests before processing retainers. Listings already at or below that world's lowest price are skipped without changing their asking price. |
| Default amount | 0 gil | Fallback price when no price can be found. `0` disables the fallback. |
| Show inventory context menu entry | On | Adds the right-click inventory entry used to add or configure per-item min/max prices. |

### Per-Item Min/Max Prices

Use the `Min/Max Prices` tab to set item-specific price limits. `0` means no limit.

| Option | Description |
| --- | --- |
| Min | Lowest price Dagobert may set for that item. |
| Max | Highest price Dagobert may set for that item. |
| Remove | Deletes the item-specific limits. |

Right-click an item in your inventory and choose `Add Dagobert price limits` to add it to the table. If the item is already configured, the same menu entry opens the configuration window as `Configure Dagobert price limits`.

Disable `Show inventory context menu entry` in the `General` tab to hide this right-click entry.

Per-item limits are applied after Dagobert finds a candidate price, including prices from Universalis, and before the price is written to the retainer listing.

### Timing

| Option | Default | Description |
| --- | --- | --- |
| Market Board Price Check Delay | 3000 ms | Delay before opening the Market Board price list. Lower values are faster but less reliable. |
| Market Board Keep Open Time | 1000 ms | How long the Market Board window stays open while fetching prices. Lower values are faster but less reliable. |

Recommended timing values are around `3000-4000 ms` for the price check delay and `1000-2000 ms` for the keep-open time.

### Retainers and Chat

| Option | Default | Description |
| --- | --- | --- |
| Retainer Selection | All enabled | Choose which retainers are included when using Auto Pinch from the retainer list. Open the retainer list in-game to populate or refresh this list. |
| Auto Pinch after all retainers are processed | On | Automatically starts Auto Pinch when AutoRetainer finishes processing all retainers. |
| Automatically sell eligible items during Auto Pinch | Off | When enabled, Dagobert scans regular character inventory (not the Armoury Chest) after repricing each retainer and uses `Put Up for Sale` for non-stackable, tradeable equippable items and housing items in the Roof, Exterior Wall, Window, Door, Roof Decoration, Exterior Wall Decoration, Placard, Fence, Interior Wall, Flooring, Ceiling Light, Outdoor Furnishing, Furnishing, Table, Tabletop, Wall-mounted, or Rug categories. If an HQ item has no HQ Market Board listing but does have NQ listings, it uses 110% of the lowest NQ price. If there are no Market Board listings, it falls back to the highest applicable Universalis price. Otherwise normal Market Board pricing applies. It will not exceed the 20-listing limit for a retainer. |
| Minimum item price to sell | 1 gil | A non-stackable listing with a current or proposed price below this amount is removed from that retainer and returned to inventory, regardless of the automatic-selling toggle. A below-minimum item is not automatically re-listed from inventory during the same run, but copies already listed by other retainers are still checked. Inventory items are listed only when the proposed price is equal to or above this minimum. |
| Show errors in chat | On | Prints pinching errors and skipped items to chat. |
| Show Price Adjustments | On | Prints detailed price changes to chat. |
| Show Retainer Names | On | Prints each retainer name while pinching all retainers. |
| Clear retainer Cache | - | Clears the stored list of your own retainers used for self-undercut detection. |

Dagobert keeps a `RetainerMarketData.json` file in its plugin configuration directory. It records each character, retainer, and item processed for listing, including the item's name, item ID, price, and quality. Before Auto Pinch, it requests Universalis prices for the saved retainer catalog, then makes a second request for eligible inventory items. Eligible items are non-stackable and tradeable, and are either equippable or in one of the supported housing categories; only regular inventory containers are scanned, never the Armoury Chest. Both requests use the current world (not the whole data center), and Auto Pinch waits for them before processing retainers. Items without a usable Universalis result fall back to the in-game market board. Large requests are sent in API-sized batches.

The file is organized by character and retainer:

```json
{
  "Characters": [
    {
      "Key": "Character@World",
      "Name": "Character",
      "World": "World",
      "Retainers": [
        {
          "RetainerId": 123,
          "Name": "Retainer",
          "Items": [
            {
              "Name": "Item",
              "ItemId": 456,
              "Price": 1000,
              "IsHq": false
            }
          ]
        }
      ]
    }
  ]
}
```

### Hotkeys

| Option | Default | Description |
| --- | --- | --- |
| Enable Post Pinch Hotkey | On | Lets you hold the configured key while posting a new item to automatically get the lowest price. |
| Auto Post Pinch Key | Shift | Key used for post pinch. |
| Enable Pinch Hotkey | Off | Lets you press the configured key to start Auto Pinch from the retainer list or retainer sell list. |
| Auto Pinch Key | Q | Key used for Auto Pinch. |

Configured hotkeys still perform their normal in-game actions.

### Text-to-Speech

The Text-to-Speech options are shown when Windows TTS is available.

| Option | Default | Description |
| --- | --- | --- |
| All | Off | Speaks a message after Auto Pinch has processed all retainers. |
| Each | Off | Speaks a message after Auto Pinch has processed the current retainer. |
| TTS Volume | 20% | Volume for spoken messages. |

## Usage

Dagobert adds an `Auto Pinch` button to the retainer list and retainer sell list. While Auto Pinch is running, avoid interacting with the game until it finishes or you press `Cancel`.
