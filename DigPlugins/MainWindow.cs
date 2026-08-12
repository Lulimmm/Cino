using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System.Globalization;

namespace AutoTreasureHunt;

public sealed class MainWindow
{
    private const string RememberedCredentialMask = "********";
    private static readonly Vector4 RunningColor = new(0.30f, 0.85f, 0.45f, 1.00f);
    private static readonly Vector4 StoppedColor = new(0.95f, 0.35f, 0.35f, 1.00f);

    private readonly Plugin plugin;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly Dictionary<uint, ISharedImmediateTexture?> mapIconCache = new();
    private bool isOpen = true;
    private string credentialInput = string.Empty;
    private string credentialStatus = string.Empty;
    private bool credentialValidationInProgress;
    private string navigationBaseIdInput = "2013860";
    private string mapSupplementMaxUnitPriceInput = string.Empty;
    private int mapSupplementMaxUnitPriceObserved;

    public MainWindow(
        Plugin plugin,
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        ITextureProvider textureProvider)
    {
        this.plugin = plugin;
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
    }

    public void Open()
    {
        isOpen = true;
    }

    public void Draw()
    {
        plugin.DrawCustomMarketUi();
        if (!isOpen)
        {
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(560, 500), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("海豹助手###AutoTreasureHuntMainWindow", ref isOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.Text("海豹助手");
        ImGui.Separator();
        ImGui.Spacing();

        if (!plugin.IsCredentialValidated)
        {
            DrawCredentialPage();
            ImGui.End();
            return;
        }

        ImGui.TextDisabled($"已验证：{plugin.CredentialRoleName}");
        ImGui.SameLine();
        if (ImGui.SmallButton("退出验证"))
        {
            plugin.ClearCredential();
            credentialInput = plugin.HasSavedCredential ? RememberedCredentialMask : string.Empty;
            credentialStatus = string.Empty;
        }

        if (ImGui.BeginTabBar("AutoTreasureHuntTabs"))
        {
            if (ImGui.BeginTabItem("自动挖宝"))
            {
                DrawAutoTreasureHuntPage();
                ImGui.EndTabItem();
            }

            if (plugin.HasDeveloperCredential && ImGui.BeginTabItem("测试"))
            {
                DrawTestPage();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void DrawCredentialPage()
    {
        plugin.EnsureValidationServerHealthCheck();

        if (string.IsNullOrEmpty(credentialInput) && plugin.HasSavedCredential)
        {
            credentialInput = RememberedCredentialMask;
        }

        ImGui.Text("凭证验证");
        var serverStatusColor = plugin.ValidationServerConnected == true
            ? RunningColor
            : plugin.ValidationServerConnected == false
                ? StoppedColor
                : new Vector4(0.95f, 0.75f, 0.25f, 1.00f);
        ImGui.TextColored(serverStatusColor, plugin.ValidationServerStatusText);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("###Credential", ref credentialInput, 128, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ValidateCredentialInput();
        }

        if (ImGui.Button("验证凭证"))
        {
            ValidateCredentialInput();
        }

        if (!string.IsNullOrEmpty(credentialStatus))
        {
            ImGui.SameLine();
            ImGui.TextColored(StoppedColor, credentialStatus);
        }
    }

    private async void ValidateCredentialInput()
    {
        if (credentialValidationInProgress)
            return;

        credentialValidationInProgress = true;
        credentialStatus = "验证中…";
        var validated = credentialInput == RememberedCredentialMask
            ? await plugin.ValidateRememberedCredentialAsync()
            : await plugin.ValidateCredentialAsync(credentialInput);
        credentialValidationInProgress = false;

        if (validated)
        {
            credentialInput = string.Empty;
            credentialStatus = string.Empty;
        }
        else
        {
            credentialStatus = "凭证无效";
        }
        if (!validated && !string.IsNullOrEmpty(plugin.CredentialValidationError))
            credentialStatus = plugin.CredentialValidationError;
    }

    private void DrawAutoTreasureHuntPage()
    {
        ImGui.Text($"当前逻辑：{plugin.CurrentLogicName}");
        DrawLogicModeSelector();

        var autoTreasureHuntEnabled = plugin.IsAutoTreasureHuntEnabled;
        if (ImGui.Checkbox("开启自动挖宝", ref autoTreasureHuntEnabled))
        {
            plugin.SetAutoTreasureHuntEnabled(autoTreasureHuntEnabled);
        }

        ImGui.SameLine();
        ImGui.TextColored(
            plugin.IsAutoTreasureHuntEnabled ? RunningColor : StoppedColor,
            plugin.IsAutoTreasureHuntEnabled ? "已开启" : "未开启");

        if (plugin.IsWheelLogicSelected)
        {
            ImGui.Spacing();
            ImGui.TextWrapped(plugin.AutoTreasureHuntStatus);
            DrawRuntimeEnvironment();
            return;
        }

        var autoMapSupplementEnabled = plugin.IsAutoMapSupplementEnabled;
        var mapSupplementMaxUnitPrice = plugin.MapSupplementMaxUnitPrice;
        if (mapSupplementMaxUnitPriceObserved != mapSupplementMaxUnitPrice ||
            string.IsNullOrEmpty(mapSupplementMaxUnitPriceInput))
        {
            mapSupplementMaxUnitPriceInput = mapSupplementMaxUnitPrice.ToString("N0", CultureInfo.InvariantCulture);
            mapSupplementMaxUnitPriceObserved = mapSupplementMaxUnitPrice;
        }

        if (ImGui.InputText("补图最高单价", ref mapSupplementMaxUnitPriceInput, 32))
        {
            var normalizedPrice = mapSupplementMaxUnitPriceInput
                .Replace(",", string.Empty, StringComparison.Ordinal)
                .Replace("，", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (int.TryParse(normalizedPrice, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPrice))
            {
                plugin.SetMapSupplementMaxUnitPrice(parsedPrice);
                mapSupplementMaxUnitPriceObserved = plugin.MapSupplementMaxUnitPrice;
                mapSupplementMaxUnitPriceInput = mapSupplementMaxUnitPriceObserved
                    .ToString("N0", CultureInfo.InvariantCulture);
            }
        }
        ImGui.TextWrapped("仅补图自动购买生效，报价大于此值时停止购买");
        if (ImGui.Checkbox("自动补图", ref autoMapSupplementEnabled))
        {
            plugin.SetAutoMapSupplementEnabled(autoMapSupplementEnabled);
        }

        ImGui.Spacing();
        ImGui.TextWrapped(plugin.AutoTreasureHuntStatus);
        ImGui.Separator();
        DrawTreasureMapSelector();
        DrawTreasureMapStatus(showTestButton: false);
        ImGui.Text("任务道具地图");
        ImGui.SameLine(330);
        ImGui.TextColored(
            plugin.HasTaskTreasureMap ? RunningColor : StoppedColor,
            plugin.HasTaskTreasureMap ? $"持有 {plugin.TaskTreasureMapCount} 个" : "没有地图");
        ImGui.TextDisabled($"道具 ID：{plugin.SelectedTaskTreasureMapItemId}");

        DrawRuntimeEnvironment();
    }

    private void DrawRuntimeEnvironment()
    {
        ImGui.Spacing();
        ImGui.Text("运行环境");
        DrawPluginStatus("vnavmesh", "自动寻路", plugin.IsVnavmeshRunning);
        DrawPluginStatus("globetrotter", "藏宝图坐标解析", plugin.IsGlobetrotterRunning);
        DrawPluginStatus("BossMod Reborn", "副本机制处理", plugin.IsBossModRebornRunning);

        var allReady = plugin.IsVnavmeshRunning &&
                       plugin.IsGlobetrotterRunning &&
                       plugin.IsBossModRebornRunning;
        ImGui.TextColored(
            allReady ? RunningColor : StoppedColor,
            allReady ? "运行环境已就绪" : "运行环境尚未就绪");

        if (!allReady && ImGui.Button("打开卫月插件安装器"))
        {
            var missingPlugin = !plugin.IsVnavmeshRunning
                ? "vnavmesh"
                : !plugin.IsGlobetrotterRunning
                    ? "globetrotter"
                    : "BossModReborn";
            pluginInterface.OpenPluginInstallerTo(searchText: missingPlugin);
        }
    }

    private void DrawTestPage()
    {
        if (ImGui.Button("紧急停止"))
        {
            plugin.EmergencyStop();
        }
        ImGui.SameLine();
        if (ImGui.Button("测试遍历可交互物体"))
        {
            plugin.TestListInteractableObjects();
        }
        ImGui.SameLine();
        if (ImGui.Button("测试遍历不可右键选中物体"))
        {
            plugin.TestListNonTargetableObjects();
        }
        ImGui.TextWrapped(plugin.InteractableObjectScanStatus);

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("目标 BaseID###NavigationBaseId", ref navigationBaseIdInput, 16);
        ImGui.SameLine();
        if (ImGui.Button("测试按 BaseID 坐标寻路"))
        {
            plugin.TestNavigateToBaseId(navigationBaseIdInput);
        }
        ImGui.TextWrapped(plugin.BaseIdNavigationTestStatus);

        ImGui.Separator();
        ImGui.Text("OtherPlugin 测试");
        var coordinateX = plugin.OtherPluginTestX;
        var coordinateY = plugin.OtherPluginTestY;
        var coordinateZ = plugin.OtherPluginTestZ;
        ImGui.SetNextItemWidth(150);
        var coordinateChanged = ImGui.InputFloat("X##OtherPlugin", ref coordinateX, 0.1f, 1f, "%.3f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        coordinateChanged |= ImGui.InputFloat("Y##OtherPlugin", ref coordinateY, 0.1f, 1f, "%.3f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150);
        coordinateChanged |= ImGui.InputFloat("Z##OtherPlugin", ref coordinateZ, 0.1f, 1f, "%.3f");
        if (coordinateChanged)
        {
            plugin.SetOtherPluginTestCoordinates(coordinateX, coordinateY, coordinateZ);
        }

        if (ImGui.Button("读取当前坐标##OtherPlugin"))
        {
            plugin.ReadOtherPluginCurrentCoordinate();
        }
        ImGui.SameLine();
        if (ImGui.Button("测试修改自身位置##OtherPlugin"))
        {
            plugin.TestOtherPluginApplyCoordinate(coordinateX, coordinateY, coordinateZ);
        }
        ImGui.SameLine();
        if (ImGui.Button("测试打开市场##OtherPlugin"))
        {
            plugin.TestOtherPluginOpenMarket();
        }

        ImGui.TextWrapped(plugin.OtherPluginTestStatus);
        ImGui.TextWrapped(plugin.OtherPluginMarketStatus);
        ImGui.TextDisabled("市场测试：可从任意地点打开原生搜索界面；该界面不保证具有服务器购买会话。");

        ImGui.Separator();
        if (!plugin.IsHeadLogicSelected)
        {
            ImGui.TextWrapped("当前选择车轮逻辑。可在此测试聊天地图链接获取红旗；车头测试功能已关闭。");
            if (ImGui.Button("测试获取聊天地图红旗"))
            {
                plugin.TestWheelMapLink();
            }

            ImGui.TextWrapped(plugin.WheelMapLinkStatus);
            return;
        }

        DrawTreasureMapSelector();
        DrawTreasureMapStatus(showTestButton: true);
        ImGui.Separator();
        DrawTeleportTest();
    }

    private void DrawLogicModeSelector()
    {
        ImGui.Text("运行模式");
        if (ImGui.RadioButton("车头", plugin.IsHeadLogicSelected))
        {
            plugin.SetLogicMode(TreasureHuntLogicMode.Head);
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("车轮", plugin.IsWheelLogicSelected))
        {
            plugin.SetLogicMode(TreasureHuntLogicMode.Wheel);
        }

        ImGui.SameLine();
        ImGui.TextDisabled(plugin.IsHeadLogicSelected
            ? "现有完整挖宝逻辑"
            : "接受传送并前往红旗");
    }

    private void DrawTreasureMapStatus(bool showTestButton)
    {
        ImGui.Indent();
        ImGui.Text(plugin.SelectedTreasureMapName);
        ImGui.SameLine();
        ImGui.TextDisabled($"（{plugin.SelectedTreasureMapRouteName}）");
        ImGui.SameLine(330);
        ImGui.TextColored(
            plugin.HasTreasureMap ? RunningColor : StoppedColor,
            plugin.HasTreasureMap ? $"持有 {plugin.TreasureMapCount} 个" : "背包中没有");
        ImGui.TextDisabled(
            $"道具 ID：{plugin.SelectedTreasureMapItemId}（主背包 {plugin.MainInventoryTreasureMapCount}，陆行鸟鞍囊 {plugin.SaddlebagTreasureMapCount}）");

        if (!plugin.HasTreasureMap)
        {
            ImGui.TextColored(StoppedColor, "未找到地图，暂时无法开始自动挖宝。");
        }

        if (showTestButton)
        {
            ImGui.BeginDisabled(!plugin.CanUseTreasureMap);
            if (ImGui.Button("测试解读地图"))
            {
                plugin.UseTreasureMapForTest();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.TreasureMapUseStatus);

            ImGui.BeginDisabled(plugin.SaddlebagTreasureMapCount == 0);
            if (ImGui.Button("测试取出鞍囊地图"))
            {
                plugin.TestMoveMapFromSaddlebag();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();

            ImGui.BeginDisabled(
                plugin.MainInventoryTreasureMapCount == 0 ||
                plugin.SaddlebagTreasureMapCount > 0);
            if (ImGui.Button("测试存入鞍囊地图"))
            {
                plugin.TestMoveMapToSaddlebag();
            }
            ImGui.EndDisabled();

            ImGui.TextDisabled(plugin.SaddlebagMoveStatus);
        }

        ImGui.Unindent();
    }

    private void DrawTeleportTest()
    {
        ImGui.Text("传送水晶测试");
        ImGui.Text(plugin.SelectedTaskTreasureMapName);
        ImGui.SameLine(350);
        ImGui.TextColored(
            plugin.HasTaskTreasureMap ? RunningColor : StoppedColor,
            plugin.HasTaskTreasureMap ? $"持有 {plugin.TaskTreasureMapCount} 个" : "任务道具中没有");
        ImGui.TextDisabled($"任务道具 ID：{plugin.SelectedTaskTreasureMapItemId}（任务道具及水晶背包）");

        if (ImGui.Button("测试播报红旗"))
        {
            plugin.TestAnnounceCurrentFlag();
        }
        ImGui.SameLine();

        ImGui.BeginDisabled(!plugin.HasTaskTreasureMap);
        if (ImGui.Button("测试传送水晶"))
        {
            plugin.TestTeleportToOpenedMapAetheryte();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("测试随机坐骑"))
        {
            plugin.UseMountRouletteForTest();
        }
        ImGui.SameLine();
        if (ImGui.Button("测试跳下坐骑"))
        {
            plugin.TestDismount();
        }
        if (ImGui.Button("测试寻找并开启宝箱"))
        {
            plugin.TestFindAndOpenTreasureChest();
        }
        ImGui.SameLine();
        if (ImGui.Button("测试交互并确认魔纹"))
        {
            plugin.TestFindAndEnterTreasurePortal();
        }
        ImGui.SameLine();
        if (ImGui.Button("测试退出副本"))
        {
            plugin.TestExitRouletteInstance();
        }
        ImGui.SameLine();
        if (ImGui.Button("测试补图逻辑"))
        {
            plugin.TestMapSupplementLogic();
        }
        ImGui.SameLine();
        if (ImGui.Button("购买三张地图"))
        {
            plugin.TestPurchaseSelectedMap();
        }

        if (!plugin.HasTaskTreasureMap)
        {
            ImGui.TextColored(StoppedColor, "持有该任务道具后才能测试传送。" );
        }

        ImGui.TextWrapped(plugin.TeleportTestStatus);
    }

    private void DrawTreasureMapSelector()
    {
        if (!ImGui.BeginCombo("挖宝地图", plugin.SelectedTreasureMapName))
        {
            return;
        }

        for (var index = 0; index < plugin.TreasureMapOptionCount; index++)
        {
            var optionName = plugin.GetTreasureMapOptionName(index);
            DrawItemIcon(plugin.GetTreasureMapOptionItemId(index), 32);
            ImGui.SameLine();
            var selected = index == plugin.SelectedTreasureMapIndex;
            if (ImGui.Selectable(optionName, selected))
            {
                plugin.SetSelectedTreasureMap(index);
            }

            if (selected)
            {
                ImGui.SetItemDefaultFocus();
            }
        }

        ImGui.EndCombo();
    }

    private void DrawItemIcon(uint itemId, float size)
    {
        var iconId = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Icon ?? 0;
        if (iconId == 0)
        {
            ImGui.Dummy(new System.Numerics.Vector2(size, size));
            return;
        }

        if (!mapIconCache.TryGetValue(iconId, out var texture))
        {
            texture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId));
            mapIconCache[iconId] = texture;
        }

        if (texture != null && texture.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new System.Numerics.Vector2(size, size));
        else
            ImGui.Dummy(new System.Numerics.Vector2(size, size));
    }

    private static void DrawPluginStatus(string name, string purpose, bool isRunning)
    {
        ImGui.Text(name);
        ImGui.SameLine(150);
        ImGui.TextDisabled(purpose);
        ImGui.SameLine(330);
        ImGui.TextColored(isRunning ? RunningColor : StoppedColor, isRunning ? "运行中" : "未运行");
    }
}
