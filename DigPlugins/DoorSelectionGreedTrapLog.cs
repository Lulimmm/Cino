using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoTreasureHunt;

/// <summary>Stable LogMessage rows emitted by the Gambler's Lure game.</summary>
public enum DoorSelectionGreedTrapLogId : uint
{
    LureTied = 6965,
    LureRound = 6966,
    LureSprung = 6967,
    LureDeclined = 6968,
    LureDisarmed = 6969,
    LureStopped = 6970,
    LureAllDisarmed = 6971,
    LureFailed = 6972,
    LureTemptedAnother = 6973,
}

internal static class GreedTrapLogMessages
{
    public const uint LureTied = 6965;
    public const uint LureRound = 6966;
    public const uint LureSprung = 6967;
    public const uint LureDeclined = 6968;
    public const uint LureDisarmed = 6969;
    public const uint LureStopped = 6970;
    public const uint LureAllDisarmed = 6971;
    public const uint LureFailed = 6972;
    public const uint LureTemptedAnother = 6973;
}

/// <summary>Reusable classification helpers for captured lure log rows.</summary>
public readonly record struct DoorSelectionGreedTrapLogEvent(
    uint Id,
    int[] Parameters,
    DateTime SeenAt)
{
    public DoorSelectionGreedTrapLogId Kind => (DoorSelectionGreedTrapLogId)Id;

    public bool TryGetParameter(int index, out int value)
    {
        if (Parameters is not null && (uint)index < (uint)Parameters.Length)
        {
            value = Parameters[index];
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryGetReportedRound(out int round)
        => DoorSelectionGreedTrapLog.TryGetReportedRound(this, out round);

    public bool TryGetNextChallenge(out int challenge)
        => DoorSelectionGreedTrapLog.TryGetNextChallenge(this, out challenge);
}

public static class DoorSelectionGreedTrapLog
{
    private static readonly uint[] knownIds =
    [
        6965, 6966, 6967, 6968, 6969, 6970, 6971, 6972, 6973,
    ];

    public static ReadOnlySpan<uint> KnownLogIds => knownIds;

    public static bool IsKnown(uint id) => id is >= 6965 and <= 6973;

    public static bool IsKnown(DoorSelectionGreedTrapLogId id) => IsKnown((uint)id);

    public static string? NameOf(uint id) => id switch
    {
        6965 => nameof(DoorSelectionGreedTrapLogId.LureTied),
        6966 => nameof(DoorSelectionGreedTrapLogId.LureRound),
        6967 => nameof(DoorSelectionGreedTrapLogId.LureSprung),
        6968 => nameof(DoorSelectionGreedTrapLogId.LureDeclined),
        6969 => nameof(DoorSelectionGreedTrapLogId.LureDisarmed),
        6970 => nameof(DoorSelectionGreedTrapLogId.LureStopped),
        6971 => nameof(DoorSelectionGreedTrapLogId.LureAllDisarmed),
        6972 => nameof(DoorSelectionGreedTrapLogId.LureFailed),
        6973 => nameof(DoorSelectionGreedTrapLogId.LureTemptedAnother),
        _ => null,
    };

    public static bool IsTie(uint id) => id == 6965;
    public static bool IsTie(in DoorSelectionGreedTrapLogEvent value) => IsTie(value.Id);
    public static bool IsRound(uint id) => id == 6966;
    public static bool IsRound(in DoorSelectionGreedTrapLogEvent value) => IsRound(value.Id);

    public static bool IsActive(uint id) => id is 6966 or 6967 or 6969 or 6965;
    public static bool IsActive(in DoorSelectionGreedTrapLogEvent value) => IsActive(value.Id);
    public static bool IsTerminal(uint id) => id is 6968 or 6970 or 6971 or 6972 or 6973;
    public static bool IsTerminal(in DoorSelectionGreedTrapLogEvent value) => IsTerminal(value.Id);

    public static bool TryParse(uint id, ReadOnlySpan<int> parameters, out DoorSelectionGreedTrapLogEvent value)
        => TryParse(id, parameters, DateTime.UtcNow, out value);

    public static bool TryParse(uint id, ReadOnlySpan<int> parameters, DateTime seenAt, out DoorSelectionGreedTrapLogEvent value)
    {
        if (!IsKnown(id))
        {
            value = default;
            return false;
        }

        value = new DoorSelectionGreedTrapLogEvent(id, parameters.ToArray(), seenAt);
        return true;
    }

    public static bool TryGetReportedRound(in DoorSelectionGreedTrapLogEvent value, out int round)
    {
        if (!IsRound(value.Id) || !value.TryGetParameter(0, out round) || round is < 1 or > 5)
        {
            round = 0;
            return false;
        }

        return true;
    }

    public static bool TryGetNextChallenge(in DoorSelectionGreedTrapLogEvent value, out int challenge)
    {
        if (!TryGetReportedRound(value, out var round))
        {
            challenge = 0;
            return false;
        }

        challenge = Math.Min(5, round + 1);
        return true;
    }
}
