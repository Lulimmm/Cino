using Dalamud.Plugin;
using Dalamud.Game.Chat;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
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
using System.Net.Http.Json;
using System.Text.Json;
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

    private const string VnavmeshInternalName = "vnavmesh";
    private const string GlobetrotterInternalName = "globetrotter";
    private const string BossModRebornInternalName = "BossModReborn";
    private const string AeAssistInternalName = "AEAssist";
    private const uint DecipherGeneralActionId = 19;
    private const uint MountRouletteGeneralActionId = 9;
    private const uint DismountGeneralActionId = 23;
    private const uint DigGeneralActionId = 20;
    private const uint OptimizedInteractionMapId = 12;
    private const uint RouletteExitBaseId = 2000139;
    private const float RouletteInteractionDistance = 2f;
    private const float FlagNavigationMeaningfulProgressDistance = 1.5f;
    private static readonly TimeSpan FlagNavigationJitterTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FlagNavigationMaximumDuration = TimeSpan.FromSeconds(75);
    private const uint LimsaLowerDecksTerritoryId = 129;
    private const uint MarketBoardBaseId = 2000402;
    private static readonly TimeSpan MarketPurchaseActionDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MarketBoardOpenStabilizationDelay = TimeSpan.FromSeconds(2);
    private const string DeveloperCredentialHash = "6b7e18dffccc763c26914ff41a8782c1d60ec9155d86bcefeaef65c75b88b5ec";
    private const string AdvancedCredentialHash = "b517162f99cbf9966d8e5e412e44f52dbe0f59683c820d8748f544f3c8391818";
    private const string LicenseValidationEndpoint = "https://1466827323-7rwdj63720.ap-shanghai.tencentscf.com/v1/validate";
    private const string LicenseHealthEndpoint = "https://1466827323-7rwdj63720.ap-shanghai.tencentscf.com/health";
    private static readonly HttpClient LicenseHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
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
    private readonly IDutyState dutyState;
    private readonly IPartyList partyList;
    private readonly ICommandManager commandManager;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly IMarketBoard marketBoard;
    private readonly IPluginLog log;
    private readonly PluginConfiguration configuration;
    private readonly CoordinateApplier coordinateApplier;
    private readonly OpenMarketAnywhere openMarketAnywhere;
    private readonly GlobetrotterTreasureMapCore globetrotterTreasureMapCore;
    private readonly VfxEffectCenterProbe vfxEffectCenterProbe;
    private CredentialRole sessionCredentialRole;
    private bool validationServerCheckInProgress;
    private DateTime validationServerLastCheckedAt;
    private bool? validationServerConnected;
    private string credentialValidationError = string.Empty;
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
    private float navigationProgressAnchorX;
    private float navigationProgressAnchorY;
    private float navigationProgressAnchorZ;
    private DateTime navigationLastMeaningfulProgressAt;
    private DateTime navigationStartedAt;
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
    // 第二次开箱后，任务事件和任务道具库存可能不会在同一帧完成结算。
    // 在确认任务结算稳定前禁止进入下一张地图的解读流程。
    private bool treasureQuestSettlementPending;
    private DateTime treasureQuestSettlementGuardUntil;
    private DateTime treasureQuestSettlementLastSampleAt;
    private int treasureQuestSettlementStableSamples;
    private int treasureQuestSettlementLastTaskCount;
    private int treasureQuestSettlementLastTreasureCount;
    private bool autoWaitingForTaskTreasureMap;
    private bool autoMapCommandSent;
    private DateTime automaticMapFlagRequestedAt;
    private bool autoMapFlagRetryQueued;
    private bool waitingForAutomaticMapFlag;
    private bool headFlagTeleportPending;
    private bool headFlagTeleportReady;
    private bool headFlagAnnouncementPending;
    private uint headPendingFlagTerritoryId;
    private uint headPendingFlagMapId;
    private float headPendingFlagX;
    private float headPendingFlagY;
    private DateTime headFlagAnnouncementDeadline;
    private DateTime headFlagPartyReadyAt;
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
    private TreasureMapRoute activeTreasureMapRoute = TreasureMapRoute.Roulette;
    private bool doorSelectionPortalEntryPending;
    private bool doorSelectionModeActive;
    private uint doorSelectionInstanceMapId;
    private ulong doorSelectionChestEntityId;
    private bool doorSelectionChestMoveIssued;
    private bool doorSelectionChestPositionSampleValid;
    private float doorSelectionChestSampleX;
    private float doorSelectionChestSampleY;
    private float doorSelectionChestSampleZ;
    private DateTime doorSelectionChestPositionStableSince;
    private bool confirmDoorSelectionChestPending;
    private DateTime doorSelectionChestConfirmDeadline;
    private bool doorSelectionInitialChestCompleted;
    private bool doorSelectionPostCombatChestPending;
    private DateTime doorSelectionDutyReadyAt;
    private bool doorSelectionWasInCombat;
    private bool doorSelectionCombatStartedThisFloor;
    private bool doorSelectionInitialChestYesConfirmed;
    private bool doorSelectionCombatObservedAfterYes;
    private bool doorSelectionPostCombatChestHandled;
    private int doorSelectionFloor;
    private ulong doorSelectionDoorEntityId;
    private bool doorSelectionDoorMoveIssued;
    private DateTime doorSelectionDoorAnimationUntil;
    private bool doorSelectionVfxMoveIssued;
    private System.Numerics.Vector3 doorSelectionVfxPosition;
    private int wheelDoorSelectionFloor;
    private uint wheelDoorSelectionDoorBaseId;
    private ulong wheelDoorSelectionDoorEntityId;
    private bool wheelDoorSelectionDoorMoveIssued;
    private System.Numerics.Vector3 wheelDoorSelectionVfxPosition;
    private bool wheelDoorSelectionVfxMoveIssued;
    private DateTime wheelDoorSelectionAnimationUntil;
    private uint wheelDoorSelectionLastMapId;
    private ulong wheelDoorSelectionChestEntityId;
    private bool wheelDoorSelectionChestMoveIssued;
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
    private string wheelTeleportAcceptedPrompt = string.Empty;
    private bool wheelWasInCombat;
    private MapLinkPayload? wheelPendingMapLink;
    private MapLinkPayload? wheelLastMapLink;
    // The arrival order, rather than coordinates, identifies a new map link.
    // Two consecutive maps can legitimately point at the same location.
    private int wheelMapLinkGeneration;
    private int wheelPendingMapLinkGeneration;
    private int wheelActiveMapLinkGeneration;
    private DateTime wheelPendingMapLinkDeadline;
    private bool wheelAwaitingMapChangeAndFlag;
    private uint wheelTeleportSourceMapId;
    private DateTime wheelFlagReadyAt;
    private DateTime wheelTeleportStallSampleAt;
    private float wheelTeleportStallSampleX;
    private float wheelTeleportStallSampleY;
    private float wheelTeleportStallSampleZ;
    private DateTime wheelTeleportStallRecoveryAt;
    private string wheelWatchdogState = string.Empty;
    private DateTime wheelWatchdogStateSince;
    private DateTime wheelWatchdogCooldownUntil;
    private bool wheelNewFlagPending;
    private bool wheelFlagSnapshotValid;
    private DateTime wheelFlagRefreshRequestedAt;
    private uint wheelFlagSnapshotTerritoryId;
    private uint wheelFlagSnapshotMapId;
    private float wheelFlagSnapshotX;
    private float wheelFlagSnapshotY;
    private int wheelFlagRound;
    private int wheelLastProcessedRound = -1;
    private bool wheelWaitingForFreshFlag;
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
    private bool marketBoardTeleportRetryQueued;
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
    private DateTime marketPricePageWaitSince;
    private DateTime marketPricePageReadySince;
    private int marketPricePageRetryCount;
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
    private bool manualTestModeActive;
    private readonly Queue<string> interactableObjectEchoQueue = [];
    private int interactableObjectEchoGeneration;
    private int interactableObjectEchoTotal;
    private string interactableObjectEchoCategory = "可选中物体";
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

    public unsafe Plugin(
        IDalamudPluginInterface pluginInterface,
        IGameInventory gameInventory,
        IGameGui gameGui,
        IChatGui chatGui,
        IFramework framework,
        IAetheryteList aetheryteList,
        IDataManager dataManager,
        IClientState clientState,
        ICondition condition,
        IDutyState dutyState,
        IPartyList partyList,
        ICommandManager commandManager,
        IObjectTable objectTable,
        ITargetManager targetManager,
        IMarketBoard marketBoard,
        IPluginLog log,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        ITextureProvider textureProvider)
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
        this.dutyState = dutyState;
        this.partyList = partyList;
        this.commandManager = commandManager;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.marketBoard = marketBoard;
        this.log = log;
        configuration = pluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        if (!Enum.IsDefined(configuration.LogicMode))
        {
            configuration.LogicMode = TreasureHuntLogicMode.Head;
        }

        coordinateApplier = new CoordinateApplier(objectTable, SaveConfiguration);
        openMarketAnywhere = new OpenMarketAnywhere(framework, gameGui, log, interop, marketBoard, dataManager, textureProvider);
        globetrotterTreasureMapCore = new GlobetrotterTreasureMapCore(
            dataManager,
            gameGui,
            sigScanner,
            interop,
            log,
            showOnHover: () => !CheckGlobetrotterRunning(),
            showOnOpen: () => !CheckGlobetrotterRunning(),
            showOnDecipher: () => !CheckGlobetrotterRunning());
        vfxEffectCenterProbe = new VfxEffectCenterProbe(sigScanner, log);
        gameGui.HoveredItemChanged += globetrotterTreasureMapCore.OnHover;

        ValidateSavedCredential();
        mainWindow = new MainWindow(this, this.pluginInterface, dataManager, textureProvider);
        this.gameInventory.InventoryChanged += OnInventoryChanged;
        this.chatGui.ChatMessage += OnChatMessage;
        this.clientState.ZoneInit += OnZoneInit;
        this.clientState.MapIdChanged += OnMapIdChanged;
        this.framework.Update += OnFrameworkUpdate;
        this.pluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        this.pluginInterface.UiBuilder.Draw += mainWindow.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += mainWindow.Open;
        commandManager.AddHandler("/lltp", new CommandInfo(OnLltpCommand)
        {
            HelpMessage = "测试坐标修改：/lltp x y z",
        });
        commandManager.AddHandler("/llmarket", new CommandInfo(OnLlMarketCommand)
        {
            HelpMessage = "测试打开市场：/llmarket",
        });
        commandManager.AddHandler("/tmap", new CommandInfo(OnTmapCommand)
        {
            HelpMessage = "打开当前任务地图并设置红旗",
        });
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

    private void ApplyOtherPluginCoordinateOnFrameworkThread(float x, float y, float z)
    {
        coordinateApplier.Apply(x, y, z);
        OtherPluginTestStatus = coordinateApplier.Status;
    }

    private void OnLltpCommand(string command, string arguments)
    {
        if (!EnsureAdvancedCommandAccess())
        {
            return;
        }

        var values = arguments.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 3 ||
            !TryParseOtherPluginCoordinate(values[0], out var x) ||
            !TryParseOtherPluginCoordinate(values[1], out var y) ||
            !TryParseOtherPluginCoordinate(values[2], out var z))
        {
            OtherPluginTestStatus = "Usage: /lltp x y z";
            return;
        }

        TestOtherPluginApplyCoordinate(x, y, z);
    }

    private void OnLlMarketCommand(string command, string arguments)
    {
        if (!EnsureAdvancedCommandAccess())
        {
            return;
        }

        TestOtherPluginOpenMarket();
    }


    private bool EnsureAdvancedCommandAccess()
    {
        if (HasAdvancedCommandCredential)
        {
            return true;
        }

        const string message = "此命令仅限高级凭证或开发者凭证使用。";
        OtherPluginTestStatus = message;
        chatGui.PrintError(message, "海豹助手");
        return false;
    }

    private static bool TryParseOtherPluginCoordinate(string text, out float value)
    {
        return float.TryParse(
                   text,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out value) ||
               float.TryParse(
                   text,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.CurrentCulture,
                   out value);
    }

    private void SaveConfiguration() => pluginInterface.SavePluginConfig(configuration);

    public bool IsVnavmeshRunning { get; private set; }

    public bool IsGlobetrotterRunning { get; private set; }

    public bool IsBossModRebornRunning { get; private set; }

    public bool IsAutoTreasureHuntEnabled { get; private set; }

    public bool IsCredentialValidated => sessionCredentialRole != CredentialRole.None;

    public bool HasDeveloperCredential => sessionCredentialRole == CredentialRole.Developer;

    public bool HasAdvancedCommandCredential =>
        sessionCredentialRole is CredentialRole.Advanced or CredentialRole.Developer;

    public bool HasSavedCredential =>
        !string.IsNullOrWhiteSpace(configuration.SavedCredential) ||
        GetCredentialRole(configuration.CredentialHash) != CredentialRole.None;

    public string CredentialRoleName => sessionCredentialRole switch
    {
        CredentialRole.User => "用户凭证",
        CredentialRole.Advanced => "高级凭证",
        CredentialRole.Developer => "开发者凭证",
        _ => "未验证",
    };

    public bool? ValidationServerConnected => validationServerConnected;

    public string CredentialValidationError => credentialValidationError;

    public string ValidationServerStatusText => validationServerConnected switch
    {
        true => "成功连接至验证服务器",
        false => "未连接至验证服务器",
        _ => "正在连接验证服务器…",
    };

    public void EnsureValidationServerHealthCheck()
    {
        if (validationServerCheckInProgress ||
            DateTime.UtcNow - validationServerLastCheckedAt < TimeSpan.FromSeconds(30))
            return;

        validationServerCheckInProgress = true;
        _ = CheckValidationServerHealthAsync();
    }

    private async Task CheckValidationServerHealthAsync()
    {
        try
        {
            using var response = await LicenseHttpClient.GetAsync(LicenseHealthEndpoint);
            validationServerConnected = response.IsSuccessStatusCode;
            if (!response.IsSuccessStatusCode)
            {
                log.Warning("License validation health check returned {StatusCode}.",
                    (int)response.StatusCode);
            }
        }
        finally
        {
            validationServerLastCheckedAt = DateTime.UtcNow;
            validationServerCheckInProgress = false;
        }
    }

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

    public string InteractableObjectScanStatus { get; private set; } = "尚未遍历可交互物体。";

    public string BaseIdNavigationTestStatus { get; private set; } = "尚未测试按 BaseID 坐标寻路。";

    public string EffectCenterTestStatus { get; private set; } = "尚未获取特效中心坐标。";

    public string FieldMarkerTestStatus { get; private set; } = "尚未获取场地标记坐标。";

    public string OtherPluginTestStatus { get; private set; } = "OtherPlugin feature not tested yet.";

    public string OtherPluginMarketStatus => openMarketAnywhere.Status;

    public float OtherPluginTestX => configuration.OtherPluginTestX;

    public float OtherPluginTestY => configuration.OtherPluginTestY;

    public float OtherPluginTestZ => configuration.OtherPluginTestZ;

    public bool IsAutoMapSupplementEnabled => configuration.AutoMapSupplementEnabled;

    public void SetOtherPluginTestCoordinates(float x, float y, float z)
    {
        configuration.OtherPluginTestX = x;
        configuration.OtherPluginTestY = y;
        configuration.OtherPluginTestZ = z;
        SaveConfiguration();
    }

    public void ReadOtherPluginCurrentCoordinate()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            if (coordinateApplier.ReadCurrent())
                SetOtherPluginTestCoordinates(coordinateApplier.X, coordinateApplier.Y, coordinateApplier.Z);

            OtherPluginTestStatus = coordinateApplier.Status;
        });
    }

    public void TestOtherPluginApplyCoordinate(float x, float y, float z)
    {
        SetOtherPluginTestCoordinates(x, y, z);
        _ = framework.RunOnFrameworkThread(() => ApplyOtherPluginCoordinateOnFrameworkThread(x, y, z));
    }

    public void TestOtherPluginOpenMarket()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            openMarketAnywhere.OpenCustomMarketUi();
            OtherPluginTestStatus = openMarketAnywhere.Status;
            log.Information("Opened custom market UI without native market board addons.");
        });
    }

    public void DrawCustomMarketUi() => openMarketAnywhere.DrawCustomMarketUi();

    private bool CanRunAutomationOrTest => IsAutoTreasureHuntEnabled || manualTestModeActive;

    public TreasureHuntLogicMode SelectedLogicMode => configuration.LogicMode;

    public bool IsHeadLogicSelected => SelectedLogicMode == TreasureHuntLogicMode.Head;

    public bool IsWheelLogicSelected => SelectedLogicMode == TreasureHuntLogicMode.Wheel;

    public string SelectedLogicModeName => IsHeadLogicSelected ? "车头" : "车轮";

    private TreasureMapRoute SelectedTreasureMapRoute => TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].Route;

    private bool IsRouletteMap => TreasureMapDefinitions.RouletteInstanceMapIds.Contains(clientState.MapId);

    public bool IsRouletteMode => IsRouletteMap && activeTreasureMapRoute == TreasureMapRoute.Roulette;

    public string CurrentLogicName => !IsAutoTreasureHuntEnabled
        ? $"{SelectedLogicModeName}（未运行）"
        : IsWheelLogicSelected
            ? "车轮 / 接受传送并前往红旗"
            : doorSelectionModeActive
                ? "车头 / 选门"
                : rouletteModeActive || IsRouletteMode
                ? "车头 / 转盘"
                : automaticMapSupplementRunning ||
                  marketBoardAfterTeleportPending ||
                  marketBoardInteractionPending
                    ? "车头 / 补图"
                    : "车头 / 野外挖宝";

    public int SelectedTreasureMapIndex => selectedTreasureMapIndex;

    public int TreasureMapOptionCount => TreasureMapDefinitions.HeadTreasureMapOptions.Count;

    public string SelectedTreasureMapName => TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].Name;

    public string SelectedTaskTreasureMapName => TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].TaskName;

    public string SelectedTreasureMapMarketSearchName => TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].MarketSearchName;

    public uint SelectedTreasureMapItemId => TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].MapItemId;

    public uint SelectedTaskTreasureMapItemId => TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].TaskItemId;

    public int MapSupplementMaxUnitPrice => Math.Max(1, configuration.MapSupplementMaxUnitPrice);

    public bool IsMapSupplementMaxUnitPriceEnabled => configuration.MapSupplementMaxUnitPriceEnabled;

    public void SetMapSupplementMaxUnitPriceEnabled(bool enabled)
    {
        configuration.MapSupplementMaxUnitPriceEnabled = enabled;
        pluginInterface.SavePluginConfig(configuration);
    }

    public void SetMapSupplementMaxUnitPrice(int value)
    {
        configuration.MapSupplementMaxUnitPrice = Math.Clamp(value, 1, 999999999);
        pluginInterface.SavePluginConfig(configuration);
    }

    public DoorSelectionChoice GetDoorSelectionChoice(int floor) => floor switch
    {
        0 => configuration.DoorSelectionFloor1To2,
        1 => configuration.DoorSelectionFloor2To3,
        2 => configuration.DoorSelectionFloor3To4,
        3 => configuration.DoorSelectionFloor4To5,
        _ => DoorSelectionChoice.Left,
    };

    public void SetDoorSelectionChoice(int floor, DoorSelectionChoice choice)
    {
        switch (floor)
        {
            case 0: configuration.DoorSelectionFloor1To2 = choice; break;
            case 1: configuration.DoorSelectionFloor2To3 = choice; break;
            case 2: configuration.DoorSelectionFloor3To4 = choice; break;
            case 3: configuration.DoorSelectionFloor4To5 = choice; break;
            default: return;
        }
        pluginInterface.SavePluginConfig(configuration);
    }

    public string SelectedTreasureMapRouteName => TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].Route switch
    {
        TreasureMapRoute.Roulette => "转盘",
        TreasureMapRoute.DoorSelection => "选门",
        _ => "未知",
    };

    public bool IsSelectedDoorSelectionRoute =>
        TreasureMapDefinitions.HeadTreasureMapOptions[selectedTreasureMapIndex].Route == TreasureMapRoute.DoorSelection;

    public string GetTreasureMapOptionName(int index) => TreasureMapDefinitions.HeadTreasureMapOptions[index].Name;

    public uint GetTreasureMapOptionItemId(int index) => TreasureMapDefinitions.HeadTreasureMapOptions[index].MapItemId;

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
        openMarketAnywhere.Dispose();
        gameGui.HoveredItemChanged -= globetrotterTreasureMapCore.OnHover;
        globetrotterTreasureMapCore.Dispose();
        commandManager.RemoveHandler("/lltp");
        commandManager.RemoveHandler("/llmarket");
        commandManager.RemoveHandler("/tmap");
    }

    private void OnTmapCommand(string command, string arguments)
    {
        if (CheckGlobetrotterRunning())
        {
            AutoTreasureHuntStatus = "外部 Globetrotter 已启用，已禁用 DigPlugin 内置藏宝图核心。";
            return;
        }

        globetrotterTreasureMapCore.OpenCurrentMapLocation();
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
        wheelWasInCombat = false;
        wheelDoorSelectionFloor = 1;
        wheelDoorSelectionDoorBaseId = 0;
        wheelDoorSelectionDoorEntityId = 0;
        wheelDoorSelectionDoorMoveIssued = false;
        wheelDoorSelectionVfxMoveIssued = false;
        wheelDoorSelectionAnimationUntil = default;
        wheelDoorSelectionLastMapId = 0;
        wheelDoorSelectionChestEntityId = 0;
        wheelDoorSelectionChestMoveIssued = false;
                ResetWheelTeleportAcceptance();
                ResetWheelMapLinkPending();
                ResetWheelMapLinkGenerations();
                AutoTreasureHuntStatus = "车轮逻辑已开启，正在等待传送请求。";
            }
        }
        else
        {
            if (rouletteModeActive)
            {
                ExitRouletteMode();
            }

            if (doorSelectionModeActive)
            {
                ExitDoorSelectionMode();
            }

            commandManager.ProcessCommand("/vnav stop");
            commandManager.ProcessCommand("/bmrai off");
            wheelWasInCombat = false;
            CloseWorkflowBlockingWindows();
            if (automaticMapSupplementRunning)
            {
                FailMapSupplement("自动挖宝已关闭。");
            }

            autoWaitingForTaskTreasureMap = false;
            ResetTreasureQuestSettlementGuard();
            waitingForAutomaticMapFlag = false;
            headFlagTeleportPending = false;
            headFlagTeleportReady = false;
            headFlagAnnouncementPending = false;
            headFlagPartyReadyAt = default;
            autoWaitingForSaddlebagMove = false;
            autoSaddlebagContextMenuPending = false;
            autoSaddlebagMoveRequested = false;
            ResetWheelTeleportAcceptance();
            ResetWheelMapLinkPending();
            ResetWheelMapLinkGenerations();
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

    public async Task<bool> ValidateCredentialAsync(string credential)
    {
        credentialValidationError = string.Empty;
        var hash = ComputeCredentialHash(credential.Trim());
        var role = GetCredentialRole(hash);

        // Developer and advanced credentials remain permanent local credentials.
        if (role is CredentialRole.Developer or CredentialRole.Advanced)
        {
            SetValidatedCredential(hash, role, credential.Trim());
            return true;
        }

        // Character.ContentId is game memory, so read it on the framework
        // thread before sending the value to the remote validator.
        var accountId = await framework.RunOnFrameworkThread(GetLocalAccountId);
        if (accountId == 0)
        {
            credentialValidationError = "无法读取当前账号信息";
            return false;
        }

        return await ValidateCredentialAgainstServerAsync(
            hash, credential.Trim(), accountId.ToString());
    }

    private async Task<bool> ValidateCredentialAgainstServerAsync(
        string hash, string credential, string accountId)
    {
        try
        {
            using var response = await LicenseHttpClient.PostAsJsonAsync(
                LicenseValidationEndpoint,
                new { credential, accountId });

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;

            if (!response.IsSuccessStatusCode)
            {
                credentialValidationError = root.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    && error.GetString() == "credential_bound_to_another_account"
                    ? "凭证绑定的不是此账号"
                    : "凭证无效";
                return false;
            }

            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean() ||
                !root.TryGetProperty("role", out var roleElement))
            {
                credentialValidationError = "凭证无效";
                return false;
            }

            var serverRole = roleElement.GetString() switch
            {
                "user" => CredentialRole.User,
                "advanced" => CredentialRole.Advanced,
                "developer" => CredentialRole.Developer,
                _ => CredentialRole.None,
            };

            if (serverRole == CredentialRole.None)
            {
                credentialValidationError = "凭证无效";
                return false;
            }

            SetValidatedCredential(hash, serverRole, credential);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Tencent Cloud license validation request failed.");
            credentialValidationError = "未连接至验证服务器";
            return false;
        }
    }

    private void SetValidatedCredential(string hash, CredentialRole role, string? credential = null)
    {
        sessionCredentialRole = role;
        configuration.CredentialHash = hash;
        configuration.CredentialRole = role;
        if (!string.IsNullOrWhiteSpace(credential))
            configuration.SavedCredential = credential.Trim();
        pluginInterface.SavePluginConfig(configuration);
    }

    private unsafe ulong GetLocalAccountId()
    {
        var localPlayer = objectTable.LocalPlayer;
        return localPlayer != null && localPlayer.Address != nint.Zero
            ? ((Character*)localPlayer.Address)->ContentId
            : 0;
    }

    public async Task<bool> ValidateRememberedCredentialAsync()
    {
        var savedCredential = configuration.SavedCredential?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(savedCredential))
            return await ValidateCredentialAsync(savedCredential);

        sessionCredentialRole = GetCredentialRole(configuration.CredentialHash);
        configuration.CredentialRole = sessionCredentialRole;
        return sessionCredentialRole is CredentialRole.Developer or CredentialRole.Advanced;
    }

    public void ClearCredential()
    {
        IsAutoTreasureHuntEnabled = false;
        UnloadOptimizedInteraction();
        sessionCredentialRole = CredentialRole.None;
    }

    private void ValidateSavedCredential()
    {
        var savedRole = GetCredentialRole(configuration.CredentialHash);
        // User credentials are server-bound and must be entered again on each
        // plugin start. Only permanent local credentials are restored.
        sessionCredentialRole = savedRole is CredentialRole.Advanced or CredentialRole.Developer
            ? savedRole
            : CredentialRole.None;
        configuration.CredentialRole = sessionCredentialRole;
    }

    private static CredentialRole GetCredentialRole(string credentialHash)
    {
        return credentialHash switch
        {
            DeveloperCredentialHash => CredentialRole.Developer,
            AdvancedCredentialHash => CredentialRole.Advanced,
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

        if (index < 0 || index >= TreasureMapDefinitions.HeadTreasureMapOptions.Count || index == selectedTreasureMapIndex)
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

    private void StartAutoTreasureHuntOnFrameworkThread()
    {
        if (!IsHeadLogicSelected ||
            !IsAutoTreasureHuntEnabled ||
            IsRouletteMode ||
            automaticMapSupplementRunning)
        {
            return;
        }

        if (treasureQuestSettlementPending && !IsTreasureQuestSettlementReady())
        {
            AutoTreasureHuntStatus = "第二次开箱后的藏宝图任务仍在结算，暂不解读下一张地图。";
            TeleportTestStatus = AutoTreasureHuntStatus;
            return;
        }

        if (TreasureMapDefinitions.DoorSelectionInstanceMapIds.Contains(clientState.MapId))
        {
            EnterDoorSelectionMode();
            return;
        }

        if (doorSelectionModeActive)
        {
            return;
        }

        if (condition[ConditionFlag.OccupiedInQuestEvent])
        {
            if (!treasureQuestSettlementPending)
            {
                BeginTreasureQuestSettlementGuard();
            }

            AutoTreasureHuntStatus = "当前仍处于藏宝图任务事件中，等待任务结算后再解读下一张地图或进入补图。";
            _ = framework.RunOnTick(
                StartAutoTreasureHuntOnFrameworkThread,
                delay: TimeSpan.FromSeconds(1));
            return;
        }

        activeTreasureMapRoute = SelectedTreasureMapRoute;
        RefreshTreasureMapCounts();
        if (HasTaskTreasureMap)
        {
            autoWaitingForTaskTreasureMap = false;
            if (!waitingForAutomaticMapFlag)
            {
                waitingForAutomaticMapFlag = true;
                autoMapCommandSent = false;
                autoMapFlagRetryQueued = false;
            }
            AutoTreasureHuntStatus = "任务道具中已有地图，跳过解读并开始传送流程。";
            _ = framework.RunOnFrameworkThread(TestTeleportToOpenedMapAetheryteOnFrameworkThread);
            return;
        }

        if (!HasTreasureMap)
        {
            autoWaitingForTaskTreasureMap = false;
            waitingForAutomaticMapFlag = false;
            if (IsAutoMapSupplementEnabled)
            {
                automaticMapSupplementTriggered = true;
                BeginMapSupplementLogic(resumeAutoHunt: true);
                return;
            }

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

        activeTreasureMapRoute = SelectedTreasureMapRoute;
        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        autoMapCommandSent = false;
        autoMapFlagRetryQueued = false;
        waitingForAutomaticMapFlag = false;
        headFlagTeleportPending = false;
        headFlagTeleportReady = false;
        headFlagAnnouncementPending = false;
        headFlagPartyReadyAt = default;
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
        ResumeAfterEmergencyStop();
        if (!IsWheelLogicSelected)
        {
            WheelMapLinkStatus = "当前不是车轮逻辑，无法测试聊天地图链接。";
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
        ResetTreasureQuestSettlementGuard();
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
        ResetTreasureQuestSettlementGuard();
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

    public void TestFindRouletteExitPoint()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            var exit = objectTable
                .Where(gameObject =>
                    gameObject.BaseId == RouletteExitBaseId &&
                    gameObject.IsTargetable)
                .OrderBy(gameObject => objectTable.LocalPlayer == null
                    ? float.PositiveInfinity
                    : System.Numerics.Vector3.DistanceSquared(
                        objectTable.LocalPlayer.Position,
                        gameObject.Position))
                .FirstOrDefault();

            if (exit == null)
            {
                TeleportTestStatus = $"测试退出点：当前未检测到可选中的退出点（BaseId {RouletteExitBaseId}）。";
                return;
            }

            targetManager.Target = exit;
            TeleportTestStatus = FormattableString.Invariant(
                $"测试退出点：已检测到可选中退出点，BaseId {exit.BaseId}，EntityId {exit.EntityId}，XYZ ({exit.Position.X:F3}, {exit.Position.Y:F3}, {exit.Position.Z:F3})。已设为当前目标，未执行交互。");
        });
    }

    public void TestListInteractableObjects()
    {
        TestListObjects(targetable: true, "可选中物体");
    }

    public void TestListNonTargetableObjects()
    {
        TestListObjects(targetable: false, "不可右键选中物体");
    }

    public void TestCaptureEffectCenter()
    {
        EffectCenterTestStatus = "正在扫描附近对象表，查找特效中心实体...";
        _ = framework.RunOnFrameworkThread(() =>
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
            {
                EffectCenterTestStatus = "当前无法取得角色对象，无法扫描特效中心。";
                return;
            }

            var candidates = objectTable
                .Where(gameObject => gameObject.EntityId != 0 &&
                    System.Numerics.Vector3.DistanceSquared(gameObject.Position, localPlayer.Position) <= 40f * 40f)
                .OrderBy(gameObject => !string.Equals(gameObject.ObjectKind.ToString(), "EventObj", StringComparison.OrdinalIgnoreCase))
                .ThenBy(gameObject => System.Numerics.Vector3.DistanceSquared(gameObject.Position, localPlayer.Position))
                .ToList();

            var candidate = candidates.FirstOrDefault(gameObject =>
                string.Equals(gameObject.ObjectKind.ToString(), "EventObj", StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(gameObject => !gameObject.IsTargetable);
            if (candidate == null)
            {
                EffectCenterTestStatus = "对象表中没有特效实体（无 EntityID、BaseID、可选中状态）。该目标属于场景特效/区域触发器，卫月 IObjectTable 无法提供真实 XYZ；未使用玩家坐标替代。";
                PrintEcho(EffectCenterTestStatus);
                return;
            }

            var position = candidate.Position;
            EffectCenterTestStatus = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "已找到特效候选：XYZ {0:F3}, {1:F3}, {2:F3}；EntityID {3}；BaseID {4}；类型 {5}；可选中 {6}",
                position.X, position.Y, position.Z, candidate.EntityId, candidate.BaseId,
                candidate.ObjectKind, candidate.IsTargetable);
            PrintEcho(EffectCenterTestStatus);
        });
    }

    public void TestCaptureFieldMarker()
    {
        FieldMarkerTestStatus = "正在读取当前地图的场地标记...";
        _ = framework.RunOnFrameworkThread(TestCaptureFieldMarkerOnFrameworkThread);
    }

    public void TestProbeVfxEffectCenter()
    {
        EffectCenterTestStatus = "正在扫描当前客户端的 VFX/场景特效结构...";
        _ = framework.RunOnFrameworkThread(() =>
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
            {
                EffectCenterTestStatus = "当前没有角色对象，无法确定最近 VFX 候选。";
                return;
            }

            EffectCenterTestStatus = vfxEffectCenterProbe.Capture(localPlayer.Position);
            PrintEcho(EffectCenterTestStatus);
        });
    }

    public void TestProbeBaseIdlessVfx()
    {
        EffectCenterTestStatus = "正在扫描无 BaseID 的 VFX 场景特效...";
        _ = framework.RunOnFrameworkThread(() =>
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
            {
                EffectCenterTestStatus = "当前没有角色对象，无法确定最近的无 BaseID VFX。";
                return;
            }

            if (!vfxEffectCenterProbe.TryGetNearestBaseIdlessPosition(localPlayer.Position, out var position, out var count))
            {
                EffectCenterTestStatus = "当前没有检测到活动的无 BaseID VFX 实例。";
                PrintEcho(EffectCenterTestStatus);
                return;
            }

            EffectCenterTestStatus = $"已找到无 BaseID VFX：活动实例 {count} 个；最近中心 XYZ ({position.X:F3}, {position.Y:F3}, {position.Z:F3})。";
            PrintEcho(EffectCenterTestStatus);
        });
    }

    public void TestProbeVfxResourcePaths()
    {
        EffectCenterTestStatus = "正在读取附近 VFX 的 .avfx 资源路径...";
        _ = framework.RunOnFrameworkThread(() =>
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
            {
                EffectCenterTestStatus = "当前没有角色对象，无法读取 VFX 资源路径。";
                return;
            }

            EffectCenterTestStatus = vfxEffectCenterProbe.CaptureNearestPaths(localPlayer.Position);
            PrintEcho(EffectCenterTestStatus);
        });
    }

    public void TestNavigateToDoorSelectionPortalVfx()
    {
        EffectCenterTestStatus = "正在查找传送阵 VFX 中心并启动 vnavmesh 导航...";
        _ = framework.RunOnFrameworkThread(() =>
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
            {
                EffectCenterTestStatus = "当前没有角色对象，无法导航到传送阵 VFX。";
                PrintEcho(EffectCenterTestStatus);
                return;
            }

            if (!vfxEffectCenterProbe.TryGetNearestDoorSelectionPortalPosition(localPlayer.Position, out var position, out var count))
            {
                EffectCenterTestStatus = "当前没有检测到已确认的传送阵 VFX。";
                PrintEcho(EffectCenterTestStatus);
                return;
            }

            var command = FormattableString.Invariant($"/vnav moveto {position.X:F3} {position.Y:F3} {position.Z:F3}");
            var accepted = commandManager.ProcessCommand(command);
            EffectCenterTestStatus = accepted
                ? $"已找到传送阵 VFX（匹配实例 {count} 个），中心 XYZ ({position.X:F3}, {position.Y:F3}, {position.Z:F3})；已执行 {command}。"
                : $"已找到传送阵 VFX，中心 XYZ ({position.X:F3}, {position.Y:F3}, {position.Z:F3})，但命令执行失败。";
            PrintEcho(EffectCenterTestStatus);
        });
    }

    private unsafe void TestCaptureFieldMarkerOnFrameworkThread()
    {
        var mapAgent = AgentMap.Instance();
        if (mapAgent == null || mapAgent->FlagMarkerCount == 0)
        {
            FieldMarkerTestStatus = "当前没有可读取的场地地图标记（AgentMap.FlagMarkerCount=0）。未使用玩家坐标替代。";
            PrintEcho(FieldMarkerTestStatus);
            return;
        }

        var marker = mapAgent->FlagMapMarkers[0];
        if (marker.TerritoryId == 0 || marker.MapId == 0)
        {
            FieldMarkerTestStatus = "检测到场地标记数据，但区域或地图 ID 无效，无法换算 XYZ。";
            PrintEcho(FieldMarkerTestStatus);
            return;
        }

        FieldMarkerTestStatus = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "已读取场地地图标记：区域 {0}，地图 {1}，X {2:F3}，Y {3:F3}（Z 轴不在 AgentMap 标记数据中）。",
            marker.TerritoryId, marker.MapId, marker.XFloat, marker.YFloat);
        PrintEcho(FieldMarkerTestStatus);
    }

    public void TestNavigateToBaseId(string baseIdText)
    {
        ResumeAfterEmergencyStop();
        if (!uint.TryParse(baseIdText.Trim(), out var baseId) || baseId == 0)
        {
            BaseIdNavigationTestStatus = "BaseID 必须是大于 0 的整数。";
            return;
        }

        BaseIdNavigationTestStatus = $"正在从卫月对象表查找 BaseID {baseId}。";
        _ = framework.RunOnFrameworkThread(() =>
        {
            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null)
            {
                BaseIdNavigationTestStatus = "卫月对象表暂时无法取得角色对象。";
                return;
            }

            var target = objectTable
                .Where(gameObject => gameObject.BaseId == baseId && gameObject.EntityId != 0)
                .OrderBy(gameObject =>
                    System.Numerics.Vector3.DistanceSquared(localPlayer.Position, gameObject.Position))
                .FirstOrDefault();
            if (target == null)
            {
                BaseIdNavigationTestStatus = $"当前对象表中没有实体存在的 BaseID {baseId}。";
                return;
            }

            var position = target.Position;
            var command = FormattableString.Invariant(
                $"/vnav moveto {position.X:F3} {position.Y:F3} {position.Z:F3}");
            targetManager.Target = target;
            var accepted = commandManager.ProcessCommand(command);
            BaseIdNavigationTestStatus = accepted
                ? $"已找到 BaseID {baseId}（Entity ID {target.EntityId}），XYZ：{position.X:F3}, {position.Y:F3}, {position.Z:F3}；已执行 {command}。"
                : $"已找到 BaseID {baseId}，XYZ：{position.X:F3}, {position.Y:F3}, {position.Z:F3}；但命令 {command} 执行失败。";
        });
    }

    private void TestListObjects(bool targetable, string category)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            ResumeAfterEmergencyStop();
            interactableObjectEchoGeneration++;
            var generation = interactableObjectEchoGeneration;
            interactableObjectEchoQueue.Clear();
            interactableObjectEchoCategory = category;

            var objects = objectTable
                .Where(gameObject =>
                    gameObject.IsTargetable == targetable &&
                    gameObject.EntityId != 0 &&
                    (!targetable || gameObject.BaseId != 0))
                .Select(gameObject => new
                {
                    Name = gameObject.Name.TextValue,
                    gameObject.EntityId,
                    gameObject.BaseId,
                    ObjectKind = gameObject.ObjectKind.ToString(),
                })
                .OrderBy(gameObject => gameObject.ObjectKind)
                .ThenBy(gameObject => gameObject.BaseId)
                .ThenBy(gameObject => gameObject.EntityId)
                .ToList();

            foreach (var gameObject in objects)
            {
                var name = string.IsNullOrWhiteSpace(gameObject.Name)
                    ? "无名称物体"
                    : gameObject.Name.Replace('\r', ' ').Replace('\n', ' ').Trim();
                interactableObjectEchoQueue.Enqueue(
                    $"{name} 类型:{gameObject.ObjectKind} ID:{gameObject.EntityId} BaseID:[{gameObject.BaseId}]");
            }

            interactableObjectEchoTotal = interactableObjectEchoQueue.Count;
            if (interactableObjectEchoTotal == 0)
            {
                InteractableObjectScanStatus = $"当前对象表中没有{category}。";
                PrintEcho(InteractableObjectScanStatus);
                return;
            }

            InteractableObjectScanStatus =
                $"已找到 {interactableObjectEchoTotal} 个{category}，正在通过 /e 逐条输出。";
            SendNextInteractableObjectEcho(generation);
        });
    }

    private void SendNextInteractableObjectEcho(int generation)
    {
        if (generation != interactableObjectEchoGeneration || emergencyStopActive)
        {
            return;
        }

        if (interactableObjectEchoQueue.Count == 0)
        {
            InteractableObjectScanStatus =
                $"遍历完成，已通过 /e 输出 {interactableObjectEchoTotal} 个{interactableObjectEchoCategory}。";
            return;
        }

        var message = interactableObjectEchoQueue.Dequeue();
        if (!PrintEcho(message))
        {
            interactableObjectEchoQueue.Clear();
            InteractableObjectScanStatus = "游戏聊天组件暂不可用，/e 输出已停止。";
            return;
        }
        var sent = interactableObjectEchoTotal - interactableObjectEchoQueue.Count;
        InteractableObjectScanStatus =
            $"正在输出{interactableObjectEchoCategory}：{sent}/{interactableObjectEchoTotal}。";
        _ = framework.RunOnTick(
            () => SendNextInteractableObjectEcho(generation),
            delay: TimeSpan.FromMilliseconds(250));
    }

    private bool PrintEcho(string message)
    {
        return TrySendChatBoxEntry($"/e {message}");
    }

    public void TestMapSupplementLogic()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            ResumeAfterEmergencyStop();
            BeginMapSupplementLogic(resumeAutoHunt: false);
        });
    }

    public void TestPurchaseSelectedMap()
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            ResumeAfterEmergencyStop();
            BeginMapSupplementLogic(resumeAutoHunt: false);
        });
    }

    public unsafe void EmergencyStop()
    {
        emergencyStopActive = true;
        manualTestModeActive = false;
        interactableObjectEchoGeneration++;
        interactableObjectEchoQueue.Clear();
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
        waitingForAutomaticMapFlag = false;
        headFlagTeleportPending = false;
        headFlagTeleportReady = false;
        headFlagAnnouncementPending = false;
        headFlagPartyReadyAt = default;
        saddlebagStoreTestPending = false;
        saddlebagStoreMoveRequested = false;
        saddlebagStoreContextMenuPending = false;
        saddlebagTakeTestPending = false;
        saddlebagTakeMoveRequested = false;
        saddlebagTakeContextMenuPending = false;
        marketSubmittedListingIds.Clear();
        ResetWheelTeleportAcceptance();
        ResetWheelMapLinkPending();
        ResetWheelMapLinkGenerations();
        wheelWasInCombat = false;

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
        ResetTreasureQuestSettlementGuard();

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
        doorSelectionPortalEntryPending = false;
        doorSelectionModeActive = false;
        doorSelectionInstanceMapId = 0;
        doorSelectionWasInCombat = false;
        doorSelectionPostCombatChestHandled = false;
        doorSelectionFloor = 1;
        doorSelectionDoorEntityId = 0;
        doorSelectionDoorMoveIssued = false;
        doorSelectionDoorAnimationUntil = default;
        doorSelectionVfxMoveIssued = false;
        ResetDoorSelectionChestState(resetCompleted: true);

        marketBoardAfterTeleportPending = false;
        marketBoardTeleportRetryQueued = false;
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
        manualTestModeActive = true;
    }

    private unsafe void StartMarketBoardTestOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive || !CanRunAutomationOrTest || IsRouletteMode)
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
            marketBoardTeleportRetryQueued = false;
            marketBoardAfterTeleportPending = false;
            MoveToMarketBoardOnFrameworkThread();
            return;
        }

        if (objectTable.LocalPlayer == null ||
            condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.InCombat] ||
            condition[ConditionFlag.OccupiedInQuestEvent])
        {
            TeleportTestStatus = "当前状态暂时不能传送，正在通过卫月框架下一帧重试前往市场。";
            ScheduleMarketBoardTeleportAttempt(TimeSpan.FromSeconds(1));
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
            TeleportTestStatus = "卫月暂时无法获取游戏传送组件，正在主动重试。";
            ScheduleMarketBoardTeleportAttempt(TimeSpan.FromSeconds(1));
            return;
        }

        if (telepo->ActiveTeleportRequest)
        {
            TeleportTestStatus = "游戏已有传送请求正在处理，等待完成后重试前往市场。";
            ScheduleMarketBoardTeleportAttempt(TimeSpan.FromSeconds(1));
            return;
        }

        if (!telepo->Teleport(limsaAetheryte.AetheryteId, limsaAetheryte.SubIndex))
        {
            TeleportTestStatus = "游戏暂未接受前往市场的传送请求，正在通过卫月框架主动重试。";
            ScheduleMarketBoardTeleportAttempt(TimeSpan.FromSeconds(1));
            return;
        }

        marketBoardTeleportRetryQueued = false;
        marketBoardAfterTeleportPending = true;
        TeleportTestStatus = "已请求传送到利姆萨·罗敏萨，等待切区后前往市场布告板。";
    }

    private void ScheduleMarketBoardTeleportAttempt(TimeSpan delay)
    {
        if (marketBoardTeleportRetryQueued ||
            !automaticMapSupplementRunning ||
            emergencyStopActive ||
            !CanRunAutomationOrTest)
        {
            return;
        }

        marketBoardTeleportRetryQueued = true;
        _ = framework.RunOnTick(
            () =>
            {
                if (!marketBoardTeleportRetryQueued)
                {
                    return;
                }

                marketBoardTeleportRetryQueued = false;
                if (automaticMapSupplementRunning &&
                    CanRunAutomationOrTest &&
                    !emergencyStopActive)
                {
                    StartMarketBoardTestOnFrameworkThread();
                }
            },
            delay: delay);
    }

    private void MoveToMarketBoardOnFrameworkThread()
    {
        if (!IsHeadLogicSelected || emergencyStopActive || !CanRunAutomationOrTest || IsRouletteMode)
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
                var searchAgent = AgentItemSearch.Instance();
                if (searchAddon != null &&
                    searchAddon->IsReady &&
                    searchAddon->ResultsList != null &&
                    searchAddon->SearchTextInput != null &&
                    searchAgent != null)
                {
                    marketBoardInteractionPending = false;
                    marketBoardInteractionAttempted = false;
                    marketBoardPositionSampleValid = false;
                    marketPurchaseItemId = SelectedTreasureMapItemId;
                    marketPurchaseItemName = SelectedTreasureMapMarketSearchName;
                    marketPurchaseInitialMainCount = CountTreasureMaps(MainInventoryTypes, marketPurchaseItemId);
                    marketPurchaseAutomatic = automaticMapSupplementRunning;
                    marketPurchaseStage = MarketPurchaseStage.WaitingForSearchAddon;
                    marketSearchRunAt = DateTime.UtcNow + MarketBoardOpenStabilizationDelay;
                    marketPurchaseDeadline = DateTime.UtcNow.AddSeconds(25);
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
        if (interactionResult == 0)
        {
            marketBoardInteractionAttempted = false;
            marketBoardInteractionRetryAt = DateTime.UtcNow.AddMilliseconds(250);
            marketBoardPositionStableSince = DateTime.UtcNow;
            TeleportTestStatus = "市场布告板交互调用未成功，站稳后重试。";
            return;
        }

        marketBoardInteractionAttempted = true;
        marketBoardInteractionRetryAt = DateTime.UtcNow.AddSeconds(2);
        TeleportTestStatus = $"卫月已调用市场布告板交互（返回值：{interactionResult}），等待确认市场窗口打开。";
    }

    private void ResetMarketPurchase()
    {
        marketPurchaseStage = MarketPurchaseStage.None;
        marketPurchaseDeadline = default;
        marketSearchRunAt = default;
        marketPricePageWaitSince = default;
        marketPricePageReadySince = default;
        marketPricePageRetryCount = 0;
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
                marketPricePageWaitSince = DateTime.UtcNow;
                marketPricePageReadySince = default;
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
            if (infoProxy->WaitingForListings || infoProxy->ListingCount == 0)
            {
                if (TryRetryMarketPricePage("价格页面已打开，正在等待卫月报价接口返回第一条报价。"))
                {
                    return;
                }

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

            if (marketPurchaseAutomatic &&
                IsMapSupplementMaxUnitPriceEnabled &&
                purchase.UnitPrice > MapSupplementMaxUnitPrice)
            {
                HandleUnaffordableMapSupplement(
                    $"{marketPurchaseItemName}最低报价 {purchase.UnitPrice:N0} 金币，超过补图最高单价 {MapSupplementMaxUnitPrice:N0} 金币。");
                return;
            }

            if (marketSubmittedListingIds.Contains(purchase.ListingId))
            {
                TryRetryMarketPricePage($"卫月仍返回本轮已经提交过的{marketPurchaseItemName}报价，正在等待市场刷新。" );
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

        if (marketPricePageReadySince == default)
        {
            marketPricePageReadySince = DateTime.UtcNow;
            TeleportTestStatus = "有效报价已经读取，等待报价稳定后通过卫月接口购买。";
            return;
        }

        if (DateTime.UtcNow - marketPricePageReadySince < TimeSpan.FromSeconds(1))
        {
            return;
        }

        if (!infoProxy->WaitingForListings && infoProxy->ListingCount > 0)
        {
            var currentListing = infoProxy->Listings[0];
            if (currentListing.ListingId != marketPurchaseListingSnapshot.ListingId ||
                currentListing.ItemId != marketPurchaseListingSnapshot.ItemId ||
                currentListing.Quantity != marketPurchaseListingSnapshot.Quantity ||
                currentListing.UnitPrice != marketPurchaseListingSnapshot.UnitPrice)
            {
                marketPurchaseSnapshotReady = false;
                marketPricePageReadySince = default;
                TeleportTestStatus = "最低价报价在购买前发生变化，正在通过卫月接口重新读取报价。";
                return;
            }
        }

        var purchaseRequest = marketPurchaseListingSnapshot;
        if (!infoProxy->SetLastPurchasedItem(&purchaseRequest))
        {
            marketPricePageReadySince = default;
            if (TryRetryMarketPricePage("卫月报价尚未被市场代理接受，正在重建市场会话后重试。"))
            {
                return;
            }

            TeleportTestStatus = "卫月接口尚未接受最低价报价，等待价格页稳定后重试。";
            return;
        }

        if (!infoProxy->SendPurchaseRequestPacket())
        {
            marketPricePageReadySince = default;
            if (TryRetryMarketPricePage("卫月提交购买请求失败，正在重建市场会话后重试。"))
            {
                return;
            }

            TeleportTestStatus = "卫月接口暂未提交购买请求，等待一秒后在当前价格页重试。";
            return;
        }

        marketSubmittedListingIds.Add(marketPurchaseListingSnapshot.ListingId);
        marketPurchaseStage = MarketPurchaseStage.WaitingForDelivery;
        marketPurchaseDeadline = DateTime.UtcNow.AddSeconds(15);
        TeleportTestStatus = $"已通过卫月接口提交 1 张{marketPurchaseItemName}的购买请求，最低单价 {marketPurchaseSavedUnitPrice:N0} 金币，等待进入主背包。";
        AutoTreasureHuntStatus = TeleportTestStatus;
    }

    private bool TryRetryMarketPricePage(string waitingStatus)
    {
        TeleportTestStatus = waitingStatus;
        if (marketPricePageWaitSince == default)
        {
            marketPricePageWaitSince = DateTime.UtcNow;
        }

        if (DateTime.UtcNow - marketPricePageWaitSince < TimeSpan.FromSeconds(5) ||
            marketPricePageRetryCount >= 2)
        {
            return false;
        }

        marketPricePageRetryCount++;
        RetryMarketPurchaseInteraction();
        return true;
    }

    private void RetryMarketPurchaseInteraction()
    {
        var automatic = automaticMapSupplementRunning;
        var purchaseStep = mapSupplementPurchaseStep;
        var retryCount = marketPricePageRetryCount;
        CloseWorkflowBlockingWindows();
        ResetMarketPurchase();
        marketPricePageRetryCount = retryCount;
        marketBoardAfterTeleportPending = false;
        marketBoardInteractionPending = true;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;

        if (automatic)
        {
            mapSupplementStage = purchaseStep switch
            {
                1 => MapSupplementStage.WaitingForFirstPurchase,
                2 => MapSupplementStage.WaitingForSecondPurchase,
                3 => MapSupplementStage.WaitingForThirdPurchase,
                _ => MapSupplementStage.WaitingForFirstPurchase,
            };
            mapSupplementDeadline = DateTime.UtcNow.AddSeconds(30);
        }

        AutoTreasureHuntStatus = $"市场价格页加载未稳定，正在第 {marketPricePageRetryCount} 次主动重试购买，不等待总防卡恢复。";
        TeleportTestStatus = AutoTreasureHuntStatus;
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

    private void HandleUnaffordableMapSupplement(string reason)
    {
        RefreshTreasureMapCounts();
        var hasSelectedMap = HasTreasureMap || HasTaskTreasureMap;
        var resumeAutoHunt = mapSupplementResumeAutoHunt && IsAutoTreasureHuntEnabled;

        FailMarketPurchase($"补图已停止：{reason}");

        if (!resumeAutoHunt)
            return;

        if (!hasSelectedMap)
        {
            SetAutoTreasureHuntEnabled(false);
            AutoTreasureHuntStatus = $"补图已停止：{reason}没有价格符合上限的地图，且当前没有可用地图，已关闭自动挖宝。";
            TeleportTestStatus = AutoTreasureHuntStatus;
            return;
        }

        AutoTreasureHuntStatus = $"补图已停止：{reason}当前已有地图，结束补图并继续野外挖宝。";
        TeleportTestStatus = AutoTreasureHuntStatus;
        _ = framework.RunOnTick(
            StartAutoTreasureHuntOnFrameworkThread,
            delay: TimeSpan.FromSeconds(1));
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

        if (HasTreasureMap || HasTaskTreasureMap)
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
        if (!CanRunAutomationOrTest || !IsHeadLogicSelected || emergencyStopActive)
        {
            automaticMapSupplementTriggered = false;
            TeleportTestStatus = "购买三张地图测试只能在车头模式且未紧急停止时运行。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            return;
        }

        if (condition[ConditionFlag.OccupiedInQuestEvent])
        {
            automaticMapSupplementTriggered = false;
            TeleportTestStatus = "当前仍处于藏宝图任务事件中，任务结算前不会进入补图逻辑。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            return;
        }

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
        CloseWorkflowBlockingWindows();
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
        marketBoardTeleportRetryQueued = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        automaticMapSupplementRunning = true;
        workflowWatchdogState = string.Empty;
        workflowWatchdogStateSince = default;
        mapSupplementStage = MapSupplementStage.TravelingToBoard;
        mapSupplementPurchaseStep = 1;
        mapSupplementResumeAutoHunt = resumeAutoHunt;
        mapSupplementActionAt = default;
        mapSupplementDeadline = DateTime.UtcNow.AddMinutes(2);
        TeleportTestStatus = "补图逻辑：正在前往利姆萨·罗敏萨市场布告板购买第 1 张地图。";
        AutoTreasureHuntStatus = TeleportTestStatus;
        StartMarketBoardTestOnFrameworkThread();
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
        automaticMapSupplementRunning = false;
        automaticMapSupplementTriggered = false;
        mapSupplementStage = MapSupplementStage.None;
        mapSupplementPurchaseStep = 0;
        mapSupplementActionAt = default;
        mapSupplementDeadline = default;
        mapSupplementResumeAutoHunt = false;
        marketBoardAfterTeleportPending = false;
        marketBoardTeleportRetryQueued = false;
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
        automaticMapSupplementRunning = false;
        automaticMapSupplementTriggered = false;
        mapSupplementStage = MapSupplementStage.None;
        mapSupplementPurchaseStep = 0;
        mapSupplementActionAt = default;
        mapSupplementDeadline = default;
        mapSupplementResumeAutoHunt = false;
        marketBoardAfterTeleportPending = false;
        marketBoardTeleportRetryQueued = false;
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

    private bool TryHandleWheelWorkflowWatchdog()
    {
        var state = GetWheelWorkflowWatchdogState(out var timeout);
        if (string.IsNullOrEmpty(state))
        {
            wheelWatchdogState = string.Empty;
            wheelWatchdogStateSince = default;
            return false;
        }

        if (!string.Equals(state, wheelWatchdogState, StringComparison.Ordinal))
        {
            wheelWatchdogState = state;
            wheelWatchdogStateSince = DateTime.UtcNow;
            return false;
        }

        if (DateTime.UtcNow < wheelWatchdogCooldownUntil ||
            DateTime.UtcNow - wheelWatchdogStateSince < timeout)
            return false;

        RecoverStuckWheelWorkflow(state);
        wheelWatchdogState = string.Empty;
        wheelWatchdogStateSince = default;
        wheelWatchdogCooldownUntil = DateTime.UtcNow.AddSeconds(5);
        return true;
    }

    private string GetWheelWorkflowWatchdogState(out TimeSpan timeout)
    {
        timeout = TimeSpan.FromSeconds(45);
        if (!IsWheelLogicSelected || emergencyStopActive || condition[ConditionFlag.InCombat])
            return string.Empty;

        if (wheelPendingMapLink != null)
        {
            timeout = TimeSpan.FromSeconds(20);
            return "wheel-map-link";
        }

        if (wheelAwaitingMapChangeAndFlag)
        {
            timeout = wheelTeleportAcceptSubmitted
                ? TimeSpan.FromSeconds(60)
                : TimeSpan.FromSeconds(30);
            return $"wheel-teleport-flag:{wheelTeleportAcceptSubmitted}:{wheelNewFlagPending}:{wheelWaitingForFreshFlag}:{clientState.MapId}";
        }

        if (mountAfterTeleportPending)
        {
            timeout = TimeSpan.FromSeconds(45);
            return $"wheel-mount:{wheelFlagRound}:{condition[ConditionFlag.Mounted]}:{mountRetryQueued}";
        }

        if (dismountAtFlagPending)
        {
            timeout = TimeSpan.FromSeconds(90);
            return $"wheel-dismount:{navigationMovementObserved}:{condition[ConditionFlag.Mounted]}";
        }

        return string.Empty;
    }

    private void RecoverStuckWheelWorkflow(string state)
    {
        commandManager.ProcessCommand("/vnav stop");

        if (state == "wheel-map-link")
        {
            wheelPendingMapLinkDeadline = DateTime.UtcNow.AddSeconds(20);
            AutoTreasureHuntStatus = "车轮防卡：地图链接处理超时，重新尝试设置红旗。";
            _ = framework.RunOnTick(TryProcessWheelMapLink, delay: TimeSpan.FromMilliseconds(250));
            return;
        }

        if (state.StartsWith("wheel-teleport-flag:", StringComparison.Ordinal))
        {
            wheelFlagReadyAt = default;
            wheelFlagRefreshRequestedAt = DateTime.UtcNow;
            wheelWaitingForFreshFlag = true;
            wheelNewFlagPending = true;
            if (!wheelTeleportAcceptSubmitted)
                wheelTeleportAcceptAt = default;
            AutoTreasureHuntStatus = "车轮防卡：传送或红旗刷新超时，重新检查传送状态和红旗。";
            return;
        }

        if (state.StartsWith("wheel-mount:", StringComparison.Ordinal))
        {
            mountAfterTeleportPending = true;
            mountRetryQueued = false;
            mountAttemptCount = 0;
            navigationPositionSampleValid = false;
            navigationMovementObserved = false;
            AutoTreasureHuntStatus = "车轮防卡：坐骑流程超时，重新执行随机坐骑和红旗寻路。";
            UseMountRouletteAfterTeleportOnFrameworkThread();
            return;
        }

        if (state.StartsWith("wheel-dismount:", StringComparison.Ordinal))
        {
            navigationPositionSampleValid = false;
            navigationMovementObserved = false;
            AutoTreasureHuntStatus = "车轮防卡：红旗寻路或下坐骑流程超时，重新执行红旗寻路。";
            commandManager.ProcessCommand("/vnav flyflag");
        }
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
            if (!confirmNextTreasureChestInteraction && TryGetPartyMemberInCombat(out _))
            {
                return string.Empty;
            }

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
            if (!confirmNextTreasureChestInteraction && TryGetPartyMemberInCombat(out _))
            {
                return string.Empty;
            }

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
            marketBoardTeleportRetryQueued = false;
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
        ResetTreasureQuestSettlementGuard();
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
        marketBoardTeleportRetryQueued = false;
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

    /// <summary>
    /// 独立的车轮野外流程入口。车轮的传送、红旗、watchdog 和寻路调度
    /// 在这里集中处理，底层坐骑和下坐骑动作仍复用现有实现。
    /// </summary>
    private void TryHandleWheelOutdoorNavigation()
    {
        if (!IsWheelLogicSelected || emergencyStopActive)
            return;

        // 车轮进入转盘副本地图时改由退出点流程接管，不继续运行野外红旗/坐骑寻路。
        // 车轮不处理宝箱或潜网巡梦，避免干预车头的副本交互。
        if (IsRouletteMap)
        {
            TryHandleWheelRouletteExit();
            return;
        }

        // 离开转盘副本后释放转盘目标、交互和 AEAssist 状态，再回到车轮野外流程。
        if (rouletteModeActive)
        {
            ExitRouletteMode();
        }

        TryHandleWheelCombatState();
        TryProcessWheelMapLink();
        TryHandleWheelLogic();
        TryHandleWheelPostTeleport();

        if (TryHandleMovementWatchdog())
            return;

        TryHandleWheelDoorSelection();
        TryDismountAtFlag();
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        openMarketAnywhere.Update();

        if (!IsCredentialValidated || !CanRunAutomationOrTest)
        {
            return;
        }

        if (emergencyStopActive)
        {
            return;
        }

        if (!IsAutoTreasureHuntEnabled && manualTestModeActive)
        {
            TryHandleManualTestUpdate();
            return;
        }

        if (IsWheelLogicSelected)
        {
            TryHandleWheelOutdoorNavigation();
            return;
        }

        if (false)
        {
            /*
            TryHandleWheelCombatState();

            // 传送接受后的地图/红旗状态推进必须优先于 watchdog。watchdog 可能
            // 因上一阶段残留的移动状态返回 true，从而阻止这里启动坐骑和 vnav。
            TryProcessWheelMapLink();
            TryHandleWheelLogic();
            TryHandleWheelPostTeleport();

            if (TryHandleMovementWatchdog())
            {
                return;
            }

            if (TryHandleWheelWorkflowWatchdog())
            {
                return;
            }

            TryHandleWheelDoorSelection();
            TryDismountAtFlag();
            return;
        }
        */
        }

        if (!IsHeadLogicSelected)
        {
            return;
        }

        if (doorSelectionModeActive)
        {
            TryHandleDoorSelectionMode();
            return;
        }

        // 转盘地图必须优先于补图、鞍囊和野外流程接管。
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

    private void TryHandleManualTestUpdate()
    {
        // 手动测试同样优先推进车轮传送状态，避免通用 watchdog 抢先返回。
        if (IsWheelLogicSelected)
        {
            TryProcessWheelMapLink();
            TryHandleWheelLogic();
            TryHandleWheelPostTeleport();
        }

        if (TryHandleMovementWatchdog() || TryHandleWorkflowWatchdog())
        {
            return;
        }

        if (IsWheelLogicSelected)
        {
            TryDismountAtFlag();
            return;
        }

        if (doorSelectionModeActive)
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

        TryInteractWithMarketBoardAfterArrival();
        TryHandleMarketPurchase();
        TrySelectPendingMap();
        TryConfirmPendingMap();
        TryConfirmTreasureChest();
        TryConfirmTreasurePortal();
        TryHandleTreasureCombat();
        TryDismountAtFlag();
        TryHandleTreasureChest();
        TryHandleTreasurePortal();
    }

    private void TryHandleWheelCombatState()
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat && !wheelWasInCombat)
        {
            commandManager.ProcessCommand("/bmrai on");
            AutoTreasureHuntStatus = "车轮：进入战斗，已执行 /bmrai on。";
        }
        else if (!inCombat && wheelWasInCombat)
        {
            commandManager.ProcessCommand("/bmrai off");
            AutoTreasureHuntStatus = "车轮：脱离战斗，已执行 /bmrai off。";
        }

        wheelWasInCombat = inCombat;
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (!IsWheelLogicSelected ||
            emergencyStopActive ||
            message.LogKind is not (XivChatType.Party or XivChatType.CrossParty))
        {
            return;
        }

        TryCaptureWheelDoorSelectionMessage(message);

        var mapLink = message.Message.Payloads
            .OfType<MapLinkPayload>()
            .FirstOrDefault();
        if (mapLink == null)
        {
            return;
        }

        // A new party flag cancels every pending action from the prior flag.
        // This remains reliable even when two map links have identical coordinates.
        wheelMapLinkGeneration = wheelMapLinkGeneration == int.MaxValue
            ? 1
            : wheelMapLinkGeneration + 1;
        ResetWheelMapLinkPending();
        commandManager.ProcessCommand("/vnav stop");
        wheelLastMapLink = CloneMapLink(mapLink);
        // 每个聊天地图链接都开始新的车轮红旗轮次，清除地图 12
        // 或上一轮流程遗留的完成状态。
        wheelLastProcessedRound = -1;
        wheelFlagSnapshotValid = false;
        wheelFlagReadyAt = default;
        wheelTeleportAcceptSubmitted = false;
        wheelTeleportAcceptedPrompt = string.Empty;
        if (!CanRunAutomationOrTest)
        {
            WheelMapLinkStatus = $"已缓存聊天地图链接：{mapLink.PlaceName} {mapLink.CoordinateString}，可点击测试按钮读取。";
            return;
        }

        wheelPendingMapLink = CloneMapLink(mapLink);
        wheelPendingMapLinkGeneration = wheelMapLinkGeneration;
        wheelPendingMapLinkDeadline = DateTime.UtcNow.AddSeconds(20);
        WheelMapLinkStatus = $"检测到聊天地图链接：{mapLink.PlaceName} {mapLink.CoordinateString}，准备设置红旗。";
        AutoTreasureHuntStatus = $"车轮：检测到聊天地图链接 {mapLink.PlaceName} {mapLink.CoordinateString}，正在获取红旗。";
    }

    private void TryCaptureWheelDoorSelectionMessage(IChatMessage message)
    {
        var text = message.Message.TextValue;
        const string prefix = "<digdoor:896:";
        var start = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return;
        var end = text.IndexOf('>', start + prefix.Length);
        if (end < 0 || !uint.TryParse(text[(start + prefix.Length)..end], out var baseId) || baseId == 0)
            return;

        wheelDoorSelectionDoorBaseId = baseId;
        wheelDoorSelectionFloor = Math.Clamp(wheelDoorSelectionFloor, 1, 5);
        wheelDoorSelectionDoorEntityId = 0;
        wheelDoorSelectionDoorMoveIssued = false;
        wheelDoorSelectionAnimationUntil = DateTime.UtcNow.AddSeconds(2);
        wheelDoorSelectionVfxMoveIssued = false;
        AutoTreasureHuntStatus = $"车轮：收到车头第 {wheelDoorSelectionFloor} 层门信息（BaseID {baseId}），等待门动画结束。";
    }

    private unsafe void TryProcessWheelMapLink()
    {
        if (wheelPendingMapLink == null)
        {
            return;
        }

        if (wheelPendingMapLinkGeneration == 0 ||
            wheelPendingMapLinkGeneration != wheelMapLinkGeneration)
        {
            wheelPendingMapLink = null;
            wheelPendingMapLinkDeadline = default;
            wheelPendingMapLinkGeneration = 0;
            return;
        }

        if (condition[ConditionFlag.BetweenAreas] ||
            condition[ConditionFlag.BetweenAreas51])
        {
            return;
        }

        // 传送确认尚未提交时，不能提前打开地图或刷新红旗。
        // 主循环中本方法先于传送确认处理执行，因此必须在这里再次拦截，
        // 等 TryHandleWheelLogic 成功 FireCallbackInt(0) 后再继续。
        if (!wheelTeleportAcceptSubmitted)
        {
            var confirmationAddon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
            if (confirmationAddon != null && confirmationAddon->IsReady && confirmationAddon->IsVisible)
            {
                return;
            }
        }

        if (wheelPendingMapLinkDeadline != default &&
            DateTime.UtcNow >= wheelPendingMapLinkDeadline)
        {
            WheelMapLinkStatus = "车轮：红旗地图链接等待超时，将继续等待下一条红旗。";
            wheelPendingMapLink = null;
            wheelPendingMapLinkDeadline = default;
            wheelPendingMapLinkGeneration = 0;
            return;
        }

        var mapLink = wheelPendingMapLink;
        var mapLinkGeneration = wheelPendingMapLinkGeneration;
        CaptureWheelFlagSnapshot();
        if (gameGui.OpenMapWithMapLink(mapLink))
        {
            if (mapLinkGeneration != wheelMapLinkGeneration)
            {
                return;
            }

            wheelPendingMapLink = null;
            wheelPendingMapLinkDeadline = default;
            wheelPendingMapLinkGeneration = 0;
            wheelActiveMapLinkGeneration = mapLinkGeneration;
            wheelAwaitingMapChangeAndFlag = true;
            wheelNewFlagPending = true;
            wheelWaitingForFreshFlag = true;
            wheelTeleportSourceMapId = clientState.MapId;
            wheelFlagReadyAt = DateTime.UtcNow.AddSeconds(1);
            wheelFlagRefreshRequestedAt = DateTime.UtcNow;
            mountAfterTeleportPending = true;
            mountRetryQueued = false;
            mountAttemptCount = 0;
            dismountAtFlagPending = false;
            navigationPositionSampleValid = false;
            navigationMovementObserved = false;
            WheelMapLinkStatus = $"已打开 {mapLink.PlaceName} {mapLink.CoordinateString}，等待游戏设置红旗。";
            AutoTreasureHuntStatus = "车轮：已根据聊天地图链接设置红旗。";
        }
        else
        {
            WheelMapLinkStatus = $"打开 {mapLink.PlaceName} {mapLink.CoordinateString} 暂未成功，将保留并重试当前红旗。";
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

    private unsafe void CaptureWheelFlagSnapshot()
    {
        wheelFlagSnapshotValid = false;
        var mapAgent = AgentMap.Instance();
        if (mapAgent == null || mapAgent->FlagMarkerCount == 0)
        {
            return;
        }

        var marker = mapAgent->FlagMapMarkers[0];
        if (marker.TerritoryId == 0 || marker.MapId == 0)
        {
            return;
        }

        wheelFlagSnapshotValid = true;
        wheelFlagSnapshotTerritoryId = marker.TerritoryId;
        wheelFlagSnapshotMapId = marker.MapId;
        wheelFlagSnapshotX = marker.XFloat;
        wheelFlagSnapshotY = marker.YFloat;
    }

    private unsafe void TryHandleWheelLogic()
    {
        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady || !addon->IsVisible || addon->PromptText == null)
        {
            if (wheelTeleportAcceptSubmitted && wheelAwaitingMapChangeAndFlag)
            {
                return;
            }

            ResetWheelTeleportAcceptance();
            return;
        }

        var prompt = addon->PromptText->NodeText.ToString();
        var telepo = Telepo.Instance();
        var isTeleportRequest = telepo != null && telepo->ActiveTeleportRequest;
        var isMap12TeleportRequest = clientState.MapId == OptimizedInteractionMapId &&
            (isTeleportRequest ||
             prompt.Contains("传送邀请", StringComparison.Ordinal) ||
             prompt.Contains("接受前往", StringComparison.Ordinal));
        if (!isTeleportRequest &&
            !isMap12TeleportRequest &&
            !prompt.Contains("要返回到", StringComparison.Ordinal) &&
            !prompt.Contains("要传送到", StringComparison.Ordinal) &&
            !prompt.Contains("接受传送", StringComparison.Ordinal) &&
            !prompt.Contains("传送邀请", StringComparison.Ordinal))
        {
            if (wheelTeleportAcceptSubmitted && wheelAwaitingMapChangeAndFlag)
            {
                return;
            }

            ResetWheelTeleportAcceptance();
            return;
        }

        if (wheelTeleportAcceptSubmitted &&
            !string.IsNullOrEmpty(wheelTeleportAcceptedPrompt) &&
            !string.Equals(prompt, wheelTeleportAcceptedPrompt, StringComparison.Ordinal))
        {
            ResetWheelTeleportAcceptance();
        }

        // 接受状态只在当前传送流程等待地图切换时有效；地图 12 的特殊流程
        // 结束后可能仍残留旧标志，不能让它阻塞新的传送邀请。
        if (wheelTeleportAcceptSubmitted && !wheelAwaitingMapChangeAndFlag)
        {
            ResetWheelTeleportAcceptance();
        }

        if (wheelTeleportAcceptSubmitted)
        {
            return;
        }

        if (wheelTeleportAcceptAt == default)
        {
            wheelTeleportAcceptAt = DateTime.UtcNow;
            AutoTreasureHuntStatus = "车轮：检测到传送请求，等待一秒后接受。";
            return;
        }

        if (DateTime.UtcNow < wheelTeleportAcceptAt)
        {
            return;
        }

        if (wheelNewFlagPending &&
            wheelWaitingForFreshFlag &&
            TryUseWheelDirectFlagNavigation(out var playerDistanceSquared, out var aetheryteDistanceSquared))
        {
            // 当前地图内步行/飞行比从最近可用水晶重新传送更近：关闭传送邀请，
            // 直接由后续红旗流程执行坐骑和 /vnav flyflag。
            if (wheelLastMapLink != null)
            {
                wheelTeleportAcceptSubmitted = true;
                wheelTeleportAcceptedPrompt = prompt;
                wheelAwaitingMapChangeAndFlag = true;
                wheelWaitingForFreshFlag = true;
                wheelTeleportSourceMapId = clientState.MapId;
                wheelFlagReadyAt = default;
                AutoTreasureHuntStatus = $"车轮：当前距离红旗更近（角色 {MathF.Sqrt(playerDistanceSquared):F1}，最近水晶 {MathF.Sqrt(aetheryteDistanceSquared):F1}），不传送并直接前往红旗。";
            }
            else
            {
                wheelTeleportAcceptAt = DateTime.UtcNow.AddMilliseconds(250);
                AutoTreasureHuntStatus = "车轮：关闭较远传送邀请失败，正在重试。";
            }

            return;
        }

        if (addon->FireCallbackInt(0))
        {
            var hasPendingFlagNavigation = wheelNewFlagPending &&
                wheelActiveMapLinkGeneration != 0 &&
                wheelActiveMapLinkGeneration == wheelMapLinkGeneration;
            wheelTeleportAcceptSubmitted = true;
            wheelTeleportAcceptedPrompt = prompt;
            wheelAwaitingMapChangeAndFlag = hasPendingFlagNavigation;
            wheelWaitingForFreshFlag = hasPendingFlagNavigation;
            wheelTeleportSourceMapId = clientState.MapId;
            wheelFlagReadyAt = hasPendingFlagNavigation
                ? DateTime.UtcNow.AddSeconds(1)
                : default;
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
        wheelTeleportAcceptedPrompt = string.Empty;
    }

    /// <summary>
    /// 当角色和新红旗已在同一地图时，比较角色与该地图内最近可用水晶到红旗的距离。
    /// 只有角色严格更近时才返回 true；无法读取红旗或水晶坐标时维持原传送行为。
    /// </summary>
    private unsafe bool TryUseWheelDirectFlagNavigation(
        out float playerDistanceSquared,
        out float nearestAetheryteDistanceSquared)
    {
        playerDistanceSquared = float.PositiveInfinity;
        nearestAetheryteDistanceSquared = float.PositiveInfinity;

        if (wheelLastMapLink == null ||
            wheelActiveMapLinkGeneration == 0 ||
            wheelActiveMapLinkGeneration != wheelMapLinkGeneration ||
            objectTable.LocalPlayer == null)
            return false;

        var mapAgent = AgentMap.Instance();
        if (mapAgent == null || mapAgent->FlagMarkerCount == 0)
            return false;

        var flag = mapAgent->FlagMapMarkers[0];
        if (flag.MapId == 0 || flag.TerritoryId == 0 ||
            clientState.MapId != flag.MapId ||
            clientState.TerritoryType != flag.TerritoryId ||
            flag.MapId != wheelLastMapLink.Map.RowId ||
            flag.TerritoryId != wheelLastMapLink.TerritoryType.RowId)
        {
            return false;
        }

        var player = objectTable.LocalPlayer;
        var playerDeltaX = player.Position.X - flag.XFloat;
        var playerDeltaY = player.Position.Y - flag.YFloat;
        playerDistanceSquared = playerDeltaX * playerDeltaX + playerDeltaY * playerDeltaY;

        var foundPositionedAetheryte = false;
        foreach (var entry in aetheryteList)
        {
            if (entry.TerritoryId != flag.TerritoryId)
                continue;

            var aetheryte = entry.AetheryteData.Value;
            if (!aetheryte.IsAetheryte || aetheryte.Map.RowId != flag.MapId ||
                !TryGetAetheryteWorldPosition(aetheryte, flag.MapId, out var aetheryteX, out var aetheryteY))
            {
                continue;
            }

            var deltaX = aetheryteX - flag.XFloat;
            var deltaY = aetheryteY - flag.YFloat;
            var distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared < nearestAetheryteDistanceSquared)
            {
                nearestAetheryteDistanceSquared = distanceSquared;
                foundPositionedAetheryte = true;
            }
        }

        return foundPositionedAetheryte &&
               playerDistanceSquared < nearestAetheryteDistanceSquared;
    }

    private void TryHandleWheelDoorSelection()
    {
        if (!IsWheelLogicSelected || clientState.MapId != 896 || wheelDoorSelectionFloor >= 5)
            return;

        if (wheelDoorSelectionLastMapId != clientState.MapId)
        {
            wheelDoorSelectionLastMapId = clientState.MapId;
            wheelDoorSelectionFloor = 1;
            wheelDoorSelectionDoorBaseId = 0;
            wheelDoorSelectionDoorEntityId = 0;
            wheelDoorSelectionDoorMoveIssued = false;
            wheelDoorSelectionVfxMoveIssued = false;
            wheelDoorSelectionChestEntityId = 0;
            wheelDoorSelectionChestMoveIssued = false;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.Casting] || condition[ConditionFlag.OccupiedInQuestEvent])
            return;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        // Wheel follows the head to the current-floor chest but never interacts.
        if (wheelDoorSelectionDoorBaseId == 0 && !wheelDoorSelectionVfxMoveIssued)
        {
            var chest = objectTable.FirstOrDefault(o => TreasureMapDefinitions.DoorSelectionInitialChestBaseIds.Contains(o.BaseId));
            if (chest == null)
                return;
            if (wheelDoorSelectionChestEntityId != chest.EntityId)
            {
                wheelDoorSelectionChestEntityId = chest.EntityId;
                wheelDoorSelectionChestMoveIssued = false;
            }
            if (!wheelDoorSelectionChestMoveIssued)
            {
                var chestCommand = FormattableString.Invariant($"/vnav moveto {chest.Position.X:F3} {chest.Position.Y:F3} {chest.Position.Z:F3}");
                wheelDoorSelectionChestMoveIssued = commandManager.ProcessCommand(chestCommand);
            }
            return;
        }

        if (wheelDoorSelectionAnimationUntil != default && DateTime.UtcNow < wheelDoorSelectionAnimationUntil)
            return;

        if (wheelDoorSelectionDoorEntityId == 0)
        {
            var door = objectTable.FirstOrDefault(o => o.BaseId == wheelDoorSelectionDoorBaseId && o.IsTargetable);
            if (door == null)
            {
                AutoTreasureHuntStatus = $"车轮：等待车头门实体出现（BaseID {wheelDoorSelectionDoorBaseId}）。";
                return;
            }
            wheelDoorSelectionDoorEntityId = door.EntityId;
            wheelDoorSelectionDoorMoveIssued = false;
        }

        var targetDoor = objectTable.FirstOrDefault(o => o.EntityId == wheelDoorSelectionDoorEntityId);
        if (targetDoor == null)
        {
            wheelDoorSelectionDoorEntityId = 0;
            wheelDoorSelectionDoorMoveIssued = false;
            return;
        }

        if (!wheelDoorSelectionDoorMoveIssued)
        {
            var command = FormattableString.Invariant($"/vnav moveto {targetDoor.Position.X:F3} {targetDoor.Position.Y:F3} {targetDoor.Position.Z:F3}");
            wheelDoorSelectionDoorMoveIssued = commandManager.ProcessCommand(command);
            return;
        }

        if (System.Numerics.Vector3.DistanceSquared(localPlayer.Position, targetDoor.Position) > 2.5f * 2.5f)
            return;

        commandManager.ProcessCommand("/vnav stop");
        if (!wheelDoorSelectionVfxMoveIssued)
        {
            if (!vfxEffectCenterProbe.TryGetNearestDoorSelectionPortalPosition(localPlayer.Position, out wheelDoorSelectionVfxPosition, out _))
            {
                AutoTreasureHuntStatus = "车轮：已到达车头选择的门，等待最近 VFX 中心出现。";
                return;
            }

            var command = FormattableString.Invariant($"/vnav moveto {wheelDoorSelectionVfxPosition.X:F3} {wheelDoorSelectionVfxPosition.Y:F3} {wheelDoorSelectionVfxPosition.Z:F3}");
            wheelDoorSelectionVfxMoveIssued = commandManager.ProcessCommand(command);
            return;
        }

        if (System.Numerics.Vector3.DistanceSquared(localPlayer.Position, wheelDoorSelectionVfxPosition) > 2.5f * 2.5f)
            return;

        commandManager.ProcessCommand("/vnav stop");
        wheelDoorSelectionFloor++;
        wheelDoorSelectionDoorBaseId = 0;
        wheelDoorSelectionDoorEntityId = 0;
        wheelDoorSelectionDoorMoveIssued = false;
        wheelDoorSelectionVfxMoveIssued = false;
        wheelDoorSelectionAnimationUntil = default;
        AutoTreasureHuntStatus = $"车轮：已到达 VFX 中心并确认仍可移动，当前记录为第 {wheelDoorSelectionFloor} 层，等待车头继续处理宝箱。";
    }

    private void ResetWheelMapLinkPending()
    {
        wheelPendingMapLink = null;
        wheelPendingMapLinkDeadline = default;
        wheelPendingMapLinkGeneration = 0;
        wheelActiveMapLinkGeneration = 0;
        wheelNewFlagPending = false;
        wheelWaitingForFreshFlag = false;
        wheelFlagSnapshotValid = false;
        wheelFlagRefreshRequestedAt = default;
        wheelAwaitingMapChangeAndFlag = false;
        wheelTeleportSourceMapId = 0;
        wheelFlagReadyAt = default;
        wheelTeleportStallSampleAt = default;
        wheelTeleportStallRecoveryAt = default;
        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        mountAttemptCount = 0;
        dismountAtFlagPending = false;
        navigationPositionSampleValid = false;
        navigationMovementObserved = false;
    }

    private void ResetWheelMapLinkGenerations()
    {
        wheelMapLinkGeneration = 0;
        wheelPendingMapLinkGeneration = 0;
        wheelActiveMapLinkGeneration = 0;
        wheelLastMapLink = null;
    }

    private void ResetWheelFlagRoundForMap12()
    {
        // 地图 12 是流程起点：只归零轮次状态，不清除已经提交的传送接受状态。
        wheelFlagRound = 0;
        wheelLastProcessedRound = -1;
        ResetWheelTeleportAcceptance();
        ResetWheelMapLinkPending();
        ResetWheelMapLinkGenerations();
    }

    private unsafe void TryHandleWheelPostTeleport()
    {
        if (!wheelAwaitingMapChangeAndFlag ||
            !wheelNewFlagPending ||
            !wheelWaitingForFreshFlag ||
            wheelLastMapLink == null ||
            wheelActiveMapLinkGeneration == 0 ||
            wheelActiveMapLinkGeneration != wheelMapLinkGeneration ||
            emergencyStopActive)
        {
            return;
        }

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            AutoTreasureHuntStatus = "车轮：已接受传送，正在等待地图切换完成。";
            return;
        }

        // OpenMapWithMapLink opens the native map UI and it captures movement
        // input until closed. Close it before processing the new flag.
        CloseWheelMapWindows();
        var currentMapId = clientState.MapId;
        if (currentMapId == 0)
        {
            AutoTreasureHuntStatus = "车轮：等待传送后的地图 ID 变更。";
            return;
        }

        // 同地图红旗无需等待传送确认；跨地图仍必须先接受传送。
        var flagBelongsToCurrentMap = currentMapId == wheelLastMapLink.Map.RowId &&
            clientState.TerritoryType == wheelLastMapLink.TerritoryType.RowId;
        if (!flagBelongsToCurrentMap)
        {
            AutoTreasureHuntStatus = "车轮：已获取新红旗，等待接受传送邀请后再进行寻路。";
            return;
        }

        if (flagBelongsToCurrentMap)
        {
            wheelTeleportAcceptSubmitted = true;
            wheelTeleportSourceMapId = currentMapId;
        }

        if (wheelFlagReadyAt != default && DateTime.UtcNow < wheelFlagReadyAt)
        {
            AutoTreasureHuntStatus = "车轮：已接受传送请求，等待新红旗刷新。";
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
            flagMarker.MapId != currentMapId ||
            flagMarker.TerritoryId != wheelLastMapLink.TerritoryType.RowId ||
            flagMarker.MapId != wheelLastMapLink.Map.RowId)
        {
            AutoTreasureHuntStatus = "车轮：已检测到红旗，但红旗尚未匹配当前地图。";
            return;
        }

        TryRecoverWheelTeleportStall(currentMapId, flagMarker.XFloat, flagMarker.YFloat);

        // 红旗已经匹配当前地图但旧快照未被游戏清除时，最多等待 3 秒后放行，
        // 避免车轮停在“已设置红旗”状态而不进入坐骑和寻路。
        if (false && wheelFlagSnapshotValid &&
            wheelFlagRefreshRequestedAt != default &&
            DateTime.UtcNow - wheelFlagRefreshRequestedAt >= TimeSpan.FromSeconds(3))
        {
            wheelFlagSnapshotValid = false;
            wheelFlagReadyAt = default;
            AutoTreasureHuntStatus = "车轮：红旗已匹配当前地图，结束刷新等待并继续前往红旗。";
        }

        if (false && wheelFlagSnapshotValid &&
            wheelFlagRefreshRequestedAt != default &&
            DateTime.UtcNow - wheelFlagRefreshRequestedAt >= TimeSpan.FromSeconds(5) &&
            wheelFlagSnapshotTerritoryId == flagMarker.TerritoryId &&
            wheelFlagSnapshotMapId == flagMarker.MapId &&
            MathF.Abs(wheelFlagSnapshotX - flagMarker.XFloat) <= 0.01f &&
            MathF.Abs(wheelFlagSnapshotY - flagMarker.YFloat) <= 0.01f)
        {
            wheelFlagSnapshotValid = false;
        }

        if (false && wheelFlagSnapshotValid &&
            wheelFlagSnapshotTerritoryId == flagMarker.TerritoryId &&
            wheelFlagSnapshotMapId == flagMarker.MapId &&
            MathF.Abs(wheelFlagSnapshotX - flagMarker.XFloat) <= 0.01f &&
            MathF.Abs(wheelFlagSnapshotY - flagMarker.YFloat) <= 0.01f)
        {
            AutoTreasureHuntStatus = "车轮：检测到的红旗坐标与上一轮相同，等待新红旗刷新。";
            return;
        }

        if (currentMapId == OptimizedInteractionMapId ||
            TreasureMapDefinitions.RouletteInstanceMapIds.Contains(currentMapId))
        {
            wheelFlagRound++;
            wheelLastProcessedRound = wheelFlagRound;
            wheelNewFlagPending = false;
            wheelWaitingForFreshFlag = false;
            wheelAwaitingMapChangeAndFlag = false;
            wheelTeleportAcceptSubmitted = false;
            wheelTeleportSourceMapId = 0;
            mountAfterTeleportPending = false;
            mountRetryQueued = false;
            dismountAtFlagPending = false;
            navigationPositionSampleValid = false;
            navigationMovementObserved = false;
            AutoTreasureHuntStatus = $"车轮：已获取地图 {currentMapId} 的新红旗，该地图不执行寻路。";
            return;
        }

        wheelAwaitingMapChangeAndFlag = false;
        wheelFlagRound++;
        wheelLastProcessedRound = wheelFlagRound;
        wheelNewFlagPending = false;
        wheelWaitingForFreshFlag = false;
        wheelTeleportAcceptSubmitted = false;
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

    private unsafe void CloseWheelMapWindows()
    {
        foreach (var addonName in new[] { "WorldMap", "WorldMapDetail", "_WorldMap", "Map" })
        {
            var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName);
            if (addon != null && addon->IsReady && addon->IsVisible)
                addon->Close(true);
        }
    }

    private void TryRecoverWheelTeleportStall(uint currentMapId, float flagX, float flagY)
    {
        if (!wheelTeleportAcceptSubmitted || wheelLastMapLink == null ||
            currentMapId != wheelLastMapLink.Map.RowId ||
            clientState.TerritoryType != wheelLastMapLink.TerritoryType.RowId)
        {
            wheelTeleportStallSampleAt = default;
            return;
        }

        var player = objectTable.LocalPlayer;
        if (player == null)
            return;

        var deltaX = player.Position.X - flagX;
        var deltaY = player.Position.Y - flagY;
        var distanceSquared = deltaX * deltaX + deltaY * deltaY;
        if (distanceSquared <= 3f * 3f)
        {
            wheelTeleportStallSampleAt = default;
            return;
        }

        var now = DateTime.UtcNow;
        const float movementTolerance = 0.05f;
        if (wheelTeleportStallSampleAt == default ||
            MathF.Abs(player.Position.X - wheelTeleportStallSampleX) > movementTolerance ||
            MathF.Abs(player.Position.Y - wheelTeleportStallSampleY) > movementTolerance ||
            MathF.Abs(player.Position.Z - wheelTeleportStallSampleZ) > movementTolerance)
        {
            wheelTeleportStallSampleX = player.Position.X;
            wheelTeleportStallSampleY = player.Position.Y;
            wheelTeleportStallSampleZ = player.Position.Z;
            wheelTeleportStallSampleAt = now;
            return;
        }

        if (now - wheelTeleportStallSampleAt < TimeSpan.FromSeconds(2) ||
            now < wheelTeleportStallRecoveryAt ||
            condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.Casting] || condition[ConditionFlag.Mounting])
            return;

        wheelTeleportStallRecoveryAt = now.AddSeconds(3);
        mountAfterTeleportPending = true;
        mountRetryQueued = false;
        mountAttemptCount = 0;
        dismountAtFlagPending = false;
        navigationPositionSampleValid = false;
        navigationMovementObserved = false;
        commandManager.ProcessCommand("/vnav stop");
        AutoTreasureHuntStatus = "车轮：接受传送后角色在当前地图红旗附近以外连续 2 秒未移动，重新执行坐骑和红旗寻路。";
        UseMountRouletteAfterTeleportOnFrameworkThread();
    }

    private void OnMapIdChanged(uint mapId)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            UpdateOptimizedInteractionForMap(mapId);

            if (IsWheelLogicSelected && mapId == OptimizedInteractionMapId)
            {
                ResetWheelFlagRoundForMap12();
            }

            if (!IsHeadLogicSelected ||
                !IsCredentialValidated ||
                emergencyStopActive)
            {
                return;
            }

            if (doorSelectionModeActive && mapId != doorSelectionInstanceMapId)
            {
                ExitDoorSelectionMode();
            }

            if (TreasureMapDefinitions.DoorSelectionInstanceMapIds.Contains(mapId) &&
                CanRunAutomationOrTest)
            {
                EnterDoorSelectionMode();
                return;
            }

            if (!IsAutoTreasureHuntEnabled)
            {
                return;
            }

            if (IsRouletteMode)
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
        UpdateOptimizedInteractionForMap(clientState.MapId);

        if (!IsHeadLogicSelected || !IsCredentialValidated || !CanRunAutomationOrTest)
        {
            return;
        }

        if (emergencyStopActive)
        {
            return;
        }

        if (TreasureMapDefinitions.DoorSelectionInstanceMapIds.Contains(clientState.MapId))
        {
            EnterDoorSelectionMode();
            return;
        }

        if (doorSelectionModeActive)
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
            marketBoardTeleportRetryQueued = false;
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

    private void UpdateOptimizedInteractionForMap(uint mapId)
    {
        if (IsHeadLogicSelected &&
            IsCredentialValidated &&
            !emergencyStopActive &&
            IsAutoTreasureHuntEnabled &&
            mapId == OptimizedInteractionMapId)
        {
            LoadOptimizedInteraction();
            return;
        }

        UnloadOptimizedInteraction();
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

        if (condition[ConditionFlag.InCombat])
        {
            TeleportTestStatus = "当前处于战斗，暂停宝箱确认，战斗结束后继续。";
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
            doorSelectionPortalEntryPending = activeTreasureMapRoute == TreasureMapRoute.DoorSelection;
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
        EnableAeAssistForTreasureInstance();
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
        marketBoardTeleportRetryQueued = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        automaticMapSupplementRunning = false;
        ResetMarketPurchase();
        commandManager.ProcessCommand("/bmrai on");
        AutoTreasureHuntStatus = "转盘：卫月检测到当前地图 ID 1059，并执行 /bmrai on。";
    }

    private void EnterDoorSelectionMode()
    {
        if (doorSelectionModeActive ||
            !TreasureMapDefinitions.DoorSelectionInstanceMapIds.Contains(clientState.MapId))
        {
            return;
        }

        doorSelectionPortalEntryPending = false;
        doorSelectionModeActive = true;
        doorSelectionInstanceMapId = clientState.MapId;
        doorSelectionWasInCombat = false;
        doorSelectionFloor = 1;
        doorSelectionDoorEntityId = 0;
        doorSelectionDoorMoveIssued = false;
        doorSelectionDoorAnimationUntil = default;
        doorSelectionVfxMoveIssued = false;
        ResetDoorSelectionChestState(resetCompleted: true);
        commandManager.ProcessCommand("/vnav stop");
        EnableAeAssistForTreasureInstance();
        commandManager.ProcessCommand("/bmrai off");
        UnloadOptimizedInteraction();
        CloseWorkflowBlockingWindows();
        rouletteModeActive = false;
        ResetRouletteTarget();
        automaticMapSupplementRunning = false;
        automaticMapSupplementTriggered = false;
        mapSupplementStage = MapSupplementStage.None;
        mapSupplementActionAt = default;
        mapSupplementDeadline = default;
        mapSupplementPurchaseStep = 0;
        selectMapPending = false;
        confirmMapPending = false;
        autoWaitingForTaskTreasureMap = false;
        autoWaitingForSaddlebagMove = false;
        autoSaddlebagContextMenuPending = false;
        autoSaddlebagMoveRequested = false;
        saddlebagStoreTestPending = false;
        saddlebagStoreContextMenuPending = false;
        saddlebagTakeTestPending = false;
        saddlebagTakeContextMenuPending = false;
        mountAfterTeleportPending = false;
        mountRetryQueued = false;
        dismountAtFlagPending = false;
        treasureChestPending = false;
        confirmTreasureChestPending = false;
        waitForTreasureCombatStart = false;
        treasureCombatActive = false;
        treasureCombatLastCondition = false;
        treasureCombatEndCandidate = null;
        treasurePortalPending = false;
        confirmTreasurePortalPending = false;
        marketBoardAfterTeleportPending = false;
        marketBoardTeleportRetryQueued = false;
        marketBoardInteractionPending = false;
        marketBoardInteractionAttempted = false;
        marketBoardPositionSampleValid = false;
        ResetMarketPurchase();
        AutoTreasureHuntStatus = $"选门：已进入副本地图 {doorSelectionInstanceMapId}，已屏蔽其他自动流程，等待任务开始屏障消失。";
        TeleportTestStatus = AutoTreasureHuntStatus;
    }

    private void EnableAeAssistForTreasureInstance()
    {
        if (!CheckAeAssistRunning())
        {
            return;
        }

        commandManager.ProcessCommand("/aeTargetSelector on");
        commandManager.ProcessCommand("/aeTargetSelector mode6");
        commandManager.ProcessCommand("/aepull on");
    }

    private void DisableAeAssistPull()
    {
        if (CheckAeAssistRunning())
        {
            commandManager.ProcessCommand("/aepull off");
        }
    }

    private void TryHandleDoorSelectionMode()
    {
        if (!TreasureMapDefinitions.DoorSelectionInstanceMapIds.Contains(clientState.MapId))
        {
            ExitDoorSelectionMode();
            return;
        }

        if (TryHandleDoorSelectionCombatState())
        {
            return;
        }

        // If the combat-state transition was missed for one framework tick,
        // retain the post-combat chest phase instead of falling back to the
        // pre-combat targetable-chest wait branch.
        if (!condition[ConditionFlag.InCombat] &&
            doorSelectionCombatStartedThisFloor &&
            !doorSelectionPostCombatChestPending)
        {
            doorSelectionInitialChestCompleted = false;
            doorSelectionPostCombatChestPending = true;
            ResetDoorSelectionChestTarget();
        }

        if (!dutyState.IsDutyStarted)
        {
            if (doorSelectionChestMoveIssued)
            {
                commandManager.ProcessCommand("/vnav stop");
                ResetDoorSelectionChestTarget();
            }

            doorSelectionDutyReadyAt = default;
            AutoTreasureHuntStatus = "选门：等待任务正式开始及入口屏障消失。";
            return;
        }

        if (doorSelectionDutyReadyAt == default)
        {
            doorSelectionDutyReadyAt = DateTime.UtcNow.AddSeconds(1);
            AutoTreasureHuntStatus = "选门：已检测到任务开始，等待入口屏障完全消失。";
            return;
        }

        if (DateTime.UtcNow < doorSelectionDutyReadyAt)
        {
            return;
        }

        // 初始宝箱交互后异步点击“是”。这项点击不参与阶段判断，
        // 不会因 Yes/No 窗口暂未出现而阻塞后续的战斗状态等待。
        TryConfirmDoorSelectionChest();

        // A successful Yes/No confirmation marks the initial combat chest as
        // complete. Do not search or re-interact with its BaseID while waiting
        // for the encounter to start; the post-combat chest has no BaseID.
        if (doorSelectionInitialChestYesConfirmed)
        {
            if (condition[ConditionFlag.InCombat])
            {
                AutoTreasureHuntStatus = "选门：初始宝箱已确认，正在等待战斗结束。";
                return;
            }

            if (!doorSelectionCombatObservedAfterYes && !doorSelectionPostCombatChestPending)
            {
                AutoTreasureHuntStatus = "选门：初始宝箱已确认，等待进入战斗。";
                return;
            }
        }

        // The fifth floor is the terminal floor; do not attempt a sixth door.
        if (doorSelectionFloor >= 5 && doorSelectionInitialChestCompleted && !doorSelectionPostCombatChestPending)
        {
            AutoTreasureHuntStatus = "选门：已记录第五层，等待副本后续结束。";
            return;
        }

        var combatChestIds = doorSelectionFloor switch
        {
            1 => TreasureMapDefinitions.DoorSelectionCombatStartChestFloor1BaseIds,
            2 => TreasureMapDefinitions.DoorSelectionCombatStartChestFloor2BaseIds,
            3 => TreasureMapDefinitions.DoorSelectionCombatStartChestFloor3BaseIds,
            4 => TreasureMapDefinitions.DoorSelectionCombatStartChestFloor4BaseIds,
            5 => TreasureMapDefinitions.DoorSelectionCombatStartChestFloor5BaseIds,
            _ => [],
        };
        var chest = doorSelectionPostCombatChestPending
            ? objectTable.FirstOrDefault(gameObject =>
                IsTreasureChest(gameObject) &&
                gameObject.IsTargetable)
            : objectTable.FirstOrDefault(gameObject =>
                combatChestIds.Contains(gameObject.BaseId));
        if (chest == null)
        {
            if (doorSelectionPostCombatChestPending &&
                !condition[ConditionFlag.InCombat] &&
                doorSelectionCombatStartedThisFloor)
            {
                commandManager.ProcessCommand("/vnav stop");
                ResetDoorSelectionChestTarget();
                doorSelectionPostCombatChestPending = false;
                doorSelectionInitialChestCompleted = true;
                doorSelectionPostCombatChestHandled = true;
                doorSelectionCombatStartedThisFloor = false;
                doorSelectionInitialChestYesConfirmed = false;
                doorSelectionCombatObservedAfterYes = false;
                AutoTreasureHuntStatus = $"选门：第 {doorSelectionFloor} 层场上没有可选中宝箱，开始选择下一扇门。";
                TryHandleDoorSelectionDoor();
                return;
            }

            if (doorSelectionPostCombatChestPending)
            {
                // 战斗结束宝箱无 BaseID，交互完成后可能立即从对象表移除。
                // 找不到该类宝箱即表示本层战斗宝箱阶段已结束，直接进入选门。
                commandManager.ProcessCommand("/vnav stop");
                ResetDoorSelectionChestTarget();
                AutoTreasureHuntStatus = "选门：已切换为战斗后宝箱阶段，等待无 BaseID 的宝箱出现。";
                return;
            }

        if (doorSelectionInitialChestCompleted &&
            !doorSelectionPostCombatChestPending &&
            doorSelectionPostCombatChestHandled)
            {
                TryHandleDoorSelectionDoor();
            }
            else
            {
                commandManager.ProcessCommand("/vnav stop");
                ResetDoorSelectionChestTarget();
                AutoTreasureHuntStatus = doorSelectionPostCombatChestPending
                    ? $"选门：第 {doorSelectionFloor} 层战斗已结束，等待战斗宝箱（BaseID {string.Join(", ", combatChestIds)}；未配置时按名称“宝箱”识别）。"
                    : $"选门：任务已开始，等待初始宝箱出现（BaseID {string.Join(", ", TreasureMapDefinitions.DoorSelectionInitialChestBaseIds)}）。";
            }
            return;
        }

        if (doorSelectionChestEntityId != chest.EntityId)
        {
            ResetDoorSelectionChestTarget();
            doorSelectionChestEntityId = chest.EntityId;
        }

        if (!doorSelectionChestMoveIssued)
        {
            targetManager.Target = chest;
            var position = chest.Position;
            var command = FormattableString.Invariant($"/vnav moveto {position.X:F3} {position.Y:F3} {position.Z:F3}");
            doorSelectionChestMoveIssued = commandManager.ProcessCommand(command);
            AutoTreasureHuntStatus = doorSelectionChestMoveIssued
                ? $"选门：已获取初始宝箱坐标 X {position.X:F3}、Y {position.Y:F3}、Z {position.Z:F3}，正在执行 {command}。"
                : $"选门：初始宝箱坐标已获取，但命令 {command} 执行失败，正在重试。";
            return;
        }

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            AutoTreasureHuntStatus = "选门：暂时无法从卫月对象表取得角色坐标。";
            return;
        }

        const float interactionDistance = 2.5f;
        if (System.Numerics.Vector3.DistanceSquared(localPlayer.Position, chest.Position) >
            interactionDistance * interactionDistance)
        {
            doorSelectionChestPositionSampleValid = false;
            AutoTreasureHuntStatus = doorSelectionPostCombatChestPending
                ? $"选门：战斗结束后正在再次前往宝箱（Entity ID {chest.EntityId}，BaseID {chest.BaseId}）。"
                : $"选门：正在前往初始宝箱（Entity ID {chest.EntityId}，BaseID {chest.BaseId}）。";
            return;
        }

        const float movementTolerance = 0.05f;
        var playerPosition = localPlayer.Position;
        if (!doorSelectionChestPositionSampleValid)
        {
            commandManager.ProcessCommand("/vnav stop");
            doorSelectionChestPositionSampleValid = true;
            doorSelectionChestSampleX = playerPosition.X;
            doorSelectionChestSampleY = playerPosition.Y;
            doorSelectionChestSampleZ = playerPosition.Z;
            doorSelectionChestPositionStableSince = DateTime.UtcNow;
            AutoTreasureHuntStatus = "选门：已进入初始宝箱交互范围并停止 /vnav，等待角色稳定。";
            return;
        }

        if (MathF.Abs(playerPosition.X - doorSelectionChestSampleX) > movementTolerance ||
            MathF.Abs(playerPosition.Y - doorSelectionChestSampleY) > movementTolerance ||
            MathF.Abs(playerPosition.Z - doorSelectionChestSampleZ) > movementTolerance)
        {
            doorSelectionChestSampleX = playerPosition.X;
            doorSelectionChestSampleY = playerPosition.Y;
            doorSelectionChestSampleZ = playerPosition.Z;
            doorSelectionChestPositionStableSince = DateTime.UtcNow;
            AutoTreasureHuntStatus = "选门：已靠近初始宝箱，等待角色停止移动后交互。";
            return;
        }

        if (DateTime.UtcNow - doorSelectionChestPositionStableSince < TimeSpan.FromSeconds(1))
        {
            return;
        }

        commandManager.ProcessCommand("/vnav stop");
        targetManager.Target = chest;
        if (InteractWithGameObject(chest) == 0)
        {
            doorSelectionChestMoveIssued = false;
            doorSelectionChestPositionSampleValid = false;
            AutoTreasureHuntStatus = "选门：战斗宝箱交互调用失败，正在重新寻路并重试。";
            return;
        }

        if (doorSelectionPostCombatChestPending)
        {
            doorSelectionPostCombatChestPending = false;
            doorSelectionInitialChestCompleted = true;
            doorSelectionPostCombatChestHandled = true;
            doorSelectionCombatStartedThisFloor = false;
            doorSelectionInitialChestYesConfirmed = false;
            doorSelectionCombatObservedAfterYes = false;
            ResetDoorSelectionChestTarget();
            AutoTreasureHuntStatus = $"选门：第 {doorSelectionFloor} 层宝箱已完成，等待场上宝箱消失后选择下一扇门。";
            TeleportTestStatus = AutoTreasureHuntStatus;
            return;
        }

        confirmDoorSelectionChestPending = true;
        doorSelectionChestConfirmDeadline = DateTime.UtcNow.AddSeconds(10);
        doorSelectionInitialChestYesConfirmed = true;
        doorSelectionInitialChestCompleted = false;
        doorSelectionPostCombatChestPending = false;
        doorSelectionPostCombatChestHandled = false;
        doorSelectionCombatObservedAfterYes = false;
            AutoTreasureHuntStatus = "选门：已与战斗宝箱交互，等待确认窗口。";
    }

    private void TryHandleDoorSelectionDoor()
    {
        if (doorSelectionFloor >= 5)
            return;

        if (doorSelectionDoorAnimationUntil != default)
        {
            if (DateTime.UtcNow < doorSelectionDoorAnimationUntil ||
                condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
                condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Casting])
            {
                AutoTreasureHuntStatus = $"选门：第 {doorSelectionFloor} 层门动画处理中，等待移动恢复。";
                return;
            }

            if (!doorSelectionVfxMoveIssued)
            {
                var player = objectTable.LocalPlayer;
                if (player == null || !vfxEffectCenterProbe.TryGetNearestDoorSelectionPortalPosition(player.Position, out doorSelectionVfxPosition, out _))
                {
                    AutoTreasureHuntStatus = "选门：门动画已结束，但暂未找到 VFX 中心，继续等待。";
                    return;
                }

                var command = FormattableString.Invariant($"/vnav moveto {doorSelectionVfxPosition.X:F3} {doorSelectionVfxPosition.Y:F3} {doorSelectionVfxPosition.Z:F3}");
                doorSelectionVfxMoveIssued = commandManager.ProcessCommand(command);
                AutoTreasureHuntStatus = doorSelectionVfxMoveIssued
                    ? $"选门：正在前往第 {doorSelectionFloor + 1} 层 VFX 中心。"
                    : "选门：VFX 中心寻路命令失败，正在重试。";
                return;
            }

            var localPlayer = objectTable.LocalPlayer;
            if (localPlayer == null || System.Numerics.Vector3.DistanceSquared(localPlayer.Position, doorSelectionVfxPosition) > 2.5f * 2.5f)
                return;

            commandManager.ProcessCommand("/vnav stop");
            if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
                condition[ConditionFlag.OccupiedInQuestEvent] || condition[ConditionFlag.Casting])
                return;

            doorSelectionFloor++;
            doorSelectionDoorEntityId = 0;
            doorSelectionDoorMoveIssued = false;
            doorSelectionDoorAnimationUntil = default;
            doorSelectionVfxMoveIssued = false;
            doorSelectionInitialChestCompleted = false;
            doorSelectionPostCombatChestPending = false;
            doorSelectionPostCombatChestHandled = false;
            doorSelectionInitialChestYesConfirmed = false;
            doorSelectionCombatObservedAfterYes = false;
            doorSelectionDutyReadyAt = DateTime.UtcNow.AddSeconds(1);
            ResetDoorSelectionChestTarget();
            AutoTreasureHuntStatus = $"选门：已到达 VFX 中心并确认可移动，当前记录为第 {doorSelectionFloor} 层。";
            return;
        }

        var leftIds = doorSelectionFloor switch
        {
            1 => TreasureMapDefinitions.DoorSelectionLeftDoorFloor1To2BaseIds,
            2 => TreasureMapDefinitions.DoorSelectionLeftDoorFloor2To3BaseIds,
            3 => TreasureMapDefinitions.DoorSelectionLeftDoorFloor3To4BaseIds,
            4 => TreasureMapDefinitions.DoorSelectionLeftDoorFloor4To5BaseIds,
            _ => []
        };
        var rightIds = doorSelectionFloor switch
        {
            1 => TreasureMapDefinitions.DoorSelectionRightDoorFloor1To2BaseIds,
            2 => TreasureMapDefinitions.DoorSelectionRightDoorFloor2To3BaseIds,
            3 => TreasureMapDefinitions.DoorSelectionRightDoorFloor3To4BaseIds,
            4 => TreasureMapDefinitions.DoorSelectionRightDoorFloor4To5BaseIds,
            _ => []
        };
        var selectedDoorIds = GetDoorSelectionChoice(doorSelectionFloor - 1) == DoorSelectionChoice.Left
            ? leftIds
            : rightIds;
        var door = objectTable.FirstOrDefault(o => o.IsTargetable && selectedDoorIds.Contains(o.BaseId));
        if (door == null)
        {
            var side = GetDoorSelectionChoice(doorSelectionFloor - 1) == DoorSelectionChoice.Left ? "左门" : "右门";
            AutoTreasureHuntStatus = $"选门：第 {doorSelectionFloor} 层尚未检测到已选择的{side} BaseID。";
            return;
        }

        if (doorSelectionDoorEntityId != door.EntityId)
        {
            doorSelectionDoorEntityId = door.EntityId;
            doorSelectionDoorMoveIssued = false;
        }

        var playerPosition = objectTable.LocalPlayer?.Position ?? default;
        if (!doorSelectionDoorMoveIssued)
        {
            targetManager.Target = door;
            var command = FormattableString.Invariant($"/vnav moveto {door.Position.X:F3} {door.Position.Y:F3} {door.Position.Z:F3}");
            doorSelectionDoorMoveIssued = commandManager.ProcessCommand(command);
            AutoTreasureHuntStatus = $"选门：第 {doorSelectionFloor} 层正在前往{(GetDoorSelectionChoice(doorSelectionFloor - 1) == DoorSelectionChoice.Left ? "左门" : "右门")}。";
            return;
        }

        if (System.Numerics.Vector3.DistanceSquared(playerPosition, door.Position) > 2.5f * 2.5f)
            return;

        commandManager.ProcessCommand("/vnav stop");
        targetManager.Target = door;
        if (InteractWithGameObject(door) == 0)
        {
            doorSelectionDoorMoveIssued = false;
            AutoTreasureHuntStatus = "选门：门交互未被游戏接受，正在重试。";
            return;
        }

        doorSelectionDoorAnimationUntil = DateTime.UtcNow.AddSeconds(2);
        doorSelectionVfxMoveIssued = false;
        TrySendChatBoxEntry($"/p <digdoor:896:{door.BaseId}>");
        AutoTreasureHuntStatus = $"选门：已交互第 {doorSelectionFloor} 层门，等待动画结束。";
    }

    private bool TryHandleDoorSelectionCombatState()
    {
        var inCombat = condition[ConditionFlag.InCombat];
        if (inCombat == doorSelectionWasInCombat)
        {
            return false;
        }

        doorSelectionWasInCombat = inCombat;
        if (inCombat)
        {
            doorSelectionCombatStartedThisFloor = true;
            if (doorSelectionInitialChestYesConfirmed)
                doorSelectionCombatObservedAfterYes = true;
            commandManager.ProcessCommand("/bmrai on");
            AutoTreasureHuntStatus = "选门：已进入战斗并执行 /bmrai on。";
        }
        else
        {
            commandManager.ProcessCommand("/bmrai off");
            if (doorSelectionCombatStartedThisFloor)
            {
                doorSelectionInitialChestCompleted = false;
                doorSelectionPostCombatChestPending = true;
                ResetDoorSelectionChestTarget();
            }
            AutoTreasureHuntStatus = "选门：已脱离战斗并执行 /bmrai off。";
        }

        TeleportTestStatus = AutoTreasureHuntStatus;
        return true;
    }

    private unsafe void TryConfirmDoorSelectionChest()
    {
        if (!confirmDoorSelectionChestPending)
        {
            return;
        }

        if (DateTime.UtcNow > doorSelectionChestConfirmDeadline)
        {
            confirmDoorSelectionChestPending = false;
            ResetDoorSelectionChestTarget();
            AutoTreasureHuntStatus = "选门：等待初始宝箱确认窗口超时，正在重新前往并交互宝箱。";
            return;
        }

        var addon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        {
            return;
        }

        if (addon->FireCallbackInt(0))
        {
            confirmDoorSelectionChestPending = false;
            commandManager.ProcessCommand("/vnav stop");
            AutoTreasureHuntStatus = "选门：已出现并确认初始宝箱 Yes/No，等待战斗后无 BaseID 宝箱。";
            TeleportTestStatus = AutoTreasureHuntStatus;
        }
        else
        {
            AutoTreasureHuntStatus = "选门：初始宝箱确认回调被拒绝，继续等待重试。";
        }
    }

    private void ResetDoorSelectionChestTarget()
    {
        doorSelectionChestEntityId = 0;
        doorSelectionChestMoveIssued = false;
        doorSelectionChestPositionSampleValid = false;
    }

    private void ResetDoorSelectionChestState(bool resetCompleted)
    {
        ResetDoorSelectionChestTarget();
        confirmDoorSelectionChestPending = false;
        doorSelectionChestConfirmDeadline = default;
        doorSelectionDutyReadyAt = default;
        doorSelectionPostCombatChestPending = false;
        if (resetCompleted)
        {
            doorSelectionInitialChestCompleted = false;
            doorSelectionPostCombatChestHandled = false;
            doorSelectionCombatStartedThisFloor = false;
            doorSelectionInitialChestYesConfirmed = false;
            doorSelectionCombatObservedAfterYes = false;
        }
    }

    private void ExitDoorSelectionMode()
    {
        if (!doorSelectionModeActive)
        {
            return;
        }

        doorSelectionModeActive = false;
        doorSelectionInstanceMapId = 0;
        doorSelectionPortalEntryPending = false;
        commandManager.ProcessCommand("/vnav stop");
        commandManager.ProcessCommand("/bmrai off");
        DisableAeAssistPull();
        doorSelectionWasInCombat = false;
        ResetDoorSelectionChestState(resetCompleted: true);
        if (!IsAutoTreasureHuntEnabled || emergencyStopActive)
        {
            return;
        }

        AutoTreasureHuntStatus = "选门流程已结束，正在重新检查任务道具、主背包和陆行鸟鞍囊。";
        _ = framework.RunOnTick(
            () =>
            {
                if (IsAutoTreasureHuntEnabled && !doorSelectionModeActive && !IsRouletteMode && !emergencyStopActive)
                {
                    StartAutoTreasureHuntOnFrameworkThread();
                }
            },
            delay: TimeSpan.FromSeconds(2));
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
        DisableAeAssistPull();

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
        if (false && confirmRouletteDreamPending)
        {
            return;
        }

        TryConfirmRouletteExit();
        if (false && confirmRouletteExitPending)
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

        // 只有仍可选中的宝箱才阻止退出；已交互、不可选中或正在消失的实体不再阻塞退出点。
        var rouletteChests = objectTable
            .Where(gameObject => IsTreasureChest(gameObject) && gameObject.IsTargetable)
            .ToArray();
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

        // Only an interactable exit point takes precedence over the dream path.
        // A present but non-targetable exit must not block dream interaction.
        var exitEntity = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == RouletteExitBaseId &&
            gameObject.IsTargetable);
        if (exitEntity != null)
        {
            HandleRouletteTarget(exitEntity, "退出点", TimeSpan.FromSeconds(2), confirmExit: true);
            return;
        }

        var dream = objectTable.FirstOrDefault(gameObject =>
            TreasureMapDefinitions.RouletteDreamBaseIds.Contains(gameObject.BaseId) &&
            gameObject.IsTargetable &&
            !rouletteInteractedEntities.Contains(gameObject.EntityId));
        if (dream != null)
        {
            HandleRouletteTarget(dream, "潜网巡梦", TimeSpan.FromSeconds(1), confirmExit: false);
            return;
        }

        // 已交互过的潜网巡梦仍可能继续留在对象表中；只要它还在场，就禁止退出。
        if (objectTable.Any(gameObject => TreasureMapDefinitions.RouletteDreamBaseIds.Contains(gameObject.BaseId)))
        {
            if (rouletteTargetKind == "退出点")
            {
                commandManager.ProcessCommand("/vnav stop");
            }

            ResetRouletteTarget();
            AutoTreasureHuntStatus = $"转盘：场上仍存在潜网巡梦（BaseId {string.Join(", ", TreasureMapDefinitions.RouletteDreamBaseIds)}），禁止执行退出逻辑。";
            return;
        }

        var exit = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == RouletteExitBaseId &&
            gameObject.IsTargetable);
        if (exit != null)
        {
            HandleRouletteTarget(exit, "退出点", TimeSpan.FromSeconds(2), confirmExit: true);
            return;
        }

        ResetRouletteTarget();
        AutoTreasureHuntStatus = "转盘：等待潜网巡梦、宝箱或退出点出现。";
    }

    /// <summary>
    /// 车轮在转盘副本内仅执行退出流程：不交互宝箱或潜网巡梦。
    /// 可选中宝箱尚存在时不会退出，避免车头尚未完成结算时提前离开。
    /// </summary>
    private void TryHandleWheelRouletteExit()
    {
        if (condition[ConditionFlag.InCombat])
        {
            return;
        }

        TryConfirmRouletteExit();
        if (confirmRouletteExitPending)
        {
            return;
        }

        if (objectTable.Any(gameObject => IsTreasureChest(gameObject) && gameObject.IsTargetable))
        {
            ResetRouletteTarget();
            AutoTreasureHuntStatus = "车轮转盘：场上仍有可选中宝箱，仅等待车头处理，不执行退出。";
            return;
        }

        var exit = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == RouletteExitBaseId &&
            gameObject.IsTargetable &&
            !rouletteInteractedEntities.Contains(gameObject.EntityId));
        if (exit == null)
        {
            ResetRouletteTarget();
            AutoTreasureHuntStatus = "车轮转盘：等待可选中的退出点。";
            return;
        }

        HandleRouletteTarget(exit, "退出点", TimeSpan.FromSeconds(2), confirmExit: true);
    }

    private void TryHandleRouletteExitTest()
    {
        if (!IsHeadLogicSelected || condition[ConditionFlag.InCombat])
        {
            return;
        }

        TryConfirmRouletteExit();
        if (confirmRouletteExitPending || !rouletteExitTestPending)
        {
            return;
        }

        if (objectTable.Any(gameObject => TreasureMapDefinitions.RouletteDreamBaseIds.Contains(gameObject.BaseId)))
        {
            commandManager.ProcessCommand("/vnav stop");
            ResetRouletteTarget();
            TeleportTestStatus = $"场上仍存在潜网巡梦（BaseId {string.Join(", ", TreasureMapDefinitions.RouletteDreamBaseIds)}），测试退出已被阻止。";
            return;
        }

        var exit = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == RouletteExitBaseId &&
            gameObject.IsTargetable &&
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
        var interactionResult = InteractWithGameObject(target);
        if (interactionResult == 0)
        {
            // 交互请求未被游戏接受时不能提前记为已处理，否则退出点会被永久跳过。
            rouletteInteractedEntities.Remove(target.EntityId);
            roulettePositionSampleValid = false;
            rouletteExitDelayStarted = false;
            AutoTreasureHuntStatus = $"转盘：与{targetKind}交互请求未被接受，正在重试。";
            return;
        }

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
        activeTreasureMapRoute = SelectedTreasureMapRoute;
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

    private bool CheckAeAssistRunning()
    {
        return IsPluginRunning(AeAssistInternalName);
    }

    private bool IsPluginRunning(string internalName)
    {
        return pluginInterface.InstalledPlugins.Any(plugin =>
            plugin.IsLoaded &&
            string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (!IsHeadLogicSelected || !IsAutoTreasureHuntEnabled || IsRouletteMode || doorSelectionModeActive)
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
            doorSelectionModeActive ||
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

        if (headFlagTeleportPending)
        {
            return;
        }

        if (waitingForAutomaticMapFlag && !HasTaskTreasureMap)
        {
            TeleportTestStatus = "任务道具中暂时没有藏宝图，不能通过 /tmap 获取当前地图红旗，继续等待任务道具刷新。";
            return;
        }

        if (waitingForAutomaticMapFlag && !autoMapCommandSent)
        {
            if (TryRequestAutomaticTreasureMap())
            {
                TeleportTestStatus = "正在请求当前任务地图的最新红旗，等待地图标记刷新后再传送。";
                return;
            }

            waitingForAutomaticMapFlag = false;
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

        if (waitingForAutomaticMapFlag &&
            autoMapCommandSent &&
            hasAnnouncedFlag &&
            lastAnnouncedFlagTerritoryId == territoryId &&
            lastAnnouncedFlagMapId == mapId &&
            MathF.Abs(lastAnnouncedFlagX - flagX) <= 0.01f &&
            MathF.Abs(lastAnnouncedFlagY - flagY) <= 0.01f)
        {
            if (automaticMapFlagRequestedAt != default &&
                DateTime.UtcNow - automaticMapFlagRequestedAt >= TimeSpan.FromSeconds(5))
            {
                waitingForAutomaticMapFlag = false;
                autoMapCommandSent = true;
                autoMapFlagRetryQueued = false;
                hasAnnouncedFlag = false;
            }
            else
            {
                TryRequestAutomaticTreasureMap();
                return;
            }

            TeleportTestStatus = "已请求新的任务地图，但红旗仍是上一轮坐标，等待游戏刷新。";
            TryRequestAutomaticTreasureMap();
            return;
        }

        waitingForAutomaticMapFlag = false;

        if (TryUseHeadDirectFlagNavigation(
                territoryId,
                mapId,
                flagX,
                flagY,
                out var playerDistanceSquared,
                out var aetheryteDistanceSquared))
        {
            // 车头已在目标地图且比最近水晶更接近红旗：跳过原有的
            // 延迟播报、队友加载等待及 Telepo 传送，直接播报并寻路。
            headFlagTeleportReady = false;
            headFlagTeleportPending = false;
            headFlagAnnouncementPending = false;
            headFlagAnnouncementDeadline = default;
            headFlagPartyReadyAt = default;

            const float coordinateTolerance = 0.01f;
            var alreadyAnnounced = hasAnnouncedFlag &&
                lastAnnouncedFlagTerritoryId == territoryId &&
                lastAnnouncedFlagMapId == mapId &&
                MathF.Abs(lastAnnouncedFlagX - flagX) <= coordinateTolerance &&
                MathF.Abs(lastAnnouncedFlagY - flagY) <= coordinateTolerance;
            if (!alreadyAnnounced)
            {
                AnnounceFlag(territoryId, mapId, flagX, flagY, isTest: false);
            }

            mountAfterTeleportPending = true;
            mountRetryQueued = false;
            mountAttemptCount = 0;
            dismountAtFlagPending = false;
            TeleportTestStatus = $"车头：当前距离红旗更近（角色 {MathF.Sqrt(playerDistanceSquared):F1}，最近水晶 {MathF.Sqrt(aetheryteDistanceSquared):F1}），已直接播报并开始前往红旗。";
            AutoTreasureHuntStatus = TeleportTestStatus;
            ScheduleMountRouletteRetry();
            return;
        }

        if (!headFlagTeleportReady)
        {
            TryAnnounceNewFlag(territoryId, mapId, flagX, flagY);
            headFlagTeleportPending = true;
            TeleportTestStatus = "已播报红旗，等待 1 秒后再请求传送，确保车轮先收到坐标。";
            _ = framework.RunOnTick(
                () =>
                {
                    headFlagTeleportPending = false;
                    if (!CanRunAutomationOrTest || emergencyStopActive)
                    {
                        headFlagTeleportReady = false;
                        return;
                    }

                    headFlagTeleportReady = true;
                    TestTeleportToOpenedMapAetheryteOnFrameworkThread();
                },
                delay: TimeSpan.FromSeconds(1));
            return;
        }

        headFlagTeleportReady = false;

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

        if (headFlagAnnouncementPending)
        {
            headFlagAnnouncementDeadline = DateTime.UtcNow.AddSeconds(30);
            headFlagPartyReadyAt = default;
            TeleportTestStatus = "车头已请求传送，等待传送完成后发送红旗给车轮。";
            SchedulePendingHeadFlagAnnouncement();
        }

        var accepted = telepo->Teleport(matchedEntry.AetheryteId, matchedEntry.SubIndex);
        if (!accepted)
        {
            headFlagAnnouncementPending = false;
            headFlagAnnouncementDeadline = default;
            headFlagPartyReadyAt = default;
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

        headFlagAnnouncementPending = true;
        headFlagPartyReadyAt = default;
        headPendingFlagTerritoryId = territoryId;
        headPendingFlagMapId = mapId;
        headPendingFlagX = flagX;
        headPendingFlagY = flagY;
    }

    /// <summary>
    /// 车头已处于红旗同地图时，比较角色与该地图最近可用水晶到红旗的 XY 距离。
    /// 只有角色严格更近时才跳过传送并直接寻路。
    /// </summary>
    private bool TryUseHeadDirectFlagNavigation(
        uint territoryId,
        uint mapId,
        float flagX,
        float flagY,
        out float playerDistanceSquared,
        out float nearestAetheryteDistanceSquared)
    {
        playerDistanceSquared = float.PositiveInfinity;
        nearestAetheryteDistanceSquared = float.PositiveInfinity;

        var player = objectTable.LocalPlayer;
        if (player == null || clientState.MapId != mapId || clientState.TerritoryType != territoryId)
            return false;

        var playerDeltaX = player.Position.X - flagX;
        var playerDeltaY = player.Position.Y - flagY;
        playerDistanceSquared = playerDeltaX * playerDeltaX + playerDeltaY * playerDeltaY;

        var foundPositionedAetheryte = false;
        foreach (var entry in aetheryteList)
        {
            if (entry.TerritoryId != territoryId)
                continue;

            var aetheryte = entry.AetheryteData.Value;
            if (!aetheryte.IsAetheryte || aetheryte.Map.RowId != mapId ||
                !TryGetAetheryteWorldPosition(aetheryte, mapId, out var aetheryteX, out var aetheryteY))
            {
                continue;
            }

            var deltaX = aetheryteX - flagX;
            var deltaY = aetheryteY - flagY;
            var distanceSquared = deltaX * deltaX + deltaY * deltaY;
            if (distanceSquared < nearestAetheryteDistanceSquared)
            {
                nearestAetheryteDistanceSquared = distanceSquared;
                foundPositionedAetheryte = true;
            }
        }

        return foundPositionedAetheryte &&
               playerDistanceSquared < nearestAetheryteDistanceSquared;
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

    private void SchedulePendingHeadFlagAnnouncement(TimeSpan? delay = null)
    {
        if (!headFlagAnnouncementPending)
        {
            return;
        }

        _ = framework.RunOnTick(
            () =>
            {
                if (!headFlagAnnouncementPending ||
                    !IsHeadLogicSelected ||
                    !CanRunAutomationOrTest ||
                    emergencyStopActive)
                {
                    return;
                }

                if (DateTime.UtcNow >= headFlagAnnouncementDeadline)
                {
                    headFlagAnnouncementPending = false;
                    headFlagPartyReadyAt = default;
                    TeleportTestStatus = "等待车头传送完成后播报红旗超时，已取消本轮播报。";
                    return;
                }

                if (condition[ConditionFlag.BetweenAreas] ||
                    condition[ConditionFlag.BetweenAreas51] ||
                    clientState.MapId != headPendingFlagMapId)
                {
                    headFlagPartyReadyAt = default;
                    SchedulePendingHeadFlagAnnouncement(TimeSpan.FromSeconds(3));
                    return;
                }

                if (!TryAreAllPartyMembersReady(out var partyWaitReason))
                {
                    headFlagPartyReadyAt = default;
                    TeleportTestStatus = $"已完成切图，但{partyWaitReason}，暂不发送红旗。";
                    SchedulePendingHeadFlagAnnouncement(TimeSpan.FromMilliseconds(250));
                    return;
                }

                if (headFlagPartyReadyAt == default)
                {
                    headFlagPartyReadyAt = DateTime.UtcNow;
                    TeleportTestStatus = "车头与所有队友已处于同一地图且对象已加载，立即发送红旗。";
                    SchedulePendingHeadFlagAnnouncement(TimeSpan.FromMilliseconds(250));
                    return;
                }

                if (DateTime.UtcNow < headFlagPartyReadyAt)
                {
                    SchedulePendingHeadFlagAnnouncement(TimeSpan.FromMilliseconds(250));
                    return;
                }

                headFlagAnnouncementPending = false;
                headFlagAnnouncementDeadline = default;
                headFlagPartyReadyAt = default;
                AnnounceFlag(
                    headPendingFlagTerritoryId,
                    headPendingFlagMapId,
                    headPendingFlagX,
                    headPendingFlagY,
                    isTest: false);
            },
            delay: delay ?? TimeSpan.FromSeconds(3));
    }

    private bool TryAreAllPartyMembersReady(out string reason)
    {
        reason = string.Empty;
        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
        {
            reason = "暂时无法取得自己的对象";
            return false;
        }

        if (partyList.Count == 0)
        {
            reason = "队伍列表尚未加载";
            return false;
        }

        var currentTerritoryId = clientState.TerritoryType;
        foreach (var member in partyList)
        {
            if (member.EntityId == 0)
            {
                reason = "仍有队友实体尚未加载";
                return false;
            }

            if (member.Territory.RowId != currentTerritoryId)
            {
                reason = "仍有队友未进入当前地图";
                return false;
            }

            var memberObject = member.GameObject;
            if (memberObject == null || !memberObject.IsTargetable)
            {
                reason = "仍有队友对象不可选中";
                return false;
            }
        }

        if (!objectTable.Any(gameObject =>
                gameObject.EntityId != 0 &&
                gameObject.EntityId != localPlayer.EntityId &&
                gameObject.IsTargetable))
        {
            reason = "当前对象表中还没有其他可选中实体";
            return false;
        }

        return true;
    }

    private bool TrySendPartyFlag()
    {
        return TrySendChatBoxEntry("/p <flag>");
    }

    private unsafe bool TrySendChatBoxEntry(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
        {
            return false;
        }

        var message = GameUtf8String.FromSequence(Encoding.UTF8.GetBytes(command));
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
        if (!CanRunAutomationOrTest || !HasTaskTreasureMap)
        {
            return false;
        }

        if (!autoMapCommandSent)
        {
            autoMapCommandSent = true;
            automaticMapFlagRequestedAt = DateTime.UtcNow;
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
                    if (CanRunAutomationOrTest && HasTaskTreasureMap)
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
        var now = DateTime.UtcNow;
        if (!navigationPositionSampleValid)
        {
            navigationPositionSampleValid = true;
            navigationSampleX = currentPosition.X;
            navigationSampleY = currentPosition.Y;
            navigationSampleZ = currentPosition.Z;
            navigationPositionStableSince = now;
            navigationProgressAnchorX = currentPosition.X;
            navigationProgressAnchorY = currentPosition.Y;
            navigationProgressAnchorZ = currentPosition.Z;
            navigationLastMeaningfulProgressAt = now;
            navigationStartedAt = now;
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
            navigationPositionStableSince = now;

            var progressX = currentPosition.X - navigationProgressAnchorX;
            var progressY = currentPosition.Y - navigationProgressAnchorY;
            var progressZ = currentPosition.Z - navigationProgressAnchorZ;
            if (progressX * progressX + progressY * progressY + progressZ * progressZ >=
                FlagNavigationMeaningfulProgressDistance * FlagNavigationMeaningfulProgressDistance)
            {
                navigationProgressAnchorX = currentPosition.X;
                navigationProgressAnchorY = currentPosition.Y;
                navigationProgressAnchorZ = currentPosition.Z;
                navigationLastMeaningfulProgressAt = now;
            }

            if (now - navigationLastMeaningfulProgressAt >= FlagNavigationJitterTimeout)
            {
                FinishFlagNavigationAndDismount("红旗寻路在小范围内持续移动 5 秒未取得有效进展，已停止 /vnav 并尝试跳下。");
                return;
            }

            if (now - navigationStartedAt >= FlagNavigationMaximumDuration)
            {
                FinishFlagNavigationAndDismount("红旗寻路超过 75 秒仍未结束，已停止 /vnav 并尝试跳下。");
                return;
            }

            AutoTreasureHuntStatus = "vnavmesh 正在移动角色，等待 X/Y/Z 坐标停止变化。";
            return;
        }

        if (!navigationMovementObserved)
        {
            AutoTreasureHuntStatus = "正在等待 vnavmesh 开始移动角色。";
            return;
        }

        if (now - navigationPositionStableSince < TimeSpan.FromSeconds(1))
        {
            AutoTreasureHuntStatus = "角色 X/Y/Z 坐标已停止变化，正在确认稳定。";
            return;
        }

        FinishFlagNavigationAndDismount("角色已到达红旗附近，已停止 /vnav，正在使用跳下。");
    }

    private void FinishFlagNavigationAndDismount(string status)
    {
        commandManager.ProcessCommand("/vnav stop");
        dismountAtFlagPending = false;
        dismountReadyAttemptCount = 0;
        dismountUseAttemptCount = 0;
        navigationPositionSampleValid = false;
        navigationMovementObserved = false;
        navigationLastMeaningfulProgressAt = default;
        navigationStartedAt = default;
        TeleportTestStatus = status;
        AutoTreasureHuntStatus = TeleportTestStatus;
        _ = framework.RunOnTick(
            UseDismountAfterArrivalOnFrameworkThread,
            delay: TimeSpan.FromMilliseconds(250));
    }

    private unsafe void UseDismountAfterArrivalOnFrameworkThread()
    {
        if ((!IsHeadLogicSelected && !IsWheelLogicSelected) ||
            !CanRunAutomationOrTest ||
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
            !CanRunAutomationOrTest ||
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
        if (!IsHeadLogicSelected || emergencyStopActive || !CanRunAutomationOrTest || IsRouletteMode)
        {
            return;
        }

        if (condition[ConditionFlag.InCombat])
        {
            TeleportTestStatus = "当前处于战斗，暂不使用挖掘，战斗结束后重试。";
            ScheduleDigRetry();
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
        ResetTreasureQuestSettlementGuard();
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

    private void BeginTreasureQuestSettlementGuard()
    {
        RefreshTreasureMapCounts();
        treasureQuestSettlementPending = true;
        treasureQuestSettlementGuardUntil = DateTime.UtcNow.AddSeconds(5);
        treasureQuestSettlementLastSampleAt = DateTime.UtcNow;
        treasureQuestSettlementStableSamples = 0;
        treasureQuestSettlementLastTaskCount = TaskTreasureMapCount;
        treasureQuestSettlementLastTreasureCount = TreasureMapCount;
    }

    private void ResetTreasureQuestSettlementGuard()
    {
        treasureQuestSettlementPending = false;
        treasureQuestSettlementGuardUntil = default;
        treasureQuestSettlementLastSampleAt = default;
        treasureQuestSettlementStableSamples = 0;
        treasureQuestSettlementLastTaskCount = 0;
        treasureQuestSettlementLastTreasureCount = 0;
    }

    private bool IsTreasureQuestSettlementReady()
    {
        if (!treasureQuestSettlementPending)
        {
            return true;
        }

        RefreshTreasureMapCounts();
        var now = DateTime.UtcNow;
        if (HasTaskTreasureMap)
        {
            // 任务道具已经出现时，后续只能进入当前地图传送流程，
            // 不能把普通背包地图当成下一轮解读目标。
            ResetTreasureQuestSettlementGuard();
            return true;
        }

        if (condition[ConditionFlag.OccupiedInQuestEvent])
        {
            // 只要卫月任务事件仍为真，就把稳定窗口重新向后推迟。
            treasureQuestSettlementGuardUntil = now.AddSeconds(3);
            treasureQuestSettlementLastSampleAt = now;
            treasureQuestSettlementStableSamples = 0;
            treasureQuestSettlementLastTaskCount = TaskTreasureMapCount;
            treasureQuestSettlementLastTreasureCount = TreasureMapCount;
            return false;
        }

        if (now < treasureQuestSettlementGuardUntil)
        {
            return false;
        }

        if (TaskTreasureMapCount != treasureQuestSettlementLastTaskCount ||
            TreasureMapCount != treasureQuestSettlementLastTreasureCount)
        {
            // 库存事件可能晚于任务事件一到数帧到达；库存变化后重新开始稳定采样。
            treasureQuestSettlementLastTaskCount = TaskTreasureMapCount;
            treasureQuestSettlementLastTreasureCount = TreasureMapCount;
            treasureQuestSettlementLastSampleAt = now;
            treasureQuestSettlementStableSamples = 0;
            return false;
        }

        if (now - treasureQuestSettlementLastSampleAt < TimeSpan.FromSeconds(1))
        {
            return false;
        }

        treasureQuestSettlementLastSampleAt = now;
        treasureQuestSettlementStableSamples++;
        if (treasureQuestSettlementStableSamples < 2)
        {
            return false;
        }

        ResetTreasureQuestSettlementGuard();
        return true;
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

        if (condition[ConditionFlag.InCombat])
        {
            TeleportTestStatus = "当前处于战斗，暂停宝箱交互，战斗结束后继续。";
            return;
        }

        if (!confirmNextTreasureChestInteraction && TryGetPartyMemberInCombat(out var partyMemberName))
        {
            commandManager.ProcessCommand("/vnav stop");
            chestPositionSampleValid = false;
            TeleportTestStatus = $"队友 {partyMemberName} 仍处于战斗状态，暂停战斗后的第二次开箱。";
            AutoTreasureHuntStatus = TeleportTestStatus;
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

    private bool TryGetPartyMemberInCombat(out string partyMemberName)
    {
        partyMemberName = string.Empty;
        var localPlayerEntityId = objectTable.LocalPlayer?.EntityId ?? 0;
        foreach (var member in partyList)
        {
            if (member.EntityId == 0 || member.EntityId == localPlayerEntityId)
            {
                continue;
            }

            if (member.GameObject is not Dalamud.Game.ClientState.Objects.Types.ICharacter character ||
                (character.StatusFlags & StatusFlags.InCombat) == 0)
            {
                continue;
            }

            partyMemberName = member.Name.TextValue;
            if (string.IsNullOrWhiteSpace(partyMemberName))
            {
                partyMemberName = member.EntityId.ToString();
            }

            return true;
        }

        return false;
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
            BeginTreasureQuestSettlementGuard();
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
                if (!IsTreasureQuestSettlementReady())
                {
                    treasurePortalSearchDeadline = DateTime.UtcNow.AddSeconds(1);
                    AutoTreasureHuntStatus = "第二次开箱后未出现传送魔纹，正在等待任务事件和库存状态稳定，暂不解读下一张地图。";
                    TeleportTestStatus = AutoTreasureHuntStatus;
                    return;
                }

                if (condition[ConditionFlag.OccupiedInQuestEvent])
                {
                    if (!treasureQuestSettlementPending)
                    {
                        BeginTreasureQuestSettlementGuard();
                    }

                    treasurePortalSearchDeadline = DateTime.UtcNow.AddSeconds(1);
                    AutoTreasureHuntStatus = "第二次开箱后未出现传送魔纹，仍处于藏宝图任务事件，等待任务结算。";
                    TeleportTestStatus = AutoTreasureHuntStatus;
                    return;
                }

                if (HasTaskTreasureMap)
                {
                    treasurePortalPending = false;
                    treasurePortalEntityId = 0;
                    treasurePortalCloseDelayStarted = false;
                    treasurePortalSearchDeadline = default;
                    AutoTreasureHuntStatus = "未发现传送魔纹，但已检测到任务道具地图，准备恢复当前地图传送流程。";
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

            TeleportTestStatus = "第二次宝箱已开启，等待传送魔纹出现。";
            return;
        }

        ResetTreasureQuestSettlementGuard();
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
        if (!IsHeadLogicSelected || emergencyStopActive || !CanRunAutomationOrTest || IsRouletteMode)
        {
            return;
        }

        activeTreasureMapRoute = SelectedTreasureMapRoute;

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
                if (CanRunAutomationOrTest && !IsRouletteMode && !emergencyStopActive)
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
