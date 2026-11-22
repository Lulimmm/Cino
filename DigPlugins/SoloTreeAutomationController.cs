using System.Collections.Concurrent;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AutoTreasureHunt;

internal sealed unsafe class SoloTreeAutomationController : IDisposable
{
    private const uint SoloTreeObjectBaseId = 197371;
    private const uint SoloTreeEventId = 0x00240006;
    private const short SoloTreeScene = 14;
    private const int MaximumSwingActions = 30;
    private static readonly TimeSpan ActionDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CashOutWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SettlementTimeout = TimeSpan.FromSeconds(30);

    private enum AutomationStage
    {
        Idle, FindAndInteract, WaitForEvent, SendEntryAction, WaitForYield21,
        SendDifficultyAction, WaitForYield23, SendSwingAction, WaitForYield24,
        SendDoubleDown, WaitForYield25,
        SendCashOut, WaitForCashOut, SendComplete, WaitForComplete, WaitForSettlement,
    }

    private readonly SoloTreePacketCapture capture;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly Func<bool> hasCredential;
    // Hooks only enqueue; all transitions and sends run on the framework thread.
    private readonly ConcurrentQueue<SoloTreeCapturedPacket> observedPackets = new();
    private AutomationStage stage;
    private DateTime nextActionAt;
    private DateTime lastProgressAt;
    private int swingActionsSent;
    private int treeSwingActions;
    private int treesCompleted;
    private int doubleDowns;
    private int completedFullRewardRounds;
    private int consecutiveFullRewardRounds;
    private int failedRounds;
    private bool roundSucceeded;
    private bool recoveryAfterComplete;
    private long reward;
    private int balanceBeforeSettlement;
    private SoloTreeAim aim = new();
    private string status = "自动孤树无援未启动";
    private int enabled;

    public SoloTreeAutomationController(
        SoloTreePacketCapture capture, IObjectTable objectTable,
        IPluginLog log, Func<bool> hasCredential)
    {
        this.capture = capture;
        this.objectTable = objectTable;
        this.log = log;
        this.hasCredential = hasCredential;
        capture.PacketObserved += OnPacketObserved;
    }

    public bool Enabled => Volatile.Read(ref enabled) != 0;
    public string Status => Volatile.Read(ref status);

    public void SetEnabled(bool value)
    {
        if (!value) { Stop("自动孤树无援已停止"); return; }
        if (Enabled) return;
        if (!hasCredential()) { status = "当前凭证不可运行自动孤树无援"; return; }
        if (!capture.IsAutomationAvailable) { status = "孤树事件收发接口不完整，未启动"; return; }
        if (capture.IsSoloTreeEventActive) { status = "请先结束当前孤树事件，再开启自动流程"; return; }

        observedPackets.Clear();
        swingActionsSent = 0;
        treeSwingActions = 0;
        treesCompleted = 0;
        doubleDowns = 0;
        completedFullRewardRounds = 0;
        consecutiveFullRewardRounds = 0;
        failedRounds = 0;
        roundSucceeded = false;
        recoveryAfterComplete = false;
        reward = 0;
        aim = new SoloTreeAim();
        lastProgressAt = DateTime.UtcNow;
        nextActionAt = lastProgressAt;
        stage = AutomationStage.FindAndInteract;
        capture.SetAutomationEnabled(true);
        Interlocked.Exchange(ref enabled, 1);
        status = "等待交互孤树无援，事件内使用发包操作";
    }

    public void Update()
    {
        if (!Enabled) return;
        if (!hasCredential()) { Stop("凭证已失效，自动孤树无援已停止"); return; }
        while (observedPackets.TryDequeue(out var packet))
        {
            HandlePacket(packet);
            if (!Enabled) return;
        }
        var now = DateTime.UtcNow;
        if (stage == AutomationStage.WaitForSettlement)
        {
            var balanceInventory = InventoryManager.Instance();
            if (balanceInventory != null &&
                (long)balanceInventory->GetGoldSaucerCoin() - balanceBeforeSettlement >= reward)
            {
                var settledReward = reward;
                completedFullRewardRounds++;
                consecutiveFullRewardRounds = settledReward == 750 ? consecutiveFullRewardRounds + 1 : 0;
                roundSucceeded = false;
                reward = 0;
                ResetRoundProgress();
                stage = AutomationStage.FindAndInteract;
                nextActionAt = now + ActionDelay;
                lastProgressAt = now;
                status = settledReward == 750
                    ? $"已确认到账 {settledReward} 金碟币，连续满额 {consecutiveFullRewardRounds} 轮，继续挑战"
                    : $"已到账 {settledReward} 金碟币（非满额），连续满额计数已清零，继续挑战";
                return;
            }

            if (now - lastProgressAt >= SettlementTimeout)
            {
                failedRounds++;
                consecutiveFullRewardRounds = 0;
                PrepareNextRound($"满额结果 30 秒内未收到金碟币到账确认，已记录并自动重开（失败轮数 {failedRounds}）");
            }
            return;
        }
        if (stage == AutomationStage.WaitForCashOut)
        {
            var inventory = InventoryManager.Instance();
            if (inventory != null &&
                (long)inventory->GetGoldSaucerCoin() - balanceBeforeSettlement >= reward)
            {
                QueueAction(AutomationStage.SendComplete, "已检测到金碟币到账，准备结束事件");
                return;
            }

            if (now - lastProgressAt >= CashOutWait)
                QueueAction(AutomationStage.SendComplete, "领奖等待窗口结束，准备结束事件并核对到账");
            return;
        }
        if (stage != AutomationStage.FindAndInteract && now - lastProgressAt > ResponseTimeout)
        {
            Recover($"孤树无援在 {stage} 等待超时");
            return;
        }
        if (now < nextActionAt) return;
        if (stage == AutomationStage.FindAndInteract) { TryInteractWithSoloTreeObject(); return; }
        if (stage == AutomationStage.SendComplete)
        {
            stage = AutomationStage.WaitForComplete;
            lastProgressAt = now;
            if (!capture.TryCompleteSoloTreeEvent(out var error))
            {
                Recover($"结束孤树事件失败：{error}");
                return;
            }
            status = roundSucceeded && reward > 0
                ? $"结束成功事件，等待 {reward} 金碟币到账"
                : "本轮未达满奖励，正在结束事件并自动重开";
            return;
        }

        if (stage == AutomationStage.SendCashOut)
        {
            var inventory = InventoryManager.Instance();
            if (inventory == null) { Recover("暂时无法读取金碟币余额"); return; }
            balanceBeforeSettlement = unchecked((int)inventory->GetGoldSaucerCoin());
            stage = AutomationStage.WaitForCashOut;
            lastProgressAt = now;
            if (!capture.TrySendEventAction(SoloTreeEventId, SoloTreeScene, 8, null, out var cashOutError))
            {
                Recover($"提交结算选择失败：{cashOutError}");
                return;
            }
            status = $"已提交领取选择，等待 {reward} 金碟币到账";
            return;
        }

        var action = stage switch
        {
            AutomationStage.SendEntryAction => (Subtype: (byte)7, Parameter: (uint?)0, Next: AutomationStage.WaitForYield21),
            AutomationStage.SendDifficultyAction => (Subtype: (byte)9, Parameter: (uint?)1, Next: AutomationStage.WaitForYield23),
            AutomationStage.SendSwingAction => (Subtype: (byte)10, Parameter: (uint?)aim.Position, Next: AutomationStage.WaitForYield24),
            AutomationStage.SendDoubleDown => (Subtype: (byte)11, Parameter: (uint?)null, Next: AutomationStage.WaitForYield25),
            _ => default,
        };
        if (action.Subtype == 0) return;
        if (action.Subtype == 10 && treeSwingActions >= MaximumSwingActions)
        {
            roundSucceeded = false;
            reward = 0;
            QueueAction(AutomationStage.SendComplete, "当前树超过挥砍保护上限，结束本轮后自动重开");
            return;
        }
        // Set the expected response before sending; a response can arrive immediately.
        stage = action.Next;
        lastProgressAt = now;
        if (!capture.TrySendEventAction(SoloTreeEventId, SoloTreeScene, action.Subtype, action.Parameter, out var sendError))
        {
            Recover(sendError);
            return;
        }
        if (action.Subtype == 10) { swingActionsSent++; treeSwingActions++; }
        if (action.Subtype == 11) doubleDowns++;
        status = action.Subtype switch
        {
            7 => "已提交开始动作，等待服务器初始化",
            9 => "已提交难度选项 1，等待挥砍就绪",
            11 => $"已提交第 {doubleDowns} 次翻倍，等待下一棵树",
            _ => $"已提交第 {swingActionsSent} 次挥砍，等待服务器结果",
        };
    }

    public void Dispose()
    {
        Stop("自动孤树无援已停止");
        capture.PacketObserved -= OnPacketObserved;
    }

    private void OnPacketObserved(SoloTreeCapturedPacket packet)
    {
        if (Enabled && packet.EventId == SoloTreeEventId && packet.Scene == SoloTreeScene &&
            (packet.Direction == "ServerToClient" || packet.Kind.StartsWith("EventComplete/", StringComparison.Ordinal)))
            observedPackets.Enqueue(packet);
    }

    private void HandlePacket(SoloTreeCapturedPacket packet)
    {
        switch (packet.Kind)
        {
            case "EventPlay" when stage == AutomationStage.WaitForEvent:
                QueueAction(AutomationStage.SendEntryAction, "已进入孤树事件，准备提交开始动作");
                break;
            case "EventYield/21" when stage == AutomationStage.WaitForYield21:
                QueueAction(AutomationStage.SendDifficultyAction, "服务器已初始化，准备选择难度");
                break;
            case "EventYield/23" when stage == AutomationStage.WaitForYield23:
                if (packet.Parameters is not { Length: >= 9 } ready || ready[0] != 1 || ready[2] <= 0)
                {
                    Recover("孤树挥砍就绪数据无效");
                    return;
                }
                QueueAction(AutomationStage.SendSwingAction, "服务器允许挥砍，准备提交动作");
                break;
            case "EventYield/24" when stage == AutomationStage.WaitForYield24:
                HandleSwingResult(packet);
                break;
            case "EventYield/25" when stage == AutomationStage.WaitForYield25:
                if (packet.Parameters is not { Length: >= 7 } next || next[0] != 1 || next[1] <= 0)
                {
                    Recover("翻倍后的树状态无效");
                    return;
                }
                aim = new SoloTreeAim();
                treeSwingActions = 0;
                roundSucceeded = false;
                QueueAction(AutomationStage.SendSwingAction, $"翻倍已开始，下一棵树剩余 {next[1]}，准备挥砍");
                break;
            default:
                if (packet.Kind.StartsWith("EventComplete/", StringComparison.Ordinal))
                {
                    if (stage != AutomationStage.WaitForComplete)
                        break;
                    if (recoveryAfterComplete)
                    {
                        recoveryAfterComplete = false;
                        PrepareNextRound($"已恢复事件状态，准备重新挑战（失败轮数 {failedRounds}）");
                    }
                    else if (roundSucceeded && reward > 0)
                    {
                        QueueAction(AutomationStage.WaitForSettlement, $"结算包已发送，等待 {reward} 金碟币实际到账");
                    }
                    else
                    {
                        failedRounds++;
                        consecutiveFullRewardRounds = 0;
                        PrepareNextRound($"本轮未获得满额奖励，自动继续（失败轮数 {failedRounds}）");
                    }
                }
                break;
        }
    }

    private void HandleSwingResult(SoloTreeCapturedPacket packet)
    {
        if (packet.Parameters is not { Length: >= 9 } result || result[0] != 1)
        {
            Recover("服务器未接受当前孤树动作");
            return;
        }
        // [1] is hit quality, [2] remaining health, [5] completion.
        // Weak hits can finish a tree; critical hits need not finish it.
        if (result[5] == 1)
        {
            roundSucceeded = result[2] == 0;
            reward = roundSucceeded ? result[7] : 0;
            if (roundSucceeded)
                treesCompleted++;
            // The last field carries the next stake; [6] changes across bonus
            // rounds and is not a reliable permission flag for double-down.
            if (roundSucceeded && result[8] > 0)
            {
                QueueAction(AutomationStage.SendDoubleDown, $"第 {treesCompleted} 棵树已砍倒，准备翻倍继续");
                return;
            }
            QueueAction(roundSucceeded ? AutomationStage.SendCashOut : AutomationStage.SendComplete, roundSucceeded
                ? "服务器确认树已砍倒，准备结算本轮"
                : "服务器报告本轮结束，准备退出事件");
            return;
        }
        if (result[2] <= 0)
        {
            Recover("服务器状态已结束但未确认成功");
            return;
        }
        aim.Observe((int)result[1]);
        QueueAction(AutomationStage.SendSwingAction, $"挥砍评价 {result[1]}，剩余 {result[2]}，调整下一次位置为 {aim.Position}");
    }

    private void QueueAction(AutomationStage nextStage, string message)
    {
        stage = nextStage;
        lastProgressAt = DateTime.UtcNow;
        nextActionAt = lastProgressAt + ActionDelay;
        status = message;
    }

    private void Stop(string message)
    {
        if (capture.OwnsSoloTreeEvent && !capture.TryCompleteSoloTreeEvent(out var error))
            log.Warning("Failed to close automated SoloTree event: {Error}", error);
        Interlocked.Exchange(ref enabled, 0);
        capture.SetAutomationEnabled(false);
        observedPackets.Clear();
        stage = AutomationStage.Idle;
        status = message;
    }

    private void Recover(string message)
    {
        observedPackets.Clear();
        if (capture.OwnsSoloTreeEvent && capture.IsSoloTreeEventActive)
        {
            recoveryAfterComplete = true;
            roundSucceeded = false;
            reward = 0;
            stage = AutomationStage.WaitForComplete;
            lastProgressAt = DateTime.UtcNow;
            nextActionAt = lastProgressAt;
            status = $"{message}，正在关闭本轮并自动恢复";
            if (!capture.TryCompleteSoloTreeEvent(out var closeError))
                status = $"{message}；暂时无法结束事件，将重试恢复：{closeError}";
            return;
        }

        recoveryAfterComplete = false;
        PrepareNextRound($"{message}，稍后自动重试");
    }

    private void PrepareNextRound(string message)
    {
        roundSucceeded = false;
        reward = 0;
        ResetRoundProgress();
        stage = AutomationStage.FindAndInteract;
        lastProgressAt = DateTime.UtcNow;
        nextActionAt = lastProgressAt + TimeSpan.FromSeconds(2);
        status = message;
    }

    private void ResetRoundProgress()
    {
        treeSwingActions = 0;
        treesCompleted = 0;
        doubleDowns = 0;
        aim = new SoloTreeAim();
    }

    private void TryInteractWithSoloTreeObject()
    {
        var target = objectTable.FirstOrDefault(gameObject =>
            gameObject.BaseId == SoloTreeObjectBaseId && gameObject.EntityId != 0 && gameObject.Address != nint.Zero);
        if (target is null) { status = "请靠近孤树无援设施后开启自动流程"; return; }
        var targetSystem = TargetSystem.Instance();
        if (targetSystem == null) { Recover("当前无法取得游戏交互系统"); return; }
        stage = AutomationStage.WaitForEvent;
        lastProgressAt = DateTime.UtcNow;
        targetSystem->InteractWithObject((GameObject*)target.Address, true);
        status = "已交互孤树无援，等待服务器事件";
    }
}
