# Travailler sur Codex Tracker

Application Windows .NET 10 / WPF, interface française. Lire README.md, docs/ARCHITECTURE.md et docs/DESIGN.md avant une modification transversale.

## Carte du projet

- `src/CodexTracker.Core` : modèles et règles pures (quotas, échéances, rappels).
- `src/CodexTracker.Codex` : détection passive de Codex, persistance privée, DPAPI, prestataires et journal d’envoi.
- `src/CodexTracker.App` : interface WPF, barre des tâches et cycle de vie. `ApplicationCommands` partage les validations entre UI et MCP ; `Mcp` contient le transport et les outils.
- `tests/CodexTracker.Tests` : métier, intégrations simulées et mise à jour. `tests/CodexTracker.UiSmoke` : vraies fenêtres avec données fictives.

## Commandes

Prérequis : Windows, SDK .NET 10, Python 3 pour le test du protocole.

```powershell
./scripts/dev.ps1 -Action Check
./scripts/dev.ps1 -Action Demo
./scripts/dev.ps1 -Action Preview -View Assistants -Theme dark
./scripts/dev.ps1 -Action Preview -Matrix
./scripts/publish.ps1 -Version 0.8.2
./scripts/build-installer.ps1 -Version 0.8.2
```

Les scripts acceptent `-Dotnet` pour un SDK hors PATH ; dev accepte aussi `-Python`. Les artefacts restent sous `artifacts/`, ignoré par Git. Tester le MCP sur l’exécutable **publié**, pas seulement avec un client simulé.

## Invariants

- Ne jamais committer comptes personnels, sessions, quotas réels, clés, journaux privés ou captures de l’application réelle. Utiliser `--demo` pour les aperçus.
- Le compte actif est détecté dans Codex. Pas de connexion OAuth, bascule de compte ou écriture de la session Codex.
- Seul le compte actif est actualisé ; garder les dates d’observation des autres et leurs valeurs inconnues. Une échéance passée ne prouve pas qu’un quota a été restauré.
- Aucun SMS, appel ou email réel dans les tests. Utiliser le moteur existant et ses limites, jamais un envoi HTTP parallèle.
- MCP désactivé par défaut. Clés en écriture seule, DPAPI utilisateur, aucune clé dans les erreurs. Demandes externes validées localement et idempotentes.
- Les fichiers de préférences ont un seul propriétaire : le processus WPF. Passer par les commandes communes et vérifier la révision pour les modifications MCP.
- Conserver le style neutre Codex et les ressources dynamiques. Pas de nouvelle palette ni de framework UI sans besoin établi.
- Préférer une modification ciblée ; ne pas entreprendre une migration MVVM générale pour ajouter un contrôle.

## Vérification proportionnée

Modifier une règle métier : tests de ses limites et erreurs. Modifier une commande : tester concurrence, refus et répétition. Modifier une vue : aperçu clair/sombre et compact, puis navigation clavier. Avant une release : CI Windows verte sur le commit publié, installer + ZIP + SHA-256, test réel stdio et préservation des données existantes.

## Présentation publique

README bref, orienté installation et usage ; détails dans docs/UTILISATION.md. Tour GIF rapide, exclusivement fictif. Notes de release très courtes avec l’installateur recommandé en premier, le ZIP en option et uniquement le checksum ZIP nécessaire aux anciennes versions du moteur de mise à jour. Le checksum EXE reste dans les artefacts de validation et sert au manifeste WinGet.
