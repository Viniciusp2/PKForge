using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Safe Day Care / Nursery view. Deposits, eggs, and RNG state remain game-owned.</summary>
public static class DaycareEditor
{
    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var info = session.GetDaycare();
            if (!info.Supported)
            {
                await EditorMenu.ShowAsync(host, "Creche", "Este jogo não tem uma Creche ou Berçário suportado.", "OK");
                return;
            }

            var facilities = info.Facilities;
            var facilityIndex = 0;
            if (facilities.Count > 1)
            {
                var picked = await PickerMenu.ShowAsync(host, "Creche", facilities.Select((f, i) =>
                    new PickItem(i, $"{f.Name} · {(f.EggAvailable ? "egg ready" : "no egg")}")).ToList());
                if (picked is null) return;
                facilityIndex = picked.Id;
            }

            var facility = session.GetDaycare().Facilities[facilityIndex];
            var choices = facility.Slots.Select(slot => new PadOption(SlotLabel(slot))).ToArray();
            var choice = await EditorMenu.ShowAsync(host, facility.Name,
                facility.EggAvailable ? "Tem um ovo pronto. O estado do ovo é somente leitura." : "Pokémon depositados", choices);
            if (choice is null) return;
            var slotIndex = Array.FindIndex(facility.Slots.ToArray(), slot => choice == SlotLabel(slot));
            if (slotIndex < 0 || !facility.Slots[slotIndex].Occupied)
            {
                if (slotIndex >= 0)
                    await EditorMenu.ShowAsync(host, facility.Name, "Este slot está vazio.", "OK");
                continue;
            }

            var slot = facility.Slots[slotIndex];
            var confirmed = await PadMenu.ConfirmAsync(host, "RETIRAR POKéMON?",
                $"{DisplayName(slot)} vai para o primeiro slot vazio das boxes do PC. Um ponto de restauração é criado antes.", "Retirar");
            if (!confirmed) continue;
            var saved = await viewModel.RunMutationAsync(s =>
            {
                var result = s.WithdrawDaycareToFirstEmptyBox(facilityIndex, slot.Index);
                return new GenerationOutcome(true, $"{result.SpeciesName} foi para a Box {result.Box + 1}, slot {result.Slot + 1}.");
            }, Math.Max(0, viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.Move);
            if (!saved) return;
        }
    }

    private static string SlotLabel(DaycareSlot slot) => !slot.Occupied
        ? $"Slot {slot.Index + 1} · vazio"
        : $"Slot {slot.Index + 1} · {DisplayName(slot)} · Lv. {slot.Level}{(slot.Experience is { } exp ? $" · EXP {exp}" : "")}";

    private static string DisplayName(DaycareSlot slot) => string.IsNullOrWhiteSpace(slot.Nickname) || slot.Nickname == slot.SpeciesName
        ? slot.SpeciesName
        : $"{slot.Nickname} ({slot.SpeciesName})";
}
