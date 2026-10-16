using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// Background music, the same menu from Home and from the Poképark's options: play/pause, skip,
/// the music of each place (the menus, the park by day, the park by night), the library, order,
/// autostart.
/// </summary>
public static class MusicMenu
{
    private static readonly (MusicPlace Place, string Name, string Detail)[] Places =
    [
        (MusicPlace.Menus, "Menus", "Toca em todo o PKForge, fora do Poképark."),
        (MusicPlace.ParkDay, "Parque de dia", "Toca no Poképark de manhã e à tarde."),
        (MusicPlace.ParkNight, "Parque à noite", "Toca no Poképark no fim de tarde e à noite."),
    ];

    public static async Task ShowAsync(Grid host, Action<string> status)
    {
        var music = IPlatformApplication.Current?.Services.GetService<IMusicPlayer>();
        if (music is null) return;
        var start = 0;

        while (true)
        {
            var order = music.Order == MusicOrder.Shuffle ? "Aleatória" : "Em ordem";
            var auto = music.Autostart ? "LIGADO" : "DESLIGADO";
            var message = music.IsPlaying && music.CurrentIndex is { } index && index < music.Library.Count
                ? $"Tocando: {music.Library[index].Title}"
                : music.IsOn && music.Library.Count > 0 ? $"Silêncio aqui ({PlaceName(music.Place)})."
                : $"Biblioteca: {music.Library.Count} faixa(s)";
            if (music is Platforms.Android.MusicPlayer { LastError: { } error })
                message += $"\nÚltimo erro: {error}";
            PadOption[] options =
            [
                new(music.IsOn ? "Pausar" : "Tocar", IconPath: music.IsOn ? "pause" : "play"),
                new("Pular para a próxima faixa", IconPath: "skip"),
                .. Places.Select(p => new PadOption($"{p.Name}: {MusicPlaces.Describe(p.Place, music.SourceFor(p.Place), music.Library)}",
                    IconPath: p.Place == MusicPlace.Menus ? "music" : "park", Detail: p.Detail)),
                new($"Adicionar músicas ({music.Library.Count})", IconPath: "folder", Detail: "Escolha arquivos de áudio no seu aparelho para adicionar à biblioteca."),
                new(music.Library.Count > 0 ? "Limpar biblioteca" : "-", IconPath: "delete",
                    Detail: music.Library.Count > 0 ? "Esvazia a lista. Seus arquivos de áudio continuam no aparelho." : null),
                new($"Ordem: {order}", IconPath: "shuffle", Detail: "Toque as faixas em ordem ou aleatoriamente."),
                new($"Iniciar sozinha: {auto}", IconPath: "settings", Detail: "LIGADO inicia a música quando o PKForge abre."),
            ];
            var choice = await PadMenu.ShowAtAsync(host, "Música de fundo", message, start, options);
            start = Math.Max(0, Array.FindIndex(options, o => o.Label == choice));
            if (Places.FirstOrDefault(p => choice?.StartsWith(p.Name + ": ", StringComparison.Ordinal) == true) is { Name: not null } place)
            {
                await PickAsync(host, music, place.Place, status);
                continue;
            }
            switch (choice)
            {
                case "Tocar": music.Play(); break;
                case "Pausar": music.Pause(); break;
                case "Pular para a próxima faixa": music.Skip(); break;
                case var add when add?.StartsWith("Adicionar músicas", StringComparison.Ordinal) == true:
                {
                    var picker = IPlatformApplication.Current?.Services.GetService<IDocumentPicker>();
                    if (picker is null) break;
                    var documents = await picker.PickManyAsync();
                    if (documents.Count == 0) break;
                    var added = music.Add(documents);
                    status(added > 0 ? $"{added} faixa(s) adicionada(s) à biblioteca de músicas." : "Esses arquivos já estão na biblioteca.");
                    break;
                }
                case "Limpar biblioteca":
                    var confirmed = await PadMenu.ConfirmAsync(host, "Limpar a biblioteca de músicas?",
                        "Remove todas as faixas. Seus arquivos de áudio no armazenamento não são alterados.", "Limpar");
                    if (confirmed) music.Clear();
                    break;
                case var o when o?.StartsWith("Ordem: ", StringComparison.Ordinal) == true:
                    music.SetOrder(music.Order == MusicOrder.InOrder ? MusicOrder.Shuffle : MusicOrder.InOrder);
                    break;
                case var a when a?.StartsWith("Iniciar sozinha: ", StringComparison.Ordinal) == true:
                    music.SetAutostart(!music.Autostart);
                    break;
                default:
                    return;
            }
        }
    }

    private static string PlaceName(MusicPlace place) => Places.First(p => p.Place == place).Name.ToLowerInvariant();

    /// <summary>
    /// A place's music: the default (the whole library for the menus, the menus' music for the
    /// park), every track, silence, or the tracks ticked in the list. Ticking a track keeps the
    /// menu open on it, so several can be ticked in a row; B goes back.
    /// </summary>
    private static async Task PickAsync(Grid host, IMusicPlayer music, MusicPlace place, Action<string> status)
    {
        var (_, name, detail) = Places.First(p => p.Place == place);
        var start = 0;
        while (true)
        {
            var source = music.SourceFor(place);
            var library = music.Library;
            var titles = UniqueTitles(library);
            var fallback = place == MusicPlace.Menus ? "Todas as faixas" : "Igual aos menus";
            var ticked = source.Kind == MusicSourceKind.Tracks ? source.Tracks.ToHashSet() : [];
            PadOption Option(string label, bool on, string icon, string? optionDetail = null) =>
                new(label, IconPath: on ? "check" : icon, Detail: optionDetail);
            var options = new List<PadOption>
            {
                Option(fallback, source.Kind == MusicSourceKind.Default || (source.Kind == MusicSourceKind.AllTracks && place == MusicPlace.Menus), "music",
                    place == MusicPlace.Menus ? "A biblioteca inteira." : "Mantém a música dos menus tocando, sem cortar."),
            };
            if (place != MusicPlace.Menus)
                options.Add(Option("Todas as faixas", source.Kind == MusicSourceKind.AllTracks, "music", "A biblioteca inteira."));
            options.Add(Option("Silêncio", source.Kind == MusicSourceKind.Silence, "close", "Sem música aqui."));
            for (var i = 0; i < library.Count; i++)
                options.Add(Option(titles[i], ticked.Contains(library[i].DocumentId), "music"));
            var message = library.Count == 0
                ? $"{detail} Adicione músicas primeiro para escolher faixas."
                : $"{detail} Marque uma ou mais faixas.";
            var choice = await PadMenu.ShowAtAsync(host, $"Música: {name}", message, start, options.ToArray());
            if (choice is null) return;
            start = Math.Max(0, options.FindIndex(o => o.Label == choice));
            var track = Array.IndexOf(titles, choice);
            if (track >= 0)
            {
                var id = library[track].DocumentId;
                music.SetSource(place, MusicSource.Of(ticked.Contains(id) ? ticked.Where(t => t != id) : ticked.Append(id)));
                continue;
            }
            music.SetSource(place, choice switch
            {
                "Silêncio" => MusicSource.Silence,
                "Todas as faixas" when place != MusicPlace.Menus => MusicSource.AllTracks,
                _ => MusicSource.Default,
            });
            status($"{name}: {MusicPlaces.Describe(place, music.SourceFor(place), library)}.");
            return;
        }
    }

    // Menu entries are told apart by their label: two files with one title get a number.
    private static string[] UniqueTitles(IReadOnlyList<MusicTrack> library)
    {
        var seen = new Dictionary<string, int>();
        var titles = new string[library.Count];
        for (var i = 0; i < library.Count; i++)
        {
            var title = library[i].Title;
            seen[title] = seen.GetValueOrDefault(title) + 1;
            titles[i] = seen[title] == 1 && title is not ("Todas as faixas" or "Igual aos menus" or "Silêncio") ? title : $"{title} ({seen[title]})";
        }
        return titles;
    }
}
