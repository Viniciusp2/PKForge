using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The console home: a horizontal shelf of game cartridges, a first-run wizard when
/// nothing is linked, and a bottom hint bar. Landscape composition for the AYN Thor.
/// </summary>
public sealed class HomePage : ContentPage, IPadHandler
{
    private readonly SavePickerViewModel _viewModel;
    private ScrollView _shelf = null!;
    private HorizontalStackLayout _shelfItems = null!;
    private int _shelfIndex = -1;
    private DsCard[] _cards = [];
    private int _zone;       // 0 = game shelf, 1 = the destination cards
    private int _cardIndex;
    private PokeparkPage? _parkPage;
    private int _parkNavigationPending;

    public HomePage(SavePickerViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        Title = "PKForge";
        BackgroundColor = UiTokens.Housing;
        NavigationPage.SetHasNavigationBar(this, false);

        // GAMES section: the cartridge shelf (reused), labelled DS-style.
        var gamesLabel = new Label
        {
            Text = "Jogos", FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextTitle,
            TextColor = UiTokens.Ink0, VerticalOptions = LayoutOptions.Center,
        };
        var filterCaption = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextLabel,
            TextColor = UiTokens.Ink0, VerticalOptions = LayoutOptions.Center,
        };
        filterCaption.SetBinding(Label.TextProperty, new Binding("Caption",
            source: _viewModel.Filter, stringFormat: "Filtro · {0}"));
        // A menu button (the summary's resting tab), not an outlined pill.
        var filterChip = Kit.Tab("Filtro");
        filterChip.Content = filterCaption;
        Kit.SetTab(filterChip, false);
        filterCaption.TextColor = UiTokens.Ink0;
        filterChip.Padding = new Thickness(12, 4);
        filterChip.HorizontalOptions = LayoutOptions.End;
        var filterTap = new TapGestureRecognizer();
        filterTap.Tapped += (_, _) => _ = ShowFilterMenuAsync();
        filterChip.GestureRecognizers.Add(filterTap);
        var headerRow = new Grid
        {
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { gamesLabel, filterChip },
        };
        _shelfItems = new HorizontalStackLayout
        {
            Spacing = 12,
            VerticalOptions = LayoutOptions.Center,
        };
        BindableLayout.SetItemsSource(_shelfItems, _viewModel.Groups);
        BindableLayout.SetItemTemplate(_shelfItems, new DataTemplate(BuildCartridgeTile));
        _shelf = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalScrollBarVisibility = ScrollBarVisibility.Never,
            VerticalOptions = LayoutOptions.Center,
            Content = _shelfItems,
        };
        var shelfArea = new Grid
        {
            RowSpacing = 6,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { headerRow, _shelf },
        };
        Grid.SetRow(_shelf, 1);
        BlockNativeFocus(_shelf);

        // The three destinations as PKSM tiles with bundled pixel icons.
        var bank = new DsCard("bank", "Banco") { Tapped = () => _ = PushAsync<BankPage>() };
        var autopilot = new DsCard("pokedex", "Piloto da Dex") { Tapped = () => _ = OpenAutopilotAsync() };
        var park = new DsCard("park", "Poképark") { Tapped = () => _ = PushParkAsync() };
        var events = new DsCard("events", "Eventos") { Tapped = () => _ = ShowEventsMenuAsync() };
        var settings = new DsCard("settings", "Configurações") { Tapped = () => _ = ShowSettingsAsync() };
        _cards = [bank, autopilot, park, events, settings];
        foreach (var card in _cards) BlockNativeFocus(card);
        var cards = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)],
            Children = { bank, autopilot, park, events, settings },
        };
        Grid.SetColumn(autopilot, 1);
        Grid.SetColumn(park, 2);
        Grid.SetColumn(events, 3);
        Grid.SetColumn(settings, 4);

        var body = new Grid
        {
            Padding = new Thickness(14, 10),
            RowSpacing = 10,
            RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { shelfArea, cards },
        };
        Grid.SetRow(cards, 1);
        var bodyHost = new Grid { Children = { DsChrome.GridBackground(), body } };

        var footer = DsChrome.Footer(
            ("A", "Abrir", () => OnPadButton(PadButton.A)),
            // The cartridge identity menu (rename, color, which game): B on the pad, a hold by touch.
            ("B", "Editar jogo · segurar", () =>
            {
                if ((uint)_shelfIndex < (uint)_viewModel.Groups.Count) _ = ShowSaveMenuAsync(_viewModel.Groups[_shelfIndex]);
            }),
            ("L", "Filtro", () => _ = ShowFilterMenuAsync()),
            ("Y", "Vincular", () => _ = ShowLinkMenuAsync()),
            ("X", "Arquivo", () => _ = LinkFileAsync()),
            ("+", "Configurações", () => _ = ShowSettingsAsync()));

        var root = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { DsChrome.TitleBar(), DsChrome.StatusStrip("PKForge", "Offline"), bodyHost, footer },
        };
        Grid.SetRow((View)root.Children[1], 1);
        Grid.SetRow(bodyHost, 2);
        Grid.SetRow(footer, 3);

        _hostGrid = new Grid { Children = { root } };
        Content = _hostGrid;
    }

    private Grid _hostGrid = null!;

    /// <summary>
    /// The shelf's selection is drawn by PKForge (the cyan frame); Android's own focused
    /// state is the grey box that sticks around until the next tap, so it is disabled here.
    /// </summary>
    private static void BlockNativeFocus(View view)
    {
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is Android.Views.View platform)
            {
                platform.Focusable = false;
                platform.FocusableInTouchMode = false;
            }
        };
    }

    /// <summary>
    /// Handles touch on <paramref name="view"/> from the native touch stream: a press released
    /// within the touch slop is a tap, one that rests for the platform long-press timeout is a
    /// long press. Both live here because subscribing to the native stream replaces the
    /// listener a MAUI tap recognizer relies on, so a recognizer on the same view never fires.
    /// A drag that scrolls the shelf cancels the press.
    /// </summary>
    private static void AttachPress(View view, Action onTap, Action onLongPress)
    {
        view.HandlerChanged += (_, _) =>
        {
            if (view.Handler?.PlatformView is not Android.Views.View platform) return;
            var slop = Android.Views.ViewConfiguration.Get(platform.Context!)!.ScaledTouchSlop;
            var timeout = Android.Views.ViewConfiguration.LongPressTimeout;
            var token = 0;
            var pressing = false;
            float downX = 0, downY = 0;
            platform.Touch += (_, e) =>
            {
                var motion = e.Event!;
                switch (motion.ActionMasked)
                {
                    case Android.Views.MotionEventActions.Down:
                        var pressed = ++token;
                        pressing = true;
                        downX = motion.GetX();
                        downY = motion.GetY();
                        view.Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(timeout), () =>
                        {
                            if (pressed != token || !pressing) return;
                            pressing = false;
                            platform.PerformHapticFeedback(Android.Views.FeedbackConstants.LongPress);
                            onLongPress();
                        });
                        break;
                    case Android.Views.MotionEventActions.Move:
                        if (Math.Abs(motion.GetX() - downX) > slop || Math.Abs(motion.GetY() - downY) > slop)
                            pressing = false;
                        break;
                    case Android.Views.MotionEventActions.Up:
                        token++;
                        if (pressing) onTap();
                        pressing = false;
                        break;
                    case Android.Views.MotionEventActions.Cancel:
                        token++;
                        pressing = false;
                        break;
                }
                e.Handled = true;
            };
        };
    }

    // Once per launch, not per page: a color scheme change rebuilds Home, and the new page
    // must not scan, welcome or check for updates again.
    private static bool _welcomeShown;
    private static bool _scannedOnce;
    private static bool _updateCheckQueued;
    private bool _isAppearing;
    private bool _eventsSubscribed;
    private AvailableAppUpdate? _pendingAuthorizedUpdate;

    /// <summary>The Thor's lower screen is on from launch - the app *is* dual-screen.</summary>
    private SecondScreenClaim? _secondClaim;

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _isAppearing = true;
        if (!_crashOffered)
        {
            _crashOffered = true;
            _ = OfferCrashReportAsync();
        }
        _ = IPlatformApplication.Current?.Services.GetService<SpritePackDownloader>()?.FetchAddOnsQuietlyAsync();
        // The lower screen shows the shelf's highlighted game while Home is in front.
        _secondClaim ??= IPlatformApplication.Current?.Services.GetService<SecondScreenState>()?.Routes.CreateClaim(SecondScreenOwner.Home);
        _secondClaim?.Activate();
        if (!_eventsSubscribed)
        {
            App.Resumed += OnAppResumed;
            _viewModel.LinkReported += OnLinkReported;
            _eventsSubscribed = true;
        }
        _zone = 0;
        ClearCardFocus();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        // Scan once per launch; coming back from the box must not trigger a re-walk.
        if (!_scannedOnce)
        {
            _scannedOnce = true;
            var park = IPlatformApplication.Current?.Services.GetService<PokeparkService>();
            if (park is not null)
            {
                _ = Task.Run(() =>
                {
                    park.EnsureInitialized();
                    HabitatCatalog.Warm(park.LoadRoster().Select(mon => (mon.Species, mon.Form)));
                });
            }
            _viewModel.RescanCommand.Execute(null);
        }
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        if (host?.IsAvailable == true)
        {
            try { _ = host.ShowAsync(); }
            catch { }
        }
        if (_parkPage is null)
        {
            Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
            {
                if (_parkPage is null)
                    _parkPage = IPlatformApplication.Current?.Services.GetService<PokeparkPage>();
            });
        }

        // Returning from Android's install-unknown-apps screen finishes an update the
        // user already accepted with YES. No second question, no manual Check for update.
        if (_pendingAuthorizedUpdate is { } authorized && AppUpdateService.CanInstallUpdates())
        {
            _pendingAuthorizedUpdate = null;
            Dispatcher.DispatchAsync(async () => await InstallUpdateAsync(authorized));
            return;
        }

        // First run: welcome as the same in-world menu everything else uses, no special panel.
        if (_viewModel.ShowWizard && !_welcomeShown)
        {
            _welcomeShown = true;
            Dispatcher.Dispatch(async () =>
            {
                // First run plays like a Pokémon intro: a professor-style dialogue, then the choices.
                await DialogueBox.ShowSequenceAsync(_hostGrid,
                    "Olá! Bem-vindo ao PKForge!",
                    "Este é um lugar onde seus Pokémon de todos os jogos podem viver juntos, em segurança.",
                    "Eu posso editá-los, guardá-los no Banco e garantir que cada um seja legal.",
                    "E cada mudança ganha um backup antes de ser gravada, então nada se perde.",
                    "Agora sim... vamos encontrar seus jogos!");

                var choice = await PadMenu.ShowAsync(_hostGrid, "Começar", null,
                    new PadOption("Vincular um emulador", IconPath: "link", Detail: "Escolha a pasta do emulador onde seus jogos guardam os saves."),
                    new PadOption("Abrir um arquivo de save avulso", IconPath: "file", Detail: "Escolha um arquivo de save em vez de uma pasta inteira de emulador."),
                    new PadOption($"Baixar o pacote de sprites ({SpritePackDownloader.SizeHint})", IconPath: "download", Detail: "Sprites animados, imagens do HOME e ícones de itens, para usar offline."),
                    new PadOption("Talvez depois", IconPath: "close"));
                switch (choice)
                {
                    case "Vincular um emulador": await ShowLinkMenuAsync(); break;
                    case "Abrir um arquivo de save avulso": await LinkFileAsync(); break;
                    case var pack when pack?.StartsWith("Baixar o pacote de sprites", StringComparison.Ordinal) == true:
                        await DownloadSpritePackAsync();
                        break;
                }
                _viewModel.CompleteSetup();
                await CheckForUpdateAsync(automatic: true);
            });
        }
        else if (!_updateCheckQueued)
        {
            _updateCheckQueued = true;
            Dispatcher.Dispatch(async () => await CheckForUpdateAsync(automatic: true));
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _isAppearing = false;
        _secondClaim?.Release();
        if (_eventsSubscribed)
        {
            App.Resumed -= OnAppResumed;
            _viewModel.LinkReported -= OnLinkReported;
            _eventsSubscribed = false;
        }
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
    }

    private void OnLinkReported(string title, string message) =>
        MainThread.BeginInvokeOnMainThread(() => _ = PadMenu.ShowAsync(_hostGrid, title, message, "OK"));

    /// <summary>Android settings do not trigger MAUI page appearing again; resume does.</summary>
    private void OnAppResumed()
    {
        if (_pendingAuthorizedUpdate is not { } authorized || !AppUpdateService.CanInstallUpdates())
            return;

        _pendingAuthorizedUpdate = null;
        Dispatcher.DispatchAsync(async () => await InstallUpdateAsync(authorized));
    }

    /// <summary>Every button on the home screen, in one place.</summary>
    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Down when _zone == 0: _zone = 1; return FocusCard(0);
            case PadButton.Up when _zone == 1: _zone = 0; ClearCardFocus(); return true;
            case PadButton.Left: return _zone == 0 ? MoveShelf(-1) : FocusCard(_cardIndex - 1);
            case PadButton.Right: return _zone == 0 ? MoveShelf(1) : FocusCard(_cardIndex + 1);
            case PadButton.A:
                if (_zone == 1) { _cards[_cardIndex].Tapped?.Invoke(); return true; }
                return OpenShelfSelection();
            case PadButton.L: _ = ShowFilterMenuAsync(); return true;
            // B on a highlighted cartridge: its identity menu (rename, color, set game).
            case PadButton.B when _zone == 0 && _shelfIndex >= 0 && _shelfIndex < _viewModel.Groups.Count:
                _ = ShowSaveMenuAsync(_viewModel.Groups[_shelfIndex]);
                return true;
            case PadButton.X: _ = LinkFileAsync(); return true;
            case PadButton.Y: _ = ShowLinkMenuAsync(); return true;
            case PadButton.R: _ = PushAsync<BackupHistoryPage>(); return true;
            case PadButton.Start: _ = ShowSettingsAsync(); return true;
            default: return false;
        }
    }

    /// <summary>Move the cursor among the Bank/Events/Settings cards (the second focus zone).</summary>
    private bool FocusCard(int index)
    {
        if (_cards.Length == 0) return false;
        _cardIndex = Math.Clamp(index, 0, _cards.Length - 1);
        for (var i = 0; i < _cards.Length; i++) _cards[i].Selected = i == _cardIndex;
        return true;
    }

    private void ClearCardFocus()
    {
        foreach (var card in _cards) card.Selected = false;
    }

    private bool MoveShelf(int delta)
    {
        if (_viewModel.Groups.Count == 0) return false;
        _shelfIndex = Math.Clamp(_shelfIndex < 0 ? 0 : _shelfIndex + delta, 0, _viewModel.Groups.Count - 1);
        var selected = _viewModel.Groups[_shelfIndex];
        for (var i = 0; i < _viewModel.Groups.Count; i++)
            _viewModel.Groups[i].IsSelected = i == _shelfIndex;
        if (_shelfIndex < _shelfItems.Children.Count && _shelfItems.Children[_shelfIndex] is View selectedView)
        {
            var centeredX = Math.Max(0, selectedView.X - Math.Max(0, (_shelf.Width - selectedView.Width) / 2));
            // Smooth scroll for single steps; snap while the pad repeats fast. Overlapping
            // animated scrolls during held navigation made the shelf teleport.
            var snap = Environment.TickCount64 - _lastShelfMoveMs < 200;
            _ = _shelf.ScrollToAsync(centeredX, 0, !snap);
        }
        _lastShelfMoveMs = Environment.TickCount64;
        // The lower screen previews the highlighted game's hero art.
        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        if (state is not null) state.PreviewGame = selected.Save;
        return true;
    }

    private long _lastShelfMoveMs;

    private bool OpenShelfSelection()
    {
        if (_shelfIndex < 0 || _shelfIndex >= _viewModel.Groups.Count) return MoveShelf(0);
        _ = OpenCardAsync(_viewModel.Groups[_shelfIndex]);
        return true;
    }

    /// <summary>Narrows the cartridge shelf: every game, one generation, one console,
    /// or plain alphabetical.</summary>
    private async Task ShowFilterMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "Filtrar jogos", null,
            new PadOption("Todos os jogos", IconPath: "all"),
            new PadOption("Ordem de lançamento", IconPath: "calendar", Detail: "Todos os jogos, do lançamento mais antigo ao mais novo."),
            new PadOption("Alfabética (A-Z)", IconPath: "alpha"),
            new PadOption("Game Boy (Gen I-II)", IconPath: "platform-gb"),
            new PadOption("GBA (Gen III)", IconPath: "platform-gba"),
            new PadOption("DS (Gen IV-V)", IconPath: "platform-ds"),
            new PadOption("3DS (Gen VI-VII)", IconPath: "azahar"),
            new PadOption("Switch (Gen VII-IX)", IconPath: "eden"),
            new PadOption("Gen I", IconPath: "generation"),
            new PadOption("Gen II", IconPath: "generation"),
            new PadOption("Gen III", IconPath: "generation"),
            new PadOption("Gen IV", IconPath: "generation"),
            new PadOption("Gen V", IconPath: "generation"),
            new PadOption("Gen VI", IconPath: "generation"),
            new PadOption("Gen VII", IconPath: "generation"),
            new PadOption("Gen VIII", IconPath: "generation"),
            new PadOption("Gen IX", IconPath: "generation"));
        var (key, caption) = choice switch
        {
            "Todos os jogos" => ("all", "Todos"),
            "Ordem de lançamento" => ("release", "Lançamento"),
            "Alfabética (A-Z)" => ("az", "A-Z"),
            "Game Boy (Gen I-II)" => ("gb", "Game Boy"),
            "GBA (Gen III)" => ("gba", "GBA"),
            "DS (Gen IV-V)" => ("ds", "DS"),
            "3DS (Gen VI-VII)" => ("3ds", "3DS"),
            "Switch (Gen VII-IX)" => ("switch", "Switch"),
            { } gen when gen is not null && gen.StartsWith("Gen ", StringComparison.Ordinal) && int.TryParse(gen[4..], out var n)
                => ($"gen{n}", $"Gen {gen[4..]}"),
            _ => ("all", "Todos"),
        };
        _viewModel.ApplyFilter(key, caption);
        // Groups were replaced wholesale: re-run selection so a cartridge is highlighted.
        MoveShelf(_shelfIndex);
    }

    /// <summary>A cartridge with one save opens it; a shared cartridge asks which save first.</summary>
    private async Task OpenCardAsync(SaveCard card)
    {
        var save = await ChooseSaveAsync(card, "Qual save você quer abrir?");
        if (save is not null) await OpenSaveAsync(save);
    }

    /// <summary>Long-press / B: the save's identity menu ("Open save" first); a shared cartridge picks the save first.</summary>
    private async Task ShowSaveMenuAsync(SaveCard card)
    {
        var save = await ChooseSaveAsync(card, "Escolha o save para renomear, mudar a cor ou identificar.");
        if (save is null) return;
        if (await SaveIdentitySheet.ShowAsync(_hostGrid, _viewModel, save))
            await OpenSaveAsync(save);
    }

    private Task<DetectedSave?> ChooseSaveAsync(SaveCard card, string message) =>
        SavePickerSheet.ChooseFromTileAsync(_hostGrid, card.Saves,
            $"{card.DisplayName} · {card.SaveCount} saves", message);

    /// <summary>
    /// First open of a save whose editions share one format (FireRed/LeafGreen, Ruby/Sapphire,
    /// Diamond/Pearl): the bytes cannot say which cartridge it is, so the player picks once and
    /// the choice sticks (it stays editable from the save's menu). False when they backed out.
    /// </summary>
    private async Task<bool> ChooseEditionOnceAsync(DetectedSave save)
    {
        var store = _viewModel.Identities;
        var current = store.Get(save.DocumentId) ?? new SaveIdentity(save.DocumentId);
        if (_editionDeferred.Contains(save.DocumentId) || save.Guess is not { } guess
            || !SaveIdentityRules.NeedsEditionChoice(guess, current.GameChoiceId))
            return true;

        const string later = "Decidir depois";
        var editions = SaveIdentityRules.ChoicesFor(guess.Family, guess.Label).Where(c => !c.IsHack).ToArray();
        var options = editions.Select(c => new PadOption(c.Label, IconPath: "game"))
            .Append(new PadOption(later, IconPath: "close")).ToArray();
        var choice = await PadMenu.ShowAsync(_hostGrid, "Qual versão é esta?",
            "As duas versões gravam o mesmo arquivo de save, então o PKForge não consegue diferenciá-las. " +
            "Escolha a sua uma vez; você pode mudar depois no menu do save (toque longo).", options);
        if (choice is null) return false;
        if (choice == later)
        {
            _editionDeferred.Add(save.DocumentId);
            return true;
        }
        store.Set(current with { GameChoiceId = editions.First(c => c.Label == choice).Id });
        return true;
    }

    /// <summary>Saves whose edition the player chose to decide later: not asked again this session.</summary>
    private readonly HashSet<string> _editionDeferred = new(StringComparer.Ordinal);

    /// <summary>Platform folders keep emulator choices together for touch and controller use.</summary>
    private async Task ShowLinkMenuAsync()
    {
        while (true)
        {
            var platform = await PadMenu.ShowAsync(_hostGrid, "Vincular uma unidade de armazenamento", "Escolha uma plataforma para ver os emuladores dela.",
                new PadOption("Game Boy / Game Boy Color", IconPath: "platform-gb"),
                new PadOption("Game Boy Advance", IconPath: "platform-gba"),
                new PadOption("Nintendo DS", IconPath: "platform-ds"),
                new PadOption("GameCube", IconPath: "platform-gc"),
                new PadOption("Nintendo 3DS", IconPath: "azahar"),
                new PadOption("Nintendo Switch", IconPath: "eden"),
                new PadOption("Arquivo de save avulso", IconPath: "file"));
            if (platform is null) return;
            if (platform == "Arquivo de save avulso") { await LinkFileAsync(); return; }
            var options = platform switch
            {
                "Game Boy / Game Boy Color" => new[]
                {
                    new PadOption("RetroArch", IconPath: "retroarch"),
                    new PadOption("Linkboy", IconPath: "linkboy"),
                    new PadOption("Pizza Boy C (GB/GBC)", IconPath: "pizzaboy"),
                },
                "Game Boy Advance" => new[]
                {
                    new PadOption("RetroArch", IconPath: "retroarch"),
                    new PadOption("Linkboy", IconPath: "linkboy"),
                    new PadOption("Pizza Boy A (GBA)", IconPath: "pizzaboy"),
                },
                "Nintendo DS" => new[]
                {
                    new PadOption("melonDS", IconPath: "melonds"),
                    new PadOption("DraStic", IconPath: "drastic"),
                    new PadOption("RetroArch", IconPath: "retroarch"),
                },
                "GameCube" => new[] { new PadOption("Dolphin", IconPath: "dolphin") },
                "Nintendo 3DS" => new[]
                {
                    new PadOption("Azahar / Lime3DS", IconPath: "azahar"),
                    new PadOption("Citra MMJ", IconPath: "azahar"),
                },
                _ => new[] { new PadOption("Eden", IconPath: "eden") },
            };
            var choice = await PadMenu.ShowAsync(_hostGrid, platform, null, options);
            if (choice is null) continue;
            var guidance = choice switch
            {
                "Dolphin" => "Salve no jogo e pare a emulação antes de editar Colosseum ou XD. Selecione a pasta GC do Dolphin, uma pasta de região, ou Card A / Card B contendo saves .gci. Para memory cards .raw, exporte o jogo como GCI no Memory Card Manager do Dolphin, ou configure esse slot de cartão como GCI Folder. Depois de editar, inicie o jogo normalmente; carregar um save state antigo pode desfazer suas edições.",
                "DraStic" => "Salve no jogo e feche o DraStic. Selecione a pasta backup dele contendo saves de bateria .dsv, ou a pasta de dados do DraStic. Save states não são suportados. Reinicie o jogo normalmente depois de editar.",
                "Pizza Boy A (GBA)" or "Pizza Boy C (GB/GBC)" => "Salve no jogo e feche o Pizza Boy. Selecione a pasta com os saves de bateria (.sav), não os save states. Se o Android esconder a pasta, exporte o save de bateria no Pizza Boy e vincule esse arquivo exportado. Importe o arquivo editado de volta no Pizza Boy e reinicie o jogo normalmente.",
                "Azahar / Lime3DS" => "Salve no jogo e feche o emulador. Selecione a pasta user que você escolheu na configuração dele (a que contém sdmc); sdmc, Nintendo 3DS ou uma pasta acima da pasta user também funcionam. Reinicie o jogo normalmente depois de editar.",
                "Citra MMJ" => "Salve no jogo e feche o Citra MMJ. No seletor de arquivos, abra a entrada Citra MMJ (ou Android/data/org.citra.emu/files) e selecione citra-emu, ou /citra-emu em Androids mais antigos. Reinicie o jogo normalmente depois de editar.",
                "Eden" => "Salve no jogo e feche o emulador. Selecione a raiz de arquivos dele, que contém o armazenamento emulado. Reinicie o jogo normalmente depois de editar.",
                "melonDS" => "Salve no jogo e feche o melonDS. Selecione a pasta com seus saves de bateria .sav: ao lado das suas ROMs por padrão, ou a pasta de saves definida nas configurações do melonDS. Save states não são suportados. Reinicie o jogo normalmente depois de editar.",
                _ => "Salve no jogo e feche o emulador. Selecione a pasta de saves dele (ou a pasta com seus saves de bateria). Save states não são suportados. Reinicie o jogo normalmente depois de editar.",
            };
            var proceed = await PadMenu.ShowAsync(_hostGrid, $"Vincular {choice}",
                guidance + " Se o Android não oferecer acesso à pasta, use Arquivo de save avulso com um save exportado.",
                new PadOption("Escolher pasta", IconPath: "folder"), new PadOption("Voltar", IconPath: "back"));
            if (proceed != "Escolher pasta") continue;
            switch (choice)
            {
                case "RetroArch": await _viewModel.AddRetroArchCommand.ExecuteAsync(null); break;
                case "melonDS": await _viewModel.AddMelonDsCommand.ExecuteAsync(null); break;
                case "Linkboy": await _viewModel.AddLinkboyCommand.ExecuteAsync(null); break;
                case "Azahar / Lime3DS": await _viewModel.AddAzaharCommand.ExecuteAsync(null); break;
                case "Citra MMJ": await _viewModel.AddCitraMmjCommand.ExecuteAsync(null); break;
                case "Eden": await _viewModel.AddEdenCommand.ExecuteAsync(null); break;
                case "Dolphin": await _viewModel.AddDolphinCommand.ExecuteAsync(null); break;
                case "DraStic": await _viewModel.AddDraSticCommand.ExecuteAsync(null); break;
                case "Pizza Boy A (GBA)": await _viewModel.AddPizzaBoyGbaCommand.ExecuteAsync(null); break;
                case "Pizza Boy C (GB/GBC)": await _viewModel.AddPizzaBoyGbcCommand.ExecuteAsync(null); break;
            }
            return;
        }
    }

    private async Task ShowSettingsAsync()
    {
        var hiddenCount = _viewModel.HiddenSaves.Count;
        var hiddenOption = $"Mostrar saves ocultos ({hiddenCount})";
        var options = new List<PadOption>
        {
            new("Vincular um emulador", IconPath: "link", Detail: "Escolha a pasta do emulador onde seus jogos guardam os saves."),
            new("Gerenciar armazenamento vinculado", IconPath: "sdcard", Detail: "Desvincular pastas de emulador. Seus arquivos continuam no aparelho."),
            new("Abrir um arquivo de save", IconPath: "file", Detail: "Escolha um arquivo de save em vez de uma pasta inteira de emulador."),
        };
        if (hiddenCount > 0) options.Add(new PadOption(hiddenOption, IconPath: "show", Detail: "Saves que você ocultou do Início. Escolha um para mostrá-lo de novo."));
        var scheme = $"Esquema de cores: {PKForge.Chrome.ColorTheme.Current.Name}";
        var background = $"Fundo da box: {BoxBackground.Name(BoxBackground.Style)}";
        options.AddRange(
        [
            new PadOption(scheme, IconPath: "palette", Detail: "As cores do app: os azuis padrão ou um tema para cada tipo."),
            new PadOption(background, IconPath: "box", Detail: "Como cada box do PC mostra o papel de parede que o jogo dá a ela."),
            new PadOption("Pontos de restauração", IconPath: "history", Detail: "Os backups feitos antes de cada gravação, para deixar um save como ele era."),
            new PadOption("Sobre o PKForge", IconPath: "info"),
            new PadOption("Verificar atualização", IconPath: "update"),
            new PadOption("Música", IconPath: "music", Detail: "Toque seus próprios arquivos de áudio em segundo plano."),
            new PadOption("Diversos", IconPath: "gears", Detail: "Pacote de sprites, nova varredura, logs, tela e modos de edição."),
            new PadOption("Sair do PKForge", IconPath: "quit"),
        ]);
        var choice = await PadMenu.ShowAsync(_hostGrid, "Configurações", null, [.. options]);
        if (choice == hiddenOption) { await ShowHiddenSavesAsync(); return; }
        if (choice == scheme) { await ShowColorSchemeAsync(); return; }
        if (choice == background) { await ShowBoxBackgroundAsync(); return; }
        switch (choice)
        {
            case "Vincular um emulador": await ShowLinkMenuAsync(); break;
            case "Gerenciar armazenamento vinculado": await ShowManageLinkedStorageAsync(); break;
            case "Abrir um arquivo de save": await LinkFileAsync(); break;
            case "Pontos de restauração": await PushAsync<BackupHistoryPage>(); break;
            case "Sobre o PKForge": await AboutPopup.ShowAsync(_hostGrid); break;
            case "Verificar atualização": await CheckForUpdateAsync(automatic: false); break;
            case "Música": await ShowMusicAsync(); break;
            case "Diversos": await ShowMiscAsync(); break;
            case "Sair do PKForge":
                if (await PadMenu.ConfirmAsync(_hostGrid, "Sair do PKForge?", "Edições não salvas em menus abertos já têm backup a cada gravação.", "Sair"))
                    Microsoft.Maui.Controls.Application.Current?.Quit();
                break;
        }
    }

    /// <summary>Hidden saves, one row each; picking one puts it back on the shelf.</summary>
    private async Task ShowHiddenSavesAsync()
    {
        while (_viewModel.HiddenSaves is { Count: > 0 } hidden)
        {
            var save = await SavePickerSheet.ChooseFromTileAsync(_hostGrid, hidden, "Saves ocultos",
                "Escolha um save para mostrá-lo de novo no Início e nos seletores de save.", allowSingle: true);
            if (save is null) return;
            var current = _viewModel.Identities.Get(save.DocumentId) ?? new SaveIdentity(save.DocumentId);
            _viewModel.Identities.Set(current with { Hidden = false });
        }
    }

    private static string IconFor(EmulatorKind kind) => kind switch
    {
        EmulatorKind.RetroArch => "retroarch",
        EmulatorKind.MelonDS => "melonds",
        EmulatorKind.Linkboy => "linkboy",
        EmulatorKind.Azahar or EmulatorKind.CitraMmj => "azahar",
        EmulatorKind.Eden => "eden",
        EmulatorKind.Dolphin => "dolphin",
        EmulatorKind.DraStic => "drastic",
        EmulatorKind.PizzaBoyGba or EmulatorKind.PizzaBoyGbc => "pizzaboy",
        _ => "storage",
    };

    private async Task ShowManageLinkedStorageAsync()
    {
        var roots = IPlatformApplication.Current!.Services.GetRequiredService<IWatchedRootStore>().GetRoots();
        if (roots.Count == 0)
        {
            _viewModel.Status = "Nenhuma unidade de armazenamento vinculada.";
            return;
        }

        var options = roots
            .Select(root => new PadOption($"Desvincular {SaveDescriptions.EmulatorName(root.Kind)} · {root.DisplayName}", IconPath: IconFor(root.Kind)))
            .Append(new PadOption("Desvincular todas as unidades", IconPath: "unlink", Detail: "Remove todas as pastas vinculadas. Seus arquivos continuam no aparelho."))
            .Append(new PadOption("Cancelar", IconPath: "close"))
            .ToArray();
        var choice = await PadMenu.ShowAsync(_hostGrid, "Armazenamento vinculado", "Remova uma pasta de emulador vinculada sem resetar o app.", options);
        if (choice is null or "Cancelar") return;

        var store = IPlatformApplication.Current!.Services.GetRequiredService<IWatchedRootStore>();
        if (choice == "Desvincular todas as unidades")
        {
            var confirmedAll = await PadMenu.ConfirmAsync(_hostGrid, "Desvincular todo o armazenamento?",
                "Todas as pastas de emulador vinculadas serão removidas do PKForge. Seus arquivos continuam no aparelho.", "Desvincular tudo");
            if (!confirmedAll) return;
            foreach (var root in roots)
                store.RemoveRoot(root);
            _viewModel.Status = "Todas as unidades de armazenamento vinculadas foram removidas.";
        }
        else
        {
            var index = Array.FindIndex(options, option => option.Label == choice);
            if (index < 0 || index >= roots.Count) return;
            var root = roots[index];
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Desvincular armazenamento?",
                $"Remover {SaveDescriptions.EmulatorName(root.Kind)} · {root.DisplayName} do PKForge? Seus arquivos continuam no aparelho.", "Desvincular");
            if (!confirmed) return;
            store.RemoveRoot(root);
            _viewModel.Status = $"{SaveDescriptions.EmulatorName(root.Kind)} unlinked.";
        }

        await _viewModel.RescanCommand.ExecuteAsync(null);
    }

    /// <summary>Release checks: automatic failures stay quiet, manual failures explain themselves.</summary>
    private async Task CheckForUpdateAsync(bool automatic)
    {
        var service = IPlatformApplication.Current?.Services.GetService<AppUpdateService>();
        if (service is null) return;

        AppUpdateCheck check;
        try
        {
            check = await service.CheckAsync();
        }
        catch (Exception error)
        {
            Android.Util.Log.Warn("PKForgeUpdate", $"A verificação automática de atualização falhou: {error}");
            if (!automatic)
                _viewModel.Status = $"A verificação de atualização falhou: {error.Message}";
            return;
        }

        if (check.Update is not { } update || !check.IsAvailable)
        {
            if (!automatic)
                await DialogueBox.ShowSequenceAsync(_hostGrid, check.Message);
            return;
        }
        if (automatic && !service.ShouldPromptAutomatically(update))
            return;
        if (automatic && !_isAppearing)
            return;

        var choice = await DialogueChoiceBox.ShowAsync(_hostGrid,
            $"Uma nova versão do PKForge está disponível! A versão {update.Version} está pronta para instalar. Quer atualizar agora?",
            "SIM", "NÃO", "NÃO ME LEMBRE");
        switch (choice)
        {
            case "SIM":
                await InstallUpdateAsync(update);
                break;
            case "NÃO":
                _viewModel.Status = $"A versão {update.Version} será oferecida de novo na próxima abertura.";
                break;
            case "NÃO ME LEMBRE":
                service.DontRemindMe(update);
                _viewModel.Status = $"Não vou mais te lembrar da versão {update.Version}.";
                break;
        }
    }

    private async Task InstallUpdateAsync(AvailableAppUpdate update)
    {
        var service = IPlatformApplication.Current!.Services.GetRequiredService<AppUpdateService>();
        var overlay = LoadingOverlay.Show(_hostGrid, "Baixando a atualização!",
            $"O PKForge {update.Version} está a caminho. O Android vai confirmar a instalação.");
        AppUpdateInstallResult result;
        try
        {
            result = await service.DownloadAndInstallAsync(update, (done, total) =>
            {
                var percent = total <= 0 ? 0 : (int)Math.Clamp(done * 100 / total, 0, 100);
                overlay.Report(percent, 100);
            }, overlay.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Atualização pausada. Você pode retomá-la nas Configurações.";
            return;
        }
        catch (Exception error)
        {
            Android.Util.Log.Error("PKForgeUpdate", $"Falha ao baixar/instalar: {error}");
            _viewModel.Status = $"A atualização falhou: {error.Message}";
            var openRelease = await PadMenu.ConfirmAsync(_hostGrid, "Abrir a página da release?",
                "O instalador do app não conseguiu terminar. A página da release no GitHub tem o mesmo APK.", "Abrir");
            if (openRelease)
                await Launcher.OpenAsync(update.ReleaseUrl);
            return;
        }
        finally
        {
            overlay.Close();
        }

        switch (result)
        {
            case AppUpdateInstallResult.InstallerOpened:
                _viewModel.Status = "O Android está instalando a atualização.";
#if ANDROID
                // Android answers later, through the install receiver; a refusal is shown here.
                void OnFailed(string reason)
                {
                    UpdateInstallReceiver.Failed -= OnFailed;
                    _ = ReportInstallFailureAsync(update, reason);
                }
                UpdateInstallReceiver.Failed += OnFailed;
#endif
                break;
            case AppUpdateInstallResult.ReleasePageOpened:
                _viewModel.Status = "A página da release do PKForge está aberta.";
                break;
            case AppUpdateInstallResult.InstallPermissionRequired:
                _pendingAuthorizedUpdate = update;
                var permissionChoice = await DialogueChoiceBox.ShowAsync(_hostGrid,
                    "O Android precisa de permissão para instalar atualizações do PKForge. Permita instalar apps desconhecidos para o PKForge e verifique de novo?",
                    "ABRIR CONFIGURAÇÕES", "AGORA NÃO");
                if (permissionChoice == "ABRIR CONFIGURAÇÕES")
                    AppUpdateService.OpenInstallPermissionSettings();
                break;
        }
    }

    private async Task ReportInstallFailureAsync(AvailableAppUpdate update, string reason)
    {
        _viewModel.Status = $"A atualização falhou: {reason}";
        var openRelease = await PadMenu.ConfirmAsync(_hostGrid, "Abrir a página da release?",
            $"O Android não instalou a atualização ({reason}). A página da release no GitHub tem o mesmo APK.", "Abrir");
        if (openRelease)
            await Launcher.OpenAsync(update.ReleaseUrl);
    }

    private Task ShowMusicAsync() => MusicMenu.ShowAsync(_hostGrid, text => _viewModel.Status = text);

    /// <summary>How each PC box shows its game wallpaper.</summary>
    private async Task ShowBoxBackgroundAsync()
    {
        var styles = Enum.GetValues<PKForge.Chrome.StoragePaint.WallpaperStyle>();
        var picked = await PadMenu.ShowAsync(_hostGrid, "Fundo da box", null,
            [
                new PadOption(BoxBackground.Name(PKForge.Chrome.StoragePaint.WallpaperStyle.Blue), Detail: "A estampa do papel de parede do jogo nos tons do esquema de cores."),
                new PadOption(BoxBackground.Name(PKForge.Chrome.StoragePaint.WallpaperStyle.Veiled), Detail: "O papel de parede do jogo sob um véu do esquema de cores."),
                new PadOption(BoxBackground.Name(PKForge.Chrome.StoragePaint.WallpaperStyle.Duotone), Detail: "A estampa do papel de parede em dois tons: o do esquema de cores e o dela própria."),
                new PadOption(BoxBackground.Name(PKForge.Chrome.StoragePaint.WallpaperStyle.Horizon), Detail: "O papel de parede sobe a partir de baixo e se dissolve no esquema de cores."),
            ]);
        foreach (var style in styles.Where(style => BoxBackground.Name(style) == picked))
        {
            BoxBackground.Set(style);
            _viewModel.Status = $"Fundo da box: {BoxBackground.Name(style)}.";
        }
    }

    /// <summary>The color scheme picker; a new scheme rebuilds Home and the lower screen in its colors.</summary>
    private async Task ShowColorSchemeAsync()
    {
        var theme = await ThemePicker.ShowAsync(_hostGrid);
        if (theme is null || theme.Id == PKForge.Chrome.ColorTheme.Current.Id) return;
        ColorThemeSetting.Set(theme);
        AppLog.Info("theme", $"Player picked the {theme.Id} color scheme");
        if (Application.Current is App app) await app.ReloadForThemeAsync();
    }

    /// <summary>Maintenance actions: the sprite pack, the rescan, and the scan report.</summary>
    private async Task ShowMiscAsync()
    {
        var trainerProfiles = IPlatformApplication.Current!.Services.GetRequiredService<TrainerProfileStore>();
        var choice = await PadMenu.ShowAsync(_hostGrid, "Diversos", null,
            new PadOption(trainerProfiles.UseCurrentTrainerForGeneration
                ? "Pokémon gerados com o treinador do save: LIGADO"
                : "Pokémon gerados com o treinador do save: DESLIGADO", IconPath: "profile",
                Detail: "LIGADO faz os Pokémon que você cria pertencerem ao treinador do save aberto."),
            new PadOption($"Baixar pacote completo de sprites ({SpritePackDownloader.SizeHint})", IconPath: "download", Detail: "Sprites animados, imagens do HOME e ícones de itens, para usar offline."),
            new PadOption("Procurar jogos de novo", IconPath: "refresh", Detail: "Procurar saves de novo nas suas pastas vinculadas."),
            new PadOption("Relatório da varredura", IconPath: "report", Detail: "O que a última varredura encontrou em cada pasta, para copiar e nos enviar."),
            new PadOption("Compartilhar logs", IconPath: "export", Detail: "Relatórios de falha e atividade recente, para nos enviar quando algo der errado."),
            new PadOption(SecondScreenMode.UserOff ? "Segunda tela: DESLIGADA" : "Segunda tela: LIGADA", IconPath: "compact",
                Detail: "DESLIGADA mantém o PKForge em uma tela, deixando a outra livre (para um emulador, por exemplo)."),
            new PadOption(Services.HaXMode.IsOn ? "Modo HaX: LIGADO" : "Modo HaX: DESLIGADO", IconPath: "hax",
                Detail: "LIGADO faz os seletores oferecerem qualquer opção, mesmo as ilegais."),
            new PadOption(Services.HardcoreMode.IsOn ? "Modo Hardcore: LIGADO" : "Modo Hardcore: DESLIGADO", IconPath: "hardcore",
                Detail: "LIGADO bloqueia edições, novos Pokémon e cópias. Mover Pokémon continua funcionando."));
        switch (choice)
        {
            case var ownership when ownership?.StartsWith("Pokémon gerados com o treinador do save:", StringComparison.Ordinal) == true:
                trainerProfiles.SetUseCurrentTrainerForGeneration(!trainerProfiles.UseCurrentTrainerForGeneration);
                _viewModel.Status = trainerProfiles.UseCurrentTrainerForGeneration
                    ? "Pokémon gerados pertencerão ao treinador do save aberto."
                    : "Pokémon gerados podem usar dados de treinador do Auto-Legality.";
                break;
            case var pack when pack?.StartsWith("Baixar pacote completo de sprites", StringComparison.Ordinal) == true:
                await DownloadSpritePackAsync();
                break;
            case "Procurar jogos de novo":
                if (_viewModel.IsBusy && !_viewModel.ReleaseIfStuck())
                {
                    await PadMenu.ShowAsync(_hostGrid, "Ainda ocupado",
                        $"O PKForge ainda está ocupado com {_viewModel.BusyWith}. Tente de novo em instantes; se continuar travado, feche e abra o PKForge de novo.", "OK");
                    break;
                }
                // Called directly: the command refuses to start while an older run it owns is still pending.
                await _viewModel.RescanAsync();
                break;
            case "Compartilhar logs":
                await ShareLogsAsync();
                break;
            case "Segunda tela: LIGADA" or "Segunda tela: DESLIGADA":
            {
                var turnOff = !SecondScreenMode.UserOff;
                SecondScreenMode.SetUserOff(turnOff);
                var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
                try
                {
                    if (turnOff) { if (host is not null) await host.DismissAsync(); }
                    else if (host?.IsAvailable == true) await host.ShowAsync();
                }
                catch (InvalidOperationException error)
                {
                    AppLog.Warn("second", $"Alternando a tela inferior: {error.Message}");
                }
                AppLog.Info("second", turnOff ? "Player turned the second screen off" : "Player turned the second screen on");
                _viewModel.Status = turnOff
                    ? "Segunda tela desligada: o PKForge fica nesta tela."
                    : host?.IsAvailable == true ? "Segunda tela ligada." : "Segunda tela ligada, mas nenhum segundo display está disponível.";
                break;
            }
            case "Relatório da varredura":
            {
                var action = await PadMenu.ShowAsync(_hostGrid, "Relatório da varredura", _viewModel.ScanReport, "Copiar relatório", "Fechar");
                if (action == "Copiar relatório")
                {
                    await Clipboard.Default.SetTextAsync(_viewModel.ScanReport);
                    _viewModel.Status = "Relatório da varredura copiado";
                }
                break;
            }
            case "Modo HaX: DESLIGADO":
                var on = await PadMenu.ConfirmAsync(_hostGrid, "Ligar o modo HaX?",
                    "Os seletores vão oferecer todas as opções em vez de só as legais (qualquer habilidade em qualquer Pokémon). " +
                    "Use por sua conta e risco: Pokémon editados assim aparecerão como ilegais.", "Ligar");
                if (on) { Services.HaXMode.Set(true); _viewModel.Status = "O modo HaX está LIGADO."; }
                break;
            case "Modo HaX: LIGADO":
                Services.HaXMode.Set(false);
                _viewModel.Status = "O modo HaX está DESLIGADO.";
                break;
            case "Modo Hardcore: DESLIGADO":
                if (await PadMenu.ConfirmAsync(_hostGrid, "Ligar o modo Hardcore?", Services.HardcoreMode.SettingExplanation, "Ligar"))
                {
                    Services.HardcoreMode.Set(true);
                    _viewModel.Status = $"{Services.HardcoreMode.Marker}: o modo Hardcore está LIGADO - só mover, sem edições nem cópias.";
                }
                break;
            case "Modo Hardcore: LIGADO":
                Services.HardcoreMode.Set(false);
                _viewModel.Status = "O modo Hardcore está DESLIGADO.";
                break;
        }
    }

    /// <summary>
    /// Zips the logs, crash reports, device details and scan report, then opens the share
    /// sheet. Nothing is sent anywhere unless the player picks where it goes.
    /// </summary>
    private async Task ShareLogsAsync()
    {
        try
        {
            var archive = await Task.Run(() => AppLog.BuildShareArchive(new Dictionary<string, string>
            {
                ["scan-report.txt"] = _viewModel.ScanReport,
            }));
            AppLog.MarkCrashesSeen();
            await Share.Default.RequestAsync(new ShareFileRequest("Logs do PKForge", new ShareFile(archive, "application/zip")));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLog.Error("logs", "Falha ao compartilhar os logs", error);
            await PadMenu.ShowAsync(_hostGrid, "Compartilhar logs", $"Não foi possível empacotar os logs: {error.Message}", "OK");
        }
    }

    private static bool _crashOffered;

    /// <summary>After a crash, offers the report once on the next launch.</summary>
    private async Task OfferCrashReportAsync()
    {
        if (AppLog.UnseenCrashes().Count == 0) return;
        var choice = await PadMenu.ShowAsync(_hostGrid, "O PKForge fechou inesperadamente",
            "Um relatório de falha foi salvo. Compartilhá-lo nos ajuda a corrigir: ele contém o erro, sua atividade recente, nomes de arquivos e o modelo do seu aparelho, nunca os dados do seu save.",
            "Compartilhar relatório", "Agora não");
        AppLog.MarkCrashesSeen();
        if (choice == "Compartilhar relatório") await ShareLogsAsync();
    }

    /// <summary>Downloads every species' animated + HOME sprites for full offline use.</summary>
    private async Task DownloadSpritePackAsync()
    {
        var downloader = IPlatformApplication.Current?.Services.GetService<SpritePackDownloader>();
        if (downloader is null) return;
        if (await Task.Run(downloader.CountMissing) == 0)
        {
            await PadMenu.ShowAsync(_hostGrid, "Pacote de sprites", "Já baixado: todos os sprites estão neste aparelho, então tudo funciona offline.", "OK");
            return;
        }
        var overlay = LoadingOverlay.Show(_hostGrid, "Capturando todos os sprites!",
            "Baixando sprites animados de batalha e imagens do HOME de todos os Pokémon. Você pode cancelar quando quiser; as partes concluídas são mantidas e o download continua de onde parou.");
        try
        {
            await downloader.RunAsync(overlay.Report, overlay.Cancellation.Token);
            _viewModel.Status = "Pacote de sprites completo - agora tudo funciona offline.";
        }
        catch (OperationCanceledException)
        {
            _viewModel.Status = "Pacote de sprites pausado - retome quando quiser nas Configurações.";
        }
        catch (Exception error)
        {
            _viewModel.Status = $"Pacote de sprites interrompido: {error.Message}";
        }
        finally
        {
            overlay.Close();
        }
    }

    private async Task LinkFileAsync()
    {
        await _viewModel.LinkFileCommand.ExecuteAsync(null);
        if (_viewModel.OpenedSave)
            await PushAsync<BoxBrowserPage>();
        else if (_viewModel.Status.StartsWith("Não foi possível", StringComparison.Ordinal))
            await PadMenu.ShowAsync(_hostGrid, "Não foi possível abrir o save", _viewModel.Status, "OK");
    }

    /// <summary>
    /// A game as an actual cartridge: fixed uniform size, dark contact strip on top,
    /// and the game art as the cart's label sticker (era-colored plastic behind it).
    /// </summary>
    private const double TilePadding = 8;
    private const double TileInnerWidth = 128;

    private View BuildCartridgeTile()
    {
        const double cartWidth = 76;
        const double cartHeight = 84;

        var contactStrip = new BoxView { HeightRequest = 9, Color = UiTokens.MaroonDeep };

        var gameMark = new GameCartridgeMark();
        gameMark.SetBinding(GameCartridgeMark.GenerationProperty, nameof(DetectedSave.Generation));

        // Use the real game label art whenever we bundle it. The identity mark remains a
        // deliberate, consistent fallback for homebrew, hacks, and unknown saves.
        var gameArt = new Image
        {
            Aspect = Aspect.AspectFit,
            IsVisible = false,
            Margin = new Thickness(3),
        };
        gameArt.BindingContextChanged += async (_, _) =>
        {
            if (gameArt.BindingContext is not SaveCard group) return;
            gameArt.IsVisible = false;
            gameMark.IsVisible = true;
            // Box art follows the game, never the custom name; unnamed hacks keep the mark.
            var path = group.Save.ArtLabel is { } art ? await GameArt.GetIconAsync(art) : null;
            // A recycled template may have moved on while package I/O was in flight.
            if (!ReferenceEquals(gameArt.BindingContext, group)) return;
            gameArt.Source = path;
            gameArt.IsVisible = path is not null;
            gameMark.IsVisible = path is null;
        };
        var sticker = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.Outline,
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 4 },
            Margin = new Thickness(7, 5, 7, 7),
            Content = new Grid { Children = { gameMark, gameArt } },
        };

        var cartLayout = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            Children = { contactStrip, sticker },
        };
        Grid.SetRow(sticker, 1);

        var cartBody = new Border
        {
            WidthRequest = cartWidth,
            HeightRequest = cartHeight,
            StrokeThickness = 1.5,
            StrokeShape = new RoundRectangle { CornerRadius = 7 },
            HorizontalOptions = LayoutOptions.Center,
            Padding = 0,
            Content = cartLayout,
        };
        // The plastic is the player's chosen color, else the console era's.
        cartBody.SetBinding(BackgroundColorProperty, new Binding(".", converter: CartColor));
        cartBody.SetBinding(Border.StrokeProperty, new Binding(".", converter: CartEdge));

        // Several saves of one game read as a stack of physical carts: up to two plain
        // carts in the same plastic sit askew behind the labelled one (max 3 layers).
        Border StackedCart(double rotation, double dx, double dy, string visibleWhen)
        {
            var back = new Border
            {
                WidthRequest = cartWidth,
                HeightRequest = cartHeight,
                StrokeThickness = 1.5,
                StrokeShape = new RoundRectangle { CornerRadius = 7 },
                HorizontalOptions = LayoutOptions.Center,
                Padding = 0,
                Rotation = rotation,
                TranslationX = dx,
                TranslationY = dy,
                InputTransparent = true,
                Content = new BoxView { HeightRequest = 9, Color = UiTokens.MaroonDeep, VerticalOptions = LayoutOptions.Start },
            };
            back.SetBinding(BackgroundColorProperty, new Binding(".", converter: CartColor));
            back.SetBinding(Border.StrokeProperty, new Binding(".", converter: CartEdge));
            back.SetBinding(IsVisibleProperty, visibleWhen);
            return back;
        }

        var iconHost = new Grid
        {
            WidthRequest = TileInnerWidth,
            HeightRequest = cartHeight,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                StackedCart(-9, -13, 0, nameof(SaveCard.HasManySaves)),
                StackedCart(8, 13, -1, nameof(SaveCard.HasSeveralSaves)),
                cartBody,
            },
        };

        // Every line gets the tile's full inner width so long names end in an ellipsis
        // instead of being clipped on both sides of a centred run.
        Label ShelfLine(double size, Color color, bool bold = false) => new()
        {
            TextColor = color,
            FontSize = size,
            FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1,
            WidthRequest = TileInnerWidth,
            // The pixel font's first glyph overhangs its advance; keep it off the clip edge.
            Padding = new Thickness(3, 0),
        };

        var name = ShelfLine(UiTokens.TextLabel, UiTokens.Ink0);
        name.FontFamily = DsChrome.PixelFont;
        name.SetBinding(Label.TextProperty, nameof(SaveCard.ShelfTitle));

        var trainer = ShelfLine(UiTokens.TextSmall, UiTokens.Ink1);
        trainer.SetBinding(Label.TextProperty, nameof(SaveCard.TrainerLine));

        // Emulator · folder · date: what tells two saves of one game apart at a glance.
        var detail = ShelfLine(12, UiTokens.InkSoft);
        detail.SetBinding(Label.TextProperty, nameof(SaveCard.DetailLine));

        // Every tile is exactly the same size: the shelf must read as a row of carts.
        // A stack also carries a solid "×N" plate on the free right of the carts, fully
        // inside the tile so it never clips.
        var countLabel = new Label
        {
            TextColor = UiTokens.Ink0,
            FontSize = UiTokens.TextSmall,
            FontAttributes = FontAttributes.Bold,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };
        countLabel.SetBinding(Label.TextProperty, nameof(SaveCard.CountBadge));
        var countBadge = new Border
        {
            BackgroundColor = UiTokens.MaroonDeep,
            Stroke = UiTokens.Outline,
            StrokeThickness = 1.5,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            HeightRequest = 22,
            MinimumWidthRequest = 30,
            Padding = new Thickness(5, 0),
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.End,
            Margin = new Thickness(0, 0, 0, 2),
            InputTransparent = true,
            Content = countLabel,
        };
        countBadge.SetBinding(IsVisibleProperty, nameof(SaveCard.HasSeveralSaves));
        iconHost.Children.Add(countBadge);

        var card = Kit.DevicePanel(new VerticalStackLayout
        {
            Spacing = 3,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children = { iconHost, name, trainer, detail },
        }, padding: TilePadding);
        card.WidthRequest = TileInnerWidth + 2 * TilePadding;
        card.HeightRequest = 166;

        card.Triggers.Add(new DataTrigger(typeof(Border))
        {
            Binding = new Binding(nameof(SaveCard.IsSelected)),
            Value = true,
            Setters =
            {
                // The selected look: cobalt body, pale 2 dp edge - not a cyan glow ring.
                new Setter { Property = Border.BackgroundProperty, Value = new SolidColorBrush(UiTokens.SelectFill) },
                new Setter { Property = Border.StrokeProperty, Value = UiTokens.Ink0.WithAlpha(0.85f) },
            },
        });
        void Select(SaveCard group)
        {
            _shelfIndex = _viewModel.Groups.IndexOf(group);
            for (var i = 0; i < _viewModel.Groups.Count; i++)
                _viewModel.Groups[i].IsSelected = i == _shelfIndex;
        }

        // A tap opens the save; a long press (held ~0.5 s) opens its identity menu instead.
        AttachPress(card,
            () =>
            {
                if (card.BindingContext is not SaveCard group) return;
                Select(group);
                _ = OpenCardAsync(group);
            },
            () =>
            {
                if (card.BindingContext is not SaveCard group) return;
                Select(group);
                _ = ShowSaveMenuAsync(group);
            });
        BlockNativeFocus(card);
        return card;
    }

    /// <summary>The Living Dex Autopilot over every save on the shelf and the Bank.</summary>
    private async Task OpenAutopilotAsync()
    {
        var services = IPlatformApplication.Current?.Services;
        var boxes = services?.GetService<BoxBrowserViewModel>();
        var data = services?.GetService<IGameDataService>();
        var sprites = services?.GetService<ISpriteService>();
        if (boxes is null || data is null || sprites is null) return;
        await LivingDexAutopilotPage.ShowAsync(_hostGrid, boxes, data, sprites);
        // Pokémon moved between games: the shelf's counts and the open save follow.
        _viewModel.Status = boxes.Status;
    }

    /// <summary>The events shelf: community collections here, wondercards inside the game.</summary>
    private async Task ShowEventsMenuAsync()
    {
        var choice = await PadMenu.ShowAsync(_hostGrid, "Banco de dados de eventos", null,
            new PadOption($"Boxes da comunidade ({Services.CommunityBoxService.RepoTitle})", IconPath: "community",
                Detail: "Coleções de Pokémon de evento compartilhadas online, para colocar no seu Banco."),
            new PadOption("Cartões Mágicos", IconPath: "events", Detail: "Ficam dentro de cada jogo: abra um jogo, aperte Y e depois Cartões Mágicos."));
        switch (choice)
        {
            case var boxes when boxes?.StartsWith("Boxes da comunidade", StringComparison.Ordinal) == true:
                await CollectionCenter.ShowAsync(_hostGrid);
                break;
            case "Cartões Mágicos":
                await PadMenu.ShowAsync(_hostGrid, "Cartões Mágicos",
                    "Cartões Mágicos dependem do jogo para o qual são entregues. Abra um jogo na estante, aperte o botão Y (Dados do save) e escolha Cartões Mágicos lá.", "OK");
                break;
        }
    }

    private static readonly IValueConverter CartColor = new FuncConverter(card =>
        card is SaveCard c ? SaveColors.For(c.ColorKey, c.Generation) : Kit.EraColor(0));
    private static readonly IValueConverter CartEdge = new FuncConverter(card =>
        (card is SaveCard c ? SaveColors.For(c.ColorKey, c.Generation) : Kit.EraColor(0)).AddLuminosity(-0.15f));

    private sealed class FuncConverter(Func<object?, Color> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => convert(value);
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    private async Task OpenSaveAsync(DetectedSave save)
    {
        if (!await ChooseEditionOnceAsync(save)) return;
        // The pick re-resolves the save: open the version that carries the chosen edition.
        save = _viewModel.Saves.FirstOrDefault(s => s.DocumentId == save.DocumentId) ?? save;
        if (save.RequiresExtraCare)
        {
            var confirmed = await PadMenu.ConfirmAsync(_hostGrid,
                "Armazenamento de console emulado",
                $"{save.GameLabel} fica dentro do armazenamento emulado do {save.Emulator} - o caminho delicado. " +
                "O PKForge faz backup antes de cada gravação, mas feche o emulador primeiro.",
                "Conectar");
            if (!confirmed) return;
        }

        await _viewModel.OpenAsync(save);
        if (_viewModel.OpenedSave)
        {
            // PreviewGame stays: Home's claim is released while the box is in front, and
            // coming back shows the game just left instead of blank branding.
            await PushAsync<BoxBrowserPage>();
        }
        else if (_viewModel.Status.StartsWith("Não foi possível", StringComparison.Ordinal))
            await PadMenu.ShowAsync(_hostGrid, "Não foi possível abrir o save", _viewModel.Status, "OK");
    }

    private async Task PushAsync<TPage>() where TPage : Page
    {
        var services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("MAUI services are unavailable.");
        await Navigation.PushAsync(services.GetRequiredService<TPage>());
    }

    private async Task PushParkAsync()
    {
        if (Interlocked.Exchange(ref _parkNavigationPending, 1) != 0) return;
        try
        {
            var services = IPlatformApplication.Current?.Services
                ?? throw new InvalidOperationException("MAUI services are unavailable.");
            _parkPage ??= services.GetRequiredService<PokeparkPage>();
            await Navigation.PushAsync(_parkPage);
        }
        finally
        {
            Volatile.Write(ref _parkNavigationPending, 0);
        }
    }
}

internal static class ViewBuilderExtensions
{
    public static T Also<T>(this T view, Action<T> configure) where T : View
    {
        configure(view);
        return view;
    }
}
