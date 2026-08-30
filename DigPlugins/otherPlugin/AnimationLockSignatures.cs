namespace AutoTreasureHunt;

/// <summary>
/// Signatures recovered from FuckAnimationLock.dll (client build available on
/// 2026-06-16). These patterns are client-version specific and must be
/// revalidated after every game update.
/// </summary>
internal static class AnimationLockSignatures
{
    /// <summary>
    /// Action-effect callback. The callback receives an EffectHeader whose
    /// AnimationLockTime field is the value changed by FuckAnimationLock.
    /// Delegate shape used by the original plugin:
    /// (uint, Character*, Vector3*, EffectHeader*, EffectEntry*, ulong*).
    /// </summary>
    public const string ActionEffect =
        "40 55 53 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 45 70 4C 8B BD";

    /// <summary>
    /// Action completion/dispatch helper used by the source plugin's
    /// optional event hook. It is not needed to edit AnimationLockTime.
    /// </summary>
    public const string ActionDispatch =
        "E8 ?? ?? ?? ?? 80 7E 22 11";

    /// <summary>
    /// L-block removal helper used by the source plugin's optional mode.
    /// </summary>
    public const string LBlockHelper =
        "48 ?? ?? ?? 48 ?? ?? ?? 45 ?? ?? 33 ?? E8 ?? ?? ?? ?? 84 ?? 74";

    /// <summary>
    /// Resolve the native function address through Dalamud's scanner.
    /// Keeping this wrapper here makes the signature use explicit at call
    /// sites and avoids scattering version-specific byte patterns.
    /// </summary>
    public static nint Scan(Dalamud.Plugin.Services.ISigScanner sigScanner)
        => sigScanner.ScanText(ActionEffect);
}
