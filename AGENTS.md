# Travailler sur Codex Tracker

Application Windows 11 .NET 10 / WPF, interface française (anglais disponible). Lire README.md, docs/ARCHITECTURE.md et docs/DESIGN.md avant une modification transversale ; docs/UTILISATION.md décrit chaque fonction côté utilisateur.

## Carte du projet

- `src/CodexTracker.Core` : modèles et règles pures (quotas, échéances, rappels, conseils, pastille).
- `src/CodexTracker.Codex` : détection passive de Codex et Claude Code, persistance privée, DPAPI, prestataires d’envoi (Twilio, SendGrid, ntfy), journal d’envoi, Google Agenda et Gmail.
- `src/CodexTracker.App` : interface WPF, barre des tâches, pastille à l’écran et cycle de vie. `ApplicationCommands` partage les validations entre UI et MCP ; `Mcp/` contient le transport, les outils MCP et la ligne de commande `--cli` ; `AppPackage` distingue l’édition Microsoft Store.
- `tests/CodexTracker.Tests` : métier, intégrations simulées et mise à jour. `tests/CodexTracker.UiSmoke` : vraies fenêtres avec données fictives.
- `site/` : site public (codex-tracker.zqsdev.com), `site/install.ps1` : installation en une ligne. `installer/` : script Inno Setup.

## Commandes

Prérequis : Windows, SDK .NET 10, Python 3 (Pillow pour les visuels), `gh` connecté ; Node (pour `npx wrangler`, `wrangler login` une fois) et SDK Windows (makeappx) pour le site et le Store.

```powershell
# Développement
./scripts/dev.ps1 -Action Check                          # compilation, tests métier et WPF, protocole MCP et --cli
./scripts/dev.ps1 -Action Demo
./scripts/dev.ps1 -Action Preview -View Canaux -Theme light -Size compact
./scripts/dev.ps1 -Action Preview -Matrix
./scripts/visuals.ps1                                    # captures, GIF, pastille, image de partage (données fictives)

# Release (locale, pas de CI distante)
./scripts/bump.ps1 -Version 0.9.41                       # version + liens des deux README, committer ensemble
./scripts/release.ps1 -Publish -Quick -NotesPath notes.md -UpdateThisPc

# Distribution et visibilité
./scripts/winget.ps1 [-Submit]                           # manifestes winget ; -Submit met à jour la demande ouverte
./scripts/build-msix.ps1                                 # paquet Microsoft Store (docs/STORE.md)
./scripts/site.ps1 [-Draft|-Deploy]                      # site local, aperçu wrangler dev ou publication immédiate
./scripts/stats.ps1 [-Json]                              # téléchargements, trafic GitHub, visites du site
```

Les scripts trouvent un SDK .NET 10 dans le PATH ou dans `%LOCALAPPDATA%\Microsoft\dotnet` ; `-Dotnet` en impose un autre. Les `packages.lock.json` sont identiques en compilation et en publication : une différence signale un vrai changement de dépendances. Les artefacts restent sous `artifacts/`, ignoré par Git. Tester le MCP sur l’exécutable **publié**, pas seulement avec un client simulé.

**Release** : `release.ps1 -Publish` exige un arbre propre, le commit poussé sur main et les README pointant vers la version. Il compile, teste, produit installateur + ZIP + SHA-256, teste le MCP et `--cli` sur l’exécutable publié, crée la Release GitHub, prépare les manifestes winget et le paquet Store, puis fait reconstruire le site. `-Quick` saute la suite WPF, les collecteurs Claude ou le délai MCP quand les fichiers modifiés depuis le dernier tag ne les touchent pas. `-UpdateThisPc` installe la version publiée sur ce PC et vérifie que les données sont conservées. Notes de release très courtes : installateur recommandé d’abord, ZIP en option, seul le checksum ZIP (moteur de mise à jour des anciennes versions).

## Invariants

- Ne jamais committer comptes personnels, sessions, quotas réels, clés, jetons, sujets ntfy, journaux privés ou captures de l’application réelle. Utiliser `--demo` pour les aperçus. Les secrets de maintenance vivent hors dépôt, dans `%USERPROFILE%\.codex-tracker\` (client Google `google-oauth-client.json`, `store-identity.json`, `cloudflare-analytics-token.txt`).
- Le compte sélectionné de chaque outil est détecté dans ses métadonnées natives. Pas de connexion OAuth, bascule de compte ou écriture des sessions Codex/Claude Code. Claude : cache Desktop versionné et lié à l’organisation, ou barre de statut documentée, sans requête avec ses jetons. Séparer les organisations sur une même adresse.
- Seul le compte actif de chaque fournisseur est actualisé ; garder les dates d’observation des autres et leurs valeurs inconnues. Une échéance passée ne prouve pas qu’un quota a été restauré : l’afficher « probablement rechargé, à confirmer ». Le tracker conseille un compte, il n’en change jamais.
- Un reset Codex déclaré manuellement reste une projection datée et annulable : conserver les relevés mesurés, les réserves et les comptes Claude. Un nouveau relevé est prioritaire.
- Tout envoi externe (SMS, appel, e-mail, ntfy, Gmail, Google Agenda) passe par `ReminderDispatcher` : journal avant envoi, une fois par clé, limites et heures silencieuses ; jamais d’envoi HTTP parallèle. Aucun envoi réel dans les tests.
- MCP et `--cli` désactivés par défaut (réglage Assistants). Clés et sujets en écriture seule, DPAPI utilisateur, aucun secret dans les réponses ou les erreurs. Demandes externes validées localement, idempotentes (`requestId`), contrôlées par révision.
- Les fichiers de préférences ont un seul propriétaire : le processus WPF. Passer par les commandes communes et vérifier la révision pour les modifications MCP.
- Conserver la charte de docs/DESIGN.md (neutres froids, accent indigo unique, Roboto) et les ressources dynamiques, dans l’application comme sur le site. Pas de nouvelle couleur ni de framework UI sans besoin établi.
- Préférer une modification ciblée ; ne pas entreprendre une migration MVVM générale pour ajouter un contrôle.
- L’édition Store (`AppPackage.IsPackaged`) ne se met pas à jour elle-même et donne à Windows et aux outils le chemin stable `AppPackage.LaunchPath` (alias `codex-tracker.exe`), jamais `Environment.ProcessPath`.

## Pilotable par agent

Toute fonctionnalité doit rester pilotable par un agent de code sans contrôle de l’écran : chaque réglage, connexion, action ou état visible dans l’interface a son outil MCP (`Mcp/McpHost.cs`, `Mcp/TrackerControl.cs`, `docs/MCP.md`, compteur de `scripts/test-mcp.py`) et donc sa commande `--cli` (même nom, table de `Mcp/Cli.cs`). Seules les validations humaines voulues (confirmation locale, consentement Google, activation des Assistants) restent hors agent. Les opérations de maintenance (release, site, winget, Store, statistiques) ont chacune leur script.

## Vérification proportionnée

Ne jamais lancer les tests UI locaux, aperçus ou automatisations qui activent une fenêtre pendant que Final Fantasy XIV tourne (`ffxiv_dx11.exe` ou `ffxiv.exe`) : ils peuvent faire quitter le plein écran du jeu. Vérifier les processus en lecture seule avant ces actions ; reporter le contrôle local après la session de jeu. Ne pas fermer/minimiser le jeu. Les compilations et tests métier sans interface restent possibles. Les fenêtres de la suite WPF s’ouvrent sur l’écran à droite du principal s’il existe (`-TestScreen right|left|primary|DISPLAYn` ou `CODEX_TRACKER_TEST_SCREEN`) ; elles prennent tout de même le focus, et une perte de focus pendant la suite peut faire échouer un test de liste déroulante : relancer avant de chercher un bug.

Modifier une règle métier : tests de ses limites et erreurs. Modifier une commande : tester concurrence, refus et répétition, côté MCP et `--cli`. Modifier une vue : aperçu clair/sombre et compact, puis navigation clavier. Toute chaîne d’interface passe par `Loc.T`/`Loc.F` avec sa traduction anglaise ; un test refuse les traductions orphelines. Modifier le site : `scripts/site.ps1 -Draft`, puis contrôle bureau et 375 px, clair et sombre.

## Présentation publique et distribution

- **README** bref, orienté installation et usage : `README.md` (français) et `README.en.md`, alignés sur les fonctions réelles ; détails dans docs/UTILISATION.md.
- **Site** https://codex-tracker.zqsdev.com : sources `site/` (accueil en six langues, guides anglais, `/install/`, confidentialité, `llms.txt`), construit par `scripts/build-site.py` et servi par le Worker Cloudflare à fichiers statiques `codex-tracker` (`wrangler.jsonc`) : Workers Builds le reconstruit et le publie à chaque push sur main, `scripts/site.ps1 -Deploy` depuis ce PC et après chaque release (dernière version publiée, sitemap, IndexNow, Cloudflare Web Analytics sans cookie). DNS zqsdev.com chez Cloudflare ; Search Console vérifiée pour le domaine. Aucun JavaScript propre, en-têtes de sécurité dans `site/_headers`, redirections dans `site/_redirects`. Garder les six langues alignées quand une fonction change.
- **Installation** : installateur Inno (utilisateur courant, sans droits administrateur), ZIP portable, `irm https://codex-tracker.zqsdev.com/install.ps1 | iex` (empreinte GitHub vérifiée) ou « demander à Codex » via `/install/`.
- **Distribution** : winget (demande microsoft/winget-pkgs#438574, `scripts/winget.ps1 -Submit`), Microsoft Store (docs/STORE.md, le Store signe le MSIX). Pas de certificat de signature : SmartScreen peut avertir l’installateur GitHub.
- **Visuels** exclusivement fictifs : `scripts/visuals.ps1` régénère hors écran le visuel principal, les captures du guide, le GIF, la pastille et l’image de partage.
- **Google** : projet `codex-tracker-513408`, client OAuth intégré à la publication, validation en cours (docs/GOOGLE-VERIFICATION.md).
