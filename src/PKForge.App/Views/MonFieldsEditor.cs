using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Sub-editors for the per-format Pokémon fields: form, form argument, mint, shiny type and
/// raw PID / EC ("Form &amp; shiny"); OT gender, the handling trainer and memories ("Trainers");
/// and Technical Records. Each menu lists only what the format stores. Like the other
/// sub-editors they write to the session as they go, so callers gate them behind the
/// Hardcore edit door (box editor) or the entry's own Save (bank).
/// </summary>
public static class MonFieldsEditor
{
    private static IGameDataService Data => IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();

    private static string NameOf(IReadOnlyList<string> names, int id) =>
        (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id] : $"#{id}";

    private static string Hex(uint value) => value.ToString("X8");

    private static string Gender(int gender) => gender == 1 ? "Fêmea" : "Macho";

    private static async Task<bool> UnsupportedAsync(Grid host, string title, ISaveEngineSession session)
    {
        if (MonFieldService.IsSupported(session)) return false;
        await EditorMenu.ShowAsync(host, title, "Estes campos só podem ser editados em saves de formato oficial.", "OK");
        return true;
    }

    // ── Form & shiny ──

    public static async Task<bool> FormAndShinyAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        const string title = "Forma e shiny";
        if (await UnsupportedAsync(host, title, session)) return false;
        var data = Data;
        var dirty = false;
        while (true)
        {
            var form = MonFieldService.GetForm(session, box, slot);
            var argument = MonFieldService.GetFormArgument(session, box, slot);
            var mint = MonFieldService.GetStatNature(session, box, slot);
            var shiny = session.Generation >= 3 ? MonFieldService.GetShiny(session, box, slot) : null;

            var options = new List<PadOption>();
            var keys = new Dictionary<string, string>();
            void Add(string key, PadOption option) { options.Add(option); keys[option.Label] = key; }
            if (form is not null)
                Add("form", new PadOption($"Forma · {form.Choices[Math.Min(form.Form, form.Choices.Count - 1)].Name}", IconPath: "evolve"));
            if (argument is not null)
                Add("arg", new PadOption($"{argument.Label} · {ArgumentValue(argument)}", IconPath: "evolve"));
            if (mint is { } statNature)
                Add("mint", new PadOption($"Natureza dos atributos (mint) · {NameOf(data.NatureNames, statNature)}", IconPath: "stats",
                    Detail: "A natureza que os atributos dele seguem; a natureza real continua a mesma."));
            if (shiny is not null)
            {
                Add("shiny", new PadOption($"Shiny · {ShinyLabel(shiny)}", IconPath: "shiny"));
                Add("pid", new PadOption($"PID · {Hex(shiny.Pid)}", IconPath: "hex",
                    Detail: "O valor de personalidade dele, que decide se ele é shiny."));
                if (shiny.EncryptionConstant is { } ec)
                    Add("ec", new PadOption($"Constante de criptografia · {Hex(ec)}", IconPath: "hex",
                        Detail: "Um número oculto que o jogo usa para criptografar os dados dele."));
            }
            if (options.Count == 0)
            {
                await EditorMenu.ShowAsync(host, title, "O formato deste Pokémon não guarda nenhum destes campos.", "OK");
                return dirty;
            }

            var choice = await EditorMenu.ShowAsync(host, title, null, options.ToArray());
            if (choice is null || !keys.TryGetValue(choice, out var key)) return dirty;
            dirty |= key switch
            {
                "form" => await PickFormAsync(host, session, box, slot, form!),
                "arg" => await EditArgumentAsync(host, session, box, slot, argument!),
                "mint" => await PickMintAsync(host, session, box, slot, data),
                "shiny" => await PickShinyAsync(host, session, box, slot, shiny!),
                "pid" => await EditPidAsync(host, session, box, slot, shiny!),
                "ec" => await EditEcAsync(host, session, box, slot, shiny!),
                _ => false,
            };
        }
    }

    private static string ArgumentValue(FormArgumentField a) => a.Kind switch
    {
        FormArgumentKind.Named => a.Value < a.Names.Count ? a.Names[(int)a.Value] : a.Value.ToString(),
        FormArgumentKind.Triple or FormArgumentKind.TripleParty => $"{a.Remain} of {a.Max}",
        _ => a.Value.ToString(),
    };

    private static string ShinyLabel(ShinyField s) => s.Kind switch
    {
        ShinyKind.Square => "Quadrado",
        ShinyKind.Star when s.SupportsKind => "Estrela",
        ShinyKind.Star => "Shiny",
        _ => "Não shiny",
    };

    private static async Task<bool> PickFormAsync(Grid host, ISaveEngineSession session, int box, int slot, FormFieldInfo form)
    {
        var items = form.Choices.Select(c => new PickItem(c.Form, c.Name, null,
                c.BattleOnly ? "Só existe durante a batalha" : !c.InGame ? "Não existe neste jogo" : null)
            { Muted = c.BattleOnly || !c.InGame }).ToList();
        var pick = await PickerMenu.ShowAsync(host, "Forma", items, form.Form,
            filter: new PickerFilter("Neste jogo", "Mostrar tudo", item => !item.Muted, StartOn: !Services.HaXMode.IsOn));
        if (pick is null || pick.Id == form.Form) return false;
        var result = MonFieldService.SetForm(session, box, slot, pick.Id);
        var message = string.Join(" ", result.Notes);
        if (result.Warning is not null) message = message.Length == 0 ? result.Warning : $"{message}\n{result.Warning}";
        if (message.Length > 0) await EditorMenu.ShowAsync(host, pick.Name, message, "OK");
        return result.Changed;
    }

    private static async Task<bool> EditArgumentAsync(Grid host, ISaveEngineSession session, int box, int slot, FormArgumentField argument)
    {
        if (argument.Kind == FormArgumentKind.TripleParty && box != -1)
        {
            await EditorMenu.ShowAsync(host, argument.Label, MonFieldService.PartyOnlyTimer, "OK");
            return false;
        }
        uint? value;
        if (argument.Kind == FormArgumentKind.Named)
        {
            var pick = await PickerMenu.ShowAsync(host, argument.Label,
                argument.Names.Select((name, i) => new PickItem(i, name)).ToList(), (int)argument.Value);
            value = pick is null ? null : (uint)pick.Id;
        }
        else
        {
            var triple = argument.Kind is FormArgumentKind.Triple or FormArgumentKind.TripleParty;
            var current = triple ? argument.Remain : (int)Math.Min(argument.Value, int.MaxValue);
            var typed = await StatsPopup.ShowSingleAsync(host, argument.Label, current, (int)argument.Max, $"0 - {argument.Max}");
            value = typed is { } t ? (uint)t : null;
        }
        if (value is null) return false;
        MonFieldService.SetFormArgument(session, box, slot, value.Value);
        return true;
    }

    private static async Task<bool> PickMintAsync(Grid host, ISaveEngineSession session, int box, int slot, IGameDataService data)
    {
        var current = MonFieldService.GetStatNature(session, box, slot);
        var preview = MonFieldService.PreviewStatNature(session, box, slot);
        var pick = await NaturePicker.ShowAsync(host, data.NatureNames, current, preview, "Natureza dos atributos (mint)");
        if (pick is null || pick.Id == current) return false;
        MonFieldService.SetStatNature(session, box, slot, pick.Id);
        return true;
    }

    private static async Task<bool> PickShinyAsync(Grid host, ISaveEngineSession session, int box, int slot, ShinyField shiny)
    {
        PadOption[] options = shiny.SupportsKind
            ? [new("Estrela", IconPath: "shiny"), new("Quadrado", IconPath: "shiny"), new("Não shiny", IconPath: "clear")]
            : [new("Shiny", IconPath: "shiny"), new("Não shiny", IconPath: "clear")];
        var message = shiny.PidLinked ? "O PID é sorteado de novo: na Gen 3-5 isso também pode mudar a natureza, o gênero ou a habilidade." : null;
        var choice = await EditorMenu.ShowAsync(host, "Shiny", message, options);
        var kind = choice switch
        {
            "Estrela" or "Shiny" => ShinyKind.Star,
            "Quadrado" => ShinyKind.Square,
            "Não shiny" => ShinyKind.None,
            _ => (ShinyKind?)null,
        };
        return kind is { } k && MonFieldService.SetShinyKind(session, box, slot, k);
    }

    private static async Task<bool> EditPidAsync(Grid host, ISaveEngineSession session, int box, int slot, ShinyField shiny)
    {
        if (shiny.PidLinked && !await EditorMenu.ConfirmAsync(host, "PID",
                "Na Gen 3-5, o PID decide o shiny, o gênero e o slot de habilidade (e a natureza na Gen 3/4). Mudá-lo muda tudo isso também.",
                "Editar PID"))
            return false;
        var value = await AskHexAsync(host, "PID", shiny.Pid);
        if (value is null) return false;
        var result = MonFieldService.SetPid(session, box, slot, value.Value);
        if (result.Changes.Count > 0) await EditorMenu.ShowAsync(host, "PID", string.Join("\n", result.Changes), "OK");
        return result.Changed;
    }

    private static async Task<bool> EditEcAsync(Grid host, ISaveEngineSession session, int box, int slot, ShinyField shiny)
    {
        var value = await AskHexAsync(host, "Constante de criptografia", shiny.EncryptionConstant ?? 0);
        if (value is null) return false;
        var result = MonFieldService.SetEncryptionConstant(session, box, slot, value.Value);
        if (result.Changes.Count > 0) await EditorMenu.ShowAsync(host, "Constante de criptografia", string.Join("\n", result.Changes), "OK");
        return result.Changed;
    }

    private static async Task<uint?> AskHexAsync(Grid host, string title, uint current)
    {
        while (true)
        {
            var text = await TextPopup.ShowLineAsync(host, title, "8 dígitos hex", Hex(current));
            if (text is null) return null;
            if (MonFieldService.TryParseHex(text, out var value)) return value;
            await EditorMenu.ShowAsync(host, title, "Digite até 8 dígitos hexadecimais (0-9, A-F).", "OK");
        }
    }

    // ── Trainers & memories ──

    public static async Task<bool> TrainersAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        const string title = "Treinadores";
        if (await UnsupportedAsync(host, title, session)) return false;
        var dirty = false;
        while (true)
        {
            var t = MonFieldService.GetTrainers(session, box, slot);
            var otMemory = MonFieldService.GetMemory(session, box, slot, handler: false);
            var htMemory = MonFieldService.GetMemory(session, box, slot, handler: true);
            var named = t.HandlerName.Length > 0;
            var languages = t.HandlerLanguage is null ? [] : MonFieldService.GetHandlerLanguageChoices(session, box, slot);

            var options = new List<PadOption>();
            var keys = new Dictionary<string, string>();
            void Add(string key, PadOption option) { options.Add(option); keys[option.Label] = key; }
            if (t.OtGender is { } otGender) Add("otg", new PadOption($"Gênero do TO · {Gender(otGender)}", IconPath: "profile", Detail: "O gênero do treinador original dele."));
            if (t.HasHandler)
            {
                Add("otf", new PadOption($"Amizade com o TO · {t.OtFriendship}", IconPath: "heart", Detail: "Amizade com o treinador original."));
                Add("htn", new PadOption($"Treinador atual · {(named ? t.HandlerName : "nenhum")}", IconPath: "profile",
                    Detail: "O último treinador para quem ele foi trocado; vazio significa que nunca foi trocado."));
                if (named)
                {
                    Add("htg", new PadOption($"Gênero do treinador atual · {Gender(t.HandlerGender)}", IconPath: "gender"));
                    if (t.HandlerLanguage is { } language)
                        Add("htl", new PadOption($"Idioma do treinador atual · {languages.FirstOrDefault(l => l.Id == language)?.Name ?? "Nenhum"}", IconPath: "rename"));
                    Add("htf", new PadOption($"Amizade com o treinador atual · {t.HandlerFriendship}", IconPath: "heart", Detail: "Amizade com o treinador para quem ele foi trocado."));
                    Add("cur", new PadOption($"Agora com · {(t.CurrentHandler == 1 ? t.HandlerName : "o TO dele")}", IconPath: "link",
                        Detail: "Quem está com ele agora; a amizade e as memórias seguem esse treinador."));
                }
            }
            if (otMemory is not null) Add("otm", new PadOption("Memória do TO", IconPath: "info", Detail: otMemory.Editable ? otMemory.Text : otMemory.Reason));
            if (htMemory is not null) Add("htm", new PadOption("Memória do treinador atual", IconPath: "info", Detail: htMemory.Editable ? htMemory.Text : htMemory.Reason));
            if (options.Count == 0)
            {
                await EditorMenu.ShowAsync(host, title, "O formato deste Pokémon não guarda detalhes de treinador além do TO.", "OK");
                return dirty;
            }

            var choice = await EditorMenu.ShowAsync(host, title, null, options.ToArray());
            if (choice is null || !keys.TryGetValue(choice, out var key)) return dirty;
            TrainerFieldsEdit? edit = null;
            switch (key)
            {
                case "otg": edit = await AskGenderAsync(host, "Gênero do TO") is { } g1 ? new TrainerFieldsEdit(OtGender: g1) : null; break;
                case "htg": edit = await AskGenderAsync(host, "Gênero do treinador atual") is { } g2 ? new TrainerFieldsEdit(HandlerGender: g2) : null; break;
                case "otf": edit = await StatsPopup.ShowSingleAsync(host, "Amizade com o TO", t.OtFriendship, 255, "0 - 255") is { } f1 ? new TrainerFieldsEdit(OtFriendship: f1) : null; break;
                case "htf": edit = await StatsPopup.ShowSingleAsync(host, "Amizade com o treinador atual", t.HandlerFriendship, 255, "0 - 255") is { } f2 ? new TrainerFieldsEdit(HandlerFriendship: f2) : null; break;
                case "htn":
                    var name = await TextPopup.ShowLineAsync(host, "Treinador atual", $"Até {t.MaxNameLength} caracteres · vazio = nunca trocado", t.HandlerName);
                    if (name is not null && name.Length > t.MaxNameLength)
                        await EditorMenu.ShowAsync(host, "Treinador atual", $"Os nomes têm no máximo {t.MaxNameLength} caracteres neste formato.", "OK");
                    else if (name is not null && name != t.HandlerName) edit = new TrainerFieldsEdit(HandlerName: name);
                    break;
                case "htl":
                    var lang = await PickerMenu.ShowAsync(host, "Idioma do treinador atual", languages.Select(l => new PickItem(l.Id, l.Name)).ToList(), t.HandlerLanguage);
                    edit = lang is null ? null : new TrainerFieldsEdit(HandlerLanguage: lang.Id);
                    break;
                case "cur":
                    var holder = await EditorMenu.ShowAsync(host, "Agora com", "Com quem o Pokémon está agora: a amizade e as memórias seguem esse treinador.",
                        new PadOption("O treinador original", IconPath: "trainer"), new PadOption(t.HandlerName, IconPath: "link"));
                    edit = holder is null ? null : new TrainerFieldsEdit(CurrentHandler: holder == t.HandlerName ? 1 : 0);
                    break;
                case "otm": dirty |= await EditMemoryAsync(host, session, box, slot, otMemory!); break;
                case "htm": dirty |= await EditMemoryAsync(host, session, box, slot, htMemory!); break;
            }
            if (edit is null) continue;
            MonFieldService.ApplyTrainerEdit(session, box, slot, edit);
            dirty = true;
        }
    }

    private static async Task<int?> AskGenderAsync(Grid host, string title)
    {
        var choice = await EditorMenu.ShowAsync(host, title, null,
            new PadOption("Macho", IconPath: "male"), new PadOption("Fêmea", IconPath: "female"));
        return choice switch { "Macho" => 0, "Fêmea" => 1, _ => null };
    }

    /// <summary>Memory → its argument (when it takes one) → intensity → feeling, like PKHeX's memory editor.</summary>
    private static async Task<bool> EditMemoryAsync(Grid host, ISaveEngineSession session, int box, int slot, MemoryField field)
    {
        var who = field.Handler ? "Memória do treinador atual" : "Memória do TO";
        if (!field.Editable)
        {
            await EditorMenu.ShowAsync(host, who, field.Reason, "OK");
            return false;
        }

        var start = MonFieldService.GetMemoryOptions(session, box, slot, field.Handler, field.Memory);
        var memory = await PickerMenu.ShowAsync(host, who,
            start.Memories.Select(m => new PickItem(m.Id, m.Name) { Muted = !m.Legal }).ToList(), field.Memory,
            filter: new PickerFilter("Legal", "Mostrar tudo", item => item.Id == 0 || !item.Muted, StartOn: !Services.HaXMode.IsOn));
        if (memory is null) return false;
        if (memory.Id == 0)
        {
            MonFieldService.SetMemory(session, box, slot, new MemoryEdit(field.Handler, 0, 0, 0, 0));
            return true;
        }

        var options = MonFieldService.GetMemoryOptions(session, box, slot, field.Handler, memory.Id);
        var variable = 0;
        if (options.ArgumentCategory.Length > 0 && options.Arguments.Count > 1)
        {
            var argument = await PickerMenu.ShowAsync(host, options.ArgumentCategory,
                options.Arguments.Select(a => new PickItem(a.Id, a.Name)).ToList(), memory.Id == field.Memory ? field.Variable : null);
            if (argument is null) return false;
            variable = argument.Id;
        }
        var intensity = await PickerMenu.ShowAsync(host, "Intensidade",
            options.Intensities.Where(i => i.Id > 0).Select(i => new PickItem(i.Id, i.Name) { Muted = !i.Legal }).ToList(),
            memory.Id == field.Memory ? field.Intensity : null,
            filter: new PickerFilter("Legal", "Mostrar tudo", item => !item.Muted, StartOn: !Services.HaXMode.IsOn));
        if (intensity is null) return false;
        var feeling = await PickerMenu.ShowAsync(host, "Sentimento",
            options.Feelings.Select(f => new PickItem(f.Id, f.Name) { Muted = !f.Legal }).ToList(),
            memory.Id == field.Memory ? field.Feeling : null,
            filter: new PickerFilter("Legal", "Mostrar tudo", item => !item.Muted, StartOn: !Services.HaXMode.IsOn));
        if (feeling is null) return false;

        MonFieldService.SetMemory(session, box, slot, new MemoryEdit(field.Handler, memory.Id, intensity.Id, feeling.Id, variable));
        var text = MonFieldService.GetMemory(session, box, slot, field.Handler)?.Text;
        if (text is not null) await EditorMenu.ShowAsync(host, who, text, "OK");
        return true;
    }

    // ── Technical Records ──

    private const int GiveAllId = -1, ClearId = -2;

    public static async Task<bool> TechRecordsAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        const string title = "Tech records";
        if (await UnsupportedAsync(host, title, session)) return false;
        var dirty = false;
        int? highlight = null;
        while (true)
        {
            var records = MonFieldService.GetTechRecords(session, box, slot);
            if (records is null)
            {
                await EditorMenu.ShowAsync(host, title, "Technical Records são guardados por Pokémon de Sword/Shield, Scarlet/Violet e Legends: Z-A.", "OK");
                return dirty;
            }
            var learned = records.Count(r => r.Learned);
            var items = new List<PickItem>
            {
                new(GiveAllId, "Dar todos os records aprendíveis", "confirm", "Todo record que esta espécie ou suas evoluções podem aprender"),
                new(ClearId, "Limpar todos os records", "delete"),
            };
            items.AddRange(records.Select(r => new PickItem(r.Index, $"{r.Label} {r.Name}", null, r.Permitted ? null : "Esta espécie não aprende")
            {
                TypeId = r.Type,
                Tag = r.Learned ? "Aprendido" : null,
                TagColor = r.Learned ? UiTokens.Green : null,
                Muted = !r.Permitted,
                Keywords = r.Label,
            }));
            var pick = await PickerMenu.ShowAsync(host, $"{title} · {learned} aprendidos", items, highlight,
                filter: new PickerFilter("Aprendíveis", "Mostrar tudo", item => item.Id < 0 || !item.Muted, StartOn: !Services.HaXMode.IsOn));
            if (pick is null) return dirty;
            highlight = pick.Id;
            switch (pick.Id)
            {
                case GiveAllId: MonFieldService.SetAllLegalTechRecords(session, box, slot); break;
                case ClearId: MonFieldService.ClearTechRecords(session, box, slot); break;
                default:
                    var record = records.First(r => r.Index == pick.Id);
                    MonFieldService.SetTechRecord(session, box, slot, record.Index, !record.Learned);
                    break;
            }
            dirty = true;
        }
    }
}
