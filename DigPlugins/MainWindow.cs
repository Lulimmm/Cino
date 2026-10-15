using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Lumina.Excel.Sheets;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;

namespace AutoTreasureHunt;

public sealed class MainWindow
{
    private const string RememberedCredentialMask = "********";
    private static readonly Vector4 RunningColor = new(0.08f, 0.57f, 0.38f, 1.00f);
    private static readonly Vector4 StoppedColor = new(0.78f, 0.20f, 0.31f, 1.00f);
    // Aemeath's visual language: deep starfield, rose hair, cyan energy, and star gold.
    private static readonly Vector4 AemeathPink = new(0.95f, 0.39f, 0.65f, 1.00f);
    private static readonly Vector4 AemeathCyan = new(0.29f, 0.89f, 0.88f, 1.00f);
    private static readonly Vector4 AemeathGold = new(0.94f, 0.79f, 0.45f, 1.00f);
    private static readonly Vector4 AemeathInk = new(0.94f, 0.96f, 1.00f, 1.00f);
    private static readonly Vector4 AemeathMuted = new(0.63f, 0.70f, 0.86f, 1.00f);
    // The shell stays translucent, while the content surfaces remain opaque enough to hide game text.
    private static readonly Vector4 AemeathNavy = new(0.025f, 0.045f, 0.12f, 0.78f);
    private static readonly Vector4 AemeathPanel = new(0.00f, 0.00f, 0.00f, 0.00f);

    private enum MainPage
    {
        Automation,
        Credential,
        Diagnostics,
        DeepDungeon,
    }

    private readonly Plugin plugin;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IDataManager dataManager;
    private readonly ITextureProvider textureProvider;
    private readonly Dictionary<uint, ISharedImmediateTexture?> mapIconCache = new();
    private readonly object fleetTextureLock = new();
    private readonly CancellationTokenSource fleetTextureLoadCancellation = new();
    private IDalamudTextureWrap[][]? fleetCharacterActions;
    private IDalamudTextureWrap[]? fleetSealFrames;
    private Task? fleetTextureLoadTask;
    private bool fleetTextureLoadFailed;
    private bool fleetTextureDisposed;
    private int fleetCharacterFrame;
    private int fleetCharacterAction;
    private int fleetCharacterActionDirection = 1;
    private double fleetCharacterNextSpecialAt;
    private int fleetSealFrame;
    private double fleetCharacterNextFrameAt;
    private double fleetSealNextFrameAt;
    private static readonly int[] AemeathSpriteFrameCounts = { 6, 8, 8, 4, 5, 8, 6, 6, 6 };
    private static readonly int[] AemeathSpecialActionStartRows = { 1, 3, 4, 5, 6, 7, 8 };
    private readonly object aemeathSpriteLock = new();
    private readonly CancellationTokenSource aemeathSpriteLoadCancellation = new();
    private IDalamudTextureWrap[][]? aemeathSpriteRows;
    private Task? aemeathSpriteLoadTask;
    private bool aemeathSpriteLoadFailed;
    private bool aemeathSpriteDisposed;
    private int aemeathSpriteRow;
    private int aemeathSpriteFrame;
    private int aemeathDefaultFrameDirection = 1;
    private double aemeathSpriteNextFrameAt;
    private double aemeathNextSpecialAt;
    private static readonly float[] ScreenGlyphStreamPositions =
        { 0.045f, 0.105f, 0.235f, 0.365f, 0.535f, 0.585f, 0.735f, 0.895f, 0.955f };
    private static readonly float[] ScreenGlyphStreamSpeeds =
        { 0.63f, 0.92f, 0.76f, 1.08f, 0.71f, 1.17f, 0.86f, 0.68f, 1.02f };
    private static readonly string[] ScreenGlyphBitmaps =
    {
        "111101101101111", "010110010010111", "111001111100111", "111001111001111",
        "101101111001001", "111100111001111", "111100111101111", "111001001001001",
        "111101111101111", "111101111001111",
        "010101111101101", "110101110101110", "111100100100111", "110101101101110",
        "111100110100111", "111100110100100", "111100101101111", "101101111101101",
        "111010010010111", "001001001101111", "101101110101101", "100100100100111",
        "101111111101101", "101111111111101", "111101101101111", "111101111100100",
        "111101101111011", "111101111110101", "111100111001111", "111010010010010",
        "101101101101111", "101101101101010", "101101111111101", "101101010101101",
        "101101010010010", "111001010100111"
    };
    private bool isOpen = true;
    private string credentialInput = string.Empty;
    private string credentialStatus = string.Empty;
    private bool credentialValidationInProgress;
    private string navigationBaseIdInput = "2013860";
    private string mapSupplementMaxUnitPriceInput = string.Empty;
    private int mapSupplementMaxUnitPriceObserved;
    private MainPage activePage = MainPage.Automation;
    private bool treasureMapSelectorOpen;
    private readonly IFontAtlas fleetFontAtlas;
    private readonly IFontHandle fleetFontHandle;
    private static readonly ushort[] FleetChineseGlyphRanges =
    {
        0x3000, 0x303F,
        0x3400, 0x4DBF,
        0x4E00, 0x9FFF,
        0xF900, 0xFAFF,
        0xFF00, 0xFFEF,
        0
    };

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

        var fontPath = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            "Resources",
            "FleetSnowfluff",
            "UranusCN.ttf");
        fleetFontAtlas = pluginInterface.UiBuilder.CreateFontAtlas(
            FontAtlasAutoRebuildMode.Async,
            isGlobalScaled: true,
            debugName: "DigPlugins Chinese Font");
        fleetFontHandle = fleetFontAtlas.NewDelegateFontHandle(toolkit => toolkit.OnPreBuild(preBuild =>
        {
            var defaultFont = preBuild.AddDalamudDefaultFont(17f);
            var config = new SafeFontConfig
            {
                SizePx = 17f,
                GlyphRanges = FleetChineseGlyphRanges,
                MergeFont = defaultFont,
                PixelSnapH = true,
                OversampleH = 2,
                OversampleV = 1,
            };
            preBuild.AddFontFromFile(fontPath, in config);
            preBuild.Font = defaultFont;
        }));
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

        IDisposable? fontScope = null;
        if (fleetFontHandle.Available)
            fontScope = fleetFontHandle.Push();

        ImGui.SetNextWindowSize(new Vector2(940, 680), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(760, 520), new Vector2(1500, 1100));
        PushAemeathStyle();
        if (!ImGui.Begin("海豹助手###AutoTreasureHuntMainWindow", ref isOpen,
                ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.End();
            PopAemeathStyle();
            fontScope?.Dispose();
            return;
        }

        DrawAemeathWindowBackdrop();
        DrawAemeathWindowChrome();
        ImGui.SetCursorPos(new Vector2(16, 38));
        DrawAemeathHero();
        ImGui.Spacing();

        var bodyHeight = Math.Max(180f, ImGui.GetContentRegionAvail().Y - 10f);
        var bodyTopLeft = ImGui.GetCursorScreenPos();
        var bodyRight = bodyTopLeft.X + ImGui.GetContentRegionAvail().X;
        var bodyBottom = bodyTopLeft.Y + bodyHeight;
        var bodyDrawList = ImGui.GetWindowDrawList();
        const float sidebarWidth = 218f;
        bodyDrawList.AddRectFilled(
            bodyTopLeft,
            new Vector2(bodyTopLeft.X + sidebarWidth, bodyBottom),
            ImGui.GetColorU32(new Vector4(0.075f, 0.035f, 0.16f, 0.14f)),
            10f);
        bodyDrawList.AddRect(
            bodyTopLeft,
            new Vector2(bodyTopLeft.X + sidebarWidth, bodyBottom),
            ImGui.GetColorU32(new Vector4(0.66f, 0.48f, 0.91f, 0.25f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);
        var pageTopLeft = bodyTopLeft + new Vector2(sidebarWidth + 14f, 0f);
        bodyDrawList.AddRectFilled(
            pageTopLeft,
            new Vector2(bodyRight, bodyBottom),
            ImGui.GetColorU32(new Vector4(0.075f, 0.035f, 0.16f, 0.12f)),
            10f);
        bodyDrawList.AddRect(
            pageTopLeft,
            new Vector2(bodyRight, bodyBottom),
            ImGui.GetColorU32(new Vector4(0.74f, 0.49f, 0.88f, 0.22f)),
            10f,
            ImDrawFlags.RoundCornersAll,
            1f);

        DrawFleetCharacter(bodyDrawList, bodyTopLeft, bodyBottom, sidebarWidth);

        if (ImGui.BeginChild("AemeathSidebar", new Vector2(sidebarWidth, bodyHeight), false,
                ImGuiWindowFlags.NoScrollbar))
        {
            DrawSidebar();
            DrawAemeathScrollRail("SidebarScroll");
        }
        ImGui.EndChild();
        ImGui.SameLine(0, 14);
        if (ImGui.BeginChild("AemeathPage", new Vector2(0, bodyHeight), false,
                ImGuiWindowFlags.NoScrollbar))
        {
            DrawActivePage();
            DrawAemeathScrollRail("PageScroll");
            DrawSealOnPage();
        }
        ImGui.EndChild();

        ImGui.End();
        PopAemeathStyle();
        fontScope?.Dispose();
    }

    public void Dispose()
    {
        IDalamudTextureWrap[][]? rows;
        lock (aemeathSpriteLock)
        {
            if (aemeathSpriteDisposed)
                return;

            aemeathSpriteDisposed = true;
            rows = aemeathSpriteRows;
            aemeathSpriteRows = null;
        }

        aemeathSpriteLoadCancellation.Cancel();
        if (rows is not null)
        {
            foreach (var row in rows)
            foreach (var texture in row)
                texture.Dispose();
        }

        aemeathSpriteLoadCancellation.Dispose();

        IDalamudTextureWrap[][]? characterActions;
        IDalamudTextureWrap[]? sealFrames;
        lock (fleetTextureLock)
        {
            fleetTextureDisposed = true;
            characterActions = fleetCharacterActions;
            sealFrames = fleetSealFrames;
            fleetCharacterActions = null;
            fleetSealFrames = null;
        }

        fleetTextureLoadCancellation.Cancel();
        foreach (var action in characterActions ?? Array.Empty<IDalamudTextureWrap[]>())
        foreach (var texture in action)
            texture.Dispose();
        foreach (var texture in sealFrames ?? Array.Empty<IDalamudTextureWrap>())
            texture.Dispose();
        fleetTextureLoadCancellation.Dispose();

        fleetFontHandle.Dispose();
        fleetFontAtlas.Dispose();
    }

    private void EnsureFleetTextureLoad()
    {
        if (fleetCharacterActions is not null || fleetTextureLoadFailed || fleetTextureDisposed)
            return;

        fleetTextureLoadTask ??= LoadFleetTexturesAsync(fleetTextureLoadCancellation.Token);
        if (!fleetTextureLoadTask.IsCompleted)
            return;

        if (!fleetTextureLoadTask.IsCompletedSuccessfully)
        {
            _ = fleetTextureLoadTask.Exception;
            fleetTextureLoadFailed = true;
        }
    }

    private void DrawFleetCharacter(ImDrawListPtr drawList, Vector2 bodyTopLeft, float bodyBottom, float sidebarWidth)
    {
        EnsureFleetTextureLoad();
        var actions = fleetCharacterActions;
        if (actions is null || actions.Length == 0 || actions[0].Length == 0)
            return;

        var now = ImGui.GetTime();
        if (fleetCharacterNextSpecialAt <= 0d)
            fleetCharacterNextSpecialAt = now + Random.Shared.Next(10, 22);

        if (fleetCharacterAction == 0 && now >= fleetCharacterNextSpecialAt && actions.Length > 1)
        {
            fleetCharacterAction = Random.Shared.Next(1, actions.Length);
            fleetCharacterFrame = 0;
            fleetCharacterActionDirection = 1;
            fleetCharacterNextFrameAt = now + 0.12d;
        }

        var frames = actions[fleetCharacterAction];
        if (frames.Length == 0)
        {
            fleetCharacterAction = 0;
            frames = actions[0];
        }

        var frameDelay = fleetCharacterAction == 0 ? 0.12d : 0.20d;
        if (fleetCharacterNextFrameAt <= 0d)
            fleetCharacterNextFrameAt = now + frameDelay;
        while (now >= fleetCharacterNextFrameAt)
        {
            if (fleetCharacterAction == 0)
            {
                fleetCharacterFrame = (fleetCharacterFrame + 1) % frames.Length;
            }
            // A special GIF is one complete left/right-style gesture: play forward,
            // then traverse the same frames backwards before returning to idle.
            else if (fleetCharacterActionDirection > 0)
            {
                if (fleetCharacterFrame < frames.Length - 1)
                {
                    fleetCharacterFrame++;
                }
                else
                {
                    fleetCharacterActionDirection = -1;
                    if (frames.Length > 1)
                        fleetCharacterFrame--;
                }
            }
            else if (fleetCharacterFrame > 0)
            {
                fleetCharacterFrame--;
            }
            else
            {
                fleetCharacterAction = 0;
                fleetCharacterActionDirection = 1;
                fleetCharacterNextSpecialAt = now + Random.Shared.Next(10, 22);
                frames = actions[0];
            }

            fleetCharacterNextFrameAt += frameDelay;
        }

        const float spriteSize = 176f;
        var topLeft = new Vector2(
            bodyTopLeft.X + (sidebarWidth - spriteSize) * 0.5f,
            bodyBottom - spriteSize - 14f);
        drawList.AddImage(frames[Math.Min(fleetCharacterFrame, frames.Length - 1)].Handle,
            topLeft, topLeft + new Vector2(spriteSize, spriteSize));
    }

    private async Task LoadFleetTexturesAsync(CancellationToken cancellationToken)
    {
        var resourceDirectory = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            "Resources",
            "FleetSnowfluff");
        var sealPath = Path.Combine(resourceDirectory, "pixel-seal-cute.gif");
        var characterActions = new List<IDalamudTextureWrap[]>();
        var sealTextures = new List<IDalamudTextureWrap>();
        try
        {
            var characterFiles = new[]
            {
                "pixel-seal-idle.gif",
                "pixel-seal-hu.gif",
                "pixel-seal-happy.gif",
                "pixel-seal-happy-2.gif",
                "pixel-seal-jump.gif",
                "pixel-seal-jump-2.gif",
                "pixel-seal-fly.gif",
                "pixel-seal-cool.gif",
            };
            var sealFrames = await DecodeGifFramesAsync(sealPath, cancellationToken);
            for (var actionIndex = 0; actionIndex < characterFiles.Length; actionIndex++)
            {
                var path = Path.Combine(resourceDirectory, characterFiles[actionIndex]);
                var frames = await DecodeGifFramesAsync(path, cancellationToken);
                var textures = new List<IDalamudTextureWrap>(frames.Count);
                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    textures.Add(await textureProvider.CreateFromImageAsync(
                        frames[frameIndex], $"fleet-character-{actionIndex:D2}-{frameIndex:D2}", cancellationToken));
                }
                characterActions.Add(textures.ToArray());
            }
            for (var index = 0; index < sealFrames.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sealTextures.Add(await textureProvider.CreateFromImageAsync(
                    sealFrames[index], $"fleet-seal-{index:D2}", cancellationToken));
            }

            lock (fleetTextureLock)
            {
                if (fleetTextureDisposed)
                    throw new OperationCanceledException(cancellationToken);

                fleetCharacterActions = characterActions.ToArray();
                fleetSealFrames = sealTextures.ToArray();
            }
        }
        catch
        {
            foreach (var action in characterActions)
            foreach (var texture in action)
                texture.Dispose();
            foreach (var texture in sealTextures)
                texture.Dispose();
            throw;
        }
    }

    private static Task<List<byte[]>> DecodeGifFramesAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var gif = Image.FromFile(path);
            var dimension = new FrameDimension(gif.FrameDimensionsList[0]);
            var frames = new List<byte[]>(gif.GetFrameCount(dimension));
            for (var index = 0; index < gif.GetFrameCount(dimension); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                gif.SelectActiveFrame(dimension, index);
                using var frame = new Bitmap(gif.Width, gif.Height, PixelFormat.Format32bppArgb);
                using (var graphics = Graphics.FromImage(frame))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.DrawImageUnscaled(gif, 0, 0);
                }
                using var stream = new MemoryStream();
                frame.Save(stream, ImageFormat.Png);
                frames.Add(stream.ToArray());
            }
            return frames;
        }, cancellationToken);
    }

    private void DrawAemeathSprite(ImDrawListPtr drawList, Vector2 bodyTopLeft, float bodyBottom, float sidebarWidth)
    {
        if (aemeathSpriteRows is null)
        {
            if (aemeathSpriteLoadFailed)
                return;

            aemeathSpriteLoadTask ??= LoadAemeathSpritesAsync(aemeathSpriteLoadCancellation.Token);
            if (!aemeathSpriteLoadTask.IsCompleted)
                return;

            if (!aemeathSpriteLoadTask.IsCompletedSuccessfully)
            {
                _ = aemeathSpriteLoadTask.Exception;
                aemeathSpriteLoadFailed = true;
                return;
            }
        }

        var rows = aemeathSpriteRows;
        if (rows is null || rows.Length == 0)
            return;

        var now = ImGui.GetTime();
        if (aemeathNextSpecialAt <= 0d)
            aemeathNextSpecialAt = now + Random.Shared.Next(18, 36);
        if (aemeathSpriteRow == 0 && now >= aemeathNextSpecialAt)
        {
            aemeathSpriteRow = AemeathSpecialActionStartRows[
                Random.Shared.Next(AemeathSpecialActionStartRows.Length)];
            aemeathSpriteFrame = 0;
            aemeathSpriteNextFrameAt = now + 0.45d;
        }

        if (aemeathSpriteNextFrameAt <= 0d)
            aemeathSpriteNextFrameAt = now + (aemeathSpriteRow == 0 ? 0.14d : 0.45d);

        var frameDelay = aemeathSpriteRow == 0 ? 0.14d : 0.45d;
        var frameGuard = 0;
        while (now >= aemeathSpriteNextFrameAt && frameGuard++ < 32)
        {
            if (aemeathSpriteRow == 0)
            {
                aemeathSpriteFrame += aemeathDefaultFrameDirection;
                if (aemeathSpriteFrame >= rows[0].Length - 1)
                {
                    aemeathSpriteFrame = rows[0].Length - 1;
                    aemeathDefaultFrameDirection = -1;
                }
                else if (aemeathSpriteFrame <= 0)
                {
                    aemeathSpriteFrame = 0;
                    aemeathDefaultFrameDirection = 1;
                }
            }
            else
            {
                aemeathSpriteFrame++;
                if (aemeathSpriteFrame >= rows[aemeathSpriteRow].Length)
                {
                    if (aemeathSpriteRow == 1)
                    {
                        aemeathSpriteRow = 2;
                        aemeathSpriteFrame = 0;
                    }
                    else
                    {
                        aemeathSpriteRow = 0;
                        aemeathSpriteFrame = 0;
                        aemeathDefaultFrameDirection = 1;
                        aemeathNextSpecialAt = now + Random.Shared.Next(18, 36);
                        frameDelay = 0.14d;
                    }
                }
            }

            aemeathSpriteNextFrameAt += frameDelay;
        }

        var spriteSize = new Vector2(176f, 205.3f);
        var topLeft = new Vector2(
            bodyTopLeft.X + (sidebarWidth - spriteSize.X) * 0.5f,
            bodyBottom - spriteSize.Y - 14f);
        var texture = rows[aemeathSpriteRow][aemeathSpriteFrame];
        drawList.AddImage(texture.Handle, topLeft, topLeft + spriteSize);
    }

    private async Task<IDalamudTextureWrap[][]> LoadAemeathSpritesAsync(CancellationToken cancellationToken)
    {
        var resourceDirectory = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            "Resources",
            "AemeathSprites");
        var rows = new IDalamudTextureWrap[AemeathSpriteFrameCounts.Length][];
        try
        {
            for (var row = 0; row < AemeathSpriteFrameCounts.Length; row++)
            {
                rows[row] = new IDalamudTextureWrap[AemeathSpriteFrameCounts[row]];
                for (var frame = 0; frame < rows[row].Length; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(resourceDirectory, $"row{row + 1:D2}_frame{frame + 1:D2}.png");
                    var pngBytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    rows[row][frame] = await textureProvider.CreateFromImageAsync(
                        pngBytes,
                        $"aemeath-sprite-{row + 1:D2}-{frame + 1:D2}",
                        cancellationToken);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (aemeathSpriteLock)
            {
                if (aemeathSpriteDisposed)
                    throw new OperationCanceledException(cancellationToken);

                aemeathSpriteRows = rows;
            }
            return rows;
        }
        catch
        {
            foreach (var row in rows)
            {
                if (row is null)
                    continue;

                foreach (var texture in row)
                    texture?.Dispose();
            }

            throw;
        }
    }

    private static void DrawAemeathScrollRail(string id)
    {
        var maximum = ImGui.GetScrollMaxY();
        if (maximum <= 1f)
            return;

        var cursor = ImGui.GetCursorScreenPos();
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        const float railInset = 5f;
        const float railWidth = 4f;
        var railTop = windowPosition.Y + 8f;
        var railHeight = Math.Max(32f, windowSize.Y - 16f);
        var handleHeight = Math.Clamp(railHeight * windowSize.Y / (windowSize.Y + maximum), 28f, railHeight);
        var handleTravel = Math.Max(1f, railHeight - handleHeight);
        var handleTop = railTop + (ImGui.GetScrollY() / maximum) * handleTravel;
        var railLeft = windowPosition.X + windowSize.X - railInset - railWidth;

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            new Vector2(railLeft, railTop),
            new Vector2(railLeft + railWidth, railTop + railHeight),
            ImGui.GetColorU32(new Vector4(0.12f, 0.30f, 0.40f, 0.12f)),
            2f);
        drawList.AddRectFilled(
            new Vector2(railLeft, handleTop),
            new Vector2(railLeft + railWidth, handleTop + handleHeight),
            ImGui.GetColorU32(new Vector4(0.07f, 0.57f, 0.68f, 0.60f)),
            2f);
        drawList.AddRectFilled(
            new Vector2(railLeft, handleTop),
            new Vector2(railLeft + railWidth, handleTop + Math.Min(12f, handleHeight)),
            ImGui.GetColorU32(new Vector4(0.82f, 0.28f, 0.52f, 0.62f)),
            2f);

        ImGui.SetCursorScreenPos(new Vector2(railLeft - 7f, railTop));
        var clicked = ImGui.InvisibleButton($"##{id}", new Vector2(18f, railHeight));
        if (ImGui.IsItemActive())
        {
            var mouseY = ImGui.GetIO().MousePos.Y;
            var ratio = Math.Clamp((mouseY - railTop - handleHeight * 0.5f) / handleTravel, 0f, 1f);
            ImGui.SetScrollY(ratio * maximum);
        }
        else if (clicked)
        {
            var mouseY = ImGui.GetIO().MousePos.Y;
            var ratio = Math.Clamp((mouseY - railTop) / railHeight, 0f, 1f);
            ImGui.SetScrollY(ratio * maximum);
        }
        ImGui.SetCursorScreenPos(cursor);
    }

    private void PushAemeathStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16, 14));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, AemeathNavy);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.035f, 0.06f, 0.16f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.05f, 0.08f, 0.22f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, new Vector4(0.035f, 0.06f, 0.16f, 0.94f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, AemeathPanel);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.29f, 0.89f, 0.88f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.Text, AemeathInk);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, AemeathMuted);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.16f, 0.32f, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.20f, 0.35f, 0.48f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.55f, 0.16f, 0.36f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.10f, 0.22f, 0.37f, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.16f, 0.34f, 0.45f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.55f, 0.16f, 0.36f, 0.94f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.035f, 0.06f, 0.16f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.29f, 0.89f, 0.88f, 0.26f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark, AemeathCyan);
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, AemeathPink);
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, AemeathCyan);
        ImGui.PushStyleColor(ImGuiCol.TableHeaderBg, new Vector4(0.10f, 0.22f, 0.37f, 0.82f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBg, new Vector4(0.08f, 0.14f, 0.28f, 0.46f));
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, new Vector4(0.12f, 0.18f, 0.34f, 0.46f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, new Vector4(0f, 0f, 0f, 0f));
    }

    private static void PopAemeathStyle()
    {
        ImGui.PopStyleColor(29);
        ImGui.PopStyleVar(7);
    }

    private void DrawAemeathWindowChrome()
    {
        const float headerHeight = 42f;
        var drawList = ImGui.GetWindowDrawList();
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        // The chrome is a compact pixel badge and two color channels, not a second portrait.
        var headerEnd = position + new Vector2(size.X, headerHeight);
        drawList.AddRectFilled(position, headerEnd,
            ImGui.GetColorU32(new Vector4(0.13f, 0.055f, 0.24f, 0.24f)),
            14f, ImDrawFlags.RoundCornersTop);
        drawList.AddRect(position + new Vector2(1f, 1f), position + size - new Vector2(1f, 1f),
            ImGui.GetColorU32(new Vector4(0.52f, 0.35f, 0.76f, 0.36f)),
            14f, ImDrawFlags.RoundCornersAll, 1f);

        ImGui.SetCursorScreenPos(position + new Vector2(18f, 13f));
        ImGui.TextColored(AemeathInk, "海豹助手");

        ImGui.SetCursorScreenPos(position + new Vector2(size.X - 48f, 6f));
        if (ImGui.InvisibleButton("##AemeathClose", new Vector2(34f, 24f)))
            isOpen = false;
        var closeCenter = position + new Vector2(size.X - 31f, 18f);
        drawList.AddLine(closeCenter + new Vector2(-4f, -4f), closeCenter + new Vector2(4f, 4f),
            ImGui.GetColorU32(AemeathPink), 2f);
        drawList.AddLine(closeCenter + new Vector2(4f, -4f), closeCenter + new Vector2(-4f, 4f),
            ImGui.GetColorU32(AemeathPink), 2f);

        ImGui.SetCursorScreenPos(position + new Vector2(4f, 4f));
        ImGui.InvisibleButton("##AemeathDrag", new Vector2(Math.Max(80f, size.X - 60f), 28f));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
            if (delta != Vector2.Zero)
            {
                ImGui.SetWindowPos(position + delta);
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            }
        }
        return;
    }

    private void DrawAemeathWindowBackdrop()
    {
        var drawList = ImGui.GetWindowDrawList();
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        const float cornerRadius = 14f;
        var backdropMin = windowPosition + new Vector2(1f, 1f);
        var backdropMax = windowPosition + windowSize - new Vector2(1f, 1f);
        var backdropSize = backdropMax - backdropMin;
        if (backdropSize.X <= 0f || backdropSize.Y <= 0f)
            return;

        drawList.PushClipRect(windowPosition, windowPosition + windowSize, false);
        DrawScreenBackground(drawList, backdropMin, backdropMax, 1f, cornerRadius);
        drawList.PopClipRect();
    }

    private static void DrawScreenBackground(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float strength,
        float rounding)
    {
        var size = max - min;
        if (size.X <= 2f || size.Y <= 2f)
            return;

        strength = Math.Clamp(strength, 0f, 1f);
        var time = ImGui.GetTime();
        var screenMin = min;
        var screenMax = max;
        var screenSize = screenMax - screenMin;
        if (screenSize.X <= 8f || screenSize.Y <= 8f)
            return;

        // Extend the purple display and its animated details across the full client area.
        drawList.AddRectFilled(screenMin, screenMax,
            ImGui.GetColorU32(new Vector4(0.20f, 0.13f, 0.38f, 0.98f * strength)),
            rounding, ImDrawFlags.RoundCornersAll);
        const int gradientBands = 38;
        for (var band = 0; band < gradientBands; band++)
        {
            var t = band / (float)(gradientBands - 1);
            var centerWeight = 1f - MathF.Min(1f, MathF.Abs(t - 0.47f) * 1.85f);
            var pinkWeight = 0.30f + centerWeight * 0.58f;
            var blueWeight = 0.42f + (1f - centerWeight) * 0.33f;
            var bandYMin = screenMin.Y + screenSize.Y * t;
            var bandYMax = screenMin.Y + screenSize.Y * ((band + 1) / (float)gradientBands);
            var cornerInset = Math.Max(
                RoundedCornerInset(bandYMin, screenMin.Y, screenMax.Y, rounding),
                RoundedCornerInset(bandYMax, screenMin.Y, screenMax.Y, rounding));
            var bandMin = new Vector2(screenMin.X + cornerInset, bandYMin);
            var bandMax = new Vector2(screenMax.X - cornerInset, bandYMax);
            drawList.AddRectFilled(bandMin, bandMax,
                ImGui.GetColorU32(new Vector4(
                    0.12f + pinkWeight * 0.22f,
                    0.07f + pinkWeight * 0.12f,
                    0.29f + blueWeight * 0.28f,
                    (0.34f + centerWeight * 0.18f) * strength)), 0f);
        }

        var centerX = screenMin.X + screenSize.X * 0.50f;
        var centerY = screenMin.Y + screenSize.Y * 0.53f;
        for (var glow = 8; glow >= 1; glow--)
        {
            var fraction = glow / 8f;
            var halfWidth = screenSize.X * (0.10f + fraction * 0.44f);
            var alpha = (0.010f + (1f - fraction) * 0.020f) * strength;
            drawList.AddRectFilled(
                new Vector2(centerX - halfWidth, screenMin.Y),
                new Vector2(centerX + halfWidth, screenMax.Y),
                ImGui.GetColorU32(new Vector4(0.95f, 0.35f, 0.78f, alpha)), 0f);
        }
        drawList.AddRectFilled(
            new Vector2(centerX - screenSize.X * 0.24f, centerY - screenSize.Y * 0.36f),
            new Vector2(centerX + screenSize.X * 0.24f, centerY + screenSize.Y * 0.34f),
            ImGui.GetColorU32(new Vector4(1.00f, 0.55f, 0.83f, 0.035f * strength)), 0f);

        // Scanlines, grain and complete glyphs reproduce the CRT texture without broken pixels.
        var lineSpacing = Math.Clamp(screenSize.Y / 106f, 1.15f, 2.8f);
        var lineIndex = 0;
        for (var y = screenMin.Y + 0.5f; y < screenMax.Y - 0.5f; y += lineSpacing, lineIndex++)
        {
            var cornerInset = RoundedCornerInset(y, screenMin.Y, screenMax.Y, rounding);
            var pulse = 0.5f + 0.5f * MathF.Sin((float)time * 1.35f + lineIndex * 0.37f);
            var lineColor = (lineIndex & 3) == 0
                ? new Vector4(0.96f, 0.57f, 0.88f, (0.050f + pulse * 0.040f) * strength)
                : new Vector4(0.51f, 0.76f, 0.98f, (0.034f + pulse * 0.028f) * strength);
            drawList.AddRectFilled(new Vector2(screenMin.X + cornerInset, y),
                new Vector2(screenMax.X - cornerInset, y + 0.66f), ImGui.GetColorU32(lineColor), 0f);
        }

        var tick = (uint)Math.Max(0, (int)(time * 18d));
        for (var grain = 0; grain < 150; grain++)
        {
            var gy = screenMin.Y + ScreenHash(tick + (uint)(grain * 31 + 19)) * screenSize.Y;
            var cornerInset = RoundedCornerInset(gy, screenMin.Y, screenMax.Y, rounding);
            var grainWidth = Math.Max(0f, screenSize.X - cornerInset * 2f);
            var gx = screenMin.X + cornerInset + ScreenHash(tick + (uint)(grain * 17 + 7)) * grainWidth;
            var grainSize = 0.45f + ScreenHash((uint)(grain * 47 + 23)) * 1.55f;
            var grainAlpha = (0.025f + ScreenHash((uint)(grain * 71 + tick)) * 0.075f) * strength;
            var grainColor = (grain & 1) == 0
                ? new Vector4(0.72f, 0.91f, 1.00f, grainAlpha)
                : new Vector4(1.00f, 0.56f, 0.84f, grainAlpha);
            drawList.AddRectFilled(new Vector2(gx, gy), new Vector2(gx + grainSize, gy + grainSize),
                ImGui.GetColorU32(grainColor), 0f);
        }

        var glyphPixel = Math.Clamp(screenSize.X / 220f, 2.8f, 4.3f);
        var glyphHeight = glyphPixel * 5f;
        var rowSpacing = Math.Clamp(screenSize.Y / 10.5f, 25f, 43f);
        const int glyphsPerStream = 8;
        const int gapRowsPerStream = 3;
        const int streamPatternLength = glyphsPerStream + gapRowsPerStream;
        var streamRows = (int)MathF.Ceiling(screenSize.Y / rowSpacing) + 2;
        streamRows = (int)MathF.Ceiling(streamRows / (float)streamPatternLength) * streamPatternLength;
        var streamCycle = streamRows * rowSpacing;
        var glyphAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (var column = 0; column < ScreenGlyphStreamPositions.Length; column++)
        {
            var x = screenMin.X + screenSize.X * ScreenGlyphStreamPositions[column];
            var phase = (float)((time * ScreenGlyphStreamSpeeds[column] * rowSpacing
                + column * rowSpacing * 1.73d) % streamCycle);
            // Cover the full wrapped cycle so glyphs pass continuously from the bottom back to the top.
            for (var row = -streamRows; row < streamRows; row++)
            {
                var y = screenMin.Y + row * rowSpacing + phase - rowSpacing;
                if (y < screenMin.Y || y + glyphHeight > screenMax.Y)
                    continue;

                var patternRow = ((row % streamRows) + streamRows) % streamRows;
                if (patternRow % streamPatternLength >= glyphsPerStream)
                    continue;

                var hash = (uint)(patternRow * 7919 + column * 104729 + 17);
                var glyphIndex = (int)(ScreenHash(hash + 7U) * glyphAlphabet.Length);
                var palette = (int)(ScreenHash(hash + 13U) * 5f);
                var edgeFade = Math.Clamp(MathF.Min(
                    (y - screenMin.Y + rowSpacing) / (rowSpacing * 2.6f),
                    (screenMax.Y - y + rowSpacing) / (rowSpacing * 2.6f)), 0.12f, 1f);
                var alpha = (0.48f + ScreenHash(hash + 19U) * 0.38f) * edgeFade * strength;
                var glyphColor = palette switch
                {
                    0 => new Vector4(0.49f, 1.00f, 0.91f, alpha),
                    1 => new Vector4(1.00f, 0.58f, 0.88f, alpha),
                    2 => new Vector4(0.78f, 0.83f, 1.00f, alpha),
                    3 => new Vector4(1.00f, 0.91f, 0.60f, alpha),
                    _ => new Vector4(0.70f, 0.96f, 1.00f, alpha),
                };
                DrawPixelGlyph(drawList, glyphAlphabet[glyphIndex], new Vector2(x, y),
                    glyphPixel, glyphColor, ScreenGlyphBitmaps);
            }
        }

    }

    private static float ScreenHash(uint value)
    {
        value = unchecked(value ^ (value >> 16));
        value = unchecked(value * 0x7feb352dU);
        value = unchecked(value ^ (value >> 15));
        value = unchecked(value * 0x846ca68bU);
        value ^= value >> 16;
        return (value & 0x00ffffffU) / 16777215f;
    }

    private static float RoundedCornerInset(float y, float top, float bottom, float radius)
    {
        var distanceToEdge = Math.Min(y - top, bottom - y);
        if (radius <= 0f || distanceToEdge >= radius)
            return 0f;

        var offset = radius - Math.Max(0f, distanceToEdge);
        return radius - MathF.Sqrt(Math.Max(0f, radius * radius - offset * offset));
    }

    private static void DrawPixelGlyph(
        ImDrawListPtr drawList,
        char glyph,
        Vector2 position,
        float pixelSize,
        Vector4 color,
        IReadOnlyList<string> bitmaps)
    {
        var glyphIndex = glyph switch
        {
            >= '0' and <= '9' => glyph - '0',
            >= 'A' and <= 'Z' => glyph - 'A' + 10,
            _ => 0,
        };
        var bitmap = bitmaps[glyphIndex];
        var packedColor = ImGui.GetColorU32(color);
        for (var pixel = 0; pixel < bitmap.Length; pixel++)
        {
            if (bitmap[pixel] != '1')
                continue;

            var x = pixel % 3;
            var y = pixel / 3;
            var topLeft = position + new Vector2(x * pixelSize, y * pixelSize);
            drawList.AddRectFilled(topLeft, topLeft + new Vector2(pixelSize * 0.82f), packedColor);
        }
    }


    private void DrawAemeathHero()
    {
        var size = new Vector2(-1, 42);
        if (!ImGui.BeginChild("AemeathHero", size, false, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        var windowSize = ImGui.GetWindowSize();
        ImGui.SetCursorPos(new Vector2(Math.Max(420f, windowSize.X - 252f), 10f));
        DrawStatusPill("系统", plugin.IsAutoTreasureHuntEnabled ? AemeathCyan : AemeathGold,
            plugin.IsAutoTreasureHuntEnabled ? "运行中" : "待机");
        ImGui.SameLine();
        DrawStatusPill("权限", plugin.IsCredentialValidated ? AemeathGold : AemeathPink,
            plugin.IsCredentialValidated ? plugin.CredentialRoleName : "未验证");
        ImGui.EndChild();
        return;

#if false
        if (aemeathBannerTexture?.TryGetWrap(out var banner, out _) == true)
        {
            // Crop the 16:9 artwork to the hero strip instead of stretching the character.
            var targetAspect = windowSize.X / Math.Max(1f, windowSize.Y);
            var sourceAspect = 16f / 9f;
            var cropHeight = Math.Clamp(sourceAspect / Math.Max(0.1f, targetAspect), 0.05f, 1f);
            var cropTop = (1f - cropHeight) * 0.5f;
            drawList.AddImage(banner.Handle, position, position + windowSize,
                new Vector2(0, cropTop), new Vector2(1, cropTop + cropHeight),
                ImGui.GetColorU32(new Vector4(1, 1, 1, 0.20f)));
        }
        drawList.AddRectFilled(position, position + windowSize,
            ImGui.GetColorU32(new Vector4(0.98f, 0.96f, 0.99f, 0.30f)), 8f);
        var roleBadgeTopLeft = position + new Vector2(windowSize.X - 184f, 34f);
        if (aemeathProfileTexture?.TryGetWrap(out var profile, out _) == true)
        {
            drawList.AddImageRounded(
                profile.Handle,
                roleBadgeTopLeft,
                roleBadgeTopLeft + new Vector2(76f, 76f),
                new Vector2(0.20f, 0.08f),
                new Vector2(0.80f, 0.46f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.22f)),
                38f,
                ImDrawFlags.RoundCornersAll);
        }
        DrawAemeathRoleSigil(drawList, roleBadgeTopLeft, 74f, 0.78f);

        ImGui.SetCursorPos(new Vector2(20, 16));
        ImGui.TextColored(AemeathCyan, "A E M E A T H");
        ImGui.SameLine();
        ImGui.TextColored(AemeathPink, "TREASURE");
        ImGui.SetCursorPos(new Vector2(20, 47));
        ImGui.TextColored(AemeathInk, "海豹助手");
        ImGui.SameLine();
        ImGui.TextDisabled("TREASURE OPERATIONS CONSOLE");
        ImGui.SetCursorPos(new Vector2(20, 78));
        ImGui.TextDisabled("粉色星炬 · 青色扫描 · 低延迟自动化");

        ImGui.SetCursorPos(new Vector2(Math.Max(440, windowSize.X - 245), 23));
        DrawStatusPill("系统", plugin.IsAutoTreasureHuntEnabled ? AemeathCyan : AemeathGold,
            plugin.IsAutoTreasureHuntEnabled ? "ACTIVE" : "STANDBY");
        ImGui.SameLine();
        DrawStatusPill("权限", plugin.IsCredentialValidated ? AemeathGold : AemeathPink,
            plugin.IsCredentialValidated ? plugin.CredentialRoleName : "LOCKED");
        ImGui.EndChild();
#endif
    }

    private void DrawSealOnPage()
    {
        EnsureFleetTextureLoad();
        var frames = fleetSealFrames;
        if (frames is null || frames.Length == 0)
            return;

        var now = ImGui.GetTime();
        if (fleetSealNextFrameAt <= 0d)
            fleetSealNextFrameAt = now + 0.18d;
        while (now >= fleetSealNextFrameAt)
        {
            fleetSealFrame = (fleetSealFrame + 1) % frames.Length;
            fleetSealNextFrameAt += 0.18d;
        }

        var pagePosition = ImGui.GetWindowPos();
        var pageSize = ImGui.GetWindowSize();
        var sealWidth = Math.Clamp(pageSize.X * 0.22f, 126f, 188f);
        var sealHeight = sealWidth * 250f / 300f;
        var sealBottomRight = pagePosition + pageSize - new Vector2(26f, 16f);
        var sealTopLeft = sealBottomRight - new Vector2(sealWidth, sealHeight);
        ImGui.GetWindowDrawList().AddImage(frames[fleetSealFrame].Handle, sealTopLeft, sealBottomRight);
    }

    private void DrawSidebar()
    {
        var canOpenAutomation = plugin.IsCredentialValidated;
        DrawNavigationButton("自动挖宝", string.Empty, MainPage.Automation, canOpenAutomation);
        DrawNavigationButton("凭证验证", string.Empty, MainPage.Credential, !canOpenAutomation);
        DrawNavigationButton("测试面板", string.Empty, MainPage.Diagnostics, plugin.HasDeveloperCredential);
        DrawNavigationButton("IC 死宫", string.Empty, MainPage.DeepDungeon, true);

        ImGui.Spacing();
        DrawSidebarStatus("vnavmesh", plugin.IsVnavmeshRunning);
        DrawSidebarStatus("BossMod", plugin.IsBossModRebornRunning);
        if (plugin.IsCredentialValidated)
        {
            ImGui.Spacing();
            if (DrawAemeathButton("SidebarSignOut", "退出当前凭证", new Vector2(136f, 28f)))
            {
                plugin.ClearCredential();
                activePage = MainPage.Credential;
                credentialInput = plugin.HasSavedCredential ? RememberedCredentialMask : string.Empty;
                credentialStatus = string.Empty;
            }
        }
    }

    private void DrawAemeathSignalModule()
    {
        return;
#if false
        ImGui.TextColored(AemeathGold, "★★★★★");
        ImGui.SetCursorScreenPos(new Vector2(topLeft.X, bottomRight.Y + 6f));
    #endif
    }

    private void DrawNavigationButton(string label, string subtitle, MainPage page, bool enabled)
    {
        if (page == MainPage.Automation && !plugin.IsCredentialValidated)
            return;
        if (page == MainPage.Diagnostics && !plugin.HasDeveloperCredential)
            return;
        if (page == MainPage.Credential && plugin.IsCredentialValidated)
            return;

        var selected = activePage == page;
        var topLeft = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(120f, ImGui.GetContentRegionAvail().X), 48f);
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.InvisibleButton($"##Nav{page}", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        ImGui.EndDisabled();

        var drawList = ImGui.GetWindowDrawList();
        var fill = selected
            ? new Vector4(0.76f, 0.25f, 0.53f, 0.76f)
            : hovered
                ? new Vector4(0.10f, 0.35f, 0.43f, 0.84f)
                : new Vector4(0.04f, 0.10f, 0.24f, enabled ? 0.78f : 0.46f);
        if (active)
            fill = new Vector4(0.95f, 0.39f, 0.65f, 0.84f);
        drawList.AddRectFilled(topLeft, topLeft + size, ImGui.GetColorU32(fill), 7f);
        drawList.AddRect(topLeft, topLeft + size,
            ImGui.GetColorU32(selected
                ? new Vector4(0.95f, 0.39f, 0.65f, 0.82f)
                : new Vector4(0.29f, 0.89f, 0.88f, enabled ? 0.32f : 0.16f)),
            7f,
            ImDrawFlags.RoundCornersAll,
            1f);
        if (selected)
            drawList.AddRectFilled(topLeft, topLeft + new Vector2(3f, size.Y),
                ImGui.GetColorU32(AemeathPink), 2f);

        var textColor = enabled ? AemeathInk : AemeathMuted;
        var labelY = string.IsNullOrEmpty(subtitle) ? (size.Y - ImGui.GetTextLineHeight()) * 0.5f : 7f;
        drawList.AddText(topLeft + new Vector2(14f, labelY), ImGui.GetColorU32(textColor), label);
        if (!string.IsNullOrEmpty(subtitle))
        {
            drawList.AddText(topLeft + new Vector2(14f, 26f),
                ImGui.GetColorU32(enabled ? AemeathMuted : new Vector4(0.52f, 0.55f, 0.63f, 0.72f)),
                subtitle);
        }
        if (clicked)
            activePage = page;
    }

    private bool DrawAemeathButton(string id, string label, Vector2 requestedSize, bool enabled = true, bool accent = false)
    {
        var size = new Vector2(
            requestedSize.X > 0f ? requestedSize.X : Math.Max(120f, ImGui.GetContentRegionAvail().X),
            requestedSize.Y > 0f ? requestedSize.Y : 30f);
        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.InvisibleButton($"##{id}", size);
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        ImGui.EndDisabled();

        var fill = !enabled
            ? new Vector4(0.05f, 0.08f, 0.18f, 0.46f)
            : active
                ? new Vector4(0.95f, 0.39f, 0.65f, 0.74f)
                : hovered
                    ? new Vector4(0.10f, 0.35f, 0.43f, 0.86f)
                    : accent
                        ? new Vector4(0.76f, 0.25f, 0.53f, 0.76f)
                        : new Vector4(0.04f, 0.10f, 0.24f, 0.78f);
        var border = accent || active
            ? new Vector4(0.82f, 0.28f, 0.52f, 0.62f)
            : new Vector4(0.07f, 0.57f, 0.68f, enabled ? 0.34f : 0.14f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(topLeft, topLeft + size, ImGui.GetColorU32(fill), 6f);
        drawList.AddRect(topLeft, topLeft + size, ImGui.GetColorU32(border), 6f,
            ImDrawFlags.RoundCornersAll, 1f);
        drawList.AddText(topLeft + new Vector2(12f, Math.Max(4f, (size.Y - ImGui.GetTextLineHeight()) * 0.5f)),
            ImGui.GetColorU32(enabled ? AemeathInk : AemeathMuted), label);
        return clicked;
    }

    private bool DrawAemeathToggle(string id, string label, ref bool value, bool enabled = true)
    {
        var width = Math.Min(300f, Math.Max(180f, ImGui.GetContentRegionAvail().X));
        var height = 28f;
        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.InvisibleButton($"##{id}", new Vector2(width, height));
        ImGui.EndDisabled();
        if (clicked && enabled)
            value = !value;

        var drawList = ImGui.GetWindowDrawList();
        var trackMin = topLeft + new Vector2(2f, 5f);
        var trackMax = trackMin + new Vector2(38f, 18f);
        var trackColor = !enabled
            ? new Vector4(0.08f, 0.12f, 0.24f, 0.40f)
            : value
                ? new Vector4(0.95f, 0.39f, 0.65f, 0.82f)
                : new Vector4(0.08f, 0.29f, 0.38f, 0.72f);
        drawList.AddRectFilled(trackMin, trackMax, ImGui.GetColorU32(trackColor), 9f);
        drawList.AddRect(trackMin, trackMax,
            ImGui.GetColorU32(new Vector4(0.29f, 0.89f, 0.88f, enabled ? 0.46f : 0.18f)),
            9f,
            ImDrawFlags.RoundCornersAll,
            1f);
        var knob = trackMin + new Vector2(value ? 29f : 9f, 9f);
        drawList.AddCircleFilled(knob, 6f, ImGui.GetColorU32(value ? AemeathCyan : AemeathMuted));
        drawList.AddText(topLeft + new Vector2(52f, 5f),
            ImGui.GetColorU32(enabled ? AemeathInk : AemeathMuted), label);
        return clicked;
    }

    private bool DrawAemeathRadio(string id, string label, bool selected, bool enabled = true, float width = 82f)
    {
        var topLeft = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, 26f);
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.InvisibleButton($"##{id}", size);
        ImGui.EndDisabled();
        var drawList = ImGui.GetWindowDrawList();
        var center = topLeft + new Vector2(10f, 13f);
        drawList.AddCircle(center, 8f,
            ImGui.GetColorU32(new Vector4(0.07f, 0.57f, 0.68f, enabled ? 0.52f : 0.20f)), 20, 1.2f);
        if (selected)
            drawList.AddCircleFilled(center, 4.5f, ImGui.GetColorU32(AemeathPink));
        drawList.AddText(topLeft + new Vector2(24f, 5f),
            ImGui.GetColorU32(enabled ? AemeathInk : AemeathMuted), label);
        return clicked && enabled;
    }

    private static void DrawAemeathInputShell(Vector2 topLeft, Vector2 size, bool enabled = true)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            topLeft,
            topLeft + size,
            ImGui.GetColorU32(enabled
                ? new Vector4(0.04f, 0.12f, 0.27f, 0.86f)
                : new Vector4(0.04f, 0.08f, 0.18f, 0.46f)),
            7f);
        drawList.AddRect(
            topLeft,
            topLeft + size,
            ImGui.GetColorU32(new Vector4(0.29f, 0.89f, 0.88f, enabled ? 0.46f : 0.18f)),
            7f,
            ImDrawFlags.RoundCornersAll,
            1f);
        drawList.AddLine(
            topLeft + new Vector2(10f, size.Y - 2f),
            new Vector2(topLeft.X + Math.Min(size.X - 10f, 80f), topLeft.Y + size.Y - 2f),
            ImGui.GetColorU32(new Vector4(0.82f, 0.28f, 0.52f, enabled ? 0.55f : 0.20f)),
            1f);
    }

    private static bool DrawAemeathSlider(string id, string label, ref float value, float minimum, float maximum,
        string format = "0.00", bool enabled = true)
    {
        var width = Math.Min(360f, Math.Max(240f, ImGui.GetContentRegionAvail().X));
        const float height = 32f;
        var topLeft = ImGui.GetCursorScreenPos();
        ImGui.BeginDisabled(!enabled);
        var clicked = ImGui.InvisibleButton($"##{id}", new Vector2(width, height));
        var active = ImGui.IsItemActive();
        ImGui.EndDisabled();
        var changed = false;
        if ((clicked || active) && enabled)
        {
            var ratio = Math.Clamp((ImGui.GetIO().MousePos.X - (topLeft.X + 10f)) / (width - 20f), 0f, 1f);
            var next = minimum + (maximum - minimum) * ratio;
            if (Math.Abs(next - value) > 0.0001f)
            {
                value = next;
                changed = true;
            }
        }

        var drawList = ImGui.GetWindowDrawList();
        var trackMin = topLeft + new Vector2(10f, 19f);
        var trackMax = topLeft + new Vector2(width - 10f, 22f);
        drawList.AddRectFilled(trackMin, trackMax,
            ImGui.GetColorU32(new Vector4(0.15f, 0.32f, 0.42f, enabled ? 0.22f : 0.10f)), 2f);
        var ratioValue = maximum > minimum ? Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f) : 0f;
        drawList.AddRectFilled(trackMin,
            new Vector2(trackMin.X + (trackMax.X - trackMin.X) * ratioValue, trackMax.Y),
            ImGui.GetColorU32(enabled ? AemeathPink : AemeathMuted), 2f);
        drawList.AddCircleFilled(new Vector2(trackMin.X + (trackMax.X - trackMin.X) * ratioValue, 20.5f + topLeft.Y),
            5f, ImGui.GetColorU32(enabled ? AemeathCyan : AemeathMuted));
        drawList.AddText(topLeft + new Vector2(10f, 2f),
            ImGui.GetColorU32(enabled ? AemeathInk : AemeathMuted),
            $"{label}  {value.ToString(format, CultureInfo.InvariantCulture)}");
        return changed;
    }

    private static void DrawSidebarStatus(string label, bool ready)
    {
        ImGui.TextColored(ready ? AemeathCyan : StoppedColor, ready ? "●" : "○");
        ImGui.SameLine();
        ImGui.TextDisabled(label);
    }

    private void DrawActivePage()
    {
        if (!plugin.IsCredentialValidated && activePage is not (MainPage.Credential or MainPage.DeepDungeon))
            activePage = MainPage.Credential;
        else if (activePage == MainPage.Diagnostics && !plugin.HasDeveloperCredential)
            activePage = MainPage.Automation;
        else if (plugin.IsCredentialValidated && activePage == MainPage.Credential)
            activePage = MainPage.Automation;

        switch (activePage)
        {
            case MainPage.Credential:
                DrawCredentialPage();
                break;
            case MainPage.Diagnostics:
                DrawTestPage();
                break;
            case MainPage.DeepDungeon:
                DrawDeepDungeonPage();
                break;
            default:
                DrawAutoTreasureHuntPage();
                break;
        }
    }

    private static void DrawStatusPill(string label, Vector4 color, string value)
    {
        ImGui.TextDisabled(label);
        ImGui.SameLine(0, 4);
        ImGui.TextColored(color, $"[{value}]");
    }

    private void DrawCredentialPage()
    {
        plugin.EnsureValidationServerHealthCheck();

        if (string.IsNullOrEmpty(credentialInput) && plugin.HasSavedCredential)
        {
            credentialInput = RememberedCredentialMask;
        }

        DrawPageHeading("ACCESS GATE", "凭证验证", "连接星炬授权后解锁自动化控制台");
        DrawIdentityStrip("ACCESS PROTOCOL", "验证当前账号与插件权限");
        ImGui.Spacing();
        var serverStatusColor = plugin.ValidationServerConnected == true
            ? RunningColor
            : plugin.ValidationServerConnected == false
                ? StoppedColor
                : new Vector4(0.95f, 0.75f, 0.25f, 1.00f);
        ImGui.TextColored(serverStatusColor, plugin.ValidationServerStatusText);

        // 显示当前登录角色的 ContentId（CID），便于用户确认凭证绑定的账号。
        var accountIdColor = plugin.CurrentAccountId != 0
            ? RunningColor
            : new Vector4(0.95f, 0.75f, 0.25f, 1.00f);
        ImGui.TextColored(AemeathGold, "当前账号 CID");
        ImGui.SameLine();
        ImGui.TextColored(accountIdColor, plugin.CurrentAccountIdText);

        var credentialInputTopLeft = ImGui.GetCursorScreenPos();
        DrawAemeathInputShell(credentialInputTopLeft,
            new Vector2(ImGui.GetContentRegionAvail().X, 32f));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("###Credential", ref credentialInput, 128, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ValidateCredentialInput();
        }

        if (DrawAemeathButton("CredentialSubmit", "连接授权", new Vector2(150f, 34f), !credentialValidationInProgress, true))
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
            activePage = MainPage.Automation;
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
        DrawPageHeading("TREASURE OPERATIONS", "自动挖宝", "五层选门与野外挖宝流程控制台");
        DrawIdentityStrip(string.Empty, "五星共鸣者 · 选门路线");
        ImGui.Spacing();
        ImGui.TextColored(AemeathGold, $"当前逻辑  {plugin.CurrentLogicName}");
        DrawLogicModeSelector();

        var autoTreasureHuntEnabled = plugin.IsAutoTreasureHuntEnabled;
        if (DrawAemeathToggle("AutoToggle", "开启自动挖宝", ref autoTreasureHuntEnabled))
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
            DrawStatusPanel("车轮模式", "接受传送并前往红旗", plugin.AutoTreasureHuntStatus,
                plugin.IsAutoTreasureHuntEnabled ? AemeathCyan : AemeathGold);
            DrawRuntimeEnvironment();
            return;
        }

        if (plugin.IsSelectedDoorSelectionRoute)
            DrawDoorSelectionSettings();

        var autoMapSupplementEnabled = plugin.IsAutoMapSupplementEnabled;
        var maxUnitPriceEnabled = plugin.IsMapSupplementMaxUnitPriceEnabled;
        if (DrawAemeathToggle("MapPriceToggle", "启用补图最高单价", ref maxUnitPriceEnabled))
        {
            plugin.SetMapSupplementMaxUnitPriceEnabled(maxUnitPriceEnabled);
        }
        var mapSupplementMaxUnitPrice = plugin.MapSupplementMaxUnitPrice;
        if (mapSupplementMaxUnitPriceObserved != mapSupplementMaxUnitPrice ||
            string.IsNullOrEmpty(mapSupplementMaxUnitPriceInput))
        {
            mapSupplementMaxUnitPriceInput = mapSupplementMaxUnitPrice.ToString("N0", CultureInfo.InvariantCulture);
            mapSupplementMaxUnitPriceObserved = mapSupplementMaxUnitPrice;
        }

        var priceInputTopLeft = ImGui.GetCursorScreenPos();
        DrawAemeathInputShell(priceInputTopLeft,
            new Vector2(Math.Min(560f, ImGui.GetContentRegionAvail().X), 30f), maxUnitPriceEnabled);
        ImGui.BeginDisabled(!maxUnitPriceEnabled);
        ImGui.InputText("补图最高单价", ref mapSupplementMaxUnitPriceInput, 32,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.EndDisabled();
        if (ImGui.IsItemDeactivatedAfterEdit() || ImGui.IsItemDeactivated())
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
        ImGui.TextDisabled("仅补图自动购买生效，报价大于此值时停止购买");
        if (DrawAemeathToggle("AutoMapToggle", "自动补图", ref autoMapSupplementEnabled))
        {
            plugin.SetAutoMapSupplementEnabled(autoMapSupplementEnabled);
        }

        ImGui.Spacing();
        DrawStatusPanel("运行回路", "实时流程状态", plugin.AutoTreasureHuntStatus,
            plugin.IsAutoTreasureHuntEnabled ? AemeathCyan : AemeathPink);
        ImGui.Separator();
        ImGui.TextColored(AemeathGold, "地图配置");
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

    private void DrawDoorSelectionSettings()
    {
        ImGui.Spacing();
        ImGui.TextColored(AemeathGold, "选门方向 / USER ROUTE");
        ImGui.TextDisabled("门由用户选择；发光门消息只做运行时纠正");
        DrawDoorChoice("第一层 → 第二层", 0);
        DrawDoorChoice("第二层 → 第三层", 1);
        DrawDoorChoice("第三层 → 第四层", 2);
        DrawDoorChoice("第四层 → 第五层", 3);
    }

    private void DrawDoorChoice(string label, int floor)
    {
        var value = plugin.GetDoorSelectionChoice(floor);
        ImGui.Text(label);
        ImGui.SameLine(190);
        if (DrawAemeathRadio($"DoorLeft{floor}", "左门", value == DoorSelectionChoice.Left, true, 82f))
            plugin.SetDoorSelectionChoice(floor, DoorSelectionChoice.Left);
        ImGui.SameLine(278);
        if (DrawAemeathRadio($"DoorRight{floor}", "右门", value == DoorSelectionChoice.Right, true, 82f))
            plugin.SetDoorSelectionChoice(floor, DoorSelectionChoice.Right);
    }

    private void DrawPageHeading(string eyebrow, string title, string subtitle)
    {
        ImGui.TextColored(AemeathInk, title);
        ImGui.SameLine();
        ImGui.TextDisabled(subtitle);
        ImGui.Separator();
    }

    private void DrawIdentityStrip(string label, string description)
    {
        ImGui.BeginGroup();
        ImGui.TextDisabled(description);
        ImGui.EndGroup();
        return;

#if false
        var available = Math.Max(250f, ImGui.GetContentRegionAvail().X);
        var imageWidth = Math.Min(76f, available * 0.16f);
        var legacyIdentityTopLeft = ImGui.GetCursorScreenPos();
        if (aemeathProfileTexture?.TryGetWrap(out var profile, out _) == true)
        {
            ImGui.GetWindowDrawList().AddImageRounded(
                profile.Handle,
                legacyIdentityTopLeft,
                legacyIdentityTopLeft + new Vector2(imageWidth, 70f),
                new Vector2(0.18f, 0.08f),
                new Vector2(0.82f, 0.48f),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.24f)),
                6f,
                ImDrawFlags.RoundCornersAll);
        }
        DrawAemeathIdentityGlyph(new Vector2(imageWidth, 70));
        ImGui.SameLine(0, 12);
        ImGui.BeginGroup();
        ImGui.TextColored(AemeathPink, label);
        ImGui.TextDisabled(description);
        ImGui.TextColored(AemeathGold, "★★★★★   ");
        ImGui.SameLine(0, 0);
        ImGui.TextDisabled("digital ghost / among the stars");
#endif
    }

    private static void DrawStatusPanel(string title, string subtitle, string status, Vector4 accent)
    {
        ImGui.TextColored(accent, $"◆ {title}");
        ImGui.SameLine();
        ImGui.TextDisabled(subtitle);
        ImGui.TextWrapped(status);
    }

    private void DrawRuntimeEnvironment()
    {
        ImGui.Spacing();
        ImGui.TextColored(AemeathGold, "运行环境 / SYSTEM LINK");
        DrawPluginStatus("vnavmesh", "自动寻路", plugin.IsVnavmeshRunning);
        DrawPluginStatus("BossMod Reborn", "副本机制处理", plugin.IsBossModRebornRunning);

        var allReady = plugin.IsVnavmeshRunning &&
                       plugin.IsBossModRebornRunning;
        ImGui.TextColored(
            allReady ? AemeathCyan : AemeathPink,
            allReady ? "● 运行环境已就绪" : "○ 运行环境尚未就绪");

        if (!allReady && DrawAemeathButton("OpenInstaller", "打开插件安装器", new Vector2(164f, 30f), true, false))
        {
            var missingPlugin = !plugin.IsVnavmeshRunning
                ? "vnavmesh"
                : "BossModReborn";
            pluginInterface.OpenPluginInstallerTo(searchText: missingPlugin);
        }
    }

    private void DrawDeepDungeonPage()
    {
        DrawPageHeading("DEEP DUNGEON", "IC 死宫", "高度与移速调整工具");
        DrawIdentityStrip("MOVEMENT CONTROL", "仅在有效深层迷宫层数读取后生效");
        ImGui.Spacing();

        ImGui.Text("当前层数：");
        ImGui.SameLine();
        ImGui.Text(plugin.DeepDungeonCurrentFloor > 0
            ? plugin.DeepDungeonCurrentFloor.ToString(CultureInfo.InvariantCulture)
            : "未读取");
        ImGui.TextWrapped(plugin.DeepDungeonMovementStatus);

        var enabled = plugin.DeepDungeonMovementEnabled;
        var adjustment = plugin.DeepDungeonMovementAdjustment;

        var changed = DrawAemeathToggle("DeepDungeonHeight", "启用深层迷宫高度调整", ref enabled);
        ImGui.SetNextItemWidth(220f);
        changed |= ImGui.InputFloat("高度调整值", ref adjustment, 0.1f, 1f, "%.2f");
        adjustment = float.IsFinite(adjustment) ? Math.Clamp(adjustment, -10f, 10f) : 0f;
        if (DrawAemeathButton("DeepDungeonHeightDefault", "高度设为 -7", new Vector2(120f, 28f)))
        {
            adjustment = -7f;
            changed = true;
        }

        ImGui.TextDisabled("调整值范围：-10 至 10；仅在有效深层迷宫层数已读取时生效。");
        if (changed)
            plugin.SetDeepDungeonMovementSettings(enabled, adjustment);

        ImGui.Separator();
        ImGui.Text("深层迷宫移速");
        var speedEnabled = plugin.DeepDungeonSpeedEnabled;
        var speedAddition = plugin.DeepDungeonSpeedAddition;
        var maxAcceleration = plugin.DeepDungeonMaxAcceleration;
        var speedChanged = DrawAemeathToggle("DeepDungeonSpeed", "启用移速调整", ref speedEnabled);
        speedChanged |= DrawAemeathSlider("DeepDungeonSpeedSlider", "移速增加", ref speedAddition, 0f, 5f);
        if (DrawAemeathButton("DeepDungeonSpeedUp", "移速增加 +0.01", new Vector2(140f, 28f)))
        {
            speedAddition = Math.Clamp(speedAddition + 0.01f, 0f, 5f);
            speedChanged = true;
        }
        ImGui.SameLine();
        if (DrawAemeathButton("DeepDungeonSpeedDown", "移速减少 -0.01", new Vector2(140f, 28f)))
        {
            speedAddition = Math.Clamp(speedAddition - 0.01f, 0f, 5f);
            speedChanged = true;
        }
        speedChanged |= DrawAemeathToggle("DeepDungeonMaxAcceleration", "最大加速度", ref maxAcceleration);
        ImGui.TextWrapped(plugin.DeepDungeonSpeedStatus);
        ImGui.TextDisabled("移速签名已内置；只有在深层迷宫内且未受限制状态影响时生效。");

        if (speedChanged)
            plugin.SetDeepDungeonSpeedSettings(speedEnabled, speedAddition, maxAcceleration);
    }

    private void DrawTestPage()
    {
        DrawPageHeading("DEVELOPER CONSOLE", "测试面板", "诊断 VFX、BaseID、门消息与传送流程");
        ImGui.TextColored(AemeathPink, "动画锁测试");
        var animationLockEnabled = plugin.AnimationLockOverrideEnabled;
        var animationLockMilliseconds = plugin.AnimationLockOverrideMilliseconds;
        var animationLockChanged = DrawAemeathToggle("AnimationLock", "启用动画锁覆盖", ref animationLockEnabled);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        animationLockChanged |= ImGui.InputInt("动画锁 (毫秒)##AnimationLock", ref animationLockMilliseconds);
        animationLockMilliseconds = Math.Clamp(animationLockMilliseconds, 0, 2000);
        if (animationLockChanged)
        {
            plugin.SetAnimationLockOverride(animationLockEnabled, animationLockMilliseconds);
        }
        ImGui.TextDisabled("仅对本地角色动作生效；输入 0 会将动画锁设为 0。启用后需重新执行动作验证。" );

        var testButtonsInRow = 0;
        void ContinueTestButtonRow()
        {
            testButtonsInRow++;
            if (testButtonsInRow >= 4 || ImGui.GetContentRegionAvail().X < 220f)
            {
                ImGui.NewLine();
                testButtonsInRow = 0;
            }
            else
                ImGui.SameLine();
        }
        if (DrawAemeathButton("EmergencyStop", "紧急停止", new Vector2(110f, 30f), true, true))
        {
            plugin.EmergencyStop();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestInteractables", "测试遍历可交互物体", new Vector2(178f, 30f)))
        {
            plugin.TestListInteractableObjects();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestNonTargetable", "测试遍历不可选中物体", new Vector2(178f, 30f)))
        {
            plugin.TestListNonTargetableObjects();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestEffectCenter", "获取特效中心 XYZ", new Vector2(150f, 30f)))
        {
            plugin.TestCaptureEffectCenter();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestVfxStructure", "扫描 VFX 特效结构", new Vector2(158f, 30f)))
        {
            plugin.TestProbeVfxEffectCenter();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestBaseIdlessVfx", "测试寻找无 BaseID 的 VFX", new Vector2(190f, 30f)))
        {
            plugin.TestProbeBaseIdlessVfx();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestVfxPath", "读取 VFX .avfx 路径", new Vector2(158f, 30f)))
        {
            plugin.TestProbeVfxResourcePaths();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestFieldMarker", "获取场地标记 XYZ", new Vector2(158f, 30f)))
        {
            plugin.TestCaptureFieldMarker();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestPortalVfx", "Navigate to portal VFX", new Vector2(168f, 30f)))
        {
            plugin.TestNavigateToDoorSelectionPortalVfx();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestDoorFloor", "检测当前选门层数", new Vector2(158f, 30f)))
        {
            plugin.TestDetectDoorSelectionFloor();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestGlowingDoor", "检测最近门发光消息", new Vector2(170f, 30f)))
        {
            plugin.TestDetectDoorSelectionGlowingDoor();
        }
        ImGui.TextWrapped(plugin.InteractableObjectScanStatus);
        ImGui.TextWrapped(plugin.EffectCenterTestStatus);
        ImGui.TextWrapped(plugin.FieldMarkerTestStatus);
        ImGui.TextWrapped(plugin.DoorSelectionFloorDetectionTestStatus);
        ImGui.TextWrapped(plugin.DoorSelectionGlowingDoorTestStatus);

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("目标 BaseID###NavigationBaseId", ref navigationBaseIdInput, 16);
        ImGui.SameLine();
        if (DrawAemeathButton("TestBaseIdNavigation", "测试按 BaseID 坐标寻路", new Vector2(180f, 30f)))
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

        if (DrawAemeathButton("ReadOtherCoordinate", "读取当前坐标", new Vector2(140f, 30f)))
        {
            plugin.ReadOtherPluginCurrentCoordinate();
        }
        ImGui.SameLine();
        if (DrawAemeathButton("ApplyOtherCoordinate", "测试修改自身位置", new Vector2(160f, 30f)))
        {
            plugin.TestOtherPluginApplyCoordinate(coordinateX, coordinateY, coordinateZ);
        }
        ImGui.SameLine();
        if (DrawAemeathButton("OpenOtherMarket", "测试打开市场", new Vector2(132f, 30f)))
        {
            plugin.TestOtherPluginOpenMarket();
        }

        if (DrawAemeathButton("TestMarketBanner", "测试市场图片路径", new Vector2(160f, 30f)))
        {
            plugin.TestMarketBannerPath();
        }

        ImGui.TextWrapped(plugin.OtherPluginTestStatus);
        ImGui.TextWrapped(plugin.OtherPluginMarketStatus);
        ImGui.TextDisabled("市场测试：可从任意地点打开原生搜索界面；该界面不保证具有服务器购买会话。");

        ImGui.Separator();
        if (!plugin.IsHeadLogicSelected)
        {
            ImGui.TextWrapped("当前选择车轮逻辑。可在此测试聊天地图链接获取红旗；车头测试功能已关闭。");
            if (DrawAemeathButton("TestWheelMapLink", "测试获取聊天地图红旗", new Vector2(180f, 30f)))
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
        if (DrawAemeathRadio("LogicHead", "车头", plugin.IsHeadLogicSelected, true, 76f))
        {
            plugin.SetLogicMode(TreasureHuntLogicMode.Head);
        }

        ImGui.SameLine();
        if (DrawAemeathRadio("LogicWheel", "车轮", plugin.IsWheelLogicSelected, true, 76f))
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
            if (DrawAemeathButton("UseTreasureMapTest", "测试解读地图", new Vector2(132f, 30f), plugin.CanUseTreasureMap))
            {
                plugin.UseTreasureMapForTest();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled(plugin.TreasureMapUseStatus);

            ImGui.BeginDisabled(plugin.SaddlebagTreasureMapCount == 0);
            if (DrawAemeathButton("MoveMapFromSaddlebag", "测试取出鞍囊地图", new Vector2(160f, 30f), plugin.SaddlebagTreasureMapCount > 0))
            {
                plugin.TestMoveMapFromSaddlebag();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();

            ImGui.BeginDisabled(
                plugin.MainInventoryTreasureMapCount == 0 ||
                plugin.SaddlebagTreasureMapCount > 0);
            if (DrawAemeathButton("MoveMapToSaddlebag", "测试存入鞍囊地图", new Vector2(160f, 30f),
                    plugin.MainInventoryTreasureMapCount > 0 && plugin.SaddlebagTreasureMapCount == 0))
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

        if (DrawAemeathButton("AnnounceFlag", "测试播报红旗", new Vector2(132f, 30f)))
        {
            plugin.TestAnnounceCurrentFlag();
        }
        ImGui.SameLine();

        ImGui.BeginDisabled(!plugin.HasTaskTreasureMap);
        if (DrawAemeathButton("TeleportAetheryte", "测试传送水晶", new Vector2(132f, 30f), plugin.HasTaskTreasureMap))
        {
            plugin.TestTeleportToOpenedMapAetheryte();
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (DrawAemeathButton("MountRoulette", "测试随机坐骑", new Vector2(132f, 30f)))
        {
            plugin.UseMountRouletteForTest();
        }
        ImGui.SameLine();
        if (DrawAemeathButton("Dismount", "测试跳下坐骑", new Vector2(132f, 30f)))
        {
            plugin.TestDismount();
        }
        if (DrawAemeathButton("FindOpenChest", "测试寻找并开启宝箱", new Vector2(170f, 30f)))
        {
            plugin.TestFindAndOpenTreasureChest();
        }
        ImGui.SameLine();
        if (DrawAemeathButton("EnterTreasurePortal", "测试交互并确认魔纹", new Vector2(170f, 30f)))
        {
            plugin.TestFindAndEnterTreasurePortal();
        }
        ImGui.SameLine();
        if (DrawAemeathButton("ExitRouletteInstance", "测试退出副本", new Vector2(132f, 30f)))
        {
            plugin.TestExitRouletteInstance();
        }
        ImGui.SameLine();
        if (DrawAemeathButton("FindRouletteExit", "测试查找退出点", new Vector2(132f, 30f)))
        {
            plugin.TestFindRouletteExitPoint();
        }
        ImGui.SameLine();
        if (DrawAemeathButton("MapSupplement", "测试补图逻辑", new Vector2(132f, 30f)))
        {
            plugin.TestMapSupplementLogic();
        }
        ImGui.SameLine();
        if (DrawAemeathButton("PurchaseMaps", "购买三张地图", new Vector2(132f, 30f)))
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
        var width = Math.Min(620f, Math.Max(300f, ImGui.GetContentRegionAvail().X));
        const float height = 34f;
        var topLeft = ImGui.GetCursorScreenPos();
        var cursorAfter = topLeft + new Vector2(0f, height);
        var clicked = ImGui.InvisibleButton("##TreasureMapSelector", new Vector2(width, height));
        if (clicked)
            treasureMapSelectorOpen = !treasureMapSelectorOpen;

        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(topLeft, topLeft + new Vector2(width, height),
            ImGui.GetColorU32(new Vector4(0.05f, 0.15f, 0.30f, treasureMapSelectorOpen ? 0.94f : 0.84f)), 7f);
        drawList.AddRect(topLeft, topLeft + new Vector2(width, height),
            ImGui.GetColorU32(new Vector4(0.29f, 0.89f, 0.88f, treasureMapSelectorOpen ? 0.70f : 0.42f)),
            7f, ImDrawFlags.RoundCornersAll, 1f);
        drawList.AddText(topLeft + new Vector2(12f, 8f), ImGui.GetColorU32(AemeathInk),
            plugin.SelectedTreasureMapName);
        var arrowCenter = topLeft + new Vector2(width - 18f, height * 0.5f);
        drawList.AddTriangleFilled(
            arrowCenter + new Vector2(-5f, -2f),
            arrowCenter + new Vector2(5f, -2f),
            arrowCenter + new Vector2(0f, 4f),
            ImGui.GetColorU32(AemeathCyan));

        if (!treasureMapSelectorOpen || plugin.TreasureMapOptionCount == 0)
            return;

        const float optionHeight = 38f;
        var optionCount = plugin.TreasureMapOptionCount;
        var popupHeight = Math.Min(optionCount, 6) * optionHeight;
        var popupTopLeft = topLeft + new Vector2(0f, height + 4f);
        drawList.AddRectFilled(popupTopLeft, popupTopLeft + new Vector2(width, popupHeight),
            ImGui.GetColorU32(new Vector4(0.035f, 0.07f, 0.18f, 0.98f)), 8f);
        drawList.AddRect(popupTopLeft, popupTopLeft + new Vector2(width, popupHeight),
            ImGui.GetColorU32(new Vector4(0.07f, 0.57f, 0.68f, 0.54f)), 8f,
            ImDrawFlags.RoundCornersAll, 1f);

        for (var index = 0; index < Math.Min(optionCount, 6); index++)
        {
            var rowTopLeft = popupTopLeft + new Vector2(4f, index * optionHeight + 2f);
            var rowSize = new Vector2(width - 8f, optionHeight - 4f);
            ImGui.SetCursorScreenPos(rowTopLeft);
            var rowClicked = ImGui.InvisibleButton($"##TreasureMapOption{index}", rowSize);
            var selected = index == plugin.SelectedTreasureMapIndex;
            var rowHovered = ImGui.IsItemHovered();
            var rowFill = selected
                ? new Vector4(0.76f, 0.25f, 0.53f, 0.72f)
                : rowHovered
                    ? new Vector4(0.10f, 0.35f, 0.43f, 0.78f)
                    : new Vector4(0.08f, 0.14f, 0.28f, 0.52f);
            drawList.AddRectFilled(rowTopLeft, rowTopLeft + rowSize, ImGui.GetColorU32(rowFill), 5f);
            DrawItemIconAt(drawList, plugin.GetTreasureMapOptionItemId(index), rowTopLeft + new Vector2(8f, 3f), 30f);
            drawList.AddText(rowTopLeft + new Vector2(46f, 9f), ImGui.GetColorU32(AemeathInk),
                plugin.GetTreasureMapOptionName(index));
            if (rowClicked)
            {
                plugin.SetSelectedTreasureMap(index);
                treasureMapSelectorOpen = false;
            }
        }
        ImGui.SetCursorScreenPos(popupTopLeft + new Vector2(0f, popupHeight + 4f));
    }

    private void DrawItemIconAt(ImDrawListPtr drawList, uint itemId, Vector2 topLeft, float size)
    {
        var iconId = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Icon ?? 0;
        ISharedImmediateTexture? texture = null;
        if (iconId == 0 || !mapIconCache.TryGetValue(iconId, out texture))
        {
            if (iconId != 0)
            {
                texture = textureProvider.GetFromGameIcon(new GameIconLookup(iconId));
                mapIconCache[iconId] = texture;
            }
        }

        if (texture != null && texture.TryGetWrap(out var wrap, out _))
            drawList.AddImage(wrap.Handle, topLeft, topLeft + new Vector2(size, size));
        else
            drawList.AddRect(topLeft, topLeft + new Vector2(size, size),
                ImGui.GetColorU32(new Vector4(0.07f, 0.57f, 0.68f, 0.30f)), 4f);
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
