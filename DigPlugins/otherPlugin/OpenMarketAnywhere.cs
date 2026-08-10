using Dalamud.Game.Network.Structures;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
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
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
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
    private bool pendingListingValid;
    private nint pendingConfirmationAddon;
    private bool fallbackQueued;
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

    public OpenMarketAnywhere(
        IFramework framework,
        IGameGui gameGui,
        IPluginLog log,
        IGameInteropProvider interop,
        IMarketBoard marketBoard)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
        this.marketBoard = marketBoard;
        fireCallbackHook = interop.HookFromAddress<AtkUnitBase.Delegates.FireCallback>(
            AtkUnitBase.Addresses.FireCallback.Value,
            FireCallbackDetour);
        fireCallbackHook.Enable();
        marketBoard.PurchaseRequested += OnPurchaseRequested;
        marketBoard.ItemPurchased += OnItemPurchased;
    }

    public uint ItemId { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public bool SessionActive => sessionActive;

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
            _ = framework.RunOnTick(RequestWhenReady, delay: TimeSpan.FromMilliseconds(250));
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
        pendingConfirmationAddon = nint.Zero;
        fallbackQueued = false;
        fallbackAt = default;
        fallbackDeadline = default;
        fallbackAttempts = 0;
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

        NormalizeStandaloneResultParent(result, search, confirmationVisible, clearBlockedParent: false);

        var index = result->Results->SelectedItemIndex;
        if (index < 0 || index >= info->ListingCount)
            index = 0;
        var listing = info->Listings[index];
        if (listing.ListingId == 0 || listing.ItemId == 0 || listing.UnitPrice == 0)
            return;

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

    private void TryCaptureListingForConfirmation()
    {
        if (pendingListingValid || purchaseRequestObserved)
            return;

        var result = gameGui.GetAddonByName<AddonItemSearchResult>("ItemSearchResult");
        var info = InfoProxyItemSearch.Instance();
        if (result == null || !result->IsReady || result->Results == null ||
            info == null || info->WaitingForListings || info->ListingCount == 0)
            return;

        var index = result->Results->SelectedItemIndex;
        if (index < 0 || index >= info->ListingCount)
            index = 0;
        var listing = info->Listings[index];
        if (listing.ListingId == 0 || listing.ItemId == 0 || listing.UnitPrice == 0 ||
            IsRecentlyCompletedListing(listing.ListingId))
            return;

        pendingListing = listing;
        pendingListingValid = true;
        log.Information("Captured market listing at confirmation callback: index={Index}, listingId={ListingId}, itemId={ItemId}, quantity={Quantity}, unitPrice={UnitPrice}, retainerId={RetainerId}, townId={TownId}",
            index, listing.ListingId, listing.ItemId, listing.Quantity, listing.UnitPrice, listing.RetainerId, listing.TownId);
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
        var isItemSearchResult = name.Equals("ItemSearchResult", StringComparison.OrdinalIgnoreCase);
        var resultForCallback = isItemSearchResult && addon != null
            ? (AddonItemSearchResult*)addon
            : null;
        if (sessionActive && isItemSearchResult)
        {
            var callbackValue2 = values != null && valueCount > 1 ? values[1].Int : int.MinValue;
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
            TryCaptureListingForConfirmation();
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
            _ = framework.RunOnTick(MarkMarketResultClosed, delay: TimeSpan.FromMilliseconds(10));
        }

        var isTrackedConfirmation = pendingConfirmationAddon == nint.Zero ||
                                     addon == null ||
                                     (nint)addon == pendingConfirmationAddon;
        if (sessionActive && isConfirmation && isTrackedConfirmation)
        {
            log.Information("Standalone market confirmation callback: value={Value}, valueCount={Count}, close={Close}, listingId={ListingId}, itemId={ItemId}",
                callbackValue, valueCount, close, pendingListing.ListingId, pendingListing.ItemId);
            pendingConfirmationAddon = nint.Zero;
            if (callbackValue == 0 && pendingListingValid)
            {
                // Keep the result page entirely native until the server reports
                // the purchase. Repairing it while the confirmation is closing
                // can leave ItemSearchResult in the native drag-only state and
                // can steal focus from chat or the next modal.
                fallbackQueued = true;
                fallbackAt = DateTime.UtcNow.AddMilliseconds(750);
                fallbackDeadline = DateTime.UtcNow.AddSeconds(10);
                fallbackAttempts = 0;
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
        if (!fallbackQueued || DateTime.UtcNow < fallbackAt || !pendingListingValid || purchaseRequestObserved)
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
                _ = framework.RunOnTick(RequestWhenReady, delay: TimeSpan.FromMilliseconds(250));
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
        fireCallbackHook.Dispose();
    }
}
