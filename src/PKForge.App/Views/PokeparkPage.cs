using System.Diagnostics;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;
using SkiaSharp.Views.Maui;

namespace PKForge.App.Views;

public sealed class PokeparkPage : ContentPage, IPadHandler, IPadStickHandler
{
    private readonly PokeparkService _park;
    private readonly ISpriteService _sprites;
    private readonly PokeparkSpriteService _walking;
    private readonly PokeparkNarrativeMemory _narrativeMemory;
    private readonly PokeparkSocialService _social;
    private readonly ParkOfflineJournalService _offlineJournal;
    private readonly PokeparkCareStore _care;
    private readonly PokeparkTreasureStore _treasures;
    private readonly PokeparkDaycareService _daycare;
    private readonly PokeparkWildsStore _wilds;
    private readonly HashSet<string> _restored = new();
    private IReadOnlyDictionary<string, (int Friendship, int Rivalry)> _relations = new Dictionary<string, (int, int)>();
    private long _lastCareSave;
    private readonly PokeparkScene _scene;
    private const string CaptionsKey = "pkforge.pokepark.captions.v1";
    private readonly ParkCanvas _canvas = new();
    private readonly Grid _host;
    private readonly Label _status;
    private readonly IDispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly SemaphoreSlim _widgetPublishGate = new(1, 1);
    private readonly object _renderGate = new();
    private CancellationTokenSource? _secondaryShowCts;
    private bool _active;
    private bool _menuOpen;
    private long _lastJournalUpdate;
    private int _widgetRevision;
    private int _publishedWidgetRevision;
    private bool _loading;
    private int _selected;

    public PokeparkPage(PokeparkService park, ISpriteService sprites, PokeparkSpriteService walking,
        PokeparkNarrativeMemory narrativeMemory, PokeparkSocialService social,
        ParkOfflineJournalService offlineJournal, PokeparkCareStore care, PokeparkTreasureStore treasures,
        PokeparkDaycareService daycare, PokeparkWildsStore wilds)
    {
        _park = park; _sprites = sprites; _walking = walking;
        _narrativeMemory = narrativeMemory; _social = social; _offlineJournal = offlineJournal; _care = care; _treasures = treasures;
        _daycare = daycare; _wilds = wilds;
        _scene = new(sprites, walking) { NatureOf = park.NatureOf, ShowCaptions = Preferences.Default.Get(CaptionsKey, true) };
        LoadSky();
        Title = "Poképark";
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = UiTokens.Housing;
        _status = new Label { FontSize = UiTokens.TextBody };
        // The camera fits any size at a whole-number scale, so the map takes all the room left.
        _canvas.Touch += (_, e) =>
        {
            e.Handled = true;
            if (!_menuOpen) OnTouch(e);
            // A finger lifted while a dialogue is open (a pat that opened one) is still let go of.
            else if (e.ActionType is SKTouchAction.Released or SKTouchAction.Cancelled) _touches.Remove(e.Id);
        };
        _canvas.Paint += (canvas, width, height) =>
        {
            lock (_renderGate)
                _scene.Draw(canvas, width, height, _clock.ElapsedMilliseconds);
        };
        var hints = new List<(string Button, string Label, Action? OnTap)>
        {
            ("B", "Voltar", () => _ = Navigation.PopAsync()),
            ("A", "Conhecer Pokémon", () => _ = DetailsAsync()),
            // L and R fly the camera from one place of the park to the next.
            ("LR", "Lugares", () => FlyToPlace(1)),
            // Up and Down zoom, for players without a touch screen.
            ("↑↓", "Zoom", () => _scene.Viewport.Zoom(1)),
            ("X", "Foto", () => _ = TakePhotoAsync()),
            ("+", "Opções", () => _ = OptionsAsync()),
            // − lets the camera follow whatever is happening in the park.
            ("−", "Assistir", ToggleSpectate)
        };
        var footer = DsChrome.Footer([.. hints]);
        footer.VerticalOptions = LayoutOptions.End;
        // The park fills the screen; the status floats over its top-left corner and fades after a
        // while, the button hints sit along the bottom edge.
        _statusPill = new Border
        {
            Content = _status, BackgroundColor = Color.FromRgba(16, 22, 30, 170), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Margin = new Thickness(10, 10), HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start,
            InputTransparent = true
        };
        _status.TextColor = Colors.White;
        _status.Margin = new Thickness(10, 4);
        _status.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Label.Text)) ShowStatus(); };
        // What is going on, always in sight: what the one you follow is doing, and the best thing
        // happening elsewhere in the park.
        _nowPill = new Border
        {
            Content = new VerticalStackLayout { Spacing = 2, Margin = new Thickness(10, 5), Children = { _now, _elsewhere } },
            BackgroundColor = Color.FromRgba(16, 22, 30, 150), StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Margin = new Thickness(10, 0, 10, 6), HorizontalOptions = LayoutOptions.Center,
            InputTransparent = true, MaximumWidthRequest = 1100
        };
        _now.TextColor = Colors.White; _now.FontSize = UiTokens.TextBody; _now.LineBreakMode = LineBreakMode.TailTruncation;
        _elsewhere.TextColor = Color.FromRgb(0xf4, 0xc8, 0x4a); _elsewhere.FontSize = UiTokens.TextBody * .85; _elsewhere.LineBreakMode = LineBreakMode.TailTruncation;
        // At the very bottom, just above the button hints.
        _now.HorizontalTextAlignment = _elsewhere.HorizontalTextAlignment = TextAlignment.Center;
        var bottom = new VerticalStackLayout { VerticalOptions = LayoutOptions.End, Children = { _nowPill, footer } };
        footer.VerticalOptions = LayoutOptions.Start;
        _host = new Grid { Children = { _canvas.View, _statusPill, bottom } };
        Content = _host;
        _timer = Dispatcher.CreateTimer();
        // PMD frame durations count 60ths of a second.
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (_, _) =>
        {
            if (_clock.ElapsedMilliseconds - _lastJournalUpdate >= 250)
            {
                UpdateJournal();
                RecordMoments();
                CheckGameOver();
                PollHighlights();
                UpdateNow();
                UpdateMusic();
            }
            if (_clock.ElapsedMilliseconds - _lastCareSave >= 30_000) SaveCare();
            CheckLongPress(_clock.ElapsedMilliseconds);
            // 60 frames a second while the camera moves or a finger is down, 30 otherwise: the PMD
            // animations and the park's life look the same at 30, and it halves the drawing.
            if (FullRate() || (++_frame & 1) == 0) _canvas.InvalidateSurface();
        };
    }

    // ── Highlights: something worth watching has started somewhere ──────────────

    private ParkHighlight? _offered;
    private long _offeredAt;
    private bool _spectating;
    private ParkHighlight? _watching;
    private long _watchingSince;
    private const long OfferFor = 6000, IdleSpectate = 30_000;

    // A good moment shows at the top with "X: go see"; in spectator mode the camera goes there by itself.
    private void PollHighlights()
    {
        var now = _clock.ElapsedMilliseconds;
        foreach (var highlight in _scene.Life.TakeHighlights().OrderByDescending(h => h.Event is not null).ThenByDescending(h => h.Priority))
        {
            PerfTrace.Park($"moment ({highlight.Priority}): {highlight.Text}");
            // One of the park's events: it takes the whole screen, once in a while at most.
            if (highlight.Event is not null && !_menuOpen && (_lastEventAt == 0 || now - _lastEventAt > EventsEvery))
            {
                _lastEventAt = now;
                _ = AnnounceAsync(highlight);
                continue;
            }
            if (_spectating)
            {
                if (highlight.Priority >= 2) _scene.Notify(highlight.Text);
                if (_watching is null || _watching.Until <= _scene.Life.Now || highlight.Priority > _watching.Priority) Watch(highlight);
            }
            else if (highlight.Priority >= 2)
            {
                _scene.Notify($"{(highlight.Priority >= 3 ? "★ " : "")}{highlight.Text}   X: ir ver");
                _offered = highlight; _offeredAt = now;
            }
        }
        if (_spectating && (_watching is null || _watching.Until <= _scene.Life.Now) && now - _watchingSince > IdleSpectate)
        {
            // Nothing big going on: whatever is still worth a look, else someone at random.
            if (_scene.Life.Highlights.FirstOrDefault() is { } any) Watch(any);
            else if (_scene.Residents.Count > 0)
            {
                _watching = null; _watchingSince = now;
                SelectResident(Random.Shared.Next(_scene.Residents.Count));
            }
        }
    }

    private long _lastEventAt;
    private const long EventsEvery = 90_000;

    private async Task AnnounceAsync(ParkHighlight highlight)
    {
        if (_menuOpen) return; _menuOpen = true;
        bool go;
        try
        {
            var cast = highlight.Cast.Select(id => _scene.Life[id]?.Resident).OfType<ParkLifeResident>();
            // The camera goes to the moment while it is announced, a little closer.
            if (_scene.Life[highlight.ActorId] is { } star) { _scene.Viewport.FlyTo(star.X, star.Y - star.Altitude); _scene.Viewport.Zoom(1); }
            go = await PokeparkEventOverlay.ShowAsync(_host, highlight, cast, _sprites, _walking, _spectating);
        }
        finally { _menuOpen = false; }
        if (!go) { _scene.Viewport.Zoom(-1); _scene.Viewport.Resume(); return; }
        if (_spectating) Watch(highlight);
        else GoSee(highlight);
    }

    private void Watch(ParkHighlight highlight)
    {
        _watching = highlight; _watchingSince = _clock.ElapsedMilliseconds;
        GoSee(highlight);
    }

    private void GoSee(ParkHighlight highlight)
    {
        _offered = null;
        for (var i = 0; i < _scene.Residents.Count; i++)
            if (_scene.Residents[i].Id == highlight.ActorId) { SelectResident(i); return; }
        // A wild visitor: the camera glides to it.
        if (_scene.Life[highlight.ActorId] is { } actor) _scene.Viewport.FlyTo(actor.X, actor.Y);
    }

    // A photo of the park as it looks right now, captioned with what is happening, into the album.
    private async Task TakePhotoAsync()
    {
        if (_menuOpen || _scene.Residents.Count == 0) return;
        var (width, height) = ((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height);
        if (width <= 0 || height <= 0) return;
        byte[] jpeg;
        using (var surface = SKSurface.Create(new SKImageInfo(width, height)))
        {
            lock (_renderGate) _scene.Draw(surface.Canvas, width, height, _clock.ElapsedMilliseconds);
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 88);
            jpeg = data.ToArray();
        }
        var caption = (_watching ?? (Offering ? _offered : null) ?? _scene.Life.Highlights.FirstOrDefault())?.Text is { } happening
            ? happening.Split("!")[0].TrimEnd('.', ' ') + (happening.Contains('!') ? "!" : "")
            : _selected >= 0 && _selected < _scene.Residents.Count && _scene.Life[_scene.Residents[_selected].Id] is { } actor
                ? $"{actor.Resident.Name}: {actor.Activity}"
                : "O Poképark";
        await Task.Run(() => PokeparkAlbum.Save(jpeg, caption, DateTime.Now));
        _scene.Notify("Clique! Foto salva no álbum (Opções)");
    }

    private bool Offering => _offered is not null && _clock.ElapsedMilliseconds - _offeredAt < OfferFor;

    private void ToggleSpectate()
    {
        _spectating = !_spectating;
        _watching = null; _watchingSince = 0;
        _scene.Notify(_spectating ? "Assistindo: a câmera acompanha o que acontece no parque" : "Modo assistir desligado");
    }

    private void StopSpectating()
    {
        if (!_spectating) return;
        _spectating = false; _watching = null;
        _scene.Notify("Modo assistir desligado");
    }

    private int _frame;
    private (float X, float Y, float Scale) _lastView;

    private bool FullRate()
    {
        var viewport = _scene.Viewport;
        var view = (viewport.CenterX, viewport.CenterY, viewport.Scale);
        var moved = MathF.Abs(view.CenterX - _lastView.X) + MathF.Abs(view.CenterY - _lastView.Y) > .05f || view.Scale != _lastView.Scale;
        _lastView = view;
        return moved || _touches.Count > 0 || viewport.IsMoving;
    }

    private readonly Border _nowPill;
    private readonly Label _now = new(), _elsewhere = new();

    // The park's own music: one for the day, one for the evening and the night (Options, Music).
    private static IMusicPlayer? Music => IPlatformApplication.Current?.Services.GetService<IMusicPlayer>();

    private async Task SnowmanAsync()
    {
        if (_menuOpen) return; _menuOpen = true;
        try
        {
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Boneco de neve", Random.Shared.Next(10) == 0
                ? "Você troca olhares com o boneco de neve. Ele te olha com um ar de orgulho e realização. Você sente que, pelo menos agora, as coisas vão ficar bem."
                : "Só um boneco de neve. Nada demais para ver aqui.");
        }
        finally { _menuOpen = false; }
    }

    private void UpdateMusic()
    {
        var place = _scene.Life.Phase is ParkDayPhase.Evening or ParkDayPhase.Night ? MusicPlace.ParkNight : MusicPlace.ParkDay;
        if (Music is { } music && music.Place != place) music.EnterPlace(place);
    }

    private void UpdateNow()
    {
        // Hidden while a card or a menu is up: the card says what it is doing.
        if (_menuOpen || _scene.Residents.Count == 0 || _selected < 0 || _selected >= _scene.Residents.Count) { _nowPill.IsVisible = false; return; }
        _nowPill.IsVisible = true;
        var mon = _scene.Residents[_selected];
        var actor = _scene.Life[mon.Id];
        var now = actor is null ? "" : $"AGORA  ·  {mon.Name}: {actor.Activity}";
        if (_now.Text != now) _now.Text = now;
        var elsewhere = _scene.Life.Highlights.FirstOrDefault(h => h.ActorId != mon.Id) is { } highlight
            ? $"EM OUTRO LUGAR  ·  {highlight.Text.Split("! ")[0].TrimEnd('!')}{(highlight.Text.Contains('!') ? "!" : "")}"
            : "";
        if (_elsewhere.Text != elsewhere) { _elsewhere.Text = elsewhere; _elsewhere.IsVisible = elsewhere.Length > 0; }
    }

    private SecondScreenClaim? _secondClaim;
    private readonly Border _statusPill;
    private int _statusShown;

    // The status shows for a few seconds after it changes, then fades so the park is all there is.
    private void ShowStatus()
    {
        var shown = ++_statusShown;
        _statusPill.CancelAnimations();
        _statusPill.Opacity = 1;
        Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(5), () =>
        {
            if (shown == _statusShown) _ = _statusPill.FadeToAsync(0, 600);
        });
    }

    protected override void OnAppearing()
    {
        base.OnAppearing(); _active = true;
        _secondClaim ??= IPlatformApplication.Current?.Services.GetService<SecondScreenState>()?.Routes.CreateClaim(SecondScreenOwner.Pokepark);
        _secondClaim?.Activate();
        IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>()?.Open();
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Push(this);
        App.Resumed += Resume; App.Suspended += Suspend;
        _clock.Start(); _timer.Start();
        // The persisted roster is cheap and gives the first paint immediately.
        // Initialization only scans on first use and refreshes this view afterwards.
        Reload();
        _ = LoadParkAsync();
        UpdateMusic();
        // A park can be opened from the widget/deep link without passing through
        // HomePage. Ensure Thor's lower display is alive and observing the journal.
        var secondary = IPlatformApplication.Current?.Services.GetService<PKForge.Domain.ISecondaryDisplayHost>();
        if (secondary?.IsAvailable == true)
        {
            _secondaryShowCts?.Cancel();
            _secondaryShowCts?.Dispose();
            _secondaryShowCts = new CancellationTokenSource();
            _ = ShowSecondaryJournalAsync(secondary, _secondaryShowCts.Token);
        }
    }

    private static async Task ShowSecondaryJournalAsync(PKForge.Domain.ISecondaryDisplayHost host, CancellationToken cancellationToken)
    {
        try { await host.ShowPokeparkJournalAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Poképark second screen: {ex.Message}"); }
    }
    protected override void OnDisappearing()
    {
        SaveCare();
        _offlineJournal.MarkCurrent();
        _active = false; _timer.Stop(); _clock.Stop();
        _secondaryShowCts?.Cancel();
        _secondaryShowCts?.Dispose();
        _secondaryShowCts = null;
        App.Resumed -= Resume; App.Suspended -= Suspend;
        IPlatformApplication.Current?.Services.GetService<GamepadRouter>()?.Remove(this);
        if (WidgetDirty) _ = PublishWidgetAsync();
        IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>()?.Clear();
        _secondClaim?.Release();
        Music?.EnterPlace(MusicPlace.Menus);
        base.OnDisappearing();
    }
    private void Resume() { if (_active) { _clock.Start(); _timer.Start(); } }
    private void Suspend()
    {
        SaveCare();
        _offlineJournal.MarkCurrent();
        _timer.Stop(); _clock.Stop(); if (WidgetDirty) _ = PublishWidgetAsync();
    }

    // The story of the time away, and today's anniversaries, told once the park is showing.
    private IReadOnlyList<ParkAbsence.Story> _absence = [];

    private void RunOfflineLife()
    {
        var stories = _absence;
        _absence = [];
        foreach (var moment in stories.Select(s => s.Moment).OfType<ParkMoment>())
            if (_scene.Life[moment.FirstId] is { } first && _scene.Life[moment.SecondId] is { } second)
                _social.Witness(moment, first.Resident.Name, second.Resident.Name);
        var parties = Anniversaries();
        if (stories.Count == 0 && parties.Count == 0) return;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (!_active || _menuOpen) return;
            _menuOpen = true;
            try
            {
                foreach (var story in stories)
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Enquanto você estava fora", story.Text);
                foreach (var (mon, line) in parties)
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line, () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, "Joyous"));
            }
            finally { _menuOpen = false; RefreshRecent(); }
        });
    }

    // Residents met on this day in an earlier year (or today): each gets its party once a year.
    private List<(ParkPokemon Mon, string Line)> Anniversaries()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var parties = new List<(ParkPokemon, string)>();
        foreach (var mon in _scene.Residents)
        {
            if (_park.MetDateOf(mon) is not { } met || met.Month != today.Month || met.Day != today.Day) continue;
            var key = $"pkforge.pokepark.party.{mon.Id}";
            if (Preferences.Default.Get(key, 0) == today.Year) continue;
            if (_scene.Life.Celebrate(mon.Id, today.Year - met.Year).Line is not { } line) continue;
            Preferences.Default.Set(key, today.Year);
            parties.Add((mon, line));
        }
        return parties;
    }

    private async Task LoadParkAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var needsInitialization = _scene.Residents.Count == 0;
            await _scene.LoadArtAsync().ConfigureAwait(true);
            if (!_active) return;
            _canvas.InvalidateSurface();
            if (needsInitialization)
            {
                // Let the navigation animation and first frame complete before doing
                // optional first-use population work.
                await Task.Delay(350).ConfigureAwait(true);
                await Task.Run(_park.EnsureInitialized).ConfigureAwait(true);
                if (!_active) return;
                Reload();
            }
            RunOfflineLife();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Poképark load: {ex.Message}");
        }
        finally { _loading = false; }
    }

    private void Reload()
    {
        _scene.Residents = _park.LoadRoster();
        // Needs saved at the last visit come back, aged by the time away; once per resident.
        var now = DateTimeOffset.Now;
        // The needs saved at the last visit, aged by the time away, and the story of that time.
        var saved = _care.Load().Where(c => _restored.Add(c.Id)).ToArray();
        var before = saved.ToDictionary(c => c.Id);
        _scene.Life.Restore(saved, now);
        SetUpDaycareAndWilds();
        if (saved.Length > 0 && saved.Max(c => c.LastSeen) is var lastSeen && lastSeen != default)
        {
            var residents = _scene.Life.Actors.Where(a => before.ContainsKey(a.Resident.Id)).ToArray();
            _absence = ParkAbsence.Tell(residents.Select(a => a.Resident).ToArray(), before,
                residents.ToDictionary(a => a.Resident.Id, a => a.Care), lastSeen.LocalDateTime, DateTime.Now, new Random(), _scene.Life.OldFriends);
        }
        foreach (var mon in _scene.Residents) _restored.Add(mon.Id);
        _relations = _social.Relations(now);
        if (!_chronicleLoaded)
        {
            _scene.Life.Chronicle = ParkChronicle.Load(Preferences.Default.Get<string?>(ChronicleKey, null));
            _chronicleLoaded = true;
        }
        RefreshRecent();
        _scene.Life.Relation = (a, b) => _relations.GetValueOrDefault(ParkRelationship.KeyFor(a, b));
        SelectResident(Math.Min(_selected, _scene.Residents.Count - 1));
        UpdateStatus();
        UpdateJournal();
        foreach (var mon in _scene.Residents)
        {
            _sprites.Warm(mon.Species, mon.Form, mon.Shiny, AssetsLoaded);
            _walking.Warm(mon.Species, mon.Form, mon.Shiny, AssetsLoaded);
            // Portraits for the card and the lower screen, before anyone opens them.
            foreach (var emotion in new[] { "Normal", "Happy", "Joyous" }) _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, emotion);
        }
        MarkWidgetDirty();
        _canvas.InvalidateSurface();
    }
    private void AssetsLoaded() => MainThread.BeginInvokeOnMainThread(() =>
    {
        MarkWidgetDirty();
        if (_active) _canvas.InvalidateSurface();
        else _ = PublishWidgetAsync();
    });
    private void SelectResident(int index)
    {
        var visible = Enumerable.Range(0, _scene.Residents.Count).ToArray();
        if (visible.Length == 0)
        {
            _selected = 0;
            _scene.SelectedIndex = -1;
            UpdateJournal();
            _canvas.InvalidateSurface();
            return;
        }
        var position = Array.IndexOf(visible, index);
        if (position < 0) position = index < 0 ? 0 : Array.FindIndex(visible, candidate => candidate > index);
        if (position < 0) position = 0;
        _selected = visible[(position + visible.Length) % visible.Length];
        _scene.SelectedIndex = _scene.Residents.Count == 0 ? -1 : _selected;
        _scene.Viewport.Resume();
        UpdateJournal();
        _canvas.InvalidateSurface();
    }

    private void UpdateJournal()
    {
        _lastJournalUpdate = _clock.ElapsedMilliseconds;
        var journal = IPlatformApplication.Current?.Services.GetService<PokeparkJournalState>();
        if (journal is null || _selected < 0 || _selected >= _scene.Residents.Count)
        {
            journal?.Clear();
            return;
        }
        var mon = _scene.Residents[_selected]; var p = Personality(mon);
        var card = CardFor(mon);
        journal.Resident = mon; journal.Mood = card.Mood; journal.Activity = card.Activity; journal.Trait = p.Trait; journal.Likes = p.Likes; journal.Story = MemoryOf(mon, p.Story);
        journal.Personality = p.Trait; journal.Needs = card.Needs; journal.Hearts = card.Hearts;
        journal.Taste = ParkTaste.Describe(ParkTaste.NatureOrGuess(_park.NatureOf(mon), mon.Id), _scene.Life[mon.Id]?.Care.TasteKnown ?? 0);
        journal.Recent = _recent;
        journal.Bonds = BondsOf(mon);
    }

    private async Task PublishWidgetAsync()
    {
        if (!PokeparkWidgetPublisher.HasActiveWidgets())
        {
            Volatile.Write(ref _publishedWidgetRevision, Volatile.Read(ref _widgetRevision));
            return;
        }
        if (!await _widgetPublishGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var revision = Volatile.Read(ref _widgetRevision);
            var frames = await Task.Run(() =>
            {
                var result = new List<byte[]>(PokeparkWidgetPublisher.FrameCount);
                using var bitmap = new SKBitmap(PokeparkWidgetPublisher.FrameWidth, PokeparkWidgetPublisher.FrameHeight);
                using var canvas = new SKCanvas(bitmap);
                for (var frame = 0; frame < PokeparkWidgetPublisher.FrameCount; frame++)
                {
                    lock (_renderGate)
                        _scene.DrawWidgetFrame(canvas, bitmap.Width, bitmap.Height, frame);
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                    result.Add(data.ToArray());
                }
                return result;
            }).ConfigureAwait(false);
            try { PokeparkWidgetPublisher.Publish(frames); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Debug.WriteLine($"Poképark widget: {ex.Message}"); }
            Volatile.Write(ref _publishedWidgetRevision, revision);
        }
        finally { _widgetPublishGate.Release(); }
    }

    private async Task OptionsAsync()
    {
        if (_menuOpen) return; _menuOpen = true;
        try
        {
            var current = _park.Settings.Source switch
            {
                PokeparkSource.Save => "Só o save",
                PokeparkSource.Bank => "Só o Banco",
                PokeparkSource.Disabled => "Desativado",
                _ => "Save + Banco"
            };
            var options = new List<string> { "Metas de hoje", "Álbum de fotos", "Creche", "Como jogar", "Diário do parque", "Hora e clima", "Música", "Tesouros",
                "Modo de preenchimento automático", "Convidar Pokémon", "Esvaziar parque", "Ajuda do widget", "Créditos", _scene.ShowCaptions ? "Legendas: Ligadas" : "Legendas: Desligadas",
                ParkCanvas.UsesGpu ? "Desenho: GPU" : "Desenho: CPU" };
#if DIAGNOSTIC
            // Frame times over the park, for measuring on a device.
            options.Add(_scene.ShowPerformance ? "Esconder desempenho" : "Mostrar desempenho");
#endif
            var choice = await PadMenu.ShowAsync(_host, "Opções do Poképark", $"Preenchimento automático: {current}. Os moradores atuais nunca são movidos nem substituídos.", [.. options]);
            if (choice is "Desenho: GPU" or "Desenho: CPU")
            {
                var gpu = choice == "Desenho: CPU";
                Preferences.Default.Set(ParkCanvas.RendererKey, gpu ? "gpu" : "cpu");
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "Desenho", gpu
                    ? "O parque vai ser desenhado pela GPU na próxima vez que você abrir: mais leve e mais fluido."
                    : "O parque vai ser desenhado pela CPU na próxima vez que você abrir, caso o desenho pela GPU dê problema neste aparelho.");
            }
            if (choice is "Legendas: Ligadas" or "Legendas: Desligadas")
            {
                _scene.ShowCaptions = choice == "Legendas: Desligadas";
                Preferences.Default.Set(CaptionsKey, _scene.ShowCaptions);
            }
            if (choice is "Mostrar desempenho" or "Esconder desempenho") _scene.ShowPerformance = choice == "Mostrar desempenho";
            if (choice == "Diário do parque")
                await ShowJournalAsync();
            if (choice == "Álbum de fotos")
                await PokeparkAlbumOverlay.ShowAsync(_host);
            if (choice == "Modo de preenchimento automático")
            {
                var mode = await PadMenu.ShowAsync(_host, "Preencher o parque automaticamente", "Escolha de onde os moradores automáticos podem vir. Os convites manuais continuam disponíveis em todos os modos.",
                    "Save + Banco", "Só o save", "Só o Banco", "Desativado");
                var source = mode switch
                {
                    "Só o save" => PokeparkSource.Save,
                    "Só o Banco" => PokeparkSource.Bank,
                    "Desativado" => PokeparkSource.Disabled,
                    _ => PokeparkSource.Both
                };
                _park.SaveSettings(_park.Settings with { Source = source });
                await Task.Run(() =>
                {
                    _park.EnsureInitialized();
                    _park.RefreshAutoFill();
                });
                Reload();
            }
            if (choice == "Convidar Pokémon") await PokeparkCardOverlay.ShowDialogueAsync(_host, "Guia do parque", "Abra o menu de ações de um Pokémon nas boxes do seu save ou no Banco e escolha Enviar para o Poképark. O original fica exatamente onde está.");
            else if (choice == "Esvaziar parque" && _scene.Residents.Count > 0 &&
                     await PadMenu.ConfirmAsync(_host, "Esvaziar o parque?", $"Remover todos os {_scene.Residents.Count} moradores do Poképark? Os Pokémon originais continuam no save ou no Banco.", "Esvaziar parque"))
            {
                _park.RemoveAllVisitors();
                Reload();
            }
            else if (choice == "Ajuda do widget")
                await PadMenu.ShowAsync(_host, "Um prado na sua tela inicial", "Adicione o PKForge Poképark no seletor de widgets do Cocoon. Aumente o tamanho para dar mais espaço aos seus Pokémon. O prado completo mostra um momento tranquilo e vivo; toque nele para voltar aqui. Seus moradores ficam salvos entre as visitas.", "OK");
            else if (choice == "Créditos") { _menuOpen = false; await CreditsAsync(); }
            else if (choice == "Hora e clima") await SkyAsync();
            else if (choice == "Música") await MusicMenu.ShowAsync(_host, _scene.Notify);
            else if (choice == "Como jogar") await GuideAsync();
            else if (choice == "Metas de hoje") await GoalsAsync();
            else if (choice == "Creche") { _menuOpen = false; await DaycareAsync(); }
            else if (choice == "Tesouros")
            {
                var found = _treasures.Load().OrderByDescending(t => t.Value).Select(t => $"{t.Key} ×{t.Value}").ToArray();
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "Tesouros", found.Length == 0
                    ? "Nada foi desenterrado ainda. Peça para um Pokémon cavar pelo cartão dele: Play e depois Dig. Os tipos Ground e Rock têm o melhor faro para isso."
                    : "Desenterrado até agora: " + string.Join(", ", found) + ".");
            }
        }
        finally { _menuOpen = false; }
    }

    // The park's latest moments for the lower screen, newest first.
    private string[] _recent = [];
    private IReadOnlyList<ParkSocialEvent> _journalLines = [];
    private void RefreshRecent()
    {
        _journalLines = _social.Journal(100);
        _recent = _journalLines.Take(6).Select(e => e.Text).ToArray();
    }

    // What the card remembers for a resident: something that really happened to it in the park,
    // the newest while it is fresh, else one of its last few in turn; its standing story when it
    // has none yet.
    private string MemoryOf(ParkPokemon mon, string story)
    {
        var own = _journalLines.Where(e => e.PokemonIds.Contains(mon.Id) && e.Kind != ParkSocialEventKind.Rest).Take(5).ToArray();
        if (own.Length == 0) return story;
        if (DateTimeOffset.Now - own[0].OccurredAt < TimeSpan.FromHours(1)) return own[0].Text;
        return own[(int)(DateTime.Now.Ticks / TimeSpan.FromSeconds(30).Ticks % own.Length)].Text;
    }

    private void UpdateStatus() => _status.Text = _scene.Residents.Count == 0
        ? "Seu prado está esperando. Selecione um Pokémon numa box ou no Banco e depois Enviar para o Poképark."
        : $"{_scene.Region.Name}  ·  {_scene.Residents.Count}/{PokeparkService.MaximumResidents} moradores  ·  Metas de hoje {Goals().Progress.Where((_, i) => Goals().Done(i)).Count()}/{Goals().Goals.Count}  ·  Toque num Pokémon para conhecê-lo, segure para fazer carinho";

    // Today's three goals, kept until midnight.
    private const string GoalsKey = "pkforge.pokepark.goals.v1";
    private ParkGoalsState? _goals;

    private ParkGoalsState Goals()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_goals is null || _goals.Day != today)
            _goals = ParkGoals.Load(Preferences.Default.Get<string?>(GoalsKey, null), today, _scene.Residents.Count,
                _scene.Residents.Any(r => HabitatCatalog.TypesFor(r.Species, r.Form).Contains(ParkType.Fighting)));
        return _goals;
    }

    // Counts something the player did toward today's goals; a finished goal shows at the top, and all
    // three set off a party with a rare find.
    private void Deed(ParkDeed deed, string id)
    {
        if (_scene.Residents.Count == 0) return;
        var state = ParkGoals.Record(Goals(), deed, id, out var done);
        foreach (var goal in done) _scene.Notify($"Meta concluída: {goal.Text}");
        if (state.AllDone && !state.Rewarded)
        {
            var reward = ParkGoals.RewardFor(state.Day);
            state = state with { Rewarded = true };
            _treasures.Add(reward);
            _scene.Life.Rejoice();
            _scene.Notify($"Todas as metas de hoje concluídas! O parque comemora, e você encontra {reward.Name}.");
        }
        _goals = state;
        Preferences.Default.Set(GoalsKey, ParkGoals.Save(state));
        MainThread.BeginInvokeOnMainThread(UpdateStatus);
    }

    // Talking to Blissey: leave a Pokémon, pick one up, take the egg, or change whether the daycare writes back to saves.
    private async Task DaycareAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            while (true)
            {
                var roster = _scene.Residents.ToDictionary(r => r.Id);
                var guests = _daycare.Stays.Where(s => roster.ContainsKey(s.ResidentId)).Select(s => roster[s.ResidentId]).ToArray();
                var egg = _daycare.Egg is { } e && roster.TryGetValue(e.First, out var first) && roster.TryGetValue(e.Second, out var second) ? (first, second) : ((ParkPokemon, ParkPokemon)?)null;
                var writes = _park.Settings.DaycareWritesBack;
                var options = new List<PadOption>();
                if (egg is { } waiting) options.Add(new PadOption("Pegar o ovo", IconPath: "park", Detail: $"De {waiting.Item1.Name} e {waiting.Item2.Name}. {(writes ? "Ele vai para o seu Banco." : "Gravar de volta está desligado: ele vira uma lembrança.")}"));
                foreach (var guest in guests)
                {
                    var (time, growth) = _daycare.Progress(guest);
                    var detail = growth is { ExpGained: > 0 } g
                        ? $"Aqui há {Stayed(time)}. Nível {g.LevelBefore}, ficaria {g.LevelAfter} ao buscar."
                        : $"Aqui há {Stayed(time)}.";
                    options.Add(new PadOption($"Buscar {Named(guest)}", IconPath: "move", Detail: detail));
                }
                if (guests.Length < PokeparkDaycareService.Capacity && _scene.Residents.Count > guests.Length)
                    options.Add(new PadOption("Deixar um Pokémon aqui", IconPath: "park", Detail: $"{guests.Length} de {PokeparkDaycareService.Capacity} vagas ocupadas."));
                options.Add(new PadOption($"Gravar de volta nos saves: {(writes ? "Ligado" : "Desligado")}", IconPath: "settings",
                    Detail: writes ? "EXP e ovos voltam para os seus saves e Banco, onde o jogo permitir. Um ponto de restauração é criado antes." : "Tudo o que acontece aqui fica no parque."));
                options.Add(new PadOption("Como a creche funciona", IconPath: "info"));
                var note = guests.Length == 0 ? "Blissey cuida da creche perto da cabana, e Audino ajuda." : $"Cuidando de {string.Join(", ", guests.Select(g => g.Name))}.";
                var choice = await PadMenu.ShowAsync(_host, "Creche da Blissey", note, options.ToArray());
                if (choice is null) return;
                if (choice == "Pegar o ovo" && egg is { } taken)
                {
                    _scene.Life.TakeEgg();
                    var line = _daycare.TakeEgg(taken.Item1, taken.Item2, _treasures.Add);
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Blissey", line, () => _walking.GetPortrait(242, 0, false, "Joyous"));
                }
                else if (choice.StartsWith("Buscar ", StringComparison.Ordinal) && guests.FirstOrDefault(g => choice == $"Buscar {Named(g)}") is { } leaving)
                {
                    var line = await _daycare.PickUpAsync(leaving);
                    SetGuests();
                    if (_daycare.Egg is null && _scene.Life.Egg is not null) _scene.Life.TakeEgg();
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Blissey", line, () => _walking.GetPortrait(242, 0, false, "Happy"));
                }
                else if (choice == "Deixar um Pokémon aqui")
                {
                    var free = _scene.Residents.Where(r => !_daycare.IsGuest(r.Id)).ToArray();
                    var pick = await PadMenu.ShowAsync(_host, "Quem fica com a Blissey?", null,
                        free.Select(r => new PadOption(Named(r), Detail: string.IsNullOrEmpty(r.Origin) ? r.Source : $"{r.Origin} · {r.TrainerName}")).ToArray());
                    if (free.FirstOrDefault(r => Named(r) == pick) is { } staying)
                    {
                        var line = _daycare.CheckIn(staying);
                        SetGuests();
                        await PokeparkCardOverlay.ShowDialogueAsync(_host, "Blissey", line, () => _walking.GetPortrait(242, 0, false, "Happy"));
                    }
                }
                else if (choice.StartsWith("Gravar de volta nos saves", StringComparison.Ordinal))
                    _park.SaveSettings(_park.Settings with { DaycareWritesBack = !writes });
                else if (choice == "Como a creche funciona")
                    await PokeparkCardOverlay.ShowDialogueAsync(_host, "Creche da Blissey",
                        "Deixe até quatro Pokémon com a Blissey. Cada um segue a creche do próprio jogo: até Sun e Moon, eles ganham EXP a cada hora que ficam; os Berçários de Sword e Shield, Brilliant Diamond e Shining Pearl, e Scarlet e Violet não treinam, mas podem dar ovos; Let's Go e Legends não têm creche. Dois hóspedes que poderiam cruzar no jogo deles podem deixar um ovo, que a Blissey guarda até você vir buscar. Com Gravar de volta ligado, a EXP vai para o Pokémon de verdade quando você busca e o ovo vai para o seu Banco. O save ganha um ponto de restauração antes, e nada é gravado se o Pokémon tiver sido alterado desde que você o deixou.",
                        () => _walking.GetPortrait(242, 0, false, "Normal"));
            }
        }
        finally { _menuOpen = false; }
    }

    // A resident's name in a list, with its slot in the park when another has the same name.
    private string Named(ParkPokemon mon) => _scene.Residents.Count(r => r.Name == mon.Name) > 1
        ? $"{mon.Name} ({_scene.Residents.ToList().FindIndex(r => r.Id == mon.Id) + 1})" : mon.Name;

    private static string Stayed(TimeSpan time) => time.TotalHours switch
    {
        < 1 => "menos de uma hora",
        < 24 => $"{Math.Max(1, (int)time.TotalHours)} h",
        _ => $"{(int)time.TotalDays} d {time.Hours} h"
    };

    private Task GoalsAsync()
    {
        var goals = Goals();
        var lines = goals.Goals.Select((goal, i) => goals.Done(i) ? $"{goal.Text}: concluída!" : goal.Count > 1 ? $"{goal.Text}: {goals.Progress[i]} de {goal.Count}." : $"{goal.Text}.");
        var end = goals.Rewarded ? " Tudo pronto por hoje. Novas metas amanhã." : $" Conclua as três para ter uma festa e um achado raro: hoje é {ParkGoals.RewardFor(goals.Day).Name}.";
        return PokeparkCardOverlay.ShowDialogueAsync(_host, "Metas de hoje", string.Join(" ", lines) + end);
    }

    // Who it loves, its best friend and its rival: the species' own feelings plus what happened in the park.
    private string BondsOf(ParkPokemon mon)
    {
        string? friend = null, rival = null; var friendScore = 14; var rivalScore = 14; var love = false; var fear = false;
        foreach (var other in _scene.Residents.Where(r => r.Id != mon.Id))
        {
            var (friendship, rivalry) = _relations.GetValueOrDefault(ParkRelationship.KeyFor(mon.Id, other.Id));
            var feeling = _scene.Life.FeelingOf(mon.Id, other.Id);
            var near = friendship + feeling switch { ParkPairKind.Love => 60, ParkPairKind.Family => 40, ParkPairKind.Friends => 30, _ => 0 };
            var far = rivalry + feeling switch { ParkPairKind.Rivals => 60, ParkPairKind.Fears or ParkPairKind.Hunts => 40, _ => 0 };
            if (near > friendScore) { friendScore = near; friend = other.Name; love = feeling == ParkPairKind.Love; }
            if (far > rivalScore) { rivalScore = far; rival = other.Name; fear = feeling == ParkPairKind.Fears; }
        }
        var parts = new List<string>();
        if (friend is not null) parts.Add(love ? $"apaixonado por {friend}" : $"melhor amigo de {friend}");
        if (rival is not null && rival != friend) parts.Add(fear ? $"com medo de {rival}" : $"rival de {rival}");
        return parts.Count == 0 ? "nenhum laço próximo ainda" : string.Join(" · ", parts);
    }

    private void SaveCare()
    {
        _lastCareSave = _clock.ElapsedMilliseconds;
        if (_scene.Life.Actors.Count > 0) _care.Save(_scene.Life.Snapshot(DateTimeOffset.Now));
        // The park's ongoing stories (Bidoof's dam, Lapras's regulars…), kept across days.
        Preferences.Default.Set(ChronicleKey, _scene.Life.SaveChronicle());
    }

    private const string ChronicleKey = "pkforge.pokepark.chronicle.v1";
    private bool _chronicleLoaded;

    // Moments seen live go to the journal and move the relationships.
    // The daycare is open on the home meadow; its guests, the egg Blissey keeps, and the Pokémon
    // released to the Wilds who may wander back in.
    private readonly Dictionary<string, bool> _breeds = new();

    private static string BreedKey(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

    // The guests, and which pairs of them could leave an egg: found here on the UI thread, since it
    // reads their saves and the Bank, never from inside the park's step.
    private void SetGuests()
    {
        var roster = _scene.Residents.ToDictionary(r => r.Id);
        var guests = _daycare.Stays.Select(s => s.ResidentId).ToArray();
        foreach (var (a, i) in guests.Select((g, i) => (g, i)))
            foreach (var b in guests.Skip(i + 1))
            {
                var key = BreedKey(a, b);
                lock (_breeds) if (_breeds.ContainsKey(key)) continue;
                var can = roster.TryGetValue(a, out var x) && roster.TryGetValue(b, out var y) && _daycare.CanBreed(x, y);
                lock (_breeds) _breeds[key] = can;
            }
        _scene.Life.DaycareGuests = guests;
    }

    private void SetUpDaycareAndWilds()
    {
        var life = _scene.Life;
        var roster = _scene.Residents.ToDictionary(r => r.Id);
        foreach (var stay in _daycare.Stays.Where(s => !roster.ContainsKey(s.ResidentId))) _daycare.Forget(stay.ResidentId);
        life.DaycareOpen = true;
        SetGuests();
        // The park asks while it runs on the drawing thread: it only reads what SetGuests worked out.
        life.CanBreed = (a, b) => { lock (_breeds) return _breeds.GetValueOrDefault(BreedKey(a, b)); };
        if (_daycare.Egg is { } egg && life.Egg is null) life.KeepEgg(egg);
        life.OldFriends = _wilds.Load();
    }

    private void RecordMoments()
    {
        if (_scene.Life.Egg is { } found && _daycare.Egg != found) _daycare.KeepEgg(found);
        var moments = _scene.Life.TakeMoments();
        if (moments.Count == 0) return;
        // Pairs with a wild visitor make no lasting relationship; their own stories still count.
        foreach (var moment in moments.Where(m => m.SecondId.Length == 0 || !ParkLife.IsVisitor(m.SecondId) && !ParkLife.IsVisitor(m.FirstId)))
            if (_scene.Life[moment.FirstId] is { } first)
                _social.Witness(moment, first.Resident.Name, _scene.Life[moment.SecondId]?.Resident.Name ?? "");
        _relations = _social.Relations();
        RefreshRecent();
    }

    private ParkCardPaint.Card CardFor(ParkPokemon mon)
    {
        var actor = _scene.Life[mon.Id];
        var care = actor?.Care ?? new ParkCareState(mon.Id);
        var personality = Personality(mon);
        var activity = actor is null ? "Explorando outra área" : actor.Indoors ? "Dormindo na cabana" : actor.Activity;
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        return new ParkCardPaint.Card(mon.Name, mon.Shiny, personality.Trait, actor?.Mood ?? "Content", activity, personality.Likes,
            [care.Hunger, care.Energy, care.Fun, care.Social], care.Hearts(), types.Contains(ParkType.Fighting), BondRows(mon), AboutRows(mon, actor, care));
    }

    // How it gets on with every other resident, strongest bonds first.
    private IReadOnlyList<ParkCardPaint.BondRow> BondRows(ParkPokemon mon) => _scene.Residents.Where(r => r.Id != mon.Id).Select(other =>
    {
        var (friendship, rivalry) = _relations.GetValueOrDefault(ParkRelationship.KeyFor(mon.Id, other.Id));
        return new ParkCardPaint.BondRow(other.Name, _scene.Life.FeelingOf(mon.Id, other.Id), friendship, rivalry);
    }).OrderByDescending(b => (b.Kind == ParkPairKind.None ? 0 : 100) + Math.Abs(b.Friendship) + b.Rivalry).ToArray();

    // Everything the park knows about it, each with what it means.
    private IReadOnlyList<ParkCardPaint.AboutRow> AboutRows(ParkPokemon mon, ParkActor? actor, ParkCareState care)
    {
        var rows = new List<ParkCardPaint.AboutRow>();
        if (actor is null) return rows;
        var resident = actor.Resident;
        // Who it is and where it really lives.
        rows.Add(new("Espécie", _park.SpeciesName(mon.Species) + (mon.Form > 0 ? $" (forma {mon.Form})" : "") + (mon.Shiny ? " · shiny" : "")));
        var game = string.Join(" · ", new[] { mon.Origin, mon.GameName, mon.TrainerName.Length > 0 ? $"trainer {mon.TrainerName}" : "" }.Where(s => s.Length > 0));
        if (game.Length > 0) rows.Add(new("Vem de", game));
        rows.Add(new("Mora em", mon.Source));
        rows.Add(new("Temperamento", ParkLore.Explain(resident.Temperament)));
        var nature = _park.NatureOf(mon);
        var taste = ParkTaste.Describe(ParkTaste.NatureOrGuess(nature, mon.Id), care.TasteKnown);
        rows.Add(new("Natureza", nature is { } n ? $"{_park.NatureName(n)} · {taste}" : $"desconhecida até o save dele ser aberto · {taste}"));
        foreach (var (habit, meaning) in ParkLore.Habits(resident.Species, resident.Types, ParkLife.IsTroll(resident)).Take(2))
            rows.Add(new(habit, meaning));
        if (ParkLore.TouchWarning(resident.Species, resident.Types, resident.Temperament) is { } touch) rows.Add(new("Toque", touch));
        if (care.SparCount > 0) rows.Add(new("Treino", $"seu recorde: {care.SparBest} de {ParkLife.SparRounds} bloqueados em {care.SparCount} sessões"));
        return rows;
    }

    // Takes the player to the resident's own slot: in the Bank, or in the boxes of the save it came
    // from when that save is the one open (the park only ever holds a copy).
    private async Task FindInBoxAsync(ParkPokemon mon)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services is null) return;
        var parts = mon.Id.Split(':');
        if (parts[0] == "bank" && Guid.TryParse(parts.ElementAtOrDefault(1), out var id))
        {
            var bank = services.GetRequiredService<IBankService>();
            if (bank.GetAll().FirstOrDefault(e => e.Id == id) is not { } entry)
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, $"{mon.Name} não está mais no Banco. Agora só a cópia dele mora aqui.");
                return;
            }
            var page = services.GetRequiredService<BankPage>();
            page.Reveal(entry.Box, entry.Slot);
            await Navigation.PushAsync(page);
            return;
        }
        if (parts[0] == "save" && parts.Length >= 4 && int.TryParse(parts[2], out var box) && int.TryParse(parts[3], out var slot))
        {
            var saves = services.GetRequiredService<ISaveSessionService>();
            var document = saves.Current?.Document;
            var hash = document is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(document.DocumentId)))[..16];
            var where = box < 0 ? $"equipe, slot {slot + 1}" : $"box {box + 1}, slot {slot + 1}";
            if (hash != parts[1])
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name,
                    $"{mon.Name} veio de {(mon.Origin.Length > 0 ? mon.Origin : "outro save")}. Abra esse save para encontrá-lo: {where}.");
                return;
            }
            var boxes = services.GetRequiredService<BoxBrowserPage>();
            await Navigation.PushAsync(boxes);
            services.GetRequiredService<PKForge.App.ViewModels.BoxBrowserViewModel>().JumpTo(box, slot);
            return;
        }
        await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, $"{mon.Name} mora em {mon.Source}.");
    }

    private SKBitmap? PortraitFor(ParkPokemon mon) =>
        _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, ParkLife.PortraitFor(_scene.Life[mon.Id]?.Mood ?? "Content"));

    private async Task DetailsAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            if (_scene.Residents.Count == 0)
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "Um prado vazio", "Convide um Pokémon pelo menu de ações da box do save ou do Banco com Enviar para o Poképark. Depois toque nele aqui para passarem um tempo juntos.");
                return;
            }
            var mon = _scene.Residents[_selected];
            _scene.CoveredBottom = PokeparkCardOverlay.CoveredHeight(_canvas.CanvasSize.Width);
            var action = await PokeparkCardOverlay.ShowCardAsync(_host, () => CardFor(mon), () => PortraitFor(mon));
            if (action is null) return;
            if (action == "find")
            {
                _menuOpen = false;
                await FindInBoxAsync(mon);
                return;
            }
            if (action == "leave")
            {
                if (await PadMenu.ConfirmAsync(_host, "Sair do parque?", $"Tirar {mon.Name} deste prado? O original continua na box dele. Você pode convidá-lo de novo quando quiser.", "Sair do parque"))
                { _park.RemoveVisitor(mon.Id); _care.Remove(mon.Id); _daycare.Forget(mon.Id); Reload(); }
                return;
            }
            if (action.StartsWith("snack:", StringComparison.Ordinal) && int.TryParse(action[6..], out var flavor))
            {
                await FeedAsync(mon, flavor);
                return;
            }
            if (action.StartsWith("game:", StringComparison.Ordinal))
            {
                await PlayAsync(mon, action[5..]);
                return;
            }
            var state = _scene.GetResidentState(_selected, _clock.ElapsedMilliseconds);
            var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
            var nearby = _scene.Residents.Where((_, i) => i != _selected)
                .OrderBy(_ => Random.Shared.Next()).FirstOrDefault()?.Name;
            var dialogue = ParkDialogueGenerator.Generate(new ParkDialogueContext(
                mon.Species, mon.Name, types, _scene.Habitat,
                state.Activity, state.Mood, ParkLife.PersonalityFor(mon.Id), action, nearby),
                _narrativeMemory.Recent(mon.Id));
            _narrativeMemory.Remember(mon.Id, dialogue.Id);
            var reply = _scene.Interact(_selected, _clock.ElapsedMilliseconds, action);
            if (reply.Accepted && action == "talk") Deed(ParkDeed.Talked, mon.Id);
            // The Pokémon acts it out above while its line shows below: its own line when it has
            // one (a refusal, a species habit), the usual dialogue otherwise.
            var emotion = reply.Line is null ? action switch { "relax" => "Normal", _ => "Happy" } : reply.Emotion;
            await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, reply.Line ?? dialogue.Text,
                () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, emotion));
            SaveCare();
        }
        finally { _menuOpen = false; _scene.CoveredBottom = 0; }
    }

    // A berry from the player: its nature decides whether it is a favorite or a face-pull.
    private async Task FeedAsync(ParkPokemon mon, int flavor)
    {
        var nature = ParkTaste.NatureOrGuess(_park.NatureOf(mon), mon.Id);
        var reply = _scene.Life.Feed(mon.Id, flavor, nature);
        if (reply.Accepted && reply.Taste > 0) Deed(ParkDeed.LovedBerry, mon.Id);
        var berry = ParkTaste.Berries[Math.Clamp(flavor, 0, 4)];
        var taste = NatureFacts.FlavorNames[Math.Clamp(flavor, 0, 4)].ToLowerInvariant();
        var (line, emotion) = reply.Line is { } own ? (own, reply.Emotion) : reply.Taste switch
        {
            > 0 => ($"{mon.Name} devora a {berry} Berry e faz uma dancinha feliz. {char.ToUpperInvariant(taste[0])}{taste[1..]}: é o sabor favorito dele!", "Joyous"),
            < 0 => ($"{mon.Name} dá uma mordida na {berry} Berry e faz careta. Continua educado, mas sabores {taste} não são para ele.", "Sigh"),
            _ => ($"{mon.Name} come a {berry} Berry todo feliz, até a última migalha.", "Happy")
        };
        await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line,
            () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, emotion));
        SaveCare();
    }

    private void FlyToPlace(int delta)
    {
        var place = _scene.FlyToRegion(delta);
        _status.Text = $"{place.Name}  ·  L/R para visitar os lugares do parque, A para seguir um Pokémon de novo";
        if (_active) _canvas.InvalidateSurface();
    }

    private bool WidgetDirty => Volatile.Read(ref _widgetRevision) != Volatile.Read(ref _publishedWidgetRevision);
    private void MarkWidgetDirty() => Interlocked.Increment(ref _widgetRevision);

    private async Task PicnicAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            if (_scene.Residents.Count == 0)
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, "Hora do piquenique", "Piquenique é melhor com companhia! Convide um Pokémon das suas boxes ou do Banco primeiro.");
                return;
            }
            for (var i = 0; i < _scene.Residents.Count; i++)
                _scene.Interact(i, _clock.ElapsedMilliseconds, "snack");
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Hora do piquenique!", "Você divide uma cesta de berries com todo mundo. Sonzinhos felizes se espalham pelo prado. Tem o bastante para todos os moradores!");
        }
        finally { _menuOpen = false; }
    }

    private static (string Trait, string Likes, string Story, string Greeting) Personality(ParkPokemon mon)
    {
        uint hash = 2166136261;
        foreach (var letter in mon.Id) hash = (hash ^ letter) * 16777619;
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);

        // A few residents have difficult days, shaped by what they are and where
        // they feel most at home. Their needs can change through future interactions.
        if (hash % 5 == 0)
        {
            if (types.Contains(ParkType.Water))
                return ("Nadador com saudade de casa", "água calma e margens conhecidas", "Sente falta do lugar de onde veio e vive procurando no lago uma ondulação conhecida.", "olha para o lago e depois te dá um aceno pequeno e hesitante.");
            if (types.Contains(ParkType.Fire))
                return ("Faísca de pavio curto", "pedras quentes e bastante espaço pessoal", "Passou o dia todo irritado e briga com cada folha que cai por perto.", "solta uma baforadinha de fumaça e depois se esforça muito para se acalmar.");
            if (types.Contains(ParkType.Electric))
                return ("Pensador sobrecarregado", "sombra tranquila e companhia gentil", "O prado parece barulhento demais hoje, mesmo quando está tudo perfeitamente quieto.", "se assusta com um farfalhar distante e relaxa devagar quando você fica por perto.");
            if (types.Contains(ParkType.Ghost) || types.Contains(ParkType.Dark))
                return ("Andarilho solitário da noite", "cantos escuros e um amigo de confiança", "Passou boa parte do dia na beira do prado, torcendo para alguém notar.", "mantém distância, mas não vai embora quando você senta ao lado.");
            if (types.Contains(ParkType.Grass) || types.Contains(ParkType.Bug))
                return ("Coraçãozinho murcho", "chuva fina e grama intocada", "O prado parece estranhamente vazio hoje, e nem as flores favoritas conseguem animá-lo.", "abaixa o olhar e depois aceita sua companhia silenciosa.");
            return ("Visitante inquieto", "vozes calmas e um pouco mais de paciência", "Ainda não decidiu se este parque é um lugar onde pode relaxar de verdade.", "te observa com cuidado, esperando para ver se você vai ficar.");
        }

        return (hash % 12) switch
        {
            0 => ("Explorador curioso", "caminhos novos e grama farfalhando", "Vive conferindo se aquela flor cresceu desde ontem.", "inclina a cabeça, curioso sobre o seu dia."),
            1 => ("Sonhador tranquilo", "tardes tranquilas e grama macia", "Achou um cantinho ensolarado e acha que é o melhor lugar do parque.", "te dá um aceno lento e satisfeito."),
            2 => ("Vizinho simpático", "companhia e cumprimentos", "Sempre parece estar por perto quando chega um morador novo.", "chega mais perto para te fazer companhia."),
            3 => ("Andarilho brincalhão", "esticar as pernas e surpresas", "Uma vez seguiu uma folha ao vento até o outro lado do prado.", "dá uma requebradinha alegre."),
            4 => ("Observador pensativo", "observar o lago e cantinhos tranquilos", "Está convencido de que as ondulações do lago estão tentando dizer alguma coisa.", "interrompe o devaneio para olhar para você."),
            5 => ("Apaixonado por flores", "pétalas coloridas e ar fresco", "Tem uma flor favorita, mas parece escolher uma diferente a cada dia.", "parece encantado por você ter passado."),
            6 => ("Pequeno desbravador", "trilhas escondidas e folhas de grama dobradas", "Achou um atalho pelo prado e agora confere se mais alguém descobriu.", "aponta orgulhoso para um caminho ali perto."),
            7 => ("Ouvinte da chuva", "poças novas e batuque suave", "Lembra da última chuva pelos anéis que ela deixou no lago.", "se inclina como se esperasse as nuvens falarem."),
            8 => ("Guardião das berries", "berries maduras e piqueniques fartos", "Plantou uma semente de berry perto da cerca e visita com muita responsabilidade.", "te oferece uma berry e guarda outra para depois."),
            9 => ("Observador de nuvens", "céu aberto e tardes lentas", "Uma vez ficou tanto tempo olhando as nuvens que confundiu uma com um Pokémon muito sonolento.", "olha para cima e aponta para uma nuvem bem convincente."),
            10 => ("Guia do campo", "grama alta e passos conhecidos", "Reconhece cada morador pelo som dos passos no prado.", "te cumprimenta com um aceno de quem já sabe."),
            _ => ("Colecionador de pores do sol", "luz dourada e despedidas tranquilas", "Sempre volta para o mesmo morro para ver o prado ficar cor de âmbar no fim do dia.", "se acomoda ao seu lado por um momento de paz.")
        };
    }

    private static ParkEnvironmentId HomeHabitat(ParkPokemon mon)
    {
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        if (types.Contains(ParkType.Water)) return ParkEnvironmentId.WaterfallLake;
        if (types.Contains(ParkType.Fire) || types.Contains(ParkType.Ground))
            return ParkEnvironmentId.EmberGrove;
        if (types.Contains(ParkType.Flying) || types.Contains(ParkType.Ice) || types.Contains(ParkType.Rock))
            return ParkEnvironmentId.SkySummit;
        return ParkEnvironmentId.Commons;
    }

    private async Task ShowJournalAsync()
    {
        var offline = _offlineJournal.Load().Journal.Select(e => (At: e.Timestamp, e.Text));
        var social = _social.Journal(40).Select(e => (At: e.OccurredAt, e.Text));
        var entries = offline.Concat(social).OrderByDescending(e => e.At).Take(12).ToArray();
        if (entries.Length == 0)
        {
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Diário do parque",
                "As primeiras páginas ainda estão em branco. Deixe seus Pokémon aproveitarem o parque e volte depois.");
            return;
        }
        var labels = entries.Select((entry, i) =>
            $"{i + 1}. {entry.At.ToLocalTime():ddd HH:mm} · {Shorten(entry.Text, 46)}").ToArray();
        var choice = await PadMenu.ShowAsync(_host, "Diário do parque",
            "Um registro de descobertas, amizades, desentendimentos e momentos tranquilos enquanto você estava fora.",
            labels.Concat(["Fechar"]).ToArray());
        var index = Array.IndexOf(labels, choice);
        if (index >= 0)
            await PokeparkCardOverlay.ShowDialogueAsync(_host, "Diário do parque", entries[index].Text);
    }

    private static string Shorten(string text, int length) =>
        text.Length <= length ? text : text[..(length - 1)].TrimEnd() + "…";

    // The PKForge statue's plaque: who made PKForge and the park, what it is built on, and who drew
    // the sprites of the Pokémon living in the park right now.
    private async Task CreditsAsync()
    {
        if (_menuOpen) return;
        _menuOpen = true;
        try
        {
            var names = await _walking.CreditNamesAsync();
            var sprites = new List<ParkCreditsPaint.Entry>();
            foreach (var mon in _scene.Residents.DistinctBy(p => (p.Species, p.Form, p.Shiny)))
            {
                var artists = PokeparkSpriteService.Artists(_walking.GetAttribution(mon.Species, mon.Form, mon.Shiny), names);
                sprites.Add(new(_park.SpeciesName(mon.Species) + (mon.Shiny ? " (shiny)" : ""),
                    artists.Count > 0 ? string.Join(", ", artists) : "sprite do PKHeX, até o sprite animado carregar",
                    (mon.Species, mon.Form, mon.Shiny)));
            }
            sprites.Add(new("PMD SpriteCollab", "a comunidade que desenha os sprites do Mystery Dungeon (CC BY-NC 4.0); sprites originais da CHUNSOFT"));
            ParkCreditsPaint.Section[] sections =
            [
                new("Feito por", [new("sofianeelhor", "o PKForge e o Poképark")]),
                new("Arte", [new("spritedmistery", "o mapa do Poképark, o logo do PKForge e esta estátua, o visual do app, suas cores e seus botões"),
                    new("AlchemyBats", "o tileset sobre o qual o parque é construído")]),
                new("No parque agora", sprites),
                new("Construído com", [
                    new("PKHeX", "Kaphotics e todos os contribuidores: o motor sobre o qual tudo roda, e os sprites dele"),
                    new("PKHeX-Plugins", "santacrab2 e contribuidores: Auto Legality Mod, o legalizador offline"),
                    new("PKSM", "FlagBrew: a estética pixel em que o visual do app se baseia"),
                    new("Pokémon Showdown", "Smogon e os artistas dos projetos de sprites dele: sprites e ícones de box"),
                    new("PokeAPI", "a arte dos itens e as linhas evolutivas"),
                    new("SteamGridDB", "a comunidade dele: a arte da biblioteca de jogos"),
                    new("game-icons.net", "Lorc, Delapouite, Guard13007, Carl Olsen e outros: símbolos"),
                    new("Bulbagarden Archives", "os sprites de Pokérus"),
                    new("BlupBlurp, Team Lumi", "os dados do Luminescent Platinum"),
                    new("G0LD, Ruki Studios", "os dados do GS Chronicles"),
                ]),
                new("Pokémon", [new("Nintendo, Creatures Inc., GAME FREAK inc.", "Pokémon e nomes de Pokémon ©")]),
            ];
            await PokeparkCreditsOverlay.ShowAsync(_host, sections, _walking);
        }
        finally { _menuOpen = false; }
    }

    // The PKForge statue in the middle of the home park (art pixels): tapping it shows the credits.
    private static readonly SKRect StatueArea = new(482, 845, 526, 902);
    // The snowman someone built at the edge of the lava lake, on the volcano.
    private static readonly SKRect SnowmanArea = new(170, 2, 198, 34);

    // Touch: drag or fling to look around, pinch to zoom, tap a Pokémon to meet it,
    // double-tap the grass to go back to following the selected one.
    private const float TapSlop = 24;
    private readonly Dictionary<long, SKPoint> _touches = new();
    private SKPoint _touchStart, _pinchMiddle;
    private long _touchStartTime, _lastMoveTime, _lastTapTime;
    private bool _dragging, _longPressed;
    private string? _throwFor;
    private const long LongPress = 450;
    private float _pinchSpread, _speedX, _speedY;

    private void OnTouch(SKTouchEventArgs e)
    {
        var now = _clock.ElapsedMilliseconds;
        var viewport = _scene.Viewport;
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _touches[e.Id] = e.Location;
                // A finger on the park takes the camera back.
                if (!(Offering && e.Location.Y < _canvas.CanvasSize.Height * .16f)) StopSpectating();
                if (_touches.Count == 1)
                {
                    viewport.Stop();
                    _touchStart = e.Location; _touchStartTime = _lastMoveTime = now;
                    _dragging = false; _longPressed = false; _speedX = _speedY = 0;
                }
                else if (_touches.Count == 2)
                {
                    _dragging = true;
                    BeginPinch();
                }
                break;
            case SKTouchAction.Moved:
                if (!_touches.TryGetValue(e.Id, out var last)) break;
                _touches[e.Id] = e.Location;
                // Brushing: a finger moving over the Pokémon brushes it instead of moving the map.
                if (_touches.Count == 1 && Brushing(last, e.Location)) { _dragging = true; break; }
                if (_touches.Count >= 2)
                {
                    var (middle, spread) = Pinch();
                    viewport.Drag(middle.X - _pinchMiddle.X, middle.Y - _pinchMiddle.Y);
                    if (_pinchSpread > 0) viewport.Pinch(spread / _pinchSpread, middle.X, middle.Y);
                    _pinchMiddle = middle;
                    break;
                }
                if (!_dragging && SKPoint.Distance(e.Location, _touchStart) < TapSlop) break;
                _dragging = true;
                float dx = e.Location.X - last.X, dy = e.Location.Y - last.Y;
                viewport.Drag(dx, dy);
                var dt = Math.Max(1, now - _lastMoveTime);
                _speedX = _speedX * .3f + dx / dt * .7f; _speedY = _speedY * .3f + dy / dt * .7f;
                _lastMoveTime = now;
                break;
            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                if (!_touches.Remove(e.Id)) break;
                viewport.EndPinch();
                if (_touches.Count == 1) { _speedX = _speedY = 0; break; }
                if (_touches.Count > 1) { BeginPinch(); break; }
                if (e.ActionType == SKTouchAction.Cancelled) break;
                if (_longPressed) break;
                if (_dragging)
                {
                    // Only a finger still moving when it lifts throws the map.
                    if (now - _lastMoveTime < 80) viewport.Fling(_speedX, _speedY);
                }
                else if (now - _touchStartTime < 500) Tap(e.Location, now);
                break;
        }
    }

    private void BeginPinch()
    {
        _scene.Viewport.BeginPinch();
        (_pinchMiddle, _pinchSpread) = Pinch();
    }

    private (SKPoint Middle, float Spread) Pinch()
    {
        var points = _touches.Values.Take(2).ToArray();
        return (new SKPoint((points[0].X + points[1].X) / 2, (points[0].Y + points[1].Y) / 2),
            SKPoint.Distance(points[0], points[1]));
    }

    // A finger held still on a Pokémon pats it; held on the grass, it calls the selected Pokémon there.
    private void CheckLongPress(long now)
    {
        if (_touches.Count != 1 || _dragging || _longPressed || _menuOpen || _throwFor is not null || now - _touchStartTime < LongPress) return;
        var x = _touchStart.X / _canvas.CanvasSize.Width;
        var y = _touchStart.Y / _canvas.CanvasSize.Height;
        var index = _scene.HitTest(x, y, now);
        if (index < 0 && (_scene.Residents.Count == 0 || _selected >= _scene.Residents.Count)) return;
        _longPressed = true;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); }
        catch (FeatureNotSupportedException) { }
        if (index < 0)
        {
            var mon = _scene.Residents[_selected];
            var call = _scene.Life.Call(mon.Id, _scene.ScreenToMap(x, y));
            if (call.Accepted) Deed(ParkDeed.Called, mon.Id);
            Say(mon.Id, call);
            return;
        }
        SelectResident(index);
        var id = _scene.Residents[index].Id;
        var pat = _scene.Life.Pet(id);
        if (pat.Accepted) Deed(ParkDeed.Pat, id);
        Say(id, pat);
    }

    // After Play: the next tap (or A) throws the ball for the selected Pokémon.
    private void BeginThrow(ParkPokemon mon)
    {
        _throwFor = mon.Id;
        _status.Text = $"Toque onde jogar a bola para {mon.Name}  ·  A joga para a frente  ·  B guarda";
    }

    private void Throw(ParkPoint? target)
    {
        if (_throwFor is not { } id) return;
        _throwFor = null;
        UpdateStatus();
        if (_scene.Life[id] is not { } actor) return;
        // With the pad, the ball flies a few steps ahead of where the Pokémon looks.
        var ahead = target ?? new ParkPoint(actor.X + MathF.Sin(actor.Facing * MathF.PI / 4) * 70, actor.Y + MathF.Cos(actor.Facing * MathF.PI / 4) * 50);
        var fetch = _scene.Life.Fetch(id, ahead);
        if (fetch.Accepted) Deed(ParkDeed.Fetch, id);
        Say(actor.Resident.Id, fetch);
    }

    // Everything there is to do, page by page, so nothing stays hidden.
    private async Task GuideAsync()
    {
        string[] pages =
        [
            "Toque num Pokémon para abrir o cartão dele. Segure o dedo sobre ele para fazer carinho. Arraste ou incline o analógico esquerdo para olhar em volta; faça pinça, aperte Cima e Baixo ou incline o analógico direito para dar zoom; L e R voam para outro lugar; toque duas vezes na grama ou aperte Esquerda ou Direita para seguir um Pokémon de novo.",
            "Quando algo interessante começa em algum lugar do parque, aparece no topo: aperte X ou toque para ir ver. Aperte − para assistir: a câmera acompanha o que acontece sozinha, até você retomar o controle.",
            "Alguns momentos precisam de você. Fale com um Snorlax dormindo atravessado na ponte para tocar a Poké Flute; fale com um Psyduck em apuros na água para puxá-lo para fora; uma berry significa muito para quem acabou de achar o arbusto vazio.",
            "Aperte X para tirar uma foto do parque, com uma legenda do que está acontecendo. Suas fotos ficam em Opções, Álbum de fotos.",
            "Segure o dedo na grama para chamar o Pokémon que você escolheu. Um que confia em você vem correndo; um mais distante pode fingir que não ouviu.",
            "O cartão tem três páginas, Care, Bonds e About: troque pelas abas ou com L e R. Care mostra as necessidades dele e o que está fazendo; Bonds, como ele se dá com cada outro morador; About, o temperamento, os hábitos e o que tocar nele pode causar.",
            "Talk, Snack e Relax fortalecem o laço de vocês, no máximo cinco corações, um pouco a cada dia. Snack deixa você escolher uma berry: a natureza dele decide se ele adora ou faz careta. Os gostos aparecem como ??? até você descobrir.",
            "Play abre as brincadeiras: jogar a bola, esconde-esconde, corridas, cavar tesouros, escovar e, para os tipos Fighting, treinar. Se ele brinca ou não depende do temperamento dele e do laço de vocês.",
            "Cada morador tem necessidades que caem com o tempo: comida, energia, diversão e amigos. Eles se cuidam enquanto você está fora, mas não perfeitamente. À noite eles dormem, alguns na cabana.",
            "Um que fica com fome e cansado por tempo demais adoece: arrasta os pés e sua, e os curandeiros vêm ver como ele está. Uma berry da sua mão o deixa de pé de novo.",
            "Nas noites secas, um tipo Fire pode acender uma fogueira, e os outros vêm sentar em volta até ela apagar. A chuva apaga o fogo.",
            "Opções, Metas de hoje: três coisinhas para fazer a cada dia. Faça as três e o parque dá uma festa, e você encontra algo raro.",
            "Eles têm vidas próprias: amigos, namorados, rivais jurados, encrenqueiros que roubam e passam rasteira, e alguém que enfrenta eles. O diário conta o que aconteceu; a tela de baixo acompanha o que você escolheu.",
            "Opções: Hora e clima segue o relógio real e o clima do dia, ou deixa você escolher. Tesouros lista o que foi desenterrado. De vez em quando aparecem Pokémon selvagens: toque num para dar oi.",
        ];
        for (var i = 0; i < pages.Length; i++)
            await PokeparkCardOverlay.ShowDialogueAsync(_host, $"Como jogar  {i + 1}/{pages.Length}", pages[i]);
    }

    // The player picks: the real clock and the day's weather, or a time and a sky of their own.
    private const string SkyKey = "pkforge.pokepark.sky.v1";

    private void LoadSky()
    {
        var saved = Preferences.Default.Get(SkyKey, "auto|auto").Split('|');
        _scene.PhaseOverride = saved.Length > 0 && Enum.TryParse<ParkDayPhase>(saved[0], out var phase) ? phase : null;
        _scene.WeatherOverride = saved.Length > 1 && Enum.TryParse<ParkWeather>(saved[1], out var weather) ? weather : null;
    }

    private async Task SkyAsync()
    {
        var phase = await PadMenu.ShowAsync(_host, "Hora do dia",
            "Automático segue o seu relógio: manhãs, tardes e noites chegam como lá fora. Ou escolha uma e o parque fica nela.",
            "Automático", "Manhã", "Dia", "Fim de tarde", "Noite");
        if (phase is null) return;
        var weather = await PadMenu.ShowAsync(_host, "Clima",
            "Automático muda o céu algumas vezes por dia. Ou escolha um e ele fica até você mudar.",
            "Automático", "Ensolarado", "Nublado", "Chuva", "Tempestade");
        if (weather is null) return;
        Preferences.Default.Set(SkyKey, $"{(phase == "Automatic" ? "auto" : phase)}|{(weather == "Automatic" ? "auto" : weather)}");
        LoadSky();
    }

    // ── Games ────────────────────────────────────────────────────────────────

    private string? _digFor;
    private int _seekCursor = -1;
    private bool _gameResultShowing;

    private async Task PlayAsync(ParkPokemon mon, string game)
    {
        switch (game)
        {
            case "fetch": BeginThrow(mon); return;
            case "dig":
                _digFor = mon.Id;
                _status.Text = $"Toque onde {mon.Name} deve cavar  ·  A cava aqui mesmo  ·  B muda de ideia";
                return;
        }
        var kind = game switch { "hide" => ParkGameKind.HideAndSeek, "race" => ParkGameKind.Race, "spar" => ParkGameKind.Spar, _ => ParkGameKind.Brush };
        var reply = _scene.Life.StartGame(kind, mon.Id);
        if (reply.Accepted && _scene.Life.Game is { } started && kind is ParkGameKind.HideAndSeek or ParkGameKind.Race)
        {
            var place = kind == ParkGameKind.Race ? ParkMap.Park.RegionAt(ParkMap.Park.AtHome(168, 100).X, ParkMap.Park.AtHome(168, 100).Y)
                : ParkMap.Park.RegionAt(started.Spot.X, started.Spot.Y);
            _scene.Viewport.ShowAll(place.Center.X, place.Center.Y);
        }
        _seekCursor = -1;
        _scene.SeekCursor = null;
        if (reply.Line is { } line)
            await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line, () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, reply.Emotion));
    }

    private void Dig(ParkPoint? target)
    {
        if (_digFor is not { } id) return;
        _digFor = null;
        UpdateStatus();
        if (_scene.Life[id] is not { } actor) return;
        Say(id, _scene.Life.StartDig(id, target ?? new ParkPoint(actor.X, actor.Y + 10)));
    }

    // A tap during a game: look for the hider, cheer a racer, brush, block a strike.
    private bool PlayTap(ParkPoint point, SKPoint location, long now)
    {
        var life = _scene.Life;
        switch (life.Game)
        {
            case { Kind: ParkGameKind.HideAndSeek, Stage: ParkGameStage.Playing } hide:
                if (life.Seek(point)) Deed(ParkDeed.Found, hide.HostId);
                return true;
            case { Kind: ParkGameKind.Race, Stage: ParkGameStage.Playing or ParkGameStage.Countdown } race:
                var index = _scene.HitTest(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height, now);
                if (index >= 0 && race.Players.Contains(_scene.Residents[index].Id)) life.Cheer(_scene.Residents[index].Id);
                return true;
            case { Kind: ParkGameKind.Brush, Stage: ParkGameStage.Playing }:
                life.Brush(12);
                return true;
            case { Kind: ParkGameKind.Spar, Stage: ParkGameStage.Playing }:
                life.Block();
                return true;
            case { Stage: not ParkGameStage.Over }:
                return true;
        }
        return false;
    }

    private void PlayPad()
    {
        var life = _scene.Life;
        switch (life.Game)
        {
            case { Kind: ParkGameKind.HideAndSeek, Stage: ParkGameStage.Playing } hide when _scene.SeekCursor is { } spot:
                if (life.Seek(spot)) Deed(ParkDeed.Found, hide.HostId);
                break;
            case { Kind: ParkGameKind.HideAndSeek }: MoveSeekCursor(1); break;
            case { Kind: ParkGameKind.Race } race: life.Cheer(race.HostId); break;
            case { Kind: ParkGameKind.Brush }: life.Brush(25); break;
            case { Kind: ParkGameKind.Spar }: life.Block(); break;
        }
    }

    // With the pad, hide and seek steps through the places one could hide.
    private void MoveSeekCursor(int delta)
    {
        if (_scene.Life.Game is not { Kind: ParkGameKind.HideAndSeek } game) return;
        var place = ParkMap.Park.RegionAt(game.Spot.X, game.Spot.Y);
        var spots = ParkMap.Park.Spots.Where(s => s.Kind == ParkSpotKind.Hide && place.Contains(s.X, s.Y)).ToArray();
        if (spots.Length == 0) return;
        _seekCursor = ((_seekCursor + delta) % spots.Length + spots.Length) % spots.Length;
        _scene.SeekCursor = new ParkPoint(spots[_seekCursor].X, spots[_seekCursor].Y);
        _scene.Viewport.Follow(spots[_seekCursor].X, spots[_seekCursor].Y);
    }

    private bool Brushing(SKPoint from, SKPoint to)
    {
        if (_scene.Life.Game is not { Kind: ParkGameKind.Brush, Stage: ParkGameStage.Playing } game || _scene.Life[game.HostId] is not { } host) return false;
        var a = _scene.ScreenToMap(from.X / _canvas.CanvasSize.Width, from.Y / _canvas.CanvasSize.Height);
        var b = _scene.ScreenToMap(to.X / _canvas.CanvasSize.Width, to.Y / _canvas.CanvasSize.Height);
        if (MathF.Abs(b.X - host.X) > 18 || b.Y > host.Y + 6 || b.Y < host.Y - 34) return false;
        _scene.Life.Brush(MathF.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)));
        return true;
    }

    private void QuitGame()
    {
        _scene.Life.EndGame();
        _scene.SeekCursor = null;
        _scene.Viewport.Reset();
    }

    // A finished game shows its result once, then ends; dug-up treasure joins the collection.
    private void CheckGameOver()
    {
        if (_gameResultShowing || _menuOpen || _scene.Life.Game is not { Stage: ParkGameStage.Over, Result: { } result } game) return;
        _gameResultShowing = true;
        if (game.Treasure is { } treasure) _treasures.Add(treasure);
        if (ParkGoals.DeedOf(game) is { } deed) Deed(deed, game.HostId);
        var host = _scene.Residents.FirstOrDefault(r => r.Id == game.HostId);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            _menuOpen = true;
            try
            {
                await PokeparkCardOverlay.ShowDialogueAsync(_host, host?.Name ?? "Poképark", result,
                    host is null ? null : () => _walking.GetPortrait(host.Species, host.Form, host.Shiny, "Joyous"));
            }
            finally
            {
                _menuOpen = false;
                _gameResultShowing = false;
                QuitGame();
                SaveCare();
            }
        });
    }

    // A reaction with its own line (a refusal, an accident) shows in the dialogue box.
    private void Say(string id, ParkReply reply)
    {
        if (reply.Line is not { } line || _menuOpen || _scene.Residents.FirstOrDefault(r => r.Id == id) is not { } mon) return;
        _menuOpen = true;
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                _scene.CoveredBottom = PokeparkCardOverlay.CoveredHeight(_canvas.CanvasSize.Width);
                await PokeparkCardOverlay.ShowDialogueAsync(_host, mon.Name, line,
                    () => _walking.GetPortrait(mon.Species, mon.Form, mon.Shiny, reply.Emotion));
            }
            finally { _menuOpen = false; _scene.CoveredBottom = 0; }
        });
    }

    private void Tap(SKPoint location, long now)
    {
        var point = _scene.ScreenToMap(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height);
        // The banner at the top: "go see".
        if (Offering && location.Y < _canvas.CanvasSize.Height * .16f) { GoSee(_offered!); return; }
        if (_throwFor is not null) { Throw(point); return; }
        if (_digFor is not null) { Dig(point); return; }
        if (PlayTap(point, location, now)) return;
        // A wild visitor: it may bolt, or leave you a little gift.
        if (_scene.VisitorAt(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height) is { } wild)
        {
            if (ParkLife.IsKeeper(wild)) { if (!_menuOpen) MainThread.BeginInvokeOnMainThread(() => _ = DaycareAsync()); return; }
            var reply = _scene.Life.Befriend(wild);
            if (reply.Gift is { } gift) _treasures.Add(gift);
            var friend = _scene.Life.OldFriendOf(wild);
            if (reply.Line is { } line && !_menuOpen)
            {
                _menuOpen = true;
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await PokeparkCardOverlay.ShowDialogueAsync(_host, _scene.Life[wild]?.Resident.Name ?? "Um Pokémon selvagem", line,
                            friend is null ? null : () => _walking.GetPortrait(friend.Species, friend.Form, friend.Shiny, reply.Emotion));
                    }
                    finally { _menuOpen = false; }
                });
            }
            return;
        }
        var index = _scene.HitTest(location.X / _canvas.CanvasSize.Width, location.Y / _canvas.CanvasSize.Height, now);
        if (index >= 0)
        {
            SelectResident(index);
            _ = DetailsAsync();
            _lastTapTime = 0;
            return;
        }
        // The statue in the middle of the park: who made all this.
        if (StatueArea.Contains(point.X, point.Y)) { MainThread.BeginInvokeOnMainThread(() => _ = CreditsAsync()); return; }
        if (SnowmanArea.Contains(point.X, point.Y)) { MainThread.BeginInvokeOnMainThread(() => _ = SnowmanAsync()); return; }
        if (now - _lastTapTime < 350) { _scene.Viewport.Reset(); _lastTapTime = 0; }
        else _lastTapTime = now;
    }

    // The left stick glides across the map, gently near the middle and faster as it leans;
    // the right stick zooms in (up) and out (down). A Pokémon picked with Left/Right is followed again.
    public void OnStick(float leftX, float leftY, float rightX, float rightY)
    {
        static float Curve(float value) => value * MathF.Abs(value);
        if (leftX != 0 || leftY != 0 || rightY != 0) StopSpectating();
        _scene.Viewport.Stick(Curve(leftX), Curve(leftY), Curve(rightY));
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.B when _throwFor is not null || _digFor is not null: _throwFor = _digFor = null; UpdateStatus(); return true;
            case PadButton.A when _throwFor is not null: Throw(null); return true;
            case PadButton.A when _digFor is not null: Dig(null); return true;
            case PadButton.B when _scene.Life.Game is not null: QuitGame(); return true;
            case PadButton.A when _scene.Life.Game is not null: PlayPad(); return true;
            case PadButton.Left when _scene.Life.Game is { Kind: ParkGameKind.HideAndSeek }: MoveSeekCursor(-1); return true;
            case PadButton.Right when _scene.Life.Game is { Kind: ParkGameKind.HideAndSeek }: MoveSeekCursor(1); return true;
            case PadButton.B: _ = Navigation.PopAsync(); return true;
            case PadButton.X when Offering: GoSee(_offered!); return true;
            case PadButton.Select: ToggleSpectate(); return true;
            case PadButton.X: _ = TakePhotoAsync(); return true;
            case PadButton.A: _ = DetailsAsync(); return true;
            case PadButton.Y: _ = PicnicAsync(); return true;
            case PadButton.L: StopSpectating(); FlyToPlace(-1); return true;
            case PadButton.R: StopSpectating(); FlyToPlace(1); return true;
            case PadButton.Up: _scene.Viewport.Zoom(1); return true;
            case PadButton.Down: _scene.Viewport.Zoom(-1); return true;
            case PadButton.Left: StopSpectating(); SelectResident(_selected - 1); return true;
            case PadButton.Right: StopSpectating(); SelectResident(_selected + 1); return true;
            case PadButton.Start: _ = OptionsAsync(); return true;
            default: return true;
        }
    }
}
