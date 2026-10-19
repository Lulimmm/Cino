using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AutoTreasureHunt;

/// <summary>
/// Optionally suppresses client-side event-scene playback immediately after a
/// treasure-dungeon door confirmation. The normal door state machine remains
/// responsible for the server result and the floor transition.
/// </summary>
internal sealed unsafe class DoorSelectionAnimationSkipController : IDisposable
{
    // Exact prologue captured from the local IDA database for the current
    // client build. This is preferred because it identifies the intended
    // routine more narrowly than the generated fallback signature.
    internal const string IdaProcessEventPlaySignature =
        "48 89 5C 24 08 48 89 74 24 10 57 48 83 EC 40 8B 81 E8 3F 00 00 41 0F B7 F1 0F B6 BC 24 80 00 00 00 48 8B D9 85 C0 74 ?? 41 3B C0 75 ??";

    // Generated FFXIVClientStructs signature kept as a version-tolerant
    // fallback when the local IDA prologue changes after a client update.
    internal const string FcsProcessEventPlaySignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC ?? 8B 81 ?? ?? ?? ?? 41 0F B7 F1 ?? ?? ?? ?? ?? ?? ??";

    private delegate void ProcessEventPlayDelegate(
        EventFramework* framework,
        GameObject* gameObject,
        EventId eventId,
        short scene,
        ulong sceneFlags,
        uint* sceneData,
        byte sceneDataCount);

    private readonly IPluginLog log;
    private readonly Func<bool> shouldSkip;
    private Hook<ProcessEventPlayDelegate>? hook;
    private int skippedSceneCount;
    private string status = "等待初始化";

    public DoorSelectionAnimationSkipController(
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        Func<bool> shouldSkip)
    {
        this.log = log;
        this.shouldSkip = shouldSkip;

        try
        {
            var address = sigScanner.ScanText(IdaProcessEventPlaySignature);
            var source = "IDA";
            if (address == nint.Zero)
            {
                address = sigScanner.ScanText(FcsProcessEventPlaySignature);
                source = "FCS fallback";
            }
            if (address == nint.Zero)
                throw new InvalidOperationException("EventFramework.ProcessEventPlay signature resolved to null.");

            hook = interop.HookFromAddress<ProcessEventPlayDelegate>(address, OnProcessEventPlay);
            hook.Enable();
            status = $"Hook 已就绪（{source}；默认关闭）";
        }
        catch (Exception ex)
        {
            hook?.Dispose();
            hook = null;
            status = "Hook 签名不可用，已保持原动画";
            log.Warning(ex, "宝物库选门动画 Hook 初始化失败，已禁用跳过动画功能。");
        }
    }

    public string Status => hook is null
        ? status
        : skippedSceneCount > 0
            ? $"{status}；本次已拦截 {skippedSceneCount} 次场景播放"
            : status;

    public void Dispose()
    {
        hook?.Disable();
        hook?.Dispose();
        hook = null;
    }

    private void OnProcessEventPlay(
        EventFramework* framework,
        GameObject* gameObject,
        EventId eventId,
        short scene,
        ulong sceneFlags,
        uint* sceneData,
        byte sceneDataCount)
    {
        if (shouldSkip())
        {
            skippedSceneCount++;
            return;
        }

        hook!.Original(framework, gameObject, eventId, scene, sceneFlags, sceneData, sceneDataCount);
    }
}
