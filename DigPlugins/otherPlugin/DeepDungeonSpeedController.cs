using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using System.Diagnostics;
using System.Threading;

namespace AutoTreasureHunt;

/// <summary>
/// Deep Dungeon-only speed and acceleration controls from the supplied module.
/// The speed function signature targets the client routine that returns the
/// local movement speed. The indirect call has a four-byte displacement, so
/// all four bytes remain wildcards in the signature.
/// </summary>
internal sealed unsafe class DeepDungeonSpeedController : IDisposable
{
    public const string SpeedSignature =
        "40 57 48 83 EC ?? 48 8B F9 48 8B 49 ?? 48 8B 01 FF 90 ?? ?? ?? ?? 48 85 C0 75";

    public const string AccelerationSignature =
        "40 ?? 48 ?? ?? ?? 80 79 ?? ?? 48 ?? ?? 0f 84 ?? ?? ?? ?? 48 89 7c 24 ?? 48 ?? ?? ?? ";

    private static readonly HashSet<uint> MovementSuppressingStatuses =
    [
        14, 67, 181, 240, 436, 484, 502, 623, 674, 709, 1073, 1107, 1114,
        1141, 1147, 1259, 1344, 1394, 1595, 1790, 1796, 1935, 2099, 2158,
        2391, 2551, 2662, 2731, 3167, 3284, 3472, 3473, 3548, 3943, 3948,
        4334, 4341,
    ];

    private delegate float SpeedDelegate(nint a1);
    private delegate void AccelerationDelegate(nint a1);

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IObjectTable objectTable;
    private readonly IGameInteropProvider interop;
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;
    private readonly Func<bool> enabled;
    private readonly Func<float> speedAddition;
    private readonly Func<bool> maxAcceleration;
    private Hook<SpeedDelegate>? speedHook;
    private Hook<AccelerationDelegate>? accelerationHook;
    private volatile bool isInDeepDungeon;
    private readonly string speedSignature = SpeedSignature;
    private string status = "等待速度签名";
    private bool speedHookEnabled;
    private bool accelerationHookEnabled;
    private long nextStatusRefreshTimestamp;
    private volatile bool speedAdjustmentAllowed;
    private volatile bool speedBlockedByStatus;
    private int speedAdditionBits;
    private int originalSpeedBits;
    private volatile bool hasOriginalSpeed;

    private static long MillisecondsToTicks(int milliseconds) =>
        Math.Max(1L, Stopwatch.Frequency * milliseconds / 1000L);

    public DeepDungeonSpeedController(
        IFramework framework,
        ICondition condition,
        IObjectTable objectTable,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        Func<bool> enabled,
        Func<float> speedAddition,
        Func<bool> maxAcceleration)
    {
        this.framework = framework;
        this.condition = condition;
        this.objectTable = objectTable;
        this.interop = interop;
        this.sigScanner = sigScanner;
        this.log = log;
        this.enabled = enabled;
        this.speedAddition = speedAddition;
        this.maxAcceleration = maxAcceleration;
        framework.Update += Update;
        TryConfigureHooks();
    }

    public string Status
    {
        get
        {
            if (!hasOriginalSpeed)
                return status;

            var original = BitConverter.Int32BitsToSingle(
                Volatile.Read(ref originalSpeedBits));
            return $"{status}；原始移速：{original:F3}";
        }
    }

    public bool IsSpeedHookAvailable => speedHook is not null;

    public bool IsAccelerationHookAvailable => accelerationHook is not null;

    private void Update(IFramework _)
    {
        isInDeepDungeon = condition[ConditionFlag.InDeepDungeon];
        var now = Stopwatch.GetTimestamp();

        if (!isInDeepDungeon)
        {
            speedAdjustmentAllowed = false;
            speedBlockedByStatus = false;
            hasOriginalSpeed = false;
            UpdateHookState();
            status = speedHook is null ? "速度签名不可用" : "当前不在深层迷宫";
            return;
        }

        if (now >= nextStatusRefreshTimestamp)
            RefreshRuntimeCache(now);

        UpdateHookState();

        if (speedHook is null)
            status = "速度签名不可用";
        else if (!enabled())
            status = "移速调整未启用";
        else if (!float.IsFinite(GetFloat(ref speedAdditionBits)) || GetFloat(ref speedAdditionBits) == 0f)
            status = "移速增加值为 0";
        else if (speedBlockedByStatus)
            status = "当前受限制状态影响";
        else
            status = "移速 Hook 已启用";
    }

    private void TryConfigureHooks()
    {
        try
        {
            if (speedHook is null && !string.IsNullOrWhiteSpace(speedSignature))
            {
                var speedAddress = sigScanner.ScanText(speedSignature);
                if (speedAddress == nint.Zero)
                    throw new InvalidOperationException("速度签名未匹配到地址。");

                speedHook = interop.HookFromAddress<SpeedDelegate>(speedAddress, OnSpeed);
            }

            if (accelerationHook is null)
            {
                var accelerationAddress = sigScanner.ScanText(AccelerationSignature);
                if (accelerationAddress != nint.Zero)
                {
                    accelerationHook = interop.HookFromAddress<AccelerationDelegate>(
                        accelerationAddress,
                        OnAcceleration);
                }
            }

            status = speedHook is null ? "速度签名不可用" : "速度签名已匹配";
        }
        catch (Exception ex)
        {
            DisposeSpeedHook();
            status = "速度签名不可用，移速 Hook 未启用";
            log.Warning(ex, "IC死宫移速签名未匹配，移速调整功能已禁用。");
        }
    }

    private float OnSpeed(nint a1)
    {
        var original = speedHook!.Original(a1);

        if (!speedAdjustmentAllowed)
            return original;

        Interlocked.Exchange(ref originalSpeedBits, BitConverter.SingleToInt32Bits(original));
        hasOriginalSpeed = true;
        return original + GetFloat(ref speedAdditionBits);
    }

    private void OnAcceleration(nint a1)
    {
        if (accelerationHookEnabled && a1 != nint.Zero)
            *(float*)(a1 + 0x44) = 100f;

        accelerationHook!.Original(a1);
    }

    private void RefreshRuntimeCache(long now)
    {
        nextStatusRefreshTimestamp = now + MillisecondsToTicks(100);
        var addition = speedAddition();
        var canAdjust = enabled() && isInDeepDungeon &&
                        float.IsFinite(addition) && addition != 0f;
        Volatile.Write(ref speedAdditionBits,
            BitConverter.SingleToInt32Bits(float.IsFinite(addition) ? addition : 0f));
        speedBlockedByStatus = false;

        if (canAdjust)
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer is null)
            {
                canAdjust = false;
            }
            else
            {
                foreach (var statusEntry in localPlayer.StatusList)
                {
                    if (!MovementSuppressingStatuses.Contains(statusEntry.StatusId))
                        continue;

                    speedBlockedByStatus = true;
                    canAdjust = false;
                    break;
                }
            }
        }

        speedAdjustmentAllowed = canAdjust;
    }

    private void UpdateHookState()
    {
        var addition = GetFloat(ref speedAdditionBits);
        var shouldEnableSpeed = isInDeepDungeon && enabled() &&
                                float.IsFinite(addition) && addition != 0f;
        var shouldEnableAcceleration = isInDeepDungeon && enabled() && maxAcceleration();

        if (speedHook is not null && shouldEnableSpeed != speedHookEnabled)
        {
            if (shouldEnableSpeed)
                speedHook.Enable();
            else
                speedHook.Disable();
            speedHookEnabled = shouldEnableSpeed;
        }

        if (accelerationHook is not null && shouldEnableAcceleration != accelerationHookEnabled)
        {
            if (shouldEnableAcceleration)
                accelerationHook.Enable();
            else
                accelerationHook.Disable();
            accelerationHookEnabled = shouldEnableAcceleration;
        }
    }

    private static float GetFloat(ref int bits) =>
        BitConverter.Int32BitsToSingle(Volatile.Read(ref bits));

    private void DisposeAccelerationHook()
    {
        accelerationHook?.Disable();
        accelerationHook?.Dispose();
        accelerationHook = null;
        accelerationHookEnabled = false;
    }

    private void DisposeSpeedHook()
    {
        speedHook?.Disable();
        speedHook?.Dispose();
        speedHook = null;
        speedHookEnabled = false;
    }

    public void Dispose()
    {
        framework.Update -= Update;
        DisposeSpeedHook();
        DisposeAccelerationHook();
    }

}
