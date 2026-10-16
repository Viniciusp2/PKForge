using System.Security.Cryptography;
using System.Text.Json;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>A resident left at the Poképark daycare: since when, and a fingerprint of its data at
/// check-in, so the daycare only ever writes back to the very Pokémon it was given.</summary>
public sealed record ParkStay(string ResidentId, DateTimeOffset Since, string? Fingerprint);

/// <summary>
/// The Poképark daycare against the player's real Pokémon. A stay earns EXP by the hour, as far as
/// the Pokémon's own game allows (<see cref="ParkDaycareService"/>); on pick-up the EXP goes back
/// to its save slot or Bank entry when the player allows it and the Pokémon has not changed since.
/// Eggs from two guests go to the Bank. With write-back off, everything stays in the park.
/// </summary>
public sealed class PokeparkDaycareService(PokeparkService park, IBankService bank, ISaveEngine engine, BoxBrowserViewModel boxes)
{
    private const string Key = "pkforge.pokepark.daycare.v1";
    public const int Capacity = 4;

    /// <summary>EXP per hour of stay: about a walk's worth of steps.</summary>
    public const uint ExpPerHour = 500;

    private sealed record State(ParkStay[] Stays, string[]? Egg);

    private State Load()
    {
        try { return JsonSerializer.Deserialize<State>(Preferences.Default.Get(Key, "")) ?? new([], null); }
        catch (JsonException) { return new([], null); }
    }

    private static void Save(State state) => Preferences.Default.Set(Key, JsonSerializer.Serialize(state));

    public IReadOnlyList<ParkStay> Stays => Load().Stays;

    public bool IsGuest(string id) => Load().Stays.Any(s => s.ResidentId == id);

    /// <summary>The egg Blissey is keeping, if any (the two parents' ids).</summary>
    public (string First, string Second)? Egg => Load().Egg is [var a, var b] ? (a, b) : null;

    public void KeepEgg((string First, string Second)? egg) => Save(Load() with { Egg = egg is { } e ? [e.First, e.Second] : null });

    public static bool WritesBack(PokeparkSettings settings) => settings.DaycareWritesBack;

    /// <summary>Leaves a resident at the daycare.</summary>
    public string CheckIn(ParkPokemon mon)
    {
        var state = Load();
        if (state.Stays.Any(s => s.ResidentId == mon.Id)) return $"{mon.Name} já está na creche.";
        if (state.Stays.Length >= Capacity) return $"A creche está cheia: a Blissey consegue cuidar de {Capacity} por vez.";
        var source = park.SourceOf(mon);
        Save(state with { Stays = [.. state.Stays, new ParkStay(mon.Id, DateTimeOffset.Now, source is null ? null : Fingerprint(source.Data))] });
        var rules = source is null ? null : ParkDaycareService.RulesFor(source.Data, source.Format);
        var note = rules switch
        {
            null => "O save dele não está aberto, então o que ele aprender aqui fica no parque.",
            { Grows: true } => "Ele vai crescer um pouco a cada hora que ficar.",
            { Breeds: true } => "O jogo dele tem um Berçário, não uma creche: ele não vai ganhar EXP, mas pode encontrar um par.",
            _ => "O jogo dele não tem creche, então isto são férias: sem EXP, sem ovos."
        };
        return $"A Blissey pega {mon.Name} pela patinha e mostra o lugar. {note}";
    }

    /// <summary>What a stay has been worth so far, for the card: hours and the level it would reach.</summary>
    public (TimeSpan Time, ParkDaycareGrowth? Growth) Progress(ParkPokemon mon)
    {
        if (Load().Stays.FirstOrDefault(s => s.ResidentId == mon.Id) is not { } stay) return (TimeSpan.Zero, null);
        var time = DateTimeOffset.Now - stay.Since;
        var source = park.SourceOf(mon);
        return (time, source is null ? null : ParkDaycareService.Preview(source.Data, source.Format, Exp(time)));
    }

    /// <summary>Picks a resident up. The EXP goes back to the real Pokémon when allowed.</summary>
    public async Task<string> PickUpAsync(ParkPokemon mon)
    {
        var state = Load();
        if (state.Stays.FirstOrDefault(s => s.ResidentId == mon.Id) is not { } stay) return $"{mon.Name} não está na creche.";
        var time = DateTimeOffset.Now - stay.Since;
        var stayed = Duration(time);
        var message = await GrowAsync(mon, stay, Exp(time), stayed);
        var left = Load();
        Save(left with { Stays = left.Stays.Where(s => s.ResidentId != mon.Id).ToArray(), Egg = left.Egg is { } egg && egg.Contains(mon.Id) ? null : left.Egg });
        return message;
    }

    /// <summary>Called when a resident leaves the park: its stay ends with it, nothing written.</summary>
    public void Forget(string id)
    {
        var state = Load();
        Save(state with { Stays = state.Stays.Where(s => s.ResidentId != id).ToArray(), Egg = state.Egg is { } egg && egg.Contains(id) ? null : state.Egg });
    }

    private async Task<string> GrowAsync(ParkPokemon mon, ParkStay stay, uint exp, string stayed)
    {
        var settings = park.Settings;
        var source = park.SourceOf(mon);
        var hello = $"A Blissey traz {mon.Name} de volta. Ele ficou {stayed}.";
        if (source is null) return $"{hello} O save dele não está aberto, então o tempo que passou aqui fica como lembrança do parque.";
        var rules = ParkDaycareService.RulesFor(source.Data, source.Format);
        if (!rules.Grows) return $"{hello} {(rules.Breeds ? "O jogo dele tem um Berçário, que não treina" : "O jogo dele não tem creche")}, então ele volta como era, só que mais feliz.";
        var preview = ParkDaycareService.Preview(source.Data, source.Format, exp);
        var grew = preview.LevelAfter > preview.LevelBefore ? $"do nível {preview.LevelBefore} ao {preview.LevelAfter}" : $"{preview.ExpGained} de EXP";
        if (preview.ExpGained == 0) return $"{hello} Ele já está o mais forte que pode ficar.";
        if (!WritesBack(settings)) return $"{hello} Ele cresceu {grew} aqui. Gravar de volta está desligado, então isso fica no parque.";
        if (stay.Fingerprint is null || stay.Fingerprint != Fingerprint(source.Data))
            return $"{hello} Ele cresceu {grew} aqui, mas foi alterado no save dele desde que você o deixou, então a creche não mexe nele.";
        if (source.BankId is { } id)
        {
            var entry = bank.GetAll().FirstOrDefault(e => e.Id == id);
            if (entry is null || ParkDaycareService.GrowBytes(source.Data, source.Format, exp) is not { } grown) return $"{hello} Ele cresceu {grew}, mas não foi possível atualizar a entrada dele no Banco.";
            bank.Replace(id, grown.Data, engine.TryDescribeEntity(grown.Data, entry.Info.SourceName, source.Format) ?? entry.Info);
            return $"{hello} Ele cresceu {grew}, e a entrada dele no Banco foi atualizada.";
        }
        var written = await boxes.RunMutationAsync(session =>
        {
            var growth = ParkDaycareService.GrowSlot(session, source.Box, source.Slot, exp);
            return new GenerationOutcome(true, $"{mon.Name} chegou ao nível {growth.LevelAfter} na creche do Poképark.");
        }, source.Slot, refreshSlot: false, changeDescription: $"Creche do Poképark: {mon.Name} cresceu {grew}", action: SaveAction.EditMon);
        return written
            ? $"{hello} Ele cresceu {grew}, e o save dele foi atualizado. Um ponto de restauração foi criado antes."
            : $"{hello} Ele cresceu {grew} aqui, mas não foi possível gravar o save: {boxes.Status}";
    }

    /// <summary>Whether two guests could have an egg, by their real data (false when either can't be read).</summary>
    public bool CanBreed(ParkPokemon a, ParkPokemon b) =>
        park.SourceOf(a) is { } x && park.SourceOf(b) is { } y && ParkDaycareService.CanBreed(x.Data, x.Format, y.Data, y.Format);

    /// <summary>The player takes the egg from Blissey: into the Bank when allowed, otherwise a keepsake.</summary>
    public string TakeEgg(ParkPokemon first, ParkPokemon second, Action<ParkTreasure> keepsake)
    {
        KeepEgg(null);
        var settings = park.Settings;
        if (!WritesBack(settings) || park.SourceOf(first) is not { } a || park.SourceOf(second) is not { } b)
        {
            keepsake(new ParkTreasure($"um ovo da creche de {first.Name} e {second.Name}", 3));
            return WritesBack(settings)
                ? $"A Blissey te entrega o ovo. Os saves deles não estão abertos, então ele vai para a sua mochila como lembrança."
                : $"A Blissey te entrega o ovo. Gravar de volta está desligado, então ele vai para a sua mochila como lembrança.";
        }
        if (ParkDaycareService.MakeEgg(a.Data, a.Format, b.Data, b.Format, out var why) is not { } egg)
        {
            keepsake(new ParkTreasure($"um ovo da creche de {first.Name} e {second.Name}", 3));
            return $"A Blissey te entrega o ovo, mas nenhum ovo legal cabe no jogo deles ({why}). Ele vai para a sua mochila como lembrança.";
        }
        var info = engine.TryDescribeEntity(egg.Data, "Creche do Poképark", egg.Format);
        if (info is null) return "A Blissey te entrega o ovo, mas não foi possível lê-lo. Nada foi adicionado.";
        var entry = bank.Add(egg.Data, info);
        return $"A Blissey te entrega o ovo, quentinho de sol. Ele está no seu Banco, box {entry.Box + 1}, slot {entry.Slot + 1}.";
    }

    private static uint Exp(TimeSpan time) => (uint)Math.Min(uint.MaxValue / 4, Math.Max(0, time.TotalHours) * ExpPerHour);

    private static string Duration(TimeSpan time) => time.TotalHours switch
    {
        < 1 => "menos de uma hora",
        < 2 => "cerca de uma hora",
        < 24 => $"{(int)time.TotalHours} hours",
        < 48 => "um dia",
        _ => $"{(int)time.TotalDays} days"
    };

    private static string Fingerprint(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
