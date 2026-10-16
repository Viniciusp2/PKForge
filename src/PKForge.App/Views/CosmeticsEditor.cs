using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// PKHeX-style cosmetic data that belongs to an individual Pokémon rather than its
/// battle build. The engine gates every group by the entity format, so a control is
/// never shown merely because the currently-open save is from a newer generation.
/// </summary>
public static class CosmeticsEditor
{
    private static readonly string[] ContestNames = ["Cool", "Beauty", "Cute", "Smart", "Tough", "Sheen"];

    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var c = session.GetCosmetics(box, slot);
            var options = new List<PadOption>();
            if (c.Markings.Count != 0) options.Add(new PadOption($"Marcações da box · {c.Markings.Count(m => m.Value != 0)}/{c.Markings.Count}",
                Detail: "O círculo, o coração e outros símbolos mostrados na box."));
            if (c.ContestStats.Count != 0) options.Add(new PadOption($"Atributos de concurso · {c.ContestStats.Sum()}/1530",
                Detail: "Cool, Beauty, Cute, Smart, Tough e Sheen, usados nos concursos."));
            if (c.SupportsSize || c.SupportsScale) options.Add(new PadOption("Tamanho", Detail: "Os valores ocultos por trás da altura, do peso ou do tamanho dele."));
            if (c.SupportsAffection || c.SupportsFullnessEnjoyment) options.Add(new PadOption("Cuidados", Detail: "Afeição, saciedade e diversão."));
            if (HasSpecial(c)) options.Add(new PadOption("Especial", Detail: "Favorito, Dynamax, Alpha e sociabilidade, nos jogos que têm."));

            if (options.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "Visual", "Este formato de Pokémon não tem dados de visual.", "OK");
                return dirty;
            }

            var choice = await EditorMenu.ShowAsync(host, "Visual", null, options.ToArray());
            if (choice is null) return dirty;
            if (choice.StartsWith("Marcações da box", StringComparison.Ordinal)) dirty |= await EditMarkingsAsync(host, session, box, slot);
            else if (choice.StartsWith("Atributos de concurso", StringComparison.Ordinal)) dirty |= await EditContestAsync(host, session, box, slot);
            else if (choice == "Tamanho") dirty |= await EditSizeAsync(host, session, box, slot);
            else if (choice == "Cuidados") dirty |= await EditCareAsync(host, session, box, slot);
            else if (choice == "Especial") dirty |= await EditSpecialAsync(host, session, box, slot);
        }
    }

    private static bool HasSpecial(CosmeticInfo c) => c.SupportsFavorite || c.SupportsDynamax || c.SupportsAlpha || c.SupportsSociability;

    private static async Task<bool> EditMarkingsAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var c = session.GetCosmetics(box, slot);
            var items = c.Markings.Select((m, i) => new PickItem(i, $"{m.Name} · {MarkingValueName(m.Value, m.MaxValue)}")).ToList();
            var picked = await PickerMenu.ShowAsync(host, "Box markings", items);
            if (picked is null) return dirty;

            var marking = c.Markings[picked.Id];
            var next = await PickMarkingValueAsync(host, marking);
            if (next is not { } value || value == marking.Value) continue;
            var values = c.Markings.Select(m => m.Value).ToArray();
            values[picked.Id] = value;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(Markings: values));
            dirty = true;
        }
    }

    private static async Task<int?> PickMarkingValueAsync(Grid host, CosmeticMarking marking)
    {
        if (marking.MaxValue == 1)
        {
            var choice = await EditorMenu.ShowAsync(host, marking.Name, null,
                new PadOption("Desligado"), new PadOption("Ligado"));
            return choice switch { "Desligado" => 0, "Ligado" => 1, _ => null };
        }
        var color = await EditorMenu.ShowAsync(host, marking.Name, null,
            new PadOption("Desligado"), new PadOption("Azul"), new PadOption("Rosa"));
        return color switch { "Desligado" => 0, "Azul" => 1, "Rosa" => 2, _ => null };
    }

    private static async Task<bool> EditContestAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var c = session.GetCosmetics(box, slot);
            var options = ContestNames.Select((name, i) => new PadOption($"{name} · {c.ContestStats[i]}/255")).ToList();
            options.Add(new PadOption("Maximizar tudo", IconPath: "fill"));
            options.Add(new PadOption("Limpar tudo", IconPath: "clear"));
            var choice = await EditorMenu.ShowAsync(host, "Contest stats", null, options.ToArray());
            if (choice is null) return dirty;

            var values = c.ContestStats.ToArray();
            if (choice == "Maximizar tudo") values = [255, 255, 255, 255, 255, 255];
            else if (choice == "Limpar tudo") values = [0, 0, 0, 0, 0, 0];
            else
            {
                var index = Array.FindIndex(ContestNames, name => choice.StartsWith(name, StringComparison.Ordinal));
                if (index < 0) continue;
                var value = await StatsPopup.ShowSingleAsync(host, ContestNames[index], values[index], 255);
                if (value is not { } next || next == values[index]) continue;
                values[index] = next;
            }
            if (values.SequenceEqual(c.ContestStats)) continue;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(ContestStats: values));
            dirty = true;
        }
    }

    private static async Task<bool> EditSizeAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var c = session.GetCosmetics(box, slot);
        var options = new List<PadOption>();
        if (c.SupportsSize)
        {
            options.Add(new PadOption($"Escala de altura · {c.HeightScalar}/255", Detail: "0 é o mais baixo, 255 o mais alto."));
            options.Add(new PadOption($"Escala de peso · {c.WeightScalar}/255", Detail: "0 é o mais leve, 255 o mais pesado."));
        }
        if (c.SupportsScale) options.Add(new PadOption($"Escala · {c.Scale}/255", Detail: "0 é o menor, 255 o maior."));
        var choice = await EditorMenu.ShowAsync(host, "Tamanho", null, options.ToArray());
        if (choice is null) return false;

        var current = choice.StartsWith("Escala de altura", StringComparison.Ordinal) ? c.HeightScalar
            : choice.StartsWith("Escala de peso", StringComparison.Ordinal) ? c.WeightScalar : c.Scale;
        var value = await StatsPopup.ShowSingleAsync(host, choice.Split('·')[0].Trim(), current, 255);
        if (value is not { } next || next == current) return false;
        var edit = choice.StartsWith("Escala de altura", StringComparison.Ordinal) ? new CosmeticEdit(HeightScalar: next)
            : choice.StartsWith("Escala de peso", StringComparison.Ordinal) ? new CosmeticEdit(WeightScalar: next)
            : new CosmeticEdit(Scale: next);
        session.ApplyCosmeticEdit(box, slot, edit);
        return true;
    }

    private static async Task<bool> EditCareAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var c = session.GetCosmetics(box, slot);
        var options = new List<PadOption>();
        if (c.SupportsAffection)
        {
            options.Add(new PadOption($"Afeição do TO · {c.OriginalTrainerAffection}/255", Detail: "Afeição pelo treinador original."));
            options.Add(new PadOption($"Afeição do treinador atual · {c.HandlingTrainerAffection}/255", Detail: "Afeição pelo treinador para quem ele foi trocado."));
        }
        if (c.SupportsFullnessEnjoyment)
        {
            options.Add(new PadOption($"Saciedade · {c.Fullness}/255"));
            options.Add(new PadOption($"Diversão · {c.Enjoyment}/255"));
        }
        var choice = await EditorMenu.ShowAsync(host, "Cuidados", null, options.ToArray());
        if (choice is null) return false;
        var current = choice.StartsWith("Afeição do TO", StringComparison.Ordinal) ? c.OriginalTrainerAffection
            : choice.StartsWith("Afeição do treinador atual", StringComparison.Ordinal) ? c.HandlingTrainerAffection
            : choice.StartsWith("Saciedade", StringComparison.Ordinal) ? c.Fullness : c.Enjoyment;
        var value = await StatsPopup.ShowSingleAsync(host, choice.Split('·')[0].Trim(), current, 255);
        if (value is not { } next || next == current) return false;
        var edit = choice.StartsWith("Afeição do TO", StringComparison.Ordinal) ? new CosmeticEdit(OriginalTrainerAffection: next)
            : choice.StartsWith("Afeição do treinador atual", StringComparison.Ordinal) ? new CosmeticEdit(HandlingTrainerAffection: next)
            : choice.StartsWith("Saciedade", StringComparison.Ordinal) ? new CosmeticEdit(Fullness: next)
            : new CosmeticEdit(Enjoyment: next);
        session.ApplyCosmeticEdit(box, slot, edit);
        return true;
    }

    private static async Task<bool> EditSpecialAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var c = session.GetCosmetics(box, slot);
        var options = new List<PadOption>();
        if (c.SupportsFavorite) options.Add(new PadOption($"Favorito · {(c.IsFavorite ? "ligado" : "desligado")}"));
        if (c.SupportsDynamax)
        {
            options.Add(new PadOption($"Nível Dynamax · {c.DynamaxLevel}/10", Detail: "Cada nível soma HP enquanto ele está Dynamax."));
            options.Add(new PadOption($"Fator Gigantamax · {(c.CanGigantamax ? "ligado" : "desligado")}", Detail: "Permite Gigantamax se a espécie dele tiver essa forma."));
        }
        if (c.SupportsAlpha) options.Add(new PadOption($"Alpha · {(c.IsAlpha ? "ligado" : "desligado")}", Detail: "Marca como Alpha, o tipo maior dos jogos Legends."));
        if (c.SupportsSociability) options.Add(new PadOption($"Sociabilidade · {c.Sociability}"));
        var choice = await EditorMenu.ShowAsync(host, "Especial", null, options.ToArray());
        if (choice is null) return false;

        if (choice.StartsWith("Nível Dynamax", StringComparison.Ordinal))
        {
            var value = await StatsPopup.ShowSingleAsync(host, "Dynamax level", c.DynamaxLevel, 10);
            if (value is not { } next || next == c.DynamaxLevel) return false;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(DynamaxLevel: next));
            return true;
        }
        if (choice.StartsWith("Sociabilidade", StringComparison.Ordinal))
        {
            var value = await StatsPopup.ShowSingleAsync(host, "Sociability", (int)Math.Min(c.Sociability, int.MaxValue), int.MaxValue);
            if (value is not { } next || (uint)next == c.Sociability) return false;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(Sociability: (uint)next));
            return true;
        }
        if (choice.StartsWith("Favorito", StringComparison.Ordinal)) session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(IsFavorite: !c.IsFavorite));
        else if (choice.StartsWith("Fator Gigantamax", StringComparison.Ordinal)) session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(CanGigantamax: !c.CanGigantamax));
        else if (choice.StartsWith("Alpha", StringComparison.Ordinal)) session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(IsAlpha: !c.IsAlpha));
        else return false;
        return true;
    }

    private static string MarkingValueName(int value, int max) => max == 1
        ? (value == 0 ? "off" : "on")
        : value switch { 1 => "blue", 2 => "pink", _ => "off" };
}
