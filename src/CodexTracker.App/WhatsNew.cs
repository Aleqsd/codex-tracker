using CodexTracker.Core;

namespace CodexTracker.App;

/// <param name="Page">Settings or main page that shows the feature, for the "Découvrir" action; null when there is none.</param>
internal sealed record WhatsNewItem(string Version, string Text, string? Page = null);

/// <summary>
/// What changed, shown once after an update: a banner on the dashboard until "OK", also readable by agents.
/// Add the user-visible features of each release here (newest first), in French through Loc.T.
/// A fresh installation starts with nothing to announce.
/// </summary>
internal static class WhatsNew
{
    internal static IReadOnlyList<WhatsNewItem> Items() =>
    [
        new("1.0.5", Loc.T("Actualisation et synchronisation Google Agenda plus fiables"), "Comptes"),
        new("1.0.4", Loc.T("Actualisation des quotas plus fiable et plus légère"), "Comptes"),
        new("1.0.3", Loc.T("Projection de la semaine plus claire : ce qui restera au reset et combien utiliser par jour"), "Comptes"),
        new("1.0.2", Loc.T("Logo hibou redessiné, net à toutes les tailles")),
        new("1.0.1", Loc.T("Nouveau logo : un hibou, avec quelques illustrations")),
        new("0.9.45", Loc.T("Le quota de 5 heures disparaît des offres qui n’en ont pas"), "Comptes"),
        new("0.9.44", Loc.T("Interface en espagnol, allemand, portugais et japonais"), "Général"),
        new("0.9.42", Loc.T("Rappels de recharge seulement quand ils sont utiles"), "Général"),
        new("0.9.42", Loc.T("Signaler un problème en un clic, diagnostic compris"), "Application"),
        new("0.9.42", Loc.T("Ce panneau des nouveautés après chaque mise à jour")),
        new("0.9.42", Loc.T("Les assistants de code pilotent désormais tout le tracker"), "Assistants"),
        new("0.9.41", Loc.T("Les assistants de code pilotent presque tous les réglages"), "Assistants"),
        new("0.9.40", Loc.T("Installation en une ligne, ou en demandant à Codex")),
        new("0.9.39", Loc.T("Pastille à l’écran quand un compte est presque vide"), "Général"),
        new("0.9.38", Loc.T("Notifications sur le téléphone avec ntfy, même en mode Ne pas déranger"), "Canaux"),
        new("0.9.37", Loc.T("Le compte à prendre quand vous êtes presque à court"), "Comptes"),
    ];

    /// <summary>Items newer than the last version seen, up to the running one; none on a fresh installation.</summary>
    internal static IReadOnlyList<WhatsNewItem> Since(string? lastSeen, string current)
    {
        if (lastSeen is null || Parse(current) is not { } now || Parse(lastSeen) is not { } seen || seen >= now) return [];
        return Items().Where(i => Parse(i.Version) is { } v && v > seen && v <= now).ToArray();
    }

    internal static Version? Parse(string? text) =>
        text is not null && Version.TryParse(text.Split('-', '+')[0], out var version) ? version : null;
}
