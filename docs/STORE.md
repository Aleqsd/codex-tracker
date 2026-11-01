# Microsoft Store

Édition Store : le même programme, empaqueté en MSIX par `scripts/build-msix.ps1`. Le Store signe le paquet (pas de certificat à acheter) et installe ses mises à jour ; l’édition GitHub garde son installateur et sa mise à jour intégrée.

## Une fois (compte Partner Center)

1. Créer un compte développeur **individuel** sur https://storedeveloper.microsoft.com (gratuit pour un particulier, vérification d’identité).
2. Partner Center → **Applications et jeux → Nouveau produit → Application MSIX ou PWA** → réserver le nom. « Codex Tracker » contient la marque Codex d’OpenAI : si la certification le refuse, proposer « Quota Tracker for Codex and Claude Code » ou un nom propre au projet.
3. **Gestion du produit → Identité du produit** : relever `Package/Identity/Name`, `Package/Identity/Publisher` et `Package/Properties/PublisherDisplayName`.

## À chaque version

```powershell
./scripts/build-msix.ps1 -IdentityName '<Name>' -Publisher '<CN=...>' -PublisherDisplayName '<nom affiché>'
```

Envoyer `artifacts/CodexTracker-<version>.msix` dans **Packages** d’une nouvelle soumission. Le numéro du paquet est `x.y.z.0` (révision 0 exigée).

## Fiche

- **Catégorie** : Outils de développement. **Prix** : gratuit. **Âge** : questionnaire IARC, aucun contenu sensible.
- **Politique de confidentialité** : https://codex-tracker.zqsdev.com/privacy/ ; **site** : https://codex-tracker.zqsdev.com ; **support** : https://github.com/Aleqsd/codex-tracker/issues.
- **Captures** (1366 × 768 ou plus, données fictives) : `docs/hero.png`, `docs/dashboard.png`, `docs/resets-week.png`, `docs/reminders.png`, `docs/calendar.png`, `docs/pill.png` ; régénérées par `scripts/visuals.ps1`.
- **Mots-clés** : Codex, Claude Code, usage limit, quota, rate limit, reset, taskbar, MCP.

**Description (en-US)**

> Always know how much OpenAI Codex and Claude Code usage you have left. Codex Tracker puts your 5-hour and weekly limits in the Windows taskbar, tells you exactly when each one resets, and reminds you at the right time.
>
> • Every Codex and Claude Code account side by side, with the age of each reading
> • Exact reset times, a week view and banked Codex reset credits with their expiry
> • Reminders on Windows, on your phone with ntfy (even in Do Not Disturb), by Gmail and in Google Calendar
> • An on-screen pill when an account runs low, with the account to switch to
> • A local MCP server and command line for coding agents
>
> Local only: no account, no server, no password. It reads the quota data Codex and Claude already keep on your PC, never your conversations. Independent project, not affiliated with OpenAI or Anthropic.

**Description (fr-FR)**

> Sachez toujours combien il vous reste de Codex et de Claude Code. Codex Tracker affiche vos limites sur 5 heures et à la semaine dans la barre des tâches Windows, l’heure exacte de chaque recharge, et vous prévient au bon moment.
>
> • Tous vos comptes Codex et Claude Code côte à côte, avec l’ancienneté de chaque relevé
> • Heure exacte des recharges, vue de la semaine et resets en réserve avec leur expiration
> • Rappels Windows, sur le téléphone avec ntfy (même en mode Ne pas déranger), par Gmail et dans Google Agenda
> • Une pastille à l’écran quand un compte est presque vide, avec le compte à prendre
> • Un serveur MCP local et une ligne de commande pour les assistants de code
>
> Tout reste sur votre PC : ni compte, ni serveur, ni mot de passe. Le tracker lit les quotas que Codex et Claude conservent déjà, jamais vos conversations. Projet indépendant, non affilié à OpenAI ni à Anthropic.

## Notes pour la certification

À coller dans **Notes pour la certification** de la soumission :

> Codex Tracker is a Win32 (WPF, .NET 10) desktop app packaged as full trust. `runFullTrust`: it reads the local quota files that the Codex and Claude desktop apps already write in the user profile, shows a notification-area icon and Windows notifications. `unvirtualizedResources` with file and registry write virtualization disabled: the app shares its data folder (%LOCALAPPDATA%\CodexTracker) and its optional "start with Windows" entry with the classic installer edition, so users switching to the Store edition keep their accounts and settings. No account is needed to test: without Codex or Claude installed the app opens empty; `CodexTracker.exe --demo` shows fictional accounts. Network access: GitHub (none in the Store edition: updates come from the Store), optional Google Calendar/Gmail, ntfy, Twilio, SendGrid connectors configured by the user.

## Différences de l’édition Store

- Mises à jour : par le Store uniquement (`AppPackage.IsPackaged` coupe la mise à jour intégrée et masque ses réglages).
- Chemins donnés à Windows et aux autres outils (démarrage avec Windows, configuration MCP, réglage Claude Code, liens de comptes) : alias `codex-tracker.exe`, stable d’une version à l’autre.
- Données : même dossier que l’édition classique ; passer de l’une à l’autre conserve comptes et réglages. Ne pas installer les deux en même temps.
- Non testé localement : l’installation d’un MSIX non signé demande le mode développeur Windows ; tester la première version publiée en privé (visibilité « masquée » dans Partner Center) avant de la rendre publique.
