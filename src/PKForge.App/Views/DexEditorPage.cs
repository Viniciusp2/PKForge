using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The dex as one scrolling list (<see cref="DexGridView"/>): every species in national order,
/// a section per generation. A (or a tap) cycles unseen, seen, caught; L/R hop a generation;
/// touch scrolls it like a real Pokédex. Edits are staged and saved through the safe write
/// path on exit.
/// </summary>
public sealed class DexEditorPage : IPadPagingHandler
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISaveEngineSession _session;
    private readonly ILegalizerService _legalizer;
    private readonly IGameDataService _data;
    private readonly ISpriteService _sprites;
    private readonly Dictionary<int, (bool Seen, bool Caught)> _states = [];
    private readonly Dictionary<int, (bool Seen, bool Caught)> _staged = [];
    private readonly HashSet<int> _fillSelection = [];
    private readonly List<int> _missing = [];
    private readonly List<int> _orderedIds = [];
    private readonly List<int> _viewIds = [];
    private readonly DexGridView _grid;
    private readonly SecondScreenState? _secondState;
    private readonly SecondScreenClaim? _secondClaim;
    private readonly Label _title;
    private readonly Label _progress;
    private readonly Label _cursorInfo;
    private bool _gapsMode;
    private bool _loaded;
    private string _query = "";

    public static async Task ShowAsync(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session,
        ILegalizerService legalizer, IGameDataService data, ISpriteService sprites)
    {
        try
        {
            await new DexEditorPage(host, viewModel, session, legalizer, data, sprites)._result.Task;
        }
        catch (Exception error)
        {
            viewModel.Status = $"O editor da dex fechou: {error.Message}";
        }
    }

    private DexEditorPage(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session,
        ILegalizerService legalizer, IGameDataService data, ISpriteService sprites)
    {
        _host = host;
        _viewModel = viewModel;
        _session = session;
        _legalizer = legalizer;
        _data = data;
        _sprites = sprites;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        _title = new Label { Text = "Pokédex", TextColor = UiTokens.Ink0, FontFamily = DsChrome.PixelFont, FontSize = 15 };
        _progress = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody, HorizontalTextAlignment = TextAlignment.End, HorizontalOptions = LayoutOptions.End };
        _cursorInfo = new Label { TextColor = UiTokens.Ink1, FontFamily = DsChrome.PixelFont, FontSize = UiTokens.TextBody };

        _grid = new DexGridView(sprites) { VerticalOptions = LayoutOptions.Fill };
        _grid.Tapped += index => Activate(_viewIds[index]);
        // The second screen shows the Pokédex page of the species under the cursor.
        _secondState = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        _secondClaim = _secondState?.Routes.OpenOverlay(SecondScreenOwner.Pokedex);
        _grid.CursorChanged += index =>
        {
            RefreshInfo();
            if (_secondState is not null && index < _viewIds.Count) _secondState.PreviewSpecies = _viewIds[index];
        };

        View hints = Kit.WindowHints(
            ("A", "Alternar", () => OnPadButton(PadButton.A)),
            ("B", "Pronto", () => _ = CloseAsync()),
            ("LR", "Geração", () => OnPadButton(PadButton.R)),
            ("Y", "Selecionar todos (faltando)", SelectAllGaps),
            ("X", "Ações", () => _ = ShowActionsAsync()));

        var search = new Entry
        {
            Placeholder = "Buscar um Pokémon…",
            FontSize = 14,
            TextColor = UiTokens.Ink0,
            PlaceholderColor = UiTokens.Ink1,
            BackgroundColor = UiTokens.ShellPress,
            HeightRequest = 36,
            Margin = new Thickness(4, 0),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
        };
        search.TextChanged += (_, args) =>
        {
            _query = args.NewTextValue ?? "";
            RefreshView();
        };

        var content = new Grid
        {
            RowSpacing = 6,
            // header / search / SPRITE GRID (the only elastic row) / cursor line / hints.
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children =
            {
                    new Grid
                    {
                        // The title keeps its width; the counter takes the rest, so they never overlap.
                        ColumnSpacing = 16,
                        ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
                        Children = { _title, _progress },
                    },
                    search,
                    _grid,
                _cursorInfo,
                hints,
            },
        };
        Grid.SetColumn(_progress, 1);
        Grid.SetRow(search, 1);
        Grid.SetRow(_grid, 2);
        Grid.SetRow(_cursorInfo, 3);
        Grid.SetRow(hints, 4);

        var window = Kit.DevicePanel(content, padding: 10);
        window.Margin = new Thickness(24, 12);
        var scrim = new BoxView { Color = UiTokens.Scrim };
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => _ = CloseAsync();
        scrim.GestureRecognizers.Add(scrimTap);
        _overlay = new Grid { Children = { scrim, window } };
        _host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, _host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, _host.ColumnDefinitions.Count));
        Kit.AnimateIn(window);

        var loader = LoadingOverlay.Show(_host, "ABRINDO A POKéDEX…", "Lendo cada célula da dex e cada slot de armazenamento.");
        _ = Task.Run(async () =>
        {
            try
            {
                var states = new Dictionary<int, (bool, bool)>();
                var total = _session.GetDexProgress().Total;
                var max = Math.Min(_data.SpeciesNames.Count, total + 1);
                for (var id = 1; id < max; id++)
                {
                    // A species the game has no dex cell for would take the edit and drop it on save.
                    if (_data.SpeciesNames[id].Length == 0 || !_session.IsDexSpecies(id)) continue;
                    var state = _session.GetDexEntry(id);
                    states[id] = (state.Seen, state.Caught);
                }
                var missing = _session.GetMissingSpecies();
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    foreach (var pair in states) _states[pair.Key] = pair.Value;
                    _orderedIds.AddRange(states.Keys.Order());
                    _missing.AddRange(missing);
                    RefreshView();
                    _loaded = true;
                    loader.Close();
                    RefreshChrome();
                    _router?.Push(this);
                });
            }
            catch (Exception error)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    loader.Close();
                    _viewModel.Status = $"O editor da dex falhou: {error.Message}";
                    TearDown();
                    _result.TrySetResult(false);
                });
            }
        });
    }

    private async Task CloseAsync()
    {
        if (_staged.Count > 0)
        {
            var choice = await PadMenu.ShowAsync(_host, "Salvar as mudanças na dex?",
                $"{_staged.Count} espécies alteradas.", "Salvar mudanças", "Descartar mudanças", "Continuar editando");
            if (choice == "Salvar mudanças") { await ApplyAndCloseAsync(); return; }
            if (choice == "Continuar editando" || choice is null) return;
        }
        TearDown();
        _result.TrySetResult(false);
    }

    private async Task ApplyAndCloseAsync()
    {
        var staged = _staged.ToDictionary(x => x.Key, x => x.Value);
        if (staged.Count > 0)
        {
            var saved = await _viewModel.RunMutationAsync(s =>
            {
                foreach (var (species, state) in staged)
                    s.SetDexEntry(species, state.Seen, state.Caught);
                return new GenerationOutcome(true, $"Dex atualizada para {staged.Count} espécies.");
            }, Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.EditDex);
            if (!saved)
            {
                // The write aborted (validation, storage, format refusal): keep the
                // editor open with the staged changes instead of closing as if it worked.
                _viewModel.Status = "A gravação da dex falhou - mudanças mantidas, veja o status";
                return;
            }
        }
        _staged.Clear();
        TearDown();
        _result.TrySetResult(true);
    }

    private void TearDown()
    {
        _secondClaim?.Release();
        if (_secondState is not null) _secondState.PreviewSpecies = null;
        _router?.Remove(this);
        _host.Remove(_overlay);
    }

    private void SelectAllGaps()
    {
        if (!_gapsMode) return;
        if (_fillSelection.Count == _missing.Count) _fillSelection.Clear();
        else
        {
            _fillSelection.Clear();
            _fillSelection.UnionWith(_missing);
        }
        RefreshChrome();
    }

    private async Task ShowActionsAsync()
    {
        if (_gapsMode)
        {
            var gapChoice = await PadMenu.ShowAsync(_host, "Faltando na Living Dex",
                $"{_missing.Count} espécies faltando no armazenamento. {_fillSelection.Count} selecionadas.",
                new PadOption($"Gerar selecionadas ({_fillSelection.Count})", IconPath: "create",
                    Detail: "Cria um Pokémon legal de cada espécie selecionada nas suas boxes."),
                new PadOption("Ir para o editor da dex", IconPath: "pokedex", Detail: "Volta a marcar espécies vistas e capturadas."),
                new PadOption("Fechar", IconPath: "close"));
            if (gapChoice == "Ir para o editor da dex")
            {
                _gapsMode = false;
                RefreshView();
                return;
            }
            if (gapChoice != $"Gerar selecionadas ({_fillSelection.Count})" || _fillSelection.Count == 0) return;
            var species = _fillSelection.OrderBy(x => x).ToList();
            var overlay = LoadingOverlay.Show(_host, "Gerando…", "O legalizador está criando cada Pokémon offline.");
            try
            {
                await _viewModel.RunMutationAsync(s => _legalizer.FillSpecies(s, species,
                    (done, total) => overlay.Report(done, total)), Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                    action: SaveAction.CreateMon);
                _viewModel.RefreshAllSlots();
                _missing.RemoveAll(_fillSelection.Contains);
                _fillSelection.Clear();
                RefreshView();
            }
            finally { overlay.Close(); }
            return;
        }

        var choice = await PadMenu.ShowAsync(_host, "Ações da dex", null,
            new PadOption("Como obter este", IconPath: "map", Detail: "Onde e como encontrar a espécie sob o cursor."),
            new PadOption("Marcar tudo como visto", IconPath: "selectall", Detail: "Toda espécie fica como vista; as capturadas continuam capturadas."),
            new PadOption("Completar a Pokédex", IconPath: "pokedex", Detail: "Toda espécie fica como vista e capturada; você escolhe se salva ao sair."),
            new PadOption("Ir para o que falta na Living Dex", IconPath: "storage", Detail: "Espécies que faltam nas suas boxes, prontas para criar."),
            new PadOption("Descartar mudanças pendentes", IconPath: "clear", Detail: "Desfaz as mudanças feitas desde que você abriu a dex."));
        switch (choice)
        {
            case "Como obter este":
                if (_grid.Cursor < _viewIds.Count)
                    await EncounterGallery.ShowForSpeciesAsync(_host, _viewModel, _session, _viewIds[_grid.Cursor], 0, _grid.Refresh);
                return;
            case "Marcar tudo como visto":
                foreach (var id in _orderedIds)
                    _staged[id] = (true, StateOf(id).Caught); // caught species stay caught
                break;
            case "Completar a Pokédex":
                foreach (var id in _orderedIds)
                    _staged[id] = (true, true);
                break;
            case "Ir para o que falta na Living Dex":
                _gapsMode = true;
                RefreshView();
                return;
            case "Descartar mudanças pendentes":
                _staged.Clear();
                break;
        }
        RefreshChrome();
    }

    private void Activate(int species)
    {
        if (_gapsMode)
        {
            if (!_fillSelection.Remove(species)) _fillSelection.Add(species);
        }
        else if (_states.TryGetValue(species, out var state))
        {
            // Cycle from the CURRENT state (staged wins over the saved state), or every
            // press would restart from the save's original "unseen" and never advance.
            var current = StateOf(species);
            _staged[species] = current switch
            {
                { Seen: false } => (true, false),
                { Caught: false } => (true, true),
                _ => (false, false),
            };
        }
        RefreshChrome();
    }

    private (bool Seen, bool Caught) StateOf(int species) =>
        _staged.TryGetValue(species, out var staged) ? staged
        : _states.TryGetValue(species, out var state) ? state
        : (false, false);

    private void RefreshChrome()
    {
        _title.Text = _gapsMode ? $"Faltando na Living Dex · {_missing.Count}" : "Pokédex";
        var seen = 0;
        var caught = 0;
        foreach (var id in _orderedIds)
        {
            var state = StateOf(id);
            if (state.Seen) seen++;
            if (state.Caught) caught++;
        }
        _progress.Text = _gapsMode
            ? $"{_fillSelection.Count} selected"
            : $"Vistos {seen}/{_states.Count} · capturados {caught}/{_states.Count} · {_staged.Count} pendentes";
        RefreshInfo();
        _grid.Refresh();
    }

    /// <summary>The line under the list: the species under the cursor and its state.</summary>
    private void RefreshInfo()
    {
        if (_grid.Cursor >= _viewIds.Count) { _cursorInfo.Text = ""; return; }
        var id = _viewIds[_grid.Cursor];
        var state = StateOf(id);
        _cursorInfo.Text = _gapsMode
            ? $"#{id:000} {_data.SpeciesNames[id]} · {(_fillSelection.Contains(id) ? "selected for generation" : "not selected")}"
            : $"#{id:000} {_data.SpeciesNames[id]} · {(state.Caught ? "caught" : state.Seen ? "seen" : "unseen")}";
    }

    /// <summary>How the list draws a species: its state, or in gaps mode whether it is picked.</summary>
    private DexGridView.Look LookOf(int species)
    {
        var state = StateOf(species);
        return _gapsMode
            ? new DexGridView.Look(Seen: true, Caught: false, Dimmed: !_fillSelection.Contains(species), Ticked: _fillSelection.Contains(species))
            : new DexGridView.Look(state.Seen, state.Caught);
    }

    /// <summary>A generation's caught count, on its chip.</summary>
    private string? SectionNote(DexRegions.Region region)
    {
        if (_gapsMode) return null;
        var ids = _orderedIds.Where(id => id >= region.First && id <= region.Last).ToList();
        return ids.Count == 0 ? null : $"{ids.Count(id => StateOf(id).Caught)}/{ids.Count}";
    }

    /// <summary>Applies the search to the active list; resets paging and repaints.</summary>
    private void RefreshView()
    {
        _viewIds.Clear();
        var source = _gapsMode ? _missing : _orderedIds;
        var query = _query.Trim();
        foreach (var id in source)
        {
            if (query.Length == 0
                || _data.SpeciesNames[id].Contains(query, StringComparison.OrdinalIgnoreCase)
                || id.ToString() == query)
                _viewIds.Add(id);
        }
        _grid.Show(_viewIds, LookOf, SectionNote);
        _grid.SetCursor(0);
        RefreshChrome();
    }

    public bool OnPadButton(PadButton button)
    {
        if (!_loaded) return true;
        switch (button)
        {
            case PadButton.Up: _grid.Move(0, -1); return true;
            case PadButton.Down: _grid.Move(0, 1); return true;
            case PadButton.Left: _grid.Move(-1, 0); return true;
            case PadButton.Right: _grid.Move(1, 0); return true;
            case PadButton.L: _grid.JumpSection(-1); return true;
            case PadButton.R: _grid.JumpSection(1); return true;
            case PadButton.A:
                if (_grid.Cursor < _viewIds.Count) Activate(_viewIds[_grid.Cursor]);
                return true;
            case PadButton.B: _ = CloseAsync(); return true;
            case PadButton.X:
            case PadButton.Start: _ = ShowActionsAsync(); return true;
            case PadButton.Y: SelectAllGaps(); return true;
            default: return true;
        }
    }
}
