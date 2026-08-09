using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace AutoTreasureHunt;

/// <summary>
/// 任意地点打开原生 ItemSearch，并通过原生 InfoProxy 请求服务器报价。
/// 该类只调用客户端 Agent/InfoProxy，不伪造报价、不创建云端请求。
/// </summary>
public sealed unsafe class OpenMarketAnywhere
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private int requestAttempts;

    public OpenMarketAnywhere(IFramework framework, IGameGui gameGui, IPluginLog log)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.log = log;
    }

    public uint ItemId { get; set; }
    public string SearchText { get; set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;

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

            // 只调用原生 Show。不要再调用 ShowAddon，否则可能重复初始化
            // ItemSearch 并使购买确认关闭后的模态父子关系失效。
            agent->Show();
            Status = "已请求客户端打开 ItemSearch。";
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

            agent->ResultItemId = ItemId;
            infoProxy->SearchItemId = ItemId;
            infoProxy->ClearListData();
            agent->Show();
            requestAttempts = 0;
            Status = $"已打开 ItemSearch，等待 Addon 初始化后请求物品 {ItemId} 的服务器报价。";
            _ = framework.RunOnTick(RequestWhenReady, delay: TimeSpan.FromMilliseconds(250));
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to open ItemSearch and request market data");
            Status = "打开并请求服务器报价失败。";
        }
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

            log.Information(
                "Standalone market request: itemId={ItemId}, requested={Requested}, waiting={Waiting}, listingCount={Count}",
                ItemId, requested, infoProxy->WaitingForListings, infoProxy->ListingCount);
            Status = requested
                ? $"已向服务器请求物品 {ItemId} 的报价。"
                : "客户端拒绝报价请求，当前服务器可能要求有效的市场会话。";
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to request market data after ItemSearch became ready");
            Status = "请求服务器报价失败。";
        }
    }
}
