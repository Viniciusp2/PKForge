using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// HeartGold/SoulSilver gadgets: the Apricorn Box (PKHeX SAV_Apricorn) and the Pokéwalker
/// (PKHeX SAV_Misc4 Walker tab). Apricorns are bag contents (<see cref="SaveAction.EditInventory"/>);
/// Pokéwalker steps, watts and courses are world state (<see cref="SaveAction.EditWorld"/>).
/// </summary>
public static class HgssGadgetsEditor
{
    private const string AllApricorns = "Encher todos os Apricorns (99)";
    private const string NoApricorns = "Esvaziar a Apricorn Box";

    private const string Steps = "Definir passos…";
    private const string Watts = "Definir watts…";
    private const string UnlockAll = "Desbloquear todos os percursos";

    public static async Task ShowApricornsAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var apricorns = ApricornService.GetApricorns(session);
            var detail = "Dê Apricorns ao Kurt em Azalea Town e ele faz uma Poké Ball de cada um, pronta no dia seguinte.";
            if (await WorldEventsMenu.BlockedAsync(host, "Apricorn Box",
                    detail + Environment.NewLine + string.Join(Environment.NewLine, apricorns.Select(a => $"{a.Name}: {a.Count}")),
                    SaveAction.EditInventory))
                return;

            var options = apricorns.Select(a => new PadOption($"{a.Name} × {a.Count}", IconPath: "ball", Detail: $"Faz uma {a.BallMade}"))
                .Append(new PadOption(AllApricorns, IconPath: "fill", Accent: UiTokens.Green))
                .Append(new PadOption(NoApricorns, IconPath: "clear"))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Apricorn Box", detail, options);
            if (choice is null) return;
            bool ok;
            if (choice == AllApricorns)
                ok = await WorldEventsMenu.WriteAsync(viewModel, s => { ApricornService.SetAll(s, ApricornService.MaxCount); return "Todos os Apricorns em 99."; }, SaveAction.EditInventory);
            else if (choice == NoApricorns)
                ok = await WorldEventsMenu.WriteAsync(viewModel, s => { ApricornService.SetAll(s, 0); return "Apricorn Box esvaziada."; }, SaveAction.EditInventory);
            else
            {
                var index = Array.FindIndex(options, o => o.Label == choice);
                if (index < 0 || index >= apricorns.Count) continue;
                var apricorn = apricorns[index];
                var count = await StatsPopup.ShowSingleAsync(host, apricorn.Name, apricorn.Count, ApricornService.MaxCount, "0 - 99");
                if (count is not { } value) continue;
                ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    $"{apricorn.Name}: {ApricornService.SetCount(s, apricorn.Index, value)[apricorn.Index].Count}.", SaveAction.EditInventory);
            }
            if (!ok) return;
        }
    }

    public static async Task ShowPokewalkerAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var state = PokewalkerService.GetState(session);
            var unlocked = state.Courses.Count(c => c.Unlocked);
            var detail = $"Passos {state.Steps:N0} · Watts {state.Watts:N0} · {unlocked}/{state.Courses.Count} percursos desbloqueados" +
                Environment.NewLine + "Escolha um percurso para ver o que ele dá.";
            var blocked = PKForge.App.Services.HardcoreMode.Blocks(SaveAction.EditWorld, out _);

            var options = new List<PadOption>();
            if (!blocked)
            {
                options.Add(new PadOption(Steps, IconPath: "records", Detail: "O total de passos que o Pokéwalker contou."));
                options.Add(new PadOption(Watts, IconPath: "stats", Detail: "Os watts guardados no Pokéwalker."));
                options.Add(new PadOption(UnlockAll, IconPath: "all", Accent: UiTokens.Green, Detail: "Abre todos os percursos que o idioma do seu jogo tem."));
            }
            options.AddRange(state.Courses.Select(c => new PadOption(CourseLabel(c),
                Accent: c.Unlocked ? UiTokens.Green : null, IconPath: c.Unlocked ? "check" : "padlock",
                Detail: string.Join(", ", c.Encounters))));
            var choice = await EditorMenu.ShowAsync(host, "Pokéwalker",
                blocked ? detail + Environment.NewLine + PKForge.App.Services.HardcoreMode.StatusFor(SaveAction.EditWorld) : detail,
                options.ToArray());
            if (choice is null) return;

            bool ok;
            switch (choice)
            {
                case Steps:
                case Watts:
                {
                    var isSteps = choice == Steps;
                    var value = await StatsPopup.ShowSingleAsync(host, isSteps ? "Passos" : "Watts",
                        (int)Math.Min(isSteps ? state.Steps : state.Watts, PokewalkerService.MaxCounter), (int)PokewalkerService.MaxCounter,
                        $"0 - {PokewalkerService.MaxCounter:N0}");
                    if (value is not { } v) continue;
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    {
                        var after = PokewalkerService.SetCounters(s, isSteps ? (uint)v : state.Steps, isSteps ? state.Watts : (uint)v);
                        return $"Pokéwalker: {after.Steps:N0} passos, {after.Watts:N0} watts.";
                    });
                    break;
                }
                case UnlockAll:
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                        $"{PokewalkerService.UnlockAll(s).Courses.Count(c => c.Unlocked)} percursos desbloqueados (todos os que o idioma do seu jogo tem).");
                    break;
                default:
                {
                    var course = state.Courses.FirstOrDefault(c => CourseLabel(c) == choice);
                    if (course is null) continue;
                    ok = await ShowCourseAsync(host, viewModel, course, blocked);
                    break;
                }
            }
            if (!ok) return;
        }
    }

    private static async Task<bool> ShowCourseAsync(Grid host, BoxBrowserViewModel viewModel, PokewalkerCourse course, bool blocked)
    {
        var detail = (course.IsSpecial
                ? "Percurso especial: foi distribuído em eventos, não desbloqueado ganhando watts."
                : "Desbloqueado no Pokéwalker ganhando watts.") + Environment.NewLine +
            "Pokémon que você pode capturar aqui:" + Environment.NewLine + string.Join(Environment.NewLine, course.Encounters) +
            (course.AvailableForLanguage ? "" : Environment.NewLine + "O idioma do seu jogo nunca recebeu este percurso.");
        if (blocked || (!course.AvailableForLanguage && !course.Unlocked))
        {
            await EditorMenu.ShowAsync(host, course.Name, detail, "OK");
            return true;
        }
        var toggle = course.Unlocked ? "Bloquear este percurso" : "Desbloquear este percurso";
        var choice = await EditorMenu.ShowAsync(host, course.Name, detail,
            new PadOption(toggle, IconPath: course.Unlocked ? "padlock" : "check", Accent: course.Unlocked ? null : UiTokens.Green));
        if (choice != toggle) return true;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
        {
            PokewalkerService.SetCourse(s, course.Index, !course.Unlocked);
            return $"{course.Name} {(course.Unlocked ? "locked" : "unlocked")}.";
        });
    }

    private static string CourseLabel(PokewalkerCourse c) => $"{c.Index + 1:00} {c.Name}{(c.IsSpecial ? " ★" : "")}";
}
