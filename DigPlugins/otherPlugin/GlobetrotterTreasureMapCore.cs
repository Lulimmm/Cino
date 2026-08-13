using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace AutoTreasureHunt;

/// <summary>
/// Globetrotter 的藏宝图核心功能（不包含任何 UI）。
/// 负责监听藏宝图事件、记录当前任务地图，并通过游戏原生地图接口设置红旗。
/// </summary>
internal sealed unsafe class GlobetrotterTreasureMapCore : IDisposable
{
    private const uint TreasureMapActorControlCategory = 0x54;

    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider interopProvider;
    private readonly IPluginLog log;
    private readonly Func<bool> showOnHover;
    private readonly Func<bool> showOnOpen;
    private readonly Func<bool> showOnDecipher;
    private readonly Dictionary<uint, uint> mapItemToRank = new();

    private TreasureMapPacket? lastMap;
    private Hook<HandleActorControlSelfDelegate>? actorControlHook;
    private Hook<ShowTreasureMapDelegate>? showMapHook;

    private delegate char HandleActorControlSelfDelegate(long a1, long a2, IntPtr dataPtr);
    private delegate IntPtr ShowTreasureMapDelegate(IntPtr manager, ushort rowId, ushort subRowId, byte a4);

    public GlobetrotterTreasureMapCore(
        IDataManager dataManager,
        IGameGui gameGui,
        ISigScanner sigScanner,
        IGameInteropProvider interopProvider,
        IPluginLog log,
        Func<bool>? showOnHover = null,
        Func<bool>? showOnOpen = null,
        Func<bool>? showOnDecipher = null)
    {
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.sigScanner = sigScanner;
        this.interopProvider = interopProvider;
        this.log = log;
        this.showOnHover = showOnHover ?? (() => true);
        this.showOnOpen = showOnOpen ?? (() => true);
        this.showOnDecipher = showOnDecipher ?? (() => true);

        BuildMapItemIndex();
        InstallHooks();
    }

    /// <summary>当前记录的任务地图是否存在。</summary>
    public bool HasCurrentMap => lastMap != null;

    /// <summary>当前记录的地图物品 ID；没有地图时返回 0。</summary>
    public uint CurrentMapItemId => lastMap?.EventItemId ?? 0;

    /// <summary>当前解读结果对应的 TreasureSpot 子行 ID。</summary>
    public uint CurrentMapSpotId => lastMap?.SubRowId ?? 0;

    /// <summary>手动打开当前任务地图并设置红旗。</summary>
    public void OpenCurrentMapLocation()
    {
        if (lastMap == null)
        {
            return;
        }

        if (!mapItemToRank.TryGetValue(lastMap.EventItemId, out var rankId))
        {
            log.Warning("Globetrotter 核心：未找到地图物品 {ItemId} 对应的藏宝图等级。", lastMap.EventItemId);
            return;
        }

        var spot = dataManager.GetSubrowExcelSheet<TreasureSpot>()
            .GetSubrowOrDefault(rankId, (ushort)lastMap.SubRowId);
        var location = spot?.Location.Value;
        var map = location?.Map.Value;
        var territory = map?.TerritoryType.Value;
        if (location == null || map == null || territory == null)
        {
            log.Warning("Globetrotter 核心：地图物品 {ItemId} 的藏宝点 {SpotId} 没有有效位置数据。",
                lastMap.EventItemId, lastMap.SubRowId);
            return;
        }

        var x = ToMapCoordinate(location.Value.X, map.Value.SizeFactor);
        var y = ToMapCoordinate(location.Value.Z, map.Value.SizeFactor);
        var link = new MapLinkPayload(
            territory.Value.RowId,
            map.Value.RowId,
            ConvertMapCoordinateToRawPosition(x, map.Value.SizeFactor),
            ConvertMapCoordinateToRawPosition(y, map.Value.SizeFactor));

        if (!gameGui.OpenMapWithMapLink(link))
        {
            log.Warning("Globetrotter 核心：打开地图链接失败，地图物品 {ItemId}。", lastMap.EventItemId);
            return;
        }

        lastMap.JustOpened = false;
    }

    /// <summary>兼容 /tmap 命令的入口。</summary>
    public void OpenMapLocation() => OpenCurrentMapLocation();

    /// <summary>将 HoveredItemChanged 直接转发到核心逻辑。</summary>
    public void OnHover(object? sender, ulong itemId)
    {
        if (!showOnHover() || lastMap == null || lastMap.EventItemId != itemId)
        {
            return;
        }

        OpenCurrentMapLocation();
    }

    public void Dispose()
    {
        actorControlHook?.Dispose();
        showMapHook?.Dispose();
        actorControlHook = null;
        showMapHook = null;
    }

    private void BuildMapItemIndex()
    {
        foreach (var rank in dataManager.GetExcelSheet<TreasureHuntRank>())
        {
            try
            {
                var opened = rank.KeyItemName.Value;
                if (opened.RowId != 0)
                {
                    mapItemToRank[opened.RowId] = rank.RowId;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is NullReferenceException)
            {
                // 部分 Lumina 行在插件加载早期可能处于无效状态，跳过该行即可。
                log.Warning("Globetrotter 核心：跳过无效藏宝图等级行 {RowId}。", rank.RowId);
            }
        }
    }

    private void InstallHooks()
    {
        try
        {
            var actorControlAddress = sigScanner.ScanText(
                "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 56 41 57 48 83 EC 30 33 FF 48 8B D9");
            actorControlHook = interopProvider.HookFromAddress<HandleActorControlSelfDelegate>(
                actorControlAddress, OnActorControlSelf);
            actorControlHook.Enable();

            var showMapAddress = sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 40 84 F6 0F 85 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ??");
            showMapHook = interopProvider.HookFromAddress<ShowTreasureMapDelegate>(
                showMapAddress, OnShowTreasureMap);
            showMapHook.Enable();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Globetrotter 核心：安装藏宝图钩子失败。请检查当前游戏版本的签名。", ex);
            Dispose();
        }
    }

    private IntPtr OnShowTreasureMap(IntPtr manager, ushort rankId, ushort spotId, byte arg)
    {
        try
        {
            if (lastMap == null)
            {
                var map = mapItemToRank.FirstOrDefault(pair => pair.Value == rankId);
                if (map.Key != 0)
                {
                    lastMap = new TreasureMapPacket(map.Key, spotId, false);
                }
            }

            if (showOnOpen() || (showOnDecipher() && lastMap?.JustOpened == true))
            {
                OpenCurrentMapLocation();
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Globetrotter 核心：处理藏宝图打开事件失败。");
        }

        return showMapHook!.Original(manager, rankId, spotId, arg);
    }

    private char OnActorControlSelf(long a1, long a2, IntPtr dataPtr)
    {
        try
        {
            var packet = ParsePacket(dataPtr);
            if (packet != null)
            {
                lastMap = packet;
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "Globetrotter 核心：解析藏宝图事件失败。");
        }

        return actorControlHook!.Original(a1, a2, dataPtr);
    }

    private static TreasureMapPacket? ParsePacket(IntPtr dataPtr)
    {
        if (dataPtr == IntPtr.Zero || Marshal.ReadByte(dataPtr) != TreasureMapActorControlCategory)
        {
            return null;
        }

        dataPtr += 4;
        var eventItemId = (uint)Marshal.ReadInt32(dataPtr);
        var spotId = (uint)Marshal.ReadInt32(dataPtr + 4);
        var justOpened = Marshal.ReadInt32(dataPtr + 8) == 1;
        return new TreasureMapPacket(eventItemId, spotId, justOpened);
    }

    private static int ConvertMapCoordinateToRawPosition(float position, float scale)
    {
        var factor = scale / 100.0f;
        var scaled = (((position - 1.0f) * factor / 41.0f * 2048.0f) - 1024.0f) / factor;
        return (int)(scaled * 1000.0f);
    }

    private static float ToMapCoordinate(float position, float scale)
    {
        var factor = scale / 100.0f;
        return (41.0f / factor * ((position * factor + 1024.0f) / 2048.0f)) + 1.0f;
    }

    private sealed class TreasureMapPacket
    {
        public TreasureMapPacket(uint eventItemId, uint subRowId, bool justOpened)
        {
            EventItemId = eventItemId;
            SubRowId = subRowId;
            JustOpened = justOpened;
        }

        public uint EventItemId { get; }
        public uint SubRowId { get; }
        public bool JustOpened { get; set; }
    }
}
