using Dalamud.Plugin;
using Dalamud.Game.Chat;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using System.Security.Cryptography;
using System.Text;
using GameUtf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;
using LuminaAetheryte = Lumina.Excel.Sheets.Aetheryte;
using LuminaMap = Lumina.Excel.Sheets.Map;
using LuminaMapMarker = Lumina.Excel.Sheets.MapMarker;

namespace AutoTreasureHunt;

public sealed class Plugin : IDalamudPlugin
{
    private enum MarketPurchaseStage
    {
        None,
        WaitingForSearchAddon,
        WaitingToRunSearch,
        WaitingForSearchResults,
        WaitingBeforePurchase,
        WaitingForPurchaseConfirmation,
        WaitingForDelivery,
    }

    private enum MapSupplementStage
    {
        None,
        TravelingToBoard,
        WaitingForFirstPurchase,
        WaitingBeforeDecipher,
        WaitingForTaskMap,
        WaitingBeforeSecondInteraction,
        WaitingForSecondPurchase,
        WaitingBeforeSaddlebagMove,
        WaitingForSaddlebagContextMenu,
        WaitingForSaddlebagMap,
        WaitingBeforeThirdInteraction,
        WaitingForThirdPurchase,
    }

    private readonly record struct TreasureMapOption(
        string Name,
        string TaskName,
        uint MapItemId,
        uint TaskItemId);

    private static readonly TreasureMapOption[] TreasureMapOptions =
    [
        new("陈旧的卡冈图亚革地图", "卡冈图亚草制的宝物地图", GagantuaTreasureMapItemId, GagantuaTaskTreasureMapItemId),
        new("陈旧的狞豹革地图（占位用未适配）", "狩豹革制的宝物地图", LeopardTreasureMapItemId, LeopardTaskTreasureMapItemId),
    ];

    private const string VnavmeshInternalName = "vnavmesh";
    private const string GlobetrotterInternalName = "globetrotter";
    private const string BossModRebornInternalName = "BossModReborn";
    private const uint LeopardTreasureMapItemId = 43557;
    private const uint LeopardTaskTreasureMapItemId = 2003563;
    private const uint GagantuaTreasureMapItemId = 46185;
    private const uint GagantuaTaskTreasureMapItemId = 2003785;
    private const uint DecipherGeneralActionId = 19;
    private const uint MountRouletteGeneralActionId = 9;
    private const uint DismountGeneralActionId = 23;
    private const uint DigGeneralActionId = 20;
    private const uint RouletteMapId = 1059;
    private const uint RouletteDreamBaseId = 2014790;
    private const uint RouletteExitBaseId = 2000139;
    private const float RouletteInteractionDistance = 2f;
    private const uint LimsaLowerDecksTerritoryId = 129;
    private const uint MarketBoardBaseId = 2000402;
    private static readonly TimeSpan MarketPurchaseActionDelay = TimeSpan.FromSeconds(1);
    private const string UserCredentialHash = "58bc0f328fbfb79b6ebfec309e20974128f9c8e965f9b553cbaf8c28a1f72c61";
    private const string DeveloperCredentialHash = "6b7e18dffccc763c26914ff41a8782c1d60ec9155d86bcefeaef65c75b88b5ec";
    private static readonly GameInventoryType[] MainInventoryTypes =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    ];
    private static readonly GameInventoryType[] SaddlebagInventoryTypes =
    [
        GameInventoryType.SaddleBag1,
        GameInventoryType.SaddleBag2,
        GameInventoryType.PremiumSaddleBag1,
        GameInventoryType.PremiumSaddleBag2,
    ];
    private static readonly GameInventoryType[] TaskItemInventoryTypes =
    [
        GameInventoryType.KeyItems,
    ];
    private static readonly InventoryType[] MainClientInventoryTypes =
    [
        InventoryType.Inventory1,
        InventoryType.Inventory2,
        InventoryType.Inventory3,
        InventoryType.Inventory4,
    ];
    private static readonly InventoryType[] SaddlebagClientInventoryTypes =
    [
        InventoryType.SaddleBag1,
        InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1,
        InventoryType.PremiumSaddleBag2,
    ];

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IGameInventory gameInventory;
    private readonly IGameGui gameGui;
    private readonly IChatGui chatGui;
    private readonly IFramework framework;
    private readonly IAetheryteList aetheryteList;
    private readonly IDataManager dataManager;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly PluginConfiguration configuration;
    private CredentialRole sessionCredentialRole;
    private readonly MainWindow mainWindow;
    private bool selectMapPending;
    private bool confirmMapPending;
    private bool mountAfterTeleportPending;
    private bool mountRetryQueued;
    private bool dismountAtFlagPending;
    private bool navigationPositionSampleValid;
    private bool navigationMovementObserved;
    private float navigationSampleX;
    private float navigationSampleY;
    private float navigationSampleZ;
    private DateTime navigationPositionStableSince;
    private bool treasureChestPending;
    private ulong treasureChestEntityId;
    private bool chestPositionSampleValid;
    private float chestSampleX;
    private float chestSampleY;
    private float chestSampleZ;
    private DateTime chestPositionStableSince;
    private bool confirmTreasureChestPending;
    private DateTime treasureChestConfirmDeadline;
    private bool confirmNextTreasureChestInteraction = true;
    private bool waitForTreasureCombatStart;
    private bool treasureCombatActive;
    private bool treasureCombatLastCondition;
    private DateTime? treasureCombatEndCandidate;
    private bool treasurePortalPending;
    private ulong treasurePortalEntityId;
    private bool treasurePortalCloseDelayStarted;
    private DateTime treasurePortalInteractAt;
    private DateTime treasurePortalSearchDeadline;
    private bool confirmTreasurePortalPending;
    private DateTime treasurePortalConfirmDeadline;
    private bool autoWaitingForTaskTreasureMap;
    private bool autoMapCommandSent;
    private bool autoMapFlagRetryQueued;
    private bool hasAnnouncedFlag;
    private uint lastAnnouncedFlagTerritoryId;
    private uint lastAnnouncedFlagMapId;
    private float lastAnnouncedFlagX;
    private float lastAnnouncedFlagY;
    private bool autoWaitingForSaddlebagMove;
    private bool autoSaddlebagContextMenuPending;
    private bool autoSaddlebagMoveRequested;
    private DateTime autoSaddlebagMoveDeadline;
    private DateTime autoSaddlebagActionAt;
    private bool saddlebagStoreTestPending;
    private DateTime saddlebagStoreTestDeadline;
    private DateTime saddlebagStoreTestActionAt;
    private int saddlebagStoreTestInitialMainCount;
    private int saddlebagStoreTestInitialSaddlebagCount;
    private bool saddlebagStoreMoveRequested;
    private bool saddlebagStoreContextMenuPending;
    private bool saddlebagTakeTestPending;
    private DateTime saddlebagTakeTestDeadline;
    private DateTime saddlebagTakeTestActionAt;
    private int saddlebagTakeTestInitialMainCount;
    private int saddlebagTakeTestInitialSaddlebagCount;
    private bool saddlebagTakeMoveRequested;
    private bool saddlebagTakeContextMenuPending;
    private int selectedTreasureMapIndex;
    private int decipherRetryCount;
    private bool decipherRetryQueued;
    private bool questDecipherRetryQueued;
    private int digRetryCount;
    private bool digRetryQueued;
    private int mountAttemptCount;
    private int dismountWaitCount;
    private int dismountReadyAttemptCount;
    private int dismountUseAttemptCount;
    private DateTime wheelTeleportAcceptAt;
    private bool wheelTeleportAcceptSubmitted;
    private MapLinkPayload? wheelPendingMapLink;
    private MapLinkPayload? wheelLastMapLink;
    private bool wheelAwaitingMapChangeAndFlag;
    private uint wheelTeleportSourceMapId;
    private bool rouletteModeActive;
    private bool rouletteWasInCombat;
    private ulong rouletteTargetEntityId;
    private string rouletteTargetKind = string.Empty;
    private bool roulettePositionSampleValid;
    private float rouletteSampleX;
    private float rouletteSampleY;
    private float rouletteSampleZ;
    private DateTime roulettePositionStableSince;
    private bool rouletteExitDelayStarted;
    private DateTime rouletteExitInteractAt;
    private bool confirmRouletteDreamPending;
    private ulong rouletteDreamConfirmEntityId;
    private DateTime rouletteDreamConfirmDeadline;
    private ulong rouletteInteractedDreamEntityId;
    private bool confirmRouletteExitPending;
    private DateTime rouletteExitConfirmDeadline;
    private bool rouletteExitTestPending;
    private readonly HashSet<ulong> rouletteInteractedChestEntities = [];
    private DateTime rouletteChestDisappearDeadline;
    private bool marketBoardAfterTeleportPending;
    private bool marketBoardInteractionPending;
    private bool marketBoardInteractionAttempted;
    private DateTime marketBoardInteractionRetryAt;
    private bool marketBoardPositionSampleValid;
    private float marketBoardSampleX;
    private float marketBoardSampleY;
    private float marketBoardSampleZ;
    private DateTime marketBoardPositionStableSince;
    private MarketPurchaseStage marketPurchaseStage;
    private DateTime marketPurchaseDeadline;
    private DateTime marketSearchRunAt;
    private uint marketPurchaseItemId;
    private string marketPurchaseItemName = string.Empty;
    private int marketPurchaseInitialMainCount;
    private bool marketPurchaseAutomatic;
    private bool marketPurchaseSnapshotReady;
    private MarketBoardListing marketPurchaseListingSnapshot;
    private uint marketPurchaseSavedUnitPrice;
    private readonly HashSet<ulong> marketSubmittedListingIds = [];
    private bool automaticMapSupplementTriggered;
    private bool automaticMapSupplementRunning;
    private MapSupplementStage mapSupplementStage;
    private DateTime mapSupplementActionAt;
    private DateTime mapSupplementDeadline;
    private int mapSupplementPurchaseStep;
    private bool mapSupplementResumeAutoHunt;
    private bool optimizedInteractionLoaded;
    private bool emergencyStopActive;
    private string workflowWatchdogState = string.Empty;
    private DateTime workflowWatchdogStateSince;
    private DateTime workflowWatchdogCooldownUntil;
    private int workflowWatchdogRecoveryCount;
    private string movementWatchdogState = string.Empty;
    private float movementWatchdogSampleX;
    private float movementWatchdogSampleY;
    private float movementWatchdogSampleZ;
    private DateTime movementWatchdogLastMovedAt;
    private DateTime movementWatchdogRetryCooldownUntil;
    private readonly HashSet<ulong> rouletteInteractedEntities = [];

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameInventory gameInventory,
        IGameGui gameGui,
        IChatGui chatGui,
        IFramework framework,
        IAetheryteList aetheryteList,
        IDataManager dataManager,
        IClientState clientState,
        ICondition condition,
        ICommandManager commandManager,
        IObjectTable objectTable,
        ITargetManager targetManager)
    {
        this.pluginInterface = pluginInterface;
        this.gameInventory = gameInventory;
        this.gameGui = gameGui;
        this.chatGui = chatGui;
        this.framework = framework;
        this.aetheryteList = aetheryteList;
        this.dataManager = dataManager;
        this.clientState = clientState;
        this.condition = condition;
        this.commandManager = commandManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        if (!Enum.IsDefined(configuration.LogicMode))
        {
            configuration.LogicMode = TreasureHuntLogicMode.Head;
        }

        ValidateSavedCredential();
        mainWindow = new MainWindow(this, this.pluginInterface);
        this.gameInventory.InventoryChanged += OnInventoryChanged;
        this.chatGui.ChatMessage += OnChatMessage;
        this.clientState.ZoneInit += OnZoneInit;
        this.clientState.MapIdChanged += OnMapIdChanged;
        this.framework.Update += OnFrameworkUpdate;
        this.pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        this.pluginInterface.UiBuilder.Draw += mainWindow.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += mainWindow.Open;
        _ = this.framework.RunOnTick(
            InitializeRuntimeState,
            delay: TimeSpan.FromSeconds(1));
    }

    private void InitializeRuntimeState()
    {
        IsVnavmeshRunning = CheckVnavmeshRunning();
        IsGlobetrotterRunning = CheckGlobetrotterRunning();
        IsBossModRebornRunning = CheckBossModRebornRunning();
        RefreshTreasureMapCounts();
    }

    public bool IsVnavmeshRunning { get; private set; }

    public bool IsGlobetrotterRunning { get; private set; }

    public bool IsBossModRebornRunning { get; private set; }

    public bool IsAutoTreasureHuntEnabled { get; private set; }

    public bool IsCredentialValidated => sessionCredentialRole != CredentialRole.None;

    public bool HasDeveloperCredential => sessionCredentialRole == CredentialRole.Developer;

    public bool HasSavedCredential => GetCredentialRole(configuration.CredentialHash) != CredentialRole.None;

    public string CredentialRoleName => sessionCredentialRole switch
    {
        CredentialRole.User => "用户凭证",
        CredentialRole.Developer => "开发者凭证",
        _ => "未验证",
    };

    public int MainInventoryTreasureMapCount { get; private set; }

    public int SaddlebagTreasureMapCount { get; private set; }

    public int TreasureMapCount => MainInventoryTreasureMapCount + SaddlebagTreasureMapCount;

    public bool HasTreasureMap => TreasureMapCount > 0;

    public int TaskTreasureMapCount { get; private set; }

    public bool HasTaskTreasureMap => TaskTreasureMapCount > 0;

    public bool CanUseTreasureMap => MainInventoryTreasureMapCount > 0;

    public string TreasureMapUseStatus { get; private set; } = "尚未测试使用地图。";

    public string TeleportTestStatus { get; private set; } = "尚未测试传送水晶。";

    public string AutoTreasureHuntStatus { get; private set; } = "自动挖宝尚未开启。";

    public string WheelMapLinkStatus { get; private set; } = "尚未检测到聊天地图链接。";

    public string SaddlebagMoveStatus { get; private set; } = "尚未测试取出鞍囊地图。";

    public bool IsAutoMapSupplementEnabled => configuration.AutoMapSupplementEnabled;

    public TreasureHuntLogicMode SelectedLogicMode => configuration.LogicMode;

    public bool IsHeadLogicSelected => SelectedLogicMode == TreasureHuntLogicMode.Head;

    public bool IsWheelLogicSelected => SelectedLogicMode == TreasureHuntLogicMode.Wheel;

    public string SelectedLogicModeName => IsHeadLogicSelected ? "车头" : "车轮";

    public bool IsRouletteMode => clientState.MapId == RouletteMapId;

    public string CurrentLogicName => !IsAutoTreasureHuntEnabled
        ? $"{SelectedLogicModeName}（未运行）"
        : IsWheelLogicSelected
            ? "车轮 / 接受传送并前往红旗"
            : rouletteModeActive || IsRouletteMode
                ? "车头 / 转盘"
                : automaticMapSupplementRunning ||
                  marketBoardAfterTeleportPending ||
                  marketBoardInteractionPending
                    ? "车头 / 补图"
                    : "车头 / 野外挖宝";

    public int SelectedTreasureMapIndex => selectedTreasureMapIndex;

    public int TreasureMapOptionCount => TreasureMapOptions.Length;

    public string SelectedTreasureMapName => TreasureMapOptions[selectedTreasureMapIndex].Name;

    public string SelectedTaskTreasureMapName => TreasureMapOptions[selectedTreasureMapIndex].TaskName;

    public uint SelectedTreasureMapItemId => TreasureMapOptions[selectedTreasureMapIndex].MapItemId;

    public uint SelectedTaskTreasureMapItemId => TreasureMapOptions[selectedTreasureMapIndex].TaskItemId;

    public string GetTreasureMapOptionName(int index) => TreasureMapOptions[index].Name;

    public event Action<bool>? VnavmeshRunningChanged;

    public event Action<bool>? GlobetrotterRunningChanged;

    public void Dispose()
    {
        UnloadOptimizedInteraction();
        pluginInterface.UiBuilder.OpenMainUi -= mainWindow.Open;
        pluginInterface.UiBuilder.Draw -= mainWindow.Draw;
        pluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        framework.Update -= OnFrameworkUpdate;
        clientState.MapIdChanged -= OnMapIdChanged;
        clientState.ZoneInit -= OnZoneInit;
        gameInventory.InventoryChanged -= OnInventoryChanged;
        chatGui.ChatMessage -= OnChatMessage;
    }

    public void SetAutoTreasureHuntEnabled(bool enabled)
    {
        if (!IsCredentialValidated)
        {
            IsAutoTreasureHuntEnabled = false;
            AutoTreasureHuntStatus = "凭证未验证，插件不会运行。";
            return;
        }

        IsAutoTreasureHuntEnabled = enabled;
        ResetMovementWatchdog();
        if (enabled)
        {
            emergencyStopActive = false;
            RefreshTreasureMapCounts();
            if (IsHeadLogicSelected)
            {
                AutoTreasureHuntStatus = "车头逻辑：正在检查背包和任务道具中的地图...";
                _ = framework.RunOnFrameworkThread(StartAutoTreasureHuntOnFrameworkThread);
            }
            else
            {
                ResetWheelTeleportAcceptance();
                ResetWheelMapLinkPending();
                AutoTreasureHuntStatus = "车轮逻辑已开启，正在等待传送请求。";
            }
        }
        else
        {
            if (rouletteModeActive)
            {
                ExitRouletteMode();
            }

            commandManager.ProcessCommand("/vnav stop");
            commandManager.ProcessCommand("/bmrai off");
            CloseWorkflowBlockingWindows();
            if (automaticMapSupplementRunning)
            {
                FailMapSupplement("自动挖宝已关闭。");
            }

            autoWaitingForTaskTreasureMap = false;
            autoWaitingForSaddlebagMove = false;
            autoSaddlebagContextMenuPending = false;
            autoSaddlebagMoveRequested = false;
            ResetWheelTeleportAcceptance();
            ResetWheelMapLinkPending();
            RefreshTreasureMapCounts();
            AutoTreasureHuntStatus = "自动挖宝已关闭。";
        }
    }

    public void SetLogicMode(TreasureHuntLogicMode mode)
    {
        if (!Enum.IsDefined(mode) || configuration.LogicMode == mode)
        {
            return;
        }

        var restartSelectedMode = IsAutoTreasureHuntEnabled;
        if (restartSelectedMode)
        {
            EmergencyStop();
        }

        configuration.LogicMode = mode;
        pluginInterface.SavePluginConfig(configuration);

        if (restartSelectedMode)
        {
            SetAutoTreasureHuntEnabled(true);
        }
        else
        {
            AutoTreasureHuntStatus = IsHeadLogicSelected
                ? "已切换到车头逻辑。"
                : "已切换到车轮逻辑；开启后将自动接受传送请求。";
        }
    }

    public void SetAutoMapSupplementEnabled(bool enabled)
    {
        configuration.AutoMapSupplementEnabled = enabled;
        if (!enabled)
        {
            automaticMapSupplementTriggered = false;
            if (automaticMapSupplementRunning)
            {
                FailMapSupplement("自动补图已关闭。");
            }
            else
            {
                UnloadOptimizedInteraction();
                ResetMarketPurchase();
            }
        }

        pluginInterface.SavePluginConfig(configuration);
    }

    public bool ValidateCredential(string credential)
    {
        var hash = ComputeCredentialHash(credential.Trim());
        var role = GetCredentialRole(hash);

        if (role == CredentialRole.None)
        {
            return false;
        }

        sessionCredentialRole = role;
        configuration.CredentialHash = hash;
        configuration.CredentialRole = role;
        pluginInterface.SavePluginConfig(configuration);
        return true;
    }

    public bool ValidateRememberedCredential()
    {
        sessionCredentialRole = GetCredentialRole(configuration.CredentialHash);
        configuration.CredentialRole = sessionCredentialRole;
        return sessionCredentialRole != CredentialRole.None;
    }

    public void ClearCredential()
    {
        IsAutoTreasureHuntEnabled = false;
        UnloadOptimizedInteraction();
        sessionCredentialRole = CredentialRole.None;
    }

    private void ValidateSavedCredential()
    {
        sessionCredentialRole = GetCredentialRole(configuration.CredentialHash);
        configuration.CredentialRole = sessionCredentialRole;
    }

    private static CredentialRole GetCredentialRole(string credentialHash)
    {
        return credentialHash switch
        {
            UserCredentialHash => CredentialRole.User,
            DeveloperCredentialHash => CredentialRole.Developer,
            _ => CredentialRole.None,
        };
    }

    private static string ComputeCredentialHash(string credential)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential))).ToLowerInvariant();
    }

    public void SetSelectedTreasureMap(int index)
    {
        if (automaticMapSupplementRunning)
        {
            AutoTreasureHuntStatus = "补图逻辑运行中，暂时不能切换地图类型。";
            return;
        }

        if (index < 0 || index >= TreasureMapOptions.Length || index == selectedTreasureMapIndex)
        {
            return;
        }

        selectedTreasureMapIndex = index;
        RefreshTreasureMapCounts();
        if (IsAutoTreasureHuntEnabled)
        {
            AutoTreasureHuntStatus = $"已切换为 {SelectedTreasureMapName}，正在重新检查地图。";
            _ = framework.RunOnFrameworkThread(StartAutoTreasureHuntOnFrameworkThread);
        }
    }

    private bool TrySelectMapTypeForAutomaticRun()
    {
        for (var index = 0; index < TreasureMapOptions.Length; index++)
        {
            var option = TreasureMapOptions[index];
            var taskCount = CountTreasureMaps(TaskItemInventoryTypes, option.TaskItemId);
            if (taskCount > 0)
            {
                selectedTreasureMapIndex = index;
                return true;
            }
        }

        for (var index = 0; index < TreasureMapOptions.Length; index++)
        {
            var option = TreasureMapOptions[index];
            var mapCount = CountTreasureMaps(MainInventoryTypes, option.MapItemId) +
                           CountTreasureMaps(SaddlebagInventoryTypes, option.MapItemId);
            if (mapCount > 0)
            {
                selectedTreasureMapIndex = index;
                return true;
            }
        }

        return false;
    }

    private void StartAutoTreasureHuntOnFrameworkThread()
    {
        if (!IsHeadLogicSelected ||
            !IsAutoTreasureHuntEnabled ||
            IsRouletteMode ||
            automaticMapSupplementRunning)
        {
            return;
        }

        TrySelectMapTypeForAutomaticRun();
        RefreshTreasureMapCounts();
        if (HasTaskTreasureMap)
        {
            autoWaitingForTaskTreasureMap = false;
            AutoTreasureHuntStatus = "任务道具中已有地图，跳过解读并开始传送流程。";
            TestTeleportToOpenedMapAetheryte();
            return;
        }

        if (!HasTreasureMap)
        {
            autoWaitingForTaskTreasureMap = false;
            AutoTreasureHuntStatus = "主背包和陆行鸟鞍囊中都没有可解读的地图。";
            return;
        }

        if (!CanUseTreasureMap)
        {
            autoWaitingForSaddlebagMove = true;
            autoSaddlebagContextMenuPending = false;
            autoSaddlebagMoveRequested = false;
            autoSaddlebagMoveDeadline = DateTime.UtcNow.AddSeconds(20);
            autoSaddlebagActionAt = DateTime.UtcNow;
            AutoTreasureHuntStatus = $"检测到 {SaddlebagTreasureMapCount} 张地图位于陆行鸟鞍囊，正在通过右键菜单取出。";
            return;
        }

        autoWaitingForTaskTreasureMap = true;
        AutoTreasureHuntStatus = $"任务道具中没有地图，正在解读主背包中的地图（总计 {TreasureMapCount} 张）。";
        UseTreasureMapOnFrameworkThread();
    }

    public void TestMoveMapFromSaddlebag()
    {
        ResumeAfterEmergencyStop();
        RefreshTreasureMapCounts();
        if (saddlebagStoreTestPending || saddlebagTakeTestPending)
        {
            SaddlebagMoveStatus = "鞍囊测试正在运行，请等待当前操作结束。";
            return;
        }

        if (SaddlebagTreasureMapCount < 1)
        {
            SaddlebagMoveStatus = $"陆行鸟鞍囊中没有{SelectedTreasureMapName}。";
            return;
        }

        saddlebagTakeTestInitialMainCount = MainInventoryTreasureMapCount;
        saddlebagTakeTestInitialSaddlebagCount = SaddlebagTreasureMapCount;
        saddlebagTakeTestDeadline = DateTime.UtcNow.AddSeconds(20);
        saddlebagTakeTestActionAt = DateTime.UtcNow;
        saddlebagTakeMoveRequested = false;
        saddlebagTakeContextMenuPending = false;
        saddlebagTakeTestPending = true;
        SaddlebagMoveStatus = $"正在测试从陆行鸟鞍囊取出{SelectedTreasureMapName}...";
    }

    public void TestMoveMapToSaddlebag()
    {
        ResumeAfterEmergencyStop();
        RefreshTreasureMapCounts();
        if (automaticMapSupplementRunning)
        {
            SaddlebagMoveStatus = "自动补图运行中，不能同时测试存入陆行鸟鞍囊。";
            return;
        }

        if (saddlebagStoreTestPending || saddlebagTakeTestPending)
        {
            SaddlebagMoveStatus = "鞍囊测试正在运行，请等待当前操作结束。";
            return;
        }

        if (MainInventoryTreasureMapCount < 1)
        {
            SaddlebagMoveStatus = $"主背包中没有{SelectedTreasureMapName}。";
            return;
        }

        if (SaddlebagTreasureMapCount > 0)
        {
            SaddlebagMoveStatus = $"陆行鸟鞍囊中已经有{SelectedTreasureMapName}。";
            return;
        }

        saddlebagStoreTestInitialMainCount = MainInventoryTreasureMapCount;
        saddlebagStoreTestInitialSaddlebagCount = SaddlebagTreasureMapCount;
        saddlebagStoreTestDeadline = DateTime.UtcNow.AddSeconds(20);
        saddlebagStoreTestActionAt = DateTime.UtcNow;
        saddlebagStoreMoveRequested = false;
        saddlebagStoreContextMenuPending = false;
        saddlebagStoreTestPending = true;
        SaddlebagMoveStatus = $"正在测试将{SelectedTreasureMapName}存入陆行鸟鞍囊...";
    }

    private void TryHandleSaddlebagStoreTest()
    {
        if (!saddlebagStoreTestPending)
        {
            return;
        }

        RefreshTreasureMapCounts();
        if (MainInventoryTreasureMapCount < saddlebagStoreTestInitialMainCount &&
            SaddlebagTreasureMapCount > saddlebagStoreTestInitialSaddlebagCount)
        {
            saddlebagStoreTestPending = false;
            saddlebagStoreMoveRequested = false;
            saddlebagStoreContextMenuPending = false;
            CloseSaddlebagWindow();
            SaddlebagMoveStatus = $"测试成功：{SelectedTreasureMapName}已存入陆行鸟鞍囊。";
            return;
        }

        if (DateTime.UtcNow > saddlebagStoreTestDeadline)
        {
            saddlebagStoreTestPending = false;
            saddlebagStoreMoveRequested = false;
            saddlebagStoreContextMenuPending = false;
            CloseSaddlebagWindow();
            SaddlebagMoveStatus = "测试超时：游戏未确认地图已进入陆行鸟鞍囊。";
            return;
        }

        if (DateTime.UtcNow < saddlebagStoreTestActionAt)
        {
            return;
        }

        if (saddlebagStoreMoveRequested)
        {
            return;
        }

        if (saddlebagStoreContextMenuPending)
        {
            if (TrySelectFirstInventoryContextMenuOption(fromSaddlebag: false))
            {
                saddlebagStoreContextMenuPending = false;
                saddlebagStoreMoveRequested = true;
                SaddlebagMoveStatus = "已选择右键菜单第一项，等待地图实际进入陆行鸟鞍囊。";
            }

            return;
        }

        if (TryOpenSelectedMapInventoryContextMenu(fromSaddlebag: false))
        {
            saddlebagStoreContextMenuPending = true;
            saddlebagStoreTestActionAt = DateTime.UtcNow.AddMilliseconds(250);
            SaddlebagMoveStatus = "已在主背包地图槽位打开右键菜单，准备选择第一项。";
            return;
        }

        saddlebagStoreTestActionAt = DateTime.UtcNow.AddSeconds(1);
    }

    private void TryHandleSaddlebagTakeTest()
    {
        if (!saddlebagTakeTestPending)
        {
            return;
        }

        RefreshTreasureMapCounts();
        if (MainInventoryTreasureMapCount > saddlebagTakeTestInitialMainCount &&
            SaddlebagTreasureMapCount < saddlebagTakeTestInitialSaddlebagCount)
        {
            saddlebagTakeTestPending = false;
            saddlebagTakeMoveRequested = false;
            saddlebagTakeContextMenuPending = false;
            CloseSaddlebagWindow();
            SaddlebagMoveStatus = $"测试成功：{SelectedTreasureMapName}已取出到主背包。";
            return;
        }

        if (DateTime.UtcNow > saddlebagTakeTestDeadline)
        {
            saddlebagTakeTestPending = false;
            saddlebagTakeMoveRequested = false;
            saddlebagTakeContextMenuPending = false;
            CloseSaddlebagWindow();
            SaddlebagMoveStatus = "测试超时：游戏未确认地图已进入主背包。";
            return;
        }

        if (DateTime.UtcNow < saddlebagTakeTestActionAt || saddlebagTakeMoveRequested)
        {
            return;
        }

        if (saddlebagTakeContextMenuPending)
        {
            if (TrySelectFirstInventoryContextMenuOption(fromSaddlebag: true))
            {
                saddlebagTakeContextMenuPending = false;
                saddlebagTakeMoveRequested = true;
                SaddlebagMoveStatus = "已选择右键菜单第一项，等待地图实际进入主背包。";
            }

            return;
        }

        if (TryOpenSelectedMapInventoryContextMenu(fromSaddlebag: true))
        {
            saddlebagTakeContextMenuPending = true;
            saddlebagTakeTestActionAt = DateTime.UtcNow.AddMilliseconds(250);
            SaddlebagMoveStatus = "已在鞍囊地图槽位打开右键菜单，准备选择第一项。";
            return;
        }

        saddlebagTakeTestActionAt = DateTime.UtcNow.AddSeconds(1);
    }

    private void CheckAutomaticSaddlebagMove()
    {
        if (!IsAutoTreasureHuntEnabled || !autoWaitingForSaddlebagMove)
        {
            return;
        }

        RefreshTreasureMapCounts();
        if (MainInventoryTreasureMapCount > 0)
        {
            autoWaitingForSaddlebagMove = false;
            autoSaddlebagContextMenuPending = false;
            autoSaddlebagMoveRequested = false;
            CloseSaddlebagWindow();
            AutoTreasureHuntStatus = "鞍囊地图已进入主背包，正在开始解读。";
            StartAutoTreasureHuntOnFrameworkThread();
            return;
        }

        if (DateTime.UtcNow > autoSaddlebagMoveDeadline)
        {
            autoWaitingForSaddlebagMove = false;
            autoSaddlebagContextMenuPending = false;
            autoSaddlebagMoveRequested = false;
            AutoTreasureHuntStatus = "通过右键菜单取出鞍囊地图超时，主背包仍未检测到地图。";
            return;
        }

        if (DateTime.UtcNow < autoSaddlebagActionAt || autoSaddlebagMoveRequested)
        {
            return;
        }

        if (autoSaddlebagContextMenuPending)
        {
            if (TrySelectFirstInventoryContextMenuOption(fromSaddlebag: true))
            {
                autoSaddlebagContextMenuPending = false;
                autoSaddlebagMoveRequested = true;
                AutoTreasureHuntStatus = "已选择鞍囊地图右键菜单第一项，等待地图进入主背包。";
            }

            return;
        }

        if (TryOpenSelectedMapInventoryContextMenu(fromSaddlebag: true))
        {
            autoSaddlebagContextMenuPending = true;
            autoSaddlebagActionAt = DateTime.UtcNow.AddMilliseconds(250);
            AutoTreasureHuntStatus = "已打开鞍囊地图右键菜单，准备选择第一项。";
            return;
        }

        autoSaddlebagActionAt = DateTime.UtcNow.AddSeconds(1);
        AutoTreasureHuntStatus = SaddlebagMoveStatus;
    }

    public void TestTeleportToOpenedMapAetheryte()
    {
        ResumeAfterEmergencyStop();
        if (IsRouletteMode)
        {
            TeleportTestStatus = "当前处于转盘地图，已禁止执行野外挖宝传送测试。";
            return;
        }

        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        autoMapCommandSent = false;
        autoMapFlagRetryQueued = false;
        decipherRetryCount = 0;
        decipherRetryQueued = false;
        digRetryCount = 0;
        digRetryQueued = false;
        mountAttemptCount = 0;
        RefreshTreasureMapCounts();
        if (!HasTaskTreasureMap)
        {
            TeleportTestStatus = $"任务道具背包中没有{SelectedTaskTreasureMapName}（ID {SelectedTaskTreasureMapItemId}）。";
            return;
        }

        TeleportTestStatus = "正在读取当前打开地图并匹配传送水晶...";
        _ = framework.RunOnFrameworkThread(TestTeleportToOpenedMapAetheryteOnFrameworkThread);
    }

    public void TestAnnounceCurrentFlag()
    {
        ResumeAfterEmergencyStop();
        if (!IsHeadLogicSelected)
        {
            TeleportTestStatus = "当前不是车头逻辑，无法测试播报红旗。";
            return;
        }

        _ = framework.RunOnFrameworkThread(TestAnnounceCurrentFlagOnFrameworkThread);
    }

    public void TestWheelMapLink()
    {
        if (!IsWheelLogicSelected)
        {
            WheelMapLinkStatus = "当前不是车轮逻辑，无法测试聊天地图链接。";
            return;
        }

        if (!IsAutoTreasureHuntEnabled || emergencyStopActive)
        {
            WheelMapLinkStatus = "自动挖宝未开启，车轮逻辑不会执行。";
            return;
        }

        _ = framework.RunOnFrameworkThread(() =>
        {
            if (wheelLastMapLink == null)
            {
                WheelMapLinkStatus = "还没有检测到聊天地图链接，请先发送一条带坐标红旗的聊天信息。";
                return;
            }

            wheelPendingMapLink = CloneMapLink(wheelLastMapLink);
            WheelMapLinkStatus = $"测试：准备打开 {wheelLastMapLink.PlaceName} {wheelLastMapLink.CoordinateString}。";
            TryProcessWheelMapLink();
        });
    }

    public void UseMountRouletteForTest()
    {
        ResumeAfterEmergencyStop();
        mountAfterTeleportPending = true;
        mountRetryQueued = false;
        mountAttemptCount = 0;
        TeleportTestStatus = "正在测试使用随机坐骑（通用技能 ID 9）...";
        _ = framework.RunOnFrameworkThread(UseMountRouletteAfterTeleportOnFrameworkThread);
    }

    public void TestDismount()
    {
        ResumeAfterEmergencyStop();
        TeleportTestStatus = "正在测试跳下坐骑（通用技能 ID 23）...";
        _ = framework.RunOnFrameworkThread(UseDismountForTestOnFrameworkThread);
    }

    public void TestFindAndOpenTreasureChest()
    {
        ResumeAfterEmergencyStop();
        treasureChestPending = true;
        treasureChestEntityId = 0;
        chestPositionSampleValid = false;
        confirmTreasureChestPending = false;
        confirmNextTreasureChestInteraction = true;
        waitForTreasureCombatStart = false;
        treasureCombatActive = false;
        treasureCombatLastCondition = false;
        treasureCombatEndCandidate = null;
        treasurePortalPending = false;
        treasurePortalEntityId = 0;
        treasurePortalCloseDelayStarted = false;
        confirmTreasurePortalPending = false;
        TeleportTestStatus = "正在测试寻找并开启宝箱...";
        _ = framework.RunOnFrameworkThread(TryHandleTreasureChest);
    }

    public void TestFindAndEnterTreasurePortal()
    {
        ResumeAfterEmergencyStop();
        treasurePortalPending = true;
        treasurePortalEntityId = 0;
        treasurePortalCloseDelayStarted = false;
        confirmTreasurePortalPending = false;
        TeleportTestStatus = "正在测试寻找、交互并确认传送魔纹...";
        _ = framework.RunOnFrameworkThread(TryHandleTreasurePortal);
    }

    public void TestExitRouletteInstance()
    {
        ResumeAfterEmergencyStop();
        rouletteExitTestPending = true;
        confirmRouletteExitPending = false;
        rouletteInteractedEntities.Clear();
        rouletteInteractedChestEntities.Clear();
        rouletteChestDisappearDeadline = default;
        ResetRouletteTarget();
        TeleportTestStatus = "正在测试寻找并退出副本...";
        _ = framework.RunOnFrameworkThread(TryHandleRouletteExitTest);
    }

    public void TestMapSupplementLogic()
    {
        ResumeAfterEmergencyStop();
        BeginMapSupplementLogic(resumeAutoHunt: false);
    }

    public void TestPurchaseSelectedMap()
    {
        ResumeAfterEmergencyStop();
        BeginMapSupplementLogic(resumeAutoHunt: false);
    }

    public unsafe void EmergencyStop()
    {
        emergencyStopActive = true;
        ResetMovementWatchdog();
        UnloadOptimizedInteraction();
        IsAutoTreasureHuntEnabled = false;
        automaticMapSupplementTriggered = false;
        automaticMapSupplementRunning = false;
        mapSupplementStage = MapSupplementStage.None;
        mapSupplementActionAt = default;
        mapSupplementDeadline = default;
        mapSupplementPurchaseStep = 0;
        mapSupplementResumeAutoHunt = false;

        selectMapPending = false;
        confirmMapPending = false;
        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        dismountAtFlagPending = false;
        navigationPositionSampleValid = false;
        navigationMovementObserved = false;
        autoWaitingForTaskTreasureMap = false;
        autoWaitingForSaddlebagMove = false;
        autoSaddlebagContextMenuPending = false;
        autoSaddlebagMoveRequested = false;
        autoMapCommandSent = false;
        autoMapFlagRetryQueued = false;
        saddlebagStoreTestPending = false;
        saddlebagStoreMoveRequested = false;
        saddlebagStoreContextMenuPending = false;
        saddlebagTakeTestPending = false;
        saddlebagTakeMoveRequested = false;
        saddlebagTakeContextMenuPending = false;
        marketSubmittedListingIds.Clear();
        ResetWheelTeleportAcceptance();
        ResetWheelMapLinkPending();

        treasureChestPending = false;
        treasureChestEntityId = 0;
        chestPositionSampleValid = false;
        confirmTreasureChestPending = false;
        waitForTreasureCombatStart = false;
        treasureCombatActive = false;
        treasureCombatLastCondition = false;
        treasureCombatEndCandidate = null;
        treasurePortalPending = false;
        treasurePortalEntityId = 0;
        treasurePortalCloseDelayStarted = false;
        confirmTreasurePortalPending = false;

        rouletteModeActive = false;
        rouletteWasInCombat = false;
        rouletteExitTestPending = false;
        confirmRouletteDreamPending = false;
        rouletteDreamConfirmEntityId = 0;
        rouletteInteractedDreamEntityId = 0;
        confirmRouletteExitPending = false;
        rouletteInteractedEntities.Clear();
        rouletteInteractedChestEntities.Clear();
        rouletteChestDisappearDeadline = default;
        ResetRouletteTarget();

        marketBoardAfterTeleportPending = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        ResetMarketPurchase();
        CloseMarketBoardWindows();
        CloseSaddlebagWindow();

        commandManager.ProcessCommand("/vnav stop");
        commandManager.ProcessCommand("/bmrai off");
        AutoTreasureHuntStatus = "已紧急停止全部自动逻辑。";
        TeleportTestStatus = AutoTreasureHuntStatus;
        TreasureMapUseStatus = AutoTreasureHuntStatus;
        SaddlebagMoveStatus = AutoTreasureHuntStatus;
    }

    private void ResumeAfterEmergencyStop()
    {
        emergencyStopActive = false;
    }

    private unsafe void StartMarketBoardTestOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive || !IsAutoTreasureHuntEnabled || IsRouletteMode)
        {
            return;
        }

        if (automaticMapSupplementRunning && IsRouletteMode)
        {
            FailMapSupplement("当前位于转盘地图 1059，不运行自动补图逻辑。");
            return;
        }

        if (clientState.TerritoryType == LimsaLowerDecksTerritoryId)
        {
            marketBoardAfterTeleportPending = false;
            MoveToMarketBoardOnFrameworkThread();
            return;
        }

        var limsaAetheryte = aetheryteList.FirstOrDefault(entry =>
            entry.TerritoryId == LimsaLowerDecksTerritoryId &&
            entry.AetheryteData.Value.IsAetheryte);
        if (limsaAetheryte == null)
        {
            FailMapSupplement("未找到已解锁的利姆萨·罗敏萨传送水晶。");
            return;
        }

        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            FailMapSupplement("无法获取游戏传送组件。");
            return;
        }

        if (!telepo->Teleport(limsaAetheryte.AetheryteId, limsaAetheryte.SubIndex))
        {
            FailMapSupplement("游戏拒绝了前往利姆萨·罗敏萨的传送请求。");
            return;
        }

        marketBoardAfterTeleportPending = true;
        TeleportTestStatus = "已请求传送到利姆萨·罗敏萨，等待切区后前往市场布告板。";
    }

    private void MoveToMarketBoardOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive || !IsAutoTreasureHuntEnabled || IsRouletteMode)
        {
            return;
        }

        const string command = "/vnav moveto -122.564 18.000 11.079";
        var commandAccepted = commandManager.ProcessCommand(command);
        marketBoardInteractionPending = true;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        TeleportTestStatus = commandAccepted
            ? $"已发送：{command}，到位后将与市场布告板交互。"
            : $"已发送但命令返回未确认：{command}，仍将监控市场布告板。";
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindNearestMarketBoard(
        System.Numerics.Vector3 playerPosition)
    {
        return objectTable
            .Where(gameObject =>
                gameObject.IsTargetable &&
                (gameObject.BaseId == MarketBoardBaseId ||
                 gameObject.Name.TextValue.Contains("市场布告板", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(gameObject =>
                System.Numerics.Vector3.DistanceSquared(gameObject.Position, playerPosition))
            .FirstOrDefault();
    }

    private void TryInteractWithMarketBoardAfterArrival()
    {
        if (!marketBoardInteractionPending)
        {
            return;
        }

        if (marketBoardInteractionAttempted)
        {
            unsafe
            {
                var searchAddon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
                if (searchAddon != null && searchAddon->IsReady)
                {
                    marketBoardInteractionPending = false;
                    marketBoardInteractionAttempted = false;
                    marketBoardPositionSampleValid = false;
                    marketPurchaseItemId = SelectedTreasureMapItemId;
                    marketPurchaseItemName = SelectedTreasureMapName;
                    marketPurchaseInitialMainCount = CountTreasureMaps(MainInventoryTypes, marketPurchaseItemId);
                    marketPurchaseAutomatic = automaticMapSupplementRunning;
                    marketPurchaseStage = MarketPurchaseStage.WaitingForSearchAddon;
                    marketSearchRunAt = DateTime.UtcNow + MarketPurchaseActionDelay;
                    marketPurchaseDeadline = DateTime.UtcNow.AddSeconds(15);
                    TeleportTestStatus = $"已确认市场布告板窗口打开，等待搜索{marketPurchaseItemName}。";
                    return;
                }
            }

            if (DateTime.UtcNow < marketBoardInteractionRetryAt)
            {
                return;
            }

            marketBoardInteractionAttempted = false;
            marketBoardPositionSampleValid = false;
            TeleportTestStatus = "市场窗口未打开，正在重新与布告板交互。";
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return;
        }

        var marketBoard = objectTable
            .Where(gameObject =>
                gameObject.IsTargetable &&
                (gameObject.BaseId == MarketBoardBaseId ||
                 gameObject.Name.TextValue.Contains("市场布告板", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(gameObject => System.Numerics.Vector3.DistanceSquared(gameObject.Position, localPlayer.Position))
            .FirstOrDefault();
        if (marketBoard == null)
        {
            TeleportTestStatus = $"正在前往目标，等待可交互的市场布告板（BaseId {MarketBoardBaseId}）进入对象表。";
            return;
        }

        var delta = localPlayer.Position - marketBoard.Position;
        const float interactionDistance = 3f;
        if (delta.LengthSquared() > interactionDistance * interactionDistance)
        {
            marketBoardPositionSampleValid = false;
            return;
        }

        const float movementTolerance = 0.05f;
        var position = localPlayer.Position;
        if (!marketBoardPositionSampleValid ||
            MathF.Abs(position.X - marketBoardSampleX) > movementTolerance ||
            MathF.Abs(position.Y - marketBoardSampleY) > movementTolerance ||
            MathF.Abs(position.Z - marketBoardSampleZ) > movementTolerance)
        {
            marketBoardPositionSampleValid = true;
            marketBoardSampleX = position.X;
            marketBoardSampleY = position.Y;
            marketBoardSampleZ = position.Z;
            marketBoardPositionStableSince = DateTime.UtcNow;
            TeleportTestStatus = "已贴近市场布告板，等待角色停止移动。";
            return;
        }

        if (DateTime.UtcNow - marketBoardPositionStableSince < TimeSpan.FromSeconds(1))
        {
            return;
        }

        targetManager.Target = marketBoard;
        var interactionResult = InteractWithGameObject(marketBoard);
        marketBoardInteractionAttempted = true;
        marketBoardInteractionRetryAt = DateTime.UtcNow.AddSeconds(2);
        TeleportTestStatus = $"卫月已调用市场布告板交互（返回值：{interactionResult}），等待确认市场窗口打开。";
    }

    private void ResetMarketPurchase()
    {
        marketPurchaseStage = MarketPurchaseStage.None;
        marketPurchaseDeadline = default;
        marketSearchRunAt = default;
        marketPurchaseItemId = 0;
        marketPurchaseItemName = string.Empty;
        marketPurchaseInitialMainCount = 0;
        marketPurchaseAutomatic = false;
        marketPurchaseSnapshotReady = false;
        marketPurchaseListingSnapshot = default;
        marketPurchaseSavedUnitPrice = 0;
    }

    private unsafe void TryHandleMarketPurchase()
    {
        if (marketPurchaseStage == MarketPurchaseStage.None)
        {
            return;
        }

        if (DateTime.UtcNow > marketPurchaseDeadline)
        {
            var failure = $"购买地图超时：未能完成{marketPurchaseItemName}的市场搜索或购买。";
            ResetMarketPurchase();
            if (automaticMapSupplementRunning)
            {
                workflowWatchdogRecoveryCount++;
                RecoverMapSupplementFromInventory();
                AutoTreasureHuntStatus = $"{failure} 已执行第 {workflowWatchdogRecoveryCount} 次防卡恢复。";
                TeleportTestStatus = AutoTreasureHuntStatus;
            }
            else
            {
                TeleportTestStatus = failure;
            }

            return;
        }

        if (marketPurchaseStage == MarketPurchaseStage.WaitingForDelivery)
        {
            var currentCount = CountTreasureMaps(MainInventoryTypes, marketPurchaseItemId);
            if (currentCount <= marketPurchaseInitialMainCount)
            {
                return;
            }

            var purchasedItemName = marketPurchaseItemName;
            var continueAutomaticRun = marketPurchaseAutomatic;
            TeleportTestStatus = $"已确认购买 1 张{purchasedItemName}并放入主背包。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            ResetMarketPurchase();
            RefreshTreasureMapCounts();
            if (continueAutomaticRun && automaticMapSupplementRunning)
            {
                CloseMarketBoardWindows();
                mapSupplementActionAt = DateTime.UtcNow.AddSeconds(1);
                mapSupplementDeadline = DateTime.UtcNow.AddSeconds(30);
                mapSupplementStage = mapSupplementPurchaseStep switch
                {
                    1 => MapSupplementStage.WaitingBeforeDecipher,
                    2 => MapSupplementStage.WaitingBeforeSaddlebagMove,
                    3 => MapSupplementStage.WaitingForThirdPurchase,
                    _ => MapSupplementStage.None,
                };
                return;
            }

            automaticMapSupplementRunning = false;
            if (continueAutomaticRun && IsAutoTreasureHuntEnabled)
            {
                _ = framework.RunOnTick(
                    StartAutoTreasureHuntOnFrameworkThread,
                    delay: TimeSpan.FromSeconds(1));
            }

            return;
        }

        if (marketPurchaseStage == MarketPurchaseStage.WaitingForPurchaseConfirmation)
        {
            var confirmAddon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
            if (confirmAddon == null || !confirmAddon->IsReady)
            {
                return;
            }

            if (!confirmAddon->FireCallbackInt(0))
            {
                TeleportTestStatus = "已找到市场购买确认窗口，但确认回调被拒绝，正在重试。";
                return;
            }

            marketSubmittedListingIds.Add(marketPurchaseListingSnapshot.ListingId);
            marketPurchaseStage = MarketPurchaseStage.WaitingForDelivery;
            marketPurchaseDeadline = DateTime.UtcNow.AddSeconds(15);
            TeleportTestStatus = $"已确认购买 1 张{marketPurchaseItemName}，最低单价 {marketPurchaseSavedUnitPrice:N0} 金币，等待地图进入主背包。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            return;
        }

        var agent = AgentItemSearch.Instance();
        if (agent == null)
        {
            return;
        }

        if (marketPurchaseStage == MarketPurchaseStage.WaitingForSearchAddon)
        {
            if (DateTime.UtcNow < marketSearchRunAt)
            {
                return;
            }

            var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            if (addon == null || !addon->IsReady || addon->ResultsList == null)
            {
                return;
            }

            if (addon->SearchTextInput == null)
            {
                TeleportTestStatus = "已打开道具搜索窗口，但无法获取搜索输入框。";
                return;
            }

            addon->SearchTextInput->SetText(marketPurchaseItemName);
            addon->SearchText.SetString(marketPurchaseItemName);
            addon->SearchText2.SetString(marketPurchaseItemName);
            addon->PartialMatch = false;
            marketPurchaseStage = MarketPurchaseStage.WaitingToRunSearch;
            marketSearchRunAt = DateTime.UtcNow + MarketPurchaseActionDelay;
            TeleportTestStatus = $"已将{marketPurchaseItemName}填入搜索框，等待游戏界面同步后执行搜索。";
            return;
        }

        if (marketPurchaseStage == MarketPurchaseStage.WaitingToRunSearch)
        {
            if (DateTime.UtcNow < marketSearchRunAt)
            {
                return;
            }

            var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            if (addon == null || !addon->IsReady)
            {
                return;
            }

            addon->RunSearch(ignoreFilters: false);
            marketPurchaseStage = MarketPurchaseStage.WaitingForSearchResults;
            marketSearchRunAt = DateTime.UtcNow + MarketPurchaseActionDelay;
            marketPurchaseDeadline = DateTime.UtcNow.AddSeconds(15);
            TeleportTestStatus = $"已输入并搜索{marketPurchaseItemName}，等待精确搜索结果。";
            return;
        }

        if (marketPurchaseStage == MarketPurchaseStage.WaitingForSearchResults)
        {
            if (DateTime.UtcNow < marketSearchRunAt)
            {
                return;
            }

            var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
            if (addon == null ||
                !addon->IsReady ||
                addon->ResultsList == null ||
                !agent->ListingPageLoaded ||
                agent->ListingPageItemCount == 0)
            {
                return;
            }

            var resultCount = Math.Min((int)agent->ListingPageItemCount, addon->ResultsList->GetItemCount());
            for (var index = 0; index < resultCount; index++)
            {
                var listedItem = agent->ListingPageItems[index];
                if (listedItem.ItemId != marketPurchaseItemId)
                {
                    continue;
                }

                addon->ResultsList->SelectItem(index, dispatchEvent: false);
                addon->ResultsList->DispatchItemEvent(index, AtkEventType.ListItemClick);
                marketPurchaseStage = MarketPurchaseStage.WaitingBeforePurchase;
                marketSearchRunAt = DateTime.UtcNow + MarketPurchaseActionDelay;
                marketPurchaseDeadline = DateTime.UtcNow.AddSeconds(20);
                TeleportTestStatus = $"已与搜索结果中的{marketPurchaseItemName}交互，交互满 1 秒后购买。";
                return;
            }

            return;
        }

        if (marketPurchaseStage != MarketPurchaseStage.WaitingBeforePurchase ||
            emergencyStopActive)
        {
            return;
        }

        var infoProxy = InfoProxyItemSearch.Instance();
        if (infoProxy == null)
        {
            return;
        }

        if (!marketPurchaseSnapshotReady)
        {
            if (infoProxy->WaitingForListings)
            {
                return;
            }

            if (infoProxy->ListingCount == 0)
            {
                return;
            }

            var purchase = infoProxy->Listings[0];
            if (purchase.ListingId == 0 ||
                purchase.ItemId != marketPurchaseItemId ||
                purchase.Quantity != 1 ||
                purchase.UnitPrice == 0)
            {
                FailMarketPurchase($"第一条报价不是数量为 1 的{marketPurchaseItemName}，已停止购买。");
                return;
            }

            if (marketSubmittedListingIds.Contains(purchase.ListingId))
            {
                TeleportTestStatus = $"卫月仍返回本轮已经提交过的{marketPurchaseItemName}报价，等待市场刷新下一条。";
                return;
            }

            marketPurchaseSnapshotReady = true;
            marketPurchaseListingSnapshot = purchase;
            marketPurchaseSavedUnitPrice = purchase.UnitPrice;
            TeleportTestStatus = DateTime.UtcNow < marketSearchRunAt
                ? $"已保存第一条{marketPurchaseItemName}报价，等待交互满 1 秒。"
                : $"已保存第一条{marketPurchaseItemName}报价，正在发送购买请求。";
        }

        if (DateTime.UtcNow < marketSearchRunAt)
        {
            return;
        }

        var resultAddon = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult");
        if (resultAddon == null ||
            !resultAddon->IsReady ||
            resultAddon->Results == null ||
            resultAddon->Results->GetItemCount() == 0)
        {
            TeleportTestStatus = "等待卫月价格列表第一行变为可交互状态。";
            return;
        }

        resultAddon->Results->SelectItem(0, dispatchEvent: false);
        resultAddon->Results->DispatchItemEvent(0, AtkEventType.ListItemClick);
        marketPurchaseStage = MarketPurchaseStage.WaitingForPurchaseConfirmation;
        marketPurchaseDeadline = DateTime.UtcNow.AddSeconds(10);
        TeleportTestStatus = $"已通过卫月接口点击第一条{marketPurchaseItemName}报价，等待购买确认窗口。";
    }

    private void FailMarketPurchase(string failure)
    {
        var supplementWasRunning = automaticMapSupplementRunning;
        ResetMarketPurchase();
        if (supplementWasRunning)
        {
            FailMapSupplement(failure);
        }
        else
        {
            TeleportTestStatus = failure;
        }
    }

    private void TryStartAutomaticMapSupplement()
    {
        if (!IsHeadLogicSelected ||
            !IsAutoTreasureHuntEnabled ||
            !IsAutoMapSupplementEnabled ||
            automaticMapSupplementRunning ||
            marketBoardAfterTeleportPending ||
            marketBoardInteractionPending ||
            marketPurchaseStage != MarketPurchaseStage.None ||
            IsRouletteMode ||
            condition[ConditionFlag.OccupiedInQuestEvent])
        {
            return;
        }

        if (HasAnySupportedTreasureMap())
        {
            automaticMapSupplementTriggered = false;
            return;
        }

        if (automaticMapSupplementTriggered || condition[ConditionFlag.InCombat])
        {
            return;
        }

        var hasChest = objectTable.Any(IsTreasureChest);
        var hasPortal = objectTable.Any(IsTreasurePortal);
        if (hasChest || hasPortal ||
            treasureChestPending ||
            treasurePortalPending ||
            waitForTreasureCombatStart ||
            treasureCombatActive ||
            confirmTreasureChestPending ||
            confirmTreasurePortalPending)
        {
            return;
        }

        automaticMapSupplementTriggered = true;
        BeginMapSupplementLogic(resumeAutoHunt: true);
    }

    private void BeginMapSupplementLogic(bool resumeAutoHunt)
    {
        marketSubmittedListingIds.Clear();
        RefreshTreasureMapCounts();
        if (MainInventoryTreasureMapCount > 0 || SaddlebagTreasureMapCount > 0 || TaskTreasureMapCount > 0)
        {
            TeleportTestStatus = "补图逻辑只会在主背包、陆行鸟鞍囊和任务道具均无当前地图时启动。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            automaticMapSupplementTriggered = false;
            return;
        }

        if (IsRouletteMode)
        {
            TeleportTestStatus = "当前位于转盘地图 1059，无法启动补图逻辑。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            automaticMapSupplementTriggered = false;
            return;
        }

        ResetMarketPurchase();
        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        dismountAtFlagPending = false;
        treasureChestPending = false;
        confirmTreasureChestPending = false;
        waitForTreasureCombatStart = false;
        treasureCombatActive = false;
        treasurePortalPending = false;
        confirmTreasurePortalPending = false;
        autoWaitingForTaskTreasureMap = false;
        autoWaitingForSaddlebagMove = false;
        selectMapPending = false;
        confirmMapPending = false;
        marketBoardAfterTeleportPending = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        automaticMapSupplementRunning = true;
        LoadOptimizedInteraction();
        mapSupplementStage = MapSupplementStage.TravelingToBoard;
        mapSupplementPurchaseStep = 1;
        mapSupplementResumeAutoHunt = resumeAutoHunt;
        mapSupplementActionAt = default;
        mapSupplementDeadline = DateTime.UtcNow.AddMinutes(2);
        TeleportTestStatus = "补图逻辑：正在前往利姆萨·罗敏萨市场布告板购买第 1 张地图。";
        AutoTreasureHuntStatus = TeleportTestStatus;
        _ = framework.RunOnFrameworkThread(StartMarketBoardTestOnFrameworkThread);
    }

    private void TryHandleMapSupplement()
    {
        if (!automaticMapSupplementRunning)
        {
            return;
        }

        if (marketPurchaseStage != MarketPurchaseStage.None)
        {
            TryHandleMarketPurchase();
            return;
        }

        TrySelectPendingMap();
        TryConfirmPendingMap();
        RefreshTreasureMapCounts();

        if (MainInventoryTreasureMapCount == 1 &&
            SaddlebagTreasureMapCount == 1 &&
            TaskTreasureMapCount == 1)
        {
            CompleteMapSupplement();
            return;
        }

        if (mapSupplementDeadline != default && DateTime.UtcNow > mapSupplementDeadline)
        {
            workflowWatchdogRecoveryCount++;
            var timedOutStage = mapSupplementStage;
            RecoverMapSupplementFromInventory();
            AutoTreasureHuntStatus = $"补图阶段 {timedOutStage} 超时，已执行第 {workflowWatchdogRecoveryCount} 次防卡恢复。";
            TeleportTestStatus = AutoTreasureHuntStatus;
            return;
        }

        switch (mapSupplementStage)
        {
            case MapSupplementStage.TravelingToBoard:
            case MapSupplementStage.WaitingForFirstPurchase:
                mapSupplementStage = MapSupplementStage.WaitingForFirstPurchase;
                TryInteractWithMarketBoardAfterArrival();
                break;

            case MapSupplementStage.WaitingBeforeDecipher:
                if (DateTime.UtcNow < mapSupplementActionAt)
                {
                    return;
                }

                if (MainInventoryTreasureMapCount != 1 || TaskTreasureMapCount != 0)
                {
                    FailMapSupplement("第 1 张地图到货后的背包状态不正确，无法开始解读。");
                    return;
                }

                CloseMarketBoardWindows();
                selectMapPending = false;
                confirmMapPending = false;
                autoWaitingForTaskTreasureMap = false;
                UseTreasureMapOnFrameworkThread();
                if (!selectMapPending)
                {
                    mapSupplementActionAt = DateTime.UtcNow.AddSeconds(1);
                    AutoTreasureHuntStatus = $"补图逻辑：暂时无法解读第 1 张地图，稍后重试。{TreasureMapUseStatus}";
                    TeleportTestStatus = AutoTreasureHuntStatus;
                    return;
                }

                mapSupplementStage = MapSupplementStage.WaitingForTaskMap;
                mapSupplementDeadline = DateTime.UtcNow.AddSeconds(30);
                AutoTreasureHuntStatus = "补图逻辑：正在解读第 1 张地图并等待任务道具出现。";
                TeleportTestStatus = AutoTreasureHuntStatus;
                break;

            case MapSupplementStage.WaitingForTaskMap:
                if (TaskTreasureMapCount < 1 || MainInventoryTreasureMapCount != 0)
                {
                    return;
                }

                mapSupplementPurchaseStep = 2;
                mapSupplementStage = MapSupplementStage.WaitingBeforeSecondInteraction;
                mapSupplementActionAt = DateTime.UtcNow.AddSeconds(1);
                mapSupplementDeadline = DateTime.UtcNow.AddSeconds(20);
                AutoTreasureHuntStatus = "补图逻辑：第 1 张地图已进入任务道具，准备第 2 次交互布告板。";
                TeleportTestStatus = AutoTreasureHuntStatus;
                break;

            case MapSupplementStage.WaitingBeforeSecondInteraction:
                if (DateTime.UtcNow < mapSupplementActionAt)
                {
                    return;
                }

                BeginSupplementBoardInteraction(MapSupplementStage.WaitingForSecondPurchase, 2);
                break;

            case MapSupplementStage.WaitingForSecondPurchase:
                TryInteractWithMarketBoardAfterArrival();
                break;

            case MapSupplementStage.WaitingBeforeSaddlebagMove:
                if (DateTime.UtcNow < mapSupplementActionAt)
                {
                    return;
                }

                if (MainInventoryTreasureMapCount != 1 || SaddlebagTreasureMapCount != 0)
                {
                    FailMapSupplement("第 2 张地图到货后的背包状态不正确，无法移入陆行鸟鞍囊。");
                    return;
                }

                if (!TryOpenSelectedMapInventoryContextMenu(fromSaddlebag: false))
                {
                    mapSupplementActionAt = DateTime.UtcNow.AddSeconds(1);
                    AutoTreasureHuntStatus = $"补图逻辑：暂时无法打开第 2 张地图的右键菜单，稍后重试。{SaddlebagMoveStatus}";
                    TeleportTestStatus = AutoTreasureHuntStatus;
                    return;
                }

                mapSupplementStage = MapSupplementStage.WaitingForSaddlebagContextMenu;
                mapSupplementActionAt = DateTime.UtcNow.AddMilliseconds(250);
                mapSupplementDeadline = DateTime.UtcNow.AddSeconds(10);
                AutoTreasureHuntStatus = "补图逻辑：已打开第 2 张地图的右键菜单，准备选择第一项。";
                TeleportTestStatus = AutoTreasureHuntStatus;
                break;

            case MapSupplementStage.WaitingForSaddlebagContextMenu:
                if (DateTime.UtcNow < mapSupplementActionAt)
                {
                    return;
                }

                if (!TrySelectFirstInventoryContextMenuOption(fromSaddlebag: false))
                {
                    return;
                }

                mapSupplementStage = MapSupplementStage.WaitingForSaddlebagMap;
                mapSupplementDeadline = DateTime.UtcNow.AddSeconds(15);
                AutoTreasureHuntStatus = "补图逻辑：已选择右键菜单第一项，等待第 2 张地图实际进入陆行鸟鞍囊。";
                TeleportTestStatus = AutoTreasureHuntStatus;
                break;

            case MapSupplementStage.WaitingForSaddlebagMap:
                if (SaddlebagTreasureMapCount < 1 || MainInventoryTreasureMapCount != 0)
                {
                    return;
                }

                CloseSaddlebagWindow();
                mapSupplementPurchaseStep = 3;
                mapSupplementStage = MapSupplementStage.WaitingBeforeThirdInteraction;
                mapSupplementActionAt = DateTime.UtcNow.AddSeconds(1);
                mapSupplementDeadline = DateTime.UtcNow.AddSeconds(20);
                AutoTreasureHuntStatus = "补图逻辑：第 2 张地图已进入陆行鸟鞍囊，准备第 3 次交互布告板。";
                TeleportTestStatus = AutoTreasureHuntStatus;
                break;

            case MapSupplementStage.WaitingBeforeThirdInteraction:
                if (DateTime.UtcNow < mapSupplementActionAt)
                {
                    return;
                }

                BeginSupplementBoardInteraction(MapSupplementStage.WaitingForThirdPurchase, 3);
                break;

            case MapSupplementStage.WaitingForThirdPurchase:
                TryInteractWithMarketBoardAfterArrival();
                break;
        }
    }

    private void BeginSupplementBoardInteraction(MapSupplementStage nextStage, int purchaseStep)
    {
        CloseMarketBoardWindows();
        marketBoardInteractionPending = true;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        mapSupplementPurchaseStep = purchaseStep;
        mapSupplementStage = nextStage;
        mapSupplementDeadline = DateTime.UtcNow.AddSeconds(30);
        AutoTreasureHuntStatus = $"补图逻辑：正在第 {purchaseStep} 次与市场布告板交互。";
        TeleportTestStatus = AutoTreasureHuntStatus;
    }

    private unsafe void CloseMarketBoardWindows()
    {
        var resultAddon = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult");
        if (resultAddon != null && resultAddon->IsReady)
        {
            resultAddon->Close(fireCallback: true);
        }

        var searchAddon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        if (searchAddon != null && searchAddon->IsReady)
        {
            searchAddon->Close(fireCallback: true);
        }
    }

    private void CompleteMapSupplement()
    {
        var resumeAutoHunt = mapSupplementResumeAutoHunt;
        CloseMarketBoardWindows();
        UnloadOptimizedInteraction();
        automaticMapSupplementRunning = false;
        automaticMapSupplementTriggered = false;
        mapSupplementStage = MapSupplementStage.None;
        mapSupplementPurchaseStep = 0;
        mapSupplementActionAt = default;
        mapSupplementDeadline = default;
        mapSupplementResumeAutoHunt = false;
        marketBoardAfterTeleportPending = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        ResetMarketPurchase();
        AutoTreasureHuntStatus = "补图逻辑完成：主背包 1 张、陆行鸟鞍囊 1 张、任务道具 1 张。";
        TeleportTestStatus = AutoTreasureHuntStatus;
        if (resumeAutoHunt && IsAutoTreasureHuntEnabled)
        {
            _ = framework.RunOnTick(
                StartAutoTreasureHuntOnFrameworkThread,
                delay: TimeSpan.FromSeconds(1));
        }
    }

    private void FailMapSupplement(string reason)
    {
        CloseMarketBoardWindows();
        UnloadOptimizedInteraction();
        automaticMapSupplementRunning = false;
        automaticMapSupplementTriggered = false;
        mapSupplementStage = MapSupplementStage.None;
        mapSupplementPurchaseStep = 0;
        mapSupplementActionAt = default;
        mapSupplementDeadline = default;
        mapSupplementResumeAutoHunt = false;
        marketBoardAfterTeleportPending = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        selectMapPending = false;
        confirmMapPending = false;
        ResetMarketPurchase();
        AutoTreasureHuntStatus = $"补图逻辑已停止：{reason}";
        TeleportTestStatus = AutoTreasureHuntStatus;
    }

    private void LoadOptimizedInteraction()
    {
        if (optimizedInteractionLoaded)
        {
            return;
        }

        commandManager.ProcessCommand("/pdr load OptimizedInteraction");
        optimizedInteractionLoaded = true;
    }

    private void UnloadOptimizedInteraction()
    {
        if (!optimizedInteractionLoaded)
        {
            return;
        }

        commandManager.ProcessCommand("/pdr unload OptimizedInteraction");
        optimizedInteractionLoaded = false;
    }

    private bool HasAnySupportedTreasureMap()
    {
        foreach (var option in TreasureMapOptions)
        {
            if (CountTreasureMaps(MainInventoryTypes, option.MapItemId) > 0 ||
                CountTreasureMaps(SaddlebagInventoryTypes, option.MapItemId) > 0 ||
                CountTreasureMaps(TaskItemInventoryTypes, option.TaskItemId) > 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryHandleWorkflowWatchdog()
    {
        var state = GetWorkflowWatchdogState(out var timeout);
        if (string.IsNullOrEmpty(state))
        {
            workflowWatchdogState = string.Empty;
            workflowWatchdogStateSince = default;
            return false;
        }

        if (!string.Equals(state, workflowWatchdogState, StringComparison.Ordinal))
        {
            workflowWatchdogState = state;
            workflowWatchdogStateSince = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow < workflowWatchdogCooldownUntil ||
            DateTime.UtcNow - workflowWatchdogStateSince < timeout)
        {
            return false;
        }

        workflowWatchdogRecoveryCount++;
        RecoverStuckWorkflow(state);
        workflowWatchdogState = string.Empty;
        workflowWatchdogStateSince = default;
        workflowWatchdogCooldownUntil = DateTime.UtcNow.AddSeconds(5);
        return true;
    }

    private bool TryHandleMovementWatchdog()
    {
        var state = GetMovementWatchdogState();
        var localPlayer = objectTable.LocalPlayer;
        if (string.IsNullOrEmpty(state) || localPlayer == null)
        {
            movementWatchdogState = string.Empty;
            movementWatchdogLastMovedAt = default;
            return false;
        }

        var position = localPlayer.Position;
        const float movementTolerance = 0.05f;
        if (!string.Equals(state, movementWatchdogState, StringComparison.Ordinal))
        {
            movementWatchdogState = state;
            movementWatchdogSampleX = position.X;
            movementWatchdogSampleY = position.Y;
            movementWatchdogSampleZ = position.Z;
            movementWatchdogLastMovedAt = DateTime.UtcNow;
            return false;
        }

        if (MathF.Abs(position.X - movementWatchdogSampleX) > movementTolerance ||
            MathF.Abs(position.Y - movementWatchdogSampleY) > movementTolerance ||
            MathF.Abs(position.Z - movementWatchdogSampleZ) > movementTolerance)
        {
            movementWatchdogSampleX = position.X;
            movementWatchdogSampleY = position.Y;
            movementWatchdogSampleZ = position.Z;
            movementWatchdogLastMovedAt = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow < movementWatchdogRetryCooldownUntil ||
            DateTime.UtcNow - movementWatchdogLastMovedAt < TimeSpan.FromSeconds(3))
        {
            return false;
        }

        RetryStuckMovement(state);
        movementWatchdogSampleX = position.X;
        movementWatchdogSampleY = position.Y;
        movementWatchdogSampleZ = position.Z;
        movementWatchdogLastMovedAt = DateTime.UtcNow;
        movementWatchdogRetryCooldownUntil = DateTime.UtcNow.AddSeconds(1);

        // 已针对移动阶段执行恢复，重新开始原有流程看门狗计时，避免同一帧重复恢复。
        workflowWatchdogState = string.Empty;
        workflowWatchdogStateSince = default;
        workflowWatchdogCooldownUntil = DateTime.UtcNow.AddSeconds(1);
        return true;
    }

    private void ResetMovementWatchdog()
    {
        movementWatchdogState = string.Empty;
        movementWatchdogLastMovedAt = default;
        movementWatchdogRetryCooldownUntil = default;
    }

    private string GetMovementWatchdogState()
    {
        if (condition[ConditionFlag.InCombat] ||
            condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51])
        {
            return string.Empty;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return string.Empty;
        }

        if (IsRouletteMode &&
            rouletteTargetEntityId != 0 &&
            !confirmRouletteDreamPending &&
            !confirmRouletteExitPending)
        {
            var target = objectTable.FirstOrDefault(gameObject => gameObject.EntityId == rouletteTargetEntityId);
            if (target != null &&
                (!string.Equals(rouletteTargetKind, "潜网巡梦", StringComparison.Ordinal) || target.IsTargetable) &&
                System.Numerics.Vector3.DistanceSquared(localPlayer.Position, target.Position) >
                RouletteInteractionDistance * RouletteInteractionDistance)
            {
                return $"roulette-move:{target.EntityId}";
            }

            return string.Empty;
        }

        if (marketBoardInteractionPending && !marketBoardInteractionAttempted)
        {
            var board = FindNearestMarketBoard(localPlayer.Position);
            if (board == null ||
                System.Numerics.Vector3.DistanceSquared(localPlayer.Position, board.Position) > 9f)
            {
                return "market-board-move";
            }
        }

        if (dismountAtFlagPending && condition[ConditionFlag.Mounted])
        {
            return "flyflag-move";
        }

        if (treasureChestPending && treasureChestEntityId != 0)
        {
            var chest = objectTable.FirstOrDefault(gameObject => gameObject.EntityId == treasureChestEntityId);
            var distance = confirmNextTreasureChestInteraction ? 3f : 1f;
            if (chest != null &&
                System.Numerics.Vector3.DistanceSquared(localPlayer.Position, chest.Position) > distance * distance)
            {
                return $"treasure-chest-move:{chest.EntityId}";
            }
        }

        if (treasurePortalPending && treasurePortalEntityId != 0)
        {
            var portal = objectTable.FirstOrDefault(gameObject => gameObject.EntityId == treasurePortalEntityId);
            if (portal != null &&
                System.Numerics.Vector3.DistanceSquared(localPlayer.Position, portal.Position) > 4f)
            {
                return $"treasure-portal-move:{portal.EntityId}";
            }
        }

        return string.Empty;
    }

    private void RetryStuckMovement(string state)
    {
        commandManager.ProcessCommand("/vnav stop");

        if (state.StartsWith("roulette-move:", StringComparison.Ordinal))
        {
            var target = objectTable.FirstOrDefault(gameObject => gameObject.EntityId == rouletteTargetEntityId);
            if (target != null)
            {
                targetManager.Target = target;
                roulettePositionSampleValid = false;
                commandManager.ProcessCommand("/vnav movetarget");
                AutoTreasureHuntStatus = $"转盘移动防卡：X/Y/Z 连续 3 秒未变化，正在重新前往{rouletteTargetKind}。";
            }

            return;
        }

        if (state == "market-board-move")
        {
            MoveToMarketBoardOnFrameworkThread();
            AutoTreasureHuntStatus = "补图移动防卡：X/Y/Z 连续 3 秒未变化，正在重新前往市场布告板。";
            TeleportTestStatus = AutoTreasureHuntStatus;
            return;
        }

        if (state == "flyflag-move")
        {
            navigationPositionSampleValid = false;
            navigationMovementObserved = false;
            commandManager.ProcessCommand("/vnav flyflag");
            AutoTreasureHuntStatus = "红旗移动防卡：X/Y/Z 连续 3 秒未变化，正在重新执行 /vnav flyflag。";
            TeleportTestStatus = AutoTreasureHuntStatus;
            return;
        }

        if (state.StartsWith("treasure-chest-move:", StringComparison.Ordinal))
        {
            var chest = objectTable.FirstOrDefault(gameObject => gameObject.EntityId == treasureChestEntityId);
            if (chest != null)
            {
                targetManager.Target = chest;
                chestPositionSampleValid = false;
                commandManager.ProcessCommand("/vnav movetarget");
                AutoTreasureHuntStatus = "宝箱移动防卡：X/Y/Z 连续 3 秒未变化，正在重新前往宝箱。";
                TeleportTestStatus = AutoTreasureHuntStatus;
            }

            return;
        }

        if (state.StartsWith("treasure-portal-move:", StringComparison.Ordinal))
        {
            var portal = objectTable.FirstOrDefault(gameObject => gameObject.EntityId == treasurePortalEntityId);
            if (portal != null)
            {
                targetManager.Target = portal;
                treasurePortalCloseDelayStarted = false;
                commandManager.ProcessCommand("/vnav movetarget");
                AutoTreasureHuntStatus = "魔纹移动防卡：X/Y/Z 连续 3 秒未变化，正在重新前往传送魔纹。";
                TeleportTestStatus = AutoTreasureHuntStatus;
            }
        }
    }

    private string GetWorkflowWatchdogState(out TimeSpan timeout)
    {
        timeout = TimeSpan.FromSeconds(45);

        if (IsRouletteMode)
        {
            if (condition[ConditionFlag.InCombat] ||
                (!confirmRouletteDreamPending &&
                 !confirmRouletteExitPending &&
                 rouletteTargetEntityId == 0))
            {
                return string.Empty;
            }

            timeout = confirmRouletteDreamPending || confirmRouletteExitPending
                ? TimeSpan.FromSeconds(15)
                : string.Equals(rouletteTargetKind, "潜网巡梦", StringComparison.Ordinal)
                    ? TimeSpan.FromSeconds(4)
                    : TimeSpan.FromSeconds(45);
            return $"roulette:{rouletteTargetKind}:{confirmRouletteDreamPending}:{confirmRouletteExitPending}";
        }

        if (saddlebagStoreTestPending)
        {
            timeout = TimeSpan.FromSeconds(25);
            return $"saddle-test-store:{saddlebagStoreContextMenuPending}:{saddlebagStoreMoveRequested}";
        }

        if (saddlebagTakeTestPending)
        {
            timeout = TimeSpan.FromSeconds(25);
            return $"saddle-test-take:{saddlebagTakeContextMenuPending}:{saddlebagTakeMoveRequested}";
        }

        if (autoWaitingForSaddlebagMove)
        {
            timeout = TimeSpan.FromSeconds(25);
            return $"saddle-auto-take:{autoSaddlebagContextMenuPending}:{autoSaddlebagMoveRequested}";
        }

        if (automaticMapSupplementRunning)
        {
            timeout = mapSupplementStage == MapSupplementStage.TravelingToBoard
                ? TimeSpan.FromMinutes(2)
                : mapSupplementStage == MapSupplementStage.WaitingForSecondPurchase
                    ? TimeSpan.FromSeconds(5)
                    : TimeSpan.FromSeconds(20);
            return $"supplement:{mapSupplementStage}:{marketPurchaseStage}:{marketBoardAfterTeleportPending}:{marketBoardInteractionPending}:{selectMapPending}:{confirmMapPending}";
        }

        if (marketPurchaseStage != MarketPurchaseStage.None ||
            marketBoardAfterTeleportPending ||
            marketBoardInteractionPending)
        {
            timeout = marketBoardAfterTeleportPending
                ? TimeSpan.FromMinutes(1)
                : TimeSpan.FromSeconds(35);
            return $"market:{marketPurchaseStage}:{marketBoardAfterTeleportPending}:{marketBoardInteractionPending}:{marketBoardInteractionAttempted}";
        }

        if (selectMapPending || confirmMapPending)
        {
            timeout = TimeSpan.FromSeconds(20);
            return $"decipher:{selectMapPending}:{confirmMapPending}";
        }

        if (mountAfterTeleportPending)
        {
            timeout = TimeSpan.FromSeconds(45);
            return "mount-after-teleport";
        }

        if (dismountAtFlagPending)
        {
            timeout = TimeSpan.FromSeconds(90);
            return $"dismount:{navigationMovementObserved}";
        }

        if (confirmTreasureChestPending)
        {
            timeout = TimeSpan.FromSeconds(15);
            return "chest-confirm";
        }

        if (waitForTreasureCombatStart && !condition[ConditionFlag.InCombat])
        {
            timeout = TimeSpan.FromSeconds(45);
            return "wait-combat-start";
        }

        if (treasureChestPending && !condition[ConditionFlag.InCombat])
        {
            timeout = TimeSpan.FromSeconds(90);
            return $"chest:{treasureChestEntityId}:{chestPositionSampleValid}";
        }

        if (confirmTreasurePortalPending)
        {
            timeout = TimeSpan.FromSeconds(15);
            return "portal-confirm";
        }

        if (treasurePortalPending && !condition[ConditionFlag.InCombat])
        {
            timeout = TimeSpan.FromSeconds(90);
            return $"portal:{treasurePortalEntityId}:{treasurePortalCloseDelayStarted}";
        }

        if (autoWaitingForTaskTreasureMap)
        {
            if (condition[ConditionFlag.OccupiedInQuestEvent])
            {
                // 上一张藏宝图任务尚未结算时允许持续等待，不按普通 60 秒超时恢复。
                return string.Empty;
            }

            timeout = TimeSpan.FromSeconds(60);
            return "wait-task-map";
        }

        return string.Empty;
    }

    private void RecoverStuckWorkflow(string state)
    {
        commandManager.ProcessCommand("/vnav stop");

        if (state.StartsWith("roulette:", StringComparison.Ordinal))
        {
            confirmRouletteDreamPending = false;
            rouletteDreamConfirmEntityId = 0;
            rouletteInteractedDreamEntityId = 0;
            confirmRouletteExitPending = false;
            rouletteExitDelayStarted = false;
            rouletteInteractedEntities.Clear();
            rouletteInteractedChestEntities.Clear();
            rouletteChestDisappearDeadline = default;
            ResetRouletteTarget();
            AutoTreasureHuntStatus = $"转盘防卡：第 {workflowWatchdogRecoveryCount} 次重置目标并重新寻路。";
            return;
        }

        if (state.StartsWith("supplement:", StringComparison.Ordinal))
        {
            RecoverMapSupplementFromInventory();
            return;
        }

        if (state.StartsWith("saddle-test-", StringComparison.Ordinal))
        {
            saddlebagStoreTestPending = false;
            saddlebagStoreContextMenuPending = false;
            saddlebagStoreMoveRequested = false;
            saddlebagTakeTestPending = false;
            saddlebagTakeContextMenuPending = false;
            saddlebagTakeMoveRequested = false;
            CloseWorkflowBlockingWindows();
            SaddlebagMoveStatus = $"鞍囊测试防卡：操作超时，已停止并清理窗口（第 {workflowWatchdogRecoveryCount} 次）。";
            return;
        }

        if (state.StartsWith("saddle-auto-take:", StringComparison.Ordinal))
        {
            autoSaddlebagContextMenuPending = false;
            autoSaddlebagMoveRequested = false;
            autoSaddlebagActionAt = DateTime.UtcNow.AddSeconds(2);
            autoSaddlebagMoveDeadline = DateTime.UtcNow.AddSeconds(25);
            CloseWorkflowBlockingWindows();
            AutoTreasureHuntStatus = $"鞍囊取图防卡：第 {workflowWatchdogRecoveryCount} 次重新打开鞍囊和右键菜单。";
            return;
        }

        if (state.StartsWith("market:", StringComparison.Ordinal))
        {
            ResetMarketPurchase();
            marketBoardAfterTeleportPending = false;
            marketBoardInteractionPending = false;
            marketBoardInteractionAttempted = false;
            CloseWorkflowBlockingWindows();
            TeleportTestStatus = $"市场流程防卡：已清理残留窗口并停止本次测试（第 {workflowWatchdogRecoveryCount} 次）。";
            return;
        }

        CloseWorkflowBlockingWindows();
        selectMapPending = false;
        confirmMapPending = false;
        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        dismountAtFlagPending = false;
        navigationPositionSampleValid = false;
        navigationMovementObserved = false;
        confirmTreasureChestPending = false;
        waitForTreasureCombatStart = false;
        treasureCombatActive = false;
        treasureChestPending = false;
        confirmTreasurePortalPending = false;
        treasurePortalPending = false;
        AutoTreasureHuntStatus = $"野外流程防卡：已清理卡住状态并重新检查地图（第 {workflowWatchdogRecoveryCount} 次）。";
        TeleportTestStatus = AutoTreasureHuntStatus;
        if (IsAutoTreasureHuntEnabled)
        {
            _ = framework.RunOnTick(
                StartAutoTreasureHuntOnFrameworkThread,
                delay: TimeSpan.FromSeconds(2));
        }
    }

    private void RecoverMapSupplementFromInventory()
    {
        CloseWorkflowBlockingWindows();
        ResetMarketPurchase();
        marketBoardAfterTeleportPending = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        selectMapPending = false;
        confirmMapPending = false;
        RefreshTreasureMapCounts();

        if (TaskTreasureMapCount > 0 &&
            MainInventoryTreasureMapCount > 0 &&
            SaddlebagTreasureMapCount > 0)
        {
            CompleteMapSupplement();
            return;
        }

        if (TaskTreasureMapCount == 0 && MainInventoryTreasureMapCount > 0)
        {
            mapSupplementStage = MapSupplementStage.WaitingBeforeDecipher;
            mapSupplementActionAt = DateTime.UtcNow.AddSeconds(2);
            mapSupplementDeadline = DateTime.UtcNow.AddSeconds(40);
            AutoTreasureHuntStatus = "补图防卡：检测到第 1 张地图，重新执行解读。";
        }
        else if (TaskTreasureMapCount > 0 &&
                 MainInventoryTreasureMapCount > 0 &&
                 SaddlebagTreasureMapCount == 0)
        {
            mapSupplementStage = MapSupplementStage.WaitingBeforeSaddlebagMove;
            mapSupplementActionAt = DateTime.UtcNow.AddSeconds(2);
            mapSupplementDeadline = DateTime.UtcNow.AddSeconds(40);
            AutoTreasureHuntStatus = "补图防卡：检测到第 2 张地图，重新执行右键存入鞍囊。";
        }
        else
        {
            mapSupplementPurchaseStep = TaskTreasureMapCount == 0
                ? 1
                : SaddlebagTreasureMapCount == 0
                    ? 2
                    : 3;
            mapSupplementStage = mapSupplementPurchaseStep switch
            {
                1 => MapSupplementStage.WaitingForFirstPurchase,
                2 => MapSupplementStage.WaitingForSecondPurchase,
                _ => MapSupplementStage.WaitingForThirdPurchase,
            };
            mapSupplementDeadline = DateTime.UtcNow.AddMinutes(1);
            AutoTreasureHuntStatus = $"补图防卡：按当前库存恢复第 {mapSupplementPurchaseStep} 次购买。";
            if (clientState.TerritoryType == LimsaLowerDecksTerritoryId)
            {
                MoveToMarketBoardOnFrameworkThread();
            }
            else
            {
                StartMarketBoardTestOnFrameworkThread();
            }
        }

        TeleportTestStatus = AutoTreasureHuntStatus;
    }

    private unsafe void CloseWorkflowBlockingWindows()
    {
        CloseMarketBoardWindows();
        CloseSaddlebagWindow();

        var contextMenu = gameGui.GetAddonByName<AddonContextMenu>("ContextMenu");
        if (contextMenu != null && contextMenu->IsReady && contextMenu->IsVisible)
        {
            contextMenu->Close(true);
        }

        var selectMapAddon = gameGui.GetAddonByName<AddonSelectIconString>("SelectIconString");
        if (selectMapAddon != null && selectMapAddon->IsReady && selectMapAddon->IsVisible)
        {
            selectMapAddon->Close(true);
        }

        var yesNoAddon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (yesNoAddon != null && yesNoAddon->IsReady && yesNoAddon->IsVisible)
        {
            yesNoAddon->Close(true);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsCredentialValidated || !IsAutoTreasureHuntEnabled)
        {
            return;
        }

        if (emergencyStopActive)
        {
            return;
        }

        if (IsWheelLogicSelected)
        {
            if (TryHandleMovementWatchdog())
            {
                return;
            }

            TryProcessWheelMapLink();
            TryHandleWheelLogic();
            TryHandleWheelPostTeleport();
            TryDismountAtFlag();
            return;
        }

        if (!IsHeadLogicSelected)
        {
            return;
        }

        // 地图 1059 必须优先于补图、鞍囊和野外流程接管。
        if (IsRouletteMode)
        {
            if (TryHandleMovementWatchdog())
            {
                return;
            }

            if (TryHandleWorkflowWatchdog())
            {
                return;
            }

            TryHandleRouletteMode();
            return;
        }

        if (rouletteModeActive)
        {
            ExitRouletteMode();
        }

        if (TryHandleMovementWatchdog())
        {
            return;
        }

        if (TryHandleWorkflowWatchdog())
        {
            return;
        }

        TryHandleSaddlebagStoreTest();
        if (saddlebagStoreTestPending)
        {
            return;
        }

        TryHandleSaddlebagTakeTest();
        if (saddlebagTakeTestPending)
        {
            return;
        }

        CheckAutomaticSaddlebagMove();
        if (autoWaitingForSaddlebagMove)
        {
            return;
        }

        if (automaticMapSupplementRunning)
        {
            TryHandleMapSupplement();
            return;
        }

        if (rouletteExitTestPending)
        {
            TryHandleRouletteExitTest();
            return;
        }

        TryStartAutomaticMapSupplement();
        TryInteractWithMarketBoardAfterArrival();
        TryHandleMarketPurchase();
        TrySelectPendingMap();
        TryConfirmPendingMap();
        TryConfirmTreasureChest();
        TryConfirmTreasurePortal();
        TryHandleTreasureCombat();
        TryAdvanceAutomaticTreasureHunt();
        TryDismountAtFlag();
        TryHandleTreasureChest();
        TryHandleTreasurePortal();
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (!IsWheelLogicSelected || !IsAutoTreasureHuntEnabled || emergencyStopActive)
        {
            return;
        }

        var mapLink = message.Message.Payloads
            .OfType<MapLinkPayload>()
            .FirstOrDefault();
        if (mapLink == null)
        {
            return;
        }

        wheelLastMapLink = CloneMapLink(mapLink);
        wheelPendingMapLink = CloneMapLink(mapLink);
        WheelMapLinkStatus = $"检测到聊天地图链接：{mapLink.PlaceName} {mapLink.CoordinateString}，准备设置红旗。";
        AutoTreasureHuntStatus = $"车轮：检测到聊天地图链接 {mapLink.PlaceName} {mapLink.CoordinateString}，正在获取红旗。";
    }

    private void TryProcessWheelMapLink()
    {
        if (wheelPendingMapLink == null)
        {
            return;
        }

        var mapLink = wheelPendingMapLink;
        wheelPendingMapLink = null;
        if (gameGui.OpenMapWithMapLink(mapLink))
        {
            WheelMapLinkStatus = $"已打开 {mapLink.PlaceName} {mapLink.CoordinateString}，等待游戏设置红旗。";
            AutoTreasureHuntStatus = "车轮：已根据聊天地图链接设置红旗。";
        }
        else
        {
            WheelMapLinkStatus = $"打开 {mapLink.PlaceName} {mapLink.CoordinateString} 失败，将等待下一条聊天地图链接。";
        }
    }

    private static MapLinkPayload CloneMapLink(MapLinkPayload mapLink)
    {
        return new MapLinkPayload(
            mapLink.TerritoryType.RowId,
            mapLink.Map.RowId,
            mapLink.RawX,
            mapLink.RawY);
    }

    private unsafe void TryHandleWheelLogic()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->PromptText == null)
        {
            ResetWheelTeleportAcceptance();
            return;
        }

        var prompt = addon->PromptText->NodeText.ToString();
        var telepo = Telepo.Instance();
        var isTeleportRequest = telepo != null && telepo->ActiveTeleportRequest;
        if (!isTeleportRequest &&
            !prompt.Contains("要返回到", StringComparison.Ordinal) &&
            !prompt.Contains("要传送到", StringComparison.Ordinal) &&
            !prompt.Contains("接受传送", StringComparison.Ordinal) &&
            !prompt.Contains("传送邀请", StringComparison.Ordinal))
        {
            ResetWheelTeleportAcceptance();
            return;
        }

        if (wheelTeleportAcceptSubmitted)
        {
            return;
        }

        if (wheelTeleportAcceptAt == default)
        {
            wheelTeleportAcceptAt = DateTime.UtcNow.AddSeconds(1);
            AutoTreasureHuntStatus = "车轮：检测到传送请求，等待一秒后接受。";
            return;
        }

        if (DateTime.UtcNow < wheelTeleportAcceptAt)
        {
            return;
        }

        if (addon->FireCallbackInt(0))
        {
            wheelTeleportAcceptSubmitted = true;
            wheelAwaitingMapChangeAndFlag = true;
            wheelTeleportSourceMapId = clientState.MapId;
            AutoTreasureHuntStatus = "车轮：已接受传送请求。";
        }
        else
        {
            wheelTeleportAcceptAt = DateTime.UtcNow.AddMilliseconds(250);
            AutoTreasureHuntStatus = "车轮：接受传送回调被拒绝，正在等待重试。";
        }
    }

    private void ResetWheelTeleportAcceptance()
    {
        wheelTeleportAcceptAt = default;
        wheelTeleportAcceptSubmitted = false;
    }

    private void ResetWheelMapLinkPending()
    {
        wheelPendingMapLink = null;
        wheelAwaitingMapChangeAndFlag = false;
        wheelTeleportSourceMapId = 0;
        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        mountAttemptCount = 0;
        dismountAtFlagPending = false;
        navigationPositionSampleValid = false;
        navigationMovementObserved = false;
    }

    private unsafe void TryHandleWheelPostTeleport()
    {
        if (!wheelAwaitingMapChangeAndFlag || emergencyStopActive)
        {
            return;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            AutoTreasureHuntStatus = "车轮：已接受传送，正在等待地图切换完成。";
            return;
        }

        var currentMapId = clientState.MapId;
        if (currentMapId == 0 || currentMapId == wheelTeleportSourceMapId)
        {
            AutoTreasureHuntStatus = "车轮：等待传送后的地图 ID 变更。";
            return;
        }

        var mapAgent = AgentMap.Instance();
        if (mapAgent == null || mapAgent->FlagMarkerCount == 0)
        {
            AutoTreasureHuntStatus = "车轮：地图 ID 已变更，等待红旗坐标加载。";
            return;
        }

        var flagMarker = mapAgent->FlagMapMarkers[0];
        if (flagMarker.TerritoryId == 0 ||
            flagMarker.MapId == 0 ||
            flagMarker.MapId != currentMapId)
        {
            AutoTreasureHuntStatus = "车轮：已检测到红旗，但红旗尚未匹配当前地图。";
            return;
        }

        wheelAwaitingMapChangeAndFlag = false;
        wheelTeleportSourceMapId = 0;
        mountAfterTeleportPending = true;
        mountRetryQueued = false;
        mountAttemptCount = 0;
        dismountAtFlagPending = false;
        navigationPositionSampleValid = false;
        navigationMovementObserved = false;
        AutoTreasureHuntStatus = $"车轮：已获取地图 {currentMapId} 的红旗坐标，准备使用随机坐骑前往。";
        UseMountRouletteAfterTeleportOnFrameworkThread();
    }

    private void OnMapIdChanged(uint mapId)
    {
        if (!IsHeadLogicSelected ||
            !IsCredentialValidated ||
            emergencyStopActive ||
            !IsAutoTreasureHuntEnabled)
        {
            return;
        }

        _ = framework.RunOnFrameworkThread(() =>
        {
            if (mapId == RouletteMapId || clientState.MapId == RouletteMapId)
            {
                EnterRouletteMode();
                return;
            }

            if (rouletteModeActive)
            {
                ExitRouletteMode();
            }
        });
    }

    private void OnZoneInit(ZoneInitEventArgs eventArgs)
    {
        if (!IsHeadLogicSelected || !IsCredentialValidated || !IsAutoTreasureHuntEnabled)
        {
            return;
        }

        if (emergencyStopActive)
        {
            return;
        }

        if (IsRouletteMode)
        {
            EnterRouletteMode();
            return;
        }

        if (marketBoardAfterTeleportPending && eventArgs.TerritoryType.RowId == LimsaLowerDecksTerritoryId)
        {
            marketBoardAfterTeleportPending = false;
            TeleportTestStatus = "已到达利姆萨·罗敏萨，两秒后前往市场布告板。";
            _ = framework.RunOnTick(
                MoveToMarketBoardOnFrameworkThread,
                delay: TimeSpan.FromSeconds(2));
        }

        if (automaticMapSupplementRunning)
        {
            return;
        }

        if (!mountAfterTeleportPending)
        {
            return;
        }

        TeleportTestStatus = $"卫月已收到区域初始化事件（区域 {eventArgs.TerritoryType.RowId}），等待切区完成后使用随机坐骑。";
        ScheduleMountRouletteRetry();
    }

    private unsafe void TrySelectPendingMap()
    {
        if (!selectMapPending)
        {
            return;
        }

        var addon = gameGui.GetAddonByName<AddonSelectIconString>("SelectIconString");
        if (addon == null || !addon->IsReady)
        {
            return;
        }

        TreasureMapUseStatus = "已检测到地图选择窗口，正在确认...";
        // 解读窗口只展示可用地图；测试按钮只在目标地图存在时开放，因此选择第一项。
        if (addon->FireCallbackInt(0))
        {
            selectMapPending = false;
            confirmMapPending = true;
            TreasureMapUseStatus = $"已选择{SelectedTreasureMapName}，等待确认。";
        }
        else
        {
            TreasureMapUseStatus = "已找到地图选择窗口，但确认回调被拒绝。";
        }
    }

    private unsafe void TryConfirmPendingMap()
    {
        if (!confirmMapPending)
        {
            return;
        }

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady)
        {
            return;
        }

        TreasureMapUseStatus = "已检测到地图确认窗口，正在确定...";
        if (addon->FireCallbackInt(0))
        {
            confirmMapPending = false;
            TreasureMapUseStatus = "已确认解读地图，等待读条。";
        }
        else
        {
            TreasureMapUseStatus = "已找到地图确认窗口，但确定回调被拒绝。";
        }
    }

    private unsafe void TryConfirmTreasureChest()
    {
        if (!confirmTreasureChestPending)
        {
            return;
        }

        if (DateTime.UtcNow > treasureChestConfirmDeadline)
        {
            confirmTreasureChestPending = false;
            treasureChestPending = true;
            treasureChestEntityId = 0;
            chestPositionSampleValid = false;
            TeleportTestStatus = "等待宝箱确认窗口超时，已重新寻找并交互宝箱。";
            return;
        }

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady)
        {
            return;
        }

        if (addon->FireCallbackInt(0))
        {
            confirmTreasureChestPending = false;
            waitForTreasureCombatStart = true;
            TeleportTestStatus = "已在宝箱确认窗口选择“是”，等待进入战斗。";
        }
        else
        {
            TeleportTestStatus = "已找到宝箱确认窗口，但确定回调被拒绝。";
        }
    }

    private unsafe void TryConfirmTreasurePortal()
    {
        if (!confirmTreasurePortalPending)
        {
            return;
        }

        if (DateTime.UtcNow > treasurePortalConfirmDeadline)
        {
            confirmTreasurePortalPending = false;
            treasurePortalPending = true;
            treasurePortalEntityId = 0;
            treasurePortalCloseDelayStarted = false;
            TeleportTestStatus = "等待传送魔纹确认窗口超时，已重新寻找并交互魔纹。";
            return;
        }

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady)
        {
            return;
        }

        if (addon->FireCallbackInt(0))
        {
            confirmTreasurePortalPending = false;
            TeleportTestStatus = "已确认进入传送魔纹。";
        }
        else
        {
            TeleportTestStatus = "已找到传送魔纹确认窗口，但确定回调被拒绝。";
        }
    }

    private void EnterRouletteMode()
    {
        if (rouletteModeActive)
        {
            return;
        }

        commandManager.ProcessCommand("/vnav stop");
        UnloadOptimizedInteraction();
        CloseMarketBoardWindows();
        CloseSaddlebagWindow();
        autoWaitingForTaskTreasureMap = false;
        autoWaitingForSaddlebagMove = false;
        autoSaddlebagContextMenuPending = false;
        autoSaddlebagMoveRequested = false;
        saddlebagStoreTestPending = false;
        saddlebagStoreContextMenuPending = false;
        saddlebagTakeTestPending = false;
        saddlebagTakeContextMenuPending = false;
        selectMapPending = false;
        confirmMapPending = false;
        confirmTreasureChestPending = false;
        confirmTreasurePortalPending = false;
        waitForTreasureCombatStart = false;
        treasureCombatActive = false;
        mapSupplementStage = MapSupplementStage.None;
        mapSupplementDeadline = default;
        mapSupplementPurchaseStep = 0;
        automaticMapSupplementTriggered = false;
        rouletteModeActive = true;
        rouletteWasInCombat = condition[ConditionFlag.InCombat];
        rouletteTargetEntityId = 0;
        rouletteTargetKind = string.Empty;
        roulettePositionSampleValid = false;
        rouletteExitDelayStarted = false;
        confirmRouletteDreamPending = false;
        rouletteDreamConfirmEntityId = 0;
        rouletteInteractedDreamEntityId = 0;
        confirmRouletteExitPending = false;
        rouletteInteractedEntities.Clear();
        rouletteInteractedChestEntities.Clear();
        rouletteChestDisappearDeadline = default;
        mountAfterTeleportPending = false;
        dismountAtFlagPending = false;
        treasureChestPending = false;
        treasurePortalPending = false;
        marketBoardAfterTeleportPending = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        automaticMapSupplementRunning = false;
        ResetMarketPurchase();
        commandManager.ProcessCommand("/bmrai on");
        AutoTreasureHuntStatus = "转盘：卫月检测到当前地图 ID 1059，并执行 /bmrai on。";
    }

    private void ExitRouletteMode()
    {
        var shouldResumeAutoHunt =
            rouletteModeActive &&
            IsAutoTreasureHuntEnabled &&
            !emergencyStopActive &&
            !IsRouletteMode;

        rouletteModeActive = false;
        rouletteTargetEntityId = 0;
        rouletteTargetKind = string.Empty;
        roulettePositionSampleValid = false;
        rouletteExitDelayStarted = false;
        confirmRouletteDreamPending = false;
        rouletteDreamConfirmEntityId = 0;
        rouletteInteractedDreamEntityId = 0;
        confirmRouletteExitPending = false;
        rouletteInteractedEntities.Clear();
        rouletteInteractedChestEntities.Clear();
        rouletteChestDisappearDeadline = default;

        if (shouldResumeAutoHunt)
        {
            AutoTreasureHuntStatus = "转盘流程已结束，正在重新检查任务道具、主背包和陆行鸟鞍囊。";
            _ = framework.RunOnTick(
                () =>
                {
                    if (IsAutoTreasureHuntEnabled && !IsRouletteMode && !emergencyStopActive)
                    {
                        StartAutoTreasureHuntOnFrameworkThread();
                    }
                },
                delay: TimeSpan.FromSeconds(2));
        }
    }

    private void TryHandleRouletteMode()
    {
        EnterRouletteMode();
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat)
        {
            // 潜网巡梦交互后会消失并进入战斗。进入战斗即代表本轮交互完成，
            // 释放实体记录，避免下一轮复用同一 EntityId 时被误判为已经交互。
            if (rouletteInteractedDreamEntityId != 0)
            {
                rouletteInteractedEntities.Remove(rouletteInteractedDreamEntityId);
                rouletteInteractedDreamEntityId = 0;
            }

            if (!rouletteWasInCombat)
            {
                commandManager.ProcessCommand("/bmrai on");
            }

            rouletteWasInCombat = true;
            AutoTreasureHuntStatus = "转盘：战斗中。";
            ResetRouletteTarget();
            return;
        }

        // 宝箱、潜网巡梦和它们的确认窗口都只允许在脱离战斗后处理。
        TryConfirmRouletteDream();
        if (confirmRouletteDreamPending)
        {
            return;
        }

        TryConfirmRouletteExit();
        if (confirmRouletteExitPending)
        {
            return;
        }

        if (rouletteWasInCombat)
        {
            rouletteWasInCombat = false;
            commandManager.ProcessCommand("/bmrai off");

            // 每场战斗结束后都会生成新一批宝箱，游戏可能复用上一批的 EntityId。
            // 释放上一轮宝箱记录，确保本轮所有宝箱都会重新逐个处理。
            foreach (var chestEntityId in rouletteInteractedChestEntities)
            {
                rouletteInteractedEntities.Remove(chestEntityId);
            }

            rouletteInteractedChestEntities.Clear();
            rouletteChestDisappearDeadline = default;
            ResetRouletteTarget();
            AutoTreasureHuntStatus = "转盘：已脱离战斗并执行 /bmrai off，正在检查本轮全部宝箱。";
        }

        var rouletteChests = objectTable.Where(IsTreasureChest).ToArray();
        var chest = rouletteChests.FirstOrDefault(gameObject =>
            !rouletteInteractedChestEntities.Contains(gameObject.EntityId));
        if (chest != null)
        {
            HandleRouletteTarget(chest, "宝箱", TimeSpan.FromSeconds(1), confirmExit: false);
            return;
        }

        // 已交互的宝箱可能需要短暂时间才从对象表消失。在它们仍存在时继续等待，
        // 不允许提前处理潜网巡梦或退出点。
        if (rouletteChests.Length > 0)
        {
            if (rouletteChestDisappearDeadline != default &&
                DateTime.UtcNow >= rouletteChestDisappearDeadline)
            {
                foreach (var lingeringChest in rouletteChests)
                {
                    rouletteInteractedChestEntities.Remove(lingeringChest.EntityId);
                    rouletteInteractedEntities.Remove(lingeringChest.EntityId);
                }

                rouletteChestDisappearDeadline = default;
                ResetRouletteTarget();
                AutoTreasureHuntStatus = "转盘防卡：宝箱交互后 4 秒仍在场，正在重新逐个交互剩余宝箱。";
                return;
            }

            ResetRouletteTarget();
            AutoTreasureHuntStatus = $"转盘：本轮检测到 {rouletteChests.Length} 个宝箱，已全部执行交互，等待宝箱消失或刷新下一阶段。";
            return;
        }

        rouletteChestDisappearDeadline = default;

        var dream = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == RouletteDreamBaseId &&
            gameObject.IsTargetable &&
            !rouletteInteractedEntities.Contains(gameObject.EntityId));
        if (dream != null)
        {
            HandleRouletteTarget(dream, "潜网巡梦", TimeSpan.FromSeconds(1), confirmExit: false);
            return;
        }

        // 已交互过的潜网巡梦仍可能继续留在对象表中；只要它还在场，就禁止退出。
        if (objectTable.Any(gameObject => gameObject.BaseId == RouletteDreamBaseId))
        {
            if (rouletteTargetKind == "退出点")
            {
                commandManager.ProcessCommand("/vnav stop");
            }

            ResetRouletteTarget();
            AutoTreasureHuntStatus = $"转盘：场上仍存在潜网巡梦（BaseId {RouletteDreamBaseId}），禁止执行退出逻辑。";
            return;
        }

        var exit = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == RouletteExitBaseId &&
            !rouletteInteractedEntities.Contains(gameObject.EntityId));
        if (exit != null)
        {
            HandleRouletteTarget(exit, "退出点", TimeSpan.FromSeconds(2), confirmExit: true);
            return;
        }

        ResetRouletteTarget();
        AutoTreasureHuntStatus = "转盘：等待潜网巡梦、宝箱或退出点出现。";
    }

    private void TryHandleRouletteExitTest()
    {
        if (!IsHeadLogicSelected)
        {
            return;
        }

        TryConfirmRouletteExit();
        if (confirmRouletteExitPending || !rouletteExitTestPending)
        {
            return;
        }

        if (objectTable.Any(gameObject => gameObject.BaseId == RouletteDreamBaseId))
        {
            commandManager.ProcessCommand("/vnav stop");
            ResetRouletteTarget();
            TeleportTestStatus = $"场上仍存在潜网巡梦（BaseId {RouletteDreamBaseId}），测试退出已被阻止。";
            return;
        }

        var exit = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == RouletteExitBaseId &&
            !rouletteInteractedEntities.Contains(gameObject.EntityId));
        if (exit == null)
        {
            TeleportTestStatus = $"当前场景没有检测到退出点（BaseId {RouletteExitBaseId}）。";
            return;
        }

        HandleRouletteTarget(exit, "退出点", TimeSpan.FromSeconds(2), confirmExit: true);
        TeleportTestStatus = AutoTreasureHuntStatus;
    }

    private void HandleRouletteTarget(
        Dalamud.Game.ClientState.Objects.Types.IGameObject target,
        string targetKind,
        TimeSpan stableDelay,
        bool confirmExit)
    {
        if (targetKind == "潜网巡梦" && !target.IsTargetable)
        {
            commandManager.ProcessCommand("/vnav stop");
            ResetRouletteTarget();
            AutoTreasureHuntStatus = "转盘：潜网巡梦当前不可选中，等待其变为可选中后再寻路。";
            return;
        }

        if (rouletteTargetEntityId != target.EntityId || rouletteTargetKind != targetKind)
        {
            rouletteTargetEntityId = target.EntityId;
            rouletteTargetKind = targetKind;
            roulettePositionSampleValid = false;
            rouletteExitDelayStarted = false;
            targetManager.Target = target;
            commandManager.ProcessCommand("/vnav movetarget");
            AutoTreasureHuntStatus = $"转盘：正在前往{targetKind}。";
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return;
        }

        var delta = localPlayer.Position - target.Position;
        if (delta.LengthSquared() > RouletteInteractionDistance * RouletteInteractionDistance)
        {
            roulettePositionSampleValid = false;
            rouletteExitDelayStarted = false;
            return;
        }

        const float movementTolerance = 0.05f;
        var position = localPlayer.Position;
        if (!roulettePositionSampleValid ||
            MathF.Abs(position.X - rouletteSampleX) > movementTolerance ||
            MathF.Abs(position.Y - rouletteSampleY) > movementTolerance ||
            MathF.Abs(position.Z - rouletteSampleZ) > movementTolerance)
        {
            roulettePositionSampleValid = true;
            rouletteSampleX = position.X;
            rouletteSampleY = position.Y;
            rouletteSampleZ = position.Z;
            roulettePositionStableSince = DateTime.UtcNow;
            rouletteExitDelayStarted = false;
            AutoTreasureHuntStatus = $"转盘：已贴近{targetKind}，等待角色停止移动。";
            return;
        }

        if (DateTime.UtcNow - roulettePositionStableSince < TimeSpan.FromSeconds(1))
        {
            return;
        }

        if (stableDelay > TimeSpan.FromSeconds(1))
        {
            if (!rouletteExitDelayStarted)
            {
                rouletteExitDelayStarted = true;
                rouletteExitInteractAt = DateTime.UtcNow.Add(stableDelay - TimeSpan.FromSeconds(1));
                AutoTreasureHuntStatus = "转盘：场上无宝箱，贴近退出点后两秒退出。";
                return;
            }

            if (DateTime.UtcNow < rouletteExitInteractAt)
            {
                return;
            }
        }

        targetManager.Target = target;
        InteractWithGameObject(target);
        if (targetKind == "潜网巡梦")
        {
            confirmRouletteDreamPending = true;
            rouletteDreamConfirmEntityId = target.EntityId;
            rouletteDreamConfirmDeadline = DateTime.UtcNow.AddSeconds(10);
            AutoTreasureHuntStatus = "转盘：已与潜网巡梦交互，等待确认窗口。";
        }
        else if (confirmExit)
        {
            rouletteInteractedEntities.Add(target.EntityId);
            confirmRouletteExitPending = true;
            rouletteExitConfirmDeadline = DateTime.UtcNow.AddSeconds(10);
            AutoTreasureHuntStatus = "转盘：已与退出点交互，等待确认窗口。";
        }
        else
        {
            rouletteInteractedEntities.Add(target.EntityId);
            if (IsTreasureChest(target))
            {
                rouletteInteractedChestEntities.Add(target.EntityId);
                rouletteChestDisappearDeadline = DateTime.UtcNow.AddSeconds(4);
            }

            AutoTreasureHuntStatus = $"转盘：已与{targetKind}交互。";
        }

        ResetRouletteTarget();
    }

    private unsafe void TryConfirmRouletteDream()
    {
        if (!confirmRouletteDreamPending)
        {
            return;
        }

        if (DateTime.UtcNow > rouletteDreamConfirmDeadline)
        {
            confirmRouletteDreamPending = false;
            if (rouletteDreamConfirmEntityId != 0)
            {
                rouletteInteractedEntities.Remove(rouletteDreamConfirmEntityId);
            }

            rouletteDreamConfirmEntityId = 0;
            ResetRouletteTarget();
            AutoTreasureHuntStatus = "转盘：潜网巡梦确认窗口超时，已准备重新交互。";
            return;
        }

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady)
        {
            return;
        }

        if (addon->FireCallbackInt(0))
        {
            confirmRouletteDreamPending = false;
            if (rouletteDreamConfirmEntityId != 0)
            {
                rouletteInteractedEntities.Add(rouletteDreamConfirmEntityId);
                rouletteInteractedDreamEntityId = rouletteDreamConfirmEntityId;
            }

            rouletteDreamConfirmEntityId = 0;
            AutoTreasureHuntStatus = "转盘：已确认启动潜网巡梦。";
        }
        else
        {
            AutoTreasureHuntStatus = "转盘：潜网巡梦确认回调被拒绝，正在等待重试。";
        }
    }

    private unsafe void TryConfirmRouletteExit()
    {
        if (!confirmRouletteExitPending)
        {
            return;
        }

        if (DateTime.UtcNow > rouletteExitConfirmDeadline)
        {
            confirmRouletteExitPending = false;
            rouletteExitTestPending = false;
            rouletteInteractedEntities.Clear();
            ResetRouletteTarget();
            AutoTreasureHuntStatus = "转盘防卡：等待退出确认窗口超时，已重新寻找退出点。";
            TeleportTestStatus = AutoTreasureHuntStatus;
            return;
        }

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady)
        {
            return;
        }

        if (addon->FireCallbackInt(0))
        {
            confirmRouletteExitPending = false;
            rouletteExitTestPending = false;
            AutoTreasureHuntStatus = "转盘：已确认退出副本。";
            TeleportTestStatus = AutoTreasureHuntStatus;
        }
    }

    private void ResetRouletteTarget()
    {
        rouletteTargetEntityId = 0;
        rouletteTargetKind = string.Empty;
        roulettePositionSampleValid = false;
        rouletteExitDelayStarted = false;
    }

    private unsafe ulong InteractWithGameObject(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
    {
        var targetSystem = TargetSystem.Instance();
        var nativeObject = (GameObject*)gameObject.Address;
        if (targetSystem == null || nativeObject == null)
        {
            return 0;
        }

        targetSystem->SetHardTarget(nativeObject);
        return targetSystem->InteractWithObject(nativeObject, checkLineOfSight: true);
    }

    public void UseTreasureMapForTest()
    {
        ResumeAfterEmergencyStop();
        if (IsRouletteMode)
        {
            TreasureMapUseStatus = "当前处于转盘地图，已禁止执行野外挖宝解读。";
            return;
        }

        RefreshTreasureMapCounts();
        if (!IsAutoTreasureHuntEnabled)
        {
            TreasureMapUseStatus = "请先开启自动挖宝。";
            return;
        }

        if (!HasTreasureMap)
        {
            TreasureMapUseStatus = $"背包中没有{SelectedTreasureMapName}。";
            return;
        }

        if (!CanUseTreasureMap)
        {
            TreasureMapUseStatus = "地图仅在陆行鸟鞍囊中，请先移到主背包。";
            return;
        }

        TreasureMapUseStatus = "正在请求游戏使用地图...";
        selectMapPending = false;
        confirmMapPending = false;
        _ = framework.RunOnFrameworkThread(UseTreasureMapOnFrameworkThread);
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs eventArgs)
    {
        if (eventArgs.AffectedInternalNames.Contains(
                VnavmeshInternalName,
                StringComparer.OrdinalIgnoreCase))
        {
            UpdateVnavmeshRunning();
        }

        if (eventArgs.AffectedInternalNames.Contains(
                GlobetrotterInternalName,
                StringComparer.OrdinalIgnoreCase))
        {
            UpdateGlobetrotterRunning();
        }

        if (eventArgs.AffectedInternalNames.Contains(
                BossModRebornInternalName,
                StringComparer.OrdinalIgnoreCase))
        {
            UpdateBossModRebornRunning();
        }
    }

    private void UpdateVnavmeshRunning()
    {
        var isRunning = CheckVnavmeshRunning();
        if (isRunning == IsVnavmeshRunning)
        {
            return;
        }

        IsVnavmeshRunning = isRunning;
        VnavmeshRunningChanged?.Invoke(isRunning);
    }

    private void UpdateGlobetrotterRunning()
    {
        var isRunning = CheckGlobetrotterRunning();
        if (isRunning == IsGlobetrotterRunning)
        {
            return;
        }

        IsGlobetrotterRunning = isRunning;
        GlobetrotterRunningChanged?.Invoke(isRunning);
    }

    private void UpdateBossModRebornRunning()
    {
        IsBossModRebornRunning = CheckBossModRebornRunning();
    }

    private bool CheckVnavmeshRunning()
    {
        return IsPluginRunning(VnavmeshInternalName);
    }

    private bool CheckGlobetrotterRunning()
    {
        return IsPluginRunning(GlobetrotterInternalName);
    }

    private bool CheckBossModRebornRunning()
    {
        return IsPluginRunning(BossModRebornInternalName);
    }

    private bool IsPluginRunning(string internalName)
    {
        return pluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded &&
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (!IsHeadLogicSelected || !IsAutoTreasureHuntEnabled || IsRouletteMode)
        {
            return;
        }

        RefreshTreasureMapCounts();
        if (automaticMapSupplementRunning)
        {
            return;
        }

        TryAdvanceAutomaticTreasureHunt();
    }

    private void TryAdvanceAutomaticTreasureHunt()
    {
        if (!IsHeadLogicSelected ||
            !IsAutoTreasureHuntEnabled ||
            IsRouletteMode ||
            !autoWaitingForTaskTreasureMap)
        {
            return;
        }

        RefreshTreasureMapCounts();
        if (HasTaskTreasureMap)
        {
            autoWaitingForTaskTreasureMap = false;
            AutoTreasureHuntStatus = "解读完成并检测到任务道具地图，一秒后开始传送流程。";
            _ = framework.RunOnTick(
                StartAutoTreasureHuntOnFrameworkThread,
                delay: TimeSpan.FromSeconds(1));
        }
    }

    private void RefreshTreasureMapCounts()
    {
        MainInventoryTreasureMapCount = CountTreasureMaps(MainInventoryTypes);
        SaddlebagTreasureMapCount = CountTreasureMaps(SaddlebagInventoryTypes);
        TaskTreasureMapCount = CountTreasureMaps(TaskItemInventoryTypes, SelectedTaskTreasureMapItemId);
    }

    private int CountTreasureMaps(IEnumerable<GameInventoryType> inventoryTypes, uint? itemId = null)
    {
        var targetItemId = itemId ?? SelectedTreasureMapItemId;
        var count = 0;
        foreach (var inventoryType in inventoryTypes)
        {
            foreach (ref readonly var item in gameInventory.GetInventoryItems(inventoryType))
            {
                if (item.BaseItemId == targetItemId)
                {
                    count += item.Quantity;
                }
            }
        }

        return count;
    }

    private unsafe bool TryOpenSelectedMapInventoryContextMenu(bool fromSaddlebag)
    {
        if (emergencyStopActive)
        {
            SaddlebagMoveStatus = "已紧急停止鞍囊操作。";
            return false;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            SaddlebagMoveStatus = "无法获取游戏库存管理器。";
            return false;
        }

        if (!EnsureSaddlebagLoaded(inventoryManager))
        {
            return false;
        }

        var sourceInventoryTypes = fromSaddlebag
            ? SaddlebagClientInventoryTypes
            : MainClientInventoryTypes;
        InventoryType sourceType = default;
        var sourceSlot = -1;
        foreach (var inventoryType in sourceInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->GetBaseItemId() == SelectedTreasureMapItemId)
                {
                    sourceType = inventoryType;
                    sourceSlot = slot;
                    break;
                }
            }

            if (sourceSlot >= 0)
            {
                break;
            }
        }

        if (sourceSlot < 0)
        {
            SaddlebagMoveStatus = fromSaddlebag
                ? $"陆行鸟鞍囊中没有{SelectedTreasureMapName}。"
                : $"主背包中没有{SelectedTreasureMapName}。";
            return false;
        }

        uint ownerAddonId;
        if (fromSaddlebag)
        {
            var saddlebagAddon = gameGui.GetAddonByName<AddonInventoryBuddy>("InventoryBuddy");
            if (saddlebagAddon == null || !saddlebagAddon->IsReady)
            {
                SaddlebagMoveStatus = "陆行鸟鞍囊界面尚未就绪。";
                return false;
            }

            ownerAddonId = saddlebagAddon->Id;
        }
        else if (!TryGetMainInventoryAddonId(out ownerAddonId))
        {
            var inventoryAgent = AgentInventory.Instance();
            if (inventoryAgent != null &&
                !inventoryAgent->IsAgentActive() &&
                inventoryAgent->IsActivatable())
            {
                inventoryAgent->Show();
            }

            SaddlebagMoveStatus = "正在打开主背包界面，等待地图槽位可以右键交互。";
            return false;
        }

        var existingMenu = gameGui.GetAddonByName<AddonContextMenu>("ContextMenu");
        if (existingMenu != null && existingMenu->IsReady && existingMenu->IsVisible)
        {
            existingMenu->Close(true);
        }

        var inventoryContext = AgentInventoryContext.Instance();
        if (inventoryContext == null)
        {
            SaddlebagMoveStatus = "无法获取游戏背包右键菜单代理。";
            return false;
        }

        inventoryContext->OpenForItemSlot(sourceType, sourceSlot, 0, ownerAddonId);
        SaddlebagMoveStatus = fromSaddlebag
            ? $"已对鞍囊 {sourceType}[{sourceSlot}] 中的地图执行右键交互。"
            : $"已对主背包 {sourceType}[{sourceSlot}] 中的地图执行右键交互。";
        return true;
    }

    private unsafe bool TrySelectFirstInventoryContextMenuOption(bool fromSaddlebag)
    {
        var contextMenu = gameGui.GetAddonByName<AddonContextMenu>("ContextMenu");
        var inventoryContext = AgentInventoryContext.Instance();
        if (contextMenu == null ||
            !contextMenu->IsReady ||
            !contextMenu->IsVisible ||
            !contextMenu->IsFullyLoaded() ||
            inventoryContext == null ||
            inventoryContext->ContextItemCount < 1 ||
            inventoryContext->TargetInventorySlot == null)
        {
            return false;
        }

        var targetIsSaddlebag = SaddlebagClientInventoryTypes.Contains(inventoryContext->TargetInventoryId);
        var targetIsMainInventory = MainClientInventoryTypes.Contains(inventoryContext->TargetInventoryId);
        if (targetIsSaddlebag != fromSaddlebag ||
            (!targetIsSaddlebag && !targetIsMainInventory) ||
            inventoryContext->TargetInventorySlot->GetBaseItemId() != SelectedTreasureMapItemId)
        {
            SaddlebagMoveStatus = "右键菜单目标不是当前选择的地图，已停止操作。";
            return false;
        }

        if (inventoryContext->IsContextItemDisabled(0))
        {
            SaddlebagMoveStatus = "右键菜单第一项当前不可用，正在等待游戏允许操作。";
            return false;
        }

        contextMenu->OnMenuSelected(0, 0);
        return true;
    }

    private unsafe bool TryGetMainInventoryAddonId(out uint addonId)
    {
        addonId = 0;

        var inventoryAddon = gameGui.GetAddonByName<AddonInventory>("Inventory");
        if (inventoryAddon != null && inventoryAddon->IsReady && inventoryAddon->IsVisible)
        {
            addonId = inventoryAddon->Id;
            return true;
        }

        var largeInventoryAddon = gameGui.GetAddonByName<AddonInventoryLarge>("InventoryLarge");
        if (largeInventoryAddon != null && largeInventoryAddon->IsReady && largeInventoryAddon->IsVisible)
        {
            addonId = largeInventoryAddon->Id;
            return true;
        }

        var expandedInventoryAddon = gameGui.GetAddonByName<AddonInventoryExpansion>("InventoryExpansion");
        if (expandedInventoryAddon != null && expandedInventoryAddon->IsReady && expandedInventoryAddon->IsVisible)
        {
            addonId = expandedInventoryAddon->Id;
            return true;
        }

        return false;
    }

    private unsafe bool TryMoveSelectedMapFromSaddlebagToMainInventory(out int moveResult)
    {
        moveResult = -1;
        if (emergencyStopActive)
        {
            SaddlebagMoveStatus = "已紧急停止鞍囊操作。";
            return false;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            SaddlebagMoveStatus = "无法获取游戏库存管理器。";
            return false;
        }

        if (!EnsureSaddlebagLoaded(inventoryManager))
        {
            return false;
        }

        InventoryType sourceType = default;
        ushort sourceSlot = 0;
        var sourceFound = false;
        foreach (var inventoryType in SaddlebagClientInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->GetBaseItemId() == SelectedTreasureMapItemId)
                {
                    sourceType = inventoryType;
                    sourceSlot = (ushort)slot;
                    sourceFound = true;
                    break;
                }
            }

            if (sourceFound)
            {
                break;
            }
        }

        if (!sourceFound)
        {
            SaddlebagMoveStatus = $"陆行鸟鞍囊中没有{SelectedTreasureMapName}，或鞍囊尚未加载。";
            return false;
        }

        InventoryType destinationType = default;
        ushort destinationSlot = 0;
        var destinationFound = false;
        foreach (var inventoryType in MainClientInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->ItemId == 0)
                {
                    destinationType = inventoryType;
                    destinationSlot = (ushort)slot;
                    destinationFound = true;
                    break;
                }
            }

            if (destinationFound)
            {
                break;
            }
        }

        if (!destinationFound)
        {
            SaddlebagMoveStatus = "主背包没有空槽，无法取出陆行鸟鞍囊地图。";
            return false;
        }

        moveResult = 0;
        return TryOpenSelectedMapInventoryContextMenu(fromSaddlebag: true);
    }

    private unsafe bool TryMoveSelectedMapFromMainInventoryToSaddlebag(out int moveResult)
    {
        moveResult = -1;
        if (emergencyStopActive)
        {
            SaddlebagMoveStatus = "已紧急停止鞍囊操作。";
            return false;
        }

        var inventoryManager = InventoryManager.Instance();
        if (inventoryManager == null)
        {
            SaddlebagMoveStatus = "无法获取游戏库存管理器。";
            return false;
        }

        if (!EnsureSaddlebagLoaded(inventoryManager))
        {
            return false;
        }

        InventoryType sourceType = default;
        ushort sourceSlot = 0;
        var sourceFound = false;
        foreach (var inventoryType in MainClientInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->GetBaseItemId() == SelectedTreasureMapItemId)
                {
                    sourceType = inventoryType;
                    sourceSlot = (ushort)slot;
                    sourceFound = true;
                    break;
                }
            }

            if (sourceFound)
            {
                break;
            }
        }

        if (!sourceFound)
        {
            SaddlebagMoveStatus = $"主背包中没有{SelectedTreasureMapName}。";
            return false;
        }

        InventoryType destinationType = default;
        ushort destinationSlot = 0;
        var destinationFound = false;
        foreach (var inventoryType in SaddlebagClientInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container == null || !container->IsLoaded)
            {
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item != null && item->ItemId == 0)
                {
                    destinationType = inventoryType;
                    destinationSlot = (ushort)slot;
                    destinationFound = true;
                    break;
                }
            }

            if (destinationFound)
            {
                break;
            }
        }

        if (!destinationFound)
        {
            SaddlebagMoveStatus = "陆行鸟鞍囊没有已加载的空槽，无法存入第 2 张地图。";
            return false;
        }

        moveResult = 0;
        return TryOpenSelectedMapInventoryContextMenu(fromSaddlebag: false);
    }

    private unsafe bool EnsureSaddlebagLoaded(InventoryManager* inventoryManager)
    {
        var agentModule = AgentModule.Instance();
        var saddlebagAgent = agentModule == null
            ? null
            : agentModule->GetAgentByInternalId(AgentId.InventoryBuddy);
        if (saddlebagAgent == null)
        {
            SaddlebagMoveStatus = "无法获取陆行鸟鞍囊界面代理。";
            return false;
        }

        var saddlebagAddon = gameGui.GetAddonByName<AddonInventoryBuddy>("InventoryBuddy");
        if (saddlebagAddon == null ||
            !saddlebagAddon->IsReady ||
            !saddlebagAddon->IsFullyLoaded())
        {
            if (!saddlebagAgent->IsAgentActive())
            {
                if (!saddlebagAgent->IsActivatable())
                {
                    SaddlebagMoveStatus = "当前状态无法打开陆行鸟鞍囊，请先结束市场交互后再重试。";
                    return false;
                }

                saddlebagAgent->Show();
                SaddlebagMoveStatus = "正在打开陆行鸟鞍囊，等待界面完整加载。";
            }
            else
            {
                SaddlebagMoveStatus = "陆行鸟鞍囊已打开，等待界面完成加载。";
            }

            return false;
        }

        var regularSaddlebagLoaded = false;
        foreach (var inventoryType in SaddlebagClientInventoryTypes)
        {
            var container = inventoryManager->GetInventoryContainer(inventoryType);
            if (container != null && container->IsLoaded)
            {
                regularSaddlebagLoaded = true;
                break;
            }
        }

        if (regularSaddlebagLoaded)
        {
            return true;
        }

        SaddlebagMoveStatus = "陆行鸟鞍囊界面已就绪，但库存仍未完成加载。";
        return false;
    }

    private unsafe void CloseSaddlebagWindow()
    {
        var agentModule = AgentModule.Instance();
        var saddlebagAgent = agentModule == null
            ? null
            : agentModule->GetAgentByInternalId(AgentId.InventoryBuddy);
        if (saddlebagAgent != null && saddlebagAgent->IsAgentActive())
        {
            saddlebagAgent->Hide();
        }
    }

    private unsafe void TestTeleportToOpenedMapAetheryteOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive)
        {
            return;
        }

        var mapAgent = AgentMap.Instance();
        if (mapAgent == null)
        {
            if (TryRequestAutomaticTreasureMap())
            {
                return;
            }

            TeleportTestStatus = "无法读取地图界面。请先打开要测试的地图。";
            return;
        }

        if (mapAgent->FlagMarkerCount == 0)
        {
            if (TryRequestAutomaticTreasureMap())
            {
                return;
            }

            TeleportTestStatus = "当前地图没有红旗标点，请先设置红旗后再测试。";
            return;
        }

        var flagMarker = mapAgent->FlagMapMarkers[0];
        var territoryId = flagMarker.TerritoryId;
        var mapId = flagMarker.MapId;
        var flagX = flagMarker.XFloat;
        var flagY = flagMarker.YFloat;
        if (territoryId == 0 || mapId == 0)
        {
            if (TryRequestAutomaticTreasureMap())
            {
                return;
            }

            TeleportTestStatus = "红旗标点没有有效的地图信息。";
            return;
        }

        TryAnnounceNewFlag(territoryId, mapId, flagX, flagY);

        autoMapCommandSent = false;
        autoMapFlagRetryQueued = false;

        IAetheryteEntry? matchedEntry = null;
        IAetheryteEntry? firstMatchedEntry = null;
        var candidateCount = 0;
        var positionedCount = 0;
        var nearestDistanceSquared = float.PositiveInfinity;
        try
        {
            foreach (var entry in aetheryteList)
            {
                if (entry.TerritoryId != territoryId)
                {
                    continue;
                }

                var aetheryte = entry.AetheryteData.Value;
                if (!aetheryte.IsAetheryte || aetheryte.Map.RowId != mapId)
                {
                    continue;
                }

                candidateCount++;
                if (firstMatchedEntry is null)
                {
                    firstMatchedEntry = entry;
                }

                if (TryGetAetheryteWorldPosition(aetheryte, mapId, out var aetheryteX, out var aetheryteY))
                {
                    var deltaX = aetheryteX - flagX;
                    var deltaY = aetheryteY - flagY;
                    var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
                    if (distanceSquared < nearestDistanceSquared)
                    {
                        nearestDistanceSquared = distanceSquared;
                        matchedEntry = entry;
                    }

                    positionedCount++;
                }
            }
        }
        catch (Exception exception)
        {
            TeleportTestStatus = $"读取传送水晶数据失败：{exception.GetType().Name}";
            return;
        }

        var usedCompatibilityFallback = matchedEntry is null && firstMatchedEntry is not null;
        if (usedCompatibilityFallback)
        {
            matchedEntry = firstMatchedEntry;
        }

        if (matchedEntry is null)
        {
            TeleportTestStatus = $"当前地图未匹配到传送水晶（地图 {mapId}，区域 {territoryId}）。";
            return;
        }

        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            TeleportTestStatus = "无法获取游戏传送组件。";
            return;
        }

        var accepted = telepo->Teleport(matchedEntry.AetheryteId, matchedEntry.SubIndex);
        if (!accepted)
        {
            TeleportTestStatus = "游戏拒绝了传送请求，请确认角色当前可以传送。";
            return;
        }

        mountAfterTeleportPending = true;
        mountRetryQueued = false;
        mountAttemptCount = 0;
        dismountAtFlagPending = false;
        TeleportTestStatus = usedCompatibilityFallback
            ? $"水晶坐标不可用，已按兼容方式发起传送（候选 {candidateCount} 个），等待传送完成后使用随机坐骑。"
            : $"已请求传送到红旗最近的水晶（候选 {candidateCount} 个，可比较 {positionedCount} 个），等待传送完成后使用随机坐骑。";
        ScheduleMountRouletteRetry();
    }

    private unsafe void TestAnnounceCurrentFlagOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive)
        {
            return;
        }

        var mapAgent = AgentMap.Instance();
        if (mapAgent == null || mapAgent->FlagMarkerCount == 0)
        {
            TeleportTestStatus = "当前没有可播报的红旗坐标。";
            return;
        }

        var flagMarker = mapAgent->FlagMapMarkers[0];
        if (flagMarker.TerritoryId == 0 || flagMarker.MapId == 0)
        {
            TeleportTestStatus = "当前红旗没有有效的区域或地图信息。";
            return;
        }

        AnnounceFlag(
            flagMarker.TerritoryId,
            flagMarker.MapId,
            flagMarker.XFloat,
            flagMarker.YFloat,
            isTest: true);
    }

    private void TryAnnounceNewFlag(uint territoryId, uint mapId, float flagX, float flagY)
    {
        const float coordinateTolerance = 0.01f;
        if (hasAnnouncedFlag &&
            lastAnnouncedFlagTerritoryId == territoryId &&
            lastAnnouncedFlagMapId == mapId &&
            MathF.Abs(lastAnnouncedFlagX - flagX) <= coordinateTolerance &&
            MathF.Abs(lastAnnouncedFlagY - flagY) <= coordinateTolerance)
        {
            return;
        }

        AnnounceFlag(territoryId, mapId, flagX, flagY, isTest: false);
    }

    private void AnnounceFlag(uint territoryId, uint mapId, float flagX, float flagY, bool isTest)
    {
        var accepted = TrySendPartyFlag();
        hasAnnouncedFlag = true;
        lastAnnouncedFlagTerritoryId = territoryId;
        lastAnnouncedFlagMapId = mapId;
        lastAnnouncedFlagX = flagX;
        lastAnnouncedFlagY = flagY;

        var prefix = isTest ? "测试播报" : "检测到新红旗并播报";
        TeleportTestStatus = accepted
            ? $"{prefix}：已发送 /p <flag>（区域 {territoryId}，地图 {mapId}，X {flagX:F2}，Y {flagY:F2}）。"
            : $"{prefix}：游戏聊天组件暂不可用，已记录该红旗避免重复刷屏。";
    }

    private unsafe bool TrySendPartyFlag()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            return false;
        }

        var message = GameUtf8String.FromSequence(Encoding.UTF8.GetBytes("/p <flag>"));
        if (message == null)
        {
            return false;
        }

        try
        {
            uiModule->ProcessChatBoxEntry(message);
            return true;
        }
        finally
        {
            message->Dtor(free: true);
        }
    }

    private bool TryRequestAutomaticTreasureMap()
    {
        if (!IsAutoTreasureHuntEnabled || !HasTaskTreasureMap)
        {
            return false;
        }

        if (!autoMapCommandSent)
        {
            autoMapCommandSent = true;
            var commandAccepted = commandManager.ProcessCommand("/tmap");
            AutoTreasureHuntStatus = commandAccepted
                ? "任务道具中有地图但没有标点信息，已执行 /tmap，等待地图标点。"
                : "任务道具中有地图但没有标点信息，/tmap 执行失败，正在重试读取。";
        }

        if (!autoMapFlagRetryQueued)
        {
            autoMapFlagRetryQueued = true;
            _ = framework.RunOnTick(
                () =>
                {
                    autoMapFlagRetryQueued = false;
                    if (IsAutoTreasureHuntEnabled && HasTaskTreasureMap)
                    {
                        TestTeleportToOpenedMapAetheryteOnFrameworkThread();
                    }
                },
                delay: TimeSpan.FromSeconds(2));
        }

        return true;
    }

    private unsafe void UseMountRouletteAfterTeleportOnFrameworkThread()
    {
        if (!IsHeadLogicSelected && !IsWheelLogicSelected)
        {
            mountAfterTeleportPending = false;
            mountRetryQueued = false;
            return;
        }

        if (!mountAfterTeleportPending)
        {
            return;
        }

        // 地图 1059 只允许转盘流程；传送后的旧坐骑回调可能在切图后才到达，
        // 必须在这里清掉挂起状态，避免继续执行野外寻路。
        if ((IsHeadLogicSelected && IsRouletteMode) || emergencyStopActive)
        {
            mountAfterTeleportPending = false;
            mountRetryQueued = false;
            dismountAtFlagPending = false;
            return;
        }

        if (condition[ConditionFlag.Mounted])
        {
            mountAfterTeleportPending = false;
            var commandAccepted = commandManager.ProcessCommand("/vnav flyflag");
            dismountAtFlagPending = true;
            navigationPositionSampleValid = false;
            navigationMovementObserved = false;
            TeleportTestStatus = commandAccepted
                ? "已确认进入骑乘状态并执行 /vnav flyflag，正在监控角色坐标。"
                : "已发送 /vnav flyflag（命令返回未确认），仍将监控角色坐标。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            return;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            TeleportTestStatus = "正在切换区域，等待传送完成后使用随机坐骑。";
            ScheduleMountRouletteRetry();
            return;
        }

        if (condition[ConditionFlag.Mounting] || condition[ConditionFlag.Casting])
        {
            TeleportTestStatus = "随机坐骑正在读条，等待进入骑乘状态。";
            ScheduleMountRouletteRetry();
            return;
        }

        mountAttemptCount++;
        if (mountAttemptCount > 30)
        {
            mountAttemptCount = 0;
            TeleportTestStatus = "随机坐骑连续尝试 30 次仍未成功，防卡保护将在三秒后重新开始尝试。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            mountRetryQueued = true;
            _ = framework.RunOnTick(
                () =>
                {
                    mountRetryQueued = false;
                    UseMountRouletteAfterTeleportOnFrameworkThread();
                },
                delay: TimeSpan.FromSeconds(3));
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            TeleportTestStatus = "无法获取动作管理器。";
            ScheduleMountRouletteRetry();
            return;
        }

        var mountActionStatus = actionManager->GetActionStatus(
            ActionType.GeneralAction,
            MountRouletteGeneralActionId);
        if (mountActionStatus != 0)
        {
            TeleportTestStatus = $"当前无法使用随机坐骑（状态码：{mountActionStatus}），一秒后重试。";
            ScheduleMountRouletteRetry();
            return;
        }

        TeleportTestStatus = actionManager->UseAction(
            ActionType.GeneralAction,
            MountRouletteGeneralActionId)
            ? "已开始随机坐骑读条，等待进入骑乘状态。"
            : "随机坐骑动作调用失败，一秒后重试。";
        ScheduleMountRouletteRetry();
    }

    private void ScheduleMountRouletteRetry()
    {
        if (!mountAfterTeleportPending || mountRetryQueued)
        {
            return;
        }

        mountRetryQueued = true;
        _ = framework.RunOnTick(
            () =>
            {
                mountRetryQueued = false;
                UseMountRouletteAfterTeleportOnFrameworkThread();
            },
            delay: TimeSpan.FromSeconds(1));
    }

    private void TryDismountAtFlag()
    {
        if (!dismountAtFlagPending)
        {
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            AutoTreasureHuntStatus = "红旗监控已启动，但卫月对象表暂时无法取得角色对象。";
            return;
        }

        const float movementTolerance = 0.05f;
        var currentPosition = localPlayer.Position;
        if (!navigationPositionSampleValid)
        {
            navigationPositionSampleValid = true;
            navigationSampleX = currentPosition.X;
            navigationSampleY = currentPosition.Y;
            navigationSampleZ = currentPosition.Z;
            navigationPositionStableSince = DateTime.UtcNow;
            AutoTreasureHuntStatus = "正在等待 vnavmesh 开始移动角色。";
            return;
        }

        if (MathF.Abs(currentPosition.X - navigationSampleX) > movementTolerance ||
            MathF.Abs(currentPosition.Y - navigationSampleY) > movementTolerance ||
            MathF.Abs(currentPosition.Z - navigationSampleZ) > movementTolerance)
        {
            navigationMovementObserved = true;
            navigationSampleX = currentPosition.X;
            navigationSampleY = currentPosition.Y;
            navigationSampleZ = currentPosition.Z;
            navigationPositionStableSince = DateTime.UtcNow;
            AutoTreasureHuntStatus = "vnavmesh 正在移动角色，等待 X/Y/Z 坐标停止变化。";
            return;
        }

        if (!navigationMovementObserved)
        {
            AutoTreasureHuntStatus = "正在等待 vnavmesh 开始移动角色。";
            return;
        }

        if (DateTime.UtcNow - navigationPositionStableSince < TimeSpan.FromSeconds(1))
        {
            AutoTreasureHuntStatus = "角色 X/Y/Z 坐标已停止变化，正在确认稳定。";
            return;
        }

        dismountAtFlagPending = false;
        dismountReadyAttemptCount = 0;
        dismountUseAttemptCount = 0;
        TeleportTestStatus = "角色 X/Y/Z 坐标已稳定，正在使用跳下。";
        AutoTreasureHuntStatus = TeleportTestStatus;
        UseDismountAfterArrivalOnFrameworkThread();
    }

    private unsafe void UseDismountAfterArrivalOnFrameworkThread()
    {
        if ((!IsHeadLogicSelected && !IsWheelLogicSelected) ||
            !IsAutoTreasureHuntEnabled ||
            (IsHeadLogicSelected && IsRouletteMode) ||
            emergencyStopActive)
        {
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            TeleportTestStatus = "无法获取动作管理器，一秒后重试使用跳下。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            _ = framework.RunOnTick(
                UseDismountAfterArrivalOnFrameworkThread,
                delay: TimeSpan.FromSeconds(1));
            return;
        }

        dismountReadyAttemptCount++;
        var actionStatus = actionManager->GetActionStatus(
            ActionType.GeneralAction,
            DismountGeneralActionId);
        if (actionStatus != 0)
        {
            if (dismountReadyAttemptCount >= 20)
            {
                dismountReadyAttemptCount = 0;
                TeleportTestStatus = $"等待跳下可用超时（最后状态码：{actionStatus}），防卡保护将在两秒后重试。";
                AutoTreasureHuntStatus = TeleportTestStatus;
                _ = framework.RunOnTick(
                    UseDismountAfterArrivalOnFrameworkThread,
                    delay: TimeSpan.FromSeconds(2));
                return;
            }

            TeleportTestStatus = $"正在等待跳下可用（状态码：{actionStatus}，第 {dismountReadyAttemptCount} 次检查）。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            _ = framework.RunOnTick(
                UseDismountAfterArrivalOnFrameworkThread,
                delay: TimeSpan.FromMilliseconds(500));
            return;
        }

        var actionAccepted = actionManager->UseAction(
            ActionType.GeneralAction,
            DismountGeneralActionId);
        if (!actionAccepted)
        {
            TeleportTestStatus = "跳下动作调用失败，防卡保护将在一秒后重试。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            _ = framework.RunOnTick(
                UseDismountAfterArrivalOnFrameworkThread,
                delay: TimeSpan.FromSeconds(1));
            return;
        }

        dismountWaitCount = 0;
        dismountUseAttemptCount++;
        TeleportTestStatus = $"已调用跳下（第 {dismountUseAttemptCount} 次），等待骑乘状态解除。";
        AutoTreasureHuntStatus = TeleportTestStatus;
        _ = framework.RunOnTick(
            WaitForDismountThenDig,
            delay: TimeSpan.FromMilliseconds(500));
    }

    private void WaitForDismountThenDig()
    {
        if ((!IsHeadLogicSelected && !IsWheelLogicSelected) ||
            !IsAutoTreasureHuntEnabled ||
            (IsHeadLogicSelected && IsRouletteMode) ||
            emergencyStopActive)
        {
            return;
        }

        if (!condition[ConditionFlag.Mounted])
        {
            if (IsWheelLogicSelected)
            {
                AutoTreasureHuntStatus = "车轮：已到达红旗位置并下坐骑。";
                return;
            }

            TeleportTestStatus = "已确认下坐骑，一秒后使用挖掘。";
            _ = framework.RunOnTick(
                UseDigOnFrameworkThread,
                delay: TimeSpan.FromSeconds(1));
            return;
        }

        dismountWaitCount++;
        if (dismountWaitCount >= 4)
        {
            if (dismountUseAttemptCount >= 10)
            {
                dismountUseAttemptCount = 0;
                dismountReadyAttemptCount = 0;
                TeleportTestStatus = "已调用跳下 10 次但仍未解除骑乘状态，防卡保护将在两秒后重新尝试。";
                AutoTreasureHuntStatus = TeleportTestStatus;
                _ = framework.RunOnTick(
                    UseDismountAfterArrivalOnFrameworkThread,
                    delay: TimeSpan.FromSeconds(2));
                return;
            }

            dismountReadyAttemptCount = 0;
            TeleportTestStatus = "跳下后两秒仍处于骑乘状态，正在重试。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            _ = framework.RunOnTick(
                UseDismountAfterArrivalOnFrameworkThread,
                delay: TimeSpan.FromMilliseconds(500));
            return;
        }

        _ = framework.RunOnTick(
            WaitForDismountThenDig,
            delay: TimeSpan.FromMilliseconds(500));
    }

    private unsafe void UseDismountForTestOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive)
        {
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            TeleportTestStatus = "无法获取动作管理器。";
            return;
        }

        var actionStatus = actionManager->GetActionStatus(
            ActionType.GeneralAction,
            DismountGeneralActionId);
        if (actionStatus != 0)
        {
            TeleportTestStatus = $"当前无法使用跳下（状态码：{actionStatus}）。";
            return;
        }

        TeleportTestStatus = actionManager->UseAction(
            ActionType.GeneralAction,
            DismountGeneralActionId)
            ? "已调用跳下（通用技能 ID 23）。"
            : "跳下动作调用失败。";
    }

    private unsafe void UseDigOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive || !IsAutoTreasureHuntEnabled || IsRouletteMode)
        {
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            TeleportTestStatus = "无法获取动作管理器，挖掘将在一秒后重试。";
            ScheduleDigRetry();
            return;
        }

        var actionStatus = actionManager->GetActionStatus(
            ActionType.GeneralAction,
            DigGeneralActionId);
        if (actionStatus != 0)
        {
            TeleportTestStatus = $"当前无法使用挖掘（状态码：{actionStatus}），一秒后重试。";
            ScheduleDigRetry();
            return;
        }

        if (actionManager->UseAction(ActionType.GeneralAction, DigGeneralActionId))
        {
            digRetryCount = 0;
            digRetryQueued = false;
            TeleportTestStatus = StartTreasureChestSearch();
        }
        else
        {
            TeleportTestStatus = "挖掘动作调用失败，一秒后重试。";
            ScheduleDigRetry();
        }
    }

    private void ScheduleDigRetry()
    {
        if (emergencyStopActive || digRetryQueued)
        {
            return;
        }

        digRetryCount++;
        if (digRetryCount >= 30)
        {
            digRetryCount = 0;
            workflowWatchdogRecoveryCount++;
            RecoverStuckWorkflow("dig");
            return;
        }

        digRetryQueued = true;
        _ = framework.RunOnTick(
            () =>
            {
                digRetryQueued = false;
                UseDigOnFrameworkThread();
            },
            delay: TimeSpan.FromSeconds(1));
    }

    private string StartTreasureChestSearch()
    {
        treasureChestPending = true;
        treasureChestEntityId = 0;
        chestPositionSampleValid = false;
        confirmNextTreasureChestInteraction = true;
        waitForTreasureCombatStart = false;
        treasureCombatActive = false;
        treasureCombatLastCondition = false;
        treasureCombatEndCandidate = null;
        treasurePortalPending = false;
        treasurePortalEntityId = 0;
        treasurePortalCloseDelayStarted = false;
        treasurePortalSearchDeadline = default;
        confirmTreasurePortalPending = false;
        return "已使用挖掘，等待宝箱出现。";
    }

    private void TryHandleTreasureCombat()
    {
        if (!IsHeadLogicSelected)
        {
            return;
        }

        if (waitForTreasureCombatStart)
        {
            if (!condition[ConditionFlag.InCombat])
            {
                return;
            }

            waitForTreasureCombatStart = false;
            treasureCombatActive = true;
            treasureCombatLastCondition = true;
            treasureCombatEndCandidate = null;
            TeleportTestStatus = commandManager.ProcessCommand("/bmrai on")
                ? "已进入宝箱战斗，并已执行 /bmrai on。"
                : "已进入宝箱战斗，但 /bmrai on 执行失败。";
            return;
        }

        if (!treasureCombatActive)
        {
            return;
        }

        if (condition[ConditionFlag.InCombat])
        {
            if (!treasureCombatLastCondition)
            {
                commandManager.ProcessCommand("/bmrai on");
                TeleportTestStatus = "重新进入宝箱战斗，并已执行 /bmrai on。";
            }

            treasureCombatLastCondition = true;
            treasureCombatEndCandidate = null;
            return;
        }

        if (treasureCombatLastCondition)
        {
            treasureCombatLastCondition = false;
            TeleportTestStatus = commandManager.ProcessCommand("/bmrai off")
                ? "已脱离战斗，并已执行 /bmrai off。"
                : "已脱离战斗，但 /bmrai off 返回未确认。";
        }

        treasureCombatEndCandidate ??= DateTime.UtcNow;
        if (DateTime.UtcNow - treasureCombatEndCandidate.Value < TimeSpan.FromSeconds(2))
        {
            TeleportTestStatus = "战斗状态已解除，正在确认战斗结束。";
            return;
        }

        treasureCombatActive = false;
        treasureCombatLastCondition = false;
        treasureCombatEndCandidate = null;
        treasureChestPending = true;
        treasureChestEntityId = 0;
        chestPositionSampleValid = false;
        confirmNextTreasureChestInteraction = false;
        TeleportTestStatus = "战斗已结束，正在重新寻找并开启宝箱。";
    }

    private void TryHandleTreasureChest()
    {
        if (!IsHeadLogicSelected)
        {
            return;
        }

        if (!treasureChestPending)
        {
            return;
        }

        var chest = treasureChestEntityId == 0
            ? objectTable.FirstOrDefault(IsTreasureChest)
            : objectTable.FirstOrDefault(gameObject => gameObject.EntityId == treasureChestEntityId);

        if (chest == null)
        {
            TeleportTestStatus = "已使用挖掘，等待宝箱出现。";
            return;
        }

        if (treasureChestEntityId == 0)
        {
            treasureChestEntityId = chest.EntityId;
            chestPositionSampleValid = false;
            targetManager.Target = chest;
            if (!commandManager.ProcessCommand("/vnav movetarget"))
            {
                treasureChestEntityId = 0;
                TeleportTestStatus = "已找到宝箱，但 /vnav movetarget 执行失败。";
                return;
            }

            TeleportTestStatus = "已找到宝箱，正在前往宝箱位置。";
            return;
        }

        if (chest.EntityId != treasureChestEntityId)
        {
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return;
        }

        var delta = localPlayer.Position - chest.Position;
        var interactionDistance = confirmNextTreasureChestInteraction ? 3f : 1f;
        if (delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z >
            interactionDistance * interactionDistance)
        {
            chestPositionSampleValid = false;
            return;
        }

        const float movementTolerance = 0.05f;
        var currentPosition = localPlayer.Position;
        if (!chestPositionSampleValid)
        {
            chestPositionSampleValid = true;
            chestSampleX = currentPosition.X;
            chestSampleY = currentPosition.Y;
            chestSampleZ = currentPosition.Z;
            chestPositionStableSince = DateTime.UtcNow;
            TeleportTestStatus = "已接近宝箱，正在确认角色停止移动。";
            return;
        }

        if (MathF.Abs(currentPosition.X - chestSampleX) > movementTolerance ||
            MathF.Abs(currentPosition.Y - chestSampleY) > movementTolerance ||
            MathF.Abs(currentPosition.Z - chestSampleZ) > movementTolerance)
        {
            chestSampleX = currentPosition.X;
            chestSampleY = currentPosition.Y;
            chestSampleZ = currentPosition.Z;
            chestPositionStableSince = DateTime.UtcNow;
            TeleportTestStatus = "角色仍在靠近宝箱，等待坐标稳定。";
            return;
        }

        if (DateTime.UtcNow - chestPositionStableSince < TimeSpan.FromSeconds(1))
        {
            TeleportTestStatus = "已接近宝箱，正在确认角色停止移动。";
            return;
        }

        treasureChestPending = false;
        chestPositionSampleValid = false;
        targetManager.Target = chest;
        InteractWithTreasureChest(chest);
    }

    private unsafe void InteractWithTreasureChest(object chest)
    {
        if (chest is not Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
        {
            TeleportTestStatus = "宝箱对象已失效，无法开启。";
            return;
        }

        TargetSystem.Instance()->InteractWithObject(
            (GameObject*)gameObject.Address,
            false);
        if (confirmNextTreasureChestInteraction)
        {
            confirmTreasureChestPending = true;
            treasureChestConfirmDeadline = DateTime.UtcNow.AddSeconds(10);
            TeleportTestStatus = "已到达宝箱位置并尝试开启，等待确认窗口。";
        }
        else
        {
            confirmTreasureChestPending = false;
            treasurePortalPending = true;
            treasurePortalEntityId = 0;
            treasurePortalCloseDelayStarted = false;
            treasurePortalSearchDeadline = DateTime.UtcNow.AddSeconds(3);
            TeleportTestStatus = "战斗结束后已返回并开启宝箱，等待传送魔纹出现。";
        }
    }

    private void TryHandleTreasurePortal()
    {
        if (!IsHeadLogicSelected)
        {
            return;
        }

        if (!treasurePortalPending)
        {
            return;
        }

        var portal = treasurePortalEntityId == 0
            ? objectTable.FirstOrDefault(IsTreasurePortal)
            : objectTable.FirstOrDefault(gameObject => gameObject.EntityId == treasurePortalEntityId);
        if (portal == null)
        {
            if (treasurePortalSearchDeadline != default &&
                DateTime.UtcNow >= treasurePortalSearchDeadline)
            {
                RefreshTreasureMapCounts();
                if (!HasTaskTreasureMap)
                {
                    treasurePortalPending = false;
                    treasurePortalEntityId = 0;
                    treasurePortalCloseDelayStarted = false;
                    treasurePortalSearchDeadline = default;
                    AutoTreasureHuntStatus = HasTreasureMap
                        ? $"本次挖宝已结束且未发现传送魔纹，检测到背包和鞍囊中还有 {TreasureMapCount} 张地图，准备继续解读。"
                        : "本次挖宝已结束且未发现传送魔纹，正在重新检查地图库存。";
                    TeleportTestStatus = AutoTreasureHuntStatus;
                    _ = framework.RunOnTick(
                        () =>
                        {
                            if (IsAutoTreasureHuntEnabled && !IsRouletteMode && !emergencyStopActive)
                            {
                                StartAutoTreasureHuntOnFrameworkThread();
                            }
                        },
                        delay: TimeSpan.FromSeconds(1));
                    return;
                }

                // 库存事件可能比任务道具刷新更早，继续给游戏几秒钟同步状态。
                treasurePortalSearchDeadline = DateTime.UtcNow.AddSeconds(5);
            }

            TeleportTestStatus = "第二次宝箱已开启，等待传送魔纹出现。";
            return;
        }

        if (treasurePortalEntityId == 0)
        {
            treasurePortalEntityId = portal.EntityId;
            treasurePortalCloseDelayStarted = false;
            treasurePortalSearchDeadline = default;
            targetManager.Target = portal;
            commandManager.ProcessCommand("/vnav movetarget");
            TeleportTestStatus = "已发现传送魔纹，正在前往魔纹位置。";
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return;
        }

        var delta = localPlayer.Position - portal.Position;
        const float interactionDistance = 2f;
        if (delta.X * delta.X + delta.Y * delta.Y + delta.Z * delta.Z >
            interactionDistance * interactionDistance)
        {
            treasurePortalCloseDelayStarted = false;
            return;
        }

        if (!treasurePortalCloseDelayStarted)
        {
            treasurePortalCloseDelayStarted = true;
            treasurePortalInteractAt = DateTime.UtcNow.AddSeconds(3);
            TeleportTestStatus = "已贴近传送魔纹，等待三秒后交互。";
            return;
        }

        if (DateTime.UtcNow < treasurePortalInteractAt)
        {
            var remaining = treasurePortalInteractAt - DateTime.UtcNow;
            TeleportTestStatus = $"已贴近传送魔纹，{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} 秒后交互。";
            return;
        }

        treasurePortalPending = false;
        treasurePortalCloseDelayStarted = false;
        targetManager.Target = portal;
        InteractWithTreasurePortal(portal);
    }

    private unsafe void InteractWithTreasurePortal(object portal)
    {
        if (portal is not Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
        {
            TeleportTestStatus = "传送魔纹对象已失效，无法交互。";
            return;
        }

        TargetSystem.Instance()->InteractWithObject(
            (GameObject*)gameObject.Address,
            false);
        confirmTreasurePortalPending = true;
        treasurePortalConfirmDeadline = DateTime.UtcNow.AddSeconds(10);
        TeleportTestStatus = "已与传送魔纹交互，等待进入确认窗口。";
    }

    private static bool IsTreasurePortal(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
    {
        return gameObject.BaseId == 2007181 ||
               gameObject.Name.TextValue.Contains("传送魔纹", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTreasureChest(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
    {
        var name = gameObject.Name.TextValue;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var objectKind = gameObject.ObjectKind.ToString();
        return name.Contains("宝箱", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Treasure", StringComparison.OrdinalIgnoreCase) ||
               objectKind.Contains("Treasure", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetAetheryteWorldPosition(
        LuminaAetheryte aetheryte,
        uint mapId,
        out float worldX,
        out float worldY)
    {
        var level = aetheryte.Level[0].ValueNullable;
        if (level.HasValue)
        {
            worldX = level.Value.X;
            worldY = level.Value.Z;
            return true;
        }

        LuminaMapMarker? matchedMarker = null;
        var markerSheet = dataManager.GetSubrowExcelSheet<LuminaMapMarker>();
        foreach (var marker in markerSheet.Flatten())
        {
            if (marker.DataType == 3 && marker.DataKey.RowId == aetheryte.RowId)
            {
                matchedMarker = marker;
                break;
            }
        }

        if (!matchedMarker.HasValue && aetheryte.AethernetName.RowId != 0)
        {
            foreach (var marker in markerSheet.Flatten())
            {
                if (marker.DataType == 4 && marker.DataKey.RowId == aetheryte.AethernetName.RowId)
                {
                    matchedMarker = marker;
                    break;
                }
            }
        }

        var map = dataManager.GetExcelSheet<LuminaMap>().GetRowOrDefault(mapId);
        if (!matchedMarker.HasValue || !map.HasValue)
        {
            worldX = 0;
            worldY = 0;
            return false;
        }

        var scale = map.Value.SizeFactor * 0.01f;
        worldX = PixelCoordToWorldCoord(matchedMarker.Value.X, scale, map.Value.OffsetX);
        worldY = PixelCoordToWorldCoord(matchedMarker.Value.Y, scale, map.Value.OffsetY);
        return true;
    }

    private static float PixelCoordToWorldCoord(float coordinate, float scale, short offset)
    {
        return ((coordinate * 0.9990244f) - 1024f) / scale - (offset * 0.001f);
    }

    private unsafe void UseTreasureMapOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive || !IsAutoTreasureHuntEnabled || IsRouletteMode)
        {
            return;
        }

        // 卫月显性任务状态：上一张藏宝图任务尚未完全结算时，不调用解读技能。
        if (condition[ConditionFlag.OccupiedInQuestEvent])
        {
            TreasureMapUseStatus = "当前仍处于藏宝图任务事件中，等待任务完全结束后再解读下一张地图。";
            AutoTreasureHuntStatus = TreasureMapUseStatus;
            ScheduleQuestCompletionDecipherRetry();
            return;
        }

        var actionManager = ActionManager.Instance();
        if (actionManager is null)
        {
            TreasureMapUseStatus = "无法获取游戏动作管理器，一秒后重试解读。";
            AutoTreasureHuntStatus = TreasureMapUseStatus;
            ScheduleDecipherRetry();
            return;
        }

        var actionStatus = actionManager->GetActionStatus(
            ActionType.GeneralAction,
            DecipherGeneralActionId);
        if (actionStatus != 0)
        {
            TreasureMapUseStatus = $"当前无法解读地图（状态码：{actionStatus}），一秒后重试。";
            AutoTreasureHuntStatus = TreasureMapUseStatus;
            ScheduleDecipherRetry();
            return;
        }

        TreasureMapUseStatus = actionManager->UseAction(
            ActionType.GeneralAction,
            DecipherGeneralActionId)
            ? "已开始解读地图。"
            : "解读动作调用失败。";

        if (TreasureMapUseStatus == "已开始解读地图。")
        {
            decipherRetryCount = 0;
            decipherRetryQueued = false;
            questDecipherRetryQueued = false;
            selectMapPending = true;
        }
        else if (autoWaitingForTaskTreasureMap)
        {
            AutoTreasureHuntStatus = "解读动作调用失败，一秒后重试。";
            ScheduleDecipherRetry();
        }
    }

    private void ScheduleQuestCompletionDecipherRetry()
    {
        if (emergencyStopActive ||
            questDecipherRetryQueued ||
            automaticMapSupplementRunning)
        {
            return;
        }

        questDecipherRetryQueued = true;
        _ = framework.RunOnTick(
            () =>
            {
                questDecipherRetryQueued = false;
                if (IsAutoTreasureHuntEnabled && !IsRouletteMode && !emergencyStopActive)
                {
                    UseTreasureMapOnFrameworkThread();
                }
            },
            delay: TimeSpan.FromSeconds(1));
    }

    private void ScheduleDecipherRetry()
    {
        if (emergencyStopActive || decipherRetryQueued || automaticMapSupplementRunning)
        {
            return;
        }

        decipherRetryCount++;
        if (decipherRetryCount >= 30)
        {
            decipherRetryCount = 0;
            workflowWatchdogRecoveryCount++;
            RecoverStuckWorkflow("decipher-action");
            return;
        }

        decipherRetryQueued = true;
        _ = framework.RunOnTick(
            () =>
            {
                decipherRetryQueued = false;
                UseTreasureMapOnFrameworkThread();
            },
            delay: TimeSpan.FromSeconds(1));
    }
}
