using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Application.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoTreasureHunt;

internal sealed unsafe class SoloTreePacketCapture : IDisposable
{
    private const int MaximumPackets = 500;
    private const int MaximumParameterCount = 64;
    private const int MaximumRawPacketSize = 4096;
    private readonly ConcurrentQueue<SoloTreeCapturedPacket> packets = new();
    private IReadOnlyList<SoloTreeOpenWindow> openWindows = Array.Empty<SoloTreeOpenWindow>();
    private DateTimeOffset openWindowsCapturedAt;
    private readonly IPluginLog log;
    private readonly Func<bool> hasDeveloperCredential;
    private readonly Func<bool> hasAutomationCredential;
    private Hook<PacketDispatcher.Delegates.HandleEventPlayPacket>? eventPlayHook;
    private Hook<PacketDispatcher.Delegates.HandleEventYieldPacket>? eventYieldHook;
    private Hook<PacketDispatcher.Delegates.SendEventCompletePacket>? eventCompleteHook;
    private Hook<ZoneClient.Delegates.SendPacket>? zoneUpHook;
    private int packetCount;
    private int captureEnabled;
    private int automationEnabled;
    private int ownsSoloTreeEvent;
    private long sequence;
    private long activeEventId;
    private int activeScene;
    private nint latestZoneClient;
    private uint latestTransportParameter3;
    private uint latestTransportParameter4;
    private bool latestTransportFlag;
    private long testSwingPacketSentAtTicks;
    private long testSwingPacketResponseAtTicks;
    private string testSwingPacketStatus = string.Empty;
    private string eventPlayStatus = "不可用";
    private string eventYieldStatus = "不可用";
    private string eventCompleteStatus = "不可用";

    public SoloTreePacketCapture(
        IGameInteropProvider interop,
        IPluginLog log,
        Func<bool> hasDeveloperCredential,
        Func<bool> hasAutomationCredential)
    {
        this.log = log;
        this.hasDeveloperCredential = hasDeveloperCredential;
        this.hasAutomationCredential = hasAutomationCredential;

        TryInstallEventPlayHook(interop);
        TryInstallEventYieldHook(interop);
        TryInstallEventCompleteHook(interop);
        TryInstallZoneUpHook(interop);
    }

    public bool IsAvailable =>
        eventPlayHook is not null || eventYieldHook is not null || eventCompleteHook is not null || zoneUpHook is not null;

    public bool IsCapturing => Volatile.Read(ref captureEnabled) != 0 && hasDeveloperCredential();

    public bool IsAutomationEnabled => Volatile.Read(ref automationEnabled) != 0;

    public bool IsAutomationAvailable =>
        eventPlayHook is not null && eventYieldHook is not null &&
        eventCompleteHook is not null && zoneUpHook is not null;

    public bool OwnsSoloTreeEvent => Volatile.Read(ref ownsSoloTreeEvent) != 0;

    public bool IsSoloTreeEventActive =>
        Interlocked.Read(ref activeEventId) == 0x00240006 &&
        Volatile.Read(ref activeScene) == 14;

    public string TestSwingPacketStatus
    {
        get
        {
            var sentAtTicks = Interlocked.Read(ref testSwingPacketSentAtTicks);
            if (sentAtTicks == 0)
                return string.Empty;

            var status = Volatile.Read(ref testSwingPacketStatus);
            if (Interlocked.Read(ref testSwingPacketResponseAtTicks) != 0)
                return status;

            return DateTime.UtcNow.Ticks - sentAtTicks >= TimeSpan.FromSeconds(2).Ticks
                ? "本地已接受发包，但 2 秒内未收到服务器 EventYield 响应。"
                : status;
        }
    }

    public bool IsSoloTreeMiniGameVisible
    {
        get
        {
            var unitManager = RaptureAtkUnitManager.Instance();
            if (unitManager == null)
                return false;

            var loadedUnits = &unitManager->AllLoadedUnitsList;
            var loadedUnitCount = Math.Min((int)loadedUnits->Count, 256);
            for (var index = 0; index < loadedUnitCount; index++)
            {
                var addon = *(AtkUnitBase**)((byte*)loadedUnits + 8 + index * sizeof(nint));
                if (addon != null && addon->IsReady && addon->IsVisible &&
                    string.Equals(Plugin.ReadAddonName(addon), "MiniGameAimg", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public int Count => Volatile.Read(ref packetCount);

    public string HookStatus =>
        $"EventPlay {eventPlayStatus} · EventYield {eventYieldStatus} · EventComplete {eventCompleteStatus} · " +
        $"Zone 上行 {(zoneUpHook is null ? "不可用" : "已启用")}";

    public IReadOnlyList<SoloTreeCapturedPacket> Snapshot() => packets.ToArray();

    public IReadOnlyList<SoloTreeOpenWindow> OpenWindowsSnapshot() => openWindows;

    public DateTimeOffset OpenWindowsCapturedAt => openWindowsCapturedAt;

    public event Action<SoloTreeCapturedPacket>? PacketObserved;

    public bool Start()
    {
        if (!hasDeveloperCredential() || !IsAvailable)
            return false;

        Clear();
        Interlocked.Exchange(ref captureEnabled, 1);
        return true;
    }

    public void Stop()
    {
        Interlocked.Exchange(ref captureEnabled, 0);
    }

    public void SetAutomationEnabled(bool enabled)
    {
        if (!enabled)
        {
            Interlocked.Exchange(ref automationEnabled, 0);
            return;
        }

        Interlocked.Exchange(ref automationEnabled, 1);
    }

    public void Clear()
    {
        Stop();
        while (packets.TryDequeue(out _))
        {
        }
        Interlocked.Exchange(ref packetCount, 0);
        openWindows = Array.Empty<SoloTreeOpenWindow>();
        openWindowsCapturedAt = default;
    }

    public int CaptureOpenWindows()
    {
        if (!hasDeveloperCredential())
            return 0;

        var captured = new List<SoloTreeOpenWindow>();
        var unitManager = RaptureAtkUnitManager.Instance();
        if (unitManager == null)
            return 0;

        var loadedUnits = &unitManager->AllLoadedUnitsList;
        var loadedUnitCount = Math.Min((int)loadedUnits->Count, 256);
        for (var index = 0; index < loadedUnitCount; index++)
        {
            var addon = *(AtkUnitBase**)((byte*)loadedUnits + 8 + index * sizeof(nint));
            if (addon == null || !addon->IsReady)
                continue;

            var text = Plugin.ReadAddonText(addon);
            var isActive = addon->IsVisible || addon->NumBlockingAddons > 0;
            if (!isActive && string.IsNullOrWhiteSpace(text))
                continue;

            captured.Add(new SoloTreeOpenWindow(
                index,
                Plugin.ReadAddonName(addon),
                addon->Id,
                addon->IsReady,
                addon->IsVisible,
                addon->NumBlockingAddons,
                text));
        }

        openWindows = captured;
        openWindowsCapturedAt = DateTimeOffset.UtcNow;
        return captured.Count;
    }

    public string ExportOpenWindowsJson() => JsonSerializer.Serialize(
        OpenWindowsSnapshot(),
        new JsonSerializerOptions { WriteIndented = true });

    public void UpdateAuthorization()
    {
        if (!hasDeveloperCredential())
        {
            Stop();
        }

        if (!hasAutomationCredential())
        {
            Interlocked.Exchange(ref automationEnabled, 0);
        }
    }

    public bool TrySendEventAction(
        uint eventId,
        short scene,
        byte subtype,
        uint? parameter,
        out string error)
        => TrySendEventAction(eventId, scene, subtype, parameter, requireAutomation: true, out error);

    private bool TrySendEventAction(
        uint eventId,
        short scene,
        byte subtype,
        uint? parameter,
        bool requireAutomation,
        out string error)
    {
        error = string.Empty;
        if (!hasAutomationCredential() || !IsSoloTreeEventActive ||
            eventId != 0x00240006 || scene != 14 || subtype is not (7 or 8 or 9 or 10 or 11))
        {
            error = "当前孤树事件或操作无效，未发送。";
            return false;
        }

        const int packetSize = 52;
        var packet = new byte[packetSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0x00, 2), 0x0119);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x08, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x10, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x20, 4), eventId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0x24, 2), unchecked((ushort)scene));
        packet[0x26] = subtype;
        packet[0x27] = parameter.HasValue ? (byte)1 : (byte)0;
        if (parameter.HasValue)
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x28, 4), parameter.Value);

        return TrySendPacket(packet, requireAutomation, out error);
    }

    public bool TryCompleteSoloTreeEvent(out string error)
    {
        error = string.Empty;
        if (!OwnsSoloTreeEvent || !IsSoloTreeEventActive || eventCompleteHook is null)
        {
            error = "没有由自动流程接管的孤树事件。";
            return false;
        }

        try
        {
            var payload = 0u;
            PacketDispatcher.SendEventCompletePacket(
                new EventId { Id = 0x00240006 }, 14, 0, &payload, 0, null);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to complete SoloTree event");
            error = ex.Message;
            return false;
        }
    }

    public bool TryResendSoloTreeSettlement(out string error)
    {
        error = string.Empty;
        if (!hasAutomationCredential() || eventCompleteHook is null || zoneUpHook is null)
        {
            error = "结算接口不可用。";
            return false;
        }

        try
        {
            var payload = 0u;
            var cashOutPacket = BuildEventActionPacket(0x00240006, 14, 8, null);
            if (!TrySendPacket(cashOutPacket, requireAutomation: true, out error))
                return false;
            PacketDispatcher.SendEventCompletePacket(
                new EventId { Id = 0x00240006 }, 14, 0, &payload, 0, null);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to retry SoloTree settlement");
            error = ex.Message;
            return false;
        }
    }

    private static byte[] BuildEventActionPacket(uint eventId, short scene, byte subtype, uint? parameter)
    {
        const int packetSize = 52;
        var packet = new byte[packetSize];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0x00, 2), 0x0119);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x08, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x10, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x20, 4), eventId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0x24, 2), unchecked((ushort)scene));
        packet[0x26] = subtype;
        packet[0x27] = parameter.HasValue ? (byte)1 : (byte)0;
        if (parameter.HasValue)
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0x28, 4), parameter.Value);
        return packet;
    }

    public bool TrySendCapturedSoloTreeSwingPacket(out string error) =>
        TrySendEventAction(0x00240006, 14, 10, 72, out error);

    public bool TrySendCapturedSoloTreeSwingPacketForTest(bool hasSoloTreeTarget, out string error)
    {
        error = string.Empty;
        if (!hasDeveloperCredential())
        {
            error = "需要开发者凭证。";
            return false;
        }

        if (IsAutomationEnabled)
        {
            error = "请先关闭自动孤树无援，再单独测试发包。";
            return false;
        }

        if (!IsSoloTreeEventActive && !(hasSoloTreeTarget && IsSoloTreeMiniGameVisible))
        {
            error = "当前没有进行中的孤树无援事件。";
            return false;
        }

        Interlocked.Exchange(ref testSwingPacketResponseAtTicks, 0);
        Volatile.Write(ref testSwingPacketStatus, "本地已接受发包，等待服务器 EventYield 响应…");
        Interlocked.Exchange(ref testSwingPacketSentAtTicks, DateTime.UtcNow.Ticks);
        if (TrySendEventAction(0x00240006, 14, 10, 72, requireAutomation: false, out error))
            return true;

        Interlocked.Exchange(ref testSwingPacketSentAtTicks, 0);
        Volatile.Write(ref testSwingPacketStatus, string.Empty);
        return false;
    }

    private bool TrySendAutomationPacket(byte[] packet, out string error)
        => TrySendPacket(packet, requireAutomation: true, out error);

    private bool TrySendPacket(byte[] packet, bool requireAutomation, out string error)
    {
        error = string.Empty;
        if (requireAutomation && !IsAutomationEnabled)
        {
            error = "自动孤树无援未启用。";
            return false;
        }

        var zoneClient = Volatile.Read(ref latestZoneClient);
        if (zoneClient == nint.Zero || zoneUpHook is null)
        {
            error = "尚未取得可用的 ZoneClient。";
            return false;
        }

        nint buffer = nint.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal(packet.Length);
            Marshal.Copy(packet, 0, buffer, packet.Length);
            // Include our own sends in the same trace as normal client packets.
            var accepted = OnZoneUp(
                (ZoneClient*)zoneClient,
                buffer,
                0,
                0,
                false);
            if (!accepted)
            {
                error = "游戏客户端拒绝了 EventAction。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to send automated SoloTree packet");
            error = "孤树无援发包异常。";
            return false;
        }
        finally
        {
            if (buffer != nint.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    public string ExportJson() => JsonSerializer.Serialize(
        Snapshot(),
        new JsonSerializerOptions { WriteIndented = true });

    public void Dispose()
    {
        Stop();
        eventPlayHook?.Dispose();
        eventYieldHook?.Dispose();
        eventCompleteHook?.Dispose();
        zoneUpHook?.Dispose();
    }

    private bool ShouldRecord =>
        Volatile.Read(ref captureEnabled) != 0 && hasDeveloperCredential();

    private bool ShouldObserve =>
        (Volatile.Read(ref captureEnabled) != 0 && hasDeveloperCredential()) ||
        Volatile.Read(ref automationEnabled) != 0 ||
        Interlocked.Read(ref testSwingPacketSentAtTicks) != 0 &&
        Interlocked.Read(ref testSwingPacketResponseAtTicks) == 0 &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref testSwingPacketSentAtTicks) < TimeSpan.FromSeconds(5).Ticks;

    private void Publish(SoloTreeCapturedPacket packet)
    {
        if (ShouldRecord)
            Record(packet);

        if (Interlocked.Read(ref testSwingPacketSentAtTicks) != 0 &&
            Interlocked.Read(ref testSwingPacketResponseAtTicks) == 0 &&
            packet.Direction == "ServerToClient" &&
            packet.EventId == 0x00240006 &&
            packet.Scene == 14 &&
            packet.Kind.StartsWith("EventYield/", StringComparison.Ordinal))
        {
            Volatile.Write(ref testSwingPacketStatus, $"已收到服务器 {packet.Kind}：{packet.ParameterText}");
            Interlocked.Exchange(ref testSwingPacketResponseAtTicks, DateTime.UtcNow.Ticks);
        }

        PacketObserved?.Invoke(packet);
    }

    private void Record(SoloTreeCapturedPacket packet)
    {
        packets.Enqueue(packet);
        var count = Interlocked.Increment(ref packetCount);
        while (count > MaximumPackets && packets.TryDequeue(out _))
            count = Interlocked.Decrement(ref packetCount);
    }

    private void OnEventPlay(
        GameObjectId objectId,
        EventId eventId,
        short scene,
        ulong sceneFlags,
        uint* sceneData,
        byte sceneDataCount)
    {
        Interlocked.Exchange(ref activeEventId, eventId.Id);
        Volatile.Write(ref activeScene, scene);
        var packetOnly = IsAutomationEnabled && eventId.Id == 0x00240006 && scene == 14;
        if (packetOnly)
            Interlocked.Exchange(ref ownsSoloTreeEvent, 1);

        if (ShouldObserve)
        {
            try
            {
                Publish(new SoloTreeCapturedPacket(
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    "ServerToClient",
                    "EventPlay",
                    eventId.Id,
                    scene,
                    objectId.Id,
                    sceneFlags,
                    0,
                    false,
                    sceneDataCount,
                    CopyUnsigned(sceneData, sceneDataCount)));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to capture EventPlay parameters");
            }
        }

        // The packet controller owns this scene. Running its Lua/UI script as well
        // leaves a SelectString prompt waiting while the server is already playing.
        if (!packetOnly)
            eventPlayHook!.OriginalDisposeSafe(objectId, eventId, scene, sceneFlags, sceneData, sceneDataCount);
    }

    private void OnEventYield(
        EventId eventId,
        short scene,
        byte yieldId,
        int* intParams,
        byte intParamCount)
    {
        Interlocked.Exchange(ref activeEventId, eventId.Id);
        Volatile.Write(ref activeScene, scene);

        if (ShouldObserve)
        {
            try
            {
                Publish(new SoloTreeCapturedPacket(
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    "ServerToClient",
                    $"EventYield/{yieldId}",
                    eventId.Id,
                    scene,
                    0,
                    0,
                    yieldId,
                    true,
                    intParamCount,
                    CopySigned(intParams, intParamCount)));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to capture EventYield parameters");
            }
        }

        // Preserve native event state so the client can process result/settlement
        // data. EventPlay is still suppressed to avoid the entry confirmation UI.
        eventYieldHook!.OriginalDisposeSafe(eventId, scene, yieldId, intParams, intParamCount);
    }

    private void OnEventComplete(
        EventId eventId,
        short scene,
        byte subtype,
        uint* payload,
        byte payloadCount,
        void* a6)
    {
        if (ShouldObserve)
        {
            try
            {
                Publish(new SoloTreeCapturedPacket(
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    "ClientToServer",
                    $"EventComplete/{subtype}",
                    eventId.Id,
                    scene,
                    0,
                    0,
                    subtype,
                    false,
                    payloadCount,
                    CopyUnsigned(payload, payloadCount)));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to capture EventComplete parameters");
            }
        }

        try
        {
            eventCompleteHook!.OriginalDisposeSafe(eventId, scene, subtype, payload, payloadCount, a6);
        }
        finally
        {
            if (Interlocked.Read(ref activeEventId) == eventId.Id)
                Interlocked.Exchange(ref activeEventId, 0);
            if (eventId.Id == 0x00240006 && scene == 14)
                Interlocked.Exchange(ref ownsSoloTreeEvent, 0);
        }
    }

    private bool OnZoneUp(ZoneClient* client, nint packet, uint parameter3, uint parameter4, bool sendFlag)
    {
        if (client != null)
        {
            Volatile.Write(ref latestZoneClient, (nint)client);
            Volatile.Write(ref latestTransportParameter3, parameter3);
            Volatile.Write(ref latestTransportParameter4, parameter4);
            latestTransportFlag = sendFlag;
        }

        var eventId = Interlocked.Read(ref activeEventId);
        if (ShouldObserve && packet != nint.Zero && eventId != 0)
        {
            try
            {
                var opcode = *(ushort*)packet;
                var declaredLength = *(uint*)(packet + 8);
                var activeSceneValue = unchecked((short)Volatile.Read(ref activeScene));
                string? packetHex = null;
                string kind = "ZoneUp";
                var messageEventId = unchecked((uint)eventId);
                var scene = activeSceneValue;
                byte subtype = 0;
                var parametersAreSigned = false;
                var parameterCount = 0;
                long[]? parameters = [];
                var totalLength = (ulong)declaredLength + 20UL;
                if (declaredLength > 0 && totalLength <= MaximumRawPacketSize)
                {
                    var bytes = new ReadOnlySpan<byte>((void*)packet, (int)totalLength).ToArray();
                    packetHex = Convert.ToHexString(bytes);

                    if ((opcode is 0x0119 or 0x0390) && totalLength >= 44)
                    {
                        var actionEventId = *(uint*)(packet + 32);
                        var actionScene = *(ushort*)(packet + 36);
                        var actionCount = *(byte*)(packet + 39);
                        var availableParameterBytes = (int)totalLength - 40;
                        if (actionEventId == unchecked((uint)eventId) &&
                            actionScene == unchecked((ushort)activeSceneValue) &&
                            actionCount <= MaximumParameterCount &&
                            actionCount * sizeof(uint) <= availableParameterBytes)
                        {
                            messageEventId = actionEventId;
                            scene = unchecked((short)actionScene);
                            subtype = *(byte*)(packet + 38);
                            parameterCount = actionCount;
                            parameters = CopyUnsigned((uint*)(packet + 40), actionCount);
                            kind = $"EventAction/{subtype}";
                        }
                    }
                }

                Publish(new SoloTreeCapturedPacket(
                    Interlocked.Increment(ref sequence),
                    DateTimeOffset.UtcNow,
                    "ClientToServer",
                    kind,
                    messageEventId,
                    scene,
                    0,
                    0,
                    subtype,
                    parametersAreSigned,
                    parameterCount,
                    parameters,
                    opcode,
                    declaredLength,
                    parameter3,
                    parameter4,
                    sendFlag,
                    packetHex));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Failed to capture Zone up packet in active event");
            }
        }

        return zoneUpHook!.OriginalDisposeSafe(client, packet, parameter3, parameter4, sendFlag);
    }

    private static long[]? CopyUnsigned(uint* values, byte count)
    {
        if (count == 0)
            return [];
        if (values == null || count > MaximumParameterCount)
            return null;

        var copy = new long[count];
        for (var index = 0; index < count; index++)
            copy[index] = values[index];
        return copy;
    }

    private static long[]? CopySigned(int* values, byte count)
    {
        if (count == 0)
            return [];
        if (values == null || count > MaximumParameterCount)
            return null;

        var copy = new long[count];
        for (var index = 0; index < count; index++)
            copy[index] = values[index];
        return copy;
    }

    private void TryInstallEventPlayHook(IGameInteropProvider interop)
    {
        try
        {
            var address = PacketDispatcher.Addresses.HandleEventPlayPacket.Value;
            if (address == nint.Zero)
                throw new InvalidOperationException("EventPlay hook address is unavailable.");
            eventPlayHook = interop.HookFromAddress<PacketDispatcher.Delegates.HandleEventPlayPacket>(address, OnEventPlay);
            eventPlayHook.Enable();
            eventPlayStatus = "已启用";
        }
        catch (Exception ex)
        {
            eventPlayHook?.Dispose();
            eventPlayHook = null;
            log.Warning(ex, "Could not install EventPlay capture hook");
        }
    }

    private void TryInstallEventYieldHook(IGameInteropProvider interop)
    {
        try
        {
            var address = PacketDispatcher.Addresses.HandleEventYieldPacket.Value;
            if (address == nint.Zero)
                throw new InvalidOperationException("EventYield hook address is unavailable.");
            eventYieldHook = interop.HookFromAddress<PacketDispatcher.Delegates.HandleEventYieldPacket>(address, OnEventYield);
            eventYieldHook.Enable();
            eventYieldStatus = "已启用";
        }
        catch (Exception ex)
        {
            eventYieldHook?.Dispose();
            eventYieldHook = null;
            log.Warning(ex, "Could not install EventYield capture hook");
        }
    }

    private void TryInstallEventCompleteHook(IGameInteropProvider interop)
    {
        try
        {
            var address = PacketDispatcher.Addresses.SendEventCompletePacket.Value;
            if (address == nint.Zero)
                throw new InvalidOperationException("EventComplete hook address is unavailable.");
            eventCompleteHook = interop.HookFromAddress<PacketDispatcher.Delegates.SendEventCompletePacket>(address, OnEventComplete);
            eventCompleteHook.Enable();
            eventCompleteStatus = "已启用";
        }
        catch (Exception ex)
        {
            eventCompleteHook?.Dispose();
            eventCompleteHook = null;
            log.Warning(ex, "Could not install EventComplete capture hook");
        }
    }

    private void TryInstallZoneUpHook(IGameInteropProvider interop)
    {
        try
        {
            var address = (nint)ZoneClient.MemberFunctionPointers.SendPacket;
            if (address == nint.Zero)
                throw new InvalidOperationException("Zone up hook address is unavailable.");
            zoneUpHook = interop.HookFromAddress<ZoneClient.Delegates.SendPacket>(address, OnZoneUp);
            zoneUpHook.Enable();
        }
        catch (Exception ex)
        {
            zoneUpHook?.Dispose();
            zoneUpHook = null;
            log.Warning(ex, "Could not install Zone up capture hook");
        }
    }

}

internal sealed record SoloTreeCapturedPacket(
    long Sequence,
    DateTimeOffset CapturedAt,
    string Direction,
    string Kind,
    uint EventId,
    short Scene,
    ulong ObjectId,
    ulong Flags,
    byte Subtype,
    bool ParametersAreSigned,
    int ParameterCount,
    long[]? Parameters,
    ushort Opcode = 0,
    uint DeclaredLength = 0,
    uint TransportParameter3 = 0,
    uint TransportParameter4 = 0,
    bool TransportFlag = false,
    string? RawPacketHex = null)
{
    public string ObjectAndFlags =>
        Kind == "ZoneUp" || Kind.StartsWith("EventAction/", StringComparison.Ordinal)
            ? $"Len={DeclaredLength}, P3={TransportParameter3}, P4={TransportParameter4}, Flag={TransportFlag}"
            : ObjectId != 0 ? $"Object=0x{ObjectId:X}" : Flags != 0 ? $"Flags=0x{Flags:X}" : "-";

    public string PacketLabel => Opcode != 0 ? $"0x{Opcode:X4}" : $"0x{EventId:X8}";

    public string DataText => Kind.StartsWith("EventAction/", StringComparison.Ordinal)
        ? $"{ParameterText} | raw={RawPacketHex}"
        : RawPacketHex is not null ? RawPacketHex : ParameterText;

    public string ParameterText
    {
        get
        {
            if (Parameters is null)
                return $"<{ParameterCount} 个参数未读取>";
            if (Parameters.Length == 0)
                return "n=0: -";

            return $"n={ParameterCount}: " + string.Join(", ", Parameters.Select(value =>
            {
                var raw = unchecked((uint)value);
                return ParametersAreSigned
                    ? $"{value} (0x{raw:X8})"
                    : $"0x{raw:X8} ({value})";
            }));
        }
    }
}

internal sealed record SoloTreeOpenWindow(
    int LoadIndex,
    string Name,
    uint Id,
    bool IsReady,
    bool IsVisible,
    int BlockingAddonCount,
    string Text);
