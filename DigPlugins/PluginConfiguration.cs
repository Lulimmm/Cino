using Dalamud.Configuration;

namespace AutoTreasureHunt;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string CredentialHash { get; set; } = string.Empty;

    public string SavedCredential { get; set; } = string.Empty;

    public CredentialRole CredentialRole { get; set; }

    public bool AutoMapSupplementEnabled { get; set; }

    public int MapSupplementMaxUnitPrice { get; set; } = 100000;

    public bool MapSupplementMaxUnitPriceEnabled { get; set; }

    public DoorSelectionChoice DoorSelectionFloor1To2 { get; set; } = DoorSelectionChoice.Left;
    public DoorSelectionChoice DoorSelectionFloor2To3 { get; set; } = DoorSelectionChoice.Left;
    public DoorSelectionChoice DoorSelectionFloor3To4 { get; set; } = DoorSelectionChoice.Left;
    public DoorSelectionChoice DoorSelectionFloor4To5 { get; set; } = DoorSelectionChoice.Left;

    // Matches AutoTreasureMaps' Gambler's Lure policy.  AskMe is the source
    // project's safe default: the player decides whether to challenge the
    // lure and which side to guess.
    public DoorSelectionGreedTrapPolicy DoorSelectionGreedTrapPolicy { get; set; } = DoorSelectionGreedTrapPolicy.AskMe;

    // Native Need/Greed roll policy for items shown by a treasure coffer.
    public LootRoll LootRoll { get; set; } = LootRoll.Need;

    public TreasureHuntLogicMode LogicMode { get; set; } = TreasureHuntLogicMode.Head;

    public float OtherPluginTestX { get; set; }

    public float OtherPluginTestY { get; set; }

    public float OtherPluginTestZ { get; set; }

    public bool AnimationLockOverrideEnabled { get; set; }

    public int AnimationLockOverrideMilliseconds { get; set; } = 550;

    public bool DoorSelectionSkipAnimationEnabled { get; set; }

    public bool DeepDungeonMovementEnabled { get; set; }

    public float DeepDungeonMovementAdjustment { get; set; }

    public bool DeepDungeonSpeedEnabled { get; set; }

    public float DeepDungeonSpeedAddition { get; set; }

    public bool DeepDungeonMaxAcceleration { get; set; }

}

public enum CredentialRole
{
    None,
    User,
    Developer,
    Advanced,
}

public enum TreasureHuntLogicMode
{
    Head,
    Wheel,
}

public enum DoorSelectionChoice
{
    Left,
    Right,
}

public enum DoorSelectionGreedTrapPolicy : byte
{
    AskMe,
    OpenCoffer,
    Auto,
}

public enum LootRoll : byte
{
    Need,
    Greed,
    Pass,
    Manual,
}
