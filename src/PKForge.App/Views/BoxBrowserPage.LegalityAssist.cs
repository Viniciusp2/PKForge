using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Lot B surfaces of the box browser, kept out of the main page file: the previewed
/// Showdown team import, the multi-select bulk actions and the legality fix report. Every
/// write goes through the view model's RunMutationAsync: Hardcore-guarded,
/// validated, backed up (restore point) and written once.
/// </summary>
public sealed partial class BoxBrowserPage
{
    // ── Bulk actions (multi-select) ──

    private const string BulkLegalize = "Legalizar seleção";
    private const string BulkShiny = "Tornar shiny";
    private const string BulkUnshiny = "Remover shiny";
    private const string BulkHandler = "Tornar-me o treinador atual";
    private const string BulkHeal = "Curar seleção";
    private const string BulkClones = "Apagar clones";

    /// <summary>The bulk entries for the organizer menu, without the ones Hardcore refuses.</summary>
    private IEnumerable<PadOption> BulkOptions()
    {
        var session = _sessionsFor();
        var stock = session is SaveEngineSession && session.SupportsLegalityAnalysis;
        return Menu(
            stock ? Allowed(SaveAction.BatchEdit, new(BulkLegalize, IconPath: "fix", Detail: "Reescreve os Pokémon marcados ilegais para a versão legal mais próxima.")) : null,
            stock ? Allowed(SaveAction.BatchEdit, new(BulkShiny, IconPath: "shiny", Detail: "Só quando existe uma versão shiny legal, a não ser que o modo HaX esteja ligado.")) : null,
            stock ? Allowed(SaveAction.BatchEdit, new(BulkUnshiny, IconPath: "shiny", Detail: "Só quando existe uma versão legal, a não ser que o modo HaX esteja ligado.")) : null,
            stock && session!.Generation >= 6 ? Allowed(SaveAction.BatchEdit, new(BulkHandler, IconPath: "trainer", Detail: "Você vira o treinador atual deles, como depois de uma troca de verdade.")) : null,
            session is SaveEngineSession ? Allowed(SaveAction.BatchEdit, new(BulkHeal, IconPath: "heal", Detail: "HP, status e PP completos na equipe; os Pokémon das boxes recuperam o PP.")) : null,
            Allowed(SaveAction.Release, new(BulkClones, IconPath: "release", Detail: "Mantém um de cada Pokémon idêntico e solta as cópias.")));
    }

    /// <summary>Runs a bulk entry; false when <paramref name="choice"/> is not one.</summary>
    private async Task<bool> TryRunBulkAsync(string? choice)
    {
        switch (choice)
        {
            case BulkLegalize:
            {
                var legalizer = IPlatformApplication.Current?.Services.GetService<ILegalizerService>();
                if (legalizer is null) return true;
                await RunBulkAsync(BulkLegalize, "Cada Pokémon marcado ilegal é reescrito para a versão legal mais próxima; os legais ficam como estão.",
                    "Legalizando…", (s, slots, report) => legalizer.LegalizeSlots(s, slots, report));
                return true;
            }
            case BulkShiny or BulkUnshiny:
            {
                var shiny = choice == BulkShiny;
                var hax = HaXMode.IsOn;
                await RunBulkAsync(choice, (shiny
                        ? "Os Pokémon marcados ficam shiny. "
                        : "Os Pokémon marcados perdem o shiny. ") +
                    (hax ? "Modo HaX: o PID é reescrito mesmo quando isso quebra a legalidade."
                        : "Um Pokémon legal só é alterado quando existe uma versão legal dele (os que têm shiny bloqueado ficam como estão)."),
                    shiny ? "Tornando shiny…" : "Removendo shiny…",
                    (s, slots, report) => BulkBoxActions.SetShiny(s, slots, shiny, keepLegal: !hax, report)
                        .ToOutcome(shiny ? "Ficaram shiny:" : "Shiny removido de"));
                return true;
            }
            case BulkHandler:
                await RunBulkAsync(BulkHandler, "Seu treinador vira o treinador atual dos Pokémon marcados da Gen 6+, como numa troca de verdade (nome, gênero, amizade, memória de troca). Seus próprios Pokémon voltam para você como TO; qualquer passagem que quebraria a legalidade é ignorada.",
                    null, (s, slots, _) => BulkBoxActions.SetCurrentTrainerAsHandler(s, slots).ToOutcome("Passados para você"));
                return true;
            case BulkHeal:
                await RunBulkAsync(BulkHeal, "Os membros da equipe recebem HP, status e PP completos; os Pokémon das boxes recuperam o PP.",
                    null, (s, slots, _) => BulkBoxActions.Heal(s, slots).ToOutcome("Curados"));
                return true;
            case BulkClones:
                await DeleteClonesAsync();
                return true;
            default:
                return false;
        }
    }

    private async Task RunBulkAsync(string verb, string explanation, string? overlayTitle,
        Func<ISaveEngineSession, IReadOnlyList<(int Box, int Slot)>, Action<int, int>, GenerationOutcome> operation)
    {
        if (Denied(SaveAction.BatchEdit)) return;
        var marked = _viewModel.MarkedSlots.ToList();
        if (marked.Count == 0) return;
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, $"{verb}?",
            $"{explanation}\n{marked.Count} marcados. O estado atual continua disponível como ponto de restauração.", verb);
        if (!confirmed) return;

        var overlay = overlayTitle is null ? null : LoadingOverlay.Show(_hostGrid, overlayTitle, $"{marked.Count} Pokémon, uma única gravação, com backup.");
        try
        {
            var ok = await _viewModel.RunMutationAsync(s => operation(s, marked, (done, total) => overlay?.Report(done, total)),
                Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                changeDescription: $"{verb} ({marked.Count} marcados)", action: SaveAction.BatchEdit);
            _viewModel.RefreshAllSlots();
            _canvas.InvalidateSurface();
            if (!ok) await PadMenu.ShowAsync(_hostGrid, verb, _viewModel.Status, "OK");
        }
        finally
        {
            overlay?.Close();
        }
    }

    /// <summary>Clone groups inside the selection (CollectionAudit keys): one of each stays,
    /// the rest are released in one backed-up write. Locked and party slots are never touched.</summary>
    private async Task DeleteClonesAsync()
    {
        if (Denied(SaveAction.Release)) return;
        var session = _sessionsFor();
        var docId = DocumentId;
        if (session is null) return;
        var extras = BulkBoxActions.FindCloneExtras(session, _viewModel.MarkedSlots.ToList());
        var locked = docId is null ? [] : extras
            .Where(e => !Protection.CanRelease(docId, e.Box, e.Slot, session.GetRngInfo(e.Box, e.Slot).Pid)).ToList();
        var targets = extras.Except(locked).ToList();
        if (targets.Count == 0)
        {
            _viewModel.Status = extras.Count == 0 ? "Nenhum clone entre os Pokémon marcados." : "Todos os clones extras estão travados contra soltura.";
            return;
        }
        var lockedNote = locked.Count > 0 ? $" {locked.Count} clone(s) travado(s) ficam." : "";
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Apagar clones?",
            $"{targets.Count} repetido(s) têm a mesma identidade (EC + PID, ou PID + TO + TID) de outro Pokémon marcado. Um de cada é mantido (primeiro os da equipe).{lockedNote} O estado atual continua disponível como ponto de restauração.",
            "Apagar clones");
        if (!confirmed) return;
        await _viewModel.BulkReleaseAsync(targets);
        _canvas.InvalidateSurface();
    }

    // ── Showdown team / box import ──

    private const string ImportThisBox = "Esta box";
    private const string ImportOnward = "Esta box e as seguintes";
    private const string ImportPickBox = "Escolher uma box…";
    private const string ImportParty = "A equipe";

    /// <summary>
    /// Paste many sets, see each one's legality after an offline dry run, choose which to
    /// keep and where they go, then place them all in one backed-up write.
    /// </summary>
    private async Task ImportShowdownPreviewedAsync(string title)
    {
        if (Denied(SaveAction.CreateMon)) return;
        var session = _sessionsFor();
        var legalizer = IPlatformApplication.Current?.Services.GetService<ILegalizerService>();
        if (session is null || legalizer is null) return;
        var text = await TextPopup.ShowAsync(_hostGrid, title,
            "Cole um time ou uma box inteira: um set do Showdown por Pokémon, com uma linha em branco entre os sets.");
        if (string.IsNullOrWhiteSpace(text)) return;

        var hax = HaXMode.IsOn;
        IReadOnlyList<ShowdownSetPreview> previews;
        var overlay = LoadingOverlay.Show(_hostGrid, "Lendo os sets…", "Cada set é legalizado offline como teste; nada é colocado ainda.");
        try
        {
            previews = await Task.Run(() => ShowdownTeamService.Preview(session, legalizer, text, hax,
                (done, total) => overlay.Report(done, total), overlay.Cancellation.Token));
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            _viewModel.Status = error.Message;
            return;
        }
        finally { overlay.Close(); }
        if (previews.Count == 0)
        {
            await PadMenu.ShowAsync(_hostGrid, title, "Nenhum set do Showdown foi encontrado nesse texto.", "OK");
            return;
        }

        var stock = session is SaveEngineSession;
        var chosen = previews.Select(p => p.Generated || !stock).ToArray();
        while (true)
        {
            var count = chosen.Count(c => c);
            var importLabel = $"Importar {count} set(s)…";
            var options = new List<PadOption>();
            if (count > 0) options.Add(new PadOption(importLabel, Glyph: "●", Accent: UiTokens.Green, Detail: "Escolha para onde eles vão em seguida"));
            for (var i = 0; i < previews.Count; i++)
                options.Add(SetRow(previews[i], chosen[i]));
            var choice = await PadMenu.ShowAsync(_hostGrid, $"{title} · {previews.Count} set(s)",
                Note("A: inclui ou deixa de fora um set. A legalidade vem do teste offline."), options.ToArray());
            if (choice is null) return;
            if (choice == importLabel) break;
            var index = options.FindIndex(o => o.Label == choice) - (count > 0 ? 1 : 0);
            if ((uint)index >= (uint)previews.Count) continue;
            if (stock && !previews[index].Generated)
            {
                await PadMenu.ShowAsync(_hostGrid, previews[index].Title, $"{previews[index].Verdict}. Este set não pode ser importado.", "OK");
                continue;
            }
            chosen[index] = !chosen[index];
        }

        var picked = previews.Where((_, i) => chosen[i]).ToList();
        var targets = await PickImportTargetsAsync(session, picked.Count);
        if (targets is null) return;
        if (targets.Count == 0)
        {
            await PadMenu.ShowAsync(_hostGrid, title, "Não há slot vazio ali.", "OK");
            return;
        }
        var fits = Math.Min(picked.Count, targets.Count);
        var confirmed = await PadMenu.ConfirmAsync(_hostGrid, "Importar sets?",
            $"{fits} Pokémon vão para slots vazios" + (picked.Count > targets.Count ? $"; {picked.Count - targets.Count} não cabem e são ignorados" : "") +
            ". Nada é sobrescrito. O estado atual continua disponível como ponto de restauração.", "Importar");
        if (!confirmed) return;

        var place = LoadingOverlay.Show(_hostGrid, "Colocando os sets…", "Uma única gravação, com backup.");
        try
        {
            await _viewModel.RunMutationAsync(s => ShowdownTeamService.Place(s, legalizer, picked, targets, hax),
                Math.Max(0, _viewModel.SelectedSlot), refreshSlot: false,
                changeDescription: $"Importar {fits} set(s) do Showdown", action: SaveAction.CreateMon);
            _viewModel.RefreshAllSlots(includeEmptyPartySlots: targets.Any(t => t.Box == -1));
            _canvas.InvalidateSurface();
        }
        finally { place.Close(); }
    }

    private static PadOption SetRow(ShowdownSetPreview set, bool included)
    {
        var accent = !set.Generated ? set.Candidate.IsEmpty && set.Verdict.StartsWith("Checked", StringComparison.Ordinal) ? UiTokens.Blueprint : UiTokens.Bad
            : set.Legal ? UiTokens.Green : UiTokens.Warn;
        var problems = set.ParseProblems.Count > 0 ? $" · {set.ParseProblems.Count} linha(s) não lida(s): {set.ParseProblems[0]}" : "";
        return new PadOption($"{set.Index + 1}. {set.Title}", Glyph: included ? "●" : "○", Accent: accent,
            Detail: (included ? "" : "Deixado de fora · ") + set.Verdict + problems);
    }

    private async Task<IReadOnlyList<SlotRef>?> PickImportTargetsAsync(ISaveEngineSession session, int needed)
    {
        var box = _viewModel.BoxIndex;
        if (box == -1)
        {
            var party = ShowdownTeamService.EmptySlots(session, -1, onlyThisBox: true);
            var partyChoice = await PadMenu.ShowAsync(_hostGrid, "Importar para onde?", null,
                new PadOption($"{ImportParty} · {party.Count} livres", IconPath: "party"),
                new PadOption($"{ImportPickBox}", IconPath: "box"));
            if (partyChoice is null) return null;
            if (partyChoice.StartsWith(ImportParty, StringComparison.Ordinal)) return party;
            return await PickBoxAsync(session);
        }

        var here = ShowdownTeamService.EmptySlots(session, box, onlyThisBox: true);
        var onward = ShowdownTeamService.EmptySlots(session, box, onlyThisBox: false);
        var choice = await PadMenu.ShowAsync(_hostGrid, "Importar para onde?", $"{needed} set(s) para colocar em slots vazios.",
            new PadOption($"{ImportThisBox} · {here.Count} vazios", IconPath: "box", Detail: $"Só a box {box + 1:00}"),
            new PadOption($"{ImportOnward} · {onward.Count} vazios", IconPath: "all", Detail: $"Da box {box + 1:00} em diante"),
            new PadOption(ImportPickBox, IconPath: "move"));
        if (choice is null) return null;
        if (choice.StartsWith(ImportOnward, StringComparison.Ordinal)) return onward;
        if (choice.StartsWith(ImportThisBox, StringComparison.Ordinal)) return here;
        return await PickBoxAsync(session);
    }

    private async Task<IReadOnlyList<SlotRef>?> PickBoxAsync(ISaveEngineSession session)
    {
        var boxes = Enumerable.Range(0, _viewModel.BoxCount)
            .Select(b => new PadOption($"Box {b + 1:00}", Detail: $"{ShowdownTeamService.EmptySlots(session, b, onlyThisBox: true).Count} empty"))
            .ToArray();
        var target = await PadMenu.ShowAsync(_hostGrid, "Importar para qual box?", null, boxes);
        if (target is null) return null;
        var index = Array.FindIndex(boxes, b => b.Label == target);
        return index < 0 ? null : ShowdownTeamService.EmptySlots(session, index, onlyThisBox: true);
    }

    // ── Legality report with fixes ──

    /// <summary>The grouped report with per-check fixes; Hardcore mode (or a game without
    /// legality tables) keeps the read-only text report instead.</summary>
    private async Task<bool> TryShowLegalityFixesAsync()
    {
        var session = _sessionsFor();
        if (HardcoreMode.IsOn || session is null || !session.SupportsLegalityAnalysis) return false;
        await RunSubEditorAsync(LegalityReportEditor.ShowAsync, "Correções de legalidade aplicadas");
        _canvas.InvalidateSurface();
        return true;
    }
}
