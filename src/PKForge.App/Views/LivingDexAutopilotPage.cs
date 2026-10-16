using System.Globalization;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using PKForge.Engine;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Living Dex Autopilot. One window reads every save on the shelf plus the Bank, plans
/// one living dex (normal or shiny, species or every form) in the chosen destination with
/// Pokémon the player already owns, dry-runs the whole plan in memory, and applies it:
/// one restore point and one scoped write per touched save, all-or-nothing.
/// <para>A guided flow, like a game's tutorial screens: a first-run explainer ("How it works",
/// re-openable), then 1 · choose where the living dex lives (a card per place with the gain it
/// would get), 2 · review the plan as plain sentences grouped by kind (expandable), 3 · confirm
/// and run with progress, 4 · the result. Lower screen (dual-screen): the route map of game
/// cartridges with Pokémon travelling between them; single-screen devices get the same map as
/// a strip above the list.</para>
/// </summary>
public sealed class LivingDexAutopilotPage : IPadHandler
{
    private const string DestinationKey = "livingdex_destination";
    private const string IntroSeenKey = "livingdex_intro_seen";

    private enum Stage { Intro, Destination, Review, Confirm, Running, Result }

    private enum RowKind { Card, Group, Step, Section, Option, Info }

    /// <summary>A game's badge on a row: its cartridge art, else the generation mark.</summary>
    private sealed record Badge(string? Art, int Generation, string? ColorKey, bool Bank);

    /// <summary>One line of the current step's list; every stage draws the same row language.</summary>
    private sealed record Row(RowKind Kind, string Key, string Title, string Sub)
    {
        public string? Icon { get; init; }
        public Badge? Game { get; init; }
        public LivingDexStep? Step { get; init; }
        public string? Right { get; init; }
        public string? RightSub { get; init; }
        public SKColor RightColor { get; init; } = Pksm.InkSoft;
        public string? RightIcon { get; init; }
        public SKColor SubColor { get; init; } = Pksm.InkSoft;
        public bool Muted { get; init; }
        public int Indent { get; init; }
        public string? Detail { get; init; }
        public (Badge From, string FromText, Badge To, string ToText)? Route { get; init; }
        public Func<Task>? Activate { get; init; }
    }

    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    private readonly IEncounterLookup? _encounters;
    private readonly SecondScreenState? _secondState;
    private readonly SecondScreenClaim? _secondClaim;
    private readonly bool _dualScreen;

    private readonly Label _title;
    private readonly Border[] _steps;
    private readonly Label _lead;
    private readonly Label _status;
    private readonly SKCanvasView _list;
    private readonly Border _listFrame;
    private readonly View _intro;
    private readonly Label _detail;
    private readonly LivingDexRouteMap? _strip;
    private readonly ContentView _hints = new();

    private Stage _stage = Stage.Destination;
    private Stage _beforeIntro = Stage.Destination;
    private LivingDexShelf? _shelf;
    private LivingDexPlan? _plan;
    private LivingDexStaging? _staging;
    private LivingDexRunResult? _run;
    private Dictionary<LivingDexStep, LivingDexStepOutcome> _outcomes = new(ReferenceEqualityComparer.Instance);
    private readonly List<Row> _rows = [];
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<(int, int), string> _guideHints = [];
    private readonly Dictionary<string, (int Before, int After, int Target)> _previews = new(StringComparer.Ordinal);
    private LivingDexOptions? _previewOptions;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _stageCts;
    private CancellationTokenSource? _applyCts;
    private LivingDexOptions _options = new(LivingDexPlanner.BankId);
    private int _cursor;
    private int _top;
    private long _routeSequence;
    private bool _busy;
    private bool _applying;
    private bool _closed;
    private bool _dryRunDone;
    private string _stageLine = "";
    private (int Done, int Total, string Message) _runProgress;

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, IGameDataService data, ISpriteService sprites)
    {
        if (HardcoreMode.IsOn)
        {
            await PadMenu.ShowAsync(host, "Piloto automático da Living Dex",
                "O modo Hardcore está ligado. O Piloto evolui por troca e reorganiza boxes, o que o Hardcore proíbe; " +
                "desligue o Hardcore nas Configurações para usá-lo.", "OK");
            return;
        }
        try
        {
            await new LivingDexAutopilotPage(host, viewModel, data, sprites)._result.Task;
        }
        catch (Exception error)
        {
            viewModel.Status = $"O Piloto automático da Living Dex fechou: {error.Message}";
        }
    }

    private LivingDexAutopilotPage(Grid host, BoxBrowserViewModel viewModel, IGameDataService data, ISpriteService sprites)
    {
        _host = host;
        _viewModel = viewModel;
        _data = data;
        _sprites = sprites;
        var services = IPlatformApplication.Current?.Services;
        _router = services?.GetService<GamepadRouter>();
        _encounters = services?.GetService<IEncounterLookup>();
        _secondState = services?.GetService<SecondScreenState>();
        _dualScreen = services?.GetService<ISecondaryDisplayHost>()?.IsAvailable == true;

        _title = new Label { Text = "Piloto automático da Living Dex", TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextHeading, VerticalTextAlignment = TextAlignment.Center };
        _steps = [Kit.Tab("1 Onde"), Kit.Tab("2 Revisar"), Kit.Tab("3 Aplicar"), Kit.Tab("4 Pronto")];
        foreach (var tab in _steps) tab.Padding = new Thickness(9, 3);
        var stepper = new HorizontalStackLayout { Spacing = 5, HorizontalOptions = LayoutOptions.End };
        foreach (var tab in _steps) stepper.Children.Add(tab);
        _lead = new Label { TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 2 };
        _status = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 };
        _detail = new Label { TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextSmall, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 3, MinimumHeightRequest = 34 };

        _list = new SKCanvasView { EnableTouchEvents = true, VerticalOptions = LayoutOptions.Fill };
        _list.PaintSurface += PaintList;
        _list.Touch += TouchList;
        _listFrame = new Border
        {
            Stroke = UiTokens.ShellEdge, StrokeThickness = 1.5, BackgroundColor = UiTokens.Paper,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Content = _list,
        };
        _intro = BuildIntro();
        _intro.IsVisible = false;

        var header = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)], Children = { _title, stepper } };
        Grid.SetColumn(stepper, 1);

        var rows = new Grid
        {
            RowSpacing = 6,
            // header / lead / status / [strip] / BODY (elastic) / detail / hints.
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto),
                new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
        };
        rows.Add(header, 0, 0);
        rows.Add(_lead, 0, 1);
        rows.Add(_status, 0, 2);
        if (!_dualScreen)
        {
            _strip = new LivingDexRouteMap(sprites, compact: true) { HeightRequest = 92, IsVisible = false };
            rows.Add(_strip, 0, 3);
        }
        rows.Add(new Grid { Children = { _listFrame, _intro } }, 0, 4);
        rows.Add(_detail, 0, 5);
        rows.Add(_hints, 0, 6);

        var window = Kit.DevicePanel(rows, padding: 10);
        window.Margin = new Thickness(24, 12);
        var scrim = new BoxView { Color = UiTokens.Scrim };
        _overlay = new Grid { Children = { scrim, window } };
        _host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, _host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, _host.ColumnDefinitions.Count));
        Kit.AnimateIn(window);

        // The lower screen belongs to the route map while the autopilot is open.
        _secondClaim = _secondState?.Routes.OpenOverlay(SecondScreenOwner.Autopilot);
        _router?.Push(this);
        _ = LoadAsync(firstLoad: true);
    }

    // ── How it works (first run, re-openable) ───────────────────────────────

    private static View BuildIntro()
    {
        static View Line(string icon, string text, string tint = PksmIcons.Cyan)
        {
            var copy = new Label { Text = text, TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody, LineBreakMode = LineBreakMode.WordWrap };
            var line = new Grid
            {
                ColumnDefinitions = [new(new GridLength(30)), new(GridLength.Star)],
                ColumnSpacing = 10,
                Children = { PksmIcons.Icon(icon, 22, tint), copy },
            };
            Grid.SetColumn(copy, 1);
            return line;
        }

        static Label Head(string text) => new()
        {
            Text = text, TextColor = UiTokens.IndigoInk, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextTitle, Margin = new Thickness(0, 2, 0, 0),
        };

        // One screen, two columns: what it does beside what it never does.
        var does = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Head("O que o Piloto faz"),
                Line("move", "Reúne um de cada Pokémon que você já tem, de todos os seus jogos e do Banco, num lugar que você escolher."),
                Line("evolve", "No caminho, evolui os Pokémon extras que só evoluem por troca (como o Kadabra)."),
                Line("map", "Mostra o que falta capturar, e em qual dos seus jogos."),
            },
        };
        var never = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                Head("O que ele nunca faz"),
                Line("copy", "Nunca copia: todo Pokémon é movido, nenhum é duplicado.", PksmIcons.White),
                Line("padlock", "Nunca pega o único exemplar de uma espécie num jogo, nem a sua equipe, a menos que você permita.", PksmIcons.White),
                Line("hardcore", "Nunca mexe em saves Hardcore, de ROM hack ou somente leitura.", PksmIcons.White),
                Line("history", "Cria um ponto de restauração por jogo e não grava nada até você apertar Aplicar.", PksmIcons.White),
            },
        };
        var columns = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)], ColumnSpacing = 18, Children = { does, never } };
        Grid.SetColumn(never, 1);
        var stack = new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(14, 8, 14, 10),
            Children =
            {
                Head("O que é uma Living Dex?"),
                Line("pokedex", "Um de cada Pokémon, cada um no seu espaço da box, na ordem da Pokédex: uma Pokédex que você pode abrir e ver."),
                columns,
            },
        };
        return new Border
        {
            Stroke = UiTokens.ShellEdge, StrokeThickness = 1.5, BackgroundColor = UiTokens.Paper,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Content = new ScrollView { Content = stack },
        };
    }

    private void ShowIntro()
    {
        if (_applying || _stage == Stage.Intro) return;
        _beforeIntro = _stage is Stage.Result or Stage.Running ? Stage.Review : _stage;
        SetStage(Stage.Intro);
    }

    private void CloseIntro()
    {
        Preferences.Default.Set(IntroSeenKey, true);
        SetStage(_plan is null && _beforeIntro != Stage.Destination ? Stage.Destination : _beforeIntro);
    }

    // ── Stages ─────────────────────────────────────────────────────────────

    private void SetStage(Stage stage)
    {
        _stage = stage;
        _cursor = 0;
        _top = 0;
        var index = stage switch { Stage.Destination => 0, Stage.Review => 1, Stage.Confirm or Stage.Running => 2, Stage.Result => 3, _ => -1 };
        for (var i = 0; i < _steps.Length; i++) Kit.SetTab(_steps[i], i == index);
        _intro.IsVisible = stage == Stage.Intro;
        _listFrame.IsVisible = stage != Stage.Intro;
        _detail.IsVisible = stage != Stage.Intro;
        if (_strip is not null) _strip.IsVisible = stage is Stage.Review or Stage.Confirm or Stage.Running or Stage.Result;
        _hints.Content = stage switch
        {
            Stage.Intro => Preferences.Default.Get(IntroSeenKey, false)
                ? Kit.WindowHints(("A", "Entendi", CloseIntro), ("B", "Voltar", CloseIntro))
                : Kit.WindowHints(("A", "Vamos lá", CloseIntro), ("B", "Fechar", Close)),
            Stage.Destination => Kit.WindowHints(("A", "Escolher", () => _ = ActivateAsync()), ("B", "Fechar", Close), ("-", "Como funciona", ShowIntro)),
            Stage.Review => Kit.WindowHints(("A", "Abrir", () => _ = ActivateAsync()), ("B", "Voltar", () => SetStage(Stage.Destination)),
                ("-", "Como funciona", ShowIntro), ("+", "Continuar", () => _ = ContinueAsync())),
            Stage.Confirm => Kit.WindowHints(("B", "Voltar", () => SetStage(Stage.Review)), ("+", "Aplicar", () => _ = ApplyAsync())),
            Stage.Running => Kit.WindowHints(("B", "Cancelar", CancelApply)),
            _ => Kit.WindowHints(("A", "Pronto", () => _ = FinishAsync())),
        };
        if (stage == Stage.Destination) _ = PreviewDestinationsAsync();
        RebuildRows();
        // Step 1 opens on the place the plan uses now.
        if (stage == Stage.Destination && _rows.FindIndex(r => r.Key == $"dest:{_options.DestinationId}") is var current and > 0)
        {
            _cursor = current;
            RefreshDetail();
            _list.InvalidateSurface();
        }
    }

    // ── Loading and planning ───────────────────────────────────────────────

    private async Task LoadAsync(bool firstLoad)
    {
        var loader = LoadingOverlay.Show(_host, "Piloto automático da Living Dex", "Lendo o Banco e todos os jogos da sua estante…");
        _busy = true;
        try
        {
            _shelf = await LivingDexAutopilot.ReadShelfAsync(null, loader.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            loader.Close();
            Close();
            return;
        }
        catch (Exception error)
        {
            loader.Close();
            await PadMenu.ShowAsync(_host, "Piloto automático da Living Dex", $"Não foi possível ler a estante: {error.Message}", "OK");
            Close();
            return;
        }
        finally
        {
            _busy = false;
        }
        loader.Close();
        if (_closed) return;
        _previews.Clear();
        _previewOptions = null;

        // The last destination when it is still usable, else the Bank: it spans every generation.
        var remembered = Preferences.Default.Get(DestinationKey, LivingDexPlanner.BankId);
        var destination = _shelf.Sources.FirstOrDefault(s => s.Id == remembered && !s.IsExcluded)?.Id ?? LivingDexPlanner.BankId;
        if (_shelf.Sources.All(s => s.Id != destination))
            destination = _shelf.Sources.FirstOrDefault(s => !s.IsExcluded)?.Id ?? LivingDexPlanner.BankId;
        _options = _options with { DestinationId = destination };
        if (firstLoad)
        {
            // First run: the explainer; afterwards straight to step 1.
            if (!Preferences.Default.Get(IntroSeenKey, false))
            {
                _beforeIntro = Stage.Destination;
                SetStage(Stage.Intro);
            }
            else SetStage(Stage.Destination);
        }
        else
        {
            await ReplanAsync();
            SetStage(Stage.Review);
        }
    }

    private LivingDexOptions EffectiveOptions(string destinationId) => _options with
    {
        DestinationId = destinationId,
        BankStartBox = destinationId == LivingDexPlanner.BankId ? LivingDexAutopilot.BankStartBox : null,
    };

    /// <summary>Step 1's cards: what each place would gain, planned in the background (pure, no I/O).</summary>
    private async Task PreviewDestinationsAsync()
    {
        if (_shelf is not { } shelf) return;
        var basis = _options with { DestinationId = "" };
        if (_previewOptions == basis && _previews.Count > 0) return;
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        _previews.Clear();
        _previewOptions = basis;
        foreach (var source in shelf.Sources.Where(s => !s.IsExcluded).OrderBy(s => s.Kind == LivingDexSourceKind.Bank ? 0 : 1))
        {
            var options = EffectiveOptions(source.Id);
            LivingDexPlan plan;
            try
            {
                plan = await Task.Run(() => LivingDexPlanner.Plan(shelf.Catalog, shelf.Sources, options), cts.Token);
            }
            catch (Exception)
            {
                continue;
            }
            if (cts.IsCancellationRequested || _closed) return;
            _previews[source.Id] = (plan.CoveredBefore, plan.CoveredAfter, plan.TargetCount);
            if (_stage == Stage.Destination) RebuildRows();
        }
    }

    private async Task ReplanAsync()
    {
        if (_shelf is null || _closed) return;
        _stageCts?.Cancel();
        _staging?.Dispose();
        _staging = null;
        _dryRunDone = false;
        _outcomes = new(ReferenceEqualityComparer.Instance);
        var shelf = _shelf;
        var options = EffectiveOptions(_options.DestinationId);
        _stageLine = "Planejando…";
        RebuildRows();
        LivingDexPlan plan;
        try
        {
            plan = await Task.Run(() => LivingDexPlanner.Plan(shelf.Catalog, shelf.Sources, options));
        }
        catch (Exception error)
        {
            _stageLine = error.Message;
            RebuildRows();
            return;
        }
        if (_closed) return;
        _plan = plan;
        _previews[plan.Options.DestinationId] = (plan.CoveredBefore, plan.CoveredAfter, plan.TargetCount);
        RebuildRows();
        PublishRoute("De onde vêm os seus Pokémon", running: false, done: false, travelers: []);
        _ = DryRunAsync(plan);
    }

    /// <summary>The mandatory dry run: every move staged in memory against fresh reads of the files.</summary>
    private async Task DryRunAsync(LivingDexPlan plan)
    {
        if (_shelf is null) return;
        if (!plan.HasWork)
        {
            _stageLine = plan.Guides > 0 ? "Nada do que você tem pode entrar: o resto precisa ser capturado." : "Esta Living Dex está completa.";
            _dryRunDone = true;
            RebuildRows();
            return;
        }
        var cts = _stageCts = new CancellationTokenSource();
        var executor = LivingDexAutopilot.CreateExecutor();
        var shelf = _shelf;
        var progress = new Progress<LivingDexProgress>(p =>
        {
            if (cts.IsCancellationRequested || _closed) return;
            _stageLine = $"Verificando cada movimento na memória ({p.Done} de {p.Total}). Nada é gravado.";
            if (p.Step is { } step && p.Phase == LivingDexPhase.Staging && step.Kind != LivingDexStepKind.Arrange)
                PublishRoute("Verificando cada movimento (nada é gravado)", running: true, done: false, travelers: [Traveler(step)]);
            RefreshChrome();
        });
        try
        {
            var staging = await Task.Run(() => executor.StageAsync(plan, shelf.Saves, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || _closed || !ReferenceEquals(plan, _plan))
            {
                staging.Dispose();
                return;
            }
            _staging = staging;
            _outcomes = ByStep(staging.StepOutcomes);
            _stageLine = staging.Failed == 0
                ? $"Verificado: todos os {staging.Staged} podem ser movidos com segurança. Nada foi gravado ainda."
                : $"Verificado: {staging.Staged} podem ser movidos, {staging.Failed} não podem (ficam onde estão). Nada foi gravado ainda.";
            PublishRoute("Verificado: aperte + para continuar", running: false, done: false, travelers: []);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception error)
        {
            if (!ReferenceEquals(plan, _plan)) return;
            _stageLine = $"A verificação parou: {error.Message}";
        }
        _dryRunDone = true;
        RebuildRows();
    }

    private static Dictionary<LivingDexStep, LivingDexStepOutcome> ByStep(IEnumerable<LivingDexStepOutcome> outcomes)
    {
        var map = new Dictionary<LivingDexStep, LivingDexStepOutcome>(ReferenceEqualityComparer.Instance);
        foreach (var outcome in outcomes) map[outcome.Step] = outcome;
        return map;
    }

    // ── Rows per stage ──────────────────────────────────────────────────────

    private void RebuildRows()
    {
        var key = _cursor < _rows.Count ? _rows[_cursor].Key : null;
        _rows.Clear();
        switch (_stage)
        {
            case Stage.Destination: BuildDestinationRows(); break;
            case Stage.Review: BuildReviewRows(); break;
            case Stage.Confirm: BuildConfirmRows(); break;
            case Stage.Result: BuildResultRows(); break;
        }
        if (key is not null && _rows.FindIndex(r => r.Key == key) is var kept and >= 0) _cursor = kept;
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, _rows.Count - 1));
        RefreshChrome();
        RefreshDetail();
        _list.InvalidateSurface();
    }

    private Badge BadgeOf(string? id)
    {
        var source = _shelf?.Sources.FirstOrDefault(s => s.Id == id);
        return id == LivingDexPlanner.BankId || source?.Kind == LivingDexSourceKind.Bank
            ? new Badge(null, 0, null, true)
            : new Badge(id is null ? null : _shelf?.ArtLabel(id), source?.Generation ?? 0, id is null ? null : _shelf?.ColorKeys.GetValueOrDefault(id), false);
    }

    /// <summary>" · SOF" (the trainer), so two saves of the same game can be told apart.</summary>
    private string Trainer(string id) => _shelf?.Saves.GetValueOrDefault(id) is { } save
        && !string.IsNullOrWhiteSpace(save.TrainerName) ? $" · {save.TrainerName}" : "";

    private void BuildDestinationRows()
    {
        if (_shelf is null) return;
        foreach (var source in _shelf.Sources.Where(s => !s.IsExcluded).OrderBy(s => s.Kind == LivingDexSourceKind.Bank ? 0 : 1))
        {
            var bank = source.Kind == LivingDexSourceKind.Bank;
            var preview = _previews.TryGetValue(source.Id, out var p) ? p : ((int, int, int)?)null;
            var (right, rightSub, color) = preview switch
            {
                null => ("…", "calculando", Pksm.InkSoft),
                var (before, _, target) when before >= target => ("Completa", $"{target} of {target}", Pksm.Legal),
                var (before, after, target) when after == before => ("+0", $"continua {before} de {target}", Pksm.InkSoft),
                var (before, after, target) => ($"+{after - before}", $"{before} → {after} de {target}", Pksm.Legal),
            };
            var id = source.Id;
            _rows.Add(new Row(RowKind.Card, $"dest:{id}", bank ? "Banco do PKForge" : source.Label,
                bank ? "Boxes da Living Dex na ordem da Pokédex. Cabe toda geração."
                     : $"Jogo da Gen {source.Generation}{Trainer(id)} · {source.FreeSlots?.Count ?? 0} espaços livres nas boxes")
            {
                Game = BadgeOf(id),
                Right = right,
                RightSub = rightSub,
                RightColor = color,
                Detail = bank
                    ? "Seus Pokémon de todos os jogos se reúnem em novas boxes do Banco, ordenados pelo número da Pokédex. Nada se perde se um jogo não puder ter uma espécie."
                    : $"Seus Pokémon se reúnem nos espaços livres das boxes de {source.Label}. Só Pokémon que existem em {source.Label} podem ir para lá.",
                Activate = () => ChooseDestinationAsync(id),
            });
        }
        foreach (var source in _shelf.Sources.Where(s => s.IsExcluded))
        {
            var reason = source.ExclusionReason ?? LivingDexPlanner.Describe(source.Exclusion);
            _rows.Add(new Row(RowKind.Card, $"dest:{source.Id}", source.Label, $"De fora: {reason}")
            {
                Game = BadgeOf(source.Id),
                Muted = true,
                Right = "De fora",
                RightIcon = "padlock",
                Detail = $"{source.Label} fica de fora e nunca é alterado: {reason}",
            });
        }
    }

    private async Task ChooseDestinationAsync(string id)
    {
        if (_applying) return;
        var changed = id != _options.DestinationId || _plan is null;
        _options = _options with { DestinationId = id };
        Preferences.Default.Set(DestinationKey, id);
        SetStage(Stage.Review);
        if (changed) await ReplanAsync();
    }

    private void BuildReviewRows()
    {
        if (_plan is not { } plan) return;
        var groups = LivingDexPlanSummary.Group(plan, s => _outcomes.GetValueOrDefault(s)?.Status == LivingDexStepStatus.Failed);
        foreach (var group in groups)
        {
            var key = $"group:{group.Kind}";
            var open = _expanded.Contains(key);
            _rows.Add(new Row(RowKind.Group, key, group.Title, group.Detail)
            {
                Icon = group.Kind switch
                {
                    LivingDexGroupKind.Move => "move",
                    LivingDexGroupKind.Evolve => "evolve",
                    LivingDexGroupKind.Sort => "sort",
                    LivingDexGroupKind.CannotMove => "warning",
                    _ => "ball",
                },
                Right = open ? "Esconder" : group.Kind == LivingDexGroupKind.Catch ? "Ver onde" : "Mostrar",
                RightColor = Pksm.Indigo,
                Detail = $"{group.Detail}{(group.Detail.EndsWith('.') ? "" : ".")} Aperte A para {(open ? "esconder" : "mostrar")} {(group.Steps.Count == 1 ? "ele" : $"todos os {group.Steps.Count}")}.",
                Activate = () => Toggle(key),
            });
            if (open)
                foreach (var step in group.Steps) _rows.Add(StepRow(step, group.Kind));
        }

        if (plan.Skipped.Count > 0)
        {
            const string key = "skipped";
            var open = _expanded.Contains(key);
            _rows.Add(new Row(RowKind.Group, key, $"{LivingDexPlanSummary.Count(plan.Skipped.Count, "jogo fica", "jogos ficam")} de fora",
                "Saves Hardcore, de ROM hack, ocultos e somente leitura nunca são alterados.")
            {
                Icon = "padlock",
                Right = open ? "Esconder" : "Mostrar",
                RightColor = Pksm.Indigo,
                Activate = () => Toggle(key),
            });
            if (open)
                foreach (var skipped in plan.Skipped)
                    _rows.Add(new Row(RowKind.Info, $"skip:{skipped.Id}", skipped.Label, skipped.Reason) { Game = BadgeOf(skipped.Id), Indent = 1, Muted = true });
        }

        _rows.Add(new Row(RowKind.Section, "options", "Opções", "Cada uma muda o plano na hora. Nada é gravado."));
        _rows.Add(OptionRow("opt:shiny", "Living Dex shiny", _options.Shiny,
            _options.Shiny ? "Só Pokémon shiny contam e são movidos." : "Qualquer Pokémon conta, shiny ou não.", ToggleShiny));
        _rows.Add(OptionRow("opt:forms", "Todas as formas", _options.Forms,
            _options.Forms ? "Um de cada forma (Alolan Vulpix, cada Unown…): mais espaços para preencher." : "Um por espécie; as formas não precisam de espaço próprio.", ToggleForms));
        _rows.Add(OptionRow("opt:last", "Pegar o único exemplar de um jogo", _options.AllowLastCopies,
            _options.AllowLastCopies ? "Um jogo pode ceder o único exemplar que tem de uma espécie." : "Cada jogo mantém pelo menos um de cada espécie que tem.", ToggleLastCopiesAsync));
        _rows.Add(OptionRow("opt:party", "Usar Pokémon da equipe", _options.IncludeParty,
            _options.IncludeParty ? "Os Pokémon da equipe também podem ser movidos." : "Sua equipe fica exatamente como está.", ToggleParty));
        _rows.Add(OptionRow("opt:older", "Enviar para jogos mais antigos", _options.AllowDowngrades,
            _options.AllowDowngrades ? "Os Pokémon podem voltar para um jogo mais antigo; golpes, habilidade ou bola podem mudar para caber."
                                     : "Os Pokémon só vão para o mesmo jogo ou um mais novo.", ToggleDowngrades));
        if (plan.DestinationKind == LivingDexSourceKind.Bank)
        {
            var first = plan.BankStartBox + 1;
            var last = plan.BankStartBox + LivingDexPlanner.BoxesFor(plan.TargetCount);
            _rows.Add(new Row(RowKind.Option, "opt:boxes", "Boxes da Living Dex", $"Boxes {first}–{last} do Banco, na ordem da Pokédex.")
            {
                Icon = "box",
                Right = $"Box {first}",
                RightColor = Pksm.Indigo,
                Detail = $"A Living Dex ocupa as boxes {first} a {last} do Banco. Aperte A para escolher onde ela começa.",
                Activate = PickBankStartAsync,
            });
        }
        _rows.Add(new Row(RowKind.Info, "how", "Como funciona", "O que o Piloto faz, e o que ele nunca faz.")
        {
            Icon = "info",
            Activate = () => { ShowIntro(); return Task.CompletedTask; },
        });
    }

    /// <summary>Lets the player put the Bank living dex region on boxes they already made for it.</summary>
    private async Task PickBankStartAsync()
    {
        if (_plan is not { } plan || plan.Sources.FirstOrDefault(s => s.Id == LivingDexPlanner.BankId) is not { } bank) return;
        var span = LivingDexPlanner.BoxesFor(plan.TargetCount);
        var perBox = bank.Holdings.Where(h => !h.IsParty).GroupBy(h => h.Box).ToDictionary(g => g.Key, g => g.Count());
        const string automatic = "Primeiras boxes vazias";
        var choices = new List<PadOption>
        {
            new(automatic, Detail: $"Box {LivingDexPlanner.FirstEmptyStretch(bank, span) + 1}: nada do que você guarda fica no caminho."),
        };
        for (var box = 0; box <= bank.BoxCount; box++)
        {
            var inside = Enumerable.Range(box, span).Sum(b => perBox.GetValueOrDefault(b));
            var where = $"Boxes {box + 1}–{box + span}";
            choices.Add(new PadOption($"Box {box + 1}", Detail: inside == 0 ? $"{where} estão vazias." : $"{where} têm {inside} Pokémon."));
        }

        var choice = await PadMenu.ShowAsync(_host, "Começar a Living Dex em…",
            "Os Pokémon que já estão nessas boxes e fazem parte da dex são ordenados no espaço deles. Qualquer outro Pokémon fica onde está e mantém o espaço bloqueado.",
            [.. choices]);
        if (choice is null) return;
        LivingDexAutopilot.BankStartBox = choice == automatic ? null : int.Parse(choice.AsSpan(4), CultureInfo.InvariantCulture) - 1;
        await ReplanAsync();
    }

    private static Row OptionRow(string key, string title, bool on, string consequence, Func<Task> toggle) =>
        new(RowKind.Option, key, title, consequence)
        {
            Icon = on ? "ui:checkbox_on.png" : "ui:checkbox_blank.png",
            Right = on ? "Ligado" : "Desligado",
            RightColor = on ? Pksm.Legal : Pksm.InkSoft,
            Detail = $"{title}: {(on ? "ligado" : "desligado")}. {consequence} Aperte A para {(on ? "desligar" : "ligar")}.",
            Activate = toggle,
        };

    private Task Toggle(string key)
    {
        if (!_expanded.Remove(key)) _expanded.Add(key);
        RebuildRows();
        return Task.CompletedTask;
    }

    private string SpeciesName(int species) =>
        (uint)species < (uint)_data.SpeciesNames.Count ? _data.SpeciesNames[species] : $"#{species}";

    private string Label(string? id) => _plan?.Sources.FirstOrDefault(s => s.Id == id)?.Label ?? "?";

    private string Place(string? id) => id == LivingDexPlanner.BankId ? "Banco" : Label(id);

    private string MonName(LivingDexStep step) =>
        $"#{step.Species:000} {SpeciesName(step.Species)}{(step.Form > 0 ? $" (forma {step.Form})" : "")}{(step.Shiny ? " ★" : "")}";

    private Row StepRow(LivingDexStep step, LivingDexGroupKind group)
    {
        var outcome = _outcomes.GetValueOrDefault(step);
        var key = $"step:{step.Kind}:{step.Species}:{step.Form}:{step.Ordinal}";
        if (group == LivingDexGroupKind.Catch)
            return new Row(RowKind.Step, key, MonName(step), _guideHints.GetValueOrDefault((step.Species, step.Form)) ?? "Escolha para ver onde encontrá-lo.")
            {
                Step = step, Indent = 1, Detail = DetailLine(step),
                Activate = () => ShowDetailsAsync(step),
            };
        if (group == LivingDexGroupKind.CannotMove)
            return new Row(RowKind.Step, key, MonName(step),
                $"Fica em {Place(step.SourceId)}: {step.Blocked ?? outcome?.Message ?? "não pode ser movido"}")
            {
                Step = step, Indent = 1, SubColor = Pksm.Illegal, Detail = DetailLine(step),
                Activate = () => ShowDetailsAsync(step),
            };

        var from = step.Holding is { IsParty: true } ? $"{Place(step.SourceId)} · equipe"
            : step.Holding is { } h ? $"{Place(step.SourceId)} · box {h.Box + 1}" : Place(step.SourceId);
        if (step.Kind == LivingDexStepKind.EvolveAndMove) from += $" (como {SpeciesName(step.FromSpecies)})";
        var to = step.Destination is { } d ? $"{Place(_plan!.Options.DestinationId)} · box {d.Box + 1}" : "?";
        var caveats = LivingDexPlanSummary.Caveats(step.Warnings, outcome?.Legal);
        return new Row(RowKind.Step, key, MonName(step), $"{from} → {to}")
        {
            Step = step,
            Indent = 1,
            Route = (BadgeOf(step.SourceId), from, BadgeOf(_plan!.Options.DestinationId), to),
            Right = caveats.Count > 0 ? string.Join(" · ", caveats.Take(2).Select(c => c.Short)) : null,
            RightIcon = caveats.Count > 0 ? "warning" : null,
            RightColor = caveats.Any(c => c.Serious) ? Pksm.Illegal : Pksm.ShinyGold,
            Detail = DetailLine(step),
            Activate = () => ShowDetailsAsync(step),
        };
    }

    private void BuildConfirmRows()
    {
        if (_plan is not { } plan || _staging is not { } staging) return;
        var outgoing = plan.OutgoingBySource;
        var dest = plan.Options.DestinationId;
        _rows.Add(new Row(RowKind.Info, "c:dest", plan.DestinationKind == LivingDexSourceKind.Bank ? "Banco do PKForge" : plan.DestinationLabel,
            $"Recebe {LivingDexPlanSummary.Count(staging.Staged, "Pokémon", "Pokémon")}: {plan.CoveredBefore} → {plan.CoveredBefore + staging.Staged} de {plan.TargetCount}")
        {
            Game = BadgeOf(dest), Right = $"+{staging.Staged}", RightColor = Pksm.Legal,
        });
        foreach (var (id, count) in outgoing.OrderByDescending(p => p.Value))
            _rows.Add(new Row(RowKind.Info, $"c:{id}", id == LivingDexPlanner.BankId ? "Banco do PKForge" : Label(id),
                $"Cede {LivingDexPlanSummary.Count(count, "Pokémon extra", "Pokémon extras")}{Trainer(id)}")
            {
                Game = BadgeOf(id), Right = $"-{count}", RightColor = Pksm.Ink,
            });
        if (plan.Evolutions > 0)
            _rows.Add(new Row(RowKind.Info, "c:evolve", $"{plan.Evolutions} evoluem por troca no caminho", "Um extra evolui primeiro e depois entra.") { Icon = "evolve" });
        if (plan.Arranges > 0)
            _rows.Add(new Row(RowKind.Info, "c:sort", $"{plan.Arranges} que já estão no Banco são ordenados", "Eles vão para o próprio espaço na ordem da Pokédex.") { Icon = "sort" });
        if (staging.Failed > 0)
            _rows.Add(new Row(RowKind.Info, "c:failed", $"{staging.Failed} não podem ser movidos", "Eles ficam exatamente onde estão.") { Icon = "warning" });
        if (staging.IllegalResults > 0)
            _rows.Add(new Row(RowKind.Info, "c:illegal", $"{staging.IllegalResults} seriam sinalizados como ilegais", "O jogo ainda carrega eles normalmente.") { Icon = "warning" });
        if (plan.Downgrades > 0)
            _rows.Add(new Row(RowKind.Info, "c:older", $"{plan.Downgrades} voltam para um jogo mais antigo", "Os golpes, a habilidade ou a bola deles podem mudar para caber.") { Icon = "reverse" });
        _rows.Add(new Row(RowKind.Info, "c:backup", "Um ponto de restauração antes", "Cada jogo ganha o próprio ponto de restauração antes de ser gravado.") { Icon = "history" });
        _rows.Add(new Row(RowKind.Info, "c:all", "Tudo ou nada", "Se algum jogo não puder ser gravado, todos os jogos voltam a ser como eram.") { Icon = "check" });
        var cautions = plan.Sources.Where(s => s.Caution is not null && plan.TouchedSaves.Contains(s.Id)).ToList();
        if (cautions.Count > 0)
            foreach (var s in cautions)
                _rows.Add(new Row(RowKind.Info, $"c:caution:{s.Id}", s.Label, s.Caution!) { Game = BadgeOf(s.Id), RightIcon = "warning", Right = "feche", RightColor = Pksm.ShinyGold });
        else
            _rows.Add(new Row(RowKind.Info, "c:emu", "Feche seus emuladores", "Um jogo aberto num emulador pode salvar por cima das mudanças.") { Icon = "warning" });
    }

    private void BuildResultRows()
    {
        if (_run is not { } run || _plan is not { } plan) return;
        _rows.Add(new Row(RowKind.Info, "r:head", run.Committed ? "Living Dex atualizada" : "Nada mudou", run.Summary)
        {
            Icon = run.Committed ? "check" : "warning",
            Right = run.Committed ? $"{run.CoveredAfter} of {plan.TargetCount}" : null,
            RightColor = Pksm.Legal,
        });
        if (run.Committed)
        {
            var done = run.Outcomes.Where(o => o.Status == LivingDexStepStatus.Done && o.Step.Fills).ToList();
            _rows.Add(new Row(RowKind.Info, "r:dest", plan.DestinationKind == LivingDexSourceKind.Bank ? "Banco do PKForge" : plan.DestinationLabel,
                $"Recebeu {LivingDexPlanSummary.Count(done.Count, "Pokémon", "Pokémon")}")
            {
                Game = BadgeOf(plan.Options.DestinationId), Right = $"+{done.Count}", RightColor = Pksm.Legal,
            });
            foreach (var group in done.Where(o => o.Step.SourceId is not null).GroupBy(o => o.Step.SourceId!).OrderByDescending(g => g.Count()))
                _rows.Add(new Row(RowKind.Info, $"r:{group.Key}", group.Key == LivingDexPlanner.BankId ? "Banco do PKForge" : Label(group.Key),
                    $"Cedeu {LivingDexPlanSummary.Count(group.Count(), "Pokémon", "Pokémon")}")
                {
                    Game = BadgeOf(group.Key), Right = $"-{group.Count()}", RightColor = Pksm.Ink,
                });
        }
        if (run.BackupIds.Count > 0)
            _rows.Add(new Row(RowKind.Info, "r:backup", $"{LivingDexPlanSummary.Count(run.BackupIds.Count, "restore point", "restore points")} made",
                "Início › Pontos de restauração (R) deixa qualquer jogo como era antes.") { Icon = "history" });
        if (plan.Guides > 0)
            _rows.Add(new Row(RowKind.Info, "r:catch", $"Ainda falta capturar: {plan.Guides}", "Abra o Piloto de novo para ver onde encontrar cada um.") { Icon = "ball" });
    }

    // ── Chrome and detail ──────────────────────────────────────────────────

    private void RefreshChrome()
    {
        var mode = _options.Shiny ? "Living Dex shiny" : "Living Dex";
        _title.Text = $"Piloto · {mode}{(_options.Forms ? " · formas" : "")}";
        switch (_stage)
        {
            case Stage.Intro:
                _lead.Text = "Como funciona o Piloto automático da Living Dex";
                _status.Text = Preferences.Default.Get(IntroSeenKey, false)
                    ? "Aperte A para voltar para onde você estava."
                    : "Aperte A para começar. \"Como funciona\" (o botão −) abre isto de novo quando quiser.";
                break;
            case Stage.Destination:
                _lead.Text = "Onde sua Living Dex deve ficar?";
                _status.Text = "O número verde é quantos Pokémon que você já tem poderiam ir para lá.";
                break;
            case Stage.Review:
                _lead.Text = _plan is { } plan ? LivingDexPlanSummary.Headline(plan) : "Planejando…";
                _status.Text = _stageLine;
                break;
            case Stage.Confirm:
                _lead.Text = "Pronto. Aperte + para gravar estas mudanças.";
                _status.Text = "Nada foi gravado ainda. B volta para o plano.";
                break;
            case Stage.Running:
                _lead.Text = "Movendo seus Pokémon…";
                _status.Text = "Mantenha o PKForge aberto. B para depois do jogo atual (o resto volta como era).";
                break;
            case Stage.Result:
                _lead.Text = _run?.Committed == true ? "Pronto! Sua Living Dex foi atualizada." : "Nada foi alterado.";
                _status.Text = "Aperte A para ver o plano atualizado.";
                break;
        }
    }

    private void RefreshDetail()
    {
        if (_stage == Stage.Running)
        {
            _detail.Text = _runProgress.Message;
            return;
        }
        if (_stage == Stage.Intro || _rows.Count == 0)
        {
            _detail.Text = _stage == Stage.Review && _plan is not null ? "Nada para mostrar." : "";
            return;
        }
        var row = _rows[_cursor];
        _detail.Text = row.Detail ?? row.Sub;
        if (_stage == Stage.Destination) PublishShelfRoute(row.Key.StartsWith("dest:", StringComparison.Ordinal) ? row.Key[5..] : null);
        if (row.Step is { Kind: LivingDexStepKind.Guide } step) WarmGuideHint(step);
    }

    private string DetailLine(LivingDexStep step)
    {
        var name = MonName(step);
        if (step.Kind == LivingDexStepKind.Guide)
            return $"{name}: ninguém na sua estante tem um. " + (_guideHints.GetValueOrDefault((step.Species, step.Form)) ?? "Procurando onde capturá-lo…");
        if (step.Blocked is not null) return $"{name} fica onde está: {step.Blocked}";

        var lines = new List<string>();
        var from = step.Holding is { IsParty: true } ? "a equipe" : step.Holding is { } h ? $"box {h.Box + 1}, espaço {h.Slot + 1}" : "?";
        var to = step.Destination is { } d ? $"box {d.Box + 1}, espaço {d.Slot + 1}" : "?";
        var into = Place(_plan?.Options.DestinationId);
        lines.Add(step.Kind switch
        {
            LivingDexStepKind.EvolveAndMove => $"{name}: um {SpeciesName(step.FromSpecies)} extra em {Place(step.SourceId)} ({from}) evolui por troca e depois vai para {into} ({to}).",
            LivingDexStepKind.Arrange => $"{name}: já está no Banco; vai para o próprio espaço ({to}).",
            _ => $"{name}: vai de {Place(step.SourceId)} ({from}) para {into} ({to}).",
        });
        var outcome = _outcomes.GetValueOrDefault(step);
        if (outcome?.Status == LivingDexStepStatus.Failed) lines.Add($"A verificação diz que ele não pode ser movido: {outcome.Message} Ele fica onde está.");
        lines.AddRange(LivingDexPlanSummary.Caveats(step.Warnings, outcome?.Legal).Select(c => c.Long));
        if (outcome is { Status: not LivingDexStepStatus.Failed }) lines.AddRange(outcome.Warnings.Take(2));
        return string.Join(" ", lines);
    }

    /// <summary>Where to catch it, from the offline encounter data, in the player's own games first.</summary>
    private void WarmGuideHint(LivingDexStep step)
    {
        var key = (step.Species, step.Form);
        if (_guideHints.ContainsKey(key) || _encounters is null || _shelf is null) return;
        _guideHints[key] = "Procurando onde capturá-lo…";
        var owned = _shelf.Saves.Values.Select(s => Plain(s.Identity?.GameLabel ?? s.GameLabel)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() =>
        {
            string hint;
            try
            {
                var games = _encounters.Describe(step.Species, step.Form).Where(g => g.Obtainable).Select(g => Plain(g.GameName)).Distinct().ToList();
                // Whole names only: "Red" is not "FireRed", "Gold" is not "HeartGold".
                var mine = games.Where(g => g.Split(['/', ',', '&'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => owned.Contains(part))).ToList();
                hint = mine.Count > 0 ? $"Capture em {string.Join(", ", mine.Take(4))}."
                    : games.Count > 0 ? $"Não está nos seus jogos; aparece em {string.Join(", ", games.Take(3))}{(games.Count > 3 ? "…" : "")}."
                    : "Nenhum encontro selvagem ou de presente é conhecido (evento ou troca).";
            }
            catch (Exception)
            {
                hint = "Escolha para ver onde conseguir.";
            }
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _guideHints[key] = hint;
                if (_closed || _stage != Stage.Review) return;
                RebuildRows();
            });
        });
    }

    private static string Plain(string game) => LivingDexAutopilot.CartridgeName(game);

    // ── The list canvas ────────────────────────────────────────────────────

    private float RowHeight(SKImageInfo info) => _stage == Stage.Destination
        ? Math.Max(60, info.Height / 4.6f)
        : Math.Max(44, info.Height / 6.6f);

    private int VisibleRows(SKImageInfo info) => Math.Max(1, (int)(info.Height / RowHeight(info)));

    private void PaintList(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        var info = args.Info;
        c.Clear(Pksm.Paper);
        var rowH = RowHeight(info);
        var unit = Math.Max(44, info.Height / 6.6f) / 48f;
        using var font = new SKFont(PixelFont.Face, 15 * unit) { Edging = SKFontEdging.Antialias };
        using var small = new SKFont(PixelFont.Face, 12 * unit) { Edging = SKFontEdging.Antialias };
        using var big = new SKFont(PixelFont.Face, 22 * unit) { Edging = SKFontEdging.Antialias, Embolden = true };

        if (_stage == Stage.Running)
        {
            PaintRunning(c, info, unit, font, small);
            return;
        }
        var visible = VisibleRows(info);
        if (_cursor < _top) _top = _cursor;
        if (_cursor >= _top + visible) _top = _cursor - visible + 1;
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - visible));

        if (_rows.Count == 0)
        {
            PksmPaint.CenterText(c, _plan is null || _shelf is null ? "Planejando…" : "Nada para mostrar.", info.Width / 2f, info.Height / 2f, font, Pksm.InkSoft, SKColors.Transparent, SKTextAlign.Center);
            return;
        }

        for (var i = 0; i < visible + 1 && _top + i < _rows.Count; i++)
        {
            var index = _top + i;
            var row = _rows[index];
            if (row.Step is { Kind: LivingDexStepKind.Guide } guide) WarmGuideHint(guide);
            var r = new SKRect(0, i * rowH, info.Width, (i + 1) * rowH);
            PaintRow(c, r, row, index == _cursor, unit, font, small, big);
        }

        // Scroll position, PKSM-style: a thin bar on the right edge.
        if (_rows.Count > visible)
        {
            var track = info.Height - 8f;
            var length = Math.Max(18, track * visible / _rows.Count);
            var offset = 4 + (track - length) * _top / Math.Max(1, _rows.Count - visible);
            using var bar = new SKPaint { Color = Pksm.SelectBorder, IsAntialias = true };
            c.DrawRoundRect(new SKRect(info.Width - 5, offset, info.Width - 2, offset + length), 1.5f, 1.5f, bar);
        }
    }

    private void PaintRow(SKCanvas c, SKRect r, Row row, bool selected, float unit, SKFont font, SKFont small, SKFont big)
    {
        if (row.Kind == RowKind.Section)
        {
            using var band = new SKPaint { Color = Pksm.PaperShade, IsAntialias = true };
            c.DrawRect(r, band);
            PksmPaint.CenterText(c, row.Title, r.Left + 12 * unit, r.MidY - small.Size * 0.55f, font, Pksm.Indigo, SKColors.Transparent);
            PksmPaint.CenterText(c, Fit(row.Sub, small, r.Width - 24 * unit), r.Left + 12 * unit, r.MidY + small.Size * 0.75f, small, Pksm.InkSoft, SKColors.Transparent);
            if (selected) PksmPaint.Selection(c, SKRect.Inflate(r, -2, -2));
            return;
        }
        PksmPaint.StripeRow(c, r, selected);
        var alpha = (byte)(row.Muted ? 0x88 : 0xFF);
        var pad = 6 * unit;
        var x = r.Left + 8 * unit + row.Indent * 18 * unit;

        // The visual: a Pokémon, a game, or a plain icon.
        var size = row.Kind == RowKind.Card ? r.Height - 14 * unit : r.Height - 12 * unit;
        var box = new SKRect(x, r.MidY - size / 2, x + size, r.MidY + size / 2);
        if (row.Step is { } step) DrawMon(c, box, step);
        else if (row.Game is { } game) AutopilotArt.DrawGame(c, box, game.Art, game.Generation, game.ColorKey, game.Bank, _list.InvalidateSurface, alpha);
        else if (row.Icon is { } icon)
        {
            var inset = size * 0.18f;
            AutopilotArt.DrawIcon(c, icon, SKRect.Inflate(box, -inset, -inset), icon.StartsWith("ui:", StringComparison.Ordinal) ? PksmIcons.Native : PksmIcons.Cyan, alpha);
        }
        var textX = box.Right + 10 * unit;

        // Right column: the result / state / caveat, never a pill.
        var right = r.Right - 12 * unit;
        if (row.Right is { } text)
        {
            var rightFont = row.Kind == RowKind.Card && !row.Muted ? big : font;
            var width = rightFont.MeasureText(text);
            if (row.RightSub is { } sub)
            {
                var subWidth = small.MeasureText(sub);
                PksmPaint.CenterText(c, text, right, r.MidY - small.Size * 0.6f, rightFont, row.RightColor.WithAlpha(alpha), SKColors.Transparent, SKTextAlign.Right);
                PksmPaint.CenterText(c, sub, right, r.MidY + rightFont.Size * 0.62f, small, Pksm.InkSoft.WithAlpha(alpha), SKColors.Transparent, SKTextAlign.Right);
                width = Math.Max(width, subWidth);
            }
            else
            {
                var textFont = row.Kind == RowKind.Step ? small : rightFont;
                width = textFont.MeasureText(text);
                PksmPaint.CenterText(c, text, right, r.MidY, textFont, row.RightColor.WithAlpha(alpha), SKColors.Transparent, SKTextAlign.Right);
            }
            right -= width + 6 * unit;
        }
        if (row.RightIcon is { } rightIcon)
        {
            var iconSize = 16 * unit;
            AutopilotArt.DrawIcon(c, rightIcon, new SKRect(right - iconSize, r.MidY - iconSize / 2, right, r.MidY + iconSize / 2), PksmIcons.White, alpha);
            right -= iconSize + 8 * unit;
        }

        var room = right - textX - pad;
        PksmPaint.CenterText(c, Fit(row.Title, font, room), textX, r.MidY - small.Size * 0.62f, font, Pksm.Ink.WithAlpha(alpha), SKColors.Transparent);
        var subY = r.MidY + small.Size * 0.78f;
        if (row.Route is { } route)
            DrawRoute(c, textX, subY, room, route, small, unit);
        else
            PksmPaint.CenterText(c, Fit(row.Sub, small, room), textX, subY, small, row.SubColor.WithAlpha(alpha), SKColors.Transparent);
    }

    /// <summary>"[game] HeartGold · box 3 → [Bank] Bank · box 12": both ends with their cartridge art.</summary>
    private void DrawRoute(SKCanvas c, float x, float y, float room, (Badge From, string FromText, Badge To, string ToText) route, SKFont small, float unit)
    {
        var badge = small.Size * 1.35f;
        var arrow = "  →  ";
        var arrowWidth = small.MeasureText(arrow);
        var half = (room - 2 * (badge + 4 * unit) - arrowWidth) / 2;
        if (half < 20 * unit)
        {
            PksmPaint.CenterText(c, Fit($"{route.FromText} → {route.ToText}", small, room), x, y, small, Pksm.InkSoft, SKColors.Transparent);
            return;
        }
        void End(Badge b, string text, ref float left)
        {
            AutopilotArt.DrawGame(c, new SKRect(left, y - badge / 2, left + badge, y + badge / 2), b.Art, b.Generation, b.ColorKey, b.Bank, _list.InvalidateSurface);
            left += badge + 4 * unit;
            var fitted = Fit(text, small, half);
            PksmPaint.CenterText(c, fitted, left, y, small, Pksm.InkSoft, SKColors.Transparent);
            left += small.MeasureText(fitted);
        }
        var cursor = x;
        End(route.From, route.FromText, ref cursor);
        PksmPaint.CenterText(c, arrow, cursor, y, small, Pksm.Indigo, SKColors.Transparent);
        cursor += arrowWidth;
        End(route.To, route.ToText, ref cursor);
    }

    private void DrawMon(SKCanvas c, SKRect box, LivingDexStep step)
    {
        var sprite = _sprites.GetSprite(step.Species, step.Form, step.Shiny);
        if (sprite is null)
        {
            _sprites.Warm(step.Species, step.Form, step.Shiny, () => MainThread.BeginInvokeOnMainThread(_list.InvalidateSurface));
            return;
        }
        var scale = Math.Min(box.Width / sprite.Width, box.Height / sprite.Height);
        var w = sprite.Width * scale;
        var h = sprite.Height * scale;
        using var image = SKImage.FromBitmap(sprite);
        var dest = new SKRect(box.MidX - w / 2, box.MidY - h / 2, box.MidX + w / 2, box.MidY + h / 2);
        if (step.Kind == LivingDexStepKind.Guide)
        {
            // Not owned yet: the silhouette, like an unseen Pokédex entry.
            using var silhouette = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(Pksm.LogoVoid, SKBlendMode.SrcIn) };
            c.DrawImage(image, dest, BoxGridRenderer.SpriteSampling, silhouette);
        }
        else c.DrawImage(image, dest, BoxGridRenderer.SpriteSampling);
    }

    private void PaintRunning(SKCanvas c, SKImageInfo info, float unit, SKFont font, SKFont small)
    {
        var (done, total, message) = _runProgress;
        var bar = new SKRect(24 * unit, info.Height / 2f - 10 * unit, info.Width - 24 * unit, info.Height / 2f + 10 * unit);
        using (var track = new SKPaint { Color = Pksm.PaperShade, IsAntialias = true })
            c.DrawRoundRect(bar, bar.Height / 2, bar.Height / 2, track);
        var t = total > 0 ? Math.Clamp(done / (float)total, 0, 1) : 0;
        if (t > 0)
        {
            using var fill = new SKPaint { Color = Pksm.Legal, IsAntialias = true };
            c.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + Math.Max(bar.Height, bar.Width * t), bar.Bottom), bar.Height / 2, bar.Height / 2, fill);
        }
        PksmPaint.CenterText(c, Fit(message, font, bar.Width), info.Width / 2f, bar.Top - 22 * unit, font, Pksm.Ink, SKColors.Transparent, SKTextAlign.Center);
        PksmPaint.CenterText(c, total > 0 ? $"{done} of {total}" : "Começando…", info.Width / 2f, bar.Bottom + 20 * unit, small, Pksm.InkSoft, SKColors.Transparent, SKTextAlign.Center);
    }

    private static string Fit(string text, SKFont font, float width)
    {
        if (width <= 0) return "";
        if (font.MeasureText(text) <= width) return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }

    private bool _pressed;

    private void TouchList(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { _pressed = true; args.Handled = true; return; }
        // A release without its press is the tail of the tap that opened the window.
        if (args.ActionType != SKTouchAction.Released || !_pressed || _rows.Count == 0 || _stage == Stage.Running) return;
        _pressed = false;
        args.Handled = true;
        var info = new SKImageInfo((int)_list.CanvasSize.Width, (int)_list.CanvasSize.Height);
        var index = _top + (int)(args.Location.Y / RowHeight(info));
        if (index < 0 || index >= _rows.Count) return;
        if (index == _cursor) { _ = ActivateAsync(); return; }
        _cursor = index;
        RefreshDetail();
        _list.InvalidateSurface();
    }

    private void MoveCursor(int delta)
    {
        if (_rows.Count == 0) return;
        _cursor = Math.Clamp(_cursor + delta, 0, _rows.Count - 1);
        RefreshDetail();
        _list.InvalidateSurface();
    }

    private async Task ActivateAsync()
    {
        if (_applying || _busy || _rows.Count == 0 || _stage is Stage.Intro or Stage.Running) return;
        if (_rows[_cursor].Activate is { } activate) await activate();
    }

    // ── Options ────────────────────────────────────────────────────────────

    private Task ToggleShiny()
    {
        _options = _options with { Shiny = !_options.Shiny };
        return ReplanAsync();
    }

    private Task ToggleForms()
    {
        _options = _options with { Forms = !_options.Forms };
        return ReplanAsync();
    }

    private Task ToggleParty()
    {
        _options = _options with { IncludeParty = !_options.IncludeParty };
        return ReplanAsync();
    }

    private Task ToggleDowngrades()
    {
        _options = _options with { AllowDowngrades = !_options.AllowDowngrades };
        return ReplanAsync();
    }

    private async Task ToggleLastCopiesAsync()
    {
        if (!_options.AllowLastCopies && !await PadMenu.ConfirmAsync(_host, "Pegar o único exemplar de um jogo?",
                "Assim, um jogo pode ceder o único Pokémon de uma espécie que tem. Ele fica seguro na sua Living Dex, e todo jogo ganha um ponto de restauração.", "Permitir"))
            return;
        _options = _options with { AllowLastCopies = !_options.AllowLastCopies };
        await ReplanAsync();
    }

    private async Task ShowDetailsAsync(LivingDexStep step)
    {
        if (_applying) return;
        if (step.Kind == LivingDexStepKind.Guide)
        {
            // The same "How to get" answer the Collection dex gives, for every game.
            var session = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>()?.CurrentSession;
            await EncounterGallery.ShowAllGamesForSpeciesAsync(_host, _viewModel, session, step.Species, () => { });
            return;
        }
        await PadMenu.ShowAsync(_host, MonName(step), DetailLine(step), "OK");
    }

    // ── Confirm, apply, result ─────────────────────────────────────────────

    /// <summary>Review → Confirm, once the check has staged the plan.</summary>
    private async Task ContinueAsync()
    {
        if (_applying || _busy || _stage != Stage.Review || _plan is not { } plan) return;
        if (!plan.HasWork)
        {
            await PadMenu.ShowAsync(_host, "Nada para mover", "Nada do que você tem pode entrar agora: o resto precisa ser capturado.", "OK");
            return;
        }
        if (_staging is not { } staging || !ReferenceEquals(staging.Plan, plan))
        {
            await PadMenu.ShowAsync(_host, "Um momento", _dryRunDone
                ? _stageLine : "O Piloto ainda está verificando cada movimento. Tente de novo em instantes.", "OK");
            return;
        }
        if (staging.Staged == 0 && plan.Arranges == 0)
        {
            await PadMenu.ShowAsync(_host, "Nada pode ser movido", "A verificação não conseguiu preparar nenhum movimento; abra \"não podem ser movidos\" para ver o motivo.", "OK");
            return;
        }
        SetStage(Stage.Confirm);
    }

    private async Task ApplyAsync()
    {
        if (_applying || _busy || _stage != Stage.Confirm || _plan is not { } plan || _shelf is null) return;
        if (_staging is not { } staging || !ReferenceEquals(staging.Plan, plan)) { SetStage(Stage.Review); return; }

        _applying = true;
        _runProgress = (0, 0, "Começando…");
        SetStage(Stage.Running);
        var cts = _applyCts = new CancellationTokenSource();
        var executor = LivingDexAutopilot.CreateExecutor();
        var bySource = plan.Steps.Where(s => s.Fills).GroupBy(s => s.SourceId ?? "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var progress = new Progress<LivingDexProgress>(p =>
        {
            _runProgress = (p.Done, p.Total, p.Message);
            // Each save's Pokémon take off when that save is written (the destination's
            // write carries the Bank's; the Bank's step carries a Bank destination's).
            IReadOnlyList<RouteTraveler> travelers = [];
            if (p.Phase == LivingDexPhase.Writing && p.SaveId is { } id)
            {
                var ids = id == plan.Options.DestinationId ? new[] { id, LivingDexPlanner.BankId } : [id];
                travelers = [.. ids.SelectMany(k => bySource.GetValueOrDefault(k) ?? []).Where(s => _outcomes.GetValueOrDefault(s)?.Status != LivingDexStepStatus.Failed).Select(Traveler)];
                if (id == LivingDexPlanner.BankId && plan.DestinationKind == LivingDexSourceKind.Bank) travelers = [];
            }
            PublishRoute(p.Message, running: p.Phase != LivingDexPhase.Finished, done: false, travelers);
            RefreshDetail();
            _list.InvalidateSurface();
        });

        LivingDexRunResult result;
        try
        {
            result = await Task.Run(() => executor.CommitAsync(staging, progress, cts.Token));
        }
        catch (Exception error)
        {
            result = new LivingDexRunResult(false, false, $"Interrompido: {error.Message}", [], new Dictionary<string, string>(), plan.CoveredBefore, plan.CoveredBefore, error.Message);
        }
        _applying = false;
        _applyCts = null;
        _outcomes = ByStep(result.Outcomes);
        _run = result;
        PublishRoute(result.Committed ? "Pronto!" : "Nada mudou", running: false, done: result.Committed, travelers: []);

        if (result.Committed && plan.DestinationKind == LivingDexSourceKind.Bank && LivingDexAutopilot.BankStartBox is null)
            LivingDexAutopilot.BankStartBox = plan.BankStartBox;
        await RefreshOpenSaveAsync(plan);
        _viewModel.Status = result.Summary;
        SetStage(Stage.Result);
    }

    /// <summary>Result → the updated plan: re-read the shelf, the next plan starts from what the files now hold.</summary>
    private async Task FinishAsync()
    {
        if (_stage != Stage.Result || _busy) return;
        _staging?.Dispose();
        _staging = null;
        _run = null;
        await LoadAsync(firstLoad: false);
    }

    /// <summary>A touched save that is open in PKForge is reopened, so its boxes show the new truth.
    /// If that fails the save is closed: the old in-memory session must never be written back
    /// over the file, which would undo (or duplicate) what Autopilot moved.</summary>
    private async Task RefreshOpenSaveAsync(LivingDexPlan plan)
    {
        var sessions = IPlatformApplication.Current?.Services.GetService<ISaveSessionService>();
        if (sessions?.Current is not { } current || !plan.TouchedSaves.Contains(current.Document.DocumentId)) return;
        try
        {
            await sessions.OpenAsync(current.Document);
            _viewModel.RefreshFromCurrentSession();
        }
        catch (Exception error)
        {
            sessions.Close();
            _viewModel.Disconnect();
            _viewModel.Status = $"Abra o jogo de novo para ver as mudanças ({error.Message}).";
        }
    }

    private void CancelApply()
    {
        if (!_applying) return;
        _applyCts?.Cancel();
        _runProgress = (_runProgress.Done, _runProgress.Total, "Parando depois do jogo atual…");
        RefreshDetail();
        _list.InvalidateSurface();
    }

    // ── The route map (lower screen / strip) ──────────────────────────────

    private static RouteTraveler Traveler(LivingDexStep step) =>
        new(step.Species, step.Form, step.Shiny, step.SourceId ?? "", "", step.Kind == LivingDexStepKind.EvolveAndMove);

    private void PublishRoute(string caption, bool running, bool done, IReadOnlyList<RouteTraveler> travelers)
    {
        if (_plan is null || _shelf is null || _closed) return;
        var dest = _plan.Options.DestinationId;
        var carts = LivingDexAutopilot.Carts(_plan, _shelf);
        var route = new LivingDexRoute(carts, dest, caption,
            $"{(_plan.DestinationKind == LivingDexSourceKind.Bank ? "Banco" : _plan.DestinationLabel)}: {_plan.CoveredBefore} → {_plan.CoveredAfter} de {_plan.TargetCount}",
            [.. travelers.Select(t => t with { ToId = dest })], ++_routeSequence, running, done);
        _strip?.Show(route);
        if (_secondState is not null) _secondState.AutopilotRoute = route;
    }

    /// <summary>Step 1 on the lower screen: every cartridge on the shelf, the highlighted place in front with its gain.</summary>
    private void PublishShelfRoute(string? focusId)
    {
        if (_shelf is null || _closed || focusId is null) return;
        var focus = _shelf.Sources.FirstOrDefault(s => s.Id == focusId);
        if (focus is null) return;
        var preview = _previews.TryGetValue(focusId, out var p) ? p : ((int, int, int)?)null;
        var carts = _shelf.Sources
            .Select(s => new RouteCart(s.Id, s.Label, s.Generation, _shelf.ColorKeys.GetValueOrDefault(s.Id), 0,
                s.Id == focusId && preview is var (before, after, _) ? after - before : 0, s.IsExcluded,
                s.Kind == LivingDexSourceKind.Bank, _shelf.ArtLabel(s.Id)))
            .OrderBy(c => c.Id == focusId ? 0 : c.Excluded ? 2 : 1)
            .ToList();
        var name = focus.Kind == LivingDexSourceKind.Bank ? "Banco" : focus.Label;
        var progress = preview is var (b, a, t) ? $"{name}: {b} → {a} de {t}" : $"{name}: calculando…";
        var route = new LivingDexRoute(carts, focusId, "Onde sua Living Dex deve ficar?", progress, [], ++_routeSequence, false, false);
        _strip?.Show(route);
        if (_secondState is not null) _secondState.AutopilotRoute = route;
    }

    // ── Pad ────────────────────────────────────────────────────────────────

    public bool OnPadButton(PadButton button)
    {
        if (_applying)
        {
            if (button == PadButton.B) CancelApply();
            return true; // nothing else while saves are being written
        }
        if (_stage == Stage.Intro)
        {
            if (button == PadButton.A || button == PadButton.Start) CloseIntro();
            else if (button == PadButton.B) { if (Preferences.Default.Get(IntroSeenKey, false)) CloseIntro(); else Close(); }
            return true;
        }
        switch (button)
        {
            case PadButton.Up: MoveCursor(-1); return true;
            case PadButton.Down: MoveCursor(1); return true;
            case PadButton.Left: MoveCursor(-5); return true;
            case PadButton.Right: MoveCursor(5); return true;
            case PadButton.A:
                if (_stage == Stage.Result) _ = FinishAsync();
                else if (_stage != Stage.Confirm) _ = ActivateAsync();
                return true;
            case PadButton.B:
                switch (_stage)
                {
                    case Stage.Destination: Close(); break;
                    case Stage.Review: SetStage(Stage.Destination); break;
                    case Stage.Confirm: SetStage(Stage.Review); break;
                    case Stage.Result: _ = FinishAsync(); break;
                }
                return true;
            case PadButton.Select: ShowIntro(); return true;
            case PadButton.Start:
                if (_stage == Stage.Review) _ = ContinueAsync();
                else if (_stage == Stage.Confirm) _ = ApplyAsync();
                return true;
            default: return true;
        }
    }

    private void Close()
    {
        if (_applying || _closed) return;
        _closed = true;
        _stageCts?.Cancel();
        _previewCts?.Cancel();
        _staging?.Dispose();
        _staging = null;
        _router?.Remove(this);
        _secondClaim?.Release();
        if (_secondState is not null) _secondState.AutopilotRoute = null;
        _host.Remove(_overlay);
        _result.TrySetResult(true);
    }
}
