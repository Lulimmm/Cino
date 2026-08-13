using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.STD;
using System.Numerics;

namespace AutoTreasureHunt;

/// <summary>
/// Read-only diagnostic probe for scene VFX. FFXIV does not expose these effects
/// through IObjectTable; offsets/signatures are client-version specific.
/// </summary>
internal sealed class VfxEffectCenterProbe
{
    private readonly ISigScanner sigScanner;
    private readonly IPluginLog log;

    public VfxEffectCenterProbe(ISigScanner sigScanner, IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.log = log;
    }

    public unsafe string Capture(Vector3 referencePosition)
    {
        try
        {
            var managerSignature = "48 8B D1 48 8B 0D ?? ?? ?? ?? 48 85 C9 74 0A";
            var managerAddress = sigScanner.ScanText(managerSignature);
            if (managerAddress == IntPtr.Zero)
                return "当前客户端未匹配 LayoutWorld/VFX 管理器签名。";

            var world = LayoutWorld.Instance();
            if (world == null || world->ActiveLayout == null)
                return "VFX 管理器已定位，但当前没有活动场景布局。";

            var layout = world->ActiveLayout;
            var type = InstanceType.Vfx;
            if (!layout->InstancesByType.TryGetValuePointer(type, out var instances) || instances == null)
                return "当前场景没有 VFX 实例表。";

            var map = (*instances).Value;
            var count = 0;
            var nearest = default(Vector3);
            var nearestDistance = float.MaxValue;
            foreach (ref var pair in *map)
            {
                var instance = pair.Item2.Value;
                if (instance == null || !instance->IsActive)
                    continue;

                var position = default(Vector3);
                instance->GetTranslation(&position);
                if (!IsFinite(position))
                    continue;

                count++;
                // Choose the active scene VFX nearest to the caller. The
                // returned position is the VFX transform, never the player.
                var distance = Vector3.DistanceSquared(position, referencePosition);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = position;
                }
            }

            return count == 0
                ? "VFX 管理器已定位，但当前没有活动 VFX 实例。"
                : $"已读取活动 VFX 实例：数量 {count}；最近候选中点 XYZ ({nearest.X:F3}, {nearest.Y:F3}, {nearest.Z:F3})。";
        }
        catch (Exception ex)
        {
            log.Debug(ex, "VFX 场景特效读取失败。");
            return "VFX 管理器或实例布局读取失败，已停止本次读取以避免崩溃。";
        }
    }

    public unsafe bool TryGetNearestPosition(Vector3 referencePosition, out Vector3 position, out int count)
    {
        position = default;
        count = 0;
        try
        {
            var world = LayoutWorld.Instance();
            if (world == null || world->ActiveLayout == null)
                return false;
            var type = InstanceType.Vfx;
            if (!world->ActiveLayout->InstancesByType.TryGetValuePointer(type, out var instances) || instances == null)
                return false;
            var map = (*instances).Value;
            var nearestDistance = float.MaxValue;
            foreach (ref var pair in *map)
            {
                var instance = pair.Item2.Value;
                if (instance == null || !instance->IsActive)
                    continue;
                var candidate = default(Vector3);
                instance->GetTranslation(&candidate);
                if (!IsFinite(candidate))
                    continue;
                count++;
                var distance = Vector3.DistanceSquared(candidate, referencePosition);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    position = candidate;
                }
            }
            return count > 0 && nearestDistance < float.MaxValue;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "VFX 中心读取失败。");
            position = default;
            count = 0;
            return false;
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
