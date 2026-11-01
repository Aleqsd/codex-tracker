# Microsoft Store

Édition Store : le même programme, empaqueté en MSIX par `scripts/build-msix.ps1`. Le Store signe le paquet (pas de certificat à acheter) et installe ses mises à jour ; l’édition GitHub garde son installateur et sa mise à jour intégrée.

## Une fois (compte Partner Center)

1. Créer un compte développeur **individuel** sur https://storedeveloper.microsoft.com (gratuit pour un particulier, vérification d’identité).
2. Partner Center → **Applications et jeux → Nouveau produit → Application MSIX ou PWA** → réserver le nom. « Codex Tracker » contient la marque Codex d’OpenAI : si la certification le refuse, proposer « Quota Tracker for Codex and Claude Code » ou un nom propre au projet.
3. **Gestion du produit → Identité du produit** : relever `Package/Identity/Name`, `Package/Identity/Publisher` et `Package/Properties/PublisherDisplayName`.

## À chaque version

`scripts/release.ps1 -Publish` construit automatiquement le MSIX de la version. `scripts/build-msix.ps1` lit l’identité dans `%USERPROFILE%\.codex-tracker\store-identity.json` (hors dépôt) :

```json
{ "IdentityName": "<Name>", "Publisher": "<CN=...>", "PublisherDisplayName": "<nom affiché>" }
```

Appel manuel toujours possible avec ces valeurs en paramètres :

```powershell
./scripts/build-msix.ps1 -IdentityName '<Name>' -Publisher '<CN=...>' -PublisherDisplayName '<nom affiché>'
```

Envoyer `artifacts/CodexTracker-<version>.msix` dans **Packages** d’une nouvelle soumission. Le numéro du paquet est `x.y.z.0` (révision 0 exigée).

## Fiche

- **Catégorie** : Outils de développement. **Prix** : gratuit. **Âge** : questionnaire IARC, aucun contenu sensible.
- **Politique de confidentialité** : https://codex-tracker.zqsdev.com/privacy/ ; **site** : https://codex-tracker.zqsdev.com ; **support** : https://github.com/Aleqsd/codex-tracker/issues.
- **Captures** : `python scripts/store-screenshots.py` rend hors écran six captures 1920 × 1080 par langue (fr, en, es, de, pt, ja), données fictives, dans `artifacts/store/<langue>/` ; envoyer celles de la langue de chaque fiche, dans l’ordre des numéros.
- **Langues de fiche** : le paquet déclare en-US, fr-FR, es-ES, de-DE, pt-BR et ja-JP ; une fiche par langue (descriptions ci-dessous).
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

**Description (es-ES)**

> Sabe siempre cuánto te queda de OpenAI Codex y Claude Code. Codex Tracker muestra tus límites de 5 horas y semanales en la barra de tareas de Windows, te dice exactamente cuándo se recarga cada uno y te avisa en el momento justo.
>
> • Todas tus cuentas de Codex y Claude Code lado a lado, con la antigüedad de cada medición
> • Hora exacta de cada recarga, vista semanal y reinicios en reserva de Codex con su caducidad
> • Recordatorios en Windows, en el móvil con ntfy (incluso en No molestar), por Gmail y en Google Calendar
> • Una pastilla en pantalla cuando una cuenta se agota, con la cuenta a la que cambiar
> • Un servidor MCP local y una línea de comandos para los agentes de código
>
> Todo se queda en tu PC: sin cuenta, sin servidor, sin contraseña. Lee los datos de cuota que Codex y Claude ya guardan, nunca tus conversaciones. Proyecto independiente, sin relación con OpenAI ni Anthropic.

**Description (de-DE)**

> Wisse immer, wie viel OpenAI Codex und Claude Code dir noch bleibt. Codex Tracker zeigt deine 5-Stunden- und Wochenlimits in der Windows-Taskleiste, sagt dir genau, wann jedes aufgefüllt wird, und erinnert dich im richtigen Moment.
>
> • Alle Codex- und Claude-Code-Konten nebeneinander, mit dem Alter jeder Messung
> • Genaue Auffüllzeiten, Wochenansicht und Codex-Reserve-Resets mit Ablaufdatum
> • Erinnerungen unter Windows, auf dem Handy mit ntfy (auch bei Nicht stören), per Gmail und im Google Kalender
> • Ein Hinweis auf dem Bildschirm, wenn ein Konto knapp wird, mit dem Konto, zu dem du wechseln kannst
> • Ein lokaler MCP-Server und eine Kommandozeile für Coding-Agents
>
> Alles bleibt auf deinem PC: kein Konto, kein Server, kein Passwort. Gelesen werden nur die Kontingentdaten, die Codex und Claude bereits speichern, nie deine Unterhaltungen. Unabhängiges Projekt, nicht mit OpenAI oder Anthropic verbunden.

**Description (pt-BR)**

> Saiba sempre quanto ainda resta do OpenAI Codex e do Claude Code. O Codex Tracker mostra seus limites de 5 horas e semanais na barra de tarefas do Windows, diz exatamente quando cada um recarrega e avisa você na hora certa.
>
> • Todas as suas contas do Codex e do Claude Code lado a lado, com a idade de cada leitura
> • Horário exato das recargas, visão da semana e resets em reserva do Codex com a expiração
> • Lembretes no Windows, no celular com ntfy (mesmo em Não incomodar), por Gmail e no Google Agenda
> • Uma pílula na tela quando uma conta está acabando, com a conta para a qual trocar
> • Um servidor MCP local e uma linha de comando para agentes de código
>
> Tudo fica no seu PC: sem conta, sem servidor, sem senha. Ele lê os dados de cota que o Codex e o Claude já guardam, nunca suas conversas. Projeto independente, sem afiliação com a OpenAI ou a Anthropic.

**Description (ja-JP)**

> OpenAI CodexとClaude Codeの残りを、いつでも把握。Codex Trackerは5時間枠と週間枠をWindowsのタスクバーに表示し、それぞれの回復時刻を正確に示して、ちょうどよいタイミングで通知します。
>
> • CodexとClaude Codeの全アカウントを並べて表示、各測定の経過時間つき
> • 正確な回復時刻、週間ビュー、Codexの予備リセットとその期限
> • Windows、ntfyでスマートフォン（応答不可でも）、Gmail、Googleカレンダーでリマインダー
> • アカウントの残りが少なくなると画面上にバッジを表示し、切り替え先を提案
> • コーディングエージェント向けのローカルMCPサーバーとコマンドライン
>
> すべてPC内で完結：アカウント、サーバー、パスワードは不要。CodexとClaudeがすでに保存している利用枠のデータだけを読み取り、会話は読みません。OpenAIおよびAnthropicとは無関係の独立したプロジェクトです。

## Notes pour la certification

À coller dans **Notes pour la certification** de la soumission :

> Codex Tracker is a Win32 (WPF, .NET 10) desktop app packaged as full trust. `runFullTrust`: it reads the local quota files that the Codex and Claude desktop apps already write in the user profile, shows a notification-area icon and Windows notifications. `unvirtualizedResources` with file and registry write virtualization disabled: the app shares its data folder (%LOCALAPPDATA%\CodexTracker) and its optional "start with Windows" entry with the classic installer edition, so users switching to the Store edition keep their accounts and settings. No account is needed to test: without Codex or Claude installed the app opens empty; `CodexTracker.exe --demo` shows fictional accounts. Network access: GitHub (none in the Store edition: updates come from the Store), optional Google Calendar/Gmail, ntfy, Twilio, SendGrid connectors configured by the user.

## Différences de l’édition Store

- Widget Windows 11 (Win+W) inclus : serveur COM `Widgets\CodexTracker.Widgets.exe`, dépendance au Windows App Runtime 1.8 (installée par le Store). Essai local : `./scripts/build-msix.ps1 -Register` (mode développeur), retrait : `-Unregister`.

- Mises à jour : par le Store uniquement (`AppPackage.IsPackaged` coupe la mise à jour intégrée et masque ses réglages).
- Chemins donnés à Windows et aux autres outils (démarrage avec Windows, configuration MCP, réglage Claude Code, liens de comptes) : alias `codex-tracker.exe`, stable d’une version à l’autre.
- Données : même dossier que l’édition classique ; passer de l’une à l’autre conserve comptes et réglages. Ne pas installer les deux en même temps.
- Non testé localement : l’installation d’un MSIX non signé demande le mode développeur Windows ; tester la première version publiée en privé (visibilité « masquée » dans Partner Center) avant de la rendre publique.
