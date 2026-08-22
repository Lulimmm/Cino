using Dalamud.Game.Network.Structures;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoTreasureHunt;

/// <summary>
/// 统一管理任意地点打开市场、报价请求、原生确认回调补发和结果页关闭。
/// Plugin.cs 只负责调用入口和显示状态，不再分散维护市场 UI 状态。
/// </summary>
public sealed unsafe class OpenMarketAnywhere : IDisposable
{
    private static TimeSpan RandomizedDelay(TimeSpan baseDelay)
    {
        if (baseDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        var jitterMilliseconds = Math.Min(500d, Math.Max(25d, baseDelay.TotalMilliseconds * 0.25d));
        var milliseconds = baseDelay.TotalMilliseconds +
            (Random.Shared.NextDouble() * 2d - 1d) * jitterMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Max(25d, milliseconds));
    }

    private static TimeSpan RandomizedDelay(double seconds) =>
        RandomizedDelay(TimeSpan.FromSeconds(seconds));

    private static TimeSpan RandomizedDelayMilliseconds(double milliseconds) =>
        RandomizedDelay(TimeSpan.FromMilliseconds(milliseconds));

    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly IMarketBoard marketBoard;
    private readonly Hook<AtkUnitBase.Delegates.FireCallback> fireCallbackHook;
    private int requestAttempts;
    private bool sessionActive;
    private bool purchaseRequestObserved;
    private DateTime sessionOpenedAt;
    private DateTime purchaseSentAt;
    private DateTime resultCloseAt;
    private int resultCloseAttempts;
    private string resultCloseReason = string.Empty;
    private MarketBoardListing pendingListing;
    private readonly Dictionary<int, MarketBoardListing> observedListings = new();
    private bool pendingListingValid;
    private nint pendingConfirmationAddon;
    private bool fallbackQueued;
    private bool listingCaptureRetryQueued;
    private DateTime listingCaptureRetryDeadline;
    private DateTime listingRefreshAt;
    private DateTime listingWaitingSince;
    private int listingRefreshAttempts;
    private int pendingConfirmationListingIndex = -1;
    private int pendingListingIndex = -1;
    private uint observedSearchItemId;
    private DateTime fallbackAt;
    private DateTime fallbackDeadline;
    private int fallbackAttempts;
    private ulong lastObservedListingId;
    private ulong lastCompletedListingId;
    private ulong activePurchaseListingId;
    private DateTime purchaseCompletedAt;
    private bool marketBlockStateCaptured;
    private byte capturedResultBlockingAddons;
    private ushort capturedResultBlockedParentId;
    private byte capturedSearchBlockingAddons;
    private ushort capturedSearchBlockedParentId;
    private bool customMarketOpen;
    private bool nativeItemDetailShown;
    private bool nativeDetailHoverThisFrame;
    private bool nativeDetailRetryQueued;
    private string customItemIdText = string.Empty;
    private uint customSearchItemId;
    private int customSelectedRow = -1;
    private DateTime customLastRequest;
    private DateTime customWaitingSince;
    private int customRequestAttempts;
    private string customStatus = string.Empty;
    private readonly List<MarketBoardListing> customListings = new();
    private readonly Dictionary<ulong, bool> listingQuality = new();
    private int customQualityFilter;
    private ulong customListingsSignature;
    private string customSearchText = string.Empty;
    private readonly List<(uint Id, string Name, uint Level)> customItemResults = new();
    private int customCategoryIndex;
    private int customSubcategoryIndex;
    private sealed record MarketCategory(uint Id, string Name, int SortOrder);
    private sealed record MarketSubcategory(uint Id, string Name, int SortOrder, int Icon);
    private readonly List<MarketCategory> customCategories = new();
    private readonly Dictionary<uint, List<MarketSubcategory>> customSubcategories = new();
    private bool customCategoryCacheBuilt;
    private static readonly string[] NativeMajorCategoryNames = ["主手/副手", "装备/饰品", "其他", "房屋"];
    private readonly Dictionary<uint, ISharedImmediateTexture?> iconCache = new();
    private ISharedImmediateTexture? marketBannerTexture;
    private bool marketBannerLoadAttempted;

    public OpenMarketAnywhere(
        IFramework framework,
        IGameGui gameGui,
        IPluginLog log,
        IGameInteropProvider interop,
        IMarketBoard marketBoard,
        IDataManager dataManager,
        ITextureProvider textureProvider)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.marketBoard = marketBoard;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        fireCallbackHook = interop.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
            AtkUnitBase.Addresses.FireCallback.Value,
            FireCallbackDetour);
        fireCallbackHook.Enable();
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
        marketBoard.OfferingsReceived += OnOfferingsReceived;
    }

    public uint ItemId { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public bool SessionActive => sessionActive;

    public string GetMarketBannerPath()
    {
        var path = GetInstalledPluginBannerPath();
        return $"path={path}; exists={File.Exists(path)}";
    }

    private static string GetInstalledPluginBannerPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "XIVLauncherCN", "installedPlugins", "AutoTreasureHunt", "1.0.6.5", "Resources", "dny.jpg");
    }

    private bool MatchesQuality(ulong listingId)
    {
        if (customQualityFilter == 0)
            return true;
        return listingQuality.TryGetValue(listingId, out var isHq) &&
            ((customQualityFilter == 1 && !isHq) || (customQualityFilter == 2 && isHq));
    }

    private void OnOfferingsReceived(IMarketBoardCurrentOfferings offerings)
    {
        foreach (var listing in offerings.ItemListings)
            listingQuality[listing.ListingId] = listing.IsHq;
    }

    public void OpenCustomMarketUi()
    {
        customMarketOpen = true;
        customSelectedRow = -1;
        customStatus = "请输入物品 ID 后查询报价。";
        customItemIdText = ItemId == 0 ? string.Empty : ItemId.ToString();
    }

    public void DrawCustomMarketUi()
    {
        if (!customMarketOpen)
            return;

        nativeDetailHoverThisFrame = false;

        EnsureNativeMarketCategories();

        var open = customMarketOpen;
        // dny.jpg is 1280x1920 (2:3); keep the window at the same portrait
        // aspect ratio so the background is not stretched horizontally.
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(640, 960), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        if (!ImGui.Begin("市场（自绘）##AutoTreasureHuntMarket", ref open,
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoResize))
        {
            ImGui.End();
            ImGui.PopStyleVar(4);
            customMarketOpen = open;
            return;
        }

        DrawMarketBackground();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.055f, 0.065f, 0.16f, 0.48f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new System.Numerics.Vector4(0.08f, 0.09f, 0.22f, 0.58f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new System.Numerics.Vector4(0.10f, 0.13f, 0.28f, 0.56f));
        ImGui.PushStyleColor(ImGuiCol.Button, new System.Numerics.Vector4(0.70f, 0.12f, 0.28f, 0.86f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new System.Numerics.Vector4(0.90f, 0.25f, 0.42f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new System.Numerics.Vector4(0.48f, 0.08f, 0.20f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.Header, new System.Numerics.Vector4(0.18f, 0.30f, 0.62f, 0.80f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new System.Numerics.Vector4(0.30f, 0.48f, 0.86f, 0.90f));
        ImGui.PushStyleColor(ImGuiCol.Border, new System.Numerics.Vector4(0.52f, 0.32f, 0.58f, 0.70f));
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new System.Numerics.Vector4(0.05f, 0.08f, 0.20f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, new System.Numerics.Vector4(0.05f, 0.06f, 0.16f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, new System.Numerics.Vector4(0.12f, 0.10f, 0.24f, 0.16f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(8, 5));
        ImGui.SetWindowFontScale(1.0f);
        ImGui.SetCursorPos(new System.Numerics.Vector2(ImGui.GetWindowWidth() - 42, 8));
        if (ImGui.SmallButton("×"))
            customMarketOpen = false;
        ImGui.SetCursorPos(new System.Numerics.Vector2(12, 38));
        ImGui.Text("搜索物品");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(360);
        ImGui.InputText("##CustomSearchText", ref customSearchText, 64);
        ImGui.SameLine();
        if (ImGui.Button("搜索物品"))
            SearchCustomItems();

        if (customItemResults.Count > 0)
        {
            ImGui.SetCursorPosX(36);
            ImGui.BeginChild("customItemResults", new System.Numerics.Vector2(360, 180), true);
            foreach (var result in customItemResults)
            {
                DrawItemIcon(result.Id, 24);
                ImGui.SameLine();
                if (ImGui.Selectable($"{result.Name} [{result.Id}]"))
                {
                    customItemIdText = result.Id.ToString();
                    customSearchText = result.Name;
                    StartCustomMarketSearch(result.Id);
                    customItemResults.Clear();
                    break;
                }
                if (ImGui.IsItemHovered())
                    DrawNativeStyleItemTooltip(result.Id);
            }
            ImGui.EndChild();
        }

        ImGui.Separator();
        ImGui.SetCursorPosX(36);
        ImGui.Text("分类搜索");
        ImGui.SetCursorPosX(36);
        ImGui.TextDisabled("先选择大类，再选择细分；也可以直接输入名称搜索。");
        ImGui.SetCursorPosX(36);
        for (var category = 0; category < customCategories.Count; category++)
        {
            var buttonWidth = ImGui.CalcTextSize(customCategories[category].Name).X + 20;
            if (category > 0 && ImGui.GetCursorPosX() + buttonWidth > ImGui.GetWindowContentRegionMax().X)
                ImGui.NewLine();
            else if (category > 0)
                ImGui.SameLine();
            if (ImGui.SmallButton($"{customCategories[category].Name}##marketCategory{category}"))
            {
                customCategoryIndex = category;
                customSubcategoryIndex = -1;
                customSearchText = string.Empty;
                customItemResults.Clear();
                customSearchItemId = 0;
                customListings.Clear();
                SearchCustomItems();
            }
        }
        if (customCategoryIndex > 0)
        {
            ImGui.SetCursorPosX(36);
            ImGui.Text("细分");
            var selectedCategoryId = customCategories[customCategoryIndex].Id;
            var subcategories = customSubcategories.TryGetValue(selectedCategoryId, out var nativeSubcategories)
                ? nativeSubcategories
                : new List<MarketSubcategory>();
            var iconSize = 28f;
            var iconSpacing = 6f;
            var availableWidth = Math.Max(iconSize, ImGui.GetContentRegionAvail().X);
            var iconsPerRow = Math.Max(1, (int)((availableWidth + iconSpacing) / (iconSize + iconSpacing)));
            for (var subcategory = 0; subcategory < subcategories.Count; subcategory++)
            {
                if (subcategory > 0 && subcategory % iconsPerRow == 0)
                    ImGui.NewLine();
                else if (subcategory > 0)
                    ImGui.SameLine(0, iconSpacing);
                if (DrawCategoryIconButton(subcategories[subcategory], subcategory, iconSize))
                {
                    customSubcategoryIndex = subcategory;
                    customSearchText = string.Empty;
                    customItemResults.Clear();
                    customSearchItemId = 0;
                    customListings.Clear();
                    SearchCustomItems();
                }
            }
        }

        ImGui.SetCursorPosX(36);
        ImGui.Text("物品 ID");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.InputText("##CustomItemId", ref customItemIdText, 16);
        ImGui.SameLine();
        if (ImGui.Button("查询报价") && uint.TryParse(customItemIdText, out var itemId) && itemId != 0)
            StartCustomMarketSearch(itemId);

        ImGui.SetCursorPosX(36);
        if (customSearchItemId != 0)
            ImGui.Text($"当前物品：{customSearchItemId}    报价数：{customListings.Count}");
        ImGui.SetCursorPosX(36);
        ImGui.TextWrapped(customStatus);

        ImGui.SetCursorPosX(36);
        var qualityLabel = customQualityFilter switch
        {
            1 => "NQ（普通品质）",
            2 => "HQ（高品质）",
            _ => "全部品质",
        };
        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("物品质量##customMarket", qualityLabel))
        {
            if (ImGui.Selectable("全部品质", customQualityFilter == 0)) customQualityFilter = 0;
            if (ImGui.Selectable("NQ（普通品质）", customQualityFilter == 1)) customQualityFilter = 1;
            if (ImGui.Selectable("HQ（高品质）", customQualityFilter == 2)) customQualityFilter = 2;
            ImGui.EndCombo();
        }

        if (ImGui.BeginTable("marketListings", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(-1, 330)))
        {
            ImGui.TableSetupColumn("行");
            ImGui.TableSetupColumn("数量");
            ImGui.TableSetupColumn("质量");
            ImGui.TableSetupColumn("单价");
            ImGui.TableSetupColumn("操作");
            ImGui.TableHeadersRow();
            for (var i = 0; i < customListings.Count; i++)
            {
                var listing = customListings[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                DrawItemIcon(listing.ItemId, 22);
                ImGui.SameLine();
                // Do not span the selectable over all columns: that would
                // place an invisible hitbox on top of the purchase button.
                var itemName = dataManager.GetExcelSheet<Item>().GetRowOrDefault(listing.ItemId)?.Name.ToString();
                if (string.IsNullOrWhiteSpace(itemName))
                    itemName = listing.ItemId.ToString();
                if (ImGui.Selectable(itemName, customSelectedRow == i))
                    customSelectedRow = i;
                if (ImGui.IsItemHovered())
                    DrawNativeStyleItemTooltip(listing.ItemId);
                ImGui.TableNextColumn(); ImGui.Text(listing.Quantity.ToString());
                ImGui.TableNextColumn();
                ImGui.Text(listingQuality.TryGetValue(listing.ListingId, out var isHq) && isHq ? "HQ" : "NQ");
                ImGui.TableNextColumn();
                ImGui.TextColored(new System.Numerics.Vector4(0.95f, 0.78f, 0.35f, 1f), listing.UnitPrice.ToString("N0"));
                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"购买##{i}"))
                {
                    customSelectedRow = i;
                    SubmitCustomMarketPurchase(listing);
                }
            }
            ImGui.EndTable();
        }

        if (ImGui.Button("刷新") && customSearchItemId != 0)
            RequestCustomMarketData(force: true);
        ImGui.SameLine();
        if (ImGui.Button("关闭"))
            customMarketOpen = false;
        if (!nativeDetailHoverThisFrame && nativeItemDetailShown)
        {
            HideNativeItemDetail();
            nativeItemDetailShown = false;
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(12);
        ImGui.SetWindowFontScale(1f);
        ImGui.End();
        ImGui.PopStyleVar(4);
        customMarketOpen = open && customMarketOpen;
    }

    private void DrawMarketBackground()
    {
        if (!marketBannerLoadAttempted)
        {
            marketBannerLoadAttempted = true;
            var path = GetInstalledPluginBannerPath();
            if (File.Exists(path))
                marketBannerTexture = textureProvider.GetFromFile(path);
            else
                log.Warning("Market banner image not found: path={Path}", path);
        }

        if (marketBannerTexture == null || !marketBannerTexture.TryGetWrap(out var wrap, out _))
            return;

        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        ImGui.GetWindowDrawList().AddImageRounded(
            wrap.Handle,
            windowPosition,
            windowPosition + windowSize,
            new System.Numerics.Vector2(0, 0),
            new System.Numerics.Vector2(1, 1),
            ImGui.GetColorU32(new System.Numerics.Vector4(1f, 1f, 1f, 0.78f)),
            14f,
            ImDrawFlags.RoundCornersAll);
    }

    private void StartCustomMarketSearch(uint itemId)
    {
        customSearchItemId = itemId;
        customSelectedRow = -1;
        customListings.Clear();
        customListingsSignature = 0;
        customRequestAttempts = 0;
        customWaitingSince = default;
        customStatus = "正在请求服务器报价……";
        RequestCustomMarketData(force: true);
    }

    private void SearchCustomItems()
    {
        EnsureNativeMarketCategories();
        customItemResults.Clear();
        var query = customSearchText.Trim();

        var sheet = dataManager.GetExcelSheet<Item>();
        if (sheet == null)
        {
            customStatus = "物品表尚未加载。";
            return;
        }

        var selectedCategoryId = customCategoryIndex > 0 && customCategoryIndex < customCategories.Count
            ? customCategories[customCategoryIndex].Id
            : 0u;
        var selectedSubcategoryId = 0u;
        if (selectedCategoryId != 0 && customSubcategories.TryGetValue(selectedCategoryId, out var selectedSubcategories) &&
            customSubcategoryIndex >= 0 && customSubcategoryIndex < selectedSubcategories.Count)
            selectedSubcategoryId = selectedSubcategories[customSubcategoryIndex].Id;

        var searchSheet = dataManager.GetExcelSheet<ItemSearchCategory>();
        var candidates = new List<(uint Id, string Name, uint Level, byte Major, byte SearchOrder, byte UiMajor, byte UiMinor)>();
        foreach (var item in sheet)
        {
            // Market search should only expose items that can actually be
            // traded; quest/bound/untradeable items are omitted.
            if (item.IsUntradable)
                continue;
            var name = item.Name.ToString();
            if (string.IsNullOrWhiteSpace(name) || (query.Length > 0 && !name.Contains(query, StringComparison.OrdinalIgnoreCase)))
                continue;
            var searchCategory = item.ItemSearchCategory.RowId;
            var uiCategory = item.ItemUICategory.RowId;
            var nativeSearchRow = searchSheet?.GetRowOrDefault(searchCategory);
            var nativeMajorCategory = nativeSearchRow?.Category ?? 0;
            if (selectedCategoryId != 0 && nativeMajorCategory != selectedCategoryId)
                continue;
            if (selectedSubcategoryId != 0 && searchCategory != selectedSubcategoryId)
                continue;
            // Equipment ranking uses LevelEquip (e.g. 770), while ordinary
            // items use LevelItem.  The old code sorted only LevelItem and
            // therefore displayed equipment as level 70 and in the wrong
            // order when a category was selected without a search query.
            var qualityLevel = Math.Max(item.LevelEquip, item.LevelItem.RowId);
            var uiRow = dataManager.GetExcelSheet<ItemUICategory>()?.GetRowOrDefault(uiCategory);
            candidates.Add((item.RowId, name, qualityLevel, nativeMajorCategory,
                nativeSearchRow?.Order ?? 0, uiRow?.OrderMajor ?? 0, uiRow?.OrderMinor ?? 0));
        }

        foreach (var item in candidates
                     .OrderBy(x => x.Major)
                     .ThenBy(x => x.SearchOrder)
                     .ThenByDescending(x => x.Level)
                     .ThenBy(x => x.UiMajor)
                     .ThenBy(x => x.UiMinor)
                     .ThenBy(x => x.Id)
                     .Take(100))
            customItemResults.Add((item.Id, item.Name, item.Level));
        customStatus = customItemResults.Count == 0
            ? "没有找到匹配物品。"
            : $"找到 {customItemResults.Count} 个匹配物品，可点击选择。";
    }

    private void EnsureNativeMarketCategories()
    {
        if (customCategoryCacheBuilt)
            return;

        customCategoryCacheBuilt = true;
        customCategories.Clear();
        customSubcategories.Clear();
        var itemSheet = dataManager.GetExcelSheet<Item>();
        if (itemSheet == null)
            return;

        customCategories.Add(new MarketCategory(0, "全部", 0));
        var searchSheet = dataManager.GetExcelSheet<ItemSearchCategory>();
        if (searchSheet == null)
            return;

        // ItemSearchCategory.Category is the native market's major group;
        // each ItemSearchCategory row is its native subcategory.  The Order
        // column is retained for the same left-to-right/top-to-bottom order.
        var categoryIds = itemSheet.Where(x => !x.IsUntradable && x.ItemSearchCategory.RowId != 0)
            .Select(x => x.ItemSearchCategory.RowId)
            .Select(id => searchSheet.GetRowOrDefault(id))
            .Where(row => row.HasValue)
            .Select(row => row!.Value)
            .GroupBy(row => row.Category)
            .OrderBy(group => group.Min(row => row.Order))
            .ThenBy(group => group.Key)
            .Select(group => group.Key)
            .ToArray();
        var majorIndex = 0;
        foreach (var id in categoryIds)
        {
            var categoryRow = searchSheet.GetRowOrDefault(id);
            var firstSubcategory = searchSheet.Where(row => row.Category == id).OrderBy(row => row.Order).FirstOrDefault();
            var nativeLabel = categoryRow.HasValue && categoryRow.Value.Category == id
                ? GetNativeRowName(categoryRow.Value, $"大类 {id}")
                : GetNativeRowName(firstSubcategory, $"大类 {id}");
            var label = majorIndex < NativeMajorCategoryNames.Length
                ? NativeMajorCategoryNames[majorIndex]
                : nativeLabel;
            customCategories.Add(new MarketCategory(id, label, (int)id));
            majorIndex++;

            var subIds = itemSheet.Where(x => !x.IsUntradable && x.ItemSearchCategory.RowId != 0)
                .Select(x => x.ItemSearchCategory.RowId)
                .Select(searchSheet.GetRowOrDefault)
                .Where(row => row.HasValue && row.Value.Category == id)
                .Select(row => row!.Value)
                .DistinctBy(row => row.RowId)
                .OrderBy(row => row.Order).ThenBy(row => row.RowId)
                .ToArray();
            var list = new List<MarketSubcategory>();
            foreach (var subRow in subIds)
                list.Add(new MarketSubcategory(subRow.RowId, GetNativeRowName(subRow, $"子分类 {subRow.RowId}"), subRow.Order, subRow.Icon));
            customSubcategories[id] = list;
        }
        log.Information("Native market category cache built: majorCategories={MajorCount}, subcategories={SubcategoryCount}",
            customCategories.Count - 1, customSubcategories.Values.Sum(list => list.Count));
    }

    private static string GetNativeRowName<T>(T row, string fallback)
    {
        var property = typeof(T).GetProperty("Name");
        var value = property?.GetValue(row)?.ToString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private bool DrawCategoryIconButton(MarketSubcategory category, int index, float size)
    {
        var buttonSize = new System.Numerics.Vector2(size, size);
        var position = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton($"##marketSubcategoryIcon{index}", buttonSize);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        if (category.Icon > 0)
        {
            var iconId = (uint)category.Icon;
            if (!iconCache.TryGetValue(iconId, out var texture))
            {
                texture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId));
                iconCache[iconId] = texture;
            }

            if (texture != null && texture.TryGetWrap(out var wrap, out _))
                drawList.AddImage(wrap.Handle, position, position + buttonSize);
            else
                drawList.AddRectFilled(position, position + buttonSize, ImGui.GetColorU32(new System.Numerics.Vector4(0.34f, 0.12f, 0.25f, 0.85f)), 5f);
        }
        else
        {
            drawList.AddRectFilled(position, position + buttonSize, ImGui.GetColorU32(new System.Numerics.Vector4(0.34f, 0.12f, 0.25f, 0.85f)), 5f);
            var text = "全";
            var textSize = ImGui.CalcTextSize(text);
            drawList.AddText(position + (buttonSize - textSize) * 0.5f, ImGui.GetColorU32(ImGuiCol.Text), text);
        }

        if (hovered)
        {
            drawList.AddRect(position, position + buttonSize,
                ImGui.GetColorU32(new System.Numerics.Vector4(1f, 0.78f, 0.25f, 1f)), 5f, ImDrawFlags.RoundCornersAll, 2f);
            ImGui.BeginTooltip();
            ImGui.Text(category.Name);
            ImGui.EndTooltip();
        }

        return clicked;
    }

    private void DrawItemIcon(uint itemId, float size)
    {
        var iconId = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Icon ?? 0;
        if (iconId == 0)
        {
            ImGui.Dummy(new System.Numerics.Vector2(size, size));
            return;
        }

        if (!iconCache.TryGetValue(iconId, out var texture))
        {
            texture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId));
            iconCache[iconId] = texture;
        }

        if (texture != null && texture.TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, new System.Numerics.Vector2(size, size));
        else
            ImGui.Dummy(new System.Numerics.Vector2(size, size));
    }

    private void DrawNativeStyleItemTooltip(uint itemId)
    {
        var item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null)
            return;

        ImGui.PushStyleColor(ImGuiCol.PopupBg, new System.Numerics.Vector4(0.92f, 0.78f, 0.57f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new System.Numerics.Vector4(0.92f, 0.78f, 0.57f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new System.Numerics.Vector4(0.92f, 0.78f, 0.57f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.18f, 0.12f, 0.08f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new System.Numerics.Vector4(0.30f, 0.22f, 0.14f, 1f));
        ImGui.BeginTooltip();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(12, 10));
        DrawItemIcon(itemId, 48);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(new System.Numerics.Vector4(0.42f, 0.78f, 0.16f, 1f), item.Value.Name.ToString());
        ImGui.TextDisabled($"物品 [{itemId}]");
        ImGui.TextDisabled(item.Value.IsUntradable ? "不可交易" : "可交易");
        ImGui.EndGroup();
        ImGui.Separator();

        ImGui.BeginTable("nativeStyleItemHeader", 2, ImGuiTableFlags.SizingFixedFit);
        ImGui.TableNextColumn(); ImGui.Text($"物品等级  {item.Value.LevelItem.RowId}");
        ImGui.TableNextColumn(); ImGui.Text($"装备品级  {item.Value.LevelEquip}");
        ImGui.TableNextColumn(); ImGui.Text($"稀有度  {item.Value.Rarity}");
        ImGui.TableNextColumn(); ImGui.Text($"物品分类  {item.Value.ItemUICategory.RowId}");
        ImGui.EndTable();

        if (item.Value.DamagePhys > 0 || item.Value.DamageMag > 0 || item.Value.Delayms > 0)
        {
            ImGui.BeginTable("nativeStyleWeaponStats", 3, ImGuiTableFlags.SizingStretchProp);
            ImGui.TableNextColumn(); ImGui.Text("物理基本性能");
            ImGui.TableNextColumn(); ImGui.Text("物理自动攻击");
            ImGui.TableNextColumn(); ImGui.Text("攻击间隔");
            ImGui.TableNextColumn(); ImGui.TextColored(new System.Numerics.Vector4(0.65f, 0.12f, 0.08f, 1f), item.Value.DamagePhys.ToString());
            ImGui.TableNextColumn(); ImGui.Text(item.Value.DamageMag.ToString());
            ImGui.TableNextColumn(); ImGui.Text((item.Value.Delayms / 1000f).ToString("0.00"));
            ImGui.EndTable();
        }

        DrawOptionalItemFields(item.Value, ("物理防御", "DefensePhys", "PhysicalDefense"),
            ("魔法防御", "DefenseMag", "MagicDefense"), ("物理攻击", "DamagePhys", "PhysicalDamage"),
            ("魔法攻击", "DamageMag", "MagicDamage"), ("攻击间隔", "DelayMs", "Delay"));

        ImGui.Separator();
        ImGui.Text("[市场]");
        ImGui.TextColored(new System.Numerics.Vector4(0.84f, 0.38f, 0.12f, 1f),
            item.Value.IsUntradable ? "不可在市场出售" : "可在市场出售");

        var classJob = dataManager.GetExcelSheet<ClassJobCategory>()?.GetRowOrDefault(item.Value.ClassJobCategory.RowId);
        if (classJob.HasValue)
        {
            var jobs = GetClassJobNames(classJob.Value);
            if (jobs.Count > 0)
                ImGui.TextWrapped($"使用职业：{string.Join("、", jobs)}");
        }

        DrawBaseParameterLines(item.Value);
        if (item.Value.MateriaSlotCount > 0)
        {
            ImGui.Separator();
            ImGui.Text($"魔晶石工艺  槽位 {item.Value.MateriaSlotCount}");
            for (var slot = 0; slot < item.Value.MateriaSlotCount; slot++)
                ImGui.BulletText("未镶嵌");
        }

        ImGui.Separator();
        ImGui.Text("制作与修理");
        DrawOptionalItemFields(item.Value, ("耐久度", "Durability", "MaxDurability"),
            ("精制度", "Craftsmanship", "Control"), ("修理等级", "RepairClassJob", "RepairClass"),
            ("修理材料", "RepairItem", "RepairMaterial"), ("收购价格", "PriceLow", "PriceMid"));

        var description = item.Value.Description.ToString();
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.Separator();
            ImGui.Text("物品说明");
            ImGui.TextWrapped(description);
        }
        ImGui.PopStyleColor(5);
        ImGui.PopStyleVar();
        ImGui.EndTooltip();
    }

    private void DrawItemTooltip(uint itemId)
    {
        // The market uses a self-drawn tooltip. Do not open or hide the native
        // ItemDetail addon here: it is driven by the game's hovered item only.
        var item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item == null)
            return;

        ImGui.PushStyleColor(ImGuiCol.PopupBg, new System.Numerics.Vector4(0.18f, 0.22f, 0.30f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(0.96f, 0.93f, 0.86f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new System.Numerics.Vector4(0.78f, 0.82f, 0.88f, 1f));
        ImGui.BeginTooltip();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new System.Numerics.Vector2(12, 10));
        DrawItemIcon(itemId, 48);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(item.Value.Name.ToString());
        ImGui.TextDisabled($"物品 ID：{itemId}");
        ImGui.EndGroup();
        ImGui.Separator();
        ImGui.Text($"物品等级：{item.Value.LevelItem.RowId}");
        ImGui.Text($"装备品级：{item.Value.LevelEquip}");
        ImGui.Text($"稀有度：{item.Value.Rarity}");
        ImGui.Text($"搜索分类：{item.Value.ItemSearchCategory.RowId}");
        ImGui.Text($"物品分类：{item.Value.ItemUICategory.RowId}");
        ImGui.Text($"可交易：{(item.Value.IsUntradable ? "否" : "是")}");
        ImGui.Text(item.Value.IsUntradable ? "不可交易" : "可交易");

        if (item.Value.MateriaSlotCount > 0)
            ImGui.Text($"魔晶石：可镶嵌 {item.Value.MateriaSlotCount} 个");

        DrawOptionalItemFields(item.Value,
            ("物理防御", "DefensePhys", "PhysicalDefense"),
            ("魔法防御", "DefenseMag", "MagicDefense"),
            ("物理攻击", "DamagePhys", "PhysicalDamage"),
            ("魔法攻击", "DamageMag", "MagicDamage"),
            ("攻击间隔", "DelayMs", "Delay"));

        ImGui.Separator();
        ImGui.Text("制作与修理");
        DrawOptionalItemFields(item.Value,
            ("耐久度", "Durability", "MaxDurability"),
            ("精制度", "Craftsmanship", "Control"),
            ("修理等级", "RepairClassJob", "RepairClass"),
            ("收购价格", "PriceLow", "PriceMid"));

        var classJobSheet = dataManager.GetExcelSheet<ClassJobCategory>();
        var classJobCategory = classJobSheet?.GetRowOrDefault(item.Value.ClassJobCategory.RowId);
        if (classJobCategory.HasValue)
        {
            var jobs = GetClassJobNames(classJobCategory.Value);
            if (jobs.Count > 0)
                ImGui.TextWrapped($"使用职业：{string.Join("、", jobs)}");
        }

        DrawBaseParameterLines(item.Value);
        var description = item.Value.Description.ToString();
        if (!string.IsNullOrWhiteSpace(description))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(description);
        }
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        ImGui.EndTooltip();
    }

    private static string? ReadItemNumber<T>(T item, params string[] names)
    {
        foreach (var name in names)
        {
            var property = typeof(T).GetProperty(name);
            if (property == null)
                continue;
            var value = property.GetValue(item);
            if (value == null)
                continue;
            var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(text) && text != "0")
                return text;
        }
        return null;
    }

    private static void DrawOptionalItemFields<T>(T item, params (string Label, string Name, string Fallback)[] fields)
    {
        foreach (var field in fields)
        {
            var value = ReadItemNumber(item, field.Name, field.Fallback);
            if (!string.IsNullOrWhiteSpace(value))
                ImGui.Text($"{field.Label}：{value}");
        }
    }

    private unsafe bool TryShowNativeItemDetail(uint itemId)
    {
        var agent = AgentItemDetail.Instance();
        if (agent == null || itemId == 0)
            return false;

        agent->DetailKind = DetailKind.ItemSearchResult;
        agent->TypeOrId = itemId;
        agent->ItemId = itemId;
        agent->Index = 0;
        agent->BuyQuantity = -1;
        agent->MaxStackSize = 99;
        agent->Flag1 = 0;
        agent->Flag2 = 1;
        agent->Flag3 = 0;
        agent->Update(1);
        agent->ShowAddon();
        agent->Show();
        var addon = gameGui.GetAddonByName<AddonItemDetail>("ItemDetail");
        if (addon != null)
        {
            addon->Open(0);
            var mouse = ImGui.GetMousePos();
            addon->SetPosition(
                (short)Math.Clamp(mouse.X + 18f, short.MinValue, short.MaxValue),
                (short)Math.Clamp(mouse.Y + 18f, short.MinValue, short.MaxValue));
            addon->Show(disableShowTransition: true, unsetShowHideFlags: 0);
        }
        nativeItemDetailShown = agent->IsAddonShown();
        if (!nativeItemDetailShown)
        {
            // Some client builds only accept the direct-ID detail kind when
            // the market result addon has not initialized its item context.
            agent->DetailKind = DetailKind.ItemId;
            agent->TypeOrId = itemId;
            agent->ItemId = itemId;
            agent->Flag2 = 1;
            agent->Flag3 = 0;
            agent->ShowAddon();
            agent->Show();
            nativeItemDetailShown = agent->IsAddonShown();
        }
        nativeDetailHoverThisFrame = nativeItemDetailShown;
        if (!nativeItemDetailShown && !nativeDetailRetryQueued)
        {
            nativeDetailRetryQueued = true;
            _ = framework.RunOnTick(() =>
            {
                nativeDetailRetryQueued = false;
                TryShowNativeItemDetail(itemId);
            }, delay: TimeSpan.FromMilliseconds(50));
        }
        return nativeItemDetailShown;
    }

    private unsafe void HideNativeItemDetail()
    {
        var addon = gameGui.GetAddonByName<AddonItemDetail>("ItemDetail");
        if (addon != null)
            addon->Hide(disableHideTransition: true, callCloseCallback: false, setShowHideFlags: 0);
        var agent = AgentItemDetail.Instance();
        if (agent != null && agent->IsAddonShown())
            agent->HideAddon();
    }


    private void DrawBaseParameterLines(Item item)
    {
        var baseParamSheet = dataManager.GetExcelSheet<BaseParam>();
        if (baseParamSheet == null)
            return;

        var rows = new List<(string Name, short Value)>();
        for (var i = 0; i < 6 && i < item.BaseParamValue.Count; i++)
        {
            var value = item.BaseParamValue[i];
            var row = baseParamSheet.GetRowOrDefault(item.BaseParam[i].RowId);
            if (value != 0 && row.HasValue &&
                !row.Value.Name.ToString().Contains("物理基本性能", StringComparison.Ordinal) &&
                !row.Value.Name.ToString().Contains("魔法基本性能", StringComparison.Ordinal))
                rows.Add((row.Value.Name.ToString(), value));
        }
        if (rows.Count == 0)
            return;

        ImGui.Separator();
        ImGui.Text("特殊");
        if (ImGui.BeginTable("nativeItemParameters", 2, ImGuiTableFlags.SizingStretchProp))
        {
            for (var i = 0; i < rows.Count; i += 2)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{rows[i].Name} +{rows[i].Value}");
                if (i + 1 < rows.Count)
                {
                    ImGui.TableNextColumn();
                    ImGui.Text($"{rows[i + 1].Name} +{rows[i + 1].Value}");
                }
            }
            ImGui.EndTable();
        }
    }

    private void DrawLegacyBaseParameterLines(Item item)
    {
        var baseParamSheet = dataManager.GetExcelSheet<BaseParam>();
        if (baseParamSheet == null)
            return;

        var hasParameter = false;
        for (var i = 0; i < 6 && i < item.BaseParamValue.Count; i++)
        {
            var value = item.BaseParamValue[i];
            if (value == 0)
                continue;
            var row = baseParamSheet.GetRowOrDefault(item.BaseParam[i].RowId);
            if (!row.HasValue)
                continue;
            if (!hasParameter)
            {
                ImGui.Separator();
                ImGui.Text("属性");
                hasParameter = true;
            }
            ImGui.Text($"{row.Value.Name}：{value}");
        }

        for (var i = 0; i < 6 && i < item.BaseParamValueSpecial.Count; i++)
        {
            var value = item.BaseParamValueSpecial[i];
            if (value == 0)
                continue;
            var row = baseParamSheet.GetRowOrDefault(item.BaseParamSpecial[i].RowId);
            if (!row.HasValue)
                continue;
            if (!hasParameter)
            {
                ImGui.Separator();
                ImGui.Text("特殊属性");
                hasParameter = true;
            }
            ImGui.Text($"{row.Value.Name}：{value}");
        }
    }

    private static List<string> GetClassJobNames(ClassJobCategory category)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GLA"] = "剑术师", ["PGL"] = "格斗家", ["MRD"] = "斧术师", ["LNC"] = "枪术师",
            ["ARC"] = "弓箭手", ["CNJ"] = "幻术师", ["THM"] = "咒术师", ["ROG"] = "双剑师",
            ["PLD"] = "骑士", ["MNK"] = "武僧", ["WAR"] = "战士", ["DRG"] = "龙骑士",
            ["BRD"] = "吟游诗人", ["WHM"] = "白魔法师", ["BLM"] = "黑魔法师", ["NIN"] = "忍者",
            ["SCH"] = "学者", ["SMN"] = "召唤师", ["MCH"] = "机工士", ["DRK"] = "暗黑骑士",
            ["AST"] = "占星术士", ["SAM"] = "武士", ["RDM"] = "赤魔法师", ["GNB"] = "绝枪战士",
            ["DNC"] = "舞者", ["RPR"] = "钐镰客", ["SGE"] = "贤者", ["VPR"] = "蝰蛇剑士",
            ["PCT"] = "绘灵法师", ["BLU"] = "青魔法师", ["CRP"] = "木工师", ["BSM"] = "锻铁师",
            ["ARM"] = "铸甲师", ["GSM"] = "雕金师", ["LTW"] = "制革师", ["WVR"] = "裁衣师",
            ["ALC"] = "炼金术士", ["CUL"] = "厨师", ["MIN"] = "采矿工", ["BTN"] = "园艺工", ["FSH"] = "捕鱼人",
        };
        var result = new List<string>();
        foreach (var property in typeof(ClassJobCategory).GetProperties())
        {
            if (property.PropertyType != typeof(bool) || property.GetValue(category) is not true)
                continue;
            if (names.TryGetValue(property.Name, out var name))
                result.Add(name);
        }
        return result;
    }


    private void RequestCustomMarketData(bool force)
    {
        if (!force && DateTime.UtcNow - customLastRequest < TimeSpan.FromSeconds(1))
            return;
        var info = InfoProxyItemSearch.Instance();
        var agent = AgentItemSearch.Instance();
        if (info == null)
        {
            customStatus = "InfoProxyItemSearch 尚未初始化。";
            return;
        }

        if (agent != null)
            agent->ResultItemId = customSearchItemId;
        info->SearchItemId = customSearchItemId;
        info->ClearListData();
        var requested = info->RequestData();
        customLastRequest = DateTime.UtcNow;
        customWaitingSince = customLastRequest;
        customRequestAttempts++;
        log.Information("Custom market request: itemId={ItemId}, requested={Requested}, waiting={Waiting}, listingCount={ListingCount}, attempt={Attempt}",
            customSearchItemId, requested, info->WaitingForListings, info->ListingCount, customRequestAttempts);
        customStatus = requested ? "已请求报价，等待服务器响应……" : "报价请求未提交。";
    }

    private void UpdateCustomMarket()
    {
        if (!customMarketOpen || customSearchItemId == 0)
            return;
        var info = InfoProxyItemSearch.Instance();
        if (info == null)
            return;
        if (info->WaitingForListings && info->ListingCount == 0)
        {
            if (customWaitingSince != default && DateTime.UtcNow - customWaitingSince > TimeSpan.FromSeconds(5) &&
                DateTime.UtcNow - customLastRequest > TimeSpan.FromSeconds(2) && customRequestAttempts < 4)
                RequestCustomMarketData(force: true);
            return;
        }
        if (info->ListingCount == 0)
            return;
        ulong signature = 1469598103934665603UL;
        signature ^= (uint)customQualityFilter;
        signature *= 1099511628211UL;
        for (var i = 0; i < info->ListingCount; i++)
        {
            var row = info->Listings[i];
            signature ^= row.ListingId;
            signature *= 1099511628211UL;
            signature ^= row.UnitPrice;
            signature *= 1099511628211UL;
            signature ^= row.Quantity;
            signature *= 1099511628211UL;
            signature ^= (uint)(listingQuality.TryGetValue(row.ListingId, out var rowIsHq) && rowIsHq ? 1 : 0);
            signature *= 1099511628211UL;
        }
        if (signature == customListingsSignature)
            return;

        customListingsSignature = signature;
        customListings.Clear();
        for (var i = 0; i < info->ListingCount; i++)
        {
            var listing = info->Listings[i];
            if (listing.ListingId != 0 && listing.ItemId == customSearchItemId && listing.UnitPrice != 0 &&
                MatchesQuality(listing.ListingId))
                customListings.Add(listing);
        }
        customStatus = customListings.Count == 0 ? "当前没有匹配报价。" : "报价已更新，可选择商品购买。";
        log.Information("Custom market listings updated: itemId={ItemId}, listingCount={ListingCount}",
            customSearchItemId, customListings.Count);
    }

    private void SubmitCustomMarketPurchase(MarketBoardListing listing)
    {
        if (!MatchesQuality(listing.ListingId))
        {
            customStatus = customQualityFilter == 2
                ? "当前报价不是 HQ，已拒绝购买。"
                : "当前报价不是 NQ，已拒绝购买。";
            return;
        }
        if (listing.ItemId != customSearchItemId || listing.ListingId == 0 || listing.UnitPrice == 0)
        {
            customStatus = "报价已过期或不属于当前搜索商品，请刷新。";
            return;
        }
        var info = InfoProxyItemSearch.Instance();
        if (info == null || (info->WaitingForListings && info->ListingCount == 0))
        {
            customStatus = "报价仍在刷新，请稍候。";
            return;
        }
        var setResult = info->SetLastPurchasedItem(&listing);
        var sendResult = setResult && info->SendPurchaseRequestPacket();
        customStatus = sendResult ? "购买请求已发送，等待服务器结果。" : "购买请求发送失败，请刷新报价。";
        log.Information("Custom market purchase: itemId={ItemId}, listingId={ListingId}, setLastPurchased={SetResult}, sendPurchase={SendResult}",
            listing.ItemId, listing.ListingId, setResult, sendResult);
    }

    public void OpenSearchShell()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            _ = framework.RunOnTick(OpenSearchShell);
            return;
        }

        try
        {
            var agent = AgentItemSearch.Instance();
            if (agent == null)
            {
                Status = "ItemSearch Agent 尚未初始化，请进入角色后重试。";
                return;
            }

            BeginSession();
            agent->Show();
            Status = string.Empty;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to open ItemSearch shell");
            Status = "打开 ItemSearch 失败。";
        }
    }

    public void OpenSearchAndRequest()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            _ = framework.RunOnTick(OpenSearchAndRequest);
            return;
        }

        if (ItemId == 0)
        {
            Status = "请先填写有效的物品 ID。";
            return;
        }

        try
        {
            var agent = AgentItemSearch.Instance();
            var infoProxy = InfoProxyItemSearch.Instance();
            if (agent == null || infoProxy == null)
            {
                Status = "ItemSearch Agent/InfoProxy 尚未初始化。";
                return;
            }

            BeginSession();
            agent->ResultItemId = ItemId;
            infoProxy->SearchItemId = ItemId;
            infoProxy->ClearListData();
            agent->Show();
            requestAttempts = 0;
            Status = $"已打开 ItemSearch，等待请求物品 {ItemId} 的服务器报价。";
            _ = framework.RunOnTick(RequestWhenReady, delay: RandomizedDelayMilliseconds(250));
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to open ItemSearch and request market data");
            Status = "打开并请求服务器报价失败。";
        }
    }

    public void Update()
    {
        if (!framework.IsInFrameworkUpdateThread)
            return;

        UpdateCustomMarket();
        if (!sessionActive)
            return;

        ObservePurchaseCandidate();
        TrySubmitFallbackPurchase();
        TryCloseStandaloneResult();

        if (DateTime.UtcNow - sessionOpenedAt > TimeSpan.FromSeconds(1) &&
            !IsSessionOpen() &&
            !fallbackQueued &&
            resultCloseAt == default &&
            !purchaseRequestObserved)
            RetireSession("market windows closed");

        if (purchaseRequestObserved && DateTime.UtcNow - purchaseSentAt > TimeSpan.FromSeconds(15))
        {
            purchaseRequestObserved = false;
            Status = "购买请求发出超过 15 秒仍未收到服务器结果；报价可能已过期或市场会话无效。";
        }
    }

    private void BeginSession()
    {
        sessionActive = true;
        purchaseRequestObserved = false;
        sessionOpenedAt = DateTime.UtcNow;
        resultCloseAt = default;
        resultCloseAttempts = 0;
        resultCloseReason = string.Empty;
        pendingListingValid = false;
        listingCaptureRetryQueued = false;
        listingCaptureRetryDeadline = default;
        listingRefreshAt = default;
        listingWaitingSince = default;
        listingRefreshAttempts = 0;
        pendingConfirmationListingIndex = -1;
        pendingListingIndex = -1;
        observedListings.Clear();
        observedSearchItemId = 0;
        pendingConfirmationAddon = nint.Zero;
        fallbackQueued = false;
        listingCaptureRetryQueued = false;
        listingCaptureRetryDeadline = default;
        listingRefreshAt = default;
        pendingConfirmationListingIndex = -1;
        pendingListingIndex = -1;
        observedListings.Clear();
        observedSearchItemId = 0;
        fallbackAt = default;
        fallbackDeadline = default;
        listingRefreshAt = default;
        fallbackAttempts = 0;
        listingRefreshAt = default;
        lastObservedListingId = 0;
        lastCompletedListingId = 0;
        activePurchaseListingId = 0;
        purchaseCompletedAt = default;
        marketBlockStateCaptured = false;
        capturedResultBlockingAddons = 0;
        capturedResultBlockedParentId = 0;
        capturedSearchBlockingAddons = 0;
        capturedSearchBlockedParentId = 0;
    }

    private bool IsSessionOpen()
    {
        var search = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        if (search != null && search->IsReady && search->IsVisible)
            return true;
        var result = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult");
        if (result != null && result->IsReady && result->IsVisible)
            return true;
        var yesno = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        return yesno != null && yesno->IsReady && yesno->IsVisible;
    }

    private void RetireSession(string reason)
    {
        if (!sessionActive)
            return;
        log.Information("Released standalone market session: reason={Reason}", reason);
        sessionActive = false;
        purchaseRequestObserved = false;
        pendingListingValid = false;
        pendingListingIndex = -1;
        pendingConfirmationAddon = nint.Zero;
        fallbackQueued = false;
        resultCloseAt = default;
        resultCloseAttempts = 0;
        resultCloseReason = string.Empty;
        fallbackDeadline = default;
        fallbackAttempts = 0;
    }

    private void OnPurchaseRequested(IMarketBoardPurchaseHandler purchase)
    {
        if (!sessionActive)
            return;
        purchaseRequestObserved = true;
        purchaseSentAt = DateTime.UtcNow;
        activePurchaseListingId = purchase.ListingId;
        fallbackQueued = false;
        fallbackDeadline = default;
        pendingListingValid = false;
        pendingListingIndex = -1;
        Status = "游戏原生购买请求已提交，等待服务器结果。";
        log.Information("Standalone market native purchase observed: listingId={ListingId}, itemId={ItemId}, quantity={Quantity}, unitPrice={UnitPrice}, cityId={CityId}",
            purchase.ListingId, purchase.CatalogId, purchase.ItemQuantity, purchase.PricePerUnit, purchase.RetainerCityId);
    }

    private void OnItemPurchased(IMarketBoardPurchase purchase)
    {
        if (!sessionActive)
            return;

        // MarketBoard events originate from the packet handler.  Do not read
        // or mutate native Atk objects from that callback; queue the UI state
        // transition on the framework thread instead.
        if (!framework.IsInFrameworkUpdateThread)
        {
            _ = framework.RunOnTick(() => OnItemPurchased(purchase));
            return;
        }

        purchaseRequestObserved = false;
        if (activePurchaseListingId != 0)
            lastCompletedListingId = activePurchaseListingId;
        activePurchaseListingId = 0;
        pendingListingValid = false;
        pendingListingIndex = -1;
        fallbackQueued = false;
        fallbackDeadline = default;
        fallbackAttempts = 0;
        purchaseCompletedAt = DateTime.UtcNow;
        QueueStandaloneResultClose($"purchase completed ({purchase.CatalogId})", 150);
        Status = $"Purchase completed; closing the market result page for item {purchase.CatalogId}.";
        log.Information("Standalone market purchase completed: itemId={ItemId}, quantity={Quantity}", purchase.CatalogId, purchase.ItemQuantity);
    }
    private void QueueStandaloneResultClose(string reason, int delayMilliseconds)
    {
        resultCloseReason = reason;
        resultCloseAttempts = 0;
        resultCloseAt = DateTime.UtcNow.AddMilliseconds(Math.Max(0, delayMilliseconds));
        log.Information("Queued standalone market result close: reason={Reason}, delayMilliseconds={DelayMilliseconds}",
            reason,
            delayMilliseconds);
    }

    private void TryCloseStandaloneResult()
    {
        if (resultCloseAt == default || DateTime.UtcNow < resultCloseAt)
            return;

        if (++resultCloseAttempts > 20)
        {
            log.Warning("Timed out closing standalone market result: reason={Reason}", resultCloseReason);
            resultCloseAt = default;
            resultCloseReason = string.Empty;
            return;
        }

        var confirmation = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        if (confirmation != null && confirmation->IsReady && confirmation->IsVisible)
        {
            resultCloseAt = DateTime.UtcNow.AddMilliseconds(100);
            return;
        }

        var result = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult");
        if (result == null || !result->IsReady || !result->IsVisible)
        {
            log.Information("Standalone market result already closed: reason={Reason}", resultCloseReason);
            resultCloseAt = default;
            resultCloseReason = string.Empty;
            return;
        }

        // Close through the native addon virtual function so ItemSearch is
        // restored by the game's own parent/focus teardown. Do not rebuild
        // collision nodes or write ParentId/DrawOrderIndex before this call.
        resultCloseAt = default;
        var closed = result->Close(fireCallback: true);
        log.Information("Closed standalone market result after terminal action: reason={Reason}, closeResult={CloseResult}, visibleAfter={VisibleAfter}",
            resultCloseReason,
            closed,
            result->IsVisible);
        if (!closed || result->IsVisible)
        {
            resultCloseAt = DateTime.UtcNow.AddMilliseconds(150);
            return;
        }

        resultCloseReason = string.Empty;
    }

    private void MarkMarketResultClosed()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            _ = framework.RunOnTick(MarkMarketResultClosed);
            return;
        }

        pendingConfirmationAddon = nint.Zero;
        pendingListingValid = false;
        fallbackQueued = false;
        resultCloseAt = default;
        resultCloseAttempts = 0;
        resultCloseReason = string.Empty;
        Status = string.Empty;
        log.Information("Handled native ItemSearchResult close callback without a second native close.");
    }

    private void NormalizeStandaloneResultParent(
        AddonItemSearchResult* result,
        AddonItemSearch* search,
        bool confirmationVisible,
        bool clearBlockedParent = false)
    {
        if (result == null || search == null)
            return;

        // Keep ParentId untouched. ItemSearchResult is opened as a separate
        // native addon in this path; changing its parent to ItemSearch makes
        // the game route row/X callbacks through the wrong agent and prevents
        // SelectYesno from opening.
        var originalParentId = result->ParentId;
        var originalBlockedParentId = result->BlockedParentId;

        // BlockedParentId is the standalone modal-input relation. Keep it
        // while SelectYesno is active and clear it only at the native row
        // callback or after the modal closes.
        var blockedParentChanged = clearBlockedParent && !confirmationVisible &&
                                   result->BlockedParentId == search->Id;
        if (blockedParentChanged)
            result->BlockedParentId = 0;

        if (!marketBlockStateCaptured)
        {
            marketBlockStateCaptured = true;
            capturedResultBlockingAddons = result->NumBlockingAddons;
            capturedResultBlockedParentId = originalBlockedParentId;
            capturedSearchBlockingAddons = search->NumBlockingAddons;
            capturedSearchBlockedParentId = search->BlockedParentId;
            log.Information("Normalized standalone market result hierarchy: originalParentId={OriginalParentId}, originalBlockedParentId={OriginalBlockedParentId}, parentId={ParentId}, searchId={SearchId}, searchBlocking={SearchBlocking}",
                originalParentId,
                originalBlockedParentId,
                result->ParentId,
                search->Id,
                search->NumBlockingAddons);
        }
        else if (blockedParentChanged)
        {
            log.Information("Updated standalone market result hierarchy: originalParentId={OriginalParentId}, parentId={ParentId}, originalBlockedParentId={OriginalBlockedParentId}, blockedParentId={BlockedParentId}, clearBlockedParent={ClearBlockedParent}",
                originalParentId,
                result->ParentId,
                originalBlockedParentId,
                result->BlockedParentId,
                clearBlockedParent);
        }
    }

    private unsafe void PrepareResultForNativeClose(AddonItemSearchResult* result)
    {
        if (result == null)
            return;

        // Leave ParentId and BlockedParentId untouched before the native X
        // callback.  The close callback uses the original modal relation to
        // release the addon; changing it here can route the callback through
        // a partially torn-down tree and crash the client.
        var search = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        log.Information("Preserving standalone market result state before native close: parentId={ParentId}, blockedParentId={BlockedParentId}, searchId={SearchId}",
            result->ParentId,
            result->BlockedParentId,
            search == null ? (ushort)0 : search->Id);
    }

    private void ObservePurchaseCandidate()
    {
        var result = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult");
        var info = InfoProxyItemSearch.Instance();
        var confirmationAddon = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        var confirmationVisible = confirmationAddon != null && confirmationAddon->IsReady && confirmationAddon->IsVisible;
        var search = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        // 与抓推插件一致：确认框打开时 ItemSearchResult 可能暂时不可见，
        // 但其 Results/InfoProxy 数据仍然有效，必须继续保存报价快照。
        if (result == null || !result->IsReady || result->Results == null ||
            (!result->IsVisible && !confirmationVisible) ||
            info == null || info->WaitingForListings || info->ListingCount == 0)
            return;

        var activeSearchItemId = GetActiveSearchItemId(info);

        // A market session can reuse the same ItemSearchResult addon while
        // the user changes the searched item.  Never carry cached rows from
        // the previous search into the next confirmation callback.
        if (activeSearchItemId != 0 && observedSearchItemId != 0 &&
            activeSearchItemId != observedSearchItemId &&
            !confirmationVisible && !purchaseRequestObserved && !fallbackQueued)
        {
            observedListings.Clear();
            pendingListingValid = false;
            pendingListingIndex = -1;
            pendingConfirmationListingIndex = -1;
            pendingConfirmationAddon = nint.Zero;
            log.Information("Cleared stale market row cache after search change: previousSearchItemId={PreviousSearchItemId}, searchItemId={SearchItemId}",
                observedSearchItemId,
                activeSearchItemId);
        }
        if (activeSearchItemId != 0)
            observedSearchItemId = activeSearchItemId;

        // Keep a short-lived snapshot of every currently displayed row.  The
        // native callback can temporarily invalidate InfoProxy while opening
        // SelectYesno; the cached row is then used instead of row zero.
        for (var row = 0; row < info->ListingCount; row++)
        {
            var cached = info->Listings[row];
            if (cached.ListingId != 0 && cached.ItemId != 0 && cached.UnitPrice != 0 &&
                (activeSearchItemId == 0 || cached.ItemId == activeSearchItemId))
                observedListings[row] = cached;
        }

        NormalizeStandaloneResultParent(result, search, confirmationVisible, clearBlockedParent: false);

        // A row callback has selected a specific listing.  Do not let the
        // background observer overwrite that selection with the currently
        // visible first row while the native confirmation is being created.
        if (pendingConfirmationListingIndex != -1 &&
            !confirmationVisible && !purchaseRequestObserved && !fallbackQueued)
            return;

        var index = result->Results->SelectedItemIndex;
        if (index < 0 || index >= info->ListingCount)
            index = 0;
        var listing = info->Listings[index];
        if (listing.ListingId == 0 || listing.ItemId == 0 || listing.UnitPrice == 0)
            return;

        if (activeSearchItemId != 0 && listing.ItemId != activeSearchItemId)
        {
            // InfoProxy 切换搜索目标时可能短暂保留上一轮报价，禁止把旧报价
            // 作为当前搜索物品的购买快照。
            if (!confirmationVisible && !purchaseRequestObserved && !fallbackQueued)
            {
                pendingListingValid = false;
                pendingConfirmationAddon = nint.Zero;
            }

            log.Information("Ignored stale market listing during search transition: searchItemId={SearchItemId}, listingItemId={ListingItemId}, listingId={ListingId}",
                activeSearchItemId,
                listing.ItemId,
                listing.ListingId);
            return;
        }

        // 同一市场会话内切换搜索物品时，旧报价不能继续用于新确认框。
        // 只有确认框已经打开、购买请求已发出或备用提交已排队时，才冻结当前快照。
        if (pendingListingValid &&
            !purchaseRequestObserved &&
            !fallbackQueued &&
            !confirmationVisible &&
            pendingListing.ItemId != listing.ItemId)
        {
            log.Information("Market search item changed; discarding stale pending listing: oldItemId={OldItemId}, oldListingId={OldListingId}, newItemId={NewItemId}, newListingId={NewListingId}",
                pendingListing.ItemId,
                pendingListing.ListingId,
                listing.ItemId,
                listing.ListingId);
            pendingListingValid = false;
            pendingConfirmationAddon = nint.Zero;
            marketBlockStateCaptured = false;
        }

        // Once a confirmation is open, keep the exact listing that produced it.
        // The server refresh can reorder/replace InfoProxy.Listings before the
        // yes/no callback arrives; replacing the snapshot here causes a valid
        // confirmation to submit a different (or stale) listing.
        if (!marketBlockStateCaptured)
        {
            if (search != null && search->IsReady)
            {
                marketBlockStateCaptured = true;
                capturedResultBlockingAddons = result->NumBlockingAddons;
                capturedResultBlockedParentId = result->BlockedParentId;
                capturedSearchBlockingAddons = search->NumBlockingAddons;
                capturedSearchBlockedParentId = search->BlockedParentId;
                log.Information("Captured native market parent blocking state: resultBlocking={ResultBlocking}, resultBlockedParentId={ResultBlockedParentId}, searchBlocking={SearchBlocking}, searchBlockedParentId={SearchBlockedParentId}",
                    capturedResultBlockingAddons,
                    capturedResultBlockedParentId,
                    capturedSearchBlockingAddons,
                    capturedSearchBlockedParentId);
            }
        }
        if (!pendingListingValid && !purchaseRequestObserved && !fallbackQueued && !confirmationVisible &&
            !IsRecentlyCompletedListing(listing.ListingId))
        {
            pendingListing = listing;
            pendingListingValid = true;
            pendingListingIndex = index;
        }
        log.Information("Observed standalone market listing: index={Index}, listingId={ListingId}, itemId={ItemId}, quantity={Quantity}, unitPrice={UnitPrice}, retainerId={RetainerId}, townId={TownId}",
            index, listing.ListingId, listing.ItemId, listing.Quantity, listing.UnitPrice, listing.RetainerId, listing.TownId);
        if (lastObservedListingId != listing.ListingId)
        {
            lastObservedListingId = listing.ListingId;
        }
        if (confirmationVisible && confirmationAddon != null)
        {
            pendingConfirmationAddon = (nint)confirmationAddon;
            log.Information("Observed standalone market confirmation: addon={AddonAddress}, listingId={ListingId}, itemId={ItemId}",
                pendingConfirmationAddon,
                pendingListingValid ? pendingListing.ListingId : listing.ListingId,
                pendingListingValid ? pendingListing.ItemId : listing.ItemId);
        }
    }

    private void TryCaptureListingForConfirmation(int selectedIndex = -1)
    {
        if (purchaseRequestObserved)
            return;

        var result = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult");
        var info = InfoProxyItemSearch.Instance();
        if (result == null || !result->IsReady || result->Results == null ||
            info == null || info->WaitingForListings || info->ListingCount == 0)
        {
            TryCaptureCachedListing(selectedIndex);
            return;
        }

        var nativeSelectedIndex = result->Results->SelectedItemIndex;
        var index = nativeSelectedIndex >= 0
            ? nativeSelectedIndex
            : selectedIndex;
        if (index < 0 || index >= info->ListingCount)
        {
            log.Information("Market listing index is not synchronized yet: requestedIndex={RequestedIndex}, listingCount={ListingCount}, nativeSelectedIndex={NativeSelectedIndex}",
                selectedIndex,
                info->ListingCount,
                nativeSelectedIndex);
            TryCaptureCachedListing(selectedIndex);
            return;
        }
        var listing = info->Listings[index];
        if (listing.ListingId == 0 || listing.ItemId == 0 || listing.UnitPrice == 0 ||
            IsRecentlyCompletedListing(listing.ListingId))
        {
            TryCaptureCachedListing(index);
            return;
        }

        var activeSearchItemId = GetActiveSearchItemId(info);
        if (activeSearchItemId != 0 && listing.ItemId != activeSearchItemId)
        {
            log.Information("Ignored stale market listing at confirmation callback: searchItemId={SearchItemId}, listingItemId={ListingItemId}, listingId={ListingId}",
                activeSearchItemId,
                listing.ItemId,
                listing.ListingId);
            return;
        }

        if (pendingListingValid &&
            pendingListing.ItemId == listing.ItemId &&
            pendingListing.ListingId == listing.ListingId)
        {
            return;
        }

        pendingListing = listing;
        pendingListingValid = true;
        pendingConfirmationListingIndex = index;
        log.Information("Captured market listing at confirmation callback: index={Index}, listingId={ListingId}, itemId={ItemId}, quantity={Quantity}, unitPrice={UnitPrice}, retainerId={RetainerId}, townId={TownId}",
            index, listing.ListingId, listing.ItemId, listing.Quantity, listing.UnitPrice, listing.RetainerId, listing.TownId);
    }

    private void TryCaptureCachedListing(int index)
    {
        if (index < 0 || !observedListings.TryGetValue(index, out var listing))
            return;
        if (listing.ListingId == 0 || listing.ItemId == 0 || listing.UnitPrice == 0 ||
            IsRecentlyCompletedListing(listing.ListingId))
            return;
        if (pendingListingValid && pendingListing.ListingId == listing.ListingId)
            return;

        pendingListing = listing;
        pendingListingValid = true;
        pendingListingIndex = index;
        pendingConfirmationListingIndex = index;
        log.Information("Captured cached market listing at confirmation callback: index={Index}, listingId={ListingId}, itemId={ItemId}, quantity={Quantity}, unitPrice={UnitPrice}",
            index, listing.ListingId, listing.ItemId, listing.Quantity, listing.UnitPrice);
    }

    private void QueueListingCaptureRetryIfNeeded()
    {
        if (purchaseRequestObserved || listingCaptureRetryQueued)
            return;

        listingCaptureRetryQueued = true;
        listingCaptureRetryDeadline = DateTime.UtcNow.AddSeconds(5);
        _ = framework.RunOnTick(
            () =>
            {
                listingCaptureRetryQueued = false;
                if (purchaseRequestObserved)
                    return;

                TryCaptureListingForConfirmation(pendingConfirmationListingIndex);
                if (DateTime.UtcNow < listingCaptureRetryDeadline && !purchaseRequestObserved)
                    QueueListingCaptureRetryIfNeeded();
            },
            delay: RandomizedDelayMilliseconds(100));
    }

    private static unsafe uint GetActiveSearchItemId(InfoProxyItemSearch* info)
    {
        if (info != null && info->SearchItemId != 0)
            return info->SearchItemId;

        var agent = AgentItemSearch.Instance();
        return agent == null ? 0u : agent->ResultItemId;
    }

    private bool IsRecentlyCompletedListing(ulong listingId)
    {
        return listingId != 0 && listingId == lastCompletedListingId &&
               DateTime.UtcNow - purchaseCompletedAt < TimeSpan.FromSeconds(2);
    }

    private unsafe bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close)
    {
        var name = string.Empty;
        try
        {
            if (addon != null)
                name = addon->NameString;
        }
        catch { }

        var isConfirmation = name.Equals("SelectYesno", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("SelectYesno", StringComparison.OrdinalIgnoreCase);
        var callbackValue = values != null && valueCount > 0 ? values[0].Int : int.MinValue;
        var callbackValue2 = values != null && valueCount > 1 ? values[1].Int : int.MinValue;
        var isItemSearchResult = name.Equals("ItemSearchResult", StringComparison.OrdinalIgnoreCase);
        var resultForCallback = isItemSearchResult && addon != null
            ? (AddonItemSearchResult*)addon
            : null;
        if (sessionActive && isItemSearchResult)
        {
            log.Information("Native ItemSearchResult callback observed: value={Value}, value2={Value2}, valueCount={ValueCount}, close={Close}, selectedIndex={SelectedIndex}, listEnabled={ListEnabled}, listClickable={ListClickable}",
                callbackValue,
                callbackValue2,
                valueCount,
                close,
                resultForCallback == null ? -1 : resultForCallback->Results == null ? -1 : resultForCallback->Results->SelectedItemIndex,
                resultForCallback != null && resultForCallback->Results != null && resultForCallback->Results->IsItemInteractionEnabled,
                resultForCallback != null && resultForCallback->Results != null && resultForCallback->Results->IsItemClickEnabled);
        }
        if (sessionActive && isItemSearchResult && callbackValue == 2)
        {
            if (resultForCallback != null)
            {
                // Keep the native modal relation while the row callback is
                // dispatched.  The game uses it to route the callback and
                // create SelectYesno; clearing it here prevents the native
                // confirmation from opening in the standalone path.
                var searchForCallback = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
                NormalizeStandaloneResultParent(resultForCallback, searchForCallback, confirmationVisible: false, clearBlockedParent: false);
            }
            // -2 means a row was selected but the callback did not expose a
            // usable index yet; a later native SelectedItemIndex can still
            // resolve it without falling back to the first row.
            pendingConfirmationListingIndex = callbackValue2 >= 0 ? callbackValue2 : -2;
            // Keep the observer snapshot only when it belongs to the same
            // row.  A different row must never inherit the first-row listing.
            if (!pendingListingValid || pendingListingIndex != pendingConfirmationListingIndex)
            {
                pendingListingValid = false;
                pendingListingIndex = -1;
            }
            TryCaptureListingForConfirmation(pendingConfirmationListingIndex);
            QueueListingCaptureRetryIfNeeded();
        }
        if (sessionActive && isItemSearchResult && callbackValue == -2 && resultForCallback != null)
            PrepareResultForNativeClose(resultForCallback);
        var result = fireCallbackHook.OriginalDisposeSafe(addon, valueCount, values, close);

        if (sessionActive && isItemSearchResult && callbackValue == -2)
        {
            // The original callback owns the addon lifetime. Do not call
            // Close/Hide again from a delayed tick; the addon may already be
            // in teardown and a second close can dereference stale native
            // callback state.
            _ = framework.RunOnTick(MarkMarketResultClosed, delay: RandomizedDelayMilliseconds(10));
        }

        var isTrackedConfirmation = pendingConfirmationAddon == nint.Zero ||
                                     addon == null ||
                                     (nint)addon == pendingConfirmationAddon;
        if (sessionActive && isConfirmation && isTrackedConfirmation)
        {
            log.Information("Standalone market confirmation callback: value={Value}, valueCount={Count}, close={Close}, listingId={ListingId}, itemId={ItemId}",
                callbackValue, valueCount, close, pendingListing.ListingId, pendingListing.ItemId);
            pendingConfirmationAddon = nint.Zero;
            if (callbackValue == 0)
            {
                // Keep the result page entirely native until the server reports
                // the purchase. Repairing it while the confirmation is closing
                // can leave ItemSearchResult in the native drag-only state and
                // can steal focus from chat or the next modal.
                // The listing may have been stale when the row callback ran;
                // retry immediately while the confirmation is closing.
                TryCaptureListingForConfirmation(pendingConfirmationListingIndex);
                if (!pendingListingValid)
                    RequestCurrentMarketListings();
                fallbackQueued = true;
                fallbackAt = DateTime.UtcNow.AddMilliseconds(750);
                fallbackDeadline = DateTime.UtcNow.AddSeconds(10);
                fallbackAttempts = 0;
                listingWaitingSince = DateTime.UtcNow;
                listingRefreshAttempts = 0;
                QueueListingCaptureRetryIfNeeded();
                if (!pendingListingValid)
                    log.Warning("Purchase confirmation accepted without a valid listing snapshot yet: requestedIndex={RequestedIndex}",
                        pendingConfirmationListingIndex);
                Status = "已点击购买确认，等待原生购买上行；若原生链路未提交将由卫月接口补交。";
            }
            else
            {
                pendingListingValid = false;
                fallbackQueued = false;
                QueueStandaloneResultClose("purchase confirmation declined", 150);
                Status = "Purchase cancelled; closing the market result page.";
                log.Information("Purchase confirmation declined; queued native ItemSearchResult close.");
            }
        }

        return result;
    }

    private void TrySubmitFallbackPurchase()
    {
        if (!fallbackQueued || purchaseRequestObserved)
            return;

        if (!pendingListingValid)
        {
            if (DateTime.UtcNow >= listingRefreshAt)
            {
                var refreshInfo = InfoProxyItemSearch.Instance();
                if (refreshInfo != null && !refreshInfo->WaitingForListings)
                {
                    // Discard the stale native rows before asking for the
                    // current search again; otherwise InfoProxy can keep
                    // reporting the previous item's listings indefinitely.
                    refreshInfo->ClearListData();
                    var requested = refreshInfo->RequestData();
                    log.Information("Requested current market listings while waiting for selected row: searchItemId={SearchItemId}, requested={Requested}, waiting={Waiting}, listingCount={ListingCount}",
                        GetActiveSearchItemId(refreshInfo), requested, refreshInfo->WaitingForListings, refreshInfo->ListingCount);
                }
                else if (refreshInfo != null && refreshInfo->WaitingForListings &&
                         listingWaitingSince != default &&
                         DateTime.UtcNow - listingWaitingSince > TimeSpan.FromSeconds(2) &&
                         listingRefreshAttempts < 3)
                {
                    refreshInfo->ClearListData();
                    var requested = refreshInfo->RequestData();
                    listingRefreshAttempts++;
                    listingWaitingSince = DateTime.UtcNow;
                    log.Warning("Reset stuck market listing request: searchItemId={SearchItemId}, requested={Requested}, attempt={Attempt}",
                        GetActiveSearchItemId(refreshInfo), requested, listingRefreshAttempts);
                }
                // Do not issue another request while the previous native
                // request is still pending; repeated RequestData calls reset
                // the response state and make ListingCount oscillate.
                listingRefreshAt = DateTime.UtcNow.AddSeconds(1);
            }
            return;
        }

        if (DateTime.UtcNow < fallbackAt)
            return;

        if (fallbackDeadline != default && DateTime.UtcNow > fallbackDeadline)
        {
            fallbackQueued = false;
            fallbackDeadline = default;
            log.Warning("Standalone market purchase fallback expired without a valid outbound request: listingId={ListingId}, itemId={ItemId}",
                pendingListing.ListingId, pendingListing.ItemId);
            Status = "购买确认已关闭，但原生链路未提交购买请求；报价已过期，请重新选择。";
            pendingListingValid = false;
            return;
        }

        var info = InfoProxyItemSearch.Instance();
        if (info == null)
        {
            fallbackAt = DateTime.UtcNow.AddMilliseconds(200);
            return;
        }

        var purchase = pendingListing;
        var setResult = info->SetLastPurchasedItem(&purchase);
        var sendResult = setResult && info->SendPurchaseRequestPacket();
        fallbackQueued = false;
        if (sendResult)
        {
            purchaseRequestObserved = true;
            purchaseSentAt = DateTime.UtcNow;
            Status = "原生确认未提交购买上行，已通过卫月 InfoProxy 补交购买请求。";
            log.Information("Standalone market purchase fallback sent: setLastPurchased={SetResult}, sendPurchase={SendResult}, listingId={ListingId}, itemId={ItemId}, quantity={Quantity}, unitPrice={UnitPrice}, cityId={CityId}",
                setResult, sendResult, purchase.ListingId, purchase.ItemId, purchase.Quantity, purchase.UnitPrice, purchase.TownId);
            fallbackDeadline = default;
            fallbackAttempts = 0;
        }
        else
        {
            fallbackAttempts++;
            fallbackQueued = fallbackAttempts < 20;
            fallbackAt = DateTime.UtcNow.AddMilliseconds(200);
            Status = fallbackQueued
                ? "购买确认已关闭，正在重试提交购买请求。"
                : "购买确认已关闭，但卫月接口未能提交购买请求；请重新选择报价。";
            log.Warning("Standalone market purchase fallback failed: setLastPurchased={SetResult}, sendPurchase={SendResult}, listingId={ListingId}, itemId={ItemId}",
                setResult, sendResult, purchase.ListingId, purchase.ItemId);
        }
        if (!fallbackQueued)
            pendingListingValid = false;
    }

    private void RequestCurrentMarketListings()
    {
        var info = InfoProxyItemSearch.Instance();
        if (info == null || info->WaitingForListings)
            return;

        info->ClearListData();
        var requested = info->RequestData();
        listingRefreshAt = DateTime.UtcNow.AddSeconds(1);
        log.Information("Requested current market listings after confirmation: searchItemId={SearchItemId}, requested={Requested}, waiting={Waiting}, listingCount={ListingCount}",
            GetActiveSearchItemId(info), requested, info->WaitingForListings, info->ListingCount);
    }

    private void RequestWhenReady()
    {
        if (!framework.IsInFrameworkUpdateThread)
        {
            _ = framework.RunOnTick(RequestWhenReady);
            return;
        }

        var agent = AgentItemSearch.Instance();
        var infoProxy = InfoProxyItemSearch.Instance();
        var addon = gameGui.GetAddonByName<AddonItemSearch>("ItemSearch");
        if (agent == null || infoProxy == null || addon == null || !addon->IsReady)
        {
            if (++requestAttempts < 20)
            {
                _ = framework.RunOnTick(RequestWhenReady, delay: RandomizedDelayMilliseconds(250));
                return;
            }
            Status = "ItemSearch Addon 超时未就绪，未发送报价请求。";
            return;
        }

        try
        {
            agent->ResultItemId = ItemId;
            infoProxy->SearchItemId = ItemId;
            infoProxy->ClearListData();
            bool requested;
            if (!string.IsNullOrWhiteSpace(SearchText) && addon->SearchTextInput != null)
            {
                addon->SearchTextInput->SetText(SearchText);
                addon->SearchText.SetString(SearchText);
                addon->SearchText2.SetString(SearchText);
                addon->PartialMatch = false;
                addon->RunSearch(ignoreFilters: false);
                requested = true;
            }
            else
            {
                requested = infoProxy->RequestData();
            }
            log.Information("Standalone market request: itemId={ItemId}, requested={Requested}, waiting={Waiting}, listingCount={Count}",
                ItemId, requested, infoProxy->WaitingForListings, infoProxy->ListingCount);
            Status = requested ? $"已向服务器请求物品 {ItemId} 的报价。" : "客户端拒绝报价请求，可能需要有效市场会话。";
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to request market data after ItemSearch became ready");
            Status = "请求服务器报价失败。";
        }
    }

    public void Dispose()
    {
        marketBoard.PurchaseRequested -= OnPurchaseRequested;
        marketBoard.ItemPurchased -= OnItemPurchased;
        marketBoard.OfferingsReceived -= OnOfferingsReceived;
        fireCallbackHook.Dispose();
    }
}
