using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Dalamud.Plugin.Services;

namespace AutoTreasureHunt;

/// <summary>
/// Sends Need/Greed/Pass responses through the same native request used by
/// the game's NeedGreed window. The window is not required to be open, which
/// lets the treasure loop settle loot before moving to the next room.
/// </summary>
internal static unsafe class LootRolls
{
    private const string RollRequestCallSite =
        "48 8D 0D ?? ?? ?? ?? BA 05 00 00 00 E8 ?? ?? ?? ?? EB 4A 48 8D 4F 10";
    private const int LoadLootListLength = 7;
    private const int PassArgumentLength = 5;
    private const int CallLength = 5;

    private static ISigScanner? scanner;
    private static IPluginLog? log;
    private static bool resolved;
    private static delegate* unmanaged<Loot*, uint, uint, void> rollRequest;

    public static void Initialize(ISigScanner sigScanner, IPluginLog pluginLog)
    {
        scanner = sigScanner;
        log = pluginLog;
    }

    public static bool Available => Resolve() && LootList() is not null;

    public static int Pending(Func<uint, bool> fromChest)
    {
        var loot = LootList();
        if (loot is null)
            return 0;

        var pending = 0;
        var items = loot->Items;
        for (var index = 0; index < items.Length; index++)
        {
            if (AwaitsRoll(items[index]) && fromChest(items[index].ChestObjectId))
                pending++;
        }

        return pending;
    }

    /// <summary>
    /// Collects the distinct chest object ids that still have an item waiting
    /// for this character's roll.  The source plugin uses this snapshot to
    /// keep a later coffer roll from consuming loot left by an earlier chest.
    /// </summary>
    public static int PendingChests(Span<uint> into)
    {
        var loot = LootList();
        if (loot is null)
            return 0;

        var items = loot->Items;
        var count = 0;
        for (var index = 0; index < items.Length && count < into.Length; index++)
        {
            var chest = items[index].ChestObjectId;
            if (AwaitsRoll(items[index]) && !into[..count].Contains(chest))
                into[count++] = chest;
        }

        return count;
    }

    public static int Roll(LootRoll choice, Func<uint, bool> fromChest)
    {
        if (choice == LootRoll.Manual || !Available)
            return 0;

        var loot = LootList();
        if (loot is null)
            return 0;

        var sent = 0;
        var items = loot->Items;
        for (var index = 0; index < items.Length; index++)
        {
            ref var item = ref items[index];
            if (!AwaitsRoll(item) || !fromChest(item.ChestObjectId))
                continue;

            var roll = RollFor(choice, item.RollState);
            rollRequest(loot, (uint)roll, (uint)index);
            sent++;
            log?.Debug($"Loot roll: {roll} item {item.ItemId} x{item.ItemCount}, slot {index}.");
        }

        return sent;
    }

    public static string Describe()
    {
        var loot = LootList();
        if (loot is null)
            return "loot list unavailable";

        var builder = new System.Text.StringBuilder();
        var items = loot->Items;
        for (var index = 0; index < items.Length; index++)
        {
            ref var item = ref items[index];
            if (item.ItemId == 0)
                continue;

            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append($"slot {index}: item {item.ItemId} x{item.ItemCount} from {item.ChestObjectId:X} ({item.RollState}, {item.RollResult})");
        }

        return builder.Length == 0 ? "no pending items" : builder.ToString();
    }

    private static Loot* LootList()
        => Loot.Addresses.Instance.Value == 0 ? null : Loot.Instance();

    private static bool AwaitsRoll(in LootItem item)
        => item.ItemId != 0 && item.RollResult == RollResult.UnAwarded &&
           item.RollState is RollState.UpToNeed or RollState.UpToGreed or RollState.UpToPass;

    private static RollResult RollFor(LootRoll choice, RollState state) => (choice, state) switch
    {
        (_, RollState.UpToPass) => RollResult.Passed,
        (LootRoll.Pass, _) => RollResult.Passed,
        (LootRoll.Need, RollState.UpToNeed) => RollResult.Needed,
        _ => RollResult.Greeded,
    };

    private static bool Resolve()
    {
        if (resolved)
            return rollRequest is not null;

        resolved = true;
        if (scanner is null || !scanner.TryScanText(RollRequestCallSite, out var site))
        {
            log?.Warning("Loot roll request signature was not found; Need/Greed remains manual.");
            return false;
        }

        var lootList = RelativeTarget(site, LoadLootListLength);
        var request = RelativeTarget(site + LoadLootListLength + PassArgumentLength, CallLength);
        var textStart = scanner.Module.BaseAddress + (nint)scanner.TextSectionOffset;
        if (lootList != Loot.Addresses.Instance.Value || request < textStart ||
            request >= textStart + scanner.TextSectionSize)
        {
            log?.Warning("Loot roll signature did not validate against the loot list; Need/Greed remains manual.");
            return false;
        }

        rollRequest = (delegate* unmanaged<Loot*, uint, uint, void>)request;
        return true;
    }

    private static nint RelativeTarget(nint instruction, int length)
        => instruction + length + *(int*)(instruction + length - sizeof(int));
}
