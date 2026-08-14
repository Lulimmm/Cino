namespace AutoTreasureHunt;

/// <summary>藏宝图进入副本后的玩法类型。</summary>
internal enum TreasureMapRoute
{
    Roulette,
    DoorSelection,
}

/// <summary>一张可由车头处理的藏宝图及其对应任务道具。</summary>
internal readonly record struct TreasureMapOption(
    string Name,
    string TaskName,
    string MarketSearchName,
    uint MapItemId,
    uint TaskItemId,
    TreasureMapRoute Route);

/// <summary>
/// 车头藏宝图与转盘实体的集中配置。
/// 后续新增地图或潜网巡梦，只修改本文件中对应的 List 即可。
/// </summary>
internal static class TreasureMapDefinitions
{
    // Name 为插件显示名，MarketSearchName 为市场布告板精确搜索名。
    // MapItemId 为普通藏宝图道具 ID，TaskItemId 为解读后任务道具 ID。
    // 【转盘地图】进入副本后使用当前的转盘逻辑；新转盘藏宝图加在此 List。
    internal static readonly List<TreasureMapOption> RouletteTreasureMapOptions =
    [
        new("陈旧的卡冈图亚革地图", "卡冈图亚草制的宝物地图", MarketSearchName: "陈旧的卡冈图亚革地图", MapItemId: 46185, TaskItemId: 2003785, Route: TreasureMapRoute.Roulette),
        new("陈旧的蛇牛革地图", "蛇牛革制的宝物地图", MarketSearchName: "陈旧的蛇牛革地图", MapItemId: 39591, TaskItemId: 2003457, Route: TreasureMapRoute.Roulette),
    ];

    // 【选门地图】进入副本后进入“选门”空逻辑；新选门藏宝图加在此 List。
    internal static readonly List<TreasureMapOption> DoorSelectionTreasureMapOptions =
    [
        new("陈旧的狞豹革地图（已脑测）", "狩豹革制的宝物地图", MarketSearchName: "陈旧的狞豹革地图", MapItemId: 43557, TaskItemId: 2003563, Route: TreasureMapRoute.DoorSelection),
    ];

    // 【合并列表】界面选择、背包统计、补图购买统一使用；不直接在这里添加地图。
    internal static readonly List<TreasureMapOption> HeadTreasureMapOptions =
    [
        .. RouletteTreasureMapOptions,
        .. DoorSelectionTreasureMapOptions,
    ];

    // 【转盘实体 ID】转盘内“潜网巡梦”的 BaseID；新增同类实体时只需在此添加 ID。
    internal static readonly List<uint> RouletteDreamBaseIds =
    [
        2014790,
        2009598,
    ];

    // 【转盘副本地图 ID】进入这些地图后启用转盘逻辑。
    internal static readonly List<uint> RouletteInstanceMapIds =
    [
        1059,
        818,
    ];

    // 【选门副本地图 ID】进入这些地图后启用选门独占逻辑；后续适配新副本时追加地图 ID。
    internal static readonly List<uint> DoorSelectionInstanceMapIds =
    [
        896,
    ];

    // 【选门初始宝箱 BaseID】任务开始屏障消失后，前往并交互这些宝箱。
    internal static readonly List<uint> DoorSelectionInitialChestBaseIds =
    [
        2013860,
    ];

    // 896 副本各层进入战斗前需要交互的宝箱 BaseID。
    // 第 1 层默认使用上面的 2013860；其余楼层请按实测填写。
    internal static readonly List<uint> DoorSelectionCombatStartChestFloor1BaseIds = [2013860,];
    internal static readonly List<uint> DoorSelectionCombatStartChestFloor2BaseIds = [];
    internal static readonly List<uint> DoorSelectionCombatStartChestFloor3BaseIds = [];
    internal static readonly List<uint> DoorSelectionCombatStartChestFloor4BaseIds = [];
    internal static readonly List<uint> DoorSelectionCombatStartChestFloor5BaseIds = [];

    // 896 副本各层战斗结束后出现的宝箱 BaseID。请按实际测试结果填入；
    // 若该层宝箱没有 BaseID，可留空并由运行时按名称“宝箱”兜底识别。
    internal static readonly List<uint> DoorSelectionCombatChestFloor1BaseIds = [2013860];
    internal static readonly List<uint> DoorSelectionCombatChestFloor2BaseIds = [2013861];
    internal static readonly List<uint> DoorSelectionCombatChestFloor3BaseIds = [2013862];
    internal static readonly List<uint> DoorSelectionCombatChestFloor4BaseIds = [2013863];
    internal static readonly List<uint> DoorSelectionCombatChestFloor5BaseIds = [2013864];

    // 【选门 ID】以下列表暂为空，后续手动填入对应门的 BaseID。
    // 每组分别表示当前层到下一层的左门和右门。
    internal static readonly List<uint> DoorSelectionLeftDoorFloor1To2BaseIds = [1073757625,];
    internal static readonly List<uint> DoorSelectionRightDoorFloor1To2BaseIds = [1073757626,];

    internal static readonly List<uint> DoorSelectionLeftDoorFloor2To3BaseIds = [1073757623,];
    internal static readonly List<uint> DoorSelectionRightDoorFloor2To3BaseIds = [1073757624,];

    internal static readonly List<uint> DoorSelectionLeftDoorFloor3To4BaseIds = [1073757621,];
    internal static readonly List<uint> DoorSelectionRightDoorFloor3To4BaseIds = [1073757622,];

    internal static readonly List<uint> DoorSelectionLeftDoorFloor4To5BaseIds = [1073757619,];
    internal static readonly List<uint> DoorSelectionRightDoorFloor4To5BaseIds = [1073757620,];
}
