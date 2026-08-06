using Dalamud.Configuration;

namespace AutoTreasureHunt;

public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string CredentialHash { get; set; } = string.Empty;

    public CredentialRole CredentialRole { get; set; }

    public bool AutoMapSupplementEnabled { get; set; }

    public TreasureHuntLogicMode LogicMode { get; set; } = TreasureHuntLogicMode.Head;

}

public enum CredentialRole
{
    None,
    User,
    Developer,
}

public enum TreasureHuntLogicMode
{
    Head,
    Wheel,
}
