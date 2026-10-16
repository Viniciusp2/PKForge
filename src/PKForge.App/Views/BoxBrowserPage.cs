using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using PKHeX.Core;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

using static PKForge.App.Views.EditorRows;

namespace PKForge.App.Views;

/// <summary>
/// The storage screen, composed like a console UI: thin status strip on top,
/// square box grid left, info/editor panel right, button-hint bar at the bottom.
/// Landscape-only - this app is designed for the AYN Thor, not a phone.
/// </summary>
public sealed partial class BoxBrowserPage : ContentPage, IPadPagingHandler, IPadReleaseHandler
{
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISpriteService _sprites;
    private readonly ThemeService _theme;
    private readonly SKCanvasView _canvas;
    private readonly SKCanvasView _boxBar;
    private readonly SKCanvasView _boxNeighbors;
    private readonly FrameInvalidator _frame;
    private readonly ContentView _footerHost;
    private readonly Grid _storageContent;
    private readonly View _sidePanel;
    private ScrollView? _editorScroll;
    private EditorFocusTarget[] EditorFocusTargets = [];
    private Grid _hostGrid = null!;
    private long _partyPulseStart = Environment.TickCount64;
    private IDispatcherTimer? _partyPulseTimer;
    private IDispatcherTimer? _boxManagePulseTimer;
    private bool _boxManageMode;
    private bool _boxManageBusy;
    private bool _boxHeld;
    private bool _editorFocusMode;
    private int _editorFocusIndex;
    private int _heldBox;
    private int _slotBeforeBoxManage = -1;
    /// <summary>A is down in multi-select: d-pad moves stretch the area gesture.</summary>
    private bool _selectHeld;
    /// <summary>The slot a touch drag last reported, so moves only repaint on a new slot.</summary>
    private int _touchDragSlot = -1;
    private readonly HashSet<int> _lockedSlots = [];
    private readonly HashSet<int> _markedBoxes = [];

    /// <summary>The party cursor breathes: a light repaint loop that only runs on the party view.</summary>
    private void EnsurePartyPulse()
    {
        if (_partyPulseTimer is not null || _viewModel.SelectedSlot < 0) return;
        _partyPulseStart = Environment.TickCount64;
        _partyPulseTimer = Dispatcher.CreateTimer();
        _partyPulseTimer.Interval = TimeSpan.FromMilliseconds(45);
        _partyPulseTimer.Tick += (_, _) => _frame.Request();
        _partyPulseTimer.Start();
    }

    private void StopPartyPulse()
    {
        _partyPulseTimer?.Stop();
        _partyPulseTimer = null;
    }

    public BoxBrowserPage(BoxBrowserViewModel viewModel, ISpriteService sprites, ThemeService theme)
    {
        _viewModel = viewModel;
        _viewModel.ConfirmHackRisk = async _ =>
        {
            var choice = await PadMenu.ShowAsync(_hostGrid, RomHackNotice.Title, RomHackNotice.Warning,
                new PadOption(RomHackNotice.EditOption, Glyph: "!", Accent: UiTokens.Bad,
                    Detail: "Sua edição é salva agora. Um ponto de restauração é criado antes. Mude isso depois no Início."),
                new PadOption("Cancelar", IconPath: "back", Detail: "Nada é gravado."));
            return choice == RomHackNotice.EditOption;
        };
        _sprites = sprites;
        _theme = theme;
        BindingContext = viewModel;
        Title = "Armazenamento";
        NavigationPage.SetHasNavigationBar(this, false);

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _frame = new FrameInvalidator(_canvas);
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        // The box header rides above the well: the slanted name banner between two chevrons.
        // Touch mirrors the games: the chevrons page the boxes, the name opens the box manager.
        _boxBar = new SKCanvasView { HeightRequest = Design(HeaderDesignHeight), Margin = new Thickness(0, 0, 0, Design(28)) };
        _boxBar.PaintSurface += PaintBoxBar;
        var boxBarTap = new TapGestureRecognizer();
        boxBarTap.Tapped += (_, args) => TapBoxBar(args.GetPosition(_boxBar)?.X ?? _boxBar.Width / 2);
        _boxBar.GestureRecognizers.Add(boxBarTap);

        // In the box manager the three cards are touch targets: neighbors browse (or swap
        // the held box), the middle card marks the box.
        _boxNeighbors = new SKCanvasView { HeightRequest = 100, IsVisible = false };
        _boxNeighbors.PaintSurface += PaintBoxNeighbors;
        var neighborTap = new TapGestureRecognizer();
        neighborTap.Tapped += (_, args) => TapBoxNeighbors(args.GetPosition(_boxNeighbors)?.X ?? _boxNeighbors.Width / 2);
        _boxNeighbors.GestureRecognizers.Add(neighborTap);

        var screenBody = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star)],
            Children = { _boxBar, _boxNeighbors, _canvas },
        };
        Grid.SetRow(_boxNeighbors, 1);
        Grid.SetRow(_canvas, 2);

        // The grid canvas paints its own well (BoxGridRenderer), the party its own deck.
        var screen = screenBody;

        _sidePanel = BuildSidePanel();

        // DS chrome around the box grid + editor.
        _storageContent = new Grid
        {
            Padding = new Thickness(Design(48), Design(32), Design(28), Design(34)),
            ColumnSpacing = PanelSpacing,
            ColumnDefinitions = [new(new GridLength(1072, GridUnitType.Star)), new(PanelWidth)],
            Children = { screen, _sidePanel },
        };
        Grid.SetColumn(_sidePanel, 1);
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), _storageContent } };

        var title = string.IsNullOrEmpty(_viewModel.ConnectedName) ? "Armazenamento" : _viewModel.ConnectedName;
        _footerHost = new ContentView();
        SetStorageFooter();

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip(title, "Conectado"), bodyHost, _footerHost },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(bodyHost, 2);
        Grid.SetRow(_footerHost, 3);

        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;

        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.Save) or nameof(BoxBrowserViewModel.BoxIndex)
                or nameof(BoxBrowserViewModel.SelectedSlot) or nameof(BoxBrowserViewModel.VisibleSlots))
            {
                // The locks follow the box's contents, not the cursor.
                if (args.PropertyName is not nameof(BoxBrowserViewModel.SelectedSlot)) RefreshLockedSlots();
                _canvas.InvalidateSurface();
                _boxBar.InvalidateSurface();
            }
            // The footer's labels follow the selection ("Select box" / "Unmark box", the count).
            if (args.PropertyName is nameof(BoxBrowserViewModel.MarkedCount) or nameof(BoxBrowserViewModel.SelectMode)
                or nameof(BoxBrowserViewModel.BoxIndex) or nameof(BoxBrowserViewModel.VisibleSlots))
            {
                _boxBar.InvalidateSurface();
                if (_viewModel.SelectMode && !_boxManageMode && !_editorFocusMode) SetSelectFooter();
            }
            if (args.PropertyName is nameof(BoxBrowserViewModel.CarrySource) && !_viewModel.SelectMode && !_boxManageMode && !_editorFocusMode)
                SetStorageFooter(); // Grab ↔ Place, Back ↔ Cancel
        };
        theme.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ThemeService.SkAccent))
                _canvas.InvalidateSurface();
        };
    }

    /// <summary>Draws the box header: the name banner, and a chevron each way when a box is there.</summary>
    private void PaintBoxBar(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var bounds = new SKRect(0, 0, args.Info.Width, args.Info.Height);
        var unit = args.Info.Height / HeaderDesignHeight;
        var fontSize = 34f * unit;
        if (_boxManageMode && _boxHeld)
        {
            var phase = (Environment.TickCount64 % 900) / 900d * Math.PI * 2;
            var breath = (float)((Math.Sin(phase) + 1) * 0.5);
            bounds.Inflate(-2f - breath * 3f, -1f - breath);
            fontSize += breath * 1.5f;
        }
        using var font = new SKFont(PixelTypeface(), fontSize);
        // The game's own box name (PKHeX IBoxDetailName; "BOX 01" style when the save has none).
        var boxName = _viewModel.BoxIndex == -1 ? "Equipe"
            : _sessionsFor() is { } headerSession && _viewModel.BoxIndex < _viewModel.BoxCount
                ? headerSession.GetBoxName(_viewModel.BoxIndex)
                : $"Box {_viewModel.BoxIndex + 1:00}";
        if (_boxManageMode) boxName = _boxHeld ? $"Segurando · {boxName}" : $"Gerenciar · {boxName}";
        else if (_viewModel.SelectMode) boxName = $"{boxName} · marcados {_viewModel.MarkedInCurrentBox}/{_viewModel.MarkedCount}";
        StoragePaint.BoxHeader(canvas, bounds, boxName, font,
            canPrev: _viewModel.BoxIndex > 0,
            canNext: _viewModel.BoxIndex < _viewModel.BoxCount - 1, unit);
    }

    /// <summary>The header's height in the 1920×1080 mockup: its unit is its height over this.</summary>
    private const float HeaderDesignHeight = 72f;

    /// <summary>A compact three-box map keeps both neighbors understandable while ordering.</summary>
    private void PaintBoxNeighbors(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(Pksm.Housing);
        var session = _sessionsFor();
        if (!_boxManageMode || session is null) return;
        using var label = new SKFont(PixelTypeface(), 13);
        var gap = 10f;
        var cardWidth = (args.Info.Width - gap * 4) / 3f;
        for (var offset = -1; offset <= 1; offset++)
        {
            var box = _viewModel.BoxIndex + offset;
            var left = gap + (offset + 1) * (cardWidth + gap);
            var rect = new SKRect(left, 3, left + cardWidth, args.Info.Height - 3);
            if ((uint)box >= (uint)_viewModel.BoxCount) continue;
            var locked = DocumentId is not null && Protection.IsBoxLocked(DocumentId!, box);
            using var background = new SKPaint { Color = BoxGridRenderer.WallpaperAt(box), IsAntialias = true };
            using var border = new SKPaint
            {
                Color = locked ? Pksm.Illegal : _markedBoxes.Contains(box) ? Pksm.SelectBorder : offset == 0 ? Pksm.ShinyGold : SKColors.White,
                Style = SKPaintStyle.Stroke, StrokeWidth = offset == 0 ? 4 : 2, IsAntialias = true,
            };
            canvas.DrawRoundRect(rect, 7, 7, background);
            canvas.DrawRoundRect(rect, 7, 7, border);
            PksmPaint.CenterText(canvas, $"{(offset < 0 ? "L  " : offset > 0 ? "R  " : "")}{session.GetBoxName(box)}",
                rect.MidX, rect.Top + 13, label, SKColors.White, Pksm.WallpaperShade(background.Color), SKTextAlign.Center);
            if (locked)
                PksmPaint.CenterText(canvas, "LOCK", rect.Right - 22, rect.Bottom - 8, label,
                    SKColors.White, Pksm.Illegal, SKTextAlign.Center);

            var slots = Enumerable.Range(0, BoxGridRenderer.Columns * BoxGridRenderer.Rows)
                .Select(slot => session.ReadEntity(box, slot)).ToArray();
            var dotW = (rect.Width - 12) / BoxGridRenderer.Columns;
            var dotH = (rect.Height - 24) / BoxGridRenderer.Rows;
            using var occupied = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };
            using var shiny = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };
            for (var slot = 0; slot < slots.Length; slot++)
            {
                if (slots[slot].IsEmpty) continue;
                var x = rect.Left + 6 + (slot % BoxGridRenderer.Columns + 0.5f) * dotW;
                var y = rect.Top + 21 + (slot / BoxGridRenderer.Columns + 0.5f) * dotH;
                var radius = Math.Min(dotW, dotH) * 0.42f;
                var sprite = _sprites.GetSprite(slots[slot].Look);
                if (sprite is null)
                {
                    _sprites.Warm(slots[slot].Look, _boxNeighbors.InvalidateSurface);
                    canvas.DrawCircle(x, y, radius * 0.65f, slots[slot].IsShiny ? shiny : occupied);
                    continue;
                }
                using var image = SKImage.FromBitmap(sprite);
                var scale = Math.Min(radius * 2 / image.Width, radius * 2 / image.Height);
                var width = image.Width * scale;
                var height = image.Height * scale;
                canvas.DrawImage(image, new SKRect(x - width / 2, y - height / 2, x + width / 2, y + height / 2), BoxGridRenderer.SpriteSampling);
            }
        }
    }

    // ── Cursor modes ──────────────────────────────────────────────────────────
    // Like the games' PC: the red hand moves one Pokémon, the green hand marks many.
    // Select (-) flips between them from anywhere on the grid; the cursor colour, the
    // box bar's mark counter and this footer always say which hand is active.

    private void SetStorageFooter() => _footerHost.Content = DsChrome.Footer(
        ("A", _viewModel.CarrySource is null ? "Pegar" : "Colocar", null),
        ("-", "Multi-select", EnterSelectMode),
        ("LR", "Box", () => OnPadButton(PadButton.R)),
        ("X", "Ferramentas", () => _ = ShowToolsAsync()), ("Y", "Dados do save", () => _ = ShowSaveDataAsync()),
        ("+", "Menu", () => OpenCursorMenu()),
        ("B", _viewModel.CarrySource is null ? "Voltar" : "Cancelar", () => OnPadButton(PadButton.B)));

    private void SetSelectFooter() => _footerHost.Content = DsChrome.Footer(
        ("A", "Marcar · segure para área", () => { OnPadButton(PadButton.A); OnPadReleased(PadButton.A); }),
        ("Y", _viewModel.CurrentBoxFullyMarked ? "Desmarcar box" : "Selecionar box", () => OnPadButton(PadButton.Y)),
        ("X", $"Ações ({_viewModel.MarkedCount})", () => _ = ShowOrganizerMenuAsync()),
        ("+", "Boxes", EnterBoxManageMode),
        ("LR", "Box", () => OnPadButton(PadButton.R)),
        ("-", "Modo mover", ExitSelectMode),
        ("B", _viewModel.MarkedCount > 0 ? "Limpar" : "Pronto", () => OnPadButton(PadButton.B)));

    /// <summary>The footer for whichever mode is active; every mode change ends here.</summary>
    private void RefreshFooter()
    {
        if (_boxManageMode) SetBoxManageFooter();
        else if (_editorFocusMode) SetEditorFooter();
        else if (_viewModel.SelectMode) SetSelectFooter();
        else SetStorageFooter();
    }

    private void EnterSelectMode()
    {
        if (_boxManageMode || _editorFocusMode || _viewModel.Save is null) return;
        _selectHeld = false;
        _viewModel.EnterSelectMode();
        if (_viewModel.SelectedSlot < 0) _viewModel.SelectSlot(0);
        RepaintStorage();
    }

    private void ExitSelectMode()
    {
        _selectHeld = false;
        _viewModel.ExitSelectMode();
        ShowReady();
        RepaintStorage();
    }

    private void RepaintStorage()
    {
        _canvas.InvalidateSurface();
        _boxBar.InvalidateSurface();
        RefreshFooter();
    }

    /// <summary>Box bar touch: the chevron caps page the boxes, the name opens the box manager.</summary>
    private void TapBoxBar(double x)
    {
        if (_boxManageMode || _editorFocusMode || _viewModel.Save is null) return;
        // The chevrons own the ends of the header, up to where the banner starts.
        var cap = Math.Max(44, StoragePaint.BannerRect(new SKRect(0, 0, (float)_boxBar.Width, (float)_boxBar.Height),
            (float)_boxBar.Height / HeaderDesignHeight).Left);
        if (x < cap) OnPadButton(PadButton.L);
        else if (x > _boxBar.Width - cap) OnPadButton(PadButton.R);
        else EnterBoxManageMode();
    }

    private void TapBoxNeighbors(double x)
    {
        if (!_boxManageMode) return;
        var third = _boxNeighbors.Width / 3;
        OnPadButton(x < third ? PadButton.L : x > third * 2 ? PadButton.R : PadButton.Y);
    }

    private void SetEditorFooter() => _footerHost.Content = DsChrome.Footer(
        ("↑↓", "Navegar", null),
        ("A", "Usar campo", () => ActivateEditorFocus()),
        ("B", "Box", ExitEditorFocusMode),
        ("+", "Box", ExitEditorFocusMode));

    private void SetBoxManageFooter() => _footerHost.Content = DsChrome.Footer(
        ("A", _boxHeld ? "Soltar box" : "Segurar box", () => OnPadButton(PadButton.A)),
        ("B", "Pronto", ExitBoxManageMode),
        ("LR", _boxHeld ? "Trocar" : "Navegar", null),
        ("Y", _markedBoxes.Contains(_viewModel.BoxIndex) ? "Desmarcar" : "Selecionar", ToggleMarkedBox),
        ("X", "Ações da box", () => _ = ShowBoxBulkActionsAsync()));

    // ── Hardcore mode ─────────────────────────────────────────────────────────
    // One guard per surface, never a condition buried inside a flow. Allowed()/Menu()
    // decide what a menu may offer, so a blocked entry is simply absent and comes back
    // the moment the mode is turned off; Denied()/ReportHardcoreAsync() stop a flow
    // that was reached some other way (a panel button, a touch target) with the reason
    // on screen; and every save write still passes the guard in
    // BoxBrowserViewModel.RunMutationAsync.

    private static SaveGuard Guard => HardcoreMode.Guard;

    /// <summary>A menu entry that exists only while Hardcore mode allows its action; null hides it.</summary>
    private static PadOption? Allowed(SaveAction action, PadOption option) =>
        Guard.Allows(action) ? option : null;

    /// <summary>The menu's options without the entries Hardcore mode refuses.</summary>
    private static PadOption[] Menu(params PadOption?[] options) =>
        options.Where(option => option is not null).Select(option => option!).ToArray();

    /// <summary>Menu copy: what the surface wanted to say, plus the Hardcore line while it is on.</summary>
    private static string? Note(string? message) =>
        HardcoreMode.IsOn
            ? string.IsNullOrEmpty(message) ? HardcoreMode.StatusLine : $"{message}\n{HardcoreMode.StatusLine}"
            : message;

    /// <summary>True when Hardcore mode refuses <paramref name="action"/>; the reason lands in the status strip.</summary>
    private bool Denied(SaveAction action)
    {
        if (!HardcoreMode.Blocks(action, out var status)) return false;
        _viewModel.Status = status;
        return true;
    }

    /// <summary>The same refusal for a flow with no menu to filter; the reason is shown, never swallowed.</summary>
    private async Task<bool> DeniedAsync(SaveAction action)
    {
        if (!Denied(action)) return false;
        await PadMenu.ShowAsync(_hostGrid, "Modo Hardcore", _viewModel.Status, "OK");
        return true;
    }

    /// <summary>The idle status line, carrying the marker so the mode is visible on the screen it gates.</summary>
    private void ShowReady() => _viewModel.Status = HardcoreMode.IsOn
        ? $"Pronto · {HardcoreMode.StatusLine}"
        : LayoutMarker ?? "Pronto";

    // ── Layout risk (ROM hacks on a retail layout) ────────────────────────────
    // A suspected hack warns once per save and stays read-only unless the player picks
    // "Edit at my own risk"; a corrupting layout is always read-only. Either way the strip
    // carries a marker for as long as the save is open.

    private string? _layoutCheckedFor;
    private LayoutRiskKind _layoutRisk;
    private static readonly HashSet<string> CorruptingExplained = new(StringComparer.Ordinal);

    /// <summary>The strip marker for the open save's layout verdict; re-read on every use so
    /// a choice made on Home shows the moment the box comes back.</summary>
    private string? LayoutMarker => _layoutRisk switch
    {
        LayoutRiskKind.SuspectedHack => _viewModel.OpenSaveIsReadOnly ? RomHackNotice.ReadOnlyStatus : RomHackNotice.Marker,
        LayoutRiskKind.CorruptingLayout => "SOMENTE LEITURA · o formato deste save não é suportado",
        _ => null,
    };

    private async Task CheckLayoutRiskAsync()
    {
        var documentId = DocumentId;
        if (documentId is null) { _layoutRisk = LayoutRiskKind.None; return; }
        if (documentId != _layoutCheckedFor)
        {
            _layoutCheckedFor = documentId;
            _layoutRisk = LayoutRiskKind.None;
            LayoutRisk? risk;
            try { risk = await _viewModel.AssessOpenSaveLayoutAsync(); }
            catch (Exception) { return; } // the writer re-judges on every write; the notice is advisory
            if (DocumentId != documentId) return;
            _layoutRisk = risk?.Kind ?? LayoutRiskKind.None;
            await ExplainLayoutRiskAsync(documentId, risk);
            if (DocumentId != documentId) return;
        }
        if (LayoutMarker is { } marker && !HardcoreMode.IsOn) _viewModel.Status = marker;
    }

    private async Task ExplainLayoutRiskAsync(string documentId, LayoutRisk? risk)
    {
        switch (risk?.Kind)
        {
            case LayoutRiskKind.SuspectedHack when !RomHackNotice.IsFlagged(documentId):
                RomHackNotice.MarkFlagged(documentId);
                if (!_viewModel.OpenSaveIsReadOnly) return; // already accepted (e.g. on Home)
                var choice = await PadMenu.ShowAsync(_hostGrid, RomHackNotice.Title, RomHackNotice.Warning,
                    new PadOption(RomHackNotice.ReadOnlyOption, IconPath: "box",
                        Detail: "Veja as boxes e a equipe. Nada é gravado."),
                    new PadOption(RomHackNotice.EditOption, Glyph: "!", Accent: UiTokens.Bad,
                        Detail: "Os pontos de restauração continuam sendo criados. Mude isso depois no Início."));
                if (choice == RomHackNotice.EditOption) _viewModel.AcceptHackRisk();
                break;
            case LayoutRiskKind.CorruptingLayout when CorruptingExplained.Add(documentId):
                await PadMenu.ShowAsync(_hostGrid, RomHackNotice.CorruptingTitle,
                    $"O PKForge pode mostrar este save, mas não vai gravá-lo: {risk.Reason}\n" +
                    "Gravá-lo com o formato do jogo original quebraria o arquivo, então não há como editá-lo nem por sua conta e risco.", "OK");
                break;
        }
    }

    /// <summary>The NDS12 face (the chrome's PixelUI voice), cached once for Skia text.</summary>
    private static SKTypeface _pixelTypeface = null!;

    internal static SKTypeface PixelTypeface()
    {
        if (_pixelTypeface is not null) return _pixelTypeface;
        try
        {
            // Bundled font copied to cache once (the ball-icon pattern), then opened by path.
            var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, "NDS12.ttf");
            if (!File.Exists(cache))
            {
                using var asset = FileSystem.OpenAppPackageFileAsync("NDS12.ttf").GetAwaiter().GetResult();
                using var output = File.Create(cache);
                asset.CopyTo(output);
            }
            _pixelTypeface = SKTypeface.FromFile(cache);
        }
        catch
        {
            // ignored: fall back to the bold system face below
        }
        _pixelTypeface ??= SKTypeface.FromFamilyName(null, SKFontStyle.Bold);
        return _pixelTypeface;
    }


    // The box and the editor panel split the width as the mockup does: 1072 to 740.
    private static readonly GridLength PanelWidth = new(740, GridUnitType.Star);
    private static readonly double PanelSpacing = Design(32);

    private View BuildSidePanel()
    {
        // Idle card until a Pokémon is selected; the editor replaces it.
        var idle = new VerticalStackLayout
        {
            Spacing = 8,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                PksmIcons.Icon("storage", 44),
                new Label { Text = "Selecione um Pokémon", TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = 15 },
                new Label { Text = "Toque num slot vazio para adicionar um", TextColor = UiTokens.Ink1, FontSize = UiTokens.TextSmall, HorizontalTextAlignment = TextAlignment.Center },
            },
        };

        _editorScroll = new ScrollView { Content = BuildEditor(), IsVisible = false };
        var editor = _editorScroll;

        void SwapPanels()
        {
            var hasSelection = _viewModel.Selected is { IsEmpty: false };
            editor.IsVisible = hasSelection;
            idle.IsVisible = !hasSelection;
        }
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.Selected))
                SwapPanels();
        };
        SwapPanels();

        return EditorShell(new Grid { Children = { idle, editor } });
    }

    private void EnterEditorFocusMode()
    {
        if (_viewModel.Selected is null) return;
        _editorFocusMode = true;
        _editorFocusIndex = 0;
        SetEditorFooter();
        UpdateEditorFocusVisuals();
        _viewModel.Status = $"Foco no editor - {EditorFocusTargets[_editorFocusIndex].Caption} · A usar · B box";
    }

    private void ExitEditorFocusMode()
    {
        _editorFocusMode = false;
        UpdateEditorFocusVisuals();
        RefreshFooter();
        ShowReady();
    }

    private void MoveEditorFocus(int delta)
    {
        var current = EditorFocusTargets[_editorFocusIndex];
        if (current.Neighbors is { } neighbors)
        {
            _editorFocusIndex = delta < 0 ? neighbors.Up : neighbors.Down;
        }
        else
        {
            _editorFocusIndex = (_editorFocusIndex + delta + EditorFocusTargets.Length) % EditorFocusTargets.Length;
        }
        UpdateEditorFocusVisuals();
        _viewModel.Status = $"Foco no editor - {EditorFocusTargets[_editorFocusIndex].Caption} · A usar · B box";
    }

    private void MoveEditorFocusHorizontal(int delta)
    {
        var current = EditorFocusTargets[_editorFocusIndex];
        if (current.Neighbors is { } neighbors)
        {
            _editorFocusIndex = delta < 0 ? neighbors.Left : neighbors.Right;
            UpdateEditorFocusVisuals();
            _viewModel.Status = $"Foco no editor - {EditorFocusTargets[_editorFocusIndex].Caption} · A usar · B box";
            return;
        }

        if (current.NumericBindingPath is { } bindingPath)
        {
            var value = int.TryParse(GetVmString(bindingPath), out var level) ? level : 0;
            SetVmString(bindingPath, Math.Clamp(value + delta, 1, 100).ToString());
        }
    }

    private void ActivateEditorFocus()
    {
        _ = EditorFocusTargets[_editorFocusIndex].Activate();
    }

    private void UpdateEditorFocusVisuals()
    {
        foreach (var target in EditorFocusTargets)
        {
            switch (target.View)
            {
                case Border border:
                    // Rows rest on their own band; the focus look is painted on top.
                    border.Background = null;
                    border.BackgroundColor = target.OriginalBackground ?? Colors.Transparent;
                    border.Stroke = Colors.Transparent;
                    border.ClearValue(VisualElement.ShadowProperty);
                    SetFocusedCaptions(border, false);
                    break;
                case Button button:
                    button.Background = null;
                    Kit.SetButtonFocus(button, false, target.OriginalBackground, target.OriginalTextColor);
                    if (target.OriginalBorder is not null) button.BorderColor = target.OriginalBorder;
                    break;
            }
        }

        if (!_editorFocusMode) return;
        var focused = EditorFocusTargets[_editorFocusIndex];
        switch (focused.View)
        {
            case Border border:
                // The section chip's look: its blue gradient, a thin cyan rim, a pale caption.
                border.Background = EditorFocusBrush;
                border.Stroke = EditorPaint.Cyan.ToMauiColor();
                SetFocusedCaptions(border, true);
                break;
            case Button button:
                button.Background = EditorFocusBrush;
                button.BorderColor = EditorPaint.Cyan.ToMauiColor();
                button.TextColor = EditorPaint.ChipInk.ToMauiColor();
                break;
        }

        if (_editorScroll is not null)
            _ = _editorScroll.ScrollToAsync(focused.View, ScrollToPosition.MakeVisible, false);
    }

    private sealed record EditorFocusTarget(View View, string Caption, Func<Task> Activate,
        string? NumericBindingPath = null, Color? OriginalBackground = null, Color? OriginalTextColor = null,
        EditorFocusNeighbors? Neighbors = null, Color? OriginalBorder = null);

    private sealed record EditorFocusNeighbors(int Left, int Right, int Up, int Down);

    private async Task ShowToolsAsync()
    {
        if (_viewModel.SelectMode)
        {
            await ShowOrganizerMenuAsync();
            return;
        }
        // Box-level bulk tools run on the stock engine only: romhack sessions refuse
        // sort/batch ops, so their entries are hidden instead of failing mid-flow.
        var boxTools = _sessionsFor()?.SupportsBoxTools == true;
        var options = new List<PadOption>
        {
            new("Seleção múltipla (botão -)", IconPath: "select", Detail: "Marque vários Pokémon para mover, copiar ou editar juntos."),
            new("Gerenciador de boxes…", IconPath: "box", Detail: "Reordene, copie, trave ou esvazie boxes inteiras."),
        };
        options.AddRange(Menu(
            Allowed(SaveAction.CreateMon, new("Importar arquivos .pk", IconPath: "import", Detail: "Escolha arquivos de Pokémon para preencher os slots vazios desta box.")),
            Allowed(SaveAction.CreateMon, new("Importar time do Showdown", IconPath: "script", Detail: "Cole sets do Showdown, veja a legalidade deles e coloque-os em slots vazios.")),
            new("Exportar box para o Showdown", IconPath: "script", Detail: "Esta box como texto do Showdown, para copiar ou compartilhar."),
            Allowed(SaveAction.CreateMon, new("Gerar Living Dex", IconPath: "create", Detail: "Substitui todas as boxes por um Pokémon legal de cada espécie.")),
            new("Como obter um Pokémon…", IconPath: "map", Detail: "Como obter uma espécie no jogo que você está editando."),
            new("Encontrar item segurado…", IconPath: "item", Detail: "Veja quais Pokémon seguram um item e vá até um deles."),
            new("Ranking de IVs…", IconPath: "stats", Detail: "Todos os Pokémon ordenados pelos IVs; escolha um para ir até ele."),
            Allowed(SaveAction.CreateMon, new("Fábrica de ovos…", IconPath: "egg", Detail: "Preencha slots vazios do PC com ovos: um de cada espécie, ou de uma só espécie.")),
            new("Creche / Berçário", IconPath: "daycare", Detail: "Veja quem está na Creche e retire-os para o PC.")));
        if (_sessionsFor() is { } honeySession && PKForge.Engine.HoneyTreeService.IsSupported(honeySession))
            options.Add(new("Árvores de mel", IconPath: "tree", Detail: "Veja e altere as árvores de mel, incluindo suas árvores de Munchlax."));
        if (boxTools)
        {
            options.AddRange(Menu(
                Allowed(SaveAction.BatchEdit, new("Preparar para batalha", IconPath: "battle", Detail: "Cure, maximize PP ou defina níveis para a equipe ou todas as boxes.")),
                Allowed(SaveAction.BatchEdit, new("Editor em lote", IconPath: "batch", Detail: "Altere nível, IVs, EVs, cura e mais em vários Pokémon de uma vez.")),
                Allowed(SaveAction.BatchEdit, new("Renomear em lote / TO…", IconPath: "rename", Detail: "Restaure apelidos, ou torne-se o treinador original (TO), em vários Pokémon.")),
                Allowed(SaveAction.CreateMon, new("Time aleatório…", IconPath: "dice", Detail: "Sorteie um time legal aleatório para a equipe ou slots vazios das boxes."))));
        }
        options.AddRange(Menu(
            Allowed(SaveAction.BatchEdit, new("Predefinições…", IconPath: "preset", Detail: "Uma mudança, como Nível 100 ou IVs perfeitos, em uma box ou todas.")),
            new("Perfis de treinador…", IconPath: "profile", Detail: "Salve dados de treinador e aplique-os aos Pokémon como treinador original."),
            new("Verificação de legalidade", IconPath: "check", Detail: "Verifique todos os Pokémon do save e conserte os ilegais."),
            new("Relatório de auditoria", IconPath: "report", Detail: "Encontre Pokémon clonados e valores impossíveis no save inteiro."),
            new("Relatório de Nuzlocke", IconPath: "skull", Detail: "A primeira captura de cada rota, a partir de onde cada Pokémon foi encontrado."),
            new("Dex da coleção…", IconPath: "pokedex", Detail: "Rastreador de Living Dex do Banco e deste save, normal e shiny.")));
        if (boxTools)
            options.Add(new("Ordenar boxes…", IconPath: "sort", Detail: "Ordene esta box ou todas por número da dex, nível, tipo e mais."));
        var choice = await PadMenu.ShowAsync(_hostGrid, "Ferramentas de armazenamento", Note(null), options.ToArray());
        switch (choice)
        {
            case "Seleção múltipla (botão -)":
                EnterSelectMode();
                return;
            case "Importar arquivos .pk":
                await BulkImportAsync();
                return;
            case "Importar time do Showdown":
                await ImportShowdownTeamAsync();
                return;
            case "Exportar box para o Showdown":
                await ExportBoxShowdownAsync();
                return;
            case "Gerar Living Dex":
                await GenerateLivingDexAsync();
                return;
            case "Como obter um Pokémon…":
            {
                // Game-scoped on purpose: this is "how do I get it in the game I'm editing".
                // The all-games answer lives in the bank/living dex.
                var session = _sessionsFor();
                if (session is not null)
                    await EncounterGallery.ShowGameScopedAsync(_hostGrid, _viewModel, session, () => _canvas.InvalidateSurface());
                return;
            }
            case "Encontrar item segurado…":
                await FindHeldItemAsync();
                return;
            case "Ranking de IVs…":
                await ShowIvRankingAsync();
                return;
            case "Preparar para batalha":
                await ShowBattlePrepAsync();
                return;
            case "Fábrica de ovos…":
                await ShowEggFactoryAsync();
                return;
            case "Árvores de mel":
                {
                    var session = _sessionsFor();
                    if (session is not null)
                        await HoneyTreeEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
            case "Creche / Berçário":
                {
                    var session = _sessionsFor();
                    if (session is not null)
                        await DaycareEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
            case "Editor em lote":
                await RunBatchEditorAsync();
                return;
            case "Renomear em lote / TO…":
                await ShowBatchRenameAsync();
                return;
            case "Time aleatório…":
                await ShowRandomTeamAsync();
                return;
            case "Predefinições…":
                await ShowPresetsMenuAsync();
                return;
            case "Perfis de treinador…":
                await ShowTrainerProfilesAsync();
                return;
            case "Verificação de legalidade":
                await ShowLegalityCheckAsync();
                return;
            case "Relatório de auditoria":
                await ShowAuditReportAsync();
                return;
            case "Relatório de Nuzlocke":
                await ShowNuzlockeReportAsync();
                return;
            case "Gerenciador de boxes…":
                EnterBoxManageMode();
                return;
            case "Ordenar boxes…":
                await ShowSortMenuAsync();
                return;
            case "Dex da coleção…":
            {
                var services = IPlatformApplication.Current?.Services;
                var data = services?.GetService<IGameDataService>();
                var sprites = services?.GetService<ISpriteService>();
                if (data is not null && sprites is not null)
                    await CollectionDexPage.ShowAsync(_hostGrid, _viewModel, data, sprites);
                return;
            }
        }
    }

    /// <summary>Game-style box ordering lives directly in storage: the current box is
    /// picked up, breathes in the normal box bar, and L/R trades it with its neighbor.</summary>
    private void EnterBoxManageMode()
    {
        if (_viewModel.BoxCount == 0) return;
        if (_viewModel.BoxIndex < 0) _viewModel.BoxIndex = 0;
        _boxManageMode = true;
        _boxHeld = false;
        _heldBox = _viewModel.BoxIndex;
        _slotBeforeBoxManage = _viewModel.SelectedSlot;
        _viewModel.SelectedSlot = -1;
        _markedBoxes.Clear();
        // Multi-select marks survive the trip: the box manager can add whole boxes to them.
        if (_selectHeld) { _selectHeld = false; _viewModel.CommitRectangle(); }
        _viewModel.CancelCarry();
        _viewModel.Status = "Gerenciador de boxes - A segurar · L/R navegar · Y marcar box · X ações";
        _sidePanel.IsVisible = false;
        _storageContent.ColumnDefinitions[1].Width = new GridLength(0);
        _storageContent.ColumnSpacing = 0;
        _canvas.EnableTouchEvents = false;
        _boxNeighbors.IsVisible = true;
        _boxNeighbors.HeightRequest = 100;
        SetBoxManageFooter();
        if (_boxManagePulseTimer is null)
        {
            _boxManagePulseTimer = Dispatcher.CreateTimer();
            _boxManagePulseTimer.Interval = TimeSpan.FromMilliseconds(60);
            _boxManagePulseTimer.Tick += BoxManagePulse;
        }
        _boxBar.InvalidateSurface();
    }

    private void BoxManagePulse(object? sender, EventArgs args) => _boxBar.InvalidateSurface();

    private void UpdateBoxManagePulse()
    {
        if (_boxManageMode && _boxHeld) _boxManagePulseTimer?.Start();
        else _boxManagePulseTimer?.Stop();
    }

    private void ExitBoxManageMode()
    {
        if (!_boxManageMode) return;
        _boxManageMode = false;
        _boxHeld = false;
        _markedBoxes.Clear();
        _boxManagePulseTimer?.Stop();
        _boxNeighbors.IsVisible = false;
        _canvas.EnableTouchEvents = true;
        _sidePanel.IsVisible = true;
        _storageContent.ColumnDefinitions[1].Width = PanelWidth;
        _storageContent.ColumnSpacing = PanelSpacing;
        _viewModel.SelectedSlot = _slotBeforeBoxManage;
        if (_viewModel.SelectMode) _viewModel.Status = $"Seleção múltipla - {_viewModel.MarkedCount} marcados";
        else ShowReady();
        RepaintStorage();
    }

    private readonly CarryHand _hand = new();

    // A held box moves by writing the save: one press, one swap.
    bool IPadPagingHandler.ShouldersRepeat => !_boxHeld;

    private async Task ShiftHeldBoxAsync(int delta)
    {
        if (_boxManageBusy) return;
        var target = _heldBox + delta;
        if ((uint)target >= (uint)_viewModel.BoxCount)
        {
            _viewModel.Status = delta < 0 ? "A box já é a primeira" : "A box já é a última";
            return;
        }

        _boxManageBusy = true;
        try
        {
            var from = _heldBox;
            var ok = await _viewModel.RunMutationAsync(session =>
            {
                session.SwapBoxes(from, target);
                return new GenerationOutcome(true, $"Box {from + 1:00} trocada com a box {target + 1:00}.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
            if (!ok) return;
            _heldBox = target;
            _viewModel.BoxIndex = target;
            _viewModel.RefreshAllSlots();
            _viewModel.Status = $"Segurando a box {_heldBox + 1:00} - L/R trocar · A soltar";
            _canvas.InvalidateSurface();
            _boxBar.InvalidateSurface();
            _boxNeighbors.InvalidateSurface();
        }
        finally { _boxManageBusy = false; }
    }

    private void BrowseManagedBoxes(int delta)
    {
        var target = _viewModel.BoxIndex + delta;
        if ((uint)target >= (uint)_viewModel.BoxCount) return;
        _viewModel.BoxIndex = target;
        _heldBox = target;
        SetBoxManageFooter();
        _boxNeighbors.InvalidateSurface();
    }

    private void ToggleMarkedBox()
    {
        var box = _viewModel.BoxIndex;
        if (!_markedBoxes.Remove(box)) _markedBoxes.Add(box);
        _viewModel.Status = _markedBoxes.Count == 0
            ? "GERENCIADOR DE BOXES - nenhuma box selecionada"
            : $"Gerenciador de boxes - {_markedBoxes.Count} box(es) selecionada(s)";
        SetBoxManageFooter();
        _boxNeighbors.InvalidateSurface();
    }

    private async Task ShowBoxBulkActionsAsync()
    {
        if (_boxHeld || _boxManageBusy) return;
        var selected = _markedBoxes.Count == 0 ? new[] { _viewModel.BoxIndex } : _markedBoxes.OrderBy(x => x).ToArray();
        var noun = selected.Length == 1 ? $"box {selected[0] + 1:00}" : $"{selected.Length} boxes selecionadas";
        var emptyLabel = _markedBoxes.Count > 0 ? "Esvaziar boxes selecionadas" : "Esvaziar todas as boxes";
        var choice = await PadMenu.ShowAsync(_hostGrid, $"Ações da box · {noun}",
            Note("Sem seleção, vale a box atual. Cada gravação cria um ponto de restauração."),
            Menu(
                new PadOption("Selecionar Pokémon nestas boxes", IconPath: "select", Detail: "Marca todos os Pokémon destas boxes na seleção múltipla."),
                new PadOption("Marcar todas as boxes", IconPath: "selectall", Detail: "Seleciona todas as boxes para as ações deste menu."),
                new PadOption("Travar / Destravar box(es)", IconPath: "padlock", Detail: "Boxes travadas são ignoradas na ordenação, edições em lote e esvaziamento."),
                Allowed(SaveAction.Duplicate, new("Copiar box(es)…", IconPath: "copy", Detail: "Copia estas boxes por cima de outras, substituindo o que houver lá.")),
                new PadOption("Apagar box(es) (resgatar Pokémon)", IconPath: "delete", Detail: "Remove as boxes; os Pokémon delas vão antes para slots livres."),
                new PadOption(emptyLabel, IconPath: "clear", Detail: "Solta todos os Pokémon das boxes. A equipe não é afetada."),
                new PadOption("Limpar marcações de box", IconPath: "deselect"),
                // Name/wallpaper act on the box under the cursor (PKHeX SAV_BoxLayout edits one box at a time).
                _sessionsFor() is { } layoutSession && Engine.BoxLayoutService.SupportsNames(layoutSession)
                    ? new PadOption(BoxLayoutEditor.RenameLabel, IconPath: "rename") : null,
                _sessionsFor() is { } wallSession && Engine.BoxLayoutService.SupportsWallpapers(wallSession)
                    ? new PadOption(BoxLayoutEditor.WallpaperLabel, IconPath: "box") : null));
        if ((choice == BoxLayoutEditor.RenameLabel || choice == BoxLayoutEditor.WallpaperLabel) && _sessionsFor() is { } boxSession)
        {
            var box = _viewModel.BoxIndex;
            var changed = choice == BoxLayoutEditor.RenameLabel
                ? await BoxLayoutEditor.RenameAsync(_hostGrid, boxSession, _viewModel, box)
                : await BoxLayoutEditor.PickWallpaperAsync(_hostGrid, boxSession, _viewModel, box);
            if (changed) { _boxBar.InvalidateSurface(); _boxNeighbors.InvalidateSurface(); }
            SetBoxManageFooter();
            return;
        }
        if (choice == "Marcar todas as boxes")
        {
            _markedBoxes.Clear();
            foreach (var box in Enumerable.Range(0, _viewModel.BoxCount)) _markedBoxes.Add(box);
            _viewModel.Status = $"Gerenciador de boxes - {_markedBoxes.Count} boxes selecionadas";
            SetBoxManageFooter();
            _boxNeighbors.InvalidateSurface();
            return;
        }
        if (choice == "Selecionar Pokémon nestas boxes")
        {
            // Box marks become Pokémon marks: back on the grid in multi-select, ready for X.
            if (!_viewModel.SelectMode) _viewModel.EnterSelectMode();
            _viewModel.MarkBoxes(selected);
            ExitBoxManageMode();
            return;
        }
        if (choice == "Limpar marcações de box")
        {
            _markedBoxes.Clear();
            SetBoxManageFooter();
            _boxNeighbors.InvalidateSurface();
            return;
        }
        if (choice == "Travar / Destravar box(es)")
        {
            var docId = DocumentId;
            if (docId is null) return;
            var lockedCount = 0;
            foreach (var box in selected)
                if (Protection.ToggleBox(docId, box)) lockedCount++;
            _viewModel.Status = $"Travas atualizadas - {Protection.LockedBoxes(docId).Count} box(es) travada(s)";
            SetBoxManageFooter();
            _boxNeighbors.InvalidateSurface();
            return;
        }
        var lockedTargets = selected.Where(box => DocumentId is not null && Protection.IsBoxLocked(DocumentId!, box)).ToList();
        if ((choice == "Esvaziar boxes selecionadas" || choice == "Apagar box(es) (resgatar Pokémon)") && lockedTargets.Count > 0)
        {
            _viewModel.Status = $"{lockedTargets.Count} box(es) de destino estão travadas";
            return;
        }
        if (choice == emptyLabel && choice == "Esvaziar boxes selecionadas")
        {
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Esvaziar as boxes selecionadas?",
                $"Soltar todos os Pokémon de {selected.Length} box(es). Um ponto de restauração é criado antes.", "Esvaziar");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                foreach (var box in selected) session.ClearBox(box);
                return new GenerationOutcome(true, $"{selected.Length} box(es) esvaziada(s).");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Release);
        }
        else if (choice == "Esvaziar todas as boxes")
        {
            var docId = DocumentId;
            if (docId is not null && Protection.LockedBoxes(docId).Count > 0)
            {
                _viewModel.Status = "Boxes travadas estão protegidas - destrave ou esvazie uma por uma";
                return;
            }
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Esvaziar todas as boxes?",
                "Soltar todos os Pokémon das boxes. Os Pokémon da equipe não são afetados. Um ponto de restauração é criado antes.", "Esvaziar tudo");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                foreach (var box in Enumerable.Range(0, _viewModel.BoxCount)) session.ClearBox(box);
                return new GenerationOutcome(true, "Todas as boxes foram esvaziadas.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Release);
        }
        else if (choice == "Copiar box(es)…")
        {
            var starts = Enumerable.Range(0, _viewModel.BoxCount - selected.Length + 1)
                .Select(i => $"Começar na box {i + 1:00}").ToArray();
            var targetChoice = await PadMenu.ShowAsync(_hostGrid, "Copiar boxes", "Escolha a primeira box de destino. O conteúdo existente nela será substituído.", starts);
            var start = Array.IndexOf(starts, targetChoice);
            if (start < 0) return;
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Substituir as boxes de destino?",
                $"Copiar {selected.Length} box(es) a partir da box {start + 1:00}. O conteúdo de destino será substituído.", "Copiar");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                var copies = selected.Select(source => Enumerable.Range(0, BoxGridRenderer.Columns * BoxGridRenderer.Rows)
                    .Where(slot => !session.ReadEntity(source, slot).IsEmpty)
                    .Select(slot => (Slot: slot, Export: session.ExportSlot(source, slot))).ToArray()).ToArray();
                for (var i = 0; i < copies.Length; i++)
                {
                    session.ClearBox(start + i);
                    foreach (var copy in copies[i])
                        session.ImportSlot(start + i, copy.Slot, copy.Export.Data, copy.Export.Format);
                }
                return new GenerationOutcome(true, $"{copies.Length} box(es) copiada(s) a partir da box {start + 1:00}.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Duplicate);
        }
        else if (choice == "Apagar box(es) (resgatar Pokémon)")
        {
            var rescueNote = selected.Length >= _viewModel.BoxCount
                ? "Todas as boxes estão selecionadas: os Pokémon resgatados não têm para onde ir, então serão soltos."
                : "Os Pokémon são resgatados antes para slots livres.";
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Apagar as boxes selecionadas?",
                $"Remover {selected.Length} box(es) da ordem. {rescueNote}", "Apagar");
            if (!confirmed) return;
            await _viewModel.RunMutationAsync(session =>
            {
                foreach (var box in selected.OrderByDescending(x => x)) session.DeleteBox(box);
                return new GenerationOutcome(true, $"{selected.Length} box(es) removida(s) da ordem.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
        }
        else return;

        _markedBoxes.Clear();
        _viewModel.RefreshAllSlots();
        // Box writes move Pokémon between slots: slot marks would point at the wrong mons.
        _viewModel.ClearMarksQuietly();
        _heldBox = _viewModel.BoxIndex;
        SetBoxManageFooter();
        _canvas.InvalidateSurface();
        _boxNeighbors.InvalidateSurface();
    }

    /// <summary>The multi-select action menu (X): selection shortcuts first, then what to do
    /// with the marked Pokémon. Actions only appear once something is marked - no dead entries.</summary>
    /// <summary>
    /// "Where is my Exp. Share?": pick an item some Pokémon in this save holds (or any item),
    /// then every holder is listed with its box and slot; picking one jumps there, and
    /// "Mark all" hands them to multi-select (Batch editor → Remove held items takes them off
    /// through the dry-run + restore-point path). Read-only itself, so every engine and
    /// Hardcore mode get it; ROM-hack items without a national twin list as "Unknown item".
    /// </summary>
    private async Task FindHeldItemAsync()
    {
        var data = IPlatformApplication.Current?.Services.GetService<IGameDataService>();
        var itemNames = data is null ? [] : SaveItemNames(data);
        var all = _viewModel.AllSlots;
        var tally = HeldItemSearch.Tally(all.Where(s => s.Species is not null).Select(s => s.HeldItem));
        if (tally.Count == 0)
        {
            _viewModel.Status = "Nenhum Pokémon deste save está segurando um item";
            return;
        }
        const int AnyItem = int.MinValue;
        var directory = Path.Combine(FileSystem.AppDataDirectory, "items");
        var items = new List<PickItem> { new(AnyItem, "Qualquer item segurado", Detail: $"{tally.Sum(t => t.Count)} Pokémon") };
        foreach (var (id, n) in tally)
        {
            var name = HeldItemSearch.NameOf(itemNames, id);
            var cached = Path.Combine(directory, ItemArt.Slug(name) + ".png");
            items.Add(new PickItem(id, name, File.Exists(cached) ? cached : null, $"{n} Pokémon"));
        }
        var item = await PickerMenu.ShowAsync(_hostGrid, "Encontrar item segurado", items);
        if (item is null) return;

        var holders = HeldItemSearch.Find(all, item.Id == AnyItem ? null : item.Id);
        const int MarkAll = -1;
        var rows = new List<PickItem>
        {
            new(MarkAll, $"Marcar todos os {holders.Count} (seleção múltipla)", Detail: "Para tirá-los, use o Editor em lote e Remover itens segurados."),
        };
        for (var i = 0; i < holders.Count; i++)
        {
            var h = holders[i];
            var where = h.Box == -1 ? $"Equipe {h.Slot + 1}" : $"Box {h.Box + 1:00} · slot {h.Slot + 1:00}";
            var species = h.Species is int sp && data is not null && sp < data.SpeciesNames.Count ? data.SpeciesNames[sp] : $"#{h.Species}";
            var label = h.Nickname is { Length: > 0 } nick && !string.Equals(nick, species, StringComparison.OrdinalIgnoreCase)
                ? $"{nick} ({species})" : species;
            rows.Add(new PickItem(i, $"{where}  {label}", Detail: HeldItemSearch.NameOf(itemNames, h.HeldItem)));
        }
        var pick = await PickerMenu.ShowAsync(_hostGrid, $"{item.Name} · {holders.Count} portador(es)", rows);
        if (pick is null) return;
        if (pick.Id == MarkAll)
        {
            if (!_viewModel.SelectMode) EnterSelectMode();
            _viewModel.MarkSlots(holders.Select(h => (h.Box, h.Slot)).ToList());
            _viewModel.JumpTo(holders[0].Box, holders[0].Slot);
            RefreshFooter();
        }
        else
        {
            var target = holders[pick.Id];
            _viewModel.JumpTo(target.Box, target.Slot);
            _viewModel.Status = $"{item.Name}: {pick.Name.Trim()}";
        }
        _canvas.InvalidateSurface();
        _boxBar.InvalidateSurface();
    }

    /// <summary>
    /// Every Pokémon of the save sorted by IV total (highest or lowest first), each with its
    /// place, its total and its IV stars; picking one goes to it in its box.
    /// </summary>
    private async Task ShowIvRankingAsync()
    {
        var session = _sessionsFor();
        var data = IPlatformApplication.Current?.Services.GetService<IGameDataService>();
        if (session is null || data is null) return;
        const string Highest = "Maiores IVs primeiro", Lowest = "Menores IVs primeiro";
        var order = await PadMenu.ShowAsync(_hostGrid, "Ranking de IVs", null,
            new PadOption(Highest, IconPath: "stats"), new PadOption(Lowest, IconPath: "stats"));
        if (order is null) return;

        var occupied = _viewModel.AllSlots.Where(s => s.Species is not null && !s.IsEgg).ToList();
        var classic = session.GetTrainingCaps().IvMax == 15;
        var overlay = LoadingOverlay.Show(_hostGrid, "Ranqueando IVs…", "Lendo os IVs de todos os Pokémon.");
        IReadOnlyList<(SlotSummary Slot, IReadOnlyList<int> Ivs)> ranked;
        try
        {
            ranked = await Task.Run(() =>
            {
                var read = new List<(SlotSummary, IReadOnlyList<int>)>(occupied.Count);
                foreach (var slot in occupied)
                {
                    try { read.Add((slot, session.ReadEntity(slot.Box, slot.Slot).IVs)); }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
                }
                return IvRank.Order(read.Select(r => ((r.Item1, r.Item2), r.Item2)), highestFirst: order == Highest);
            });
        }
        finally { overlay.Close(); }
        if (ranked.Count == 0)
        {
            _viewModel.Status = "Nenhum Pokémon para ranquear neste save";
            return;
        }

        var rows = new List<PickItem>(ranked.Count);
        for (var i = 0; i < ranked.Count; i++)
        {
            var (slot, ivs) = ranked[i];
            var where = slot.Box == -1 ? $"Equipe {slot.Slot + 1}" : $"Box {slot.Box + 1:00} · slot {slot.Slot + 1:00}";
            var species = slot.Species is int sp && sp < data.SpeciesNames.Count ? data.SpeciesNames[sp] : $"#{slot.Species}";
            var label = slot.Nickname is { Length: > 0 } nick && !string.Equals(nick, species, StringComparison.OrdinalIgnoreCase)
                ? $"{nick} ({species})" : species;
            var total = ivs.Sum();
            var stars = IvRank.Stars(total);
            rows.Add(new PickItem(i, $"{i + 1}. {label}", Detail: $"{where} · {(classic ? "DV" : "IV")} total {total}")
            {
                // Gen 1 and 2 keep DVs (0-15): no PKHeX stars for them.
                Tag = classic ? null : new string('★', stars),
                TagColor = stars switch
                {
                    1 => UiTokens.Bad,
                    2 => UiTokens.Gold,
                    3 => UiTokens.Green,
                    _ => UiTokens.Cyan,
                },
            });
        }
        var pick = await PickerMenu.ShowAsync(_hostGrid, $"Ranking de IVs · {(order == Highest ? "maiores" : "menores")} primeiro", rows);
        if (pick is null) return;
        var target = ranked[pick.Id].Slot;
        _viewModel.JumpTo(target.Box, target.Slot);
        _viewModel.Status = $"Ranking de IVs: {pick.Name}";
        _canvas.InvalidateSurface();
        _boxBar.InvalidateSurface();
    }

    private async Task ShowOrganizerMenuAsync()
    {
        var count = _viewModel.MarkedCount;
        var page = _viewModel.BoxIndex == -1 ? "a equipe" : "esta box";
        var options = new List<PadOption>
        {
            new(_viewModel.CurrentBoxFullyMarked ? $"Desmarcar {page}" : $"Selecionar tudo: {page}", IconPath: "selectall"),
            new("Selecionar todas as boxes", IconPath: "all"),
            new($"Inverter {page}", IconPath: "invert", Detail: "Troca os Pokémon marcados e desmarcados aqui."),
        };
        if (count > 0)
        {
            options.Add(new("Limpar marcações", IconPath: "deselect"));
            options.AddRange(Menu(
                new PadOption("Mover para box…", IconPath: "move"),
                new PadOption("Mover para o Banco", IconPath: "bank", Detail: "Eles saem deste save e ficam guardados no Banco."),
                Allowed(SaveAction.Duplicate, new("Copiar para o Banco", IconPath: "bank", Detail: "As cópias vão para o Banco; os originais ficam aqui.")),
                new PadOption("Mover para outro jogo…", IconPath: "send", Detail: "Eles saem deste save e vão para outro jogo vinculado."),
                Allowed(SaveAction.Duplicate, new("Copiar para outro jogo…", IconPath: "copy", Detail: "As cópias vão para outro jogo vinculado; os originais ficam aqui.")),
                Allowed(SaveAction.Duplicate, new("Duplicar", IconPath: "copy", Detail: "Clona os Pokémon marcados nos primeiros slots livres das boxes.")),
                new PadOption("Exportar (arquivos .pk)", IconPath: "export", Detail: "Compartilha cada Pokémon marcado como um arquivo próprio."),
                new PadOption("Soltar", IconPath: "release")));
            options.AddRange(BulkOptions());
        }
        options.Add(new("Marcar Pokémon segurando…", IconPath: "item", Detail: "Encontre Pokémon segurando um item e marque-os ou vá até um deles."));
        options.Add(new("Gerenciador de boxes…", IconPath: "box", Detail: "Reordene, copie, trave ou esvazie boxes inteiras."));
        options.Add(new("Voltar ao modo mover", IconPath: "confirm", Detail: "Sair da seleção múltipla."));

        var choice = await PadMenu.ShowAsync(_hostGrid, $"Seleção múltipla · {count} marcados",
            Note("As marcações continuam enquanto você troca de box com L/R."), options.ToArray());
        if (await TryRunBulkAsync(choice))
        {
            _boxBar.InvalidateSurface();
            RefreshFooter();
            return;
        }
        switch (choice)
        {
            case "Selecionar tudo: esta box" or "Selecionar tudo: a equipe" or "Desmarcar esta box" or "Desmarcar a equipe":
                _viewModel.ToggleBoxMarks();
                break;
            case "Selecionar todas as boxes":
                _viewModel.MarkAllBoxes();
                break;
            case "Inverter esta box" or "Inverter a equipe":
                _viewModel.InvertBoxMarks();
                break;
            case "Limpar marcações":
                _viewModel.ClearMarks();
                break;
            case "Marcar Pokémon segurando…":
                await FindHeldItemAsync();
                break;
            case "Mover para box…":
            {
                if (_viewModel.MarksEmptyTheParty()) { _viewModel.Status = PartyNeedsOne; return; }
                var slots = _viewModel.AllSlots;
                var placement = await StartSlotPicker.PickAsync(_hostGrid, "Mover para qual box?", slots, count, offerFirstFree: false);
                if (placement?.Start is not { } start) return;
                if (!await PadMenu.ConfirmAsync(_hostGrid, $"Mover {count} Pokémon?", StartSlotPicker.Describe(slots, start, count) + ".", "Mover"))
                    return;
                await _viewModel.BulkMoveAsync(start);
                break;
            }
            case "Mover para outro jogo…":
                await SendSelectionToGameAsync(copy: false);
                break;
            case "Copiar para outro jogo…":
                await SendSelectionToGameAsync(copy: true);
                break;
            case "Duplicar":
                await DuplicateSelectionAsync();
                break;
            case "Mover para o Banco":
                await SendSelectionToBankAsync(copy: false);
                break;
            case "Copiar para o Banco":
                await SendSelectionToBankAsync(copy: true);
                break;
            case "Exportar (arquivos .pk)":
            {
                var directory = System.IO.Path.Combine(FileSystem.CacheDirectory, "export");
                Directory.CreateDirectory(directory);
                var paths = _viewModel.BulkExport(directory);
                if (paths.Count == 0) return;
                await Share.Default.RequestAsync(new ShareMultipleFilesRequest
                {
                    Title = $"{paths.Count} Pokémon",
                    Files = paths.Select(p => new ShareFile(p)).ToList(),
                });
                _viewModel.Status = $"{paths.Count} Pokémon exportados.";
                break;
            }
            case "Soltar":
                await ReleaseSelectionAsync();
                break;
            case "Gerenciador de boxes…":
                EnterBoxManageMode();
                return;
            case "Voltar ao modo mover":
                ExitSelectMode();
                return;
            default:
                return;
        }
        _canvas.InvalidateSurface();
        _boxBar.InvalidateSurface();
        RefreshFooter();
    }

    private const string PartyNeedsOne = "A EQUIPE PRECISA DE UM POKéMON - DESMARQUE UM MEMBRO DA EQUIPE";

    /// <summary>Sends every marked mon to another linked game, all or none: if one cannot enter
    /// that format, nothing is written anywhere and the refusal names it.</summary>
    private async Task SendSelectionToGameAsync(bool copy)
    {
        var session = _sessionsFor();
        var picker = IPlatformApplication.Current?.Services.GetService<SavePickerViewModel>();
        var transfer = IPlatformApplication.Current?.Services.GetService<Services.TransferService>();
        if (session is null || picker is null || transfer is null) return;
        // A move releases the marked mons after landing: this save must accept that write.
        if (!copy && _viewModel.OpenSaveRefusesWrites()) return;
        if (!copy && _viewModel.MarksEmptyTheParty()) { _viewModel.Status = PartyNeedsOne; return; }

        var count = _viewModel.MarkedCount;
        var verb = copy ? "COPIAR" : "MOVER";
        var currentDoc = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;
        var target = await SavePickerSheet.PickAsync(_hostGrid, picker.Saves,
            $"{verb} seleção para outro jogo", copy ? $"{count} Pokémon são copiados; os originais ficam" : $"{count} Pokémon saem deste save", currentDoc);
        if (target is null)
        {
            if (picker.Saves.Count == 0)
                _viewModel.Status = "Nenhum outro jogo vinculado. Vincule outro emulador ou save no Início.";
            return;
        }

        // Where they land in the other game: its first free spaces, or a box and slot the player picks.
        IReadOnlyList<SlotSummary> targetSlots;
        try { targetSlots = await transfer.ReadSlotsAsync(target); }
        catch (Exception error) when (error is InvalidDataException or IOException or InvalidOperationException)
        {
            _viewModel.Status = $"Não foi possível ler {target.GameLabel}: {error.Message}";
            return;
        }
        var placement = await StartSlotPicker.PickAsync(_hostGrid, $"Onde em {target.GameLabel}?", targetSlots, count);
        if (placement is null) return;
        var startAt = placement.Start;

        var confirm = await PadMenu.ConfirmAsync(_hostGrid, $"{verb} selection?",
            (copy
                ? $"Cópias de {count} Pokémon vão para {target.GameLabel}; os originais ficam aqui. Se algum não puder entrar nesse formato, nenhum é copiado."
                : $"{count} Pokémon vão sair deste save e ir para {target.GameLabel}. Se algum não puder entrar nesse formato, todos ficam aqui.")
            + $" Eles vão para {StartSlotPicker.Describe(targetSlots, startAt, count)}.",
            copy ? "Copiar todos" : "Mover todos");
        if (!confirm) return;

        // Bulk preview: the first marked mon's conversion diff stands for the
        // batch (per-mon prompts over N mons would be a questionnaire).
        var marked = _viewModel.MarkedSlots.ToArray();
        var firstExport = session.ExportSlot(marked[0].Box, marked[0].Slot);
        var firstPreview = await transfer.PreviewAsync(firstExport.Data, firstExport.FileName, target, startAt: startAt);
        if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, firstPreview,
                $"1 de {count} Pokémon", target.GameLabel)) return;

        // All or none: the other game takes the whole batch in one write, then a move empties
        // these slots in one write, and a failed release takes the batch back out of that game.
        var items = marked.Select(m => session.ExportSlot(m.Box, m.Slot))
            .Select(export => new PKForge.Engine.TransferItem(export.Data, export.Format, export.FileName)).ToArray();
        IReadOnlyList<SlotRef> landed = [];
        var outcome = await PKForge.Engine.BankTransfers.MoveAsync(items.Length, target.GameLabel,
            async () =>
            {
                (var refusal, landed) = await transfer.SendManyToGameAsync(items, target,
                    $"{items.Length} Pokémon chegaram de uma transferência", startAt: startAt);
                return refusal;
            },
            copy ? null : async () => await _viewModel.BulkReleaseAsync(marked) ? null : _viewModel.Status,
            async () =>
            {
                await transfer.ReleaseFromGameAsync(target, landed, $"{items.Length} Pokémon devolvidos: a transferência foi desfeita");
                return null;
            });
        if (outcome.Success && copy) _viewModel.ClearMarksQuietly();
        _viewModel.Status = outcome.Message;
    }

    /// <summary>Clones every marked mon into the first free box slots. One write.</summary>
    private async Task DuplicateSelectionAsync()
    {
        if (_sessionsFor() is null) return;
        var sources = _viewModel.MarkedSlots.ToArray();
        var used = new HashSet<(int Box, int Slot)>();
        var ok = await _viewModel.RunMutationAsync(s =>
        {
            var cloned = 0;
            foreach (var source in sources)
            {
                (int Box, int Slot)? landing = null;
                foreach (var cand in _viewModel.Save!.Slots.Where(x => x.Box >= 0))
                {
                    if (used.Contains((cand.Box, cand.Slot))) continue;
                    if (!s.ReadEntity(cand.Box, cand.Slot).IsEmpty) continue; // live check: never overwrite
                    landing = (cand.Box, cand.Slot);
                    break;
                }
                if (landing is null) break;
                if (s.DuplicateSlot(source.Box, source.Slot, landing.Value.Box, landing.Value.Slot))
                {
                    used.Add(landing.Value);
                    cloned++;
                }
            }
            return new GenerationOutcome(cloned > 0,
                cloned == 0 ? "Nenhum Pokémon pôde ser duplicado. Verifique o espaço livre."
                : $"{cloned} Pokémon clonados." + (cloned < sources.Length ? $" {sources.Length - cloned} não puderam ser duplicados." : ""));
        }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.Duplicate);
        _viewModel.RefreshAllSlots();
        if (ok) _viewModel.ClearMarksQuietly();
    }

    /// <summary>Deposits every marked mon in the Bank, all or none: every one is read first,
    /// the Bank takes them in one write, and only then does a move empty the slots in one safe
    /// save write (a failed save write takes the deposits back out of the Bank).</summary>
    private async Task SendSelectionToBankAsync(bool copy)
    {
        var session = _sessionsFor();
        var bank = IPlatformApplication.Current?.Services.GetService<IBankService>();
        var engine = IPlatformApplication.Current?.Services.GetService<ISaveEngine>();
        if (session is null || bank is null || engine is null) return;
        if (!copy && _viewModel.MarksEmptyTheParty()) { _viewModel.Status = PartyNeedsOne; return; }

        var marked = _viewModel.MarkedSlots.ToArray();
        var deposits = new List<BankDeposit>(marked.Length);
        foreach (var (box, markedSlot) in marked)
        {
            var export = session.ExportSlot(box, markedSlot);
            if (engine.TryDescribeEntity(export.Data, _viewModel.ConnectedName, export.Format) is not { } info)
            {
                _viewModel.Status = $"Nada foi enviado: não foi possível ler {BoxBrowserViewModel.SlotLabel(box, markedSlot)} para o Banco.";
                return;
            }
            deposits.Add(new BankDeposit(export.Data, info));
        }
        var outcome = await PKForge.Engine.BankTransfers.DepositAsync(bank, deposits,
            copy ? null : async () => await _viewModel.BulkReleaseAsync(marked) ? null : _viewModel.Status);
        if (outcome.Success && copy) _viewModel.ClearMarksQuietly();
        _viewModel.Status = outcome.Message;
    }

    private async Task ReleaseSelectionAsync()
    {
        if (_viewModel.MarksEmptyTheParty()) { _viewModel.Status = PartyNeedsOne; return; }
        var docId = DocumentId;
        var session = _sessionsFor();
        if (docId is not null && session is not null)
        {
            var locked = _viewModel.MarkedSlots
                .Where(m => !Protection.CanRelease(docId, m.Box, m.Slot, session.GetRngInfo(m.Box, m.Slot).Pid))
                .ToList();
            if (locked.Count > 0)
            {
                _viewModel.Status = $"{locked.Count} Pokémon marcado(s) estão travados - destrave ou desmarque-os";
                return;
            }
        }
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Soltar a seleção?",
            $"Soltar todos os {_viewModel.MarkedCount} Pokémon marcados? O estado atual do save é mantido como ponto de restauração.",
            "Soltar todos");
        if (!confirmed) return;
        await _viewModel.BulkReleaseAsync();
    }

    /// <summary>Multi-pick .pk files and import them into this box's empty slots (one write).</summary>
    private async Task BulkImportAsync()
    {
        if (Denied(SaveAction.CreateMon)) return;
        var picker = IPlatformApplication.Current?.Services.GetService<IDocumentPicker>();
        var access = IPlatformApplication.Current?.Services.GetService<ISaveFileAccess>();
        if (picker is null || access is null) return;

        var documents = await picker.PickManyAsync();
        if (documents.Count == 0) return;

        var payloads = new List<(byte[] Bytes, string Name)>();
        foreach (var document in documents)
            payloads.Add(((await access.ReadAsync(document.DocumentId)).ToArray(), document.DisplayName));

        await _viewModel.RunMutationAsync(session =>
        {
            var empties = new Queue<int>(_viewModel.VisibleSlots.Where(s => s.Species is null).Select(s => s.Slot));
            var imported = 0;
            var failed = 0;
            foreach (var (bytes, name) in payloads)
            {
                if (empties.Count == 0) break;
                // The file extension (".pb8" vs ".pk8") names the format, as in PKHeX's own loader.
                if (session.ImportSlot(_viewModel.BoxIndex, empties.Peek(), bytes, name)) { empties.Dequeue(); imported++; }
                else failed++;
            }
            return new GenerationOutcome(imported > 0,
                $"{imported} Pokémon importados." + (failed > 0 ? $" {failed} arquivo(s) não reconhecido(s)." : ""));
        }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.CreateMon);
        _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>Paste a full Showdown team: every set is previewed with its legality, then placed into chosen empty slots.</summary>
    private Task ImportShowdownTeamAsync() => ImportShowdownPreviewedAsync("Importar time do Showdown");

    /// <summary>Every mon in the current box as one Showdown text: copy or share.</summary>
    private async Task ExportBoxShowdownAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var sets = _viewModel.VisibleSlots
            .Where(s => s.Species is not null)
            .Select(s => session.GetShowdownText(_viewModel.BoxIndex, s.Slot));
        var text = string.Join("\n\n", sets);
        if (text.Length == 0) { _viewModel.Status = "Esta box está vazia."; return; }
        var choice = await PadMenu.ShowAsync(_hostGrid, "Box como time do Showdown", null, "Copiar para a área de transferência", "Compartilhar como arquivo", "Fechar");
        switch (choice)
        {
            case "Copiar para a área de transferência":
                await Clipboard.Default.SetTextAsync(text);
                _viewModel.Status = "Box copiada como texto do Showdown.";
                return;
            case "Compartilhar como arquivo":
                var path = System.IO.Path.Combine(FileSystem.CacheDirectory, $"box-{_viewModel.BoxIndex + 1}.txt");
                await File.WriteAllTextAsync(path, text);
                await Share.Default.RequestAsync(new ShareFileRequest { Title = "Time do Showdown", File = new ShareFile(path) });
                return;
        }
    }

    /// <summary>Fills the entire PC with a legal living dex (explicitly destructive; heavily confirmed).</summary>
    private async Task GenerateLivingDexAsync()
    {
        if (Denied(SaveAction.CreateMon)) return;
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Gerar Living Dex?",
            "Isto SOBRESCREVE todas as boxes com um Pokémon legal de cada espécie que este jogo suporta. " +
            "O estado atual do save é mantido como ponto de restauração. Isso pode levar vários minutos.",
            "Preencher minhas boxes");
        if (!confirmed) return;

        var session = _sessionsFor();
        if (session is null) { _viewModel.Status = "Nenhum save conectado."; return; }
        byte[]? bundle = null;
        try
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync($"dex/dex-g{session.Generation}.bin.gz");
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            bundle = memory.ToArray();
        }
        catch
        {
            _viewModel.Status = "Ainda não há pacote de Living Dex para esta geração.";
            return;
        }

        var overlay = LoadingOverlay.Show(_hostGrid, "Preenchendo a Living Dex…",
            "Um de cada espécie, copiado da dex pré-gerada. É uma gravação direta.");
        try
        {
            await _viewModel.RunMutationAsync(s =>
            {
                var outcome = ((ILegalizerService)IPlatformApplication.Current!.Services.GetRequiredService(typeof(ILegalizerService)))
                    .FillLivingDex(s, bundle!);
                return outcome;
            }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.CreateMon);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>Auto-sort: pick a criteria, pick a scope, one backed-up write compacts mons front.</summary>
    private async Task ShowSortMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "Ordenar", "Como as boxes devem ser ordenadas?",
            new PadOption("Número da dex", IconPath: "pokedex"),
            new PadOption("Alfabética", IconPath: "alpha"),
            new PadOption("Nível (mais fortes primeiro)", IconPath: "level"),
            new PadOption("Total de IVs (melhores primeiro)", IconPath: "stats"),
            new PadOption("Tipo", IconPath: "type"),
            new PadOption("Idade (mais antigos primeiro)", IconPath: "calendar"),
            new PadOption("Shiny primeiro", IconPath: "shiny"));
        if (choice is null) return;

        var scope = await PadMenu.ShowAsync(_hostGrid, "Ordenar", "Quais boxes?",
            new PadOption("Esta box", IconPath: "box"),
            new PadOption("Todas as boxes", IconPath: "storage"));

        var direction = await PadMenu.ShowAsync(_hostGrid, "Ordenar", "Qual direção?",
            new PadOption("Ordem normal", IconPath: "sort"),
            new PadOption("Invertida", IconPath: "reverse"));
        if (direction is null) return;
        var reverse = direction == "Invertida";

        var criteria = choice switch
        {
            "Alfabética" => Domain.SortCriteria.Alphabetical,
            "Nível (mais fortes primeiro)" => Domain.SortCriteria.LevelDesc,
            "Total de IVs (melhores primeiro)" => Domain.SortCriteria.IvTotalDesc,
            "Tipo" => Domain.SortCriteria.Type,
            "Idade (mais antigos primeiro)" => Domain.SortCriteria.AgeOldest,
            "Shiny primeiro" => Domain.SortCriteria.ShinyFirst,
            _ => Domain.SortCriteria.DexNumber,
        };
        IReadOnlyList<int>? boxes = scope == "Esta box" ? [_viewModel.BoxIndex] : null;

        var directionNote = reverse ? " (reversed)" : "";
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Ordenar agora?",
            (scope == "Esta box"
                ? "Os Pokémon desta box são reordenados e agrupados no topo."
                : "Os Pokémon de todas as boxes são reunidos, ordenados e agrupados a partir da box 1. Os espaços vazios ficam no final.")
            + directionNote,
            "Ordenar");
        if (!confirmed) return;

        var sorted = await _viewModel.RunMutationAsync(session =>
        {
            // Locked boxes keep their contents exactly where they are.
            if (boxes is null && DocumentId is { } docId)
            {
                var locked = Protection.LockedBoxes(docId);
                if (locked.Count > 0)
                    boxes = Enumerable.Range(0, _viewModel.BoxCount).Where(box => !locked.Contains(box)).ToList();
            }
            var placed = session.SortBoxes(criteria, boxes, reverse);
            return new GenerationOutcome(true, $"{placed} Pokémon ordenados{directionNote}.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
        if (sorted)
            _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>One-tap preset packs: competitive, speedrun, casual - all through the batch editor.</summary>
    private async Task ShowPresetsMenuAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var caps = session.GetTrainingCaps();
        var perfectLabel = caps.IvMax == 15 ? "6DV (DVs perfeitos)" : "6IV (IVs perfeitos)";
        var scope = await PadMenu.ShowAsync(_hostGrid, "Predefinições", "Aplicar em quais boxes?",
            new PadOption("Esta box", IconPath: "box"),
            new PadOption("Todas as boxes destravadas", IconPath: "storage"));
        if (scope is null) return;
        IReadOnlyList<int>? boxes;
        if (scope == "Esta box") boxes = [_viewModel.BoxIndex];
        else
        {
            var docId = DocumentId;
            var unlocked = Enumerable.Range(0, _viewModel.BoxCount)
                .Where(box => docId is null || !Protection.IsBoxLocked(docId, box)).ToList();
            if (unlocked.Count == 0) { _viewModel.Status = "Todas as boxes estão travadas"; return; }
            boxes = unlocked;
        }

        var choice = await PadMenu.ShowAsync(_hostGrid, "Predefinições", "Uma única gravação, com backup, aplica tudo.",
            new PadOption("Nível 50 fixo", IconPath: "level"),
            new PadOption("Nível 100", IconPath: "level"),
            new PadOption(perfectLabel, IconPath: "stats"),
            new PadOption("IV de Ataque 0 (especial)", IconPath: "stats", Detail: "IV de Ataque definido como 0, para atacantes especiais."),
            new PadOption("IV de Velocidade 0 (Trick Room)", IconPath: "stats", Detail: "IV de Velocidade definido como 0, para times de Trick Room."),
            new PadOption("Zerar EVs", IconPath: "restore"),
            new PadOption("Amizade máxima", IconPath: "heart"),
            new PadOption("Hyper Training em tudo", IconPath: "train"),
            new PadOption("Exportar box (Showdown)", IconPath: "script", Detail: "Compartilhe esta box como arquivo de texto do Showdown."),
            new PadOption("Importar sets do Showdown para esta box", IconPath: "script", Detail: "Cole sets do Showdown, veja a legalidade deles e coloque-os em slots vazios."));
        if (choice is null) return;

        if (choice == "Exportar box (Showdown)")
        {
            await ExportBoxShowdownFromEngineAsync();
            return;
        }
        if (choice == "Importar sets do Showdown para esta box")
        {
            await ImportShowdownSetsToBoxAsync();
            return;
        }

        IReadOnlyList<string> instructions = choice switch
        {
            "Nível 50 fixo" => ["Level=50"],
            "Nível 100" => ["Level=100"],
            var perfect when perfect == perfectLabel =>
                [.. new[] { "HP", "ATK", "DEF", "SPA", "SPD", "SPE" }.Select(stat => $"IV_{stat}={caps.IvMax}")],
            "IV de Ataque 0 (especial)" => ["IV_ATK=0"],
            "IV de Velocidade 0 (Trick Room)" => ["IV_SPE=0"],
            "Zerar EVs" => ["EV_HP=0", "EV_ATK=0", "EV_DEF=0", "EV_SPA=0", "EV_SPD=0", "EV_SPE=0"],
            "Amizade máxima" => ["Friendship=255"],
            "Hyper Training em tudo" => ["HyperTrain"],
            _ => [],
        };
        if (instructions.Count == 0) return;

        var scopeText = boxes.Count == _viewModel.BoxCount ? "todas as boxes destravadas" : $"box {_viewModel.BoxIndex + 1:00}";
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Aplicar predefinição?",
            $"{choice} em {scopeText}. Com backup antes.", "Aplicar");
        if (!confirmed) return;
        await _viewModel.RunMutationAsync(s =>
        {
            var touched = s.BatchApply(instructions, boxes);
            return touched > 0
                ? new GenerationOutcome(true, $"Predefinição aplicada a {touched} Pokémon.")
                : new GenerationOutcome(false, "Nada para editar ali.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
        _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    private async Task ExportBoxShowdownFromEngineAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var text = session.ExportBoxShowdown(_viewModel.BoxIndex);
        if (text.Length == 0) { _viewModel.Status = "Esta box está vazia"; return; }
        var path = Path.Combine(FileSystem.CacheDirectory, $"box-{_viewModel.BoxIndex + 1:00}-showdown.txt");
        File.WriteAllText(path, text);
        await Share.Default.RequestAsync(new ShareFileRequest { Title = $"Box {_viewModel.BoxIndex + 1:00} Showdown", File = new ShareFile(path) });
    }

    private Task ImportShowdownSetsToBoxAsync() => ImportShowdownPreviewedAsync("Importar sets do Showdown");

    /// <summary>The speedrunner view: PID, EC, IVs, and a shiny-safe nature reroll.</summary>
    private async Task ShowRngAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        var box = _viewModel.BoxIndex;
        var rng = session.GetRngInfo(box, slot);
        if (!rng.NatureRerollSupported)
        {
            _viewModel.Status = "Esta geração não tem naturezas";
            return;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        var current = rng;
        var stack = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar("RNG / IVs"),
                new Label { Text = $"PID      {current.Pid:X8}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = current.EncryptionConstant is { } ec ? $"EC       {ec:X8}" : "EC       (não existe nesta geração)", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = $"IVs      {string.Join("/", current.IVs)}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = $"Natureza {data.NatureNames[Math.Clamp(current.Nature, 0, data.NatureNames.Count - 1)]}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = UiTokens.Ink0 },
                new Label { Text = $"Shiny    {(current.Shiny ? "SIM" : "NÃO")}", FontFamily = DsChrome.PixelFont, FontSize = 14, TextColor = current.Shiny ? UiTokens.Gold : UiTokens.Ink0 },
            },
        };
        var window = Kit.OverlayWindow(_hostGrid, stack);

        var reroll = Kit.Capsule("Re-sortear natureza (mantém shiny)", UiTokens.Green);
        // A nature reroll edits the mon, so Hardcore mode hides the affordance and leaves
        // the read-out (PID, EC, IVs) alone - viewing stays, editing does not.
        reroll.IsVisible = Guard.CanEditMon;
        var close = Kit.Capsule("Fechar", UiTokens.Ink1);
        if (HardcoreMode.IsOn)
            stack.Children.Add(new Label
            {
                Text = HardcoreMode.StatusLine,
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextSmall,
                TextColor = UiTokens.Bad,
            });
        stack.Children.Add(reroll);
        stack.Children.Add(close);

        var done = new TaskCompletionSource();
        var overlay = Kit.AttachOverlay(_hostGrid, window, () => done.TrySetResult());
        reroll.Clicked += async (_, _) =>
        {
            // Ids come from the pick itself: an index into a filtered name list skipped Hardy
            // and shifted every choice by one.
            var session = _sessionsFor();
            var preview = session is null ? null : NaturePicker.Service?.PreviewSlot(session, box, slot);
            var picked = await NaturePicker.ShowAsync(overlay, data.NatureNames, current.Nature, preview, "Escolha uma natureza");
            if (picked is null) return;
            var nature = picked.Id;
            var targetBox = box;
            var targetSlot = slot;
            var ok = await _viewModel.RunMutationAsync(s => s.RerollNatureKeepShiny(targetBox, targetSlot, nature)
                ? new GenerationOutcome(true, "Natureza re-sorteada; shiny mantido.")
                : new GenerationOutcome(false, "Não foi possível encontrar um PID compatível. Tente de novo."), targetSlot);
            if (ok)
            {
                current = _sessionsFor()!.GetRngInfo(targetBox, targetSlot);
                _viewModel.Status = $"PID {current.Pid:X8} · natureza {data.NatureNames[Math.Clamp(current.Nature, 0, data.NatureNames.Count - 1)]} · shiny {(current.Shiny ? "SIM" : "NÃO")}";
            }
            _canvas.InvalidateSurface();
        };
        close.Clicked += (_, _) => { _hostGrid.Remove(overlay); done.TrySetResult(); };
        await done.Task;
    }

    /// <summary>First catch per route from met data: the post-run Nuzlocke audit.</summary>
    private async Task ShowNuzlockeReportAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var report = session.GetNuzlockeReport();
        if (report.Count == 0)
        {
            _viewModel.Status = "Nenhum dado de captura neste save";
            return;
        }

        var rows = new VerticalStackLayout { Spacing = 4 };
        foreach (var group in report.GroupBy(c => c.Route))
        {
            rows.Children.Add(new Label
            {
                Text = group.Key,
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextTitle,
                TextColor = UiTokens.Ink0,
                Margin = new Thickness(0, 6, 0, 2),
            });
            foreach (var catchRow in group)
                rows.Children.Add(new Label
                {
                    Text = $"   {(catchRow.FirstCatch ? "primeiro" : "repetido")} - {catchRow.Name}{(catchRow.MetDate is { } d ? $" ({d})" : "")}",
                    FontSize = UiTokens.TextSmall,
                    TextColor = catchRow.FirstCatch ? UiTokens.Ink0 : UiTokens.Ink1,
                });
        }

        var close = Kit.Capsule("Fechar", UiTokens.Ink1);
        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Kit.HeaderBar("Relatório de Nuzlocke"), new ScrollView { Content = rows, MaximumHeightRequest = 420 }, close },
        };
        var done = new TaskCompletionSource();
        var overlay = Kit.AttachOverlay(_hostGrid, Kit.OverlayWindow(_hostGrid, content), () => done.TrySetResult());
        close.Clicked += (_, _) => { _hostGrid.Remove(overlay); done.TrySetResult(); };
        await done.Task;
    }

    /// <summary>The whole-save legality sweep: off-thread scan behind the walking
    /// overlay, cached per write-generation so reopening the tool is instant until the
    /// save changes. Dots appear on the current box; illegal mons offer repairs.</summary>
    private async Task ShowLegalityCheckAsync()
    {
        var session = _sessionsFor();
        if (session is null) { _viewModel.Status = "Abra um save primeiro."; return; }
        if (!session.SupportsLegalityAnalysis)
        {
            await PadMenu.ShowAsync(_hostGrid, "Verificação de legalidade",
                "Este save é de um formato de romhack sem tabelas de legalidade offline - nada para verificar aqui.", "OK");
            return;
        }

        var overlay = default(LoadingOverlay);
        using var cancellation = new CancellationTokenSource();
        try
        {
            var results = await _viewModel.GetLegalitySweepAsync(
                () =>
                {
                    overlay = LoadingOverlay.Show(_hostGrid, "Verificando legalidade…", "Varrendo a equipe e todas as boxes.");
                    overlay.Cancellation.Token.Register(cancellation.Cancel);
                },
                (done, total) => overlay?.Report(done, total),
                cancellation.Token);
            if (results.Count == 0)
            {
                _viewModel.Status = "Este save não guarda nenhum Pokémon.";
                return;
            }
            var illegal = results.Where(r => !r.Valid).ToList();

            var choice = illegal.Count == 0
                ? await PadMenu.ShowAsync(_hostGrid, "Verificação de legalidade", $"Todos os {results.Count} Pokémon são legais.", "OK")
                : await PadMenu.ShowAsync(_hostGrid, "Verificação de legalidade",
                    Note($"{illegal.Count} ilegais de {results.Count}. Os slots com ponto vermelho nesta box falham nas verificações do PKHeX."),
                    Menu(Allowed(SaveAction.EditMon, new("Legalizar todos os ilegais", IconPath: "fix", Detail: "Reescreve cada Pokémon ilegal do save para a versão legal mais próxima.")),
                        new PadOption("Fechar", IconPath: "close")));
            if (choice == "Legalizar todos os ilegais")
                await LegalizeAllIllegalAsync(illegal);
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Verificação de legalidade cancelada.";
        }
        finally
        {
            overlay?.Close();
        }
    }

    /// <summary>One confirmed, backed-up write that repairs every flagged slot: the
    /// engine legalizes each mon in place inside a single mutation.</summary>
    private async Task LegalizeAllIllegalAsync(IReadOnlyList<SlotLegality> illegal)
    {
        var session = _sessionsFor();
        var legalizer = IPlatformApplication.Current?.Services.GetService<ILegalizerService>();
        if (session is null || legalizer is null) return;

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Legalizar todos os ilegais?",
            $"{illegal.Count} Pokémon serão reescritos para as versões legais mais próximas. " +
            "O estado atual continua disponível como ponto de restauração.", "Legalizar");
        if (!confirmed) return;

        var targets = illegal.Select(v => (v.Box, v.Slot)).ToList();
        var overlay = LoadingOverlay.Show(_hostGrid, "Legalizando…", "Consertando todos os Pokémon sinalizados em uma única gravação.");
        try
        {
            var ok = await _viewModel.RunMutationAsync(
                s => legalizer.LegalizeSlots(s, targets, (done, total) => overlay.Report(done, total)),
                Math.Max(0, _viewModel.SelectedSlot),
                refreshSlot: false,
                changeDescription: $"Legalizar {targets.Count} Pokémon sinalizados",
                action: SaveAction.EditMon);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
            if (!ok)
                await PadMenu.ShowAsync(_hostGrid, "Legalizar todos", _viewModel.Status, "OK");
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>The clone/hack audit: identical (EC,PID) or (PID,OT,TID) groups across
    /// the whole save plus impossible values from the legality sweep, as one scrollable
    /// report. The bank is excluded on purpose: its entries keep no EC/PID fingerprints.</summary>
    private async Task ShowAuditReportAsync()
    {
        var session = _sessionsFor();
        if (session is null) { _viewModel.Status = "Abra um save primeiro."; return; }
        if (!session.SupportsLegalityAnalysis)
        {
            await PadMenu.ShowAsync(_hostGrid, "Relatório de auditoria",
                "Este save é de um formato de romhack sem tabelas de legalidade offline - nada para auditar aqui.", "OK");
            return;
        }

        var overlay = default(LoadingOverlay);
        using var cancellation = new CancellationTokenSource();
        IReadOnlyList<SlotLegality> sweep;
        IReadOnlyList<MonFingerprint> fingerprints;
        try
        {
            sweep = await _viewModel.GetLegalitySweepAsync(
                () =>
                {
                    overlay = LoadingOverlay.Show(_hostGrid, "Auditando…", "Identificando cada Pokémon e verificando os valores.");
                    overlay.Cancellation.Token.Register(cancellation.Cancel);
                },
                (done, total) => overlay?.Report(done, total),
                cancellation.Token);
            fingerprints = await Task.Run(() => CollectFingerprints(session), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Auditoria cancelada.";
            overlay?.Close();
            return;
        }
        finally
        {
            overlay?.Close();
        }

        var clones = CollectionAudit.GroupClones(fingerprints);
        var illegal = sweep.Where(r => !r.Valid).ToList();
        _viewModel.Status = clones.Count == 0 && illegal.Count == 0
            ? "Auditoria: nenhum clone, nenhum valor impossível"
            : $"Auditoria: {clones.Count} grupos de clones, {illegal.Count} sinalizados";

        var report = BuildAuditReport(clones, illegal, fingerprints.Count);
        var rows = new VerticalStackLayout { Spacing = 4 };
        foreach (var line in report)
        {
            var isHeader = line.StartsWith("## ", StringComparison.Ordinal);
            rows.Children.Add(new Label
            {
                Text = isHeader ? line[3..] : line,
                FontFamily = DsChrome.PixelFont,
                FontSize = isHeader ? UiTokens.TextTitle : UiTokens.TextSmall,
                TextColor = isHeader ? UiTokens.Ink0 : UiTokens.Ink1,
                Margin = isHeader ? new Thickness(0, 6, 0, 2) : new Thickness(0),
            });
        }

        var share = Kit.Capsule("Compartilhar relatório", UiTokens.Cyan);
        var close = Kit.Capsule("Fechar", UiTokens.Ink1);
        var buttons = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Children = { share, close } };
        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children = { Kit.HeaderBar("Relatório de auditoria"), new ScrollView { Content = rows, MaximumHeightRequest = 420 }, buttons },
        };

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Grid overlayGrid = null!;
        PadOverlay pad = null!;
        void Close()
        {
            _hostGrid.Remove(overlayGrid);
            pad?.Dispose();
            done.TrySetResult();
        }
        overlayGrid = Kit.AttachOverlay(_hostGrid, Kit.OverlayWindow(_hostGrid, content), Close);
        pad = new PadOverlay(Close, Close);
        close.Clicked += (_, _) => Close();
        share.Clicked += async (_, _) => await ShareAuditReportAsync(string.Join(Environment.NewLine, report.Select(Strip)));
        await done.Task;

        static string Strip(string line) => line.StartsWith("## ", StringComparison.Ordinal) ? line[3..] : line;
    }

    /// <summary>The identity facts the clone grouping runs on, for every occupied slot.</summary>
    private static IReadOnlyList<MonFingerprint> CollectFingerprints(Domain.ISaveEngineSession session)
    {
        var fingerprints = new List<MonFingerprint>();
        foreach (var summary in session.Snapshot.Slots)
        {
            if (summary.Species is null) continue;
            var rng = session.GetRngInfo(summary.Box, summary.Slot);
            var detail = session.ReadEntity(summary.Box, summary.Slot);
            var tid = session.GetMetInfo(summary.Box, summary.Slot).TID;
            fingerprints.Add(new MonFingerprint(
                rng.Pid, rng.EncryptionConstant, detail.OriginalTrainer, tid,
                summary.Box == -1 ? $"Equipe {summary.Slot + 1}" : $"B{summary.Box + 1}-{summary.Slot + 1}",
                detail.Nickname is { Length: > 0 } ? detail.Nickname : detail.SpeciesName));
        }
        return fingerprints;
    }

    /// <summary>Plain-text audit body: clone sections then impossible values.</summary>
    private static IReadOnlyList<string> BuildAuditReport(
        IReadOnlyList<CloneGroup> clones, IReadOnlyList<SlotLegality> illegal, int total)
    {
        var lines = new List<string>
        {
            $"{total} Pokémon auditados neste save.",
            string.Empty,
        };
        if (clones.Count == 0)
        {
            lines.Add("Nenhum clone: cada Pokémon tem uma identidade única.");
        }
        else
        {
            lines.Add($"## Clones ({clones.Count} grupos)");
            foreach (var group in clones)
            {
                lines.Add($"{group.KeyKind} igual ×{group.Members.Count}:");
                lines.AddRange(group.Members.Select(m => $"   {m.SlotLabel} - {m.DisplayName}"));
            }
        }
        lines.Add(string.Empty);
        if (illegal.Count == 0)
        {
            lines.Add("Nenhum valor impossível: todos os Pokémon passaram nas verificações do PKHeX.");
        }
        else
        {
            lines.Add($"## Valores impossíveis ({illegal.Count})");
            foreach (var verdict in illegal)
            {
                var label = verdict.Box == -1 ? $"Equipe {verdict.Slot + 1}" : $"B{verdict.Box + 1}-{verdict.Slot + 1}";
                lines.Add($"{label} - {verdict.Problem}");
            }
        }
        lines.Add(string.Empty);
        lines.Add("A auditoria do Banco fica para o futuro: as entradas do Banco não guardam a identidade EC/PID.");
        return lines;
    }

    /// <summary>Hands the audit text to Android's share sheet as a file (the .pk export path).</summary>
    private static async Task ShareAuditReportAsync(string text)
    {
        try
        {
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, "pkforge-audit.txt");
            await File.WriteAllTextAsync(path, text);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Relatório de auditoria do PKForge",
                File = new ShareFile(path),
            });
        }
        catch (Exception error)
        {
            // Sharing is best-effort; the on-screen report is the source of truth.
            _ = error.Message;
        }
    }

    /// <summary>Mass egg generation: living egg dex, or one species filling this box.</summary>
    private async Task ShowEggFactoryAsync()
    {
        if (Denied(SaveAction.CreateMon)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();

        var choice = await PadMenu.ShowAsync(_hostGrid, "Fábrica de ovos", "Cada ovo ocupa um slot vazio do PC. Uma única gravação, com backup.",
            new PadOption("Um ovo de cada espécie", IconPath: "egg"),
            new PadOption("Escolher uma espécie…", IconPath: "pokedex"));
        if (choice is null) return;

        IReadOnlyList<int> species;
        if (choice == "Escolher uma espécie…")
        {
            var picked = await PokedexPicker.ShowAsync(_hostGrid, data, session);
            if (picked is null) return;
            species = [picked.Id];
        }
        else
        {
            species = Enumerable.Range(1, Math.Min(data.SpeciesNames.Count - 1, session.GetDexProgress().Total))
                .Where(id => data.SpeciesNames[id].Length > 0)
                .ToList();
        }

        var caps = session.GetTrainingCaps();
        var statKind = caps.IvMax == 15 ? "DVs" : "IVs";
        var maxIv = await PadMenu.ConfirmAsync(_hostGrid, $"{statKind} perfeitos?",
            $"Cada ovo recebe {caps.IvMax} de {statKind} nos seis atributos.", $"Sim, 6{(caps.IvMax == 15 ? "DV" : "IV")}");
        var shiny = await PadMenu.ConfirmAsync(_hostGrid, "Ovos shiny?", "Todos os ovos chocam shiny.", "Sim, shiny");

        var options = new Domain.EggOptions(maxIv, shiny);
        var overlay = LoadingOverlay.Show(_hostGrid, "Gerando ovos…", "O legalizador cria cada espécie e a transforma em ovo, offline.");
        try
        {
            var list = species;
            await _viewModel.RunMutationAsync(s => legalizer.GenerateEggs(s, list, options,
                (done, total) => overlay.Report(done, total)), Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally { overlay.Close(); }
    }

    /// <summary>One-tap battle preparation: heal, PP Max, flat rules - one backed-up write per action.</summary>
    private async Task ShowBattlePrepAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var choice = await PadMenu.ShowAsync(_hostGrid, "Preparar para batalha", "Cada ação é uma única gravação, com backup.",
            new PadOption("Curar equipe", IconPath: "heal"),
            new PadOption("Curar todos (equipe + boxes)", IconPath: "heal"),
            new PadOption("PP máximo em todos os golpes", IconPath: "moves", Detail: "PP Ups no máximo em todos os golpes, na equipe e nas boxes destravadas."),
            new PadOption("Equipe no Lv50 (regra fixa)", IconPath: "level"),
            new PadOption("Todos no Lv100", IconPath: "level", Detail: "A equipe e todas as boxes destravadas vão para o nível 100."));
        if (choice is null) return;

        var party = new[] { -1 };
        var everywhere = PartyAndUnlockedBoxes();
        var everywhereText = everywhere.Count == 1
            ? "só a equipe (todas as boxes estão travadas)"
            : "a equipe e todas as boxes destravadas";
        IReadOnlyList<string> instructions;
        IReadOnlyList<int> boxes;
        string what;
        if (choice == "Curar equipe")
        {
            instructions = ["Heal"];
            boxes = party;
            what = "a equipe";
        }
        else if (choice == "Curar todos (equipe + boxes)")
        {
            instructions = ["Heal"];
            boxes = everywhere;
            what = everywhereText;
        }
        else if (choice == "PP máximo em todos os golpes")
        {
            // PP Ups persist even in box storage; current PP only lives in party data,
            // so HealPP tops off the party and is a no-op write for boxed mons.
            instructions = ["Move1_PPUps=3", "Move2_PPUps=3", "Move3_PPUps=3", "Move4_PPUps=3", "HealPP"];
            boxes = everywhere;
            what = everywhereText;
        }
        else if (choice == "Equipe no Lv50 (regra fixa)")
        {
            instructions = ["Level=50"];
            boxes = party;
            what = "a equipe";
        }
        else
        {
            instructions = ["Level=100"];
            boxes = everywhere;
            what = everywhereText;
        }

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, $"{choice}?",
            $"Vale para {what}. Com backup antes.", choice);
        if (!confirmed) return;

        await _viewModel.RunMutationAsync(s =>
        {
            var touched = s.BatchApply(instructions, boxes);
            return touched > 0
                ? new GenerationOutcome(true, $"{choice} aplicado a {touched} Pokémon.")
                : new GenerationOutcome(false, "Nada para editar ali.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
        _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    /// <summary>The party plus every unlocked box: battle prep's whole-save scope.</summary>
    private IReadOnlyList<int> PartyAndUnlockedBoxes()
    {
        var docId = DocumentId;
        return [-1, .. Enumerable.Range(0, _viewModel.BoxCount)
            .Where(box => docId is null || !Protection.IsBoxLocked(docId, box))];
    }

    /// <summary>
    /// The batch editor: stack operations as chips (level, IVs, EV spread, heal, training,
    /// friendship, nicknames), then apply them to a scope - the organizer selection, one
    /// box, or every box - in a single backed-up write. The PKHeX instruction strings are
    /// shown before the write; experts can paste their own on top.
    /// </summary>
    private async Task RunBatchEditorAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var caps = session.GetTrainingCaps();
        var statKind = caps.IvMax == 15 ? "DVs" : "IVs";

        // Scope first. Locked boxes refuse batch edits (same rule as sort and presets).
        var scopeOptions = new List<PadOption>();
        if (_viewModel.MarkedCount > 0)
            scopeOptions.Add(new($"Pokémon selecionados ({_viewModel.MarkedCount})", IconPath: "select"));
        scopeOptions.Add(new("Esta box", IconPath: "box"));
        scopeOptions.Add(new("Todas as boxes", IconPath: "storage"));
        var scope = await PadMenu.ShowAsync(_hostGrid, "Editor em lote", "Aplicar em quais Pokémon?", scopeOptions.ToArray());
        if (scope is null) return;
        var selection = scope.StartsWith("Pokémon selecionados", StringComparison.Ordinal);

        IReadOnlyList<(int Box, int Slot)>? slots = null;
        IReadOnlyList<int>? boxes = null;
        if (selection)
        {
            var docId = DocumentId;
            slots = _viewModel.MarkedSlots
                .Where(m => docId is null || !Protection.IsBoxLocked(docId, m.Box))
                .ToList();
            if (slots.Count == 0) { _viewModel.Status = "Todas as boxes marcadas estão travadas"; return; }
        }
        else if (scope == "Esta box")
        {
            if (DocumentId is { } docId && _viewModel.BoxIndex >= 0 && Protection.IsBoxLocked(docId, _viewModel.BoxIndex))
            {
                _viewModel.Status = "Esta box está travada";
                return;
            }
            boxes = [_viewModel.BoxIndex];
        }
        else
        {
            var docId = DocumentId;
            boxes = Enumerable.Range(0, _viewModel.BoxCount)
                .Where(box => docId is null || !Protection.IsBoxLocked(docId, box)).ToList();
            if (boxes.Count == 0) { _viewModel.Status = "Todas as boxes estão travadas"; return; }
        }

        // Chip flow: each entry toggles; the menu re-opens until Apply or Cancel.
        var level = false;
        var maxIvs = false;
        var heal = false;
        var train = false;
        var friendship = false;
        var speciesNames = false;
        var takeItems = false;
        string[]? evSpread = null;
        var evSummary = "none";
        IReadOnlyList<string>? expertLines = null;
        while (true)
        {
            PadOption Chip(string label, bool on, string icon) => new($"{(on ? "✓ " : "")}{label}", IconPath: icon);
            var levelChip = Chip("Nível = 100", level, "level");
            var ivChip = Chip($"{statKind} no máximo ({caps.IvMax} em todos)", maxIvs, "stats");
            var evChip = new PadOption($"Distribuição de EVs: {evSummary}", IconPath: "stats");
            var healChip = Chip("Curar + PP máximo", heal, "heart");
            var trainChip = Chip("Hyper Training (legal $suggest)", train, "gears");
            var friendChip = Chip("Amizade máxima", friendship, "heart");
            var nameChip = Chip("Apelidos → nomes das espécies", speciesNames, "script");
            var itemChip = Chip("Remover itens segurados", takeItems, "item");
            var pick = await PadMenu.ShowAsync(_hostGrid, "Editor em lote", "Ligue ou desligue as operações e depois aplique.",
                levelChip, ivChip, evChip, healChip, trainChip, friendChip, nameChip, itemChip,
                new PadOption("Instruções avançadas…", IconPath: "code", Detail: "Adicione suas próprias linhas de lote do PKHeX além das escolhas acima."),
                new PadOption("Aplicar", IconPath: "confirm"),
                new PadOption("Cancelar", IconPath: "close"));
            if (pick is null or "Cancelar") return;
            if (pick == "Aplicar") break;
            if (pick == levelChip.Label) level = !level;
            else if (pick == ivChip.Label) maxIvs = !maxIvs;
            else if (pick == healChip.Label) heal = !heal;
            else if (pick == trainChip.Label) train = !train;
            else if (pick == friendChip.Label) friendship = !friendship;
            else if (pick == nameChip.Label) speciesNames = !speciesNames;
            else if (pick == itemChip.Label) takeItems = !takeItems;
            else if (pick == evChip.Label)
            {
                var spreads = new (string Label, string Summary, string[] Instructions)[]
                {
                    ("Atacante físico (252 Atk / 252 Spe / 4 HP)", "Atk/Spe 252+4", ["EV_HP=4", "EV_ATK=252", "EV_SPE=252"]),
                    ("Atacante especial (252 SpA / 252 Spe / 4 HP)", "SpA/Spe 252+4", ["EV_HP=4", "EV_SPA=252", "EV_SPE=252"]),
                    ("Defensor físico (252 HP / 252 Def / 4 SpD)", "HP/Def 252+4", ["EV_HP=252", "EV_DEF=252", "EV_SPD=4"]),
                    ("Defensor especial (252 HP / 252 SpD / 4 Def)", "HP/SpD 252+4", ["EV_HP=252", "EV_SPD=252", "EV_DEF=4"]),
                    ("Zerar EVs (todos em zero)", "reset", ["EV_HP=0", "EV_ATK=0", "EV_DEF=0", "EV_SPA=0", "EV_SPD=0", "EV_SPE=0"]),
                    ("Manter os EVs como estão", "none", []),
                };
                var spreadPick = await PadMenu.ShowAsync(_hostGrid, "Distribuição de EVs", "Uma distribuição substitui a escolha anterior.",
                    spreads.Select(s => new PadOption(s.Label, IconPath: "stats")).ToArray());
                var chosen = spreads.FirstOrDefault(s => s.Label == spreadPick);
                if (chosen.Label is not null)
                {
                    evSpread = chosen.Instructions;
                    evSummary = chosen.Summary;
                }
            }
            else
            {
                var text = await TextPopup.ShowAsync(_hostGrid, "Instruções avançadas",
                    $"Uma por linha, no estilo PKHeX, somadas às opções:\nLevel=100\nIV_HP={caps.IvMax}\nShiny=Yes\nEV_ATK={caps.EvMax}");
                if (!string.IsNullOrWhiteSpace(text))
                    expertLines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        var plan = new List<string>();
        if (level) plan.Add("Level=100");
        if (maxIvs)
            foreach (var stat in new[] { "HP", "ATK", "DEF", "SPA", "SPD", "SPE" })
                plan.Add($"IV_{stat}={caps.IvMax}");
        if (evSpread is { Length: > 0 }) plan.AddRange(evSpread);
        if (heal)
        {
            // PP Ups first so Heal's built-in PP refill tops off at the raised maximum.
            plan.AddRange(["Move1_PPUps=3", "Move2_PPUps=3", "Move3_PPUps=3", "Move4_PPUps=3", "Heal"]);
        }
        if (train) plan.Add("HyperTrain=$suggest");
        if (friendship) plan.Add("Friendship=255");
        if (speciesNames) plan.Add("IsNicknamed=false");
        if (takeItems) plan.Add("HeldItem=0");
        if (expertLines is not null) plan.AddRange(expertLines.Where(line => line.Length > 0));
        if (plan.Count == 0) { _viewModel.Status = "Nenhuma operação selecionada"; return; }

        var mons = 0;
        if (slots is not null)
        {
            mons = slots.Count(s => !session.ReadEntity(s.Box, s.Slot).IsEmpty);
        }
        else
        {
            foreach (var box in boxes!)
            {
                var slotCount = box == -1 ? 6 : BoxGridRenderer.Columns * BoxGridRenderer.Rows;
                for (var slot = 0; slot < slotCount; slot++)
                    if (!session.ReadEntity(box, slot).IsEmpty) mons++;
            }
        }
        var scopeText = selection
            ? $"{slots!.Count} Pokémon selecionados"
            : boxes!.Count == 1
                ? (_viewModel.BoxIndex == -1 ? "a equipe" : $"box {boxes[0] + 1:00}")
                : (boxes.Count == _viewModel.BoxCount ? "todas as boxes" : $"{boxes.Count} boxes destravadas");

        // Dry run first: the plan runs on copies so the confirm can say what would change
        // and what would turn illegal before anything is written.
        BatchDryRun? dryRun = null;
        if (InfoPickers.Info is { } info)
        {
            var rehearsal = LoadingOverlay.Show(_hostGrid, "Verificando…", "Testando a edição em lote em cópias primeiro.");
            try { dryRun = await Task.Run(() => info.DryRunBatch(session, plan, boxes, slots)); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }
            finally { rehearsal.Close(); }
        }
        if (dryRun is { Targeted: > 0, Affected: 0 })
        {
            await PadMenu.ShowAsync(_hostGrid, "Nada mudaria", $"Os {dryRun.Targeted} Pokémon em {scopeText} já atendem a todas as operações.", "OK");
            return;
        }
        var impact = dryRun is null
            ? $"Vai aplicar {plan.Count} operação(ões) a {mons} Pokémon em {scopeText}."
            : $"{dryRun.Affected} de {dryRun.Targeted} Pokémon em {scopeText} mudariam." +
              (dryRun.BecomeIllegal > 0 ? $"\n⚠ {dryRun.BecomeIllegal} ficariam ILEGAIS." : "\nNenhum ficaria ilegal.") +
              (dryRun.AlreadyIllegal > 0 ? $"\n{dryRun.AlreadyIllegal} deles já são ilegais." : "");
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, dryRun is { BecomeIllegal: > 0 } ? "Aplicar? alguns ficariam ilegais" : "Aplicar edição em lote?",
            $"{impact}\n{string.Join(" · ", plan)}\nUma única gravação, com backup.",
            "Aplicar");
        if (!confirmed) return;

        var ok = await _viewModel.RunMutationAsync(s =>
        {
            var touched = slots is not null ? s.BatchApplySlots(slots, plan) : s.BatchApply(plan, boxes);
            return touched > 0
                ? new GenerationOutcome(true, $"Lote aplicado a {touched} Pokémon (legalidade não verificada de novo).")
                : new GenerationOutcome(false, "Nada para editar nesse escopo.");
        }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
        if (ok)
            _viewModel.RefreshAllSlots();
        _canvas.InvalidateSurface();
    }

    private async Task ShowSaveDataAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var options = Menu(
            new PadOption("Cartão de treinador", IconPath: "trainer"),
            new PadOption("Mochila e itens", IconPath: "bag"),
            new PadOption("Pokédex", IconPath: "pokedex"),
            Allowed(SaveAction.EditTrainer, new("Moda", IconPath: "box", Detail: "Desbloqueia todas as roupas que este save pode ter legalmente (X, Y, Sword e Shield).")),
            new PadOption("Recordes do treinador", IconPath: "records", Detail: "Os recordes de treinador do próprio jogo, só para ver."),
            Allowed(SaveAction.WriteRawBytes, new("Manipulação de bytes", IconPath: "hex", Detail: "Edite os bytes brutos do arquivo de save. Gravado uma vez, quando você fecha.")),
            // Viewable in Hardcore mode; the editor itself refuses writes (EditWorld).
            Engine.EventFlagService.IsSupported(session) ? new PadOption("Flags de evento", IconPath: "check", Detail: "Ligue ou desligue flags de história e de eventos, e edite valores de eventos.") : null,
            new PadOption("Cartões Mágicos", IconPath: "events", Detail: "Eventos de Mystery Gift e os cartões de presente guardados neste save."),
            // Viewable in Hardcore mode; each world editor refuses writes itself.
            WorldEventsMenu.HasAny(session) ? new PadOption("Mundo e eventos", IconPath: "map", Detail: "Relógios, Pokémon errantes, encontros únicos e outros dados do mundo.") : null,
            new PadOption("Exportar save modificado", IconPath: "export", Detail: "Compartilhe uma cópia do arquivo de save editado."),
            new PadOption("Pontos de restauração", IconPath: "history", Detail: "Backups feitos antes de cada mudança; volte para um deles."),
            new PadOption("Fechar save", IconPath: "quit")).ToList();
        if (session.GetGrandUndergroundItems().Count != 0 && Guard.Allows(SaveAction.EditInventory))
            options.Insert(2, new PadOption("Grand Underground", IconPath: "underground", Detail: "Os itens do Grand Underground, guardados separados da Mochila."));
        if (session.SupportsCompassSettings && Guard.Allows(SaveAction.EditTrainer))
            options.Insert(2, new PadOption("Configurações do Compass", IconPath: "settings", Detail: "Opções do Pokémon Compass, como exp share e limite de nível."));
        var choice = await PadMenu.ShowAsync(_hostGrid, "Dados do save", Note(null), options.ToArray());
        switch (choice)
        {
            case "Cartão de treinador": await ShowTrainerCardAsync(); return;
            case "Exportar save modificado": await ExportModifiedSaveAsync(session); return;
            case "Fechar save": await Navigation.PopAsync(); return;
            case "Mochila e itens": await ShowBagAsync(); return;
            case "Grand Underground": await GrandUndergroundEditor.ShowAsync(_hostGrid, session, _viewModel); return;
            case "Configurações do Compass": await ShowCompassSettingsAsync(session); return;
            case "Pokédex": await ShowDexMenuAsync(); return;
            case "Moda": await ShowFashionAsync(); return;
            case "Recordes do treinador": await ShowTrainerRecordsAsync(); return;
            case "Manipulação de bytes": await HexEditorPage.ShowAsync(_hostGrid, _viewModel, session); return;
            case "Flags de evento": await EventFlagsEditor.ShowAsync(_hostGrid, session, _viewModel); return;
            case "Mundo e eventos": await WorldEventsMenu.ShowAsync(_hostGrid, session, _viewModel); return;
            case "Cartões Mágicos":
            {
                var wonderChoice = await PadMenu.ShowAsync(_hostGrid, "Cartões Mágicos", Note(null),
                    Menu(Allowed(SaveAction.InjectEvent, new PadOption("Galeria de eventos", IconPath: "events", Detail: "Todos os Mystery Gifts que este save pode receber.")),
                        new PadOption("Caixa de entrada do save", IconPath: "inbox", Detail: "Os cartões de Mystery Gift guardados neste save, só para ver."),
                        Engine.KeyItemEventService.IsSupported(session) ? new PadOption("Eventos de itens-chave", IconPath: "key", Detail: "Eventos de tíquete como o Eon Ticket, o Old Sea Map ou o Member Card.") : null,
                        Engine.SaveBlockEditorService.IsSupported(session) ? new PadOption("Blocos do save", IconPath: "blocks", Detail: "Navegue pelos blocos do save e edite valores simples de liga/desliga e números.") : null));
                if (wonderChoice == "Caixa de entrada do save")
                {
                    await MysteryGiftInboxEditor.ShowAsync(_hostGrid, session);
                    return;
                }
                if (wonderChoice == "Eventos de itens-chave")
                {
                    await KeyItemEventsEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
                if (wonderChoice == "Blocos do save")
                {
                    await SaveBlockEditor.ShowAsync(_hostGrid, session, _viewModel);
                    return;
                }
                if (wonderChoice != "Galeria de eventos") return;
                await EventGallery.ShowAsync(_hostGrid, _viewModel, session, targetSlot: null, () => _canvas.InvalidateSurface());
                return;
            }
            case "Pontos de restauração": await PushAsync<BackupHistoryPage>(); return;
        }
    }

    /// <summary>Exports the live edited save through the Android share sheet.</summary>
    private async Task ExportModifiedSaveAsync(Domain.ISaveEngineSession session)
    {
        try
        {
            var sourceName = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?
                .Current?.Document.DisplayName;
            var originalName = string.IsNullOrWhiteSpace(sourceName) ? "pkforge-save" : sourceName;
            var baseName = Path.GetFileNameWithoutExtension(originalName);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "pkforge-save";
            foreach (var invalid in Path.GetInvalidFileNameChars())
                baseName = baseName.Replace(invalid, '_');
            var extension = Path.GetExtension(originalName);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".sav";
            var path = Path.Combine(FileSystem.CacheDirectory, $"{baseName}-modified{extension}");
            await File.WriteAllBytesAsync(path, session.Serialize().ToArray());
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "Exportar save modificado",
                File = new ShareFile(path),
            });
            _viewModel.Status = $"{Path.GetFileName(path)} exportado";
        }
        catch (Exception error)
        {
            _viewModel.Status = $"A exportação do save falhou: {error.Message}";
        }
    }

    /// <summary>
    /// Pokemon Compass romhack settings: the confirmed QoL toggles (exp share, level
    /// cap, spawn rate...). Each change is one backed-up write through the normal path.
    /// </summary>
    private async Task ShowCompassSettingsAsync(Domain.ISaveEngineSession session)
    {
        if (Denied(SaveAction.EditTrainer)) return;
        while (true)
        {
            var settings = session.GetCompassSettings();
            if (settings.Count == 0)
            {
                _viewModel.Status = "Nenhuma configuração do Compass encontrada neste save.";
                return;
            }

            var options = settings
                .Select(setting => new PadOption($"{setting.Name}: {setting.Choices[setting.Selected]}", IconPath: "settings"))
                .Append(new PadOption("Fechar", IconPath: "close"))
                .ToArray();
            var choice = await PadMenu.ShowAsync(_hostGrid, "Configurações do Compass",
                "Opções do Pokémon Compass. Uma gravação, com backup, por mudança.", options);
            if (choice is null or "Fechar") return;

            var setting = settings.FirstOrDefault(s => choice.StartsWith(s.Name + ":", StringComparison.Ordinal));
            if (setting is null) return;
            var picked = await PickerMenu.ShowAsync(_hostGrid, setting.Name,
                setting.Choices.Select((label, index) => new PickItem(index, label)).ToList(), setting.Selected);
            if (picked is null) continue;
            var label = setting.Choices[picked.Id];

            var ok = await _viewModel.RunMutationAsync(s =>
                s.SetCompassSetting(setting.Id, picked.Id)
                    ? new GenerationOutcome(true, $"{setting.Name} definido como {label}.")
                    : new GenerationOutcome(false, "Não foi possível aplicar essa configuração do Compass."),
                Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                changeDescription: $"Compass: {setting.Name} -> {label}",
                action: SaveAction.EditTrainer);
            if (ok)
                _viewModel.Status = $"Compass: {setting.Name} = {label}";
        }
    }

    /// <summary>Trainer card: view + edit name, IDs, money, gender - written safely like everything; view only in Hardcore.</summary>
    private async Task ShowTrainerCardAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var trainer = session.GetTrainer();

        // Hardcore: a read-only card (locked fields, no SAVE), so there is nothing to refuse.
        var readOnly = Guard.Blocks(SaveAction.EditTrainer);
        var updated = await TrainerCardPopup.ShowAsync(_hostGrid, trainer, readOnly);
        if (readOnly || updated is null || updated == trainer) return;

        await _viewModel.RunMutationAsync(s =>
        {
            s.SetTrainer(updated);
            return new GenerationOutcome(true, "Cartão de treinador atualizado.");
        }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.EditTrainer);
    }

    private async Task ShowFashionAsync()
    {
        if (Denied(SaveAction.EditTrainer)) return;
        var session = _sessionsFor();
        if (session is null) return;
        if (!session.SupportsLegalFashionUnlock)
        {
            await EditorMenu.ShowAsync(_hostGrid, "Moda",
                "O desbloqueio legal de roupas só está disponível para Pokémon X, Y, Sword e Shield.", "OK");
            return;
        }
        if (session.SupportsStylePoints)
        {
            await ShowFashionXYAsync(session);
            return;
        }
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Desbloquear roupas legais?",
            "Desbloqueia todas as roupas que este save de Sword ou Shield pode ter legitimamente. Um ponto de restauração é criado antes.", "Desbloquear");
        if (!confirmed) return;
        await _viewModel.RunMutationAsync(s =>
        {
            s.UnlockAllLegalFashion();
            return new GenerationOutcome(true, "Todas as roupas legais foram desbloqueadas.");
        }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.EditTrainer);
    }

    private async Task ShowFashionXYAsync(ISaveEngineSession session)
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "Moda", Note(null),
            new PadOption("Desbloquear todas as roupas", IconPath: "box", Detail: "Todas as roupas e acessórios que seu treinador pode usar."),
            new PadOption("Style máximo", IconPath: "trainer", Detail: $"O Style está em {session.GetStylePoints()} de {byte.MaxValue}. Um Style alto abre as butiques e restaurantes de Lumiose."));
        switch (choice)
        {
            case "Desbloquear todas as roupas":
                if (!await PadMenu.ConfirmAsync(_hostGrid, "Desbloquear todas as roupas?",
                    "Desbloqueia todas as roupas que este save de X ou Y pode ter. Um ponto de restauração é criado antes.", "Desbloquear")) return;
                await _viewModel.RunMutationAsync(s =>
                {
                    s.UnlockAllLegalFashion();
                    return new GenerationOutcome(true, "Todas as roupas foram desbloqueadas.");
                }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.EditTrainer);
                return;
            case "Style máximo":
                if (!await PadMenu.ConfirmAsync(_hostGrid, "Colocar o Style no máximo?",
                    "Libera todas as butiques e restaurantes de Lumiose sem precisar ganhar Style. Um ponto de restauração é criado antes.", "Definir")) return;
                await _viewModel.RunMutationAsync(s =>
                {
                    s.SetStylePoints(byte.MaxValue);
                    return new GenerationOutcome(true, "Style colocado no máximo.");
                }, Math.Max(0, _viewModel.SelectedSlot), action: SaveAction.EditTrainer);
                return;
        }
    }

    private async Task ShowTrainerRecordsAsync()
    {
        var session = _sessionsFor();
        if (session is not null) await TrainerRecordsEditor.ShowAsync(_hostGrid, session);
    }

    private async Task ShowTrainerProfilesAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var store = IPlatformApplication.Current!.Services.GetRequiredService<TrainerProfileStore>();

        while (true)
        {
            var profiles = store.Profiles;
            var options = Menu(
                new("Salvar treinador atual como perfil", IconPath: "profile"),
                _viewModel.SelectedSlot >= 0 && profiles.Count > 0
                    ? Allowed(SaveAction.EditMon, new("Aplicar perfil ao Pokémon selecionado", IconPath: "profile", Detail: "Torna um perfil salvo o treinador original deste Pokémon."))
                    : null,
                profiles.Count > 0 ? new("Apagar um perfil", IconPath: "delete") : null,
                new(store.UseCurrentTrainerForGeneration
                    ? "Pokémon gerados com o treinador do save: LIGADO"
                    : "Pokémon gerados com o treinador do save: DESLIGADO", IconPath: "profile", Detail: "LIGADO faz os Pokémon que você cria pertencerem ao treinador do save aberto.")).ToList();

            var choice = await PadMenu.ShowAsync(_hostGrid, "Perfis de treinador",
                Note(profiles.Count == 0 ? "Nenhum perfil com nome ainda." : string.Join('\n', profiles.Select(ProfileSummary))),
                options.ToArray());
            if (choice is null) return;

            if (choice == "Salvar treinador atual como perfil")
            {
                var name = await TextPopup.ShowLineAsync(_hostGrid, "Nome do perfil", "Nome do perfil");
                if (string.IsNullOrWhiteSpace(name)) continue;
                store.Save(name, session.GetTrainer());
                _viewModel.Status = $"Perfil de treinador '{name.Trim()}' salvo.";
                continue;
            }
            if (choice.StartsWith("Pokémon gerados com o treinador do save:", StringComparison.Ordinal))
            {
                store.SetUseCurrentTrainerForGeneration(!store.UseCurrentTrainerForGeneration);
                continue;
            }

            var labels = profiles.Select(ProfileSummary).ToArray();
            var selected = await PadMenu.ShowAsync(_hostGrid,
                choice == "Apagar um perfil" ? "APAGAR PERFIL" : "APLICAR PERFIL DE TREINADOR", null, labels);
            var index = Array.IndexOf(labels, selected);
            if (index < 0) continue;
            var profile = profiles[index];
            if (choice == "Apagar um perfil")
            {
                var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Apagar perfil?", profile.DisplayName, "Apagar");
                if (confirmed) store.Delete(profile.Id);
                continue;
            }

            var slot = _viewModel.SelectedSlot;
            await _viewModel.RunMutationAsync(s => s.MakeMine(_viewModel.BoxIndex, slot, profile), slot, action: SaveAction.EditMon);
            _canvas.InvalidateSurface();
            return;
        }
    }

    private static string ProfileSummary(TrainerProfile profile) =>
        $"{profile.DisplayName} · {profile.OriginalTrainer} · {profile.TID}/{profile.SID} · {(profile.Gender == 1 ? "F" : "M")}";

    /// <summary>Bag: the navy inventory editor - pocket pills, item rows with count discs.</summary>
    private async Task ShowBagAsync()
    {
        var session = _sessionsFor();
        if (session is null) return;
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        if (session.GetPokeBeans().Count != 0)
        {
            var choice = await PadMenu.ShowAsync(_hostGrid, "Mochila e itens", null,
                new PadOption("Mochila", IconPath: "bag"),
                new PadOption("Poké Beans", IconPath: "item"));
            if (choice is null) return;
            if (choice == "Poké Beans")
            {
                await PokeBeansEditor.ShowAsync(_hostGrid, session, _viewModel);
                return;
            }
        }
        await BagEditor.ShowAsync(_hostGrid, session, _viewModel, data);
    }

    /// <summary>
    /// The bag editor overlay: the navy inventory world. Pockets are bag pills (cyan;
    /// yellow-green rim and gold fill when active), items are white PixelUI rows with
    /// round count discs. Tap a name for the exact-count sheet; left/right adjust the
    /// selected count, shoulder L/R turn pockets, up/down walk rows, A activates.
    /// </summary>
    private sealed class BagEditor : IPadHandler
    {
        private readonly Grid _host;
        private readonly ISaveEngineSession _session;
        private readonly BoxBrowserViewModel _viewModel;
        private readonly IGameDataService _data;
        private readonly ScrollView _scroll;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HorizontalStackLayout _pockets = new() { Spacing = 12, HorizontalOptions = LayoutOptions.Center };
        private readonly VerticalStackLayout _rows = new() { Spacing = 4 };
        private readonly int _slotSeed;

        private IReadOnlyList<BagPouch> _bag = [];
        private List<BagRow> _itemRows = [];
        private View _addRow = null!;
        private View _presetRow = null!;
        private Grid _overlay = null!;
        private int _pouchIndex;
        private int _cursor;
        private readonly Dictionary<(string Pouch, int Item), int> _pendingCounts = [];
        private readonly SemaphoreSlim _pendingWriteGate = new(1, 1);
        private int _pendingRevision;
        private bool _closing;

        public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, IGameDataService data)
        {
            var editor = new BagEditor(host, session, viewModel, data, Math.Max(0, viewModel.SelectedSlot));
            await editor.RunAsync();
        }

        private BagEditor(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, IGameDataService data, int slotSeed)
        {
            _host = host;
            _session = session;
            _viewModel = viewModel;
            _data = data;
            // Resolve this once from the open save. Leaving it empty made every row
            // render as #id and filtered every legal id out of ADD ITEM.
            _itemNames = session.GetItemNames();
            _slotSeed = slotSeed;

            var title = new HorizontalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    PksmIcons.Icon("bag", 22, PksmIcons.White),
                    new Label
                    {
                        Text = "Mochila",
                        FontFamily = DsChrome.PixelFont,
                        FontSize = UiTokens.TextHeading,
                        TextColor = UiTokens.Ink0,
                        VerticalTextAlignment = TextAlignment.Center,
                    },
                },
            };

            _scroll = new ScrollView { Content = _rows };

            var hint = new Label
            {
                Text = "Esquerda / direita ajusta · L / R troca de bolso · A abre a quantidade exata",
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextSmall,
                TextColor = UiTokens.InkSoft,
            };
            // Live status INSIDE the bag window: outcomes must be visible here, not on
            // the page footer hidden behind this overlay. Every async action reports.
            BagStatus = new Label
            {
                Text = "",
                FontFamily = DsChrome.PixelFont,
                FontSize = UiTokens.TextSmall,
                TextColor = UiTokens.Ink0,
                LineBreakMode = LineBreakMode.WordWrap,
                MaxLines = 2,
            };

            var body = new Grid
            {
                RowSpacing = 10,
                RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
                Children = { title, _pockets, _scroll, hint, BagStatus },
            };
            Grid.SetRow(_pockets, 1);
            Grid.SetRow(_scroll, 2);
            Grid.SetRow(hint, 3);
            Grid.SetRow(BagStatus, 4);

            var window = Kit.Panel(body, padding: 14);
            window.WidthRequest = Math.Min(host.Width > 0 ? host.Width - 24 : 560, 560);
            window.HeightRequest = host.Height > 0 ? host.Height - 24 : 340;
            window.HorizontalOptions = LayoutOptions.Center;
            window.VerticalOptions = LayoutOptions.Center;
            _overlay = Kit.AttachOverlay(host, window, () => _ = CloseAsync());
            Kit.AnimateIn(window);
        }

        private async Task RunAsync()
        {
            _bag = _session.GetBag();
            if (_bag.Count == 0)
            {
                _viewModel.Status = "Este jogo não tem uma mochila editável.";
                await CloseAsync();
                return;
            }
            Rebuild();
            IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
            try
            {
                await _closed.Task;
            }
            finally
            {
                IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
            }
        }

        internal Label BagStatus = null!;

        private void Report(string message)
        {
            MainThread.BeginInvokeOnMainThread(() => BagStatus.Text = message);
        }
        /// <summary>The Hardcore refusal inside the bag: the reason lands in the
        /// window's own status line, never swallowed. Doubles as the no-op handler for
        /// the disabled row buttons.</summary>
        private bool Refuse()
        {
            if (!HardcoreMode.Blocks(SaveAction.EditInventory, out var status)) return false;
            Report(status);
            return true;
        }

        // The OPEN GAME's item table: Gen 1 Rare Candy lives at a different index than
        // in the modern list, which is what misnamed everything before.
        private readonly IReadOnlyList<string> _itemNames;
        private string ItemName(int id) =>
            id < _itemNames.Count && _itemNames[id].Length > 0 ? _itemNames[id] : $"#{id}";

        private async Task CyclePouchAsync(int delta)
        {
            if (_bag.Count == 0) return;
            await FlushPendingAsync();
            _pouchIndex = ((_pouchIndex + delta) % _bag.Count + _bag.Count) % _bag.Count;
            _cursor = 0;
            Rebuild();
        }

        private async Task PickPouchAsync()
        {
            var pouches = _bag.Select((p, i) => new PadOption($"{p.Name} ({p.Items.Count})", IconPath: "bag")).ToArray();
            var choice = await PadMenu.ShowAsync(_host, "Bolso", null, pouches);
            if (choice is null) return;
            var index = Array.FindIndex(pouches, o => o.Label == choice);
            if (index >= 0) { _pouchIndex = index; _cursor = 0; Rebuild(); }
        }

        /// <summary>Re-reads the bag and rebuilds the switcher + rows (after every write or pouch switch).</summary>
        private void Rebuild()
        {
            _bag = _session.GetBag();
            if (_pouchIndex >= _bag.Count) _pouchIndex = 0;
            var pouch = _bag[_pouchIndex];

            // One compact switcher row instead of wrapping pills: Gen 4's eight pouches
            // were eating half the window and hiding the bag. Arrows cycle; tapping the
            // name opens the full pouch list; L/R on the pad do the same.
            _pockets.Children.Clear();
            var prev = Kit.MiniCapsule("<", UiTokens.BagCyan);
            prev.Clicked += (_, _) => _ = CyclePouchAsync(-1);
            var next = Kit.MiniCapsule(">", UiTokens.BagCyan);
            next.Clicked += (_, _) => _ = CyclePouchAsync(+1);
            var label = new Label
            {
                Text = $" {_bag[_pouchIndex].Name} ({pouch.Items.Count}) ",
                FontFamily = DsChrome.PixelFont,
                FontSize = 17,
                FontAttributes = FontAttributes.Bold,
                TextColor = UiTokens.Ink0,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalOptions = LayoutOptions.Center,
            };
            var tapList = new TapGestureRecognizer();
            tapList.Tapped += (_, _) => _ = PickPouchAsync();
            label.GestureRecognizers.Add(tapList);
            _pockets.Children.Add(prev);
            _pockets.Children.Add(label);
            _pockets.Children.Add(next);

            _rows.Children.Clear();
            _itemRows = [];
            // Hardcore mode: the bag stays readable and the pouch switcher still works,
            // but every editing affordance is disabled and says why.
            var canEdit = Guard.CanEditInventory;
            var icons = new List<Image>(pouch.Items.Count);
            foreach (var item in pouch.Items)
            {
                var captured = item;
                var shownCount = _pendingCounts.GetValueOrDefault((pouch.Name, item.Id), item.Count);
                var icon = new Image { WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
                icons.Add(icon);
                var row = new BagRow(icon, ItemName(item.Id), shownCount)
                {
                    Tapped = canEdit ? () => _ = EditCountAsync(captured) : () => Refuse(),
                    Minus = canEdit ? () => QueueNudge(captured, -1) : () => Refuse(),
                    Plus = canEdit ? () => QueueNudge(captured, +1) : () => Refuse(),
                };
                _itemRows.Add(row);
                _rows.Children.Add(row);
            }

            var add = Kit.Capsule("+  Adicionar item", UiTokens.BagCyan);
            add.FontSize = 14;
            add.Margin = new Thickness(4, 6, 4, 2);
            add.IsEnabled = canEdit;
            add.Clicked += (_, _) => _ = AddItemAsync();
            _addRow = add;
            _rows.Children.Add(_addRow);

            var presets = Kit.Capsule("Predefinições de itens", UiTokens.BagCyan);
            presets.FontSize = 14;
            presets.Margin = new Thickness(4, 2, 4, 2);
            presets.IsEnabled = canEdit;
            presets.Clicked += (_, _) => _ = ShowPresetsAsync();
            _presetRow = presets;
            _rows.Children.Add(_presetRow);

            Highlight(_cursor);
            _ = LoadIconsAsync(pouch.Items, icons);
            Report(canEdit
                ? $"{pouch.Name} - {pouch.Items.Count} tipos - tabela de nomes {_itemNames.Count}"
                : HardcoreMode.StatusLine);
        }

        private async Task LoadIconsAsync(IReadOnlyList<BagItem> items, IReadOnlyList<Image> targets)
        {
            var placeholder = ItemArt.PlaceholderPath();
            var paths = await Task.WhenAll(items.Select(i => ItemArt.GetAsync(ItemName(i.Id))));
            for (var i = 0; i < targets.Count && i < paths.Length; i++)
                targets[i].Source = ImageSource.FromFile(paths[i] ?? placeholder);
        }

        private async Task EditCountAsync(BagItem item)
        {
            if (Refuse()) return;
            await FlushPendingAsync();
            var name = ItemName(item.Id);
            var current = _session.GetBag().SelectMany(p => p.Items).FirstOrDefault(i => i.Id == item.Id)?.Count ?? item.Count;
            var count = await StatsPopup.ShowSingleAsync(_host, $"{name} - quantidade", current, 999);
            if (count is null) return;
            await WriteAsync(item.Id, count.Value);
        }

        /// <summary>Quantity changes feel immediate, but a quick run of pad presses becomes
        /// one validation, restore point, and SAF write after the user pauses.</summary>
        private void QueueNudge(BagItem item, int delta)
        {
            if (_cursor >= _itemRows.Count) return;
            var pouchName = _bag[_pouchIndex].Name;
            var key = (pouchName, item.Id);
            var current = _pendingCounts.GetValueOrDefault(key, item.Count);
            var count = Math.Clamp(current + delta, 0, 999);
            if (count == current) return;
            _pendingCounts[key] = count;
            _itemRows[_cursor].SetCount(count);
            Report($"{ItemName(item.Id)} x{count} - salvando…");
            var revision = ++_pendingRevision;
            _ = SaveAfterPauseAsync(revision);
        }

        private async Task SaveAfterPauseAsync(int revision)
        {
            await Task.Delay(550);
            if (revision == _pendingRevision)
                await FlushPendingAsync();
        }

        private async Task<bool> FlushPendingAsync()
        {
            await _pendingWriteGate.WaitAsync();
            try
            {
                if (_pendingCounts.Count == 0) return true;
                var changes = _pendingCounts.Select(x => (x.Key.Pouch, x.Key.Item, Count: x.Value)).ToArray();
                _pendingCounts.Clear();
                var outcome = await _viewModel.RunMutationAsync(s =>
                {
                    foreach (var change in changes)
                        s.SetItemCount(change.Pouch, change.Item, change.Count);
                    return new GenerationOutcome(true, $"{changes.Length} ite{(changes.Length == 1 ? "m atualizado" : "ns atualizados")}.");
                }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
                Report(outcome ? "Quantidade do item salva no arquivo - reinicie o jogo para carregá-la" : "A gravação falhou - veja o status");
                Rebuild();
                return outcome;
            }
            finally
            {
                _pendingWriteGate.Release();
            }
        }

        /// <summary>One safe write (backup + atomic), then a fresh read of the whole bag.</summary>
        private async Task WriteAsync(int itemId, int count)
        {
            await FlushPendingAsync();
            var pouchName = _bag[_pouchIndex].Name;
            var name = ItemName(itemId);
            var stored = count;
            var outcome = await _viewModel.RunMutationAsync(s =>
            {
                stored = s.SetItemCount(pouchName, itemId, count);
                return new GenerationOutcome(true, stored == 0 ? $"{name} removed." : $"{name} ×{stored}");
            }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
            Report(outcome ? $"SALVO NO ARQUIVO: {(stored == 0 ? $"{name} REMOVIDO" : $"{name} x{stored}")} - REINICIE O JOGO" : "A GRAVAÇÃO FALHOU - VEJA O STATUS");
            Rebuild();
        }

        private async Task AddItemAsync()
        {
            await FlushPendingAsync();
            var pouchName = _bag[_pouchIndex].Name;
            var gameItems = _itemNames; // the open game's own table
            var legalIds = _session.GetPouchLegalItems(pouchName)
                .Where(id => id < gameItems.Count && gameItems[id].Length > 0)
                .ToList();
            if (legalIds.Count == 0)
            {
                Report($"Nenhum item para adicionar em {pouchName} - toque no nome do bolso para trocar");
                return;
            }

            // Open the picker IMMEDIATELY with cached-or-placeholder art. The old flow
            // blocked on fetching every sprite first, which on a cold cache read as
            // "the button does nothing" for the better part of a minute.
            var itemDirectory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
            var placeholder = ItemArt.PlaceholderPath();
            var legal = legalIds.Select(id =>
            {
                var cached = System.IO.Path.Combine(itemDirectory, ItemArt.Slug(gameItems[id]) + ".png");
                return new PickItem(id, gameItems[id], File.Exists(cached) ? cached : placeholder);
            }).ToList();
            Report($"Adicionando em {pouchName} - {legal.Count} itens");
            _ = Task.Run(async () =>
            {
                foreach (var id in legalIds)
                    await ItemArt.GetAsync(gameItems[id]);
            });
            PickItem? picked;
            try
            {
                picked = await PickerMenu.ShowAsync(_host, $"Adicionar - {pouchName}", legal);
            }
            catch (Exception ex)
            {
                Report($"O seletor falhou: {ex.GetType().Name}: {ex.Message}");
                return;
            }
            if (picked is null) { Report("Cancelado"); return; }
            var count = await StatsPopup.ShowSingleAsync(_host, $"{ItemName(picked.Id)} - quantidade", 0, 999);
            if (count is null) return;
            await WriteAsync(picked.Id, count.Value);
        }

        private static readonly HashSet<string> BallNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Poké Ball", "Great Ball", "Ultra Ball", "Master Ball", "Safari Ball", "Sport Ball",
            "Fast Ball", "Level Ball", "Lure Ball", "Heavy Ball", "Love Ball", "Friend Ball", "Moon Ball",
            "Net Ball", "Dive Ball", "Nest Ball", "Repeat Ball", "Timer Ball", "Luxury Ball", "Premier Ball",
            "Dusk Ball", "Heal Ball", "Quick Ball", "Cherish Ball", "Park Ball", "Dream Ball", "Beast Ball",
            "Strange Ball",
        };

        private static readonly HashSet<string> HealingNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Potion", "Super Potion", "Hyper Potion", "Max Potion", "Full Restore", "Fresh Water",
            "Soda Pop", "Lemonade", "Moomoo Milk", "Energy Powder", "Energy Root", "Revive", "Max Revive",
            "Antidote", "Burn Heal", "Ice Heal", "Awakening", "Paralyze Heal", "Full Heal",
        };

        private async Task ShowPresetsAsync()
        {
            if (!await FlushPendingAsync()) return;
            var choice = await PadMenu.ShowAsync(_host, "Predefinições de itens", "Só os itens legais neste jogo são alterados.",
                new PadOption("Salvar mochila atual como predefinição", IconPath: "preset", Detail: "Guarda o que sua mochila tem agora, para aplicar de novo depois."),
                new PadOption("Minhas predefinições de itens", IconPath: "preset", Detail: "Aplique, atualize, renomeie ou apague suas mochilas salvas."),
                new PadOption("Reabastecer este bolso até 99", IconPath: "fill"),
                new PadOption("Dar todas as Poké Balls x50", IconPath: "ball"),
                new PadOption("Itens de cura x20", IconPath: "heal", Detail: "20 de cada poção, bebida, revive e cura de status."),
                new PadOption("Kit inicial de Nuzlocke", IconPath: "skull", Detail: "10 Poké Balls, 10 Potions, 3 Antidotes, 3 Paralyze Heals, 2 Escape Ropes."),
                new PadOption("Remover todos os itens deste bolso", IconPath: "clear"));
            if (choice is null) return;

            if (choice is "Salvar mochila atual como predefinição" or "Minhas predefinições de itens")
            {
                try
                {
                    var store = new PKForge.Infrastructure.ItemPresetStore(Path.Combine(FileSystem.AppDataDirectory, "item-presets.json"));
                    if (choice == "Salvar mochila atual como predefinição") await SavePersonalPresetAsync(store);
                    else await ShowPersonalPresetsAsync(store);
                }
                catch (Exception ex)
                {
                    Report($"Erro na predefinição: {ex.Message}");
                    await PadMenu.ShowAsync(_host, "Erro na predefinição de itens", ex.Message, "OK");
                }
                return;
            }

            if (choice == "Remover todos os itens deste bolso")
            {
                var confirmed = await PadMenu.ConfirmAsync(_host, "Esvaziar este bolso?",
                    $"Todos os itens de {_bag[_pouchIndex].Name} são removidos. Um ponto de restauração é criado antes.", "Esvaziar");
                if (!confirmed) return;
            }

            var changes = BuildPreset(choice);
            if (changes.Count == 0)
            {
                Report("Nenhum item compatível com esta predefinição");
                return;
            }

            var ok = await _viewModel.RunMutationAsync(s =>
            {
                foreach (var change in changes)
                    s.SetItemCount(change.Pouch, change.Item, change.Count);
                return new GenerationOutcome(true, $"A predefinição alterou {changes.Count} itens.");
            }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
            Report(ok ? $"Predefinição aplicada - {changes.Count} itens" : "A predefinição falhou - veja o status");
            Rebuild();
        }

        private async Task SavePersonalPresetAsync(PKForge.Infrastructure.ItemPresetStore store, ItemPreset? existing = null)
        {
            var names = _session.GetItemNames();
            var entries = _session.GetBag().SelectMany(pouch => pouch.Items
                .Where(item => item.Count > 0 && item.Id > 0 && item.Id < names.Count
                    && !string.IsNullOrWhiteSpace(names[item.Id])
                    && _session.GetPouchLegalItems(pouch.Name).Contains(item.Id))
                .Select(item => new ItemPresetEntry(pouch.Name, item.Id, names[item.Id], item.Count))).ToArray();
            if (entries.Length == 0)
            {
                Report("Nenhum item compatível para salvar numa predefinição");
                return;
            }
            var name = existing?.Name ?? await TextPopup.ShowLineAsync(_host, "Nome da predefinição", "Dê um nome à predefinição da mochila (máx. 60 caracteres)");
            if (string.IsNullOrWhiteSpace(name)) return;
            store.Save(new ItemPreset(existing?.Id ?? Guid.NewGuid().ToString("N"), name, _session.Generation, entries));
            Report($"Predefinição salva - {entries.Length} itens");
        }

        private async Task ShowPersonalPresetsAsync(PKForge.Infrastructure.ItemPresetStore store)
        {
            while (true)
            {
                var presets = store.Read();
                if (presets.Count == 0)
                {
                    await PadMenu.ShowAsync(_host, "Minhas predefinições de itens", "Ajuste sua mochila e depois escolha Salvar mochila atual como predefinição.", "OK");
                    return;
                }
                var labels = presets.Select(p => $"{p.Name} (Gen {p.Generation})").ToArray();
                var selected = await PadMenu.ShowAsync(_host, "Minhas predefinições de itens", "As predefinições aplicam as quantidades salvas. Os outros itens continuam na mochila.", labels);
                var index = Array.IndexOf(labels, selected);
                if (index < 0) return;
                var preset = presets[index];
                var action = await PadMenu.ShowAsync(_host, preset.Name,
                    $"{preset.Items.Count} itens. Os itens compatíveis valem dentro da Gen {preset.Generation}.",
                    new PadOption("Aplicar predefinição", IconPath: "confirm"),
                    new PadOption("Atualizar com a mochila atual", IconPath: "refresh", Detail: "Substitui esta predefinição pelo que sua mochila tem agora."),
                    new PadOption("Renomear", IconPath: "rename"),
                    new PadOption("Apagar", IconPath: "delete"));
                if (action == "Renomear")
                {
                    var name = await TextPopup.ShowLineAsync(_host, "Renomear predefinição", "Nome da predefinição", preset.Name);
                    if (!string.IsNullOrWhiteSpace(name)) store.Save(preset with { Name = name });
                }
                else if (action == "Apagar")
                {
                    if (await PadMenu.ConfirmAsync(_host, "Apagar predefinição?", preset.Name, "Apagar")) store.Delete(preset.Id);
                }
                else if (action == "Atualizar com a mochila atual")
                {
                    if (await PadMenu.ConfirmAsync(_host, "Atualizar predefinição?", "Substituir esta predefinição pela mochila atual e a geração dela?", "Atualizar"))
                        await SavePersonalPresetAsync(store, preset);
                }
                else if (action == "Aplicar predefinição")
                {
                    var pouches = _session.GetBag().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
                    var changes = preset.CompatibleItems(_session.Generation, _session.GetItemNames(),
                        pouch => pouches.Contains(pouch) ? _session.GetPouchLegalItems(pouch) : []);
                    if (changes.Count == 0)
                    {
                        await PadMenu.ShowAsync(_host, "Nenhum item compatível", $"Esta predefinição foi salva para a Gen {preset.Generation}. Só itens legais compatíveis podem ser aplicados.", "OK");
                        continue;
                    }
                    if (!await PadMenu.ConfirmAsync(_host, "Aplicar predefinição de itens?",
                        $"Definir quantidades para {changes.Count} itens; ignorar {preset.Items.Count - changes.Count} itens incompatíveis. Os limites de quantidade do jogo valem. Os outros itens são mantidos.", "Aplicar")) continue;
                    var ok = await _viewModel.RunMutationAsync(s =>
                    {
                        var original = s.GetBag().SelectMany(pouch => pouch.Items.Select(item =>
                            (Key: (pouch.Name, item.Id), item.Count)))
                            .ToDictionary(item => item.Key, item => item.Count);
                        var applied = new List<ItemPresetEntry>();
                        try
                        {
                            foreach (var item in changes)
                            {
                                s.SetItemCount(item.Pouch, item.ItemId, item.Count);
                                applied.Add(item);
                            }
                        }
                        catch
                        {
                            // A full pouch must not leave half of a preset in the live session.
                            foreach (var item in applied.AsEnumerable().Reverse())
                                s.SetItemCount(item.Pouch, item.ItemId, original.GetValueOrDefault((item.Pouch, item.ItemId)));
                            throw;
                        }
                        return new GenerationOutcome(true, $"A predefinição alterou {changes.Count} itens.");
                    }, _slotSeed, refreshSlot: false, action: SaveAction.EditInventory);
                    Report(ok ? $"Predefinição aplicada - {changes.Count} itens" : "A predefinição falhou - veja o status");
                    Rebuild();
                    return;
                }
            }
        }

        private List<(string Pouch, int Item, int Count)> BuildPreset(string choice)
        {
            if (choice == "Reabastecer este bolso até 99")
                return _bag[_pouchIndex].Items.Select(i => (_bag[_pouchIndex].Name, i.Id, 99)).ToList();
            if (choice == "Remover todos os itens deste bolso")
                return _bag[_pouchIndex].Items.Select(i => (_bag[_pouchIndex].Name, i.Id, 0)).ToList();

            var desired = choice switch
            {
                "Dar todas as Poké Balls x50" => BallNames.ToDictionary(name => name, _ => 50, StringComparer.OrdinalIgnoreCase),
                "Itens de cura x20" => HealingNames.ToDictionary(name => name, _ => 20, StringComparer.OrdinalIgnoreCase),
                "Kit inicial de Nuzlocke" => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Poké Ball"] = 10,
                    ["Potion"] = 10,
                    ["Antidote"] = 3,
                    ["Paralyze Heal"] = 3,
                    ["Escape Rope"] = 2,
                },
                _ => [],
            };

            var result = new List<(string Pouch, int Item, int Count)>();
            foreach (var pouch in _bag)
            {
                foreach (var id in _session.GetPouchLegalItems(pouch.Name))
                {
                    var name = ItemName(id);
                    if (desired.TryGetValue(name, out var count))
                        result.Add((pouch.Name, id, count));
                }
            }
            return result;
        }

        private void Highlight(int index)
        {
            _cursor = Math.Clamp(index, 0, _itemRows.Count + 1);
            for (var i = 0; i < _itemRows.Count; i++)
                _itemRows[i].Selected = i == _cursor;
            if (_addRow is Button capsule)
                capsule.BackgroundColor = _cursor == _itemRows.Count ? UiTokens.ChoiceFillPress : UiTokens.ChoiceFill;
            if (_presetRow is Button preset)
                preset.BackgroundColor = _cursor == _itemRows.Count + 1 ? UiTokens.ChoiceFillPress : UiTokens.ChoiceFill;
            var target = _cursor < _itemRows.Count ? (View)_itemRows[_cursor]
                : _cursor == _itemRows.Count ? _addRow : _presetRow;
            _ = _scroll.ScrollToAsync(target, ScrollToPosition.MakeVisible, false);
        }

        public bool OnPadButton(PadButton button)
        {
            switch (button)
            {
                case PadButton.Up:
                    Highlight(_cursor - 1);
                    return true;
                case PadButton.Down:
                    Highlight(_cursor + 1);
                    return true;
                case PadButton.Left:
                    if (_cursor < _itemRows.Count)
                        QueueNudge(_bag[_pouchIndex].Items[_cursor], -1);
                    return true;
                case PadButton.Right:
                    if (_cursor < _itemRows.Count)
                        QueueNudge(_bag[_pouchIndex].Items[_cursor], +1);
                    return true;
                case PadButton.L:
                    _ = CyclePouchAsync(-1);
                    return true;
                case PadButton.R:
                    _ = CyclePouchAsync(+1);
                    return true;
                case PadButton.A:
                    if (_cursor < _itemRows.Count)
                        _ = EditCountAsync(_bag[_pouchIndex].Items[_cursor]);
                    else if (_cursor == _itemRows.Count)
                        _ = AddItemAsync();
                    else
                        _ = ShowPresetsAsync();
                    return true;
                case PadButton.B:
                    _ = CloseAsync();
                    return true;
                default:
                    return true; // modal while open
            }
        }

        private async Task CloseAsync()
        {
            if (_closing) return;
            _closing = true;
            await FlushPendingAsync();
            _host.Remove(_overlay);
            _closed.TrySetResult();
        }

        // ── Row chrome, drawn locally in Skia from Pksm tokens ─────────────────

        /// <summary>A pocket tab: the bag pill (cyan idle; yellow-green rim + gold fill active).</summary>
        private sealed class BagPillTab : Grid
        {
            private readonly SKCanvasView _bg;
            private bool _selected;

            public Action? Tapped { get; set; }

            public BagPillTab(string label)
            {
                HeightRequest = 30;
                Margin = new Thickness(0, 0, 6, 6);
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += (_, args) =>
                    PksmPaint.BagPill(args.Surface.Canvas, new SKRect(0, 2, args.Info.Width, args.Info.Height - 2), _selected);
                Children.Add(_bg);
                Children.Add(new Label
                {
                    Text = label,
                    FontFamily = DsChrome.PixelFont,
                    FontSize = UiTokens.TextSmall,
                    TextColor = UiTokens.Ink0,
                    VerticalTextAlignment = TextAlignment.Center,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(14, 0),
                    InputTransparent = true,
                });
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Tapped?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public bool Selected
            {
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    _bg.InvalidateSurface();
                }
            }
        }

        /// <summary>One inventory row: item sprite, white PixelUI name, count, count discs.</summary>
        private sealed class BagRow : Grid
        {
            private readonly SKCanvasView _bg;
            private readonly Label _counter;
            private bool _selected;

            public Action? Tapped { get; set; }
            public Action? Minus { get; set; }
            public Action? Plus { get; set; }

            public BagRow(Image icon, string name, int count)
            {
                HeightRequest = 40;
                ColumnDefinitions =
                [
                    new(new GridLength(32)),
                    new(GridLength.Star),
                    new(new GridLength(54)),
                    new(new GridLength(36)),
                    new(new GridLength(36)),
                ];
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += (_, args) => DrawRow(args.Surface.Canvas, args.Info, _selected);

                var label = new Label
                {
                    Text = name,
                    FontFamily = DsChrome.PixelFont,
                    FontSize = UiTokens.TextBody,
                    TextColor = UiTokens.Ink0,
                    VerticalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    InputTransparent = true,
                };
                _counter = new Label
                {
                    Text = $"×{count}",
                    FontFamily = DsChrome.PixelFont,
                    FontSize = UiTokens.TextBody,
                    TextColor = UiTokens.InkSoft,
                    VerticalTextAlignment = TextAlignment.Center,
                    HorizontalTextAlignment = TextAlignment.End,
                    InputTransparent = true,
                };

                Children.Add(_bg);
                Grid.SetColumnSpan(_bg, 5);
                Children.Add(icon);
                Children.Add(label);
                Children.Add(_counter);
                Children.Add(CountDisc(minus: true, () => Minus?.Invoke()));
                Children.Add(CountDisc(minus: false, () => Plus?.Invoke()));
                Grid.SetColumn(icon, 0);
                Grid.SetColumn(label, 1);
                Grid.SetColumn(_counter, 2);
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[4], 3);
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[5], 4);

                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Tapped?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public bool Selected
            {
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    _bg.InvalidateSurface();
                }
            }

            public void SetCount(int count) => _counter.Text = $"×{count}";
        }

        /// <summary>The add-item row: a plus disc and the cyan invitation.</summary>
        private sealed class AddRow : Grid
        {
            private readonly SKCanvasView _bg;
            private bool _selected;

            public Action? Tapped { get; set; }

            public AddRow()
            {
                HeightRequest = 40;
                ColumnDefinitions = [new(new GridLength(32)), new(GridLength.Star)];
                _bg = new SKCanvasView { InputTransparent = true };
                _bg.PaintSurface += (_, args) => DrawRow(args.Surface.Canvas, args.Info, _selected);
                Children.Add(_bg);
                Grid.SetColumnSpan(_bg, 2);
                Children.Add(CountDisc(minus: false, () => Tapped?.Invoke()));
                Children.Add(new Label
                {
                    Text = "Adicionar item",
                    FontFamily = DsChrome.PixelFont,
                    FontSize = UiTokens.TextBody,
                    TextColor = UiTokens.Ink0,
                    VerticalTextAlignment = TextAlignment.Center,
                    InputTransparent = true,
                });
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[1], 0);
                SetColumn((Microsoft.Maui.Controls.BindableObject)Children[2], 1);
                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) => Tapped?.Invoke();
                GestureRecognizers.Add(tap);
            }

            public bool Selected
            {
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    _bg.InvalidateSurface();
                }
            }
        }

        /// <summary>A flat list row: soft stripe resting, the selected-button look under the pad cursor.</summary>
        private static void DrawRow(SKCanvas canvas, SKImageInfo info, bool selected) =>
            DsFolderButton.DrawListRow(canvas, info, selected);

        /// <summary>The round count button: navy disc, cyan rim, cyan + or - arms.</summary>
        private static Grid CountDisc(bool minus, Action onTap)
        {
            var disc = new Grid
            {
                HeightRequest = 32,
                WidthRequest = 32,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
            };
            var canvas = new SKCanvasView { InputTransparent = true };
            canvas.PaintSurface += (_, args) =>
            {
                var c = args.Surface.Canvas;
                c.Clear(SKColors.Transparent);
                var info = args.Info;
                var radius = Math.Min(info.Width, info.Height) / 2f - 2f;
                var cx = info.Width / 2f;
                var cy = info.Height / 2f;
                var r = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);
                using var fill = new SKPaint { Color = Pksm.BagNavy, IsAntialias = true };
                using var inner = new SKPaint { Color = Pksm.BagNavyDeep, IsAntialias = true };
                using var rim = new SKPaint { Color = Pksm.LogoGrid, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * (float)DeviceDisplay.MainDisplayInfo.Density };
                using var arm = new SKPaint { Color = Pksm.Ink, IsAntialias = true };
                c.DrawOval(r, fill);
                c.DrawOval(SKRect.Inflate(r, -1.5f, -1.5f), inner);
                c.DrawOval(r, rim);
                var a = radius * 0.55f;
                c.DrawRoundRect(new SKRect(cx - a, cy - 1.5f, cx + a, cy + 1.5f), 1.5f, 1.5f, arm);
                if (!minus)
                    c.DrawRoundRect(new SKRect(cx - 1.5f, cy - a, cx + 1.5f, cy + a), 1.5f, 1.5f, arm);
            };
            disc.Children.Add(canvas);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => onTap();
            disc.GestureRecognizers.Add(tap);
            return disc;
        }
    }

    /// <summary>Pokédex progress + the one-tap complete.</summary>
    private async Task ShowDexMenuAsync()
    {
        // The dex editor's whole job is setting seen/caught flags; the read-only
        // collection dex ("Collection dex…" in STORAGE TOOLS) stays for viewing.
        if (Denied(SaveAction.EditDex)) return;
        var session = _sessionsFor();
        if (session is null) return;
        var legalizer = IPlatformApplication.Current!.Services.GetRequiredService<ILegalizerService>();
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        await DexEditorPage.ShowAsync(_hostGrid, _viewModel, session, legalizer, data, _sprites);
        _viewModel.Status = "Editor da Pokédex fechado.";
    }

    /// <summary>Every button on this screen, in one place.</summary>
    public bool OnPadButton(PadButton button)
    {
        if (_boxManageMode)
        {
            switch (button)
            {
                case PadButton.L:
                    if (_boxHeld) _ = ShiftHeldBoxAsync(-1); else BrowseManagedBoxes(-1);
                    return true;
                case PadButton.R:
                    if (_boxHeld) _ = ShiftHeldBoxAsync(1); else BrowseManagedBoxes(1);
                    return true;
                case PadButton.Y: ToggleMarkedBox(); return true;
                case PadButton.X: _ = ShowBoxBulkActionsAsync(); return true;
                case PadButton.A:
                    _boxHeld = !_boxHeld;
                    _heldBox = _viewModel.BoxIndex;
                    _viewModel.Status = _boxHeld
                        ? $"Segurando a box {_heldBox + 1:00} - L/R trocar · A soltar"
                        : "Gerenciador de boxes - A segurar · L/R navegar · Y marcar box · X ações";
                    SetBoxManageFooter();
                    UpdateBoxManagePulse();
                    _boxBar.InvalidateSurface();
                    return true;
                case PadButton.B: ExitBoxManageMode(); return true;
                default: return true;
            }
        }

        if (_editorFocusMode)
        {
            switch (button)
            {
                case PadButton.Up: MoveEditorFocus(-1); return true;
                case PadButton.Down: MoveEditorFocus(1); return true;
                case PadButton.Left: MoveEditorFocusHorizontal(-1); return true;
                case PadButton.Right: MoveEditorFocusHorizontal(1); return true;
                case PadButton.A:
                case PadButton.X:
                case PadButton.Y:
                    ActivateEditorFocus();
                    return true;
                case PadButton.B:
                case PadButton.Start:
                    ExitEditorFocusMode();
                    return true;
                default:
                    return true;
            }
        }

        if (_viewModel.SelectMode) return OnSelectModeButton(button);

        switch (button)
        {
            case PadButton.Up: return _viewModel.MoveCursor(FocusDirection.Up);
            case PadButton.Down: return _viewModel.MoveCursor(FocusDirection.Down);
            case PadButton.Left: return _viewModel.MoveCursor(FocusDirection.Left);
            case PadButton.Right: return _viewModel.MoveCursor(FocusDirection.Right);
            case PadButton.L: _viewModel.PreviousBox(); return true;
            case PadButton.R: _viewModel.NextBox(); return true;
            case PadButton.A: return ConfirmCursor();
            case PadButton.B:
                if (_viewModel.CarrySource is not null) { _viewModel.CancelCarry(); _canvas.InvalidateSurface(); return true; }
                _ = Navigation.PopAsync();
                return true;
            case PadButton.X: _ = ShowToolsAsync(); return true;
            case PadButton.Y: _ = ShowSaveDataAsync(); return true;
            case PadButton.Start: return OpenCursorMenu();
            case PadButton.Select: EnterSelectMode(); return true;
            default: return false;
        }
    }

    /// <summary>The green hand. A marks (hold A and steer to sweep an area), Y takes the whole
    /// box, X opens the actions, Start the box manager; L/R keep every mark.</summary>
    private bool OnSelectModeButton(PadButton button)
    {
        // Anything but steering ends an area gesture first, so no menu opens over a live span.
        if (_selectHeld && button is not (PadButton.A or PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right))
        {
            _selectHeld = false;
            _viewModel.CommitRectangle();
        }
        switch (button)
        {
            case PadButton.Up: return MoveSelectCursor(FocusDirection.Up);
            case PadButton.Down: return MoveSelectCursor(FocusDirection.Down);
            case PadButton.Left: return MoveSelectCursor(FocusDirection.Left);
            case PadButton.Right: return MoveSelectCursor(FocusDirection.Right);
            case PadButton.L: _selectHeld = false; _viewModel.PreviousBox(); return true;
            case PadButton.R: _selectHeld = false; _viewModel.NextBox(); return true;
            case PadButton.A:
                if (_viewModel.Save is null) return false;
                if (_selectHeld) return true; // key repeat while held
                _selectHeld = true;
                var slot = Math.Max(0, _viewModel.SelectedSlot);
                _viewModel.SelectSlot(slot);
                _viewModel.BeginRectangle(slot);
                _canvas.InvalidateSurface();
                return true;
            case PadButton.Y:
                _viewModel.ToggleBoxMarks();
                _canvas.InvalidateSurface();
                return true;
            case PadButton.X: _ = ShowOrganizerMenuAsync(); return true;
            case PadButton.Start: EnterBoxManageMode(); return true;
            case PadButton.B:
                if (_viewModel.MarkedCount > 0) { _viewModel.ClearMarks(); _canvas.InvalidateSurface(); }
                else ExitSelectMode();
                return true;
            case PadButton.Select: ExitSelectMode(); return true;
            default: return false;
        }
    }

    private bool MoveSelectCursor(FocusDirection direction)
    {
        var moved = _viewModel.MoveCursor(direction);
        if (_selectHeld) _viewModel.ExtendRectangle(_viewModel.SelectedSlot);
        _canvas.InvalidateSurface();
        return moved;
    }

    public void OnPadReleased(PadButton button)
    {
        if (button != PadButton.A || !_selectHeld) return;
        _selectHeld = false;
        _viewModel.CommitRectangle();
        _canvas.InvalidateSurface();
    }

    /// <summary>
    /// A is the hand, like the games' PC: grab the mon under the cursor, carry it,
    /// place (or swap) on the next press. Empty slot with empty hand → add sheet.
    /// </summary>
    private bool ConfirmCursor()
    {
        if (_viewModel.Save is null) return false;
        var slot = Math.Max(0, _viewModel.SelectedSlot);
        _viewModel.SelectSlot(slot);

        if (_viewModel.CarrySource is { } source)
        {
            // Party swap between two Pokémon: the cards slide past each other as they trade places.
            var target = _viewModel.SelectedSlot;
            var partySwap = _viewModel.BoxIndex == -1 && source.Box == -1 && target != source.Slot
                && PartyHasMon(source.Slot) && PartyHasMon(target);
            _ = DropAndRepaintAsync(partySwap ? (source.Slot, target) : null);
            return true;
        }

        // A is the hand everywhere, party included: grab, carry, place or swap.
        // The mon's actions stay on Start.

        if (_viewModel.BeginCarry())
        {
            _canvas.InvalidateSurface();
            return true;
        }
        _ = OfferAddPokemonAsync(slot);
        return true;
    }

    private async Task DropAndRepaintAsync((int From, int To)? partySwap = null)
    {
        // The move and the grid refresh happen before the drop's first await (the write), so
        // the swap animation starts with them rather than after the save is written.
        var drop = _viewModel.DropAsync();
        if (partySwap is { } swap) PartyView.BeginSwap(swap.From, swap.To);
        _canvas.InvalidateSurface();
        await drop;
        _canvas.InvalidateSurface();
    }

    private bool PartyHasMon(int slot)
    {
        try { return _sessionsFor()?.ReadEntity(-1, slot) is { IsEmpty: false }; }
        catch { return false; }
    }

    /// <summary>Start opens the menu for whatever the cursor is on: mon actions or the add sheet.</summary>
    private bool OpenCursorMenu()
    {
        if (_viewModel.Save is null) return false;
        var slot = Math.Max(0, _viewModel.SelectedSlot);
        var slots = _viewModel.VisibleSlots;
        _viewModel.SelectSlot(slot);
        if (slot < slots.Count && slots[slot].Species is not null)
            _ = ShowMonActionsAsync(slot);
        else
            _ = OfferAddPokemonAsync(slot);
        return true;
    }

    /// <summary>The slot's verdict: the sweep's cached answer when fresh, else a one-slot
    /// analysis off-thread (the same report the side panel shows).</summary>
    private async Task<SlotLegality?> CurrentLegalityAsync(int slot)
    {
        var cached = _viewModel.LegalitySweep?.FirstOrDefault(v => v.Box == _viewModel.BoxIndex && v.Slot == slot);
        if (cached is not null) return cached;
        var session = _sessionsFor();
        var legality = IPlatformApplication.Current?.Services.GetService<ILegalityService>();
        if (session is null || legality is null || !session.SupportsLegalityAnalysis) return null;
        return await Task.Run(() =>
        {
            var report = legality.Analyze(session, _viewModel.BoxIndex, slot);
            return new SlotLegality(_viewModel.BoxIndex, slot, report.Valid,
                report.Valid ? string.Empty : report.Lines.FirstOrDefault() ?? "Ilegal.", report.Lines);
        });
    }


    /// <summary>
    /// The full-screen summary of the slot's mon: L/R walk this box's (or the party's)
    /// occupied slots and carry the cursor along; X closes it into the side-panel editor,
    /// exactly where "Edit" goes. Read-only, so Hardcore mode keeps it.
    /// </summary>
    private async Task OpenSummaryAsync(int slot)
    {
        var session = _sessionsFor();
        var services = IPlatformApplication.Current?.Services;
        var summaries = services?.GetService<IMonSummaryService>();
        if (session is null || summaries is null) return;
        var box = _viewModel.BoxIndex;
        var slots = _viewModel.VisibleSlots;
        var deck = new SummaryDeck(
            slots.Count,
            i => i < slots.Count && slots[i].Species is not null,
            slot,
            (i, legality) => SummaryLoaders.FromSlot(session, summaries, box, i, legality),
            box == -1 ? "Equipe" : $"Box {box + 1:00}",
            CanEdit: true,
            Icon: i => i < slots.Count && slots[i].Species is int species ? new SlotIcon(species, slots[i].Form, slots[i].IsShiny, slots[i].HasItem, slots[i].Traits) : null,
            Moved: i =>
            {
                if (_viewModel.BoxIndex != box) return;
                _viewModel.SelectSlot(i);
                _canvas.InvalidateSurface();
            });
        var page = services?.GetService<SecondScreenState>()?.InspectorPage ?? SummaryPage.Info;
        var result = await MonSummaryScreen.ShowAsync(_hostGrid, _sprites, deck, page);
        if (!result.EditRequested || _viewModel.BoxIndex != box) return;
        _viewModel.SelectSlot(result.Slot);
        EnterEditorFocusMode();
    }

    /// <summary>What you can do with the mon under the cursor. Editing is live in the side panel already.</summary>
    private async Task ShowMonActionsAsync(int slot)
    {
        var nickname = _viewModel.Selected?.Nickname is { Length: > 0 } nick ? nick : $"slot {slot + 1}";
        // An illegal mon leads with its repairs; the sweep's cached verdict answers
        // instantly, otherwise this one slot is analyzed before the menu opens.
        var verdict = await CurrentLegalityAsync(slot);
        var options = new List<PadOption>();
        if (verdict is { Valid: false })
        {
            options.Add(new("Explicar legalidade", IconPath: "info", Detail: "Por que este Pokémon foi sinalizado, com correções quando houver."));
            options.AddRange(Menu(
                Allowed(SaveAction.EditMon, new("Legalizar este", IconPath: "fix", Detail: "Reescreve-o para a versão legal mais próxima.")),
                Allowed(SaveAction.EditMon, new("Legalizar todos os ilegais", IconPath: "fix", Detail: "Reescreve cada Pokémon ilegal do save para a versão legal mais próxima."))));
        }
        options.AddRange(Menu(
            new PadOption("Resumo", IconPath: "info"),
            new PadOption("Editar", IconPath: "editor"),
            Allowed(SaveAction.EditMon, new("Evoluir…", IconPath: "evolve")),
            new PadOption("Enviar para o Poképark", IconPath: "park", Detail: "Adiciona-o aos visitantes do Poképark. O save não é alterado."),
            new PadOption("Mover", IconPath: "move", Detail: "Pegue-o e leve-o para outro slot."),
            Allowed(SaveAction.Duplicate, new("Duplicar", IconPath: "copy", Detail: "Clona-o no próximo slot vazio daqui.")),
            new PadOption("Enviar para o Banco", IconPath: "bank", Detail: "Ele sai deste save e fica guardado no Banco."),
            Allowed(SaveAction.Duplicate, new("Copiar para o Banco", IconPath: "copy", Detail: "Uma cópia vai para o Banco; o original fica aqui.")),
            new PadOption("Enviar para outro jogo…", IconPath: "send", Detail: "Ele sai deste save e vai para outro jogo vinculado."),
            Allowed(SaveAction.Duplicate, new("Copiar para outro jogo…", IconPath: "copy", Detail: "Uma cópia vai para outro jogo vinculado; o original fica aqui.")),
            new PadOption("Exportar arquivo .pk", IconPath: "export", Detail: "Compartilha este Pokémon como arquivo."),
            new PadOption("Mostrar como set do Showdown", IconPath: "script", Detail: "O set dele como texto do Showdown, para copiar."),
            new PadOption("Mostrar como QR code", IconPath: "qr", Detail: "O set do Showdown dele como QR code."),
            new PadOption("Mostrar como .pk QR", IconPath: "qr", Detail: "O Pokémon inteiro como um QR code que o PKForge pode escanear para o Banco."),
            new PadOption("RNG / IVs", IconPath: "dice", Detail: "Veja o PID e os IVs dele, e mude a natureza sem perder o shiny."),
            new PadOption("Travar / Destravar soltura", IconPath: "padlock", Detail: "Um Pokémon travado não pode ser solto."),
            new PadOption("Soltar", IconPath: "release"),
            new PadOption("Soltar nas Wilds", IconPath: "release", Detail: "Ele sai do save e passa a viver nas terras selvagens ao redor do Poképark. Pode voltar para visitar.")));
        var choice = await PadMenu.ShowAsync(_hostGrid, nickname,
            Note(verdict is { Valid: false } ? verdict.Problem : null), options.ToArray());
        switch (choice)
        {
            case "Explicar legalidade":
            {
                if (await TryShowLegalityFixesAsync()) return;
                var detail = string.IsNullOrWhiteSpace(_viewModel.LegalityText)
                    ? verdict?.Problem ?? "Nenhum detalhe de legalidade foi informado."
                    : _viewModel.LegalityText;
                await ShowLegalityReportAsync(detail);
                return;
            }
            case "Legalizar este":
            {
                var overlay = LoadingOverlay.Show(_hostGrid, "Legalizando…", "Procurando a versão real e legal mais próxima deste Pokémon.");
                try
                {
                    await _viewModel.RunLegalizerAsync((service, s) => service.LegalizeSlot(s, _viewModel.BoxIndex, slot), slot, SaveAction.EditMon);
                    _canvas.InvalidateSurface();
                }
                finally { overlay.Close(); }
                return;
            }
            case "Enviar para o Poképark":
                _viewModel.Status = IPlatformApplication.Current!.Services.GetRequiredService<PokeparkService>().AddSaveVisitor(_viewModel.BoxIndex, slot);
                return;
            case "Resumo":
                await OpenSummaryAsync(slot);
                return;
            case "Editar":
                EnterEditorFocusMode();
                return;
            case "Mover":
                if (_viewModel.BeginCarry()) _canvas.InvalidateSurface();
                return;
            case "Duplicar":
                await DuplicateSlotAsync(slot);
                return;
            case "Evoluir…":
                await EvolveSlotAsync(slot);
                return;
            case "Enviar para o Banco":
                await SendToBankAsync(slot, nickname);
                return;
            case "Copiar para o Banco":
                await SendToBankAsync(slot, nickname, copyInsteadOfMove: true);
                return;
            case "Enviar para outro jogo…":
                await SendSlotToAnotherGameAsync(slot, nickname);
                return;
            case "Copiar para outro jogo…":
                await SendSlotToAnotherGameAsync(slot, nickname, copyInsteadOfMove: true);
                return;
            case "Legalizar todos os ilegais":
            {
                var overlay = default(LoadingOverlay);
                IReadOnlyList<SlotLegality> sweep;
                try
                {
                    sweep = await _viewModel.GetLegalitySweepAsync(
                        () => overlay = LoadingOverlay.Show(_hostGrid, "Verificando legalidade…", "Varrendo a equipe e todas as boxes."),
                        (done, total) => overlay?.Report(done, total));
                }
                finally
                {
                    overlay?.Close();
                }
                var illegal = sweep.Where(r => !r.Valid).ToList();
                if (illegal.Count == 0) { _viewModel.Status = "Nada mais está sinalizado."; return; }
                await LegalizeAllIllegalAsync(illegal);
                return;
            }
            case "Mostrar como .pk QR":
            {
                var session = _sessionsFor();
                if (session is null) return;
                var export = session.ExportSlot(_viewModel.BoxIndex, slot);
                // The PKF1 envelope names no format, and a ROM hack's record cannot be read without one.
                if (PKForge.Engine.EntityBytes.RomHackGame(export.Format) is { } hack)
                {
                    await PadMenu.ShowAsync(_hostGrid, ".pk QR",
                        $"Pokémon de {hack} não podem viajar por .pk QR. Use Enviar para o Banco ou Exportar arquivo .pk.", "OK");
                    return;
                }
                var detail = session.ReadEntity(_viewModel.BoxIndex, slot);
                await QrPopup.ShowBinaryAsync(_hostGrid, $"{detail.SpeciesName} · .PK QR",
                    Services.QrEntityService.MakePayload(export.Data, session.Generation, detail.SpeciesName));
                return;
            }
            case "Mostrar como set do Showdown":
                await ShowShowdownAsync(slot);
                return;
            case "Exportar arquivo .pk":
                await ExportSlotAsync(slot);
                return;
            case "Mostrar como QR code":
                await ShowQrAsync(slot);
                return;
            case "RNG / IVs":
                await ShowRngAsync(slot);
                return;
            case "Travar / Destravar soltura":
            {
                var docId = DocumentId;
                var session = _sessionsFor();
                if (docId is null || session is null) return;
                var pid = session.GetRngInfo(_viewModel.BoxIndex, slot).Pid;
                var locked = Protection.ToggleMon(docId, _viewModel.BoxIndex, slot, pid);
                RefreshLockedSlots();
                _canvas.InvalidateSurface();
                _viewModel.Status = locked ? "Pokémon travado - soltura bloqueada" : "Pokémon destravado";
                return;
            }
            case "Soltar":
                await ReleaseSlotAsync(slot, nickname);
                return;
            case "Soltar nas Wilds":
                await ReleaseSlotAsync(slot, nickname, toWilds: true);
                return;
            default:
                return; // Edit: the editor is already open on the right
        }
    }

    /// <summary>Manual evolution (trade evolutions without a second console): the evolution
    /// card plans it, the edit commits through RunMutationAsync (backup, Hardcore guard).</summary>
    private async Task EvolveSlotAsync(int slot)
    {
        // Evolving rewrites the mon, so Hardcore mode refuses it like any other edit.
        if (await DeniedAsync(SaveAction.EditMon)) return;
        var session = _sessionsFor();
        var service = IPlatformApplication.Current?.Services.GetService<IEvolutionService>();
        if (session is null || service is null || _viewModel.IsBusy) return;
        var box = _viewModel.BoxIndex;
        var request = await EvolutionCard.RunAsync(_hostGrid, session, box, slot);
        if (request is null) return;
        var ok = await _viewModel.RunMutationAsync(s => service.Evolve(s, box, slot, request), slot,
            changeDescription: "Evoluir", action: SaveAction.EditMon);
        _canvas.InvalidateSurface();
        await PadMenu.ShowAsync(_hostGrid, ok ? "Parabéns!" : "A evolução falhou", _viewModel.Status, "OK");
    }

    /// <summary>Clone the mon in place: party clones append (cap 6), box clones fill the
    /// box's first empty slot. PKSM-style Duplicate, one backup per write.</summary>
    private async Task DuplicateSlotAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null || _viewModel.IsBusy) return;
        var box = _viewModel.BoxIndex;
        var source = session.ReadEntity(box, slot);
        if (source.IsEmpty) return;
        var name = source.Nickname is { Length: > 0 } nick ? nick : "Pokémon";
        var destination = box == -1
            ? Enumerable.Range(0, 6).Count(i => !session.ReadEntity(-1, i).IsEmpty)
            : _viewModel.VisibleSlots.Select(x => x.Slot).FirstOrDefault(i => session.ReadEntity(box, i).IsEmpty, -1);
        if (destination < 0 || (box == -1 && destination >= 6))
        {
            _viewModel.Status = box == -1 ? "A equipe está cheia - sem espaço para duplicar." : "Esta box está cheia - sem espaço para duplicar.";
            await PadMenu.ShowAsync(_hostGrid, "Duplicar", _viewModel.Status, "OK");
            return;
        }
        var ok = await _viewModel.RunMutationAsync(s =>
            s.DuplicateSlot(box, slot, box, destination)
                ? new GenerationOutcome(true, $"{name} duplicated.")
                : new GenerationOutcome(false, "Não foi possível duplicar nesse slot."), destination, action: SaveAction.Duplicate);
        if (!ok)
        {
            await PadMenu.ShowAsync(_hostGrid, "Falha ao duplicar", _viewModel.Status, "OK");
            return;
        }
        _viewModel.RefreshAllSlots(includeEmptyPartySlots: box == -1);
        _viewModel.SelectSlot(destination);
        _canvas.InvalidateSurface();
    }

    /// <summary>
    /// Batch identity toolkit: reset nicknames to species names, or adopt the
    /// connected trainer's OT across a scope. One write, one restore point; the
    /// confirmation says plainly that legality is not re-checked afterwards.
    /// </summary>
    private async Task ShowBatchRenameAsync()
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var session = _sessionsFor();
        if (session is null) return;

        var scope = _viewModel.MarkedCount > 0
            ? await PadMenu.ShowAsync(_hostGrid, "Aplicar em…", null,
                $"Pokémon marcados ({_viewModel.MarkedCount})", "Esta box", "Todas as boxes", "Cancelar")
            : await PadMenu.ShowAsync(_hostGrid, "Aplicar em…", null, "Esta box", "Todas as boxes", "Cancelar");
        if (scope is null or "Cancelar") return;

        var op = await PadMenu.ShowAsync(_hostGrid, "Qual operação?", null,
            "Restaurar apelidos para os nomes das espécies",
            "Tornar-me o TO (usar meu ID de treinador)",
            "Cancelar");
        if (op is null or "Cancelar") return;

        var marked = _viewModel.MarkedSlots.ToArray();
        var slots = scope.StartsWith("Pokémon marcados", StringComparison.Ordinal) ? marked
            : scope == "Esta box"
                ? _viewModel.Save?.Slots.Where(x => x.Box == _viewModel.BoxIndex)
                    .Select(x => x.Slot).Distinct().OrderBy(x => x)
                    .Select(s => (_viewModel.BoxIndex, Slot: s)).ToArray()
                    ?? Enumerable.Range(0, 30).Select(s => (_viewModel.BoxIndex, Slot: s)).ToArray()
                : null; // all boxes: the engine walks every slot itself
        var affected = slots?.Length ?? -1;

        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Aplicar em todos os Pokémon correspondentes?",
            $"{op} · {(affected < 0 ? "equipe + todas as boxes" : $"{affected} slot(s)")}\n" +
            "Mudanças de apelido e de TO NÃO têm a legalidade verificada de novo depois.", "Aplicar");
        if (!confirmed) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "Aplicando…", "Uma gravação, um ponto de restauração.");
        try
        {
            if (op.StartsWith("Restaurar apelidos", StringComparison.Ordinal))
            {
                await _viewModel.RunMutationAsync(s =>
                {
                    var touched = slots is null ? s.BatchApply(["IsNicknamed=false"]) : s.BatchApplySlots(slots, ["IsNicknamed=false"]);
                    return new GenerationOutcome(touched > 0, $"Apelidos restaurados em {touched} Pokémon (legalidade não verificada de novo).");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
            }
            else
            {
                var profileStore = IPlatformApplication.Current!.Services.GetRequiredService<TrainerProfileStore>();
                var profile = profileStore.Profiles.FirstOrDefault(); // null = MakeMine derives from the save itself
                await _viewModel.RunMutationAsync(s =>
                {
                    var targets = slots is null
                        ? s.Snapshot.Slots.Where(x => x.Box >= 0 && x.Species is > 0).Select(x => (x.Box, x.Slot)).ToArray()
                        : slots!;
                    var touched = 0;
                    foreach (var (box, monSlot) in targets)
                        if (s.ReadEntity(box, monSlot).Species > 0 && s.MakeMine(box, monSlot, profile).Success) touched++;
                    return new GenerationOutcome(touched > 0, $"TO adotado em {touched} Pokémon (legalidade não verificada de novo).");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.BatchEdit);
            }
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally { overlay.Close(); }
    }

    /// <summary>
    /// Rolls a random legal team from the game's own species pool and offers to
    /// place it in the party (overwriting) or the first empty box slots.
    /// </summary>
    private async Task ShowRandomTeamAsync()
    {
        var session = _sessionsFor();
        var services = IPlatformApplication.Current?.Services;
        var data = services?.GetService<IGameDataService>();
        var legalizer = services?.GetService<ILegalizerService>();
        if (session is null || data is null || legalizer is null) return;

        var countChoice = await PadMenu.ShowAsync(_hostGrid, "Tamanho do time", null, "1", "2", "3", "4", "5", "6");
        if (countChoice is null || !int.TryParse(countChoice, out var count)) return;
        var levelChoice = await PadMenu.ShowAsync(_hostGrid, "Faixa de nível", null,
            "Lv 50", "Lv 100", "Livre (5-70)");
        if (levelChoice is null) return;
        var (lo, hi) = levelChoice switch
        {
            "Lv 50" => (50, 50),
            "Lv 100" => (100, 100),
            _ => (5, 70),
        };
        var filters = await PadMenu.ShowAsync(_hostGrid, "Filtros", null,
            "Sem restrições", "Sem lendários", "Sem repetidos", "Sem lendários, sem repetidos");
        if (filters is null) return;
        var noLegendaries = filters.Contains("lendário", StringComparison.OrdinalIgnoreCase);
        var noDuplicates = filters.Contains("repetido", StringComparison.OrdinalIgnoreCase);

        // The game's own pool: species the engine can produce for this save.
        var max = session.MaxSpeciesId;
        var pool = new List<int>(max);
        for (var species = 1; species <= max; species++)
            if (species < data.SpeciesNames.Count && data.SpeciesNames[species].Length > 0)
                pool.Add(species);
        var options = new RandomTeamOptions(count, lo, hi, noLegendaries, noDuplicates, AllowNfe: true);
        var team = RandomTeamPlanner.Plan(pool, new HashSet<int>(), options, Random.Shared);

        var roster = string.Join(", ", team.Select(t => $"{data.SpeciesNames[t.Species]} Lv{t.Level}"));
        var placement = await PadMenu.ShowAsync(_hostGrid, "Sortear este time?",
            roster + "\nCada Pokémon é gerado legal para este jogo.", "Na equipe", "Nos primeiros slots vazios das boxes", "Cancelar");
        if (placement is null or "Cancelar") return;

        var overlay = LoadingOverlay.Show(_hostGrid, "Gerando time…", "O legalizador offline está trabalhando.");
        try
        {
            var generated = new List<(byte[] Data, int Level, string Name, string? Format)>();
            foreach (var (species, level) in team)
            {
                var mon = await Task.Run(() => legalizer.GenerateData(session,
                    new GenerationRequest(species, level, Shiny: false, null, null, null, null)));
                if (mon is not null) generated.Add((mon.Data, level, data.SpeciesNames[species], mon.Info.Format));
            }
            if (generated.Count == 0) { _viewModel.Status = "O legalizador não conseguiu montar este time."; return; }

            if (placement == "Na equipe")
            {
                var ok = await _viewModel.RunMutationAsync(s =>
                {
                    for (var i = 0; i < generated.Count; i++)
                        s.ImportSlot(-1, i, generated[i].Data, generated[i].Format);
                    return new GenerationOutcome(true, $"Time aleatório de {generated.Count} agora na equipe.");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.CreateMon);
                if (!ok) return;
            }
            else
            {
                var empties = _viewModel.Save?.Slots.Where(x => x.Box >= 0 && x.Species is null)
                    .Select(x => (x.Box, x.Slot)).Take(generated.Count).ToArray();
                if (empties is null || empties.Length < generated.Count)
                {
                    _viewModel.Status = $"Não há slots vazios suficientes nas boxes ({generated.Count} necessários).";
                    return;
                }
                var ok = await _viewModel.RunMutationAsync(s =>
                {
                    for (var i = 0; i < generated.Count; i++)
                        s.ImportSlot(empties[i].Box, empties[i].Slot, generated[i].Data, generated[i].Format);
                    return new GenerationOutcome(true, $"Time aleatório de {generated.Count} colocado nas boxes.");
                }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.CreateMon);
                if (!ok) return;
            }
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
        }
        finally { overlay.Close(); }
    }

    /// <summary>
    /// Game-to-game: pick any other detected save, the transfer service converts and
    /// writes there, then the mon leaves this box (a real move, not a copy).
    /// </summary>
    private async Task SendSlotToAnotherGameAsync(int slot, string nickname, bool copyInsteadOfMove = false)
    {
        // Copy-to-game duplicates; the move is the only transfer Hardcore mode allows.
        if (copyInsteadOfMove && Denied(SaveAction.Duplicate)) return;
        var services = IPlatformApplication.Current?.Services;
        var picker = services?.GetService<SavePickerViewModel>();
        var transfer = services?.GetService<Services.TransferService>();
        var session = _sessionsFor();
        if (picker is null || transfer is null || session is null) return;
        // A move must be able to release the original before the destination is written.
        if (!copyInsteadOfMove && _viewModel.OpenSaveRefusesWrites()) return;

        var currentDoc = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;
        var target = await SavePickerSheet.PickAsync(_hostGrid, picker.Saves,
            "Enviar para jogo", $"{nickname} → escolha o destino (o Pokémon sai desta box)", currentDoc);
        if (target is null)
        {
            if (picker.Saves.Count == 0)
                _viewModel.Status = "Nenhum outro jogo vinculado. Vincule outro emulador ou save no Início.";
            return;
        }

        var verb = copyInsteadOfMove ? "Copiar" : "Mover";
        var confirm = await PadMenu.ConfirmAsync(_hostGrid, $"{verb} para outro jogo?",
            copyInsteadOfMove
                ? $"Uma cópia de {nickname} vai para {target.GameLabel}; o original fica aqui."
                : $"{nickname} vai sair desta box e ir para {target.GameLabel} (se houver espaço nas boxes).", verb);
        if (!confirm) return;

        var export = session.ExportSlot(_viewModel.BoxIndex, slot);
        // Cross-generation conversions change things; the diff preview is the trust
        // step before the write. Same-generation sends show an empty diff.
        var preview = await transfer.PreviewAsync(export.Data, nickname, target);
        if (!await Services.TransferPreviewPrompt.ConfirmAsync(_hostGrid, preview, nickname, target.GameLabel)) return;
        var outcome = await transfer.SendToGameAsync(export.Data, nickname, target);
        _viewModel.Status = outcome.Message;
        if (!outcome.Success || copyInsteadOfMove) return;

        await _viewModel.RunMutationAsync(s =>
        {
            s.ReleaseSlot(_viewModel.BoxIndex, slot);
            return new GenerationOutcome(true, $"{nickname} movido para {target.GameLabel}.");
        }, slot, action: SaveAction.Move);
    }

    /// <summary>Writes the decrypted .pk* file and hands it to Android's share sheet.</summary>
    private async Task ExportSlotAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        try
        {
            var export = session.ExportSlot(_viewModel.BoxIndex, slot);
            var path = System.IO.Path.Combine(FileSystem.CacheDirectory, export.FileName);
            await File.WriteAllBytesAsync(path, export.Data);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = export.FileName,
                File = new ShareFile(path),
            });
            _viewModel.Status = $"{export.FileName} exportado";
        }
        catch (Exception error)
        {
            _viewModel.Status = $"A exportação falhou: {error.Message}";
        }
    }

    private async Task ShowShowdownAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        var text = session.GetShowdownText(_viewModel.BoxIndex, slot);
        var choice = await PadMenu.ShowAsync(_hostGrid, "Set do Showdown", text, "Copiar para a área de transferência", "Fechar");
        if (choice == "Copiar para a área de transferência")
        {
            await Clipboard.Default.SetTextAsync(text);
            _viewModel.Status = "Set copiado para a área de transferência.";
        }
    }

    private async Task ShowQrAsync(int slot)
    {
        var session = _sessionsFor();
        if (session is null) return;
        var text = session.GetShowdownText(_viewModel.BoxIndex, slot);
        await QrPopup.ShowAsync(_hostGrid, "Set do Showdown · QR", text);
    }

    /// <summary>
    /// Deposit: a copy duplicates into the vault and leaves the game untouched; a move
    /// additionally empties the game slot (one safe write, one restore point).
    /// </summary>
    private async Task SendToBankAsync(int slot, string nickname, bool copyInsteadOfMove = false)
    {
        // "Copy to Bank" duplicates (the original stays); "Send to Bank" is a move and
        // Hardcore mode keeps it, so the two paths are told apart right here.
        if (copyInsteadOfMove && Denied(SaveAction.Duplicate)) return;
        var session = _sessionsFor();
        var bank = IPlatformApplication.Current?.Services.GetService<IBankService>();
        var engine = IPlatformApplication.Current?.Services.GetService<ISaveEngine>();
        if (session is null || bank is null || engine is null) return;

        var export = session.ExportSlot(_viewModel.BoxIndex, slot);
        var info = engine.TryDescribeEntity(export.Data, _viewModel.ConnectedName, export.Format);
        if (info is null)
        {
            _viewModel.Status = "Não foi possível ler este Pokémon para o Banco.";
            return;
        }

        if (copyInsteadOfMove)
        {
            bank.Add(export.Data, info);
            _viewModel.Status = $"{nickname} copiado para o Banco. O original fica no jogo.";
            return;
        }

        // The Bank holds the copy before the slot empties; a failed save write takes it back out.
        var box = _viewModel.BoxIndex;
        var outcome = await PKForge.Engine.BankTransfers.DepositAsync(bank, [new BankDeposit(export.Data, info)],
            async () => await _viewModel.RunMutationAsync(s =>
            {
                s.ReleaseSlot(box, slot);
                return new GenerationOutcome(true, $"{nickname} depositado no Banco.");
            }, slot, changeDescription: $"Depositar {nickname} no Banco ({BoxBrowserViewModel.SlotLabel(box, slot)})",
                action: SaveAction.Move) ? null : _viewModel.Status);
        if (outcome.Success) _canvas.InvalidateSurface();
        else _viewModel.Status = outcome.Message;
    }

    /// <summary>Release with confirmation; the pre-release state stays recoverable as a restore point.</summary>
    private async Task ReleaseSlotAsync(int slot, string nickname, bool toWilds = false)
    {
        var docId = DocumentId;
        var session = _sessionsFor();
        if (docId is not null && session is not null)
        {
            var rng = session.GetRngInfo(_viewModel.BoxIndex, slot);
            if (!Protection.CanRelease(docId, _viewModel.BoxIndex, slot, rng.Pid))
            {
                _viewModel.Status = $"{nickname} está travado - destrave-o primeiro";
                return;
            }
        }
        var box = _viewModel.BoxIndex;
        Task<bool> Release() => _viewModel.RunMutationAsync(session =>
        {
            session.ReleaseSlot(box, slot);
            return new GenerationOutcome(true, toWilds ? $"{nickname} foi viver nas Wilds." : $"{nickname} foi solto. Tchau, tchau!");
        }, slot, action: SaveAction.Release);
        if (toWilds && session is not null && WildFriendOf(session, box, slot) is { } friend)
        {
            await WildsFarewell.RunAsync(_hostGrid, friend.Friend, async () =>
            {
                if (!await Release()) return false;
                // It no longer lives in the park either: it went further.
                if (friend.ResidentId is { } id) IPlatformApplication.Current?.Services.GetService<PokeparkService>()?.RemoveVisitor(id);
                return true;
            });
            _canvas.InvalidateSurface();
            return;
        }
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Soltar?",
            $"Soltar {nickname} de volta na natureza? O estado atual do save é mantido como ponto de restauração.",
            "Soltar");
        if (!confirmed) return;
        await Release();
        _canvas.InvalidateSurface();
    }

    // The Pokémon in this slot as the Wilds will remember it, and its park id if it lived there.
    private (ParkWildFriend Friend, string? ResidentId)? WildFriendOf(ISaveEngineSession session, int box, int slot)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services?.GetService<PokeparkWildsStore>() is not { } wilds || session.ReadEntity(box, slot) is not { IsEmpty: false } mon) return null;
        var document = services.GetService<ISaveSessionService>()?.Current?.Document;
        var documentKey = document is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document.DocumentId)))[..16];
        var resident = services.GetService<PokeparkService>()?.LoadRoster()
            .FirstOrDefault(p => p.Id.StartsWith($"save:{documentKey}:{box}:{slot}", StringComparison.Ordinal) && p.Species == mon.Species);
        var name = string.IsNullOrWhiteSpace(mon.Nickname) ? mon.SpeciesName : mon.Nickname;
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(session.ExportSlot(box, slot).Data));
        return (wilds.Describe(mon.Species, mon.Form, mon.IsShiny, name, mon.OriginalTrainer, document?.DisplayName ?? "", resident?.Id, fingerprint), resident?.Id);
    }

    /// <summary>The Thor's second screen mirrors the box automatically while this page is open.</summary>
    protected override void OnAppearing()
    {
        base.OnAppearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        // The mode gates this whole screen, so the strip says so the moment it opens.
        if (HardcoreMode.IsOn) _viewModel.Status = HardcoreMode.StatusLine;
        _ = CheckLayoutRiskAsync();
        // The lower screen's inspector follows this box's cursor while the page is in front.
        _secondClaim ??= IPlatformApplication.Current?.Services.GetService<SecondScreenState>()?.Routes.CreateClaim(SecondScreenOwner.Box);
        _secondClaim?.Activate();
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable != true) return;
        try { _ = host.ShowAsync(); }
        catch { /* single-screen devices and flaky displays must never break the box */ }
    }

    protected override void OnDisappearing()
    {
        if (_boxManageMode) ExitBoxManageMode();
        if (_selectHeld) { _selectHeld = false; _viewModel.CommitRectangle(); }
        base.OnDisappearing();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        _secondClaim?.Release();
    }

    private SecondScreenClaim? _secondClaim;

    private View BuildEditor()
    {
        EditorFocusTargets = [];
        _editorFocusIndex = 0;

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();

        View FocusBorder(View inner, string caption, Func<Task> activate, string? numericBindingPath = null)
        {
            var border = (Border)inner;
            EditorFocusTargets = [.. EditorFocusTargets, new EditorFocusTarget(border, caption, activate, numericBindingPath,
                OriginalBackground: border.BackgroundColor)];
            return border;
        }

        Button FocusButton(Button button, string caption)
        {
            EditorFocusTargets = [.. EditorFocusTargets, new EditorFocusTarget(button, caption, () => { button.SendClicked(); return Task.CompletedTask; },
                OriginalBackground: button.BackgroundColor, OriginalTextColor: button.TextColor, OriginalBorder: button.BorderColor)];
            return button;
        }

        // Abilities follow the pending species edit, so a changed species offers its own.
        List<PickItem> AbilityItems()
        {
            var (species, form) = PendingSpeciesForm();
            return InfoPickers.AbilityItems(data, _sessionsFor(), species, form);
        }
        Task OpenMove(string caption, string vmProperty) => OpenMovePickerAsync(data, caption, vmProperty);
        Task OpenItem() => OpenHeldItemPickerAsync(data);

        View? nicknameRow = null;
        View? levelRow = null;
        View? otRow = null;

        var species = FocusBorder(NamedPicker("ESPÉCIE", nameof(BoxBrowserViewModel.EditSpecies), data.SpeciesNames, null,
            openPokedex: true, shaded: true), "Espécie", async () =>
        {
            var session = _sessionsFor();
            if (session is null) return;
            var picked = await PokedexPicker.ShowAsync(_hostGrid, data, session);
            if (picked is not null) SetVmString(nameof(BoxBrowserViewModel.EditSpecies), picked.Id.ToString());
        });
        var nickname = FocusBorder(FieldRow("Apelido", nameof(BoxBrowserViewModel.EditNickname), shaded: false), "APELIDO", () =>
        {
            if (nicknameRow is not null) FocusEntry(nicknameRow);
            return Task.CompletedTask;
        });
        nicknameRow = nickname;
        var level = FocusBorder(FieldRow("Nível", nameof(BoxBrowserViewModel.EditLevel), shaded: false), "NÍVEL", () =>
        {
            if (levelRow is not null) FocusEntry(levelRow);
            return Task.CompletedTask;
        }, nameof(BoxBrowserViewModel.EditLevel));
        levelRow = level;
        // Friendship (0-255): what friendship evolutions wait for (Golbat, Eevee, Riolu...).
        // Gen 1 keeps none, so the row would edit nothing there.
        View? friendshipRow = null;
        var friendship = FocusBorder(FieldRow("Amizade", nameof(BoxBrowserViewModel.EditFriendship), shaded: false), "AMIZADE", () =>
        {
            if (friendshipRow is not null) FocusEntry(friendshipRow);
            return Task.CompletedTask;
        }, nameof(BoxBrowserViewModel.EditFriendship));
        friendshipRow = friendship;
        friendship.IsVisible = (_sessionsFor()?.Generation ?? 3) >= 2;
        var nature = FocusBorder(NamedPicker("NATUREZA", nameof(BoxBrowserViewModel.EditNature), NaturePicker.DisplayNames(data.NatureNames),
            null, shaded: true, open: () => OpenNaturePickerAsync(data)), "Natureza", () => OpenNaturePickerAsync(data));
        // Gen 1/2 have no natures: the row would edit nothing.
        nature.IsVisible = (_sessionsFor()?.Generation ?? 3) >= 3;
        var ability = FocusBorder(NamedPicker("HABILIDADE", nameof(BoxBrowserViewModel.EditAbility), data.AbilityNames,
            AbilityItems, shaded: false), "HABILIDADE", async () => await OpenNamedPickerAsync("HABILIDADE", nameof(BoxBrowserViewModel.EditAbility), AbilityItems));
        var item = FocusBorder(NamedPicker("ITEM SEGURADO", nameof(BoxBrowserViewModel.EditHeldItem), new LiveNames(() => SaveItemNames(data)),
            () => ItemsWithIcons(SaveItemNames(data)), shaded: true, open: OpenItem), "Item segurado", OpenItem);
        // Abilities start in Gen 3 and held items in Gen 2; Gen 1 stores neither.
        ability.IsVisible = (_sessionsFor()?.Generation ?? 3) >= 3;
        item.IsVisible = (_sessionsFor()?.Generation ?? 3) >= 2;
        Border Move(int index, bool dark)
        {
            var property = $"EditMove{index + 1}";
            var caption = $"GOLPE {index + 1}";
            Task Open() => OpenMove(caption, property);
            return (Border)FocusBorder(MoveRow(index, property, data, dark, Open), caption, Open);
        }
        var move1 = Move(0, dark: true);
        var move2 = Move(1, dark: false);
        var move3 = Move(2, dark: true);
        var move4 = Move(3, dark: false);
        async Task EditTraining(string stat)
        {
            var caps = _sessionsFor()?.GetTrainingCaps();
            var classic = caps?.IvMax == 15;
            var ivs = classic ? "Editar DVs" : "Editar IVs";
            var evs = classic ? "Editar stat experience" : "Editar EVs";
            var choice = await PadMenu.ShowAsync(_hostGrid, stat, null, ivs, evs);
            if (choice == ivs) await OpenStatsEditorAsync("IVS", nameof(BoxBrowserViewModel.EditIvs), () => _sessionsFor()?.GetTrainingCaps().IvMax ?? 31);
            else if (choice == evs) await OpenStatsEditorAsync("EVS", nameof(BoxBrowserViewModel.EditEvs), () => _sessionsFor()?.GetTrainingCaps().EvMax ?? 252);
        }
        var statLines = Enumerable.Range(0, 6)
            .Select(i => FocusBorder(StatLine(i, i % 2 == 0, EditTraining), StatNames[i], () => EditTraining(StatNames[i])))
            .ToArray();
        var ball = FocusBorder(NamedPicker("BOLA", nameof(BoxBrowserViewModel.EditBall), data.BallNames, BallItems, shaded: true, leading: BallIcon()), "BOLA", async () => await OpenNamedPickerAsync("BOLA", nameof(BoxBrowserViewModel.EditBall), BallItems));
        // Gen 1 and 2 Pokémon do not record the ball they were caught in.
        ball.IsVisible = (_sessionsFor()?.Generation ?? 3) >= 3;
        var ot = FocusBorder(FieldRow("TO", nameof(BoxBrowserViewModel.EditOt), shaded: false), "TO", () =>
        {
            if (otRow is not null) FocusEntry(otRow);
            return Task.CompletedTask;
        });
        otRow = ot;
        // Gender and shiny sit in one band, right under the nickname.
        var (genderShiny, genderHalf, shinyHalf) = GenderShinyRow();
        FocusBorder(genderHalf, "GÊNERO", OpenGenderPickerAsync);
        FocusBorder(shinyHalf, "Shiny", () =>
        {
            _viewModel.EditShiny = !_viewModel.EditShiny;
            return Task.CompletedTask;
        });
        var legalityLine = FocusBorder(LegalityLine(), "LEGALIDADE", OpenLegalityReportAsync);

        var legalize = FocusButton(EditorTool("Legalizar"), "LEGALIZAR");
        legalize.Clicked += async (_, _) =>
        {
            var slot = _viewModel.SelectedSlot;
            if (slot < 0) return;
            var overlay = LoadingOverlay.Show(_hostGrid, "Legalizando…", "Procurando a versão real e legal mais próxima deste Pokémon.");
            try
            {
                await _viewModel.RunLegalizerAsync((service, s) => service.LegalizeSlot(s, _viewModel.BoxIndex, slot), slot, SaveAction.EditMon);
                _canvas.InvalidateSurface();
            }
            finally { overlay.Close(); }
        };

        var makeMine = FocusButton(EditorTool("Tornar meu"), "TORNAR MEU");
        makeMine.Clicked += async (_, _) =>
        {
            var slot = _viewModel.SelectedSlot;
            if (slot < 0) return;
            await _viewModel.RunMutationAsync(s => s.MakeMine(_viewModel.BoxIndex, slot), slot);
            _canvas.InvalidateSurface();
        };

        var showdown = FocusButton(EditorTool("Showdown"), "SHOWDOWN");
        showdown.Clicked += async (_, _) => { if (_viewModel.SelectedSlot >= 0) await ShowShowdownAsync(_viewModel.SelectedSlot); };
        var exportPk = FocusButton(EditorTool("Exportar .PK"), "EXPORTAR .PK");
        exportPk.Clicked += async (_, _) => { if (_viewModel.SelectedSlot >= 0) await ExportSlotAsync(_viewModel.SelectedSlot); };
        var qr = FocusButton(EditorTool("QR"), "QR");
        qr.Clicked += async (_, _) => { if (_viewModel.SelectedSlot >= 0) await ShowQrAsync(_viewModel.SelectedSlot); };

        var save = FocusButton(EditorTool("Salvar mudanças", primary: true), "SALVAR MUDANÇAS");
        save.Margin = new Thickness(Design(28), Design(8), Design(28), Design(8));
        save.SetBinding(Button.CommandProperty, nameof(BoxBrowserViewModel.SaveEditCommand));

        var met = FocusButton(EditorTool("Encontro / origem"), "ENCONTRO / ORIGEM");
        met.Clicked += async (_, _) => await RunSubEditorAsync(MetOriginEditor.ShowAsync, "Encontro / origem atualizado");
        var moveDetails = FocusButton(EditorTool("Detalhes dos golpes"), "DETALHES DOS GOLPES");
        moveDetails.Clicked += async (_, _) => await RunSubEditorAsync(MoveDetailsEditor.ShowAsync, "Detalhes dos golpes atualizados");
        var moveShop = FocusButton(EditorTool("Loja de golpes"), "LOJA DE GOLPES");
        moveShop.Clicked += async (_, _) => await RunSubEditorAsync(MoveShopEditor.ShowAsync, "Loja de golpes atualizada");
        var potential = FocusButton(EditorTool("Potencial"), "POTENCIAL");
        potential.Clicked += async (_, _) => await RunSubEditorAsync(PotentialEditor.ShowAsync, "Potencial atualizado");
        var cosmetics = FocusButton(EditorTool("Visual"), "VISUAL");
        cosmetics.Clicked += async (_, _) => await RunSubEditorAsync(CosmeticsEditor.ShowAsync, "Visual atualizado");
        var awards = FocusButton(EditorTool("Prêmios"), "PRÊMIOS");
        awards.Clicked += async (_, _) => await RunSubEditorAsync(AwardsEditor.ShowAsync, "Prêmios atualizados");
        var ribbonAlbum = FocusButton(EditorTool("Álbum de ribbons"), "ÁLBUM DE RIBBONS");
        ribbonAlbum.Clicked += async (_, _) => await RunSubEditorAsync(
            (host, session, box, slot) => ShowRibbonAlbumAsync(host, session, box, slot), "Álbum de ribbons atualizado");
        var formShiny = FocusButton(EditorTool("Forma e shiny"), "FORMA E SHINY");
        formShiny.Clicked += async (_, _) => await RunSubEditorAsync(MonFieldsEditor.FormAndShinyAsync, "Forma e shiny atualizados");
        var trainers = FocusButton(EditorTool("Treinadores"), "TREINADORES");
        trainers.Clicked += async (_, _) => await RunSubEditorAsync(MonFieldsEditor.TrainersAsync, "Treinadores atualizados");
        var techRecords = FocusButton(EditorTool("Tech records"), "TECH RECORDS");
        techRecords.Clicked += async (_, _) => await RunSubEditorAsync(MonFieldsEditor.TechRecordsAsync, "Tech records atualizados");

        // The D-pad walks the stops in the order the panel shows them.
        string[] visualOrder =
        [
            "Espécie", "APELIDO", "GÊNERO", "Shiny", "NÍVEL",
            "Natureza", "HABILIDADE", "Item segurado", "AMIZADE", "BOLA", "TO",
            "GOLPE 1", "GOLPE 2", "GOLPE 3", "GOLPE 4",
            .. StatNames,
            "LEGALIDADE", "SALVAR MUDANÇAS",
        ];
        EditorFocusTargets = [.. EditorFocusTargets
            .Select((target, created) => (target, created))
            .OrderBy(t => Array.IndexOf(visualOrder, t.target.Caption) is var at and >= 0 ? at : visualOrder.Length + t.created)
            .Select(t => t.target)];

        var lastFieldIndex = Array.FindLastIndex(EditorFocusTargets, target => target.Neighbors is null && target.View is Border);
        int IndexOfCaption(string caption) => Array.FindIndex(EditorFocusTargets, target => target.Caption == caption);
        var saveIndex = IndexOfCaption("SALVAR MUDANÇAS");
        // Secondary actions: one equal 2-column grid; D-pad neighbours follow the grid.
        const int ActionColumns = 2;
        string[] actionCaptions =
        [
            "LEGALIZAR", "TORNAR MEU", "SHOWDOWN", "EXPORTAR .PK", "QR", "ENCONTRO / ORIGEM",
            "DETALHES DOS GOLPES", "LOJA DE GOLPES", "POTENCIAL", "VISUAL", "PRÊMIOS", "ÁLBUM DE RIBBONS",
            "FORMA E SHINY", "TREINADORES", "TECH RECORDS",
        ];
        var actionIndices = actionCaptions.Select(IndexOfCaption).ToArray();
        var legalizeIndex = actionIndices[0];

        EditorFocusTargets = EditorFocusTargets
            .Select((target, index) =>
            {
                if (target.Caption == "SALVAR MUDANÇAS")
                    return target with { Neighbors = new EditorFocusNeighbors(index, index, lastFieldIndex, legalizeIndex) };
                var at = Array.IndexOf(actionCaptions, target.Caption);
                if (at < 0) return target;
                var col = at % ActionColumns;
                int Pick(int i) => i >= 0 && i < actionIndices.Length ? actionIndices[i] : index;
                return target with
                {
                    Neighbors = new EditorFocusNeighbors(
                        col == 0 ? index : Pick(at - 1),
                        col == ActionColumns - 1 ? index : Pick(at + 1),
                        at < ActionColumns ? saveIndex : Pick(at - ActionColumns),
                        Pick(at + ActionColumns)),
                };
            })
            .ToArray();

        var monActions = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            ColumnSpacing = Design(20),
            RowSpacing = Design(16),
            Margin = new Thickness(Design(28), Design(4), Design(28), 0),
        };
        var actionButtons = new[] { legalize, makeMine, showdown, exportPk, qr, met, moveDetails, moveShop, potential, cosmetics, awards, ribbonAlbum, formShiny, trainers, techRecords };
        for (var i = 0; i < actionButtons.Length; i++)
        {
            var button = actionButtons[i];
            if (i % ActionColumns == 0) monActions.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            monActions.Add(button, i % ActionColumns, i / ActionColumns);
        }

        // Hardcore mode: the panel stays readable, but the note says up front why APPLY
        // and the mon-edit buttons refuse - rather than failing silently on press.
        var hardcoreNote = new Label
        {
            Text = HardcoreMode.StatusLine,
            FontFamily = DsChrome.PixelFont,
            FontSize = UiTokens.TextSmall,
            TextColor = UiTokens.Bad,
            HorizontalTextAlignment = TextAlignment.Center,
            IsVisible = HardcoreMode.IsOn,
        };

        var panel = new VerticalStackLayout
        {
            Children =
            {
                hardcoreNote,
                species, nickname, genderShiny, level, LevelInfoCard(),
                EditorSection("Info do PKMN"),
                nature, ability, item, friendship, ball, ot,
                EditorSection("Golpes do PKMN"),
                move1, move2, move3, move4,
                EditorSection("Atributos"),
            },
        };
        foreach (var line in statLines) panel.Children.Add(line);
        panel.Children.Add(EditorSection("Legalidade"));
        panel.Children.Add(legalityLine);
        panel.Children.Add(EditorSection("Ferramentas"));
        panel.Children.Add(save);
        panel.Children.Add(monActions);
        return panel;
    }

    private Task ShowLegalityReportAsync(string detail)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close()
        {
            _hostGrid.Remove(overlay);
            pad?.Dispose();
            done.TrySetResult();
        }

        var content = new VerticalStackLayout
        {
            Spacing = 10,
            Children =
            {
                Kit.HeaderBar("Relatório de ilegalidade"),
                new ScrollView
                {
                    HeightRequest = 220,
                    Content = new Label
                    {
                        Text = detail,
                        TextColor = UiTokens.Ink0,
                        FontSize = UiTokens.TextSmall,
                        LineBreakMode = LineBreakMode.WordWrap,
                    },
                },
                Kit.WindowHints(("B", "Voltar", Close)),
            },
        };

        var window = Kit.OverlayWindow(_hostGrid, content, preferredMaxWidth: 340, padding: 14);
        overlay = Kit.AttachOverlay(_hostGrid, window, Close);
        pad = new PadOverlay(Close, Close);
        return done.Task;
    }

    /// <summary>The ribbon album: every ribbon the format stores as tappable tiles with
    /// the legal-obtainable set lit (PKHeX's own per-encounter rules), an award-all
    /// shortcut, and marking chips that stamp the whole organizer selection at once.</summary>
    private async Task<bool> ShowRibbonAlbumAsync(Grid host, Domain.ISaveEngineSession session, int box, int slot)
    {
        var ribbons = session.GetRibbons(box, slot);
        if (ribbons.Count == 0)
        {
            await EditorMenu.ShowAsync(host, "Álbum de ribbons", "Este formato de Pokémon não guarda ribbons nem marks.", "OK");
            return false;
        }

        // Ribbon legality runs a full PKHeX analysis per Pokémon: keep it off the UI thread.
        var loading = LoadingOverlay.Show(host, "Álbum de ribbons", "Verificando quais ribbons este Pokémon pode ganhar legalmente…");
        IReadOnlyDictionary<string, int> maxima;
        try
        {
            maxima = await Task.Run(() => session.GetObtainableRibbonMaxima(box, slot));
        }
        finally
        {
            loading.Close();
        }

        var dirty = false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Grid overlay = null!;
        PadOverlay pad = null!;
        void Close()
        {
            host.Remove(overlay);
            pad?.Dispose();
            done.TrySetResult();
        }

        var summary = Kit.LcdLabel(12);
        void UpdateSummary(IReadOnlyList<RibbonAlbumEntry> entries)
        {
            var owned = entries.Count(e => e.Value != 0);
            var obtainable = entries.Count(e => e.Obtainable);
            summary.Text = $"{owned} obtidos · mais {obtainable} obteníveis · aceso = legal para este Pokémon";
        }

        var tiles = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.SpaceBetween,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Center,
        };

        async Task ToggleAsync(RibbonAlbumEntry entry)
        {
            if (entry.MaxValue == 1)
            {
                session.SetRibbon(box, slot, entry.Id, entry.Value == 0 ? 1 : 0);
                dirty = true;
            }
            else
            {
                var value = await StatsPopup.ShowSingleAsync(host, entry.Name, entry.Value, entry.MaxValue);
                if (value is not { } count || count == entry.Value) return;
                session.SetRibbon(box, slot, entry.Id, count);
                dirty = true;
            }
            Rebuild();
        }

        void Rebuild()
        {
            var entries = RibbonAlbum.Build(session.GetRibbons(box, slot), maxima);
            UpdateSummary(entries);
            tiles.Children.Clear();
            foreach (var entry in entries)
            {
                var current = entry;
                var image = new Image { WidthRequest = 30, HeightRequest = 30, HorizontalOptions = LayoutOptions.Center };
                var path = RibbonIconPath(entry.Id);
                if (path is not null) image.Source = ImageSource.FromFile(path);
                var name = new Label
                {
                    Text = entry.Name,
                    TextColor = UiTokens.Ink0,
                    FontSize = 12,
                    FontFamily = DsChrome.PixelFont,
                    HorizontalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 1,
                };
                // Owned = a toned gold plate, obtainable = a quiet stripe, the rest dimmed:
                // state by fill, never by thick coloured rings.
                var tile = new Border
                {
                    WidthRequest = 66,
                    HeightRequest = 64,
                    Margin = new Thickness(0, 0, 4, 4),
                    Padding = new Thickness(3),
                    StrokeThickness = entry.Value != 0 ? 1.2f : 0,
                    Stroke = entry.Value != 0 ? UiTokens.Rim : Colors.Transparent,
                    BackgroundColor = entry.Value != 0 ? UiTokens.AccentMoves : UiTokens.RowStripe,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = UiTokens.ControlRadius },
                    Content = new VerticalStackLayout { Children = { image, name } },
                    Opacity = entry.Value != 0 || entry.Obtainable ? 1 : 0.35,
                };
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) => await ToggleAsync(current);
                tile.GestureRecognizers.Add(tap);
                tiles.Children.Add(tile);
            }
        }

        // Marking chips: the organizer's stamps. With a marked selection they stamp
        // every marked mon (one write on close); otherwise just this Pokémon.
        var markings = session.GetCosmetics(box, slot).Markings;
        var targets = _viewModel.MarkedCount > 0
            ? _viewModel.MarkedSlots.ToList()
            : new List<(int Box, int Slot)> { (box, slot) };
        var markRow = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Direction = Microsoft.Maui.Layouts.FlexDirection.Row };
        var markCaption = new Label
        {
            Text = _viewModel.MarkedCount > 0 ? $"Marcas →{_viewModel.MarkedCount} marcadas:" : "Marcas →",
            TextColor = UiTokens.Ink1,
            FontFamily = DsChrome.PixelFont,
            FontSize = UiTokens.TextSmall,
            VerticalTextAlignment = TextAlignment.Center,
        };
        markRow.Children.Add(markCaption);
        for (var i = 0; i < markings.Count; i++)
        {
            var index = i;
            var marking = markings[i];
            var chip = Kit.Capsule(marking.Name, MarkingColor(marking.Value));
            chip.FontSize = UiTokens.TextSmall;
            chip.Padding = new Thickness(8, 4);
            chip.Margin = new Thickness(0, 0, 4, 4);
            chip.Clicked += (_, _) =>
            {
                // Cycle off → blue → pink (where the format has it) → off.
                var next = (marking.Value + 1) % (marking.MaxValue + 1);
                foreach (var target in targets)
                {
                    var cosmetics = session.GetCosmetics(target.Box, target.Slot);
                    var values = cosmetics.Markings.Select(m => m.Value).ToArray();
                    if (index >= values.Length) continue;
                    values[index] = next;
                    session.ApplyCosmeticEdit(target.Box, target.Slot, new CosmeticEdit(Markings: values));
                }
                dirty = true;
                marking = markings[index] with { Value = next };
                chip.BackgroundColor = MarkingColor(next);
            };
            markRow.Children.Add(chip);
        }

        var awardAll = Kit.Capsule("Dar todos os obteníveis", UiTokens.Green);
        awardAll.Clicked += (_, _) =>
        {
            var changed = session.AwardAllObtainableRibbons(box, slot);
            if (changed > 0) dirty = true;
            summary.Text = changed > 0
                ? $"{changed} ribbons a mais concedidos."
                : "Não há mais nada que este Pokémon possa ganhar legalmente.";
            Rebuild();
        };
        var close = Kit.Capsule("Pronto", UiTokens.Ink1);

        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Kit.HeaderBar("Álbum de ribbons"),
                summary,
                new ScrollView { Content = tiles, MaximumHeightRequest = 260 },
                markings.Count > 0 ? markRow : new BoxView { HeightRequest = 0 },
                new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center, Children = { awardAll, close } },
                Kit.WindowHints(("TOQUE", "Alternar ribbon", null), ("B", "Voltar", Close)),
            },
        };

        Rebuild();
        overlay = Kit.AttachOverlay(host, Kit.OverlayWindow(host, content, preferredMaxWidth: 440, padding: 12), Close);
        pad = new PadOverlay(Close, Close);
        await done.Task;
        return dirty;
    }

    private static Color MarkingColor(int value) => value switch
    {
        1 => UiTokens.MenuBlue,
        2 => UiTokens.GiftPinkLight,
        _ => UiTokens.Ink1,
    };

    /// <summary>Bundled ribbon artwork copied to cache once so Image can load it by file path.</summary>
    private static string? RibbonIconPath(string id)
    {
        var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, $"ribbon-{id.ToLowerInvariant()}.png");
        if (File.Exists(cache)) return cache;
        try
        {
            using var asset = FileSystem.OpenAppPackageFileAsync($"ribbons/{id.ToLowerInvariant()}.png").GetAwaiter().GetResult();
            using var output = File.Create(cache);
            asset.CopyTo(output);
            return cache;
        }
        catch
        {
            return null;
        }
    }

    private void FocusEntry(View row)
    {
        if (row is Border { Content: Grid grid })
            foreach (var child in grid.Children)
                if (child is Entry entry)
                {
                    entry.Focus();
                    return;
                }
    }

    private async Task OpenGenderPickerAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "Gênero", null,
            new PadOption("Macho", IconPath: "male"),
            new PadOption("Fêmea", IconPath: "female"),
            new PadOption("Sem gênero", IconPath: "genderless"));
        var gender = choice switch { "Macho" => 0, "Fêmea" => 1, "Sem gênero" => 2, _ => (int?)null };
        if (gender is { } value)
        {
            SetVmString(nameof(BoxBrowserViewModel.EditGender), value.ToString());
            UpdateGenderRow();
        }
    }

    private void UpdateGenderRow()
    {
        foreach (var target in EditorFocusTargets)
        {
            if (target.Caption != "GÊNERO" || target.View is not Border { Content: Grid grid }) continue;
            foreach (var child in grid.Children)
                if (child is Label { Text: not ("GÊNERO" or "Gênero") } label)
                {
                    label.Text = GetVmString(nameof(BoxBrowserViewModel.EditGender)) switch
                    {
                        "0" => "Macho",
                        "1" => "Fêmea",
                        _ => "Sem gênero",
                    };
                    return;
                }
        }
    }

    private async Task OpenNamedPickerAsync(string caption, string vmProperty, Func<List<PickItem>> itemsFactory)
    {
        var items = itemsFactory();
        if (items.Count == 0) return;
        int? current = int.TryParse(GetVmString(vmProperty), out var id) ? id : null;
        var picked = await PickerMenu.ShowAsync(_hostGrid, caption, items, current);
        if (picked is not null)
            SetVmString(vmProperty, picked.Id.ToString());
    }

    /// <summary>
    /// The nature row: labelled choices plus live stats that use the editor's pending
    /// species, level, IVs and EVs, so the numbers match what SAVE would write.
    /// </summary>
    private async Task OpenNaturePickerAsync(IGameDataService data)
    {
        var session = _sessionsFor();
        var slot = _viewModel.SelectedSlot;
        if (session is null || slot < 0 || session.Generation <= 2) return;
        static int? Int(string? text) => int.TryParse(text, out var v) ? v : null;
        static IReadOnlyList<int>? Six(string? text)
        {
            var parts = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var values = parts.Select(p => int.TryParse(p, out var v) ? v : -1).ToArray();
            return values.Length == 6 && values.All(v => v >= 0) ? values : null;
        }
        NatureStatPreview? preview = null;
        try
        {
            preview = NaturePicker.Service?.PreviewSlot(session, _viewModel.BoxIndex, slot, new StatPreviewOverrides(
                Int(GetVmString(nameof(BoxBrowserViewModel.EditSpecies))),
                Int(GetVmString(nameof(BoxBrowserViewModel.EditLevel))),
                Six(GetVmString(nameof(BoxBrowserViewModel.EditIvs))),
                Six(GetVmString(nameof(BoxBrowserViewModel.EditEvs)))));
        }
        catch (ArgumentException) { } // a stale slot: labels still help
        var picked = await NaturePicker.ShowAsync(_hostGrid, data.NatureNames, Int(GetVmString(nameof(BoxBrowserViewModel.EditNature))), preview);
        if (picked is not null)
            SetVmString(nameof(BoxBrowserViewModel.EditNature), picked.Id.ToString());
    }

    private async Task OpenStatsEditorAsync(string caption, string vmProperty, Func<int> max)
    {
        var current = (GetVmString(vmProperty) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out var value) ? value : 0).ToArray();
        if (current.Length != 6) current = new int[6];
        var updated = await StatsPopup.ShowAsync(_hostGrid, caption, current, Math.Max(1, max()),
            TrainingPreview(vmProperty == nameof(BoxBrowserViewModel.EditIvs)));
        if (updated is not null)
            SetVmString(vmProperty, string.Join(' ', updated));
    }

    private static int? ParseInt(string? text) => int.TryParse(text, out var v) ? v : null;

    private static IReadOnlyList<int>? ParseSix(string? text)
    {
        var values = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var v) ? v : -1).ToArray();
        return values.Length == 6 && values.All(v => v >= 0) ? values : null;
    }

    /// <summary>The editor's unsaved species/level/IVs/EVs: previews compute what SAVE would write.</summary>
    private StatPreviewOverrides PendingOverrides() => new(
        ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditSpecies))),
        ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditLevel))),
        ParseSix(GetVmString(nameof(BoxBrowserViewModel.EditIvs))),
        ParseSix(GetVmString(nameof(BoxBrowserViewModel.EditEvs))));

    /// <summary>The pending species (and its form: kept only while the species is unchanged).</summary>
    private (int Species, int Form) PendingSpeciesForm()
    {
        var detail = _viewModel.Selected;
        var species = ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditSpecies))) ?? detail?.Species ?? 0;
        return (species, detail is not null && detail.Species == species ? detail.Form : 0);
    }

    /// <summary>
    /// Live preview for the IV/EV window: the six stats with the edited spread against the
    /// editor's pending ones, the EV total against the format's cap, and for IVs the
    /// Hidden Power type the spread gives (with a chooser that re-rolls the low bits).
    /// </summary>
    private StatsPopup.StatsPreview? TrainingPreview(bool ivs)
    {
        var session = _sessionsFor();
        var slot = _viewModel.SelectedSlot;
        var box = _viewModel.BoxIndex;
        if (session is null || slot < 0) return null;
        var stats = NaturePicker.Service;
        var info = InfoPickers.Info;
        var pending = PendingOverrides();
        (IReadOnlyList<int>, IReadOnlyList<int>)? Stats(int[] values)
        {
            if (stats is null) return null;
            try
            {
                var before = stats.PreviewSlot(session, box, slot, pending);
                var after = stats.PreviewSlot(session, box, slot, ivs ? pending with { IVs = values } : pending with { EVs = values });
                return before is null || after is null ? null : (before.Current, after.Current);
            }
            catch (ArgumentException) { return null; }
        }
        var cap = ivs ? null : TrainingBudget.TotalCap(session.Generation, session.GetTrainingCaps());
        Func<int[], int?>? hiddenPower = ivs && info is not null ? values => info.GetHiddenPowerType(session, values) : null;
        Func<int[], int, IReadOnlyList<int>?>? ivsFor = ivs && info is not null ? (values, type) => info.GetIVsForHiddenPower(session, values, type) : null;
        return new StatsPopup.StatsPreview(Stats, cap, hiddenPower, ivsFor);
    }

    /// <summary>
    /// Under the LEVEL row: EXP at the typed level, EXP to the next, the stat change
    /// against the stored level, and a warning when the level falls below the met level
    /// (which no real Pokémon can be). Read-only; SAVE applies the level as before.
    /// </summary>
    private View LevelInfoCard()
    {
        // The EXP line sits in the header beside the species tab; the card appears only for a
        // pending level change (the stats it would give) or a level below the met level.
        var warn = InfoKit.Note(tone: InfoKit.NoteTone.Bad);
        var grid = new InfoKit.StatDeltaGrid { IsVisible = false };
        var card = InfoKit.Card(grid, warn);
        card.Margin = new Thickness(Design(28), Design(12));
        card.IsVisible = false;

        void Refresh()
        {
            var session = _sessionsFor();
            var slot = _viewModel.SelectedSlot;
            var info = InfoPickers.Info;
            var selected = _viewModel.Selected;
            if (session is null || info is null || slot < 0 || selected is null || selected.IsEmpty
                || ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditLevel))) is not { } level)
            {
                card.IsVisible = false;
                SetEditorExpLine(null);
                return;
            }
            LevelInfo? facts;
            try { facts = info.GetLevelInfo(session, _viewModel.BoxIndex, slot, level, PendingOverrides() with { Level = null }); }
            catch (ArgumentException) { facts = null; }
            if (facts is null) { card.IsVisible = false; SetEditorExpLine(null); return; }
            SetEditorExpLine(facts.Level >= 100
                ? $"EXP {facts.ExpAtLevel:N0} · nível máximo"
                : $"EXP {facts.ExpAtLevel:N0} no Lv {facts.Level}  ·  {facts.ExpToNext:N0} para o Lv {facts.Level + 1}");
            var changed = facts.Level != selected.Level && facts.StatsNow.Count == 6 && facts.StatsAtLevel.Count == 6;
            grid.IsVisible = changed;
            if (changed) grid.Show(facts.StatsNow, facts.StatsAtLevel);
            warn.Text = facts.BelowMetLevel ? $"Abaixo do nível de encontro ({facts.MetLevel}): isso seria ilegal." : null;
            warn.IsVisible = facts.BelowMetLevel;
            card.IsVisible = changed || facts.BelowMetLevel;
        }

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.EditLevel) or nameof(BoxBrowserViewModel.Selected)
                or nameof(BoxBrowserViewModel.EditSpecies) or nameof(BoxBrowserViewModel.EditIvs) or nameof(BoxBrowserViewModel.EditEvs))
                Refresh();
        };
        Refresh();
        return card;
    }

    /// <summary>The move rows: legal moves first with how they are learned, live details below.</summary>
    private async Task OpenMovePickerAsync(IGameDataService data, string caption, string vmProperty)
    {
        var (species, form) = PendingSpeciesForm();
        var picked = await InfoPickers.ShowMovesAsync(_hostGrid, caption, data, _sessionsFor(), _viewModel.BoxIndex,
            _viewModel.SelectedSlot, ParseInt(GetVmString(vmProperty)), species > 0 ? species : null, form);
        if (picked is not null)
            SetVmString(vmProperty, picked.Id.ToString());
    }

    /// <summary>The held-item row: holdable items first with their effects, sprites where cached.</summary>
    private async Task OpenHeldItemPickerAsync(IGameDataService data)
    {
        var directory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
        var placeholder = ItemArt.PlaceholderPath();
        string? Icon(int id)
        {
            var names = SaveItemNames(data);
            if ((uint)id >= (uint)names.Count) return placeholder;
            var cached = System.IO.Path.Combine(directory, ItemArt.Slug(names[id]) + ".png");
            return File.Exists(cached) ? cached : placeholder;
        }
        var picked = await InfoPickers.ShowHeldItemsAsync(_hostGrid, "Item segurado", data, _sessionsFor(),
            ParseInt(GetVmString(nameof(BoxBrowserViewModel.EditHeldItem))), Icon);
        if (picked is not null)
            SetVmString(nameof(BoxBrowserViewModel.EditHeldItem), picked.Id.ToString());
    }

    private async Task RunSubEditorAsync(Func<Grid, Domain.ISaveEngineSession, int, int, Task<bool>> editor, string message)
    {
        // Met/origin, move details, move shop, potential, cosmetics, awards and the
        // ribbon album all mutate the live session as they go - before the write funnel
        // ever sees them - so Hardcore mode stops them at this one door.
        if (Denied(SaveAction.EditMon)) return;
        var slot = _viewModel.SelectedSlot;
        var session = _sessionsFor();
        if (slot < 0 || session is null) return;
        var changed = await editor(_hostGrid, session, _viewModel.BoxIndex, slot);
        if (changed)
            await _viewModel.RunMutationAsync(_ => new GenerationOutcome(true, message), slot);
    }

    /// <summary>A Kit.Field wrapped in the striped attribute-row plate.</summary>
    private View FieldRow(string caption, string bindingPath, bool shaded)
    {
        var entry = new EditorEntry
        {
            FontSize = EditorText,
            FontFamily = DsChrome.PixelFont,
            TextColor = EditorValue,
            BackgroundColor = Colors.Transparent,
            VerticalTextAlignment = TextAlignment.Center,
            HeightRequest = EditorRowHeight,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        entry.SetBinding(Entry.TextProperty, bindingPath);
        return RowChrome(caption, entry, shaded);
    }

    private Domain.ISaveEngineSession? _sessionsFor() =>
        IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.CurrentSession;

    private Services.ProtectionStore Protection =>
        IPlatformApplication.Current!.Services.GetRequiredService<Services.ProtectionStore>();

    private string? DocumentId =>
        IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.Current?.Document.DocumentId;

    /// <summary>Item pick list with sprites for everything already in the icon cache (misses warm in the background).</summary>
    private static List<PickItem> ItemsWithIcons(IReadOnlyList<string> names)
    {
        var items = new List<PickItem>(names.Count) { new(0, "(none)") };
        var directory = System.IO.Path.Combine(FileSystem.AppDataDirectory, "items");
        for (var id = 1; id < names.Count; id++)
        {
            if (names[id].Length == 0) continue;
            var cached = System.IO.Path.Combine(directory, ItemArt.Slug(names[id]) + ".png");
            items.Add(new PickItem(id, names[id], File.Exists(cached) ? cached : ItemArt.PlaceholderPath()));
        }
        return items;
    }

    private List<PickItem> BallItems()
    {
        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        var items = new List<PickItem>();
        for (var id = 1; id < data.BallNames.Count; id++)
        {
            if (data.BallNames[id].Length == 0) continue;
            items.Add(new PickItem(id, data.BallNames[id], BallIconPath(id)));
        }
        return items;
    }

    /// <summary>Bundled ball icon copied to cache once so Image can load it by file path.</summary>
    private static string? BallIconPath(int ball)
    {
        var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, $"ballicon-{ball}.png");
        if (File.Exists(cache)) return cache;
        try
        {
            using var asset = FileSystem.OpenAppPackageFileAsync($"balls/_ball{ball}.png").GetAwaiter().GetResult();
            using var output = File.Create(cache);
            asset.CopyTo(output);
            return cache;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A tappable name field: caption + current value as a name, opens the searchable picker (or the Pokédex).</summary>
    private View NamedPicker(string caption, string vmProperty, IReadOnlyList<string> names, Func<List<PickItem>>? itemsFactory, bool openPokedex = false, bool shaded = false,
        Func<Task>? open = null, View? leading = null)
    {
        var value = EditorValueLabel();
        value.SetBinding(Label.TextProperty, new Binding(vmProperty, converter: new IdNameConverter(names)));
        View content = leading is null ? value : new HorizontalStackLayout { Spacing = Design(14), Children = { leading, value } };
        var chip = RowChrome(caption, content, shaded, EditorChevron());

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (open is not null) { await open(); return; }
            PickItem? picked;
            if (openPokedex)
            {
                var services = IPlatformApplication.Current!.Services;
                var data = services.GetRequiredService<IGameDataService>();
                var session = _sessionsFor();
                if (session is null) return;
                picked = await PokedexPicker.ShowAsync(_hostGrid, data, session);
            }
            else
            {
                var items = itemsFactory?.Invoke() ?? [];
                if (items.Count == 0) return;
                int? current = int.TryParse(GetVmString(vmProperty), out var id) ? id : null;
                picked = await PickerMenu.ShowAsync(_hostGrid, caption, items, current);
            }
            if (picked is not null)
                SetVmString(vmProperty, picked.Id.ToString());
        };
        chip.GestureRecognizers.Add(tap);
        return chip;
    }

    /// <summary>One shared row for the editor panel: caption column, value, optional trailing mark.</summary>
    private View RowChrome(string caption, View content, bool shaded, View? trailing = null)
    {
        var grid = new Grid
        {
            ColumnSpacing = Design(16),
            ColumnDefinitions = [new(new GridLength(Design(214))), new(GridLength.Star), new(GridLength.Auto)],
            Children = { EditorCaption(caption), content },
        };
        Grid.SetColumn(content, 1);
        if (trailing is not null)
        {
            grid.Children.Add(trailing);
            Grid.SetColumn(trailing, 2);
        }
        return EditorBand(grid, dark: shaded);
    }

    /// <summary>Picks one stat out of the space-separated EditStats string by index.</summary>
    private sealed class StatCellConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not string text || parameter is not string indexText || !int.TryParse(indexText, out var index))
                return "-";
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return index < parts.Length ? parts[index] : "-";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private string? GetVmString(string property) =>
        typeof(BoxBrowserViewModel).GetProperty(property)?.GetValue(_viewModel) as string;

    private void SetVmString(string property, string value) =>
        typeof(BoxBrowserViewModel).GetProperty(property)?.SetValue(_viewModel, value);

    /// <summary>Shows the id's display name; ids without a name fall back to the raw number.</summary>
    /// <summary>
    /// Item names in the open save's own id space. A held item is stored as the game's id:
    /// Gen 1-3 number their items differently from the modern table (Gen 2's 156 is Sacred
    /// Ash, the modern 156 is Persim Berry), so naming it with the modern list shows, and
    /// lets you pick, the wrong item.
    /// </summary>
    private IReadOnlyList<string> SaveItemNames(IGameDataService data) =>
        _sessionsFor()?.GetItemNames() is { Count: > 0 } names ? names : data.ItemNames;

    /// <summary>A name list read afresh on every lookup, for bindings made once but shown for whichever save is open.</summary>
    private sealed class LiveNames(Func<IReadOnlyList<string>> source) : IReadOnlyList<string>
    {
        public string this[int index] => source()[index];
        public int Count => source().Count;
        public IEnumerator<string> GetEnumerator() => source().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class IdNameConverter(IReadOnlyList<string> names) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is string text && int.TryParse(text, out var id) && id >= 0 && id < names.Count && names[id].Length > 0)
                return names[id];
            return value as string ?? "";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private async Task PushAsync<TPage>() where TPage : Page
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI services are unavailable.");
        await Navigation.PushAsync(services.GetRequiredService<TPage>());
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        if (_viewModel.BoxIndex == -1)
        {
            // The party pseudo-box renders as the navy deck, not the grid. The selected
            // card breathes like the games' party cursor.
            var phase = (float)((Environment.TickCount64 - _partyPulseStart) / 1000.0 * Math.PI * 2 / 1.4);
            PartyView.Paint(args.Surface.Canvas, args.Info, _sprites, _sessionsFor(), _viewModel.SelectedSlot, _frame.Request, _viewModel.CarrySource, phase,
                isMarked: _viewModel.SelectMode ? slot => _viewModel.IsMarked(-1, slot) : null,
                rangeMark: slot => _viewModel.InPendingRectangle(slot) ? _viewModel.PendingRectangle?.Mark : null);
            _hand.Reset();
            EnsurePartyPulse();
            return;
        }
        StopPartyPulse();
        BoxGridRenderer.Paint(args.Surface.Canvas, args.Info, _viewModel, _sprites, _theme, _frame.Request, _lockedSlots, _hand, BoxWallpaper());
    }

    /// <summary>Locked-mon badges for the current box; refreshed on box/mutation changes, never per frame.</summary>
    private void RefreshLockedSlots()
    {
        _lockedSlots.Clear();
        var docId = DocumentId;
        var session = _sessionsFor();
        if (docId is null || session is null || _viewModel.BoxIndex < 0) return;
        foreach (var summary in _viewModel.VisibleSlots)
        {
            if (summary.Species is null) continue;
            var pid = session.GetRngInfo(summary.Box, summary.Slot).Pid;
            if (Protection.IsMonLocked(docId, summary.Box, summary.Slot, pid))
                _lockedSlots.Add(summary.Slot);
        }
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (_viewModel.SelectMode)
        {
            TouchSelect(args);
            return;
        }
        // Skia only delivers Released if Pressed was marked handled.
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        var slot = TouchSlot(args.Location);
        args.Handled = true;
        if (slot < 0) return;
        // Touch: first tap selects (summary + editor), tapping the selected mon again grabs it,
        // next tap places. Empty slot with empty hands opens the add sheet.
        var wasSelected = _viewModel.SelectedSlot == slot;
        _viewModel.SelectSlot(slot);
        if (_viewModel.CarrySource is not null)
        {
            if (_viewModel.BoxIndex == -1 && slot != _viewModel.CarrySource.Value.Slot)
            {
                // Aim with the first tap (preview), confirm with the second on the same slot.
                if (wasSelected) _ = DropAndRepaintAsync();
                else _canvas.InvalidateSurface();
            }
            else
            {
                _ = DropAndRepaintAsync();
            }
            return;
        }
        var slots = _viewModel.VisibleSlots;
        if (slot < slots.Count && slots[slot].Species is null)
        {
            _ = OfferAddPokemonAsync(slot);
            return;
        }
        if (wasSelected && _viewModel.BeginCarry())
            _canvas.InvalidateSurface();
    }

    private int TouchSlot(SKPoint location) => _viewModel.BoxIndex == -1
        ? PartyView.SlotFromTouch(_canvas.CanvasSize, location)
        : BoxGridRenderer.StorageSlotFromTouch(_canvas.CanvasSize, location);

    /// <summary>Multi-select touch: a tap flips one mark, a drag sweeps the rectangle
    /// between where the finger landed and where it is (the hold-A gesture, by hand).</summary>
    private void TouchSelect(SKTouchEventArgs args)
    {
        args.Handled = true;
        var slot = TouchSlot(args.Location);
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                if (slot < 0) return;
                _touchDragSlot = slot;
                _viewModel.SelectSlot(slot);
                _viewModel.BeginRectangle(slot);
                break;
            case SKTouchAction.Moved:
                if (_touchDragSlot < 0 || slot < 0 || slot == _touchDragSlot) return;
                _touchDragSlot = slot;
                _viewModel.SelectSlot(slot);
                _viewModel.ExtendRectangle(slot);
                break;
            case SKTouchAction.Released or SKTouchAction.Cancelled:
                if (_touchDragSlot < 0) return;
                _touchDragSlot = -1;
                _viewModel.CommitRectangle();
                break;
            default:
                return;
        }
        _canvas.InvalidateSurface();
    }

    /// <summary>An empty slot is an invitation, not a dead cell: offer the ways to fill it.</summary>
    private async Task OfferAddPokemonAsync(int slot)
    {
        // Every way in this sheet fabricates a Pokémon, so Hardcore mode refuses the
        // whole sheet - with the reason on screen instead of an empty menu.
        if (await DeniedAsync(SaveAction.CreateMon)) return;
        var choice = await PadMenu.ShowAsync(_hostGrid, $"Adicionar um Pokémon · slot {slot + 1}", null,
            new PadOption("Criar um Pokémon", IconPath: "create", Detail: "Escolha uma espécie e os detalhes; ele é criado legal para este jogo."),
            new PadOption("Colar um set do Showdown", IconPath: "script", Detail: "Transforma um set do Showdown em um Pokémon legal neste slot."),
            new PadOption("Importar arquivo .pk", IconPath: "import", Detail: "Carrega um arquivo de Pokémon neste slot."),
            new PadOption("Do banco de dados de eventos", IconPath: "events", Detail: "Coloca um Pokémon de Mystery Gift neste slot."));
        switch (choice)
        {
            case "Criar um Pokémon":
                await RunGenerateWizardAsync(slot);
                return;
            case "Colar um set do Showdown":
                await RunShowdownPasteAsync(slot);
                return;
            case "Importar arquivo .pk":
            {
                var picker = IPlatformApplication.Current?.Services.GetService<IDocumentPicker>();
                var access = IPlatformApplication.Current?.Services.GetService<ISaveFileAccess>();
                if (picker is null || access is null) return;
                var document = await picker.PickSaveAsync();
                if (document is null) return;
                var bytes = (await access.ReadAsync(document.DocumentId)).ToArray();
                await _viewModel.RunMutationAsync(session =>
                    session.ImportSlot(_viewModel.BoxIndex, slot, bytes, document.DisplayName) // extension names the format
                        ? new GenerationOutcome(true, $"{document.DisplayName} importado.")
                        : new GenerationOutcome(false, "Esse arquivo não é um Pokémon reconhecível."), slot, action: SaveAction.CreateMon);
                _canvas.InvalidateSurface();
                return;
            }
            case "Do banco de dados de eventos":
            {
                var session = _sessionsFor();
                if (session is null) return;
                await EventGallery.ShowAsync(_hostGrid, _viewModel, session, slot, () => _canvas.InvalidateSurface());
                return;
            }
            default:
                return;
        }
    }

    /// <summary>Step-by-step creation: species → features → offline legalizer → into the slot.</summary>
    private async Task RunGenerateWizardAsync(int slot)
    {
        if (Denied(SaveAction.CreateMon)) return;
        var services = IPlatformApplication.Current!.Services;
        var data = services.GetRequiredService<IGameDataService>();
        var session = services.GetRequiredService<ISaveSessionService>().CurrentSession;
        if (session is null) return;

        var request = await GenerateWizard.RunAsync(_hostGrid, data, session);
        if (request is null) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "CRIANDO SEU POKéMON…",
            "O legalizador offline está procurando uma forma real e legal para este Pokémon existir.");
        try
        {
            await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.Generate(s, _viewModel.BoxIndex, slot, request), slot);
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }

    /// <summary>Paste a competitive set; the legalizer turns it into a legal mon in the slot.</summary>
    private async Task RunShowdownPasteAsync(int slot)
    {
        if (Denied(SaveAction.CreateMon)) return;
        var text = await TextPopup.ShowAsync(_hostGrid, "Colar um set do Showdown",
            "Cole o set exatamente como foi exportado do Pokémon Showdown.");
        if (string.IsNullOrWhiteSpace(text)) return;

        var overlay = LoadingOverlay.Show(_hostGrid, "Lendo o set…",
            "O legalizador offline está criando um Pokémon legal a partir do seu set.");
        try
        {
            await _viewModel.RunLegalizerAsync((legalizer, s) => legalizer.GenerateFromShowdown(s, _viewModel.BoxIndex, slot, text, Services.HaXMode.IsOn), slot);
            _canvas.InvalidateSurface();
        }
        finally
        {
            overlay.Close();
        }
    }
}
