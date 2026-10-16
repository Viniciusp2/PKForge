using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Gen 5 Entralink (PKHeX SAV_Misc5): Entralink levels, the Entree Forest filled only from PKHeX's
/// legal Dream World templates, and on Black 2/White 2 the Pass Powers and Funfest missions.
/// </summary>
public static class EntralinkEditor
{
    private const string Areas = "Áreas do Entree Forest";
    private const string Fill = "Encher a floresta com Pokémon legais do Dream World";
    private const string Clear = "Esvaziar a floresta";
    private const string UnlockAreas = "Abrir todas as áreas da floresta";
    private const string Levels = "Níveis do Entralink…";
    private const string Powers = "Pass Powers";
    private const string Missions = "Missões do Funfest";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var state = EntralinkService.GetState(session);
            var detail = $"Nível do White Forest {state.WhiteForestLevel} · Nível da Black City {state.BlackCityLevel}" + Environment.NewLine +
                $"Floresta: {state.VisitorCount} Pokémon · áreas 1-{state.UnlockedAreas} abertas{(state.NinthAreaUnlocked ? " + área 9" : "")}" +
                Environment.NewLine + "Pokémon encontrados no Entree Forest são capturas do Dream World; aqui só é usada a lista legal do Dream World do PKHeX.";
            var blocked = PKForge.App.Services.HardcoreMode.Blocks(SaveAction.EditWorld, out var status);

            var options = new List<PadOption> { new(Areas, IconPath: "tree", Detail: "Veja quais Pokémon esperam em cada área da floresta.") };
            if (!blocked)
            {
                options.Add(new PadOption(Fill, IconPath: "fill", Accent: UiTokens.Green, Detail: "Cada slot recebe um Pokémon aleatório do Dream World; todas as áreas abrem."));
                options.Add(new PadOption(UnlockAreas, IconPath: "all"));
                options.Add(new PadOption(Clear, IconPath: "clear"));
                options.Add(new PadOption(Levels, IconPath: "level", Detail: "Níveis do White Forest e da Black City, de 0 a 999."));
            }
            if (state.IsB2W2)
            {
                options.Add(new PadOption(Powers, IconPath: "power", Detail: string.Join(" · ", state.PassPowers)));
                options.Add(new PadOption(Missions, IconPath: "check",
                    Detail: $"{state.Missions.Count(m => m.Unlocked)}/{state.Missions.Count} unlocked"));
            }
            var choice = await EditorMenu.ShowAsync(host, "Entralink", blocked ? detail + Environment.NewLine + status : detail, options.ToArray());
            bool ok = true;
            switch (choice)
            {
                case Areas:
                    await ShowAreasAsync(host, state);
                    break;
                case Fill:
                    if (await EditorMenu.ConfirmAsync(host, "Encher a floresta de novo?",
                            "Cada slot é substituído por um Pokémon aleatório da lista do Dream World deste jogo, cada um com um golpe que pode ter lá. Todas as áreas abrem.", "Encher"))
                        ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                            $"Entree Forest preenchido: {EntralinkService.FillForestLegally(s).VisitorCount} Pokémon legais do Dream World.");
                    break;
                case UnlockAreas:
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    {
                        EntralinkService.UnlockAllAreas(s);
                        return "Todas as áreas do Entree Forest estão abertas.";
                    });
                    break;
                case Clear:
                    if (await EditorMenu.ConfirmAsync(host, "Esvaziar a floresta?", "Todos os Pokémon do Entree Forest vão embora.", "Esvaziar"))
                        ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                        {
                            EntralinkService.ClearForest(s);
                            return "Entree Forest esvaziado.";
                        });
                    break;
                case Levels:
                {
                    var white = await StatsPopup.ShowSingleAsync(host, "Nível do White Forest", state.WhiteForestLevel, EntralinkService.MaxLevel, "0 - 999");
                    if (white is not { } w) break;
                    var black = await StatsPopup.ShowSingleAsync(host, "Nível da Black City", state.BlackCityLevel, EntralinkService.MaxLevel, "0 - 999");
                    if (black is not { } b) break;
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    {
                        var after = EntralinkService.SetLevels(s, w, b);
                        return $"Níveis do Entralink: White Forest {after.WhiteForestLevel}, Black City {after.BlackCityLevel}.";
                    });
                    break;
                }
                case Powers:
                    ok = await ShowPowersAsync(host, viewModel, state, blocked);
                    break;
                case Missions:
                    ok = await ShowMissionsAsync(host, viewModel, state, blocked);
                    break;
                default:
                    return;
            }
            if (!ok) return;
        }
    }

    private static Task ShowAreasAsync(Grid host, EntralinkState state)
    {
        var lines = state.Areas.Select(a =>
            $"{a.Name}{(a.Unlocked ? "" : " (locked)")}: {(a.Visitors.Count == 0 ? "empty" : string.Join(", ", a.Visitors.Distinct().Take(8)) + (a.Visitors.Distinct().Count() > 8 ? "…" : ""))}");
        return EditorMenu.ShowAsync(host, "Entree Forest", string.Join(Environment.NewLine, lines), "OK");
    }

    private static async Task<bool> ShowPowersAsync(Grid host, BoxBrowserViewModel viewModel, EntralinkState state, bool blocked)
    {
        var detail = "Pass Powers são bônus do Entralink (encontros, chocar ovos, captura, EXP...) que você pode ligar em campo.";
        if (blocked)
        {
            await EditorMenu.ShowAsync(host, "Pass Powers", detail + Environment.NewLine + string.Join(Environment.NewLine, state.PassPowers), "OK");
            return true;
        }
        var slots = state.PassPowers.Select((p, i) => $"Slot {i + 1} · {p}").ToArray();
        var slotChoice = await EditorMenu.ShowAsync(host, "Pass Powers", detail, slots);
        var slot = Array.IndexOf(slots, slotChoice);
        if (slot < 0) return true;
        var choices = EntralinkService.GetPassPowerChoices();
        var picked = await PickerMenu.ShowAsync(host, $"Slot {slot + 1}",
            choices.Select(c => new PickItem(c.Value, c.Name)).ToArray());
        if (picked is null) return true;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
            $"Slot de Pass Power {slot + 1}: {EntralinkService.SetPassPower(s, slot, picked.Id).PassPowers[slot]}.");
    }

    private static async Task<bool> ShowMissionsAsync(Grid host, BoxBrowserViewModel viewModel, EntralinkState state, bool blocked)
    {
        var lines = string.Join(Environment.NewLine, state.Missions.Select(m => $"{(m.Unlocked ? "✓" : "·")} {m.Name}"));
        if (blocked)
        {
            await EditorMenu.ShowAsync(host, "Missões do Funfest", lines, "OK");
            return true;
        }
        const string unlock = "Desbloquear todas as missões";
        var choice = await EditorMenu.ShowAsync(host, "Missões do Funfest",
            "Desbloquear liga as flags de história que cada missão exige, como o PKHeX faz." + Environment.NewLine + lines,
            new PadOption(unlock, IconPath: "all", Accent: UiTokens.Green));
        if (choice != unlock) return true;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
            $"{EntralinkService.UnlockAllMissions(s).Missions.Count(m => m.Unlocked)} missões do Funfest desbloqueadas.");
    }
}
