# Travailler sur Codex Tracker

Application Windows .NET 10 / WPF, interface française. Lire README.md, docs/ARCHITECTURE.md et docs/DESIGN.md avant une modification transversale.

## Carte du projet

- `src/CodexTracker.Core` : modèles et règles pures (quotas, échéances, rappels).
- `src/CodexTracker.Codex` : détection passive de Codex et Claude Code, persistance privée, DPAPI, prestataires et journal d’envoi.
- `src/CodexTracker.App` : interface WPF, barre des tâches et cycle de vie. `ApplicationCommands` partage les validations entre UI et MCP ; `Mcp` contient le transport et les outils.
- `tests/CodexTracker.Tests` : métier, intégrations simulées et mise à jour. `tests/CodexTracker.UiSmoke` : vraies fenêtres avec données fictives.

## Commandes

Prérequis : Windows, SDK .NET 10, Python 3 pour le test du protocole.

```powershell
./scripts/dev.ps1 -Action Check
./scripts/dev.ps1 -Action Demo
./scripts/dev.ps1 -Action Preview -View Assistants -Theme dark
./scripts/dev.ps1 -Action Preview -Matrix
./scripts/visuals.ps1
./scripts/publish.ps1 -Version 0.9.2
./scripts/build-installer.ps1 -Version 0.9.2
./scripts/bump.ps1 -Version 0.9.19
./scripts/release.ps1 -Publish -NotesPath notes.md -UpdateThisPc
```

Les scripts trouvent un SDK .NET 10 dans le PATH ou dans `%LOCALAPPDATA%\Microsoft\dotnet` (dotnet-install) ; `-Dotnet` en impose un autre, dev accepte aussi `-Python`. `bump.ps1` change la version, les liens du README et les `packages.lock.json` (qui notent la version des projets) : committer les trois ensemble. Les artefacts restent sous `artifacts/`, ignoré par Git. Pas de CI distante : compilation, tests, paquets et Releases se font en local avec `scripts/release.ps1`. Tester le MCP sur l’exécutable **publié**, pas seulement avec un client simulé.

## Invariants

- Ne jamais committer comptes personnels, sessions, quotas réels, clés, journaux privés ou captures de l’application réelle. Utiliser `--demo` pour les aperçus.
- Le compte sélectionné de chaque outil est détecté dans ses métadonnées natives. Pas de connexion OAuth, bascule de compte ou écriture des sessions Codex/Claude Code. Claude : cache Desktop versionné et lié à l’organisation, ou barre de statut documentée, sans requête avec ses jetons. Séparer les organisations sur une même adresse.
- Seul le compte actif de chaque fournisseur est actualisé ; garder les dates d’observation des autres et leurs valeurs inconnues. Une échéance passée ne prouve pas qu’un quota a été restauré.
- Un reset Codex déclaré manuellement reste une projection datée et annulable : conserver les relevés mesurés, les réserves et les comptes Claude. Un nouveau relevé est prioritaire.
- Aucun SMS, appel ou email réel dans les tests. Utiliser le moteur existant et ses limites, jamais un envoi HTTP parallèle.
- MCP désactivé par défaut. Clés en écriture seule, DPAPI utilisateur, aucune clé dans les erreurs. Demandes externes validées localement et idempotentes.
- Les fichiers de préférences ont un seul propriétaire : le processus WPF. Passer par les commandes communes et vérifier la révision pour les modifications MCP.
- Conserver la charte de docs/DESIGN.md (neutres froids, accent indigo unique) et les ressources dynamiques. Pas de nouvelle couleur ni de framework UI sans besoin établi.
- Préférer une modification ciblée ; ne pas entreprendre une migration MVVM générale pour ajouter un contrôle.

## Vérification proportionnée

Ne jamais lancer les tests UI locaux, aperçus ou automatisations qui activent une fenêtre pendant que Final Fantasy XIV tourne (`ffxiv_dx11.exe` ou `ffxiv.exe`) : ils peuvent faire quitter le plein écran du jeu. Vérifier les processus en lecture seule avant ces actions ; reporter le contrôle local après la session de jeu. Ne pas fermer/minimiser le jeu. Les compilations et tests métier sans interface restent possibles. Les fenêtres de la suite WPF s’ouvrent sur l’écran à droite du principal s’il existe (`-TestScreen right|left|primary|DISPLAYn` ou `CODEX_TRACKER_TEST_SCREEN`) ; elles prennent tout de même le focus.

Modifier une règle métier : tests de ses limites et erreurs. Modifier une commande : tester concurrence, refus et répétition. Modifier une vue : aperçu clair/sombre et compact, puis navigation clavier. Avant une release : `scripts/release.ps1 -Publish` réussi sur le commit poussé (compilation, tests métier et WPF, installer + ZIP + SHA-256, test réel stdio), puis `-UpdateThisPc` pour vérifier la mise à jour réelle et la préservation des données existantes.

## Présentation publique

README bref, orienté installation et usage ; détails dans docs/UTILISATION.md. Tour GIF rapide, exclusivement fictif ; `scripts/visuals.ps1` régénère hors écran le visuel principal, les captures du guide et le GIF (Pillow requis). Notes de release très courtes avec l’installateur recommandé en premier, le ZIP en option et uniquement le checksum ZIP nécessaire aux anciennes versions du moteur de mise à jour. Le checksum EXE reste dans les artefacts locaux. Ni signature de code ni WinGet : abandonnés.
