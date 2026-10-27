using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace AutoTreasureHunt;

/// <summary>
/// Reads the treasure list shown by the Gambler's Lure window.
///
/// TreasureHighLow does not expose item ids in its AtkValues list.  The list
/// contains localized item names, so the names are matched against the small
/// set of items that the source plugin treats as rare loot.  This helper is
/// intentionally independent of the door-selection state machine so a reload
/// or a future state-machine change cannot invalidate the parser.
/// </summary>
internal static unsafe class DoorSelectionGreedTrapLoot
{
    private const string AddonName = "TreasureHighLow";
    private const int CurrentListIndex = 4;
    private const int EntryStride = 9;
    private const int MaxEntries = 12;
    private const int NameOffset = 2;

    // AutoTreasureMaps.Core.Stats.RareLoot.AllItems.
    private static readonly uint[] RareItemIds =
    [
        43606, 44257, 45005, 45072, 46105, 46106, 46147, 46261, 46773, 48225, 50830,
        43572, 44494, 44506, 45006, 45070, 46829, 46842, 46845, 46848, 47964, 47965,
        48152, 50795, 50797, 52357, 49846, 49847, 51262, 51263, 52630, 52631, 41805,
    ];

    private static Dictionary<string, uint>? rareByName;
    private static IDataManager? rareByNameDataManager;

    /// <summary>
    /// Reads the current treasure list.
    /// </summary>
    /// <param name="gameGui">Dalamud game GUI service.</param>
    /// <param name="dataManager">Dalamud Lumina data service.</param>
    /// <param name="rare">Rare item ids in the current (already awarded) list.</param>
    /// <param name="entries">Number of non-empty item names read from the list.</param>
    /// <returns><see langword="true"/> when the list exists and at least one item name was read.</returns>
    public static bool TryReadRareLoot(
        IGameGui gameGui,
        IDataManager dataManager,
        out List<uint> rare,
        out int entries)
    {
        rare = [];
        entries = 0;

        var addon = gameGui.GetAddonByName<AtkUnitBase>(AddonName);
        if (addon == null || !addon->IsReady || addon->AtkValues == null ||
            addon->AtkValuesCount <= CurrentListIndex)
        {
            return false;
        }

        var values = addon->AtkValues;
        var count = values[CurrentListIndex];
        if (count.Type != AtkValueType.UInt || count.UInt > MaxEntries)
        {
            return false;
        }

        var lookup = RareByName(dataManager);
        for (var entry = 0; entry < count.UInt; entry++)
        {
            var at = CurrentListIndex + entry * EntryStride;
            if (at + NameOffset >= addon->AtkValuesCount)
            {
                return false;
            }

            var value = values[at + NameOffset];
            // This Dalamud/FFXIVClientStructs build exposes the source
            // project's ConstString values through String/ManagedString; it
            // has no separate ConstString enum member.
            if (value.Type is not (AtkValueType.String or AtkValueType.ManagedString or
                AtkValueType.String8 or AtkValueType.WideString))
            {
                continue;
            }

            // Item names are SeStrings.  ExtractText strips colour and other
            // payloads so the result matches Lumina's Item.Name key even on
            // clients that colour the name by rarity.
            var name = value.Type == AtkValueType.WideString
                ? value.GetValueAsString()
                : value.String.HasValue
                    ? new ReadOnlySeStringSpan(value.String.AsSpan()).ExtractText()
                    : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            entries++;
            if (lookup.TryGetValue(name, out var itemId) && !rare.Contains(itemId))
            {
                rare.Add(itemId);
            }
        }

        return entries > 0;
    }

    /// <summary>
    /// Returns a snapshot of the ids considered rare by the source project.
    /// </summary>
    public static IReadOnlyList<uint> RareItemIdsSnapshot() => RareItemIds;

    private static Dictionary<string, uint> RareByName(IDataManager dataManager)
    {
        // IDataManager is stable for the lifetime of a plugin.  Checking the
        // instance also keeps this safe in tests that provide a replacement
        // data service.
        if (rareByName is { } built && ReferenceEquals(rareByNameDataManager, dataManager))
        {
            return built;
        }

        built = new Dictionary<string, uint>(StringComparer.Ordinal);
        var items = dataManager.GetExcelSheet<Item>();
        foreach (var itemId in RareItemIds)
        {
            if (items.GetRowOrDefault(itemId) is not { } item)
            {
                continue;
            }

            var name = item.Name.ExtractText();
            if (!string.IsNullOrEmpty(name))
            {
                built.TryAdd(name, itemId);
            }
        }

        rareByNameDataManager = dataManager;
        rareByName = built;
        return built;
    }
}
