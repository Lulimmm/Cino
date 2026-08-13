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

    public TreasureHuntLogicMode LogicMode { get; set; } = TreasureHuntLogicMode.Head;

    public float OtherPluginTestX { get; set; }

    public float OtherPluginTestY { get; set; }

    public float OtherPluginTestZ { get; set; }

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
