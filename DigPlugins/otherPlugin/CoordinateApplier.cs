using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace AutoTreasureHunt;

/// <summary>
/// 独立的 XYZ 坐标编辑器。
/// 通过 GameObject.SetPosition + PositionModified 交给客户端正常同步，
/// 不构造固定长度的 Zone 包，也不直接写未知网络包内存。
/// </summary>
public sealed unsafe class CoordinateApplier
{
    private readonly IObjectTable objectTable;
    private readonly Action save;
    private string status = string.Empty;

    public CoordinateApplier(IObjectTable objectTable, Action? save = null)
    {
        this.objectTable = objectTable;
        this.save = save ?? (() => { });
    }

    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public string Status => status;

    /// <summary>读取当前本地玩家坐标并保存到 X/Y/Z。</summary>
    public bool ReadCurrent()
    {
        var player = objectTable.LocalPlayer;
        if (player is null)
        {
            status = "当前没有可用的本地玩家对象。";
            return false;
        }

        X = player.Position.X;
        Y = player.Position.Y;
        Z = player.Position.Z;
        save();
        status = $"已读取：X={X:F3}, Y={Y:F3}, Z={Z:F3}";
        return true;
    }

    /// <summary>设置并立即应用一组 XYZ 坐标。</summary>
    public void Apply(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
        save();
        Apply();
    }

    /// <summary>应用输入的坐标。必须在 Framework/UI 线程调用。</summary>
    public void Apply()
    {
        var player = objectTable.LocalPlayer;
        if (player is null || player.Address == nint.Zero)
        {
            status = "当前没有可用的本地玩家对象。";
            return;
        }

        if (!float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Z))
        {
            status = "X/Y/Z 必须是有限数值。";
            return;
        }

        try
        {
            var gameObject = (GameObject*)player.Address;
            gameObject->SetPosition(X, Y, Z);
            gameObject->PositionModified();
            status = $"已应用：X={X:F3}, Y={Y:F3}, Z={Z:F3}";
        }
        catch (Exception ex)
        {
            status = $"坐标应用失败：{ex.Message}";
        }
    }
}
