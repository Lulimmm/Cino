using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace AutoTreasureHunt;

/// <summary>Optional Deep Dungeon movement adjustment based on the supplied client signatures.</summary>
internal sealed unsafe class DeepDungeonMovementController : IDisposable
{
    public const string OnSendNormalMoveSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B D8";

    public const string OnSendCombatMoveSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 48 8B F9 41 8B F0";

    private delegate nint SendMoveDelegate(nuint a1, nint data, uint a3);

    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IClientState clientState;
    private readonly Func<bool> enabled;
    private readonly Func<float> adjustment;
    private readonly IPluginLog log;
    private Hook<SendMoveDelegate>? normalMoveHook;
    private Hook<SendMoveDelegate>? combatMoveHook;
    private volatile bool isInDeepDungeon;
    private volatile int currentFloor;
    private volatile uint currentTerritory;
    private string status = "等待签名扫描";

    public DeepDungeonMovementController(
        IFramework framework,
        ICondition condition,
        IClientState clientState,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        Func<bool> enabled,
        Func<float> adjustment)
    {
        this.framework = framework;
        this.condition = condition;
        this.clientState = clientState;
        this.log = log;
        this.enabled = enabled;
        this.adjustment = adjustment;

        try
        {
            var normalAddress = sigScanner.ScanText(OnSendNormalMoveSignature);
            var combatAddress = sigScanner.ScanText(OnSendCombatMoveSignature);
            if (normalAddress == nint.Zero || combatAddress == nint.Zero)
                throw new InvalidOperationException("A movement signature resolved to a null address.");

            normalMoveHook = interop.HookFromAddress<SendMoveDelegate>(normalAddress, OnSendNormalMove);
            combatMoveHook = interop.HookFromAddress<SendMoveDelegate>(combatAddress, OnSendCombatMove);
            normalMoveHook.Enable();
            combatMoveHook.Enable();
            status = "签名已匹配；等待进入深层迷宫";
        }
        catch (Exception ex)
        {
            normalMoveHook?.Dispose();
            combatMoveHook?.Dispose();
            normalMoveHook = null;
            combatMoveHook = null;
            status = "签名不可用，功能已禁用";
            log.Error(ex, "IC死宫移动签名初始化失败，相关功能保持禁用。");
        }

        framework.Update += Update;
    }

    public int CurrentFloor => currentFloor;

    public string Status => status;

    public bool IsAvailable => normalMoveHook is not null && combatMoveHook is not null;

    private void Update(IFramework _)
    {
        currentTerritory = clientState.TerritoryType;
        isInDeepDungeon = condition[ConditionFlag.InDeepDungeon];
        currentFloor = 0;

        if (!isInDeepDungeon)
        {
            status = IsAvailable ? "当前不在深层迷宫" : "签名不可用，功能已禁用";
            return;
        }

        try
        {
            var deepDungeon = EventFramework.Instance()->GetInstanceContentDeepDungeon();
            if (deepDungeon != null && deepDungeon->Floor > 0)
            {
                currentFloor = deepDungeon->Floor;
                status = IsAvailable ? "深层迷宫层数读取正常" : "签名不可用，功能已禁用";
            }
            else
            {
                isInDeepDungeon = false;
                status = "未读取到有效层数，移动改写保持关闭";
            }
        }
        catch (Exception ex)
        {
            isInDeepDungeon = false;
            currentFloor = 0;
            status = "读取层数失败，移动改写保持关闭";
            log.Warning(ex, "读取深层迷宫实例层数失败。");
        }
    }

    private nint OnSendNormalMove(nuint a1, nint data, uint a3)
    {
        if (data != nint.Zero && ShouldAdjust(out var value))
            ((float*)data)[3] += value;

        return normalMoveHook!.Original(a1, data, a3);
    }

    private nint OnSendCombatMove(nuint a1, nint data, uint a3)
    {
        if (data != nint.Zero && ShouldAdjust(out var value))
        {
            var values = (float*)data;
            values[4] += value;
            values[7] += value;
        }

        return combatMoveHook!.Original(a1, data, a3);
    }

    private bool ShouldAdjust(out float value)
    {
        value = adjustment();
        if (!IsAvailable || !enabled() || !isInDeepDungeon || currentFloor <= 0 ||
            !float.IsFinite(value) || value == 0f)
            return false;

        if (currentFloor % 10 == 0)
            return false;

        if ((currentTerritory == 1108 || currentTerritory == 1290) && currentFloor == 99)
            return false;

        return true;
    }

    public void Dispose()
    {
        framework.Update -= Update;
        normalMoveHook?.Disable();
        combatMoveHook?.Disable();
        normalMoveHook?.Dispose();
        combatMoveHook?.Dispose();
        normalMoveHook = null;
        combatMoveHook = null;
    }
}
