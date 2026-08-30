using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AutoTreasureHunt;

/// <summary>Applies the developer test-page animation-lock override.</summary>
internal sealed unsafe class AnimationLockController : IDisposable
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct AnimationLockEffectHeader
    {
        [System.Runtime.InteropServices.FieldOffset(16)]
        public float AnimationLockTime;
    }

    private readonly IObjectTable objectTable;
    private readonly IGameInteropProvider interop;
    private readonly IPluginLog log;
    private readonly Func<bool> enabled;
    private readonly Func<float> milliseconds;
    private Hook<ActionEffectDelegate>? hook;

    private delegate void ActionEffectDelegate(
        uint sourceId,
        Character* sourceCharacter,
        Vector3* position,
        AnimationLockEffectHeader* effectHeader,
        void* effectArray,
        ulong* effectTail);

    public AnimationLockController(
        IObjectTable objectTable,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        Func<bool> enabled,
        Func<float> milliseconds)
    {
        this.objectTable = objectTable;
        this.interop = interop;
        this.log = log;
        this.enabled = enabled;
        this.milliseconds = milliseconds;

        try
        {
            hook = interop.HookFromAddress<ActionEffectDelegate>(
                AnimationLockSignatures.Scan(sigScanner),
                OnActionEffect);
            hook.Enable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "动画锁签名未匹配，动画锁测试功能不可用。请更新 AnimationLockSignatures。");
        }
    }

    public void Dispose()
    {
        hook?.Dispose();
        hook = null;
    }

    private void OnActionEffect(
        uint sourceId,
        Character* sourceCharacter,
        Vector3* position,
        AnimationLockEffectHeader* effectHeader,
        void* effectArray,
        ulong* effectTail)
    {
        hook!.Original(sourceId, sourceCharacter, position, effectHeader, effectArray, effectTail);

        var localPlayer = objectTable.LocalPlayer;
        if (!enabled() || localPlayer == null || effectHeader == null ||
            sourceId != localPlayer.GameObjectId)
        {
            return;
        }

        var value = milliseconds();
        if (value < 0f)
        {
            return;
        }

        effectHeader->AnimationLockTime = value / 1000f;
        var actionManager = ActionManager.Instance();
        if (actionManager != null)
        {
            actionManager->AnimationLock = value / 1000f;
        }
    }
}
