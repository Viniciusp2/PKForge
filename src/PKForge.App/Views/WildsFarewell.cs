using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Releasing a Pokémon to the Wilds instead of letting it vanish: a confirmation, the
/// release itself, then a goodbye in its own words. The Wilds remember it, and it may come back
/// through the Poképark arch to visit.</summary>
public static class WildsFarewell
{
    /// <param name="release">Does the actual release; false when it did not happen.</param>
    public static async Task<bool> RunAsync(Grid host, ParkWildFriend friend, Func<Task<bool>> release)
    {
        var confirmed = await PadMenu.ConfirmAsync(host, "Release to the Wilds?",
            $"{friend.Name} leaves your {(string.IsNullOrEmpty(friend.Game) ? "box" : friend.Game)} for good and goes to live in the Wilds, the wild lands around the Poképark. It will remember you, and it may come back to visit.",
            "Soltar");
        if (!confirmed || !await release()) return false;
        var services = IPlatformApplication.Current?.Services;
        services?.GetService<PokeparkWildsStore>()?.Remember(friend);
        var portraits = services?.GetService<PokeparkSpriteService>();
        await PokeparkCardOverlay.ShowDialogueAsync(host, friend.Name, ParkWilds.Farewell(friend, Random.Shared),
            portraits is null ? null : () => portraits.GetPortrait(friend.Species, friend.Form, friend.Shiny, friend.Hearts >= 4 ? "Teary-Eyed" : "Happy"));
        return true;
    }
}
