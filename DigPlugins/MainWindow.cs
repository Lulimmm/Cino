using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Dalamud.Game.Config;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Lumina.Excel.Sheets;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AutoTreasureHunt;

public sealed class MainWindow
{
    private static readonly HttpClient NowPlayingHttpClient = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const string RememberedCredentialMask = "********";
    private static readonly Vector4 RunningColor = new(0.08f, 0.57f, 0.38f, 1.00f);
    private static readonly Vector4 StoppedColor = new(0.78f, 0.20f, 0.31f, 1.00f);
    // FleetSnowfluff's site palette: lavender glass, rose accents, cyan pixels and cream highlights.
    private static readonly Vector4 AemeathPink = new(0.95f, 0.39f, 0.65f, 1.00f);
    private static readonly Vector4 AemeathCyan = new(0.29f, 0.89f, 0.88f, 1.00f);
    private static readonly Vector4 AemeathGold = new(0.58f, 0.39f, 0.15f, 1.00f);
    private static readonly Vector4 AemeathInk = new(0.94f, 0.96f, 1.00f, 1.00f);
    private static readonly Vector4 AemeathMuted = new(0.63f, 0.70f, 0.86f, 1.00f);
    private static readonly Vector4 AemeathBodyInk = new(0.18f, 0.11f, 0.32f, 1.00f);
    private static readonly Vector4 AemeathBodyMuted = new(0.29f, 0.20f, 0.44f, 1.00f);
    private static readonly Vector4 AemeathTextPink = new(0.48f, 0.12f, 0.34f, 1.00f);
    private static readonly Vector4 AemeathTextCyan = new(0.00f, 0.31f, 0.37f, 1.00f);
    private static readonly Vector4 AemeathTextGold = new(0.43f, 0.27f, 0.08f, 1.00f);
    private static readonly Vector4 SitePixelBorder = new(1.00f, 0.78f, 0.91f, 1.00f);
    private static readonly (float Position, Vector4 Color)[] FloatingSpectrumGradientStops =
    {
        (0.00f, new Vector4(1.00f, 0.67f, 0.91f, 0.98f)),
        (0.12f, new Vector4(0.84f, 0.65f, 1.00f, 0.96f)),
        (0.28f, new Vector4(0.52f, 0.78f, 1.00f, 0.95f)),
        (0.44f, new Vector4(0.48f, 0.96f, 0.89f, 0.94f)),
        (0.60f, new Vector4(1.00f, 0.87f, 0.54f, 0.92f)),
        (0.76f, new Vector4(1.00f, 0.69f, 0.48f, 0.90f)),
        (0.90f, new Vector4(1.00f, 0.57f, 0.76f, 0.90f)),
        (1.00f, new Vector4(0.74f, 0.91f, 1.00f, 0.92f)),
    };
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
    private readonly IFramework framework;
    private readonly IGameConfig gameConfig;
    private readonly ISharedImmediateTexture? fleetPanelBackground;
    private readonly ISharedImmediateTexture? nowPlayingFallbackCoverTexture;
    private ISharedImmediateTexture? nowPlayingCoverTexture;
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
    private bool isOpen;
    private string credentialInput = string.Empty;
    private string credentialStatus = string.Empty;
    private bool credentialValidationInProgress;
    private string navigationBaseIdInput = "2013860";
    private string mapSupplementMaxUnitPriceInput = string.Empty;
    private int mapSupplementMaxUnitPriceObserved;
    private MainPage activePage = MainPage.Automation;
    private bool treasureMapSelectorOpen;
    private int miniPlayerTrackIndex;
    private int nowPlayingTrackIndex = 7;
    private int nowPlayingModeIndex;
    private float nowPlayingVolume = 0.8f;
    private bool nowPlayingVolumeOpen;
    private bool nowPlayingIsPlaying;
    private bool nowPlayingIsLoading;
    private bool nowPlayingDisposed;
    private Vector2 floatingPlayerPosition = new(40f, 120f);
    private bool floatingPlayerPositionInitialized;
    private int nowPlayingRequestId;
    private string nowPlayingStatus = string.Empty;
    private MediaFoundationReader? nowPlayingReader;
    private WaveOutEvent? nowPlayingOutput;
    private SpectrumSampleProvider? nowPlayingSpectrum;
    private readonly float[] floatingSpectrumLevels = new float[64];
    private EventHandler<StoppedEventArgs>? nowPlayingStoppedHandler;
    private CancellationTokenSource? nowPlayingLoadCancellation;
    private bool gameBgmSuppressed;
    private uint savedGameBgmEnabled;
    private static readonly string[] NowPlayingTrackFiles =
    {
        "我与你 - 鸣潮先约电台、auburn.mp3",
        "小小奇迹 (尚未发布(๑•̀ᴗ- )✧✩°｡⋆！) (尚未发布(๑•̀ᴗ- )✧✩°｡⋆！) - 鸣潮先约电台、飞行雪绒、jixwang、TLK天翔.mp3",
        "小小奇迹 - 鸣潮先约电台、jixwang、飞行雪绒.mp3",
        "星炬不熄 - 鸣潮先约电台、飞行雪绒、星炬学院毕业生.mp3",
        "未完成的奇迹 - 鸣潮先约电台、飞行雪绒、jixwang、TLK天翔.mp3",
        "纸飞机 - 鸣潮先约电台、飞行雪绒.mp3",
        "远航星的告别 (Instrumental) - 鸣潮先约电台、jixwang.mp3",
        "那颗星梦见的春日 - 鸣潮先约电台、jixwang、小林未郁.mp3",
        "停泊的舟 - 鸣潮先约电台、jixwang、飞行雪绒.mp3",
        "远航星的告别 - 鸣潮先约电台、jixwang、Tarokiki、Emi Evans.mp3",
        "飞行雪绒的练习时间 - 鸣潮先约电台、飞行雪绒.mp3",
        "碎花 - 鸣潮先约电台、飞行雪绒.mp3",
        "靛青宇宙 - 鸣潮先约电台、飞行雪绒.mp3",
        "Deadline Disco (极限迪斯科) (极限迪斯科) - 鸣潮先约电台、飞行雪绒.mp3",
        "「拉海洛」之心 - 鸣潮先约电台、baitian、Victor Borba、Casey Lee Williams.mp3",
        "月终恒常于瞬息 - 鸣潮先约电台、jixwang、R!NGemie.mp3",
        "再于终末归还 - 鸣潮先约电台、jixwang、JINGYAN、Emi Evans.mp3",
        "自无垠处归航之星 - 鸣潮先约电台、jixwang、R!NGemie.mp3",
        "自无垠处归航之星(Instrumental) - 鸣潮先约电台、jixwang.mp3",
        "开往春日的列车.mp3",
    };
    private static readonly string[] NowPlayingModes = { "列表播放", "列表循环", "单曲循环", "随机循环" };
    private static readonly string[] MiniPlayerTracks =
    {
        "我与你 - 鸣潮先约电台、auburn",
        "小小奇迹 (尚未发布) - 鸣潮先约电台、飞行雪绒、jixwang、TLK天翔",
        "小小奇迹 - 鸣潮先约电台、jixwang、飞行雪绒",
        "星炬不熄 - 鸣潮先约电台、飞行雪绒、星炬学院毕业生",
        "未完成的奇迹 - 鸣潮先约电台、飞行雪绒、jixwang、TLK天翔",
        "纸飞机 - 鸣潮先约电台、飞行雪绒、jixwang、JINGYAN、Emi Evans",
        "自无垠处归航之星 - 鸣潮先约电台、jixwang、R!NGemie",
        "自无垠处归航之星(Instrumental) - 鸣潮先约电台、jixwang",
        "开往春日的列车",
    };
    private static readonly string[] MiniPlayerTrackFiles =
    {
        "我与你 - 鸣潮先约电台、auburn.mp3",
        "小小奇迹 (尚未发布(๑•̀ᴗ- )✧✩°｡⋆！) (尚未发布(๑•̀ᴗ- )✧✩°｡⋆！) - 鸣潮先约电台、飞行雪绒、jixwang、TLK天翔.mp3",
        "小小奇迹 - 鸣潮先约电台、jixwang、飞行雪绒.mp3",
        "星炬不熄 - 鸣潮先约电台、飞行雪绒、星炬学院毕业生.mp3",
        "未完成的奇迹 - 鸣潮先约电台、飞行雪绒、jixwang、TLK天翔.mp3",
        "纸飞机 - 鸣潮先约电台、飞行雪绒、jixwang、JINGYAN、Emi Evans.mp3",
        "自无垠处归航之星 - 鸣潮先约电台、jixwang、R!NGemie.mp3",
        "自无垠处归航之星(Instrumental) - 鸣潮先约电台、jixwang.mp3",
        "开往春日的列车.mp3",
    };
    private readonly IFontAtlas fleetFontAtlas;
    private readonly IFontHandle fleetFontHandle;
    private readonly IFontAtlas floatingFontAtlas;
    private readonly IFontHandle floatingFontHandle;
    private static readonly ushort[] FloatingLatinGlyphRanges =
    {
        0x0020, 0x00FF,
        0x2000, 0x206F,
        0
    };
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
        ITextureProvider textureProvider,
        IFramework framework,
        IGameConfig gameConfig)
    {
        this.plugin = plugin;
        this.pluginInterface = pluginInterface;
        this.dataManager = dataManager;
        this.textureProvider = textureProvider;
        this.framework = framework;
        this.gameConfig = gameConfig;
        var panelBackgroundPath = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            "Resources",
            "FleetSnowfluff",
            "site-background-clean.jpg");
        if (File.Exists(panelBackgroundPath))
            fleetPanelBackground = textureProvider.GetFromFile(panelBackgroundPath);

        var nowPlayingCoverPath = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            "Resources",
            "FleetSnowfluff",
            "now-playing-cover.jpg");
        if (File.Exists(nowPlayingCoverPath))
        {
            nowPlayingFallbackCoverTexture = textureProvider.GetFromFile(nowPlayingCoverPath);
            nowPlayingCoverTexture = nowPlayingFallbackCoverTexture;
        }

        var fontPath = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            "Resources",
            "FleetSnowfluff",
            "UranusCN.ttf");
        var pixelFontPath = Path.Combine(
            pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory,
            "Resources",
            "FleetSnowfluff",
            "Pixelcraft.ttf");
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
        floatingFontAtlas = pluginInterface.UiBuilder.CreateFontAtlas(
            FontAtlasAutoRebuildMode.Async,
            isGlobalScaled: true,
            debugName: "DigPlugins Web Mini Player Font");
        floatingFontHandle = floatingFontAtlas.NewDelegateFontHandle(toolkit => toolkit.OnPreBuild(preBuild =>
        {
            var defaultFont = preBuild.AddDalamudDefaultFont(14f);
            var pixelConfig = new SafeFontConfig
            {
                SizePx = 14f,
                GlyphRanges = FloatingLatinGlyphRanges,
                MergeFont = defaultFont,
                PixelSnapH = true,
                OversampleH = 1,
                OversampleV = 1,
            };
            preBuild.AddFontFromFile(pixelFontPath, in pixelConfig);
            var chineseConfig = new SafeFontConfig
            {
                SizePx = 14f,
                GlyphRanges = FleetChineseGlyphRanges,
                MergeFont = defaultFont,
                PixelSnapH = true,
                OversampleH = 1,
                OversampleV = 1,
            };
            preBuild.AddFontFromFile(fontPath, in chineseConfig);
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
            if (nowPlayingIsPlaying || nowPlayingIsLoading)
            {
                IDisposable? floatingFontScope = null;
                if (floatingFontHandle.Available)
                    floatingFontScope = floatingFontHandle.Push();
                else if (fleetFontHandle.Available)
                    floatingFontScope = fleetFontHandle.Push();

                PushFloatingPlayerStyle();
                DrawFloatingNowPlaying();
                PopFloatingPlayerStyle();
                floatingFontScope?.Dispose();
            }
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

        DrawWebsiteBackground();
        DrawAemeathWindowChrome();
        ImGui.SetCursorPos(new Vector2(16, 38));
        DrawNowPlayingHero();
        ImGui.Spacing();

        var bodyHeight = Math.Max(180f, ImGui.GetContentRegionAvail().Y - 10f);
        var bodyTopLeft = ImGui.GetCursorScreenPos();
        var bodyRight = bodyTopLeft.X + ImGui.GetContentRegionAvail().X;
        var bodyBottom = bodyTopLeft.Y + bodyHeight;
        var bodyDrawList = ImGui.GetWindowDrawList();
        const float sidebarWidth = 218f;
        DrawTabBarBackground(
            bodyTopLeft,
            new Vector2(bodyTopLeft.X + sidebarWidth, bodyBottom));
        bodyDrawList.AddRectFilled(
            bodyTopLeft,
            new Vector2(bodyTopLeft.X + sidebarWidth, bodyBottom),
            ImGui.GetColorU32(new Vector4(0.075f, 0.035f, 0.16f, 0.035f)),
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
            ImGui.GetColorU32(new Vector4(0.075f, 0.035f, 0.16f, 0.03f)),
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
        nowPlayingDisposed = true;
        nowPlayingRequestId++;
        nowPlayingLoadCancellation?.Cancel();
        nowPlayingLoadCancellation = null;
        DisposeNowPlayingPlayback();

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
        floatingFontHandle.Dispose();
        floatingFontAtlas.Dispose();
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
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 10f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 7));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, AemeathNavy);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.035f, 0.06f, 0.16f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.05f, 0.08f, 0.22f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgCollapsed, new Vector4(0.035f, 0.06f, 0.16f, 0.94f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, AemeathPanel);
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.91f, 0.76f, 0.96f, 0.72f));
        ImGui.PushStyleColor(ImGuiCol.Text, AemeathBodyInk);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, AemeathBodyMuted);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.34f, 0.25f, 0.54f, 0.84f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.55f, 0.37f, 0.68f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.79f, 0.29f, 0.58f, 0.94f));
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

    private static void PushFloatingPlayerStyle()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 5f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.10f, 0.045f, 0.19f, 0.98f));
    }

    private static void PopFloatingPlayerStyle()
    {
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(4);
    }

    private static Vector4 GetFloatingSpectrumColor(float position)
    {
        position = Math.Clamp(position, 0f, 1f);
        for (var i = 1; i < FloatingSpectrumGradientStops.Length; i++)
        {
            if (position <= FloatingSpectrumGradientStops[i].Position)
            {
                var span = FloatingSpectrumGradientStops[i].Position - FloatingSpectrumGradientStops[i - 1].Position;
                var amount = span <= 0f
                    ? 0f
                    : (position - FloatingSpectrumGradientStops[i - 1].Position) / span;
                return Vector4.Lerp(FloatingSpectrumGradientStops[i - 1].Color,
                    FloatingSpectrumGradientStops[i].Color, amount);
            }
        }
        return FloatingSpectrumGradientStops[^1].Color;
    }

    private void DrawFloatingNowPlaying()
    {
        const float width = 300f;
        const float height = 136f;
        if (!floatingPlayerPositionInitialized)
        {
            var displaySize = ImGui.GetIO().DisplaySize;
            floatingPlayerPosition = new Vector2(
                Math.Max(12f, displaySize.X - width - 28f),
                Math.Max(12f, displaySize.Y - height - 90f));
            floatingPlayerPositionInitialized = true;
        }

        ImGui.SetNextWindowPos(floatingPlayerPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNavFocus;
        if (!ImGui.Begin("##AutoTreasureHuntFloatingPlayer", flags))
        {
            ImGui.End();
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var windowMax = windowPosition + windowSize;
        drawList.AddRectFilled(windowPosition + new Vector2(8f, 9f), windowMax + new Vector2(8f, 9f),
            ImGui.GetColorU32(new Vector4(0.12f, 0.08f, 0.27f, 0.42f)), 14f,
            ImDrawFlags.RoundCornersAll);
        drawList.AddRectFilled(windowPosition, windowMax,
            ImGui.GetColorU32(new Vector4(0.23f, 0.16f, 0.42f, 0.84f)), 14f,
            ImDrawFlags.RoundCornersAll);
        drawList.AddRect(windowPosition + new Vector2(1f, 1f), windowMax - new Vector2(1f, 1f),
            ImGui.GetColorU32(new Vector4(1f, 0.90f, 0.98f, 0.70f)), 14f,
            ImDrawFlags.RoundCornersAll, 1.25f);
        drawList.AddCircleFilled(windowPosition + new Vector2(width * 0.18f, height * 0.17f), 74f,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.055f)), 32);
        drawList.AddCircleFilled(windowPosition + new Vector2(width * 0.83f, height * 0.20f), 68f,
            ImGui.GetColorU32(new Vector4(0.38f, 0.78f, 1f, 0.055f)), 32);
        drawList.AddRectFilled(windowPosition + new Vector2(10f, height - 18f),
            new Vector2(windowMax.X - 10f, height == 0f ? windowMax.Y : windowMax.Y - 8f),
            ImGui.GetColorU32(new Vector4(0.09f, 0.07f, 0.20f, 0.20f)), 8f);

        // The top strip is intentionally the only drag target so controls remain easy to click.
        ImGui.SetCursorScreenPos(windowPosition + new Vector2(8f, 4f));
        ImGui.InvisibleButton("##FloatingPlayerDrag", new Vector2(width - 54f, 18f));
        if (ImGui.IsItemActive() && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            var delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left);
            if (delta != Vector2.Zero)
            {
                floatingPlayerPosition += delta;
                ImGui.SetWindowPos(floatingPlayerPosition);
                ImGui.ResetMouseDragDelta(ImGuiMouseButton.Left);
            }
        }

        // A small reopen glyph keeps the floating player useful without adding a native title bar.
        ImGui.SetCursorScreenPos(windowMax - new Vector2(40f, height - 3f));
        if (ImGui.InvisibleButton("##FloatingOpenMain", new Vector2(28f, 20f)))
            isOpen = true;
        var openCenter = windowPosition + new Vector2(width - 25f, 13f);
        var openColor = ImGui.GetColorU32(new Vector4(0.35f, 0.95f, 1f, 0.92f));
        drawList.AddRect(openCenter - new Vector2(6f, 6f), openCenter + new Vector2(6f, 6f),
            openColor, 2f, ImDrawFlags.RoundCornersAll, 1.8f);
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("打开主界面");
            ImGui.EndTooltip();
        }

        // Match the website's inline spectrum canvas: compact bars, a glass shell and a bright baseline.
        var spectrumMin = windowPosition + new Vector2(10f, 10f);
        var spectrumMax = new Vector2(windowMax.X - 10f, windowPosition.Y + 66f);
        drawList.AddRectFilled(spectrumMin, spectrumMax,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.075f)), 10f);
        drawList.AddRect(spectrumMin, spectrumMax,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.20f)), 10f,
            ImDrawFlags.RoundCornersAll, 1f);
        Array.Clear(floatingSpectrumLevels);
        nowPlayingSpectrum?.CopyLevels(floatingSpectrumLevels);
        var spectrumWidth = spectrumMax.X - spectrumMin.X;
        const float barWidth = 4f;
        const float barGap = 2f;
        var barCount = Math.Max(1, (int)MathF.Floor(spectrumWidth / (barWidth + barGap)));
        var spectrumHeight = spectrumMax.Y - spectrumMin.Y;
        var spectrumCenter = (barCount - 1) * 0.5f;
        // The extreme high bins are usually empty in the compressed tracks used
        // by the player. Use the responsive audible half of the spectrum for the
        // mirrored edges so the first and last bars do not become static pixels.
        var displayBandLimit = Math.Max(2, floatingSpectrumLevels.Length / 2);
        for (var i = 0; i < barCount; i++)
        {
            // Put the low-frequency start of the analyser in the middle and
            // expand toward both edges, so both sides share the same responsive
            // high-frequency tail instead of leaving the left side as a separate
            // static-looking block.
            var distanceFromCenter = MathF.Abs(i - spectrumCenter) /
                Math.Max(1f, spectrumCenter);
            var fftIndex = (int)MathF.Round(distanceFromCenter * (displayBandLimit - 1));
            var fftLevel = floatingSpectrumLevels[Math.Clamp(fftIndex, 0, floatingSpectrumLevels.Length - 1)];
            var barHeight = nowPlayingIsPlaying
                ? Math.Max(6f, fftLevel * spectrumHeight * 0.80f)
                : Math.Max(4f, spectrumHeight *
                    (0.12f + (MathF.Sin(distanceFromCenter * MathF.PI * 2.6f) * 0.35f +
                        MathF.Cos(distanceFromCenter * MathF.PI * 7.2f) * 0.12f) * 0.18f +
                        distanceFromCenter * 0.04f));
            var x = spectrumMin.X + i * (barWidth + barGap);
            var barBottom = spectrumMax.Y - 3f;
            var barTop = barBottom - barHeight;
            drawList.AddRectFilled(new Vector2(x - 1f, barTop),
                new Vector2(x + barWidth + 1f, barBottom),
                ImGui.GetColorU32(new Vector4(1f, 0.70f, 0.91f, 0.10f)), 2f);
            const int gradientSegments = 7;
            for (var segment = 0; segment < gradientSegments; segment++)
            {
                var segmentTop = barTop + barHeight * segment / gradientSegments;
                var segmentBottom = barTop + barHeight * (segment + 1) / gradientSegments;
                var gradientPosition = (segmentTop - spectrumMin.Y) / (spectrumMax.Y - spectrumMin.Y);
                drawList.AddRectFilled(new Vector2(x, segmentTop),
                    new Vector2(x + barWidth, segmentBottom),
                    ImGui.GetColorU32(GetFloatingSpectrumColor(gradientPosition)), 1f);
            }
        }
        drawList.AddLine(new Vector2(spectrumMin.X + 1f, spectrumMax.Y - 1f),
            new Vector2(spectrumMax.X - 1f, spectrumMax.Y - 1f),
            ImGui.GetColorU32(new Vector4(1f, 0.94f, 1f, 0.58f)), 1f);

        var title = nowPlayingIsLoading && !string.IsNullOrWhiteSpace(nowPlayingStatus)
            ? nowPlayingStatus
            : Path.GetFileNameWithoutExtension(NowPlayingTrackFiles[nowPlayingTrackIndex]);
        if (title.Length > 39)
            title = title[..39] + "...";
        drawList.PushClipRect(windowPosition + new Vector2(12f, 68f),
            new Vector2(windowMax.X - 12f, windowPosition.Y + 87f), true);
        drawList.AddText(windowPosition + new Vector2(13f, 71f),
            ImGui.GetColorU32(new Vector4(0.10f, 0.07f, 0.20f, 0.55f)), title);
        drawList.AddText(windowPosition + new Vector2(12f, 70f),
            ImGui.GetColorU32(new Vector4(1f, 0.95f, 1f, 0.96f)), title);
        drawList.PopClipRect();

        var controlPanelMin = windowPosition + new Vector2(10f, 88f);
        var controlPanelMax = new Vector2(windowMax.X - 10f, windowMax.Y - 5f);
        drawList.AddRectFilled(controlPanelMin, controlPanelMax,
            ImGui.GetColorU32(new Vector4(0.43f, 0.35f, 0.66f, 0.64f)), 10f);
        drawList.AddRectFilled(controlPanelMin + new Vector2(2f, 2f),
            new Vector2(controlPanelMax.X - 2f, controlPanelMin.Y + 19f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.10f)), 8f,
            ImDrawFlags.RoundCornersTop);
        drawList.AddRect(controlPanelMin, controlPanelMax,
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.70f)), 10f,
            ImDrawFlags.RoundCornersAll, 1f);
        var buttonSize = new Vector2(42f, 38f);
        var buttonGap = 7f;
        var buttonX = windowPosition.X + (width - buttonSize.X * 4f - buttonGap * 3f) * 0.5f;
        var buttonY = controlPanelMin.Y + 3f;
        DrawFloatingTransportButton(new Vector2(buttonX, buttonY), buttonSize, "previous");
        DrawFloatingTransportButton(new Vector2(buttonX + buttonSize.X + buttonGap, buttonY), buttonSize,
            nowPlayingIsPlaying || nowPlayingIsLoading ? "pause" : "play");
        DrawFloatingTransportButton(new Vector2(buttonX + (buttonSize.X + buttonGap) * 2f, buttonY), buttonSize, "next");
        DrawFloatingPlaylistButton(new Vector2(buttonX + (buttonSize.X + buttonGap) * 3f, buttonY), buttonSize);

        ImGui.End();
    }

    private void DrawFloatingTransportButton(Vector2 position, Vector2 size, string icon)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton($"##FloatingTransport{icon}{position.X}", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
            drawList.AddRectFilled(position, position + size,
                ImGui.GetColorU32(new Vector4(1f, 0.74f, 0.92f, 0.18f)), 8f);

        var iconColor = ImGui.GetColorU32(new Vector4(0.95f, 0.92f, 1f, 1f));
        var center = position + size * 0.5f;
        if (icon is "previous" or "next")
        {
            var left = icon == "previous";
            var tipX = center.X + (left ? -6f : 6f);
            var baseX = center.X + (left ? 6f : -6f);
            drawList.AddTriangleFilled(new Vector2(tipX, center.Y - 9f),
                new Vector2(tipX, center.Y + 9f), new Vector2(baseX, center.Y), iconColor);
            if (clicked)
                SelectNowPlayingTrack(nowPlayingTrackIndex + (left ? NowPlayingTrackFiles.Length - 1 : 1));
        }
        else if (icon == "pause")
        {
            drawList.AddRectFilled(center + new Vector2(-10f, -10f), center + new Vector2(-4f, 10f), iconColor);
            drawList.AddRectFilled(center + new Vector2(4f, -10f), center + new Vector2(10f, 10f), iconColor);
            if (clicked)
            {
                if (nowPlayingIsLoading)
                {
                    nowPlayingRequestId++;
                    nowPlayingLoadCancellation?.Cancel();
                    nowPlayingIsLoading = false;
                    nowPlayingStatus = string.Empty;
                }
                else
                {
                    nowPlayingIsPlaying = false;
                    try { nowPlayingOutput?.Pause(); }
                    catch (Exception ex) { nowPlayingStatus = "Pause failed: " + ex.Message; }
                    finally { RestoreGameBgmVolume(); }
                }
            }
        }
        else
        {
            drawList.AddTriangleFilled(center + new Vector2(-7f, -11f),
                center + new Vector2(-7f, 11f), center + new Vector2(11f, 0f), iconColor);
            if (clicked)
            {
                if (nowPlayingOutput is not null && nowPlayingOutput.PlaybackState == PlaybackState.Paused)
                {
                    try
                    {
                        SuppressGameBgm();
                        nowPlayingOutput.Play();
                        nowPlayingIsPlaying = true;
                    }
                    catch (Exception ex)
                    {
                        DisposeNowPlayingPlayback();
                        nowPlayingIsPlaying = false;
                        nowPlayingStatus = "Play failed: " + ex.Message;
                    }
                }
                else if (nowPlayingOutput is null && !nowPlayingIsLoading)
                {
                    StartNowPlayingTrack();
                }
            }
        }
    }

    private void DrawFloatingPlaylistButton(Vector2 position, Vector2 size)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton("##FloatingPlaylist", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        if (hovered)
            drawList.AddRectFilled(position, position + size,
                ImGui.GetColorU32(new Vector4(1f, 0.61f, 0.85f, 0.10f)), 5f);
        var ink = ImGui.GetColorU32(new Vector4(1f, 0.96f, 1f, 1f));
        for (var i = 0; i < 3; i++)
        {
            var y = position.Y + 10f + i * 7f;
            drawList.AddRectFilled(new Vector2(position.X + 9f, y),
                new Vector2(position.X + size.X - 14f, y + 2f), ink);
            drawList.AddCircleFilled(new Vector2(position.X + size.X - 8f, y + 1f), 2f, ink, 8);
        }
        if (clicked)
            ImGui.OpenPopup("FloatingPlayerPlaylist");
        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("播放列表");
            ImGui.EndTooltip();
        }

        if (!ImGui.BeginPopup("FloatingPlayerPlaylist"))
            return;
        for (var i = 0; i < NowPlayingTrackFiles.Length; i++)
        {
            var label = Path.GetFileNameWithoutExtension(NowPlayingTrackFiles[i]);
            if (label.Length > 46)
                label = label[..46] + "...";
            if (ImGui.Selectable(label, nowPlayingTrackIndex == i))
            {
                SelectNowPlayingTrack(i);
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.EndPopup();
    }

    private void DrawAemeathWindowChrome()
    {
        var drawList = ImGui.GetWindowDrawList();
        var position = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();

        // Keep the title over the shared background instead of giving it a separate dark slab.
        drawList.AddRect(position + new Vector2(1f, 1f), position + size - new Vector2(1f, 1f),
            ImGui.GetColorU32(new Vector4(0.52f, 0.35f, 0.76f, 0.36f)),
            14f, ImDrawFlags.RoundCornersAll, 1f);

        ImGui.SetCursorScreenPos(position + new Vector2(18f, 13f));
        ImGui.TextColored(AemeathBodyInk, "海豹助手");

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

    private void DrawWebsiteBackground()
    {
        var sharedTexture = fleetPanelBackground;
        if (sharedTexture is null || !sharedTexture.TryGetWrap(out var texture, out _))
            return;

        var drawList = ImGui.GetWindowDrawList();
        var windowPosition = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var min = windowPosition + new Vector2(1f, 1f);
        var max = windowPosition + windowSize - new Vector2(1f, 1f);
        if (max.X <= min.X || max.Y <= min.Y)
            return;

        const float sourceAspect = 16f / 9f;
        var targetAspect = (max.X - min.X) / (max.Y - min.Y);
        var uvMin = Vector2.Zero;
        var uvMax = Vector2.One;
        if (targetAspect < sourceAspect)
        {
            var visibleWidth = targetAspect / sourceAspect;
            uvMin.X = (1f - visibleWidth) * 0.5f;
            uvMax.X = uvMin.X + visibleWidth;
        }
        else if (targetAspect > sourceAspect)
        {
            var visibleHeight = sourceAspect / targetAspect;
            uvMin.Y = (1f - visibleHeight) * 0.5f;
            uvMax.Y = uvMin.Y + visibleHeight;
        }

        const float cornerRadius = 14f;
        drawList.PushClipRect(min, max, false);
        drawList.AddImageRounded(texture.Handle, min, max, uvMin, uvMax,
            ImGui.GetColorU32(Vector4.One), cornerRadius, ImDrawFlags.RoundCornersAll);
        drawList.AddRectFilled(min, max,
            ImGui.GetColorU32(new Vector4(0.12f, 0.035f, 0.22f, 0.18f)),
            cornerRadius, ImDrawFlags.RoundCornersAll);
        drawList.PopClipRect();
    }

    private void DrawTabBarBackground(Vector2 sidebarMin, Vector2 sidebarMax)
    {
        var drawList = ImGui.GetWindowDrawList();
        var backdropMin = sidebarMin + new Vector2(1f, 0f);
        var backdropMax = sidebarMax - new Vector2(1f, 1f);
        if (backdropMax.X <= backdropMin.X || backdropMax.Y <= backdropMin.Y)
            return;

        drawList.PushClipRect(backdropMin, backdropMax, false);
        DrawDataStreams(drawList, backdropMin, backdropMax, 1f);
        drawList.PopClipRect();
    }

    private static void DrawDataStreams(
        ImDrawListPtr drawList,
        Vector2 min,
        Vector2 max,
        float strength)
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


    private void DrawNowPlayingHero()
    {
        const float heroHeight = 216f;
        const float coverSize = 128f;
        const float cassetteWidth = 205f;
        if (!ImGui.BeginChild("NowPlayingHero", new Vector2(-1f, heroHeight), false,
                ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var windowSize = ImGui.GetWindowSize();
        var headerPosition = origin + new Vector2(2f, 1f);
        drawList.AddText(headerPosition + Vector2.One, ImGui.GetColorU32(new Vector4(0.17f, 0.12f, 0.30f, 0.65f)),
            "NOW PLAYING");
        drawList.AddText(headerPosition, ImGui.GetColorU32(new Vector4(1f, 0.91f, 0.99f, 1f)),
            "NOW PLAYING");

        var visualTop = origin + new Vector2(2f, 24f);
        var coverMax = visualTop + new Vector2(coverSize, coverSize);
        drawList.AddRectFilled(visualTop + new Vector2(3f, 3f), coverMax + new Vector2(3f, 3f),
            ImGui.GetColorU32(new Vector4(0.20f, 0.13f, 0.33f, 0.76f)));
        if (nowPlayingCoverTexture?.TryGetWrap(out var cover, out _) == true)
            drawList.AddImage(cover.Handle, visualTop, coverMax);
        else
            drawList.AddRectFilled(visualTop, coverMax,
                ImGui.GetColorU32(new Vector4(0.46f, 0.34f, 0.63f, 0.90f)));
        drawList.AddRect(visualTop, coverMax,
            ImGui.GetColorU32(new Vector4(0.83f, 0.72f, 0.95f, 1f)), 0f, ImDrawFlags.None, 2f);

        var cassetteTop = visualTop + new Vector2(154f, 0f);
        DrawPixelCassette(drawList, cassetteTop, new Vector2(cassetteWidth, coverSize),
            nowPlayingIsPlaying ? (float)ImGui.GetTime() : 0f);

        var title = Path.GetFileNameWithoutExtension(NowPlayingTrackFiles[nowPlayingTrackIndex]);
        var titleSplit = title.IndexOf(" - ", StringComparison.Ordinal);
        var titlePosition = origin + new Vector2(2f, 158f);
        drawList.PushClipRect(titlePosition, new Vector2(origin.X + 368f, origin.Y + heroHeight), true);
        var titleSong = titleSplit >= 0 ? title[..titleSplit] : title;
        var titleArtists = titleSplit >= 0 ? title[(titleSplit + 3)..] : string.Empty;
        var titleColor = ImGui.GetColorU32(new Vector4(1f, 0.91f, 0.99f, 1f));
        var titleShadow = ImGui.GetColorU32(new Vector4(0.17f, 0.12f, 0.30f, 0.75f));
        drawList.AddText(titlePosition + Vector2.One, titleShadow, titleSong);
        drawList.AddText(titlePosition, titleColor, titleSong);
        if (!string.IsNullOrEmpty(titleArtists))
        {
            drawList.AddText(titlePosition + new Vector2(0f, 18f) + Vector2.One, titleShadow, titleArtists);
            drawList.AddText(titlePosition + new Vector2(0f, 18f), titleColor, titleArtists);
        }
        drawList.PopClipRect();

        var leftColumnWidth = Math.Min(370f, windowSize.X * 0.43f);
        DrawNowPlayingProgress(drawList, origin, leftColumnWidth, 194f);
        DrawMusicControlsPanel(drawList,
            origin + new Vector2(leftColumnWidth + 12f, 0f),
            new Vector2(Math.Max(320f, windowSize.X - leftColumnWidth - 14f), heroHeight));
        ImGui.EndChild();
    }

    private void DrawNowPlayingProgress(ImDrawListPtr drawList, Vector2 origin, float width, float y)
    {
        var elapsed = 0d;
        var duration = 0d;
        try
        {
            if (nowPlayingReader is not null)
            {
                elapsed = nowPlayingReader.CurrentTime.TotalSeconds;
                duration = nowPlayingReader.TotalTime.TotalSeconds;
            }
        }
        catch (ObjectDisposedException)
        {
            elapsed = 0d;
            duration = 0d;
        }

        var leftTime = origin + new Vector2(2f, y);
        var rightTime = origin + new Vector2(width - 38f, y);
        var sliderPosition = origin + new Vector2(42f, y + 4f);
        var sliderSize = new Vector2(Math.Max(50f, width - 84f), 10f);
        ImGui.SetCursorScreenPos(sliderPosition - new Vector2(0f, 3f));
        ImGui.InvisibleButton("##NowPlayingProgress", sliderSize + new Vector2(0f, 6f));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (duration > 0 && (active || ImGui.IsItemClicked()))
        {
            var progress = Math.Clamp((ImGui.GetIO().MousePos.X - sliderPosition.X) / sliderSize.X, 0f, 1f);
            elapsed = duration * progress;
            try
            {
                if (nowPlayingReader is not null)
                    nowPlayingReader.CurrentTime = TimeSpan.FromSeconds(elapsed);
            }
            catch (ObjectDisposedException)
            {
                elapsed = 0d;
                duration = 0d;
            }
        }

        var trackMax = sliderPosition + sliderSize;
        drawList.AddRectFilled(sliderPosition, trackMax,
            ImGui.GetColorU32(new Vector4(0.31f, 0.24f, 0.51f, 0.62f)));
        drawList.AddRect(sliderPosition, trackMax,
            ImGui.GetColorU32(new Vector4(1f, 0.78f, 0.91f, 0.86f)), 0f, ImDrawFlags.None, 1.5f);
        if (duration > 0)
        {
            var filled = new Vector2(sliderSize.X * (float)Math.Clamp(elapsed / duration, 0d, 1d), sliderSize.Y);
            drawList.AddRectFilled(sliderPosition, sliderPosition + filled,
                ImGui.GetColorU32(new Vector4(1f, 0.61f, 0.85f, 0.96f)));
            var thumbX = sliderPosition.X + sliderSize.X * (float)Math.Clamp(elapsed / duration, 0d, 1d);
            drawList.AddRectFilled(new Vector2(thumbX - 2f, sliderPosition.Y - 3f),
                new Vector2(thumbX + 2f, sliderPosition.Y + sliderSize.Y + 3f),
                ImGui.GetColorU32(new Vector4(1f, 0.96f, 1f, 1f)));
        }

        drawList.AddText(leftTime, ImGui.GetColorU32(new Vector4(1f, 0.91f, 0.99f, 1f)), FormatPlayerTime(elapsed));
        drawList.AddText(rightTime, ImGui.GetColorU32(new Vector4(1f, 0.91f, 0.99f, 1f)), FormatPlayerTime(duration));
        if (hovered && !string.IsNullOrEmpty(nowPlayingStatus))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(nowPlayingStatus);
            ImGui.EndTooltip();
        }
    }

    private static string FormatPlayerTime(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            seconds = 0;

        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void DrawMusicControlsPanel(ImDrawListPtr drawList, Vector2 topLeft, Vector2 size)
    {
        var bottomRight = topLeft + size;
        drawList.AddRectFilled(topLeft, bottomRight,
            ImGui.GetColorU32(new Vector4(0.47f, 0.37f, 0.73f, 0.35f)), 14f);
        drawList.AddRect(topLeft, bottomRight,
            ImGui.GetColorU32(new Vector4(1f, 0.61f, 0.85f, 0.60f)), 14f,
            ImDrawFlags.RoundCornersAll, 2f);
        drawList.AddLine(topLeft + new Vector2(15f, 3f), new Vector2(bottomRight.X - 15f, topLeft.Y + 3f),
            ImGui.GetColorU32(new Vector4(1f, 0.95f, 1f, 0.20f)), 1f);
        drawList.AddLine(new Vector2(topLeft.X + 15f, bottomRight.Y - 3f), bottomRight - new Vector2(15f, 3f),
            ImGui.GetColorU32(new Vector4(0.24f, 0.16f, 0.40f, 0.24f)), 1f);

        var rowWidth = Math.Min(360f, size.X - 24f);
        var rowGap = Math.Clamp(size.X * 0.022f, 9f, 17f);
        var cellWidth = (rowWidth - rowGap * 2f) / 3f;
        var rowLeft = topLeft.X + (size.X - rowWidth) * 0.5f;
        var buttonY = topLeft.Y + 12f;
        var transportSize = new Vector2(cellWidth, 76f);
        DrawPlayerTransportButton("PlayerPrevious", new Vector2(rowLeft, buttonY), transportSize, "previous");
        DrawPlayerTransportButton("PlayerToggle", new Vector2(rowLeft + cellWidth + rowGap, buttonY), transportSize,
            nowPlayingIsPlaying || nowPlayingIsLoading ? "pause" : "play");
        DrawPlayerTransportButton("PlayerNext", new Vector2(rowLeft + (cellWidth + rowGap) * 2f, buttonY), transportSize, "next");

        var volumeSize = new Vector2(size.X - 24f, 34f);
        var volumePosition = new Vector2(topLeft.X + 12f, topLeft.Y + 100f);
        ImGui.SetCursorScreenPos(volumePosition);
        if (ImGui.InvisibleButton("##PlayerVolume", volumeSize))
            nowPlayingVolumeOpen = !nowPlayingVolumeOpen;
        var volumeLabel = $"音量 {MathF.Round(nowPlayingVolume * 100f)}%";
        var volumeTextSize = ImGui.CalcTextSize(volumeLabel);
        var volumeShadow = ImGui.GetColorU32(new Vector4(0.36f, 0.23f, 0.56f, 0.95f));
        drawList.AddText(volumePosition + new Vector2((volumeSize.X - volumeTextSize.X) * 0.5f + 2f, 7f),
            volumeShadow, volumeLabel);
        drawList.AddText(volumePosition + new Vector2((volumeSize.X - volumeTextSize.X) * 0.5f, 5f),
            ImGui.GetColorU32(new Vector4(1f, 0.96f, 1f, 1f)), volumeLabel);
        var modeY = topLeft.Y + 164f;
        if (nowPlayingVolumeOpen)
        {
            var sliderPosition = new Vector2(topLeft.X + 24f, topLeft.Y + 138f);
            var sliderSize = new Vector2(size.X - 48f, 12f);
            ImGui.SetCursorScreenPos(sliderPosition);
            ImGui.InvisibleButton("##PlayerVolumeSlider", sliderSize);
            if (ImGui.IsItemActive() || ImGui.IsItemClicked())
                nowPlayingVolume = Math.Clamp((ImGui.GetIO().MousePos.X - sliderPosition.X) / sliderSize.X, 0f, 1f);
            if (nowPlayingOutput is not null)
                nowPlayingOutput.Volume = nowPlayingVolume;
            drawList.AddRectFilled(sliderPosition, sliderPosition + sliderSize,
                ImGui.GetColorU32(new Vector4(0.31f, 0.24f, 0.51f, 0.60f)));
            drawList.AddRect(sliderPosition, sliderPosition + sliderSize,
                ImGui.GetColorU32(new Vector4(1f, 0.61f, 0.85f, 0.62f)), 0f, ImDrawFlags.None, 2f);
            var thumbX = sliderPosition.X + (sliderSize.X - 16f) * nowPlayingVolume;
            drawList.AddRectFilled(new Vector2(thumbX, sliderPosition.Y - 2f),
                new Vector2(thumbX + 16f, sliderPosition.Y + sliderSize.Y + 2f),
                ImGui.GetColorU32(new Vector4(1f, 0.96f, 1f, 1f)));
            modeY = topLeft.Y + 178f;
        }

        var gap = Math.Clamp(size.X * 0.01f, 7f, 8f);
        var buttonWidths = NowPlayingModes.Select(mode => ImGui.CalcTextSize(mode).X + 28f).ToArray();
        var modesWidth = buttonWidths.Sum() + gap * (buttonWidths.Length - 1);
        var modeX = topLeft.X + (size.X - modesWidth) * 0.5f;
        for (var i = 0; i < NowPlayingModes.Length; i++)
        {
            if (DrawPlayerModeButton($"PlayerMode{i}", NowPlayingModes[i],
                    new Vector2(modeX, modeY),
                    new Vector2(buttonWidths[i], 32f), nowPlayingModeIndex == i))
                nowPlayingModeIndex = i;
            modeX += buttonWidths[i] + gap;
        }
    }

    private void DrawPlayerTransportButton(string id, Vector2 position, Vector2 size, string icon)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton($"##{id}", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        if (hovered && !string.IsNullOrEmpty(nowPlayingStatus))
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(nowPlayingStatus);
            ImGui.EndTooltip();
        }
        var ink = ImGui.GetColorU32(new Vector4(1f, 0.96f, 1f, 1f));
        var shadow = ImGui.GetColorU32(new Vector4(0.36f, 0.23f, 0.56f, 1f));
        if (hovered)
            drawList.AddRectFilled(position, position + size,
                ImGui.GetColorU32(new Vector4(1f, 0.61f, 0.85f, 0.08f)));

        var center = position + size * 0.5f;

        if (icon is "previous" or "next")
        {
            var left = icon == "previous";
            var tipX = center.X + (left ? -7f : 7f);
            var baseX = center.X + (left ? 7f : -7f);
            var upperY = center.Y - 9f;
            var lowerY = center.Y + 9f;
            var direction = left ? -1f : 1f;
            drawList.AddTriangleFilled(new Vector2(tipX + direction, upperY + 1f),
                new Vector2(tipX + direction, lowerY + 1f),
                new Vector2(baseX + direction, center.Y + 1f), shadow);
            drawList.AddTriangleFilled(new Vector2(tipX, upperY), new Vector2(tipX, lowerY),
                new Vector2(baseX, center.Y), ink);
            if (clicked)
            {
                var delta = left ? NowPlayingTrackFiles.Length - 1 : 1;
                SelectNowPlayingTrack(nowPlayingTrackIndex + delta);
            }
        }
        else if (icon == "pause")
        {
            drawList.AddRectFilled(center + new Vector2(-14f, -14f), center + new Vector2(-4f, 14f), shadow);
            drawList.AddRectFilled(center + new Vector2(2f, -14f), center + new Vector2(12f, 14f), shadow);
            drawList.AddRectFilled(center + new Vector2(-16f, -14f), center + new Vector2(-6f, 14f), ink);
            drawList.AddRectFilled(center + new Vector2(0f, -14f), center + new Vector2(10f, 14f), ink);
            if (clicked)
            {
                if (nowPlayingIsLoading)
                {
                    nowPlayingRequestId++;
                    nowPlayingLoadCancellation?.Cancel();
                    nowPlayingIsLoading = false;
                    nowPlayingStatus = string.Empty;
                }
                else
                {
                    nowPlayingIsPlaying = false;
                    try
                    {
                        nowPlayingOutput?.Pause();
                    }
                    catch (Exception ex)
                    {
                        nowPlayingStatus = $"暂停失败：{ex.Message}";
                    }
                    finally
                    {
                        RestoreGameBgmVolume();
                    }
                }
            }
        }
        else
        {
            drawList.AddTriangleFilled(center + new Vector2(-12f, -18f) + new Vector2(2f, 2f),
                center + new Vector2(-12f, 18f) + new Vector2(2f, 2f),
                center + new Vector2(16f, 0f) + new Vector2(2f, 2f), shadow);
            drawList.AddTriangleFilled(center + new Vector2(-14f, -18f), center + new Vector2(-14f, 18f),
                center + new Vector2(14f, 0f), ink);
            if (clicked)
            {
                if (nowPlayingOutput is not null && nowPlayingOutput.PlaybackState == PlaybackState.Paused)
                {
                    try
                    {
                        SuppressGameBgm();
                        nowPlayingOutput.Play();
                        nowPlayingIsPlaying = true;
                    }
                    catch (Exception ex)
                    {
                        DisposeNowPlayingPlayback();
                        nowPlayingIsPlaying = false;
                        nowPlayingStatus = $"播放失败：{ex.Message}";
                    }
                }
                else if (nowPlayingOutput is null && !nowPlayingIsLoading)
                {
                    StartNowPlayingTrack();
                }
            }
        }
    }

    private static bool DrawPlayerModeButton(string id, string label, Vector2 position, Vector2 size, bool selected)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton($"##{id}", size);
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var fill = selected
            ? new Vector4(1f, 0.61f, 0.85f, 0.25f)
            : hovered ? new Vector4(1f, 1f, 1f, 0.14f) : new Vector4(1f, 1f, 1f, 0.12f);
        drawList.AddRectFilled(position + new Vector2(3f, 3f), position + size + new Vector2(3f, 3f),
            ImGui.GetColorU32(new Vector4(0.24f, 0.15f, 0.40f, 0.41f)));
        drawList.AddRectFilled(position, position + size, ImGui.GetColorU32(fill));
        drawList.AddRect(position, position + size,
            ImGui.GetColorU32(selected
                ? new Vector4(1f, 0.57f, 0.84f, 1f)
                : new Vector4(0.49f, 0.36f, 0.71f, 1f)),
            0f, ImDrawFlags.None, 2f);
        var textSize = ImGui.CalcTextSize(label);
        var textColor = ImGui.GetColorU32(new Vector4(0.36f, 0.23f, 0.56f, 1f));
        drawList.AddText(position + new Vector2((size.X - textSize.X) * 0.5f + 2f,
            (size.Y - textSize.Y) * 0.5f + 2f), textColor, label);
        drawList.AddText(position + new Vector2((size.X - textSize.X) * 0.5f, (size.Y - textSize.Y) * 0.5f),
            ImGui.GetColorU32(new Vector4(1f, 0.96f, 1f, 1f)), label);
        return clicked;
    }

    private void SelectNowPlayingTrack(int trackIndex)
    {
        nowPlayingTrackIndex = (trackIndex + NowPlayingTrackFiles.Length) % NowPlayingTrackFiles.Length;
        StartNowPlayingTrack();
    }

    private void StartNowPlayingTrack()
    {
        if (nowPlayingDisposed)
            return;

        var requestId = ++nowPlayingRequestId;
        nowPlayingLoadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        nowPlayingLoadCancellation = cancellation;
        DisposeNowPlayingPlayback();
        nowPlayingCoverTexture = nowPlayingFallbackCoverTexture;
        nowPlayingIsPlaying = false;
        nowPlayingIsLoading = true;
        nowPlayingStatus = File.Exists(GetNowPlayingCachePath(NowPlayingTrackFiles[nowPlayingTrackIndex]))
            ? "正在读取本地歌曲缓存..."
            : "正在下载并缓存歌曲...";
        _ = LoadNowPlayingTrackAsync(nowPlayingTrackIndex, requestId, cancellation);
    }

    private async Task LoadNowPlayingTrackAsync(int trackIndex, int requestId, CancellationTokenSource cancellation)
    {
        var cancellationToken = cancellation.Token;
        try
        {
            var fileName = NowPlayingTrackFiles[trackIndex];
            var url = "https://fleet-snowfluff.makuraly.xyz/music/" + Uri.EscapeDataString(fileName);
            var cachePath = await CacheNowPlayingTrackAsync(url, fileName, cancellationToken);
            var coverPath = await Task.Run(
                () => CacheNowPlayingCover(cachePath, fileName),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var playback = await Task.Run(() =>
            {
                var reader = new MediaFoundationReader(cachePath);
                try
                {
                    var meter = new SpectrumSampleProvider(reader.ToSampleProvider());
                    var output = new WaveOutEvent();
                    output.Init(new SampleToWaveProvider(meter));
                    return (Reader: reader, Output: output, Meter: meter);
                }
                catch
                {
                    reader.Dispose();
                    throw;
                }
            }, cancellationToken);

            await framework.RunOnFrameworkThread(() =>
            {
                if (nowPlayingDisposed || requestId != nowPlayingRequestId)
                {
                    playback.Output.Dispose();
                    playback.Reader.Dispose();
                    return;
                }

                nowPlayingCoverTexture = coverPath is not null
                    ? textureProvider.GetFromFile(coverPath)
                    : nowPlayingFallbackCoverTexture;
                nowPlayingReader = playback.Reader;
                nowPlayingOutput = playback.Output;
                nowPlayingSpectrum = playback.Meter;
                nowPlayingOutput.Volume = nowPlayingVolume;
                nowPlayingStoppedHandler = (_, args) =>
                {
                    _ = framework.RunOnFrameworkThread(() => HandleNowPlayingStopped(requestId, args));
                };
                nowPlayingOutput.PlaybackStopped += nowPlayingStoppedHandler;
                SuppressGameBgm();
                nowPlayingOutput.Play();
                nowPlayingIsLoading = false;
                nowPlayingIsPlaying = true;
                nowPlayingStatus = string.Empty;
            });
        }
        catch (Exception ex)
        {
            await framework.RunOnFrameworkThread(() =>
            {
                if (requestId != nowPlayingRequestId || nowPlayingDisposed)
                    return;

                DisposeNowPlayingPlayback();
                nowPlayingIsLoading = false;
                nowPlayingIsPlaying = false;
                nowPlayingStatus = $"播放失败：{ex.Message}";
            });
        }
        finally
        {
            try
            {
                await framework.RunOnFrameworkThread(() =>
                {
                    if (ReferenceEquals(nowPlayingLoadCancellation, cancellation))
                        nowPlayingLoadCancellation = null;
                });
            }
            catch
            {
            }

            cancellation.Dispose();
        }
    }

    private async Task<string> CacheNowPlayingTrackAsync(string url, string fileName, CancellationToken cancellationToken)
    {
        var cachePath = GetNowPlayingCachePath(fileName);
        if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
            return cachePath;

        var cacheDirectory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(cacheDirectory);
        var temporaryPath = cachePath + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await NowPlayingHttpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, cachePath, true);
            return cachePath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private string GetNowPlayingCachePath(string fileName)
    {
        var pluginDirectory = pluginInterface.AssemblyLocation.DirectoryName ?? AppContext.BaseDirectory;
        return Path.Combine(pluginDirectory, "Resources", "FleetSnowfluff", "AudioCache", fileName);
    }

    private string? CacheNowPlayingCover(string audioPath, string audioFileName)
    {
        var cacheDirectory = Path.GetDirectoryName(audioPath)!;
        var coverBasePath = Path.Combine(cacheDirectory, Path.GetFileNameWithoutExtension(audioFileName) + ".cover");
        foreach (var extension in new[] { ".jpg", ".png", ".gif" })
        {
            var existingPath = coverBasePath + extension;
            if (File.Exists(existingPath) && new FileInfo(existingPath).Length > 0)
                return existingPath;
        }

        try
        {
            var audioData = File.ReadAllBytes(audioPath);
            var coverData = ExtractId3Cover(audioData);
            if (coverData is null)
                return null;

            var extension = coverData.Value.Extension;
            var coverPath = coverBasePath + extension;
            var temporaryPath = coverPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporaryPath, coverData.Value.Data);
                File.Move(temporaryPath, coverPath, true);
                return coverPath;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] Data, string Extension)? ExtractId3Cover(byte[] audioData)
    {
        if (audioData.Length < 10 || audioData[0] != (byte)'I' ||
            audioData[1] != (byte)'D' || audioData[2] != (byte)'3')
            return null;

        var majorVersion = audioData[3];
        if (majorVersion is not (3 or 4) || (audioData[5] & 0x80) != 0)
            return null;

        var tagSize = ReadSynchsafeInt(audioData, 6);
        var tagEnd = Math.Min(audioData.Length, 10 + tagSize);
        var offset = 10;
        if ((audioData[5] & 0x40) != 0)
        {
            if (offset + 4 > tagEnd)
                return null;

            var extendedHeaderSize = majorVersion == 4
                ? ReadSynchsafeInt(audioData, offset)
                : checked((int)ReadBigEndianUInt32(audioData, offset));
            offset += majorVersion == 4 ? extendedHeaderSize : 4 + extendedHeaderSize;
        }

        while (offset + 10 <= tagEnd)
        {
            if (audioData[offset] == 0)
                break;

            var frameId = System.Text.Encoding.ASCII.GetString(audioData, offset, 4);
            var frameSize = majorVersion == 4
                ? ReadSynchsafeInt(audioData, offset + 4)
                : checked((int)ReadBigEndianUInt32(audioData, offset + 4));
            var formatFlags = audioData[offset + 9];
            var frameDataStart = offset + 10;
            var frameEnd = frameDataStart + frameSize;
            if (frameSize <= 0 || frameEnd > tagEnd)
                break;

            if (frameId == "APIC")
            {
                var prefixLength = 0;
                if (majorVersion == 3)
                {
                    if ((formatFlags & 0xC0) != 0)
                        goto NextFrame;
                    if ((formatFlags & 0x20) != 0)
                        prefixLength = 1;
                }
                else
                {
                    if ((formatFlags & 0x0C) != 0 || (formatFlags & 0x02) != 0)
                        goto NextFrame;
                    if ((formatFlags & 0x40) != 0)
                        prefixLength++;
                    if ((formatFlags & 0x01) != 0)
                        prefixLength += 4;
                }

                var pictureStart = FindId3PictureStart(audioData, frameDataStart + prefixLength, frameEnd);
                if (pictureStart >= 0)
                {
                    var extension = GetImageExtension(audioData, pictureStart, frameEnd - pictureStart);
                    if (extension is not null)
                        return (audioData[pictureStart..frameEnd], extension);
                }
            }

        NextFrame:
            offset = frameEnd;
        }

        return null;
    }

    private static int FindId3PictureStart(byte[] data, int offset, int frameEnd)
    {
        if (offset >= frameEnd)
            return -1;

        var encoding = data[offset++];
        if (encoding > 3)
            return -1;

        var mimeEnd = Array.IndexOf(data, (byte)0, offset, frameEnd - offset);
        if (mimeEnd < 0 || mimeEnd + 1 >= frameEnd)
            return -1;

        var descriptionStart = mimeEnd + 2;
        if (encoding is 1 or 2)
        {
            for (var index = descriptionStart; index + 1 < frameEnd; index += 2)
            {
                if (data[index] == 0 && data[index + 1] == 0)
                    return index + 2;
            }
        }
        else
        {
            var descriptionEnd = Array.IndexOf(data, (byte)0, descriptionStart, frameEnd - descriptionStart);
            if (descriptionEnd >= 0)
                return descriptionEnd + 1;
        }

        return -1;
    }

    private static string? GetImageExtension(byte[] data, int offset, int length)
    {
        if (length >= 3 && data[offset] == 0xFF && data[offset + 1] == 0xD8 && data[offset + 2] == 0xFF)
            return ".jpg";
        if (length >= 8 && data[offset] == 0x89 && data[offset + 1] == 0x50 &&
            data[offset + 2] == 0x4E && data[offset + 3] == 0x47 && data[offset + 4] == 0x0D &&
            data[offset + 5] == 0x0A && data[offset + 6] == 0x1A && data[offset + 7] == 0x0A)
            return ".png";
        if (length >= 6 && data[offset] == (byte)'G' && data[offset + 1] == (byte)'I' &&
            data[offset + 2] == (byte)'F')
            return ".gif";
        return null;
    }

    private static int ReadSynchsafeInt(byte[] data, int offset) =>
        (data[offset] & 0x7F) << 21 |
        (data[offset + 1] & 0x7F) << 14 |
        (data[offset + 2] & 0x7F) << 7 |
        (data[offset + 3] & 0x7F);

    private static uint ReadBigEndianUInt32(byte[] data, int offset) =>
        (uint)data[offset] << 24 |
        (uint)data[offset + 1] << 16 |
        (uint)data[offset + 2] << 8 |
        data[offset + 3];

    private void HandleNowPlayingStopped(int requestId, StoppedEventArgs args)
    {
        if (nowPlayingDisposed || requestId != nowPlayingRequestId)
            return;

        var reachedEnd = false;
        try
        {
            reachedEnd = args.Exception is null && nowPlayingReader is not null &&
                nowPlayingReader.TotalTime - nowPlayingReader.CurrentTime <= TimeSpan.FromSeconds(1);
        }
        catch (ObjectDisposedException)
        {
        }

        DisposeNowPlayingPlayback();
        nowPlayingRequestId++;
        nowPlayingIsPlaying = false;
        nowPlayingIsLoading = false;
        if (!reachedEnd)
        {
            if (args.Exception is not null)
                nowPlayingStatus = $"播放失败：{args.Exception.Message}";
            return;
        }

        switch (nowPlayingModeIndex)
        {
            case 0:
                if (nowPlayingTrackIndex + 1 < NowPlayingTrackFiles.Length)
                {
                    nowPlayingTrackIndex++;
                    StartNowPlayingTrack();
                }
                break;
            case 1:
                nowPlayingTrackIndex = (nowPlayingTrackIndex + 1) % NowPlayingTrackFiles.Length;
                StartNowPlayingTrack();
                break;
            case 2:
                StartNowPlayingTrack();
                break;
            case 3:
                var nextTrack = Random.Shared.Next(NowPlayingTrackFiles.Length - 1);
                if (nextTrack >= nowPlayingTrackIndex)
                    nextTrack++;
                nowPlayingTrackIndex = nextTrack;
                StartNowPlayingTrack();
                break;
        }
    }

    private void DisposeNowPlayingPlayback()
    {
        RestoreGameBgmVolume();
        var output = nowPlayingOutput;
        nowPlayingOutput = null;
        if (output is not null)
        {
            if (nowPlayingStoppedHandler is not null)
                output.PlaybackStopped -= nowPlayingStoppedHandler;
            try
            {
                output.Stop();
            }
            catch
            {
            }
            output.Dispose();
        }

        nowPlayingStoppedHandler = null;
        nowPlayingSpectrum = null;
        nowPlayingReader?.Dispose();
        nowPlayingReader = null;
    }

    private void SuppressGameBgm()
    {
        if (gameBgmSuppressed)
            return;

        try
        {
            if (!gameConfig.TryGet(SystemConfigOption.IsSndBgm, out uint currentEnabled))
                return;

            savedGameBgmEnabled = currentEnabled;
            gameConfig.Set(SystemConfigOption.IsSndBgm, 0u);
            gameBgmSuppressed = true;
        }
        catch
        {
        }
    }

    private void RestoreGameBgmVolume()
    {
        if (!gameBgmSuppressed)
            return;

        try
        {
            gameConfig.Set(SystemConfigOption.IsSndBgm, savedGameBgmEnabled);
            gameBgmSuppressed = false;
        }
        catch
        {
        }
    }

    private static void DrawPixelCassette(ImDrawListPtr drawList, Vector2 position, Vector2 size, float time)
    {
        var body = new Vector4(1f, 0.95f, 0.82f, 1f);
        var bodyInset = new Vector4(1f, 0.98f, 0.90f, 1f);
        var gold = new Vector4(0.87f, 0.66f, 0.28f, 1f);
        var goldLight = new Vector4(1f, 0.88f, 0.55f, 1f);
        var dark = new Vector4(0.24f, 0.16f, 0.34f, 1f);
        var shadow = position + new Vector2(3f, 3f);
        drawList.AddRectFilled(shadow, shadow + size,
            ImGui.GetColorU32(new Vector4(0.20f, 0.13f, 0.33f, 0.72f)));
        drawList.AddRectFilled(position, position + size, ImGui.GetColorU32(body));
        drawList.AddRect(position, position + size, ImGui.GetColorU32(gold), 0f, ImDrawFlags.None, 2f);
        drawList.AddRect(position + new Vector2(4f, 4f), position + size - new Vector2(4f, 4f),
            ImGui.GetColorU32(bodyInset), 0f, ImDrawFlags.None, 1f);

        for (var x = 15f; x < size.X - 15f; x += 8f)
        {
            var stripeColor = ImGui.GetColorU32(((int)x / 8) % 2 == 0
                ? goldLight
                : new Vector4(0.97f, 0.88f, 0.68f, 1f));
            var stripeX = position.X + x;
            drawList.AddRectFilled(new Vector2(stripeX, position.Y + 14f),
                new Vector2(stripeX + 4f, position.Y + 23f), stripeColor);
            drawList.AddRectFilled(new Vector2(stripeX, position.Y + 111f),
                new Vector2(stripeX + 4f, position.Y + 120f), stripeColor);
        }
        drawList.AddRect(position + new Vector2(12f, 10f), position + new Vector2(size.X - 12f, 26f),
            ImGui.GetColorU32(gold), 0f, ImDrawFlags.None, 1.5f);
        drawList.AddRect(position + new Vector2(25f, 106f), position + new Vector2(size.X - 25f, 122f),
            ImGui.GetColorU32(gold), 0f, ImDrawFlags.None, 1.5f);

        var windowMin = position + new Vector2(20f, 36f);
        var windowMax = position + new Vector2(size.X - 20f, 93f);
        drawList.AddRectFilled(windowMin, windowMax, ImGui.GetColorU32(dark));
        drawList.AddRect(windowMin, windowMax, ImGui.GetColorU32(gold), 0f, ImDrawFlags.None, 2f);
        drawList.AddRect(windowMin + new Vector2(2f, 2f), windowMax - new Vector2(2f, 2f),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.22f)));

        var reelY = position.Y + 64.5f;
        var leftReel = new Vector2(position.X + 54f, reelY);
        var rightReel = new Vector2(position.X + size.X - 54f, reelY);
        DrawCassetteReel(drawList, leftReel, 16f, time);
        DrawCassetteReel(drawList, rightReel, 16f, -time * 0.87f);
        drawList.AddLine(leftReel + new Vector2(15f, 0f),
            new Vector2(position.X + 88f, reelY), ImGui.GetColorU32(goldLight), 3f);
        drawList.AddLine(new Vector2(position.X + size.X - 88f, reelY),
            rightReel - new Vector2(15f, 0f), ImGui.GetColorU32(goldLight), 3f);
        var hubMin = position + new Vector2(92f, 46f);
        var hubMax = position + new Vector2(113f, 83f);
        drawList.AddRectFilled(hubMin, hubMax, ImGui.GetColorU32(new Vector4(0.16f, 0.11f, 0.25f, 1f)));
        drawList.AddRect(hubMin, hubMax, ImGui.GetColorU32(goldLight), 0f, ImDrawFlags.None, 1f);
        drawList.AddLine(new Vector2(position.X + 67f, reelY), new Vector2(hubMin.X, reelY),
            ImGui.GetColorU32(goldLight), 2f);
        drawList.AddLine(new Vector2(hubMax.X, reelY), new Vector2(position.X + 138f, reelY),
            ImGui.GetColorU32(goldLight), 2f);

        foreach (var screw in new[]
                 {
                     position + new Vector2(11f, 11f), position + new Vector2(size.X - 11f, 11f),
                     position + new Vector2(11f, size.Y - 11f), position + new Vector2(size.X - 11f, size.Y - 11f),
                 })
            drawList.AddCircleFilled(screw, 1.8f, ImGui.GetColorU32(new Vector4(0.56f, 0.37f, 0.12f, 1f)), 8);
    }

    private static void DrawCassetteReel(ImDrawListPtr drawList, Vector2 center, float radius, float angle)
    {
        drawList.AddCircleFilled(center, radius, ImGui.GetColorU32(new Vector4(1f, 0.88f, 0.55f, 1f)), 24);
        drawList.AddCircle(center, radius - 1f, ImGui.GetColorU32(new Vector4(0.69f, 0.48f, 0.18f, 1f)), 24, 1f);
        for (var spoke = 0; spoke < 8; spoke++)
        {
            var radians = angle + spoke * MathF.PI / 4f;
            var inner = center + new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * 4f;
            var outer = center + new Vector2(MathF.Cos(radians), MathF.Sin(radians)) * 12f;
            drawList.AddLine(inner, outer, ImGui.GetColorU32(new Vector4(0.72f, 0.48f, 0.17f, 0.92f)), 1f);
        }
        drawList.AddCircleFilled(center, 4f, ImGui.GetColorU32(new Vector4(0.28f, 0.19f, 0.35f, 1f)), 16);
        drawList.AddCircle(center, 4f, ImGui.GetColorU32(new Vector4(1f, 0.91f, 0.64f, 1f)), 16, 1f);
    }

    private void DrawAemeathHero()
    {
        var size = new Vector2(-1, 54);
        if (!ImGui.BeginChild("AemeathHero", size, false, ImGuiWindowFlags.NoScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        var windowSize = ImGui.GetWindowSize();
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetWindowPos();
        var playerTop = origin + new Vector2(2f, 6f);
        var playerWidth = Math.Max(360f, windowSize.X - 280f);
        var playerSize = new Vector2(playerWidth, 40f);
        drawList.AddRectFilled(playerTop, playerTop + playerSize,
            ImGui.GetColorU32(new Vector4(0.94f, 0.83f, 0.98f, 0.54f)), 7f);
        drawList.AddRect(playerTop, playerTop + playerSize,
            ImGui.GetColorU32(new Vector4(1.00f, 0.77f, 0.91f, 0.78f)), 7f,
            ImDrawFlags.RoundCornersAll, 1f);

        var spectrumMin = playerTop + new Vector2(7f, 5f);
        var spectrumMax = spectrumMin + new Vector2(82f, 30f);
        drawList.AddRectFilled(spectrumMin, spectrumMax,
            ImGui.GetColorU32(new Vector4(0.44f, 0.29f, 0.65f, 0.34f)), 4f);
        var spectrumTime = (float)ImGui.GetTime();
        for (var i = 0; i < 18; i++)
        {
            var barHeight = 3f + (MathF.Sin(spectrumTime * 3.1f + i * 0.83f) * 0.5f + 0.5f) * 19f;
            var x = spectrumMin.X + 5f + i * 4f;
            drawList.AddRectFilled(new Vector2(x, spectrumMax.Y - 3f - barHeight),
                new Vector2(x + 2f, spectrumMax.Y - 3f),
                ImGui.GetColorU32(i % 3 == 0 ? AemeathCyan : AemeathPink), 1f);
        }

        var titleLeft = spectrumMax.X + 9f;
        var controlsLeft = playerTop.X + playerSize.X - 132f;
        drawList.PushClipRect(new Vector2(titleLeft, playerTop.Y),
            new Vector2(controlsLeft - 6f, playerTop.Y + playerSize.Y), true);
        drawList.AddText(new Vector2(titleLeft, playerTop.Y + 12f),
            ImGui.GetColorU32(AemeathBodyInk), MiniPlayerTracks[miniPlayerTrackIndex]);
        drawList.PopClipRect();

        DrawMiniPlayerButton("MiniPrev", new Vector2(controlsLeft, playerTop.Y + 7f), "prev");
        DrawMiniPlayerButton("MiniPlay", new Vector2(controlsLeft + 31f, playerTop.Y + 7f), "play");
        DrawMiniPlayerButton("MiniNext", new Vector2(controlsLeft + 62f, playerTop.Y + 7f), "next");
        DrawMiniPlayerButton("MiniTrack", new Vector2(controlsLeft + 93f, playerTop.Y + 7f), "list");

        ImGui.SetCursorPos(new Vector2(Math.Max(420f, windowSize.X - 252f), 13f));
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

    private void DrawMiniPlayerButton(string id, Vector2 position, string icon)
    {
        ImGui.SetCursorScreenPos(position);
        var clicked = ImGui.InvisibleButton($"##{id}", new Vector2(25f, 25f));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var min = position;
        var max = min + new Vector2(25f, 25f);
        drawList.AddRectFilled(min, max,
            ImGui.GetColorU32(hovered
                ? new Vector4(0.91f, 0.50f, 0.74f, 0.96f)
                : new Vector4(0.61f, 0.45f, 0.75f, 0.82f)), 4f);

        var centerY = min.Y + 12.5f;
        var ink = ImGui.GetColorU32(Vector4.One);
        switch (icon)
        {
            case "play":
                drawList.AddTriangleFilled(new Vector2(min.X + 9f, min.Y + 6f),
                    new Vector2(min.X + 9f, min.Y + 19f), new Vector2(min.X + 19f, centerY), ink);
                if (clicked)
                    OpenMiniPlayerTrack();
                DrawMiniPlayerTooltip("在浏览器中播放所选曲目");
                break;
            case "prev":
                drawList.AddRectFilled(new Vector2(min.X + 6f, min.Y + 7f), new Vector2(min.X + 8f, min.Y + 18f), ink);
                drawList.AddTriangleFilled(new Vector2(min.X + 18f, min.Y + 6f),
                    new Vector2(min.X + 18f, min.Y + 19f), new Vector2(min.X + 8f, centerY), ink);
                if (clicked)
                    miniPlayerTrackIndex = (miniPlayerTrackIndex + MiniPlayerTracks.Length - 1) % MiniPlayerTracks.Length;
                DrawMiniPlayerTooltip("上一首");
                break;
            case "next":
                drawList.AddRectFilled(new Vector2(min.X + 17f, min.Y + 7f), new Vector2(min.X + 19f, min.Y + 18f), ink);
                drawList.AddTriangleFilled(new Vector2(min.X + 7f, min.Y + 6f),
                    new Vector2(min.X + 7f, min.Y + 19f), new Vector2(min.X + 17f, centerY), ink);
                if (clicked)
                    miniPlayerTrackIndex = (miniPlayerTrackIndex + 1) % MiniPlayerTracks.Length;
                DrawMiniPlayerTooltip("下一首");
                break;
            case "list":
                for (var i = 0; i < 3; i++)
                {
                    var y = min.Y + 7f + i * 5f;
                    drawList.AddRectFilled(new Vector2(min.X + 6f, y), new Vector2(min.X + 9f, y + 2f), ink);
                    drawList.AddRectFilled(new Vector2(min.X + 12f, y), new Vector2(min.X + 19f, y + 2f), ink);
                }
                if (clicked)
                    ImGui.OpenPopup("MiniPlayerPlaylist");
                DrawMiniPlayerTooltip("播放列表");
                break;
        }

        if (ImGui.BeginPopup("MiniPlayerPlaylist"))
        {
            for (var i = 0; i < MiniPlayerTracks.Length; i++)
            {
                if (ImGui.Selectable(MiniPlayerTracks[i], miniPlayerTrackIndex == i))
                    miniPlayerTrackIndex = i;
            }
            ImGui.EndPopup();
        }
    }

    private static void DrawMiniPlayerTooltip(string text)
    {
        if (!ImGui.IsItemHovered())
            return;

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(text);
        ImGui.EndTooltip();
    }

    private void OpenMiniPlayerTrack()
    {
        const string baseUrl = "https://fleet-snowfluff.makuraly.xyz/music/";
        var fileName = MiniPlayerTrackFiles[miniPlayerTrackIndex];
        var url = baseUrl + Uri.EscapeDataString(fileName);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
        }
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
            ? new Vector4(0.79f, 0.29f, 0.58f, 0.90f)
            : hovered
                ? new Vector4(0.70f, 0.52f, 0.76f, 0.62f)
                : new Vector4(0.68f, 0.56f, 0.79f, enabled ? 0.40f : 0.24f);
        if (active)
            fill = new Vector4(0.91f, 0.43f, 0.70f, 0.96f);
        drawList.AddRectFilled(topLeft + new Vector2(3f, 3f), topLeft + size + new Vector2(3f, 3f),
            ImGui.GetColorU32(new Vector4(0.25f, 0.17f, 0.40f, enabled ? 0.20f : 0.10f)));
        drawList.AddRectFilled(topLeft, topLeft + size, ImGui.GetColorU32(fill), 3f);
        drawList.AddRect(topLeft, topLeft + size,
            ImGui.GetColorU32(selected
                ? new Vector4(1.00f, 0.76f, 0.90f, 0.94f)
                : new Vector4(0.88f, 0.75f, 0.96f, enabled ? 0.62f : 0.28f)),
            3f,
            ImDrawFlags.RoundCornersAll,
            1.5f);
        if (selected)
            drawList.AddRectFilled(topLeft, topLeft + new Vector2(3f, size.Y),
                ImGui.GetColorU32(AemeathPink), 2f);

        var textColor = selected
            ? AemeathInk
            : enabled ? AemeathBodyInk : AemeathBodyMuted;
        var labelSize = ImGui.CalcTextSize(label);
        var labelY = string.IsNullOrEmpty(subtitle) ? (size.Y - labelSize.Y) * 0.5f : 5f;
        drawList.AddText(topLeft + new Vector2((size.X - labelSize.X) * 0.5f, labelY),
            ImGui.GetColorU32(textColor), label);
        if (!string.IsNullOrEmpty(subtitle))
        {
            var subtitleSize = ImGui.CalcTextSize(subtitle);
            drawList.AddText(topLeft + new Vector2((size.X - subtitleSize.X) * 0.5f, 25f),
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
            ? new Vector4(0.30f, 0.25f, 0.42f, 0.42f)
            : active
                ? new Vector4(0.91f, 0.43f, 0.70f, 0.96f)
                : hovered
                    ? new Vector4(0.55f, 0.37f, 0.68f, 0.92f)
                    : accent
                        ? new Vector4(0.78f, 0.30f, 0.59f, 0.90f)
                        : new Vector4(0.34f, 0.25f, 0.54f, 0.84f);
        var border = accent || active
            ? new Vector4(1.00f, 0.74f, 0.89f, 0.92f)
            : new Vector4(0.91f, 0.76f, 0.96f, enabled ? 0.72f : 0.30f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(topLeft + new Vector2(3f, 3f), topLeft + size + new Vector2(3f, 3f),
            ImGui.GetColorU32(new Vector4(0.25f, 0.17f, 0.40f, enabled ? 0.50f : 0.22f)));
        drawList.AddRectFilled(topLeft, topLeft + size, ImGui.GetColorU32(fill), 3f);
        drawList.AddRect(topLeft, topLeft + size, ImGui.GetColorU32(border), 3f,
            ImDrawFlags.RoundCornersAll, 1.5f);
        drawList.AddRectFilled(topLeft + new Vector2(5f, 3f),
            new Vector2(topLeft.X + size.X - 5f, topLeft.Y + 4f),
            ImGui.GetColorU32(new Vector4(1.00f, 0.88f, 0.96f, enabled ? 0.24f : 0.10f)));
        var textSize = ImGui.CalcTextSize(label);
        var textPosition = topLeft + new Vector2(
            Math.Max(8f, (size.X - textSize.X) * 0.5f),
            Math.Max(0f, (size.Y - textSize.Y) * 0.5f));
        drawList.PushClipRect(topLeft + new Vector2(5f, 2f), topLeft + size - new Vector2(5f, 2f), true);
        drawList.AddText(textPosition, ImGui.GetColorU32(enabled ? AemeathInk : AemeathMuted), label);
        drawList.PopClipRect();
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
            ? new Vector4(0.34f, 0.29f, 0.47f, 0.42f)
            : value
                ? new Vector4(0.91f, 0.43f, 0.70f, 0.92f)
                : new Vector4(0.43f, 0.32f, 0.64f, 0.76f);
        drawList.AddRectFilled(trackMin, trackMax, ImGui.GetColorU32(trackColor), 9f);
        drawList.AddRect(trackMin, trackMax,
            ImGui.GetColorU32(new Vector4(1.00f, 0.78f, 0.91f, enabled ? 0.72f : 0.28f)),
            9f,
            ImDrawFlags.RoundCornersAll,
            1f);
        var knob = trackMin + new Vector2(value ? 29f : 9f, 9f);
        drawList.AddCircleFilled(knob, 6f, ImGui.GetColorU32(value ? SitePixelBorder : AemeathMuted));
        drawList.AddText(topLeft + new Vector2(52f, 5f),
            ImGui.GetColorU32(enabled ? AemeathBodyInk : AemeathBodyMuted), label);
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
            ImGui.GetColorU32(new Vector4(1.00f, 0.78f, 0.91f, enabled ? 0.72f : 0.28f)), 20, 1.2f);
        if (selected)
            drawList.AddCircleFilled(center, 4.5f, ImGui.GetColorU32(AemeathPink));
        drawList.AddText(topLeft + new Vector2(24f, 5f),
            ImGui.GetColorU32(enabled ? AemeathBodyInk : AemeathBodyMuted), label);
        return clicked && enabled;
    }

    private static void DrawAemeathInputShell(Vector2 topLeft, Vector2 size, bool enabled = true)
    {
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(
            topLeft,
            topLeft + size,
            ImGui.GetColorU32(enabled
                ? new Vector4(0.28f, 0.21f, 0.46f, 0.88f)
                : new Vector4(0.30f, 0.25f, 0.42f, 0.44f)),
            3f);
        drawList.AddRect(
            topLeft,
            topLeft + size,
            ImGui.GetColorU32(new Vector4(1.00f, 0.78f, 0.91f, enabled ? 0.72f : 0.30f)),
            3f,
            ImDrawFlags.RoundCornersAll,
            1f);
        drawList.AddLine(
            topLeft + new Vector2(10f, size.Y - 2f),
            new Vector2(topLeft.X + Math.Min(size.X - 10f, 80f), topLeft.Y + size.Y - 2f),
            ImGui.GetColorU32(new Vector4(1.00f, 0.66f, 0.85f, enabled ? 0.76f : 0.28f)),
            2f);
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
            ImGui.GetColorU32(enabled ? AemeathBodyInk : AemeathBodyMuted),
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
        var textColor = color == AemeathCyan ? AemeathTextCyan
            : color == AemeathGold ? AemeathTextGold
            : color == AemeathPink ? AemeathTextPink
            : color;
        ImGui.TextColored(textColor, $"[{value}]");
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
        ImGui.PushStyleColor(ImGuiCol.Text, AemeathInk);
        if (ImGui.InputText("###Credential", ref credentialInput, 128, ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue))
        {
            ValidateCredentialInput();
        }
        ImGui.PopStyleColor();

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

        DrawLootRollSettings();

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
        var priceLabelWidth = ImGui.CalcTextSize("补图最高单价").X;
        var priceInputWidth = Math.Min(
            550f,
            Math.Max(120f, ImGui.GetContentRegionAvail().X - priceLabelWidth - ImGui.GetStyle().ItemSpacing.X));
        DrawAemeathInputShell(priceInputTopLeft,
            new Vector2(priceInputWidth, 30f), maxUnitPriceEnabled);
        ImGui.BeginDisabled(!maxUnitPriceEnabled);
        ImGui.SetNextItemWidth(priceInputWidth);
        ImGui.PushStyleColor(ImGuiCol.Text, AemeathInk);
        ImGui.InputText("补图最高单价", ref mapSupplementMaxUnitPriceInput, 32,
            ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.PopStyleColor();
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
        ImGui.Spacing();
        ImGui.TextColored(AemeathGold, "GREED TRAP");
        ImGui.TextDisabled("选择强欲陷阱卡牌的处理方式。");
        DrawDoorSelectionGreedTrapPolicy();
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

    private void DrawDoorSelectionGreedTrapPolicy()
    {
        var policy = plugin.DoorSelectionGreedTrapPolicy;
        ImGui.Text("强欲策略");
        ImGui.SameLine(190);
        if (DrawAemeathRadio("DoorGreedTrapAskMe", "我自己选",
                policy == DoorSelectionGreedTrapPolicy.AskMe, true, 90f))
        {
            plugin.SetDoorSelectionGreedTrapPolicy(DoorSelectionGreedTrapPolicy.AskMe);
        }

        ImGui.SameLine(278);
        if (DrawAemeathRadio("DoorGreedTrapOpenCoffer", "直接开箱",
                policy == DoorSelectionGreedTrapPolicy.OpenCoffer, true, 112f))
        {
            plugin.SetDoorSelectionGreedTrapPolicy(DoorSelectionGreedTrapPolicy.OpenCoffer);
        }

        ImGui.SameLine(396);
        if (DrawAemeathRadio("DoorGreedTrapAuto", "按奖励自动挑战",
                policy == DoorSelectionGreedTrapPolicy.Auto, true, 150f))
        {
            plugin.SetDoorSelectionGreedTrapPolicy(DoorSelectionGreedTrapPolicy.Auto);
        }
    }

    private void DrawLootRollSettings()
    {
        ImGui.Spacing();
        ImGui.TextColored(AemeathGold, "战利品投票");
        ImGui.TextDisabled("设置宝箱战利品的投票方式。");
        var policy = plugin.LootRollPolicy;
        ImGui.Text("投票策略");
        ImGui.SameLine(190);
        if (DrawAemeathRadio("LootRollNeed", "需求", policy == LootRoll.Need, true, 76f))
            plugin.SetLootRollPolicy(LootRoll.Need);
        ImGui.SameLine(272);
        if (DrawAemeathRadio("LootRollGreed", "贪婪", policy == LootRoll.Greed, true, 82f))
            plugin.SetLootRollPolicy(LootRoll.Greed);
        ImGui.SameLine(360);
        if (DrawAemeathRadio("LootRollPass", "放弃", policy == LootRoll.Pass, true, 76f))
            plugin.SetLootRollPolicy(LootRoll.Pass);
        ImGui.SameLine(442);
        if (DrawAemeathRadio("LootRollManual", "手动", policy == LootRoll.Manual, true, 92f))
            plugin.SetLootRollPolicy(LootRoll.Manual);
    }

    private void DrawPageHeading(string eyebrow, string title, string subtitle)
    {
        ImGui.TextColored(AemeathBodyInk, title);
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
        ImGui.TextColored(accent == AemeathCyan ? AemeathTextCyan : AemeathTextPink, $"◆ {title}");
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
            allReady ? AemeathTextCyan : AemeathTextPink,
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
        ImGui.TextColored(AemeathTextPink, "动画锁测试");
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

        ImGui.Separator();
        ImGui.TextColored(AemeathTextPink, "宝物库选门动画");
        var skipDoorAnimation = plugin.DoorSelectionSkipAnimationEnabled;
        if (DrawAemeathToggle("DoorSelectionSkipAnimation", "Hook 跳过开门动画", ref skipDoorAnimation))
        {
            plugin.SetDoorSelectionSkipAnimationEnabled(skipDoorAnimation);
        }
        ImGui.TextWrapped(plugin.DoorSelectionSkipAnimationStatus);
        ImGui.TextDisabled("仅在选门地图确认门后短时间生效；默认关闭，Hook 不可用时自动保留原动画。" );

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
        ImGui.NewLine();
        ImGui.Separator();
        ImGui.TextColored(AemeathTextPink, "强欲卡牌监听测试");
        var greedLifecycleMonitoring = plugin.DoorSelectionGreedTrapLifecycleMonitoring;
        if (DrawAemeathToggle("TestGreedTrapLifecycle", "监听强欲卡牌数据", ref greedLifecycleMonitoring))
        {
            plugin.SetDoorSelectionGreedTrapLifecycleMonitoring(greedLifecycleMonitoring);
        }
        ImGui.TextWrapped(plugin.DoorSelectionGreedTrapLifecycleTestStatus);
        ImGui.TextDisabled("仅监听强欲页面与 TreasureHighLow 的 PostRefresh，最多保留 10 条参数和图片节点；不触发交互。");
        ImGui.NewLine();
        testButtonsInRow = 0;
        if (DrawAemeathButton("TestGreedTrapPage", "检测强欲页面", new Vector2(158f, 30f)))
        {
            plugin.TestDetectDoorSelectionGreedTrapPage();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestGreedTrapChallengeClick", "测试点击解除陷阱", new Vector2(178f, 30f)))
        {
            plugin.TestClickDoorSelectionGreedTrapChallenge();
        }
        ContinueTestButtonRow();
        if (DrawAemeathButton("TestGreedTrapCardValue", "读取强欲卡牌点数", new Vector2(178f, 30f)))
        {
            plugin.TestReadDoorSelectionGreedTrapCardValue();
        }
        ImGui.TextWrapped(plugin.InteractableObjectScanStatus);
        ImGui.TextWrapped(plugin.EffectCenterTestStatus);
        ImGui.TextWrapped(plugin.FieldMarkerTestStatus);
        ImGui.TextWrapped(plugin.DoorSelectionFloorDetectionTestStatus);
        ImGui.TextWrapped(plugin.DoorSelectionGlowingDoorTestStatus);
        ImGui.TextWrapped(plugin.DoorSelectionGreedTrapPageDetectionTestStatus);
        ImGui.TextWrapped(plugin.DoorSelectionGreedTrapChallengeClickTestStatus);
        ImGui.TextWrapped(plugin.DoorSelectionGreedTrapCardValueTestStatus);

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
        drawList.AddRectFilled(topLeft + new Vector2(3f, 3f),
            topLeft + new Vector2(width + 3f, height + 3f),
            ImGui.GetColorU32(new Vector4(0.25f, 0.17f, 0.40f, 0.50f)));
        drawList.AddRectFilled(topLeft, topLeft + new Vector2(width, height),
            ImGui.GetColorU32(treasureMapSelectorOpen
                ? new Vector4(0.55f, 0.34f, 0.65f, 0.94f)
                : new Vector4(0.32f, 0.24f, 0.51f, 0.88f)), 3f);
        drawList.AddRect(topLeft, topLeft + new Vector2(width, height),
            ImGui.GetColorU32(new Vector4(1.00f, 0.78f, 0.91f, treasureMapSelectorOpen ? 0.94f : 0.72f)),
            3f, ImDrawFlags.RoundCornersAll, 1.5f);
        drawList.AddText(topLeft + new Vector2(12f, 8f), ImGui.GetColorU32(AemeathInk),
            plugin.SelectedTreasureMapName);
        var arrowCenter = topLeft + new Vector2(width - 18f, height * 0.5f);
        drawList.AddTriangleFilled(
            arrowCenter + new Vector2(-5f, -2f),
            arrowCenter + new Vector2(5f, -2f),
            arrowCenter + new Vector2(0f, 4f),
            ImGui.GetColorU32(SitePixelBorder));

        if (!treasureMapSelectorOpen || plugin.TreasureMapOptionCount == 0)
            return;

        const float optionHeight = 38f;
        var optionCount = plugin.TreasureMapOptionCount;
        var popupHeight = Math.Min(optionCount, 6) * optionHeight;
        var popupTopLeft = topLeft + new Vector2(0f, height + 4f);
        drawList.AddRectFilled(popupTopLeft, popupTopLeft + new Vector2(width, popupHeight),
            ImGui.GetColorU32(new Vector4(0.29f, 0.21f, 0.46f, 0.98f)), 3f);
        drawList.AddRect(popupTopLeft, popupTopLeft + new Vector2(width, popupHeight),
            ImGui.GetColorU32(new Vector4(1.00f, 0.78f, 0.91f, 0.72f)), 3f,
            ImDrawFlags.RoundCornersAll, 1.5f);

        for (var index = 0; index < Math.Min(optionCount, 6); index++)
        {
            var rowTopLeft = popupTopLeft + new Vector2(4f, index * optionHeight + 2f);
            var rowSize = new Vector2(width - 8f, optionHeight - 4f);
            ImGui.SetCursorScreenPos(rowTopLeft);
            var rowClicked = ImGui.InvisibleButton($"##TreasureMapOption{index}", rowSize);
            var selected = index == plugin.SelectedTreasureMapIndex;
            var rowHovered = ImGui.IsItemHovered();
            var rowFill = selected
                ? new Vector4(0.79f, 0.29f, 0.58f, 0.88f)
                : rowHovered
                    ? new Vector4(0.56f, 0.38f, 0.68f, 0.90f)
                    : new Vector4(0.43f, 0.33f, 0.62f, 0.66f);
            drawList.AddRectFilled(rowTopLeft, rowTopLeft + rowSize, ImGui.GetColorU32(rowFill), 2f);
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

    private sealed class SpectrumSampleProvider : ISampleProvider
    {
        private const int FftSize = 128;
        private const int FftPower = 7;
        private const int BandCount = 64;
        private const float MinDecibels = -100f;
        private const float MaxDecibels = -30f;
        private const long SpectrumUpdateIntervalMs = 33;
        private const float HighFrequencyTiltDecibels = 18f;
        private const float DynamicFloorRatio = 0.16f;
        private const int TailPropagationStartBand = 24;
        private const float TailPropagationDecay = 0.985f;
        private const float TailPropagationRatio = 0.24f;
        private const float DisplayGain = 1.35f;
        private const float AttackSmoothing = 0.52f;
        private const float ReleaseSmoothing = 0.78f;
        private readonly ISampleProvider source;
        private readonly int channels;
        private readonly float[] frame = new float[FftSize];
        private readonly NAudio.Dsp.Complex[] fft = new NAudio.Dsp.Complex[FftSize];
        private readonly float[] rawLevels = new float[BandCount];
        private readonly float[] levels = new float[BandCount];
        private readonly object levelsLock = new();
        private int frameCount;
        private long lastSpectrumUpdateMs;

        public SpectrumSampleProvider(ISampleProvider source)
        {
            this.source = source;
            channels = Math.Max(1, source.WaveFormat.Channels);
        }

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            var samplesRead = source.Read(buffer, offset, count);
            var end = offset + samplesRead;
            for (var index = offset; index + channels <= end; index += channels)
            {
                var mono = 0f;
                for (var channel = 0; channel < channels; channel++)
                    mono += buffer[index + channel];
                frame[frameCount++] = mono / channels;
                if (frameCount < FftSize)
                    continue;

                AnalyzeFrame();
                Array.Copy(frame, FftSize / 2, frame, 0, FftSize / 2);
                frameCount = FftSize / 2;
            }
            return samplesRead;
        }

        public void CopyLevels(float[] destination)
        {
            lock (levelsLock)
            {
                // The website samples the analyser at 30 FPS. Apply its smoothing
                // at read time instead of once per audio FFT window.
                var now = Environment.TickCount64;
                if (now - lastSpectrumUpdateMs >= SpectrumUpdateIntervalMs)
                {
                    lastSpectrumUpdateMs = now;
                    for (var band = 0; band < BandCount; band++)
                    {
                        var target = Math.Clamp(rawLevels[band] * DisplayGain, 0f, 1f);
                        var smoothing = target > levels[band] ? AttackSmoothing : ReleaseSmoothing;
                        levels[band] = levels[band] * smoothing + target * (1f - smoothing);
                    }
                }
                Array.Copy(levels, destination, Math.Min(levels.Length, destination.Length));
            }
        }

        private void AnalyzeFrame()
        {
            for (var i = 0; i < FftSize; i++)
            {
                fft[i].X = frame[i] * (float)FastFourierTransform.HannWindow(i, FftSize);
                fft[i].Y = 0f;
            }
            FastFourierTransform.FFT(true, FftPower, fft);

            lock (levelsLock)
            {
                var energySquared = 0f;
                for (var band = 0; band < BandCount; band++)
                {
                    // Match AnalyserNode.frequencyBinCount: all 64 bins, including
                    // the low-frequency bin, are mapped linearly to the bars.
                    var bin = Math.Clamp(band, 0, FftSize / 2 - 1);
                    var magnitude = MathF.Sqrt(fft[bin].X * fft[bin].X + fft[bin].Y * fft[bin].Y);
                    // NAudio's FFT is unnormalised. Web Audio reports the equivalent
                    // amplitude after normalising by the FFT length.
                    var normalizedMagnitude = magnitude * (2f / FftSize);
                    energySquared += normalizedMagnitude * normalizedMagnitude;
                    var decibels = 20f * MathF.Log10(Math.Max(0.0000001f, normalizedMagnitude));
                    // Native FFT output usually has a much narrower high-frequency
                    // range than the browser analyser. Apply a gentle spectral tilt
                    // so upper bars retain the same visible response as the webpage.
                    decibels += band / (float)(BandCount - 1) * HighFrequencyTiltDecibels;
                    rawLevels[band] = Math.Clamp(
                        (decibels - MinDecibels) / (MaxDecibels - MinDecibels), 0f, 1f);
                }

                // Keep a small amount of whole-frame movement in quiet upper bins;
                // this prevents the tail from becoming a permanently flat baseline.
                var rms = MathF.Sqrt(energySquared / BandCount);
                var rmsDecibels = 20f * MathF.Log10(Math.Max(0.0000001f, rms));
                var dynamicFloor = Math.Clamp(
                    (rmsDecibels - MinDecibels) / (MaxDecibels - MinDecibels), 0f, 1f) *
                    DynamicFloorRatio;
                for (var band = 0; band < BandCount; band++)
                    rawLevels[band] = Math.Max(rawLevels[band], dynamicFloor);

                // Lossy music files often contain almost no energy in the last
                // few FFT bins. Carry a decaying portion of the audible spectrum
                // into that tail so it follows the music instead of becoming a
                // perfectly flat minimum-height segment.
                var propagatedLevel = 0f;
                for (var band = 0; band < BandCount; band++)
                {
                    propagatedLevel = Math.Max(rawLevels[band], propagatedLevel * TailPropagationDecay);
                    if (band >= TailPropagationStartBand)
                        rawLevels[band] = Math.Max(rawLevels[band], propagatedLevel * TailPropagationRatio);
                }
            }
        }
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
