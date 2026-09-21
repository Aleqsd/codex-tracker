# Distribution WinGet

Identifiant : `Aleqsd.CodexTracker`. La version 0.8.1 est soumise dans la [demande Microsoft #438574](https://github.com/microsoft/winget-pkgs/pull/438574). Le CLA du contributeur est validé ; les contrôles Microsoft sont relancés pour cette version. La disponibilité dans le catalogue public dépend de la validation et de la fusion du manifeste par Microsoft ; construire le manifeste ne publie pas le paquet.

Après intégration dans le catalogue :

```powershell
winget install --id Aleqsd.CodexTracker --exact --source winget
winget upgrade --id Aleqsd.CodexTracker --exact --source winget
```

En attendant, utiliser l’[installateur officiel du projet](https://github.com/Aleqsd/codex-tracker/releases). Il installe pour l’utilisateur courant, sans droits administrateur. La mise à jour conserve `%LOCALAPPDATA%\CodexTracker`, les comptes, réglages et secrets chiffrés.

## Publier une version

1. Exécuter `scripts/dev.ps1 -Action Check`, pousser le commit et attendre sa CI Windows verte.
2. Produire et tester le ZIP autonome et l’installateur avec `scripts/publish.ps1` et `scripts/build-installer.ps1`. Publier une Release dont les fichiers sont immuables.
3. Exécuter `scripts/winget.ps1 -Version 0.8.1`. Le script vérifie le SHA-256 de l’installateur, produit les trois fichiers sous `artifacts/winget/manifests/a/Aleqsd/CodexTracker/0.8.1`, puis appelle `winget validate`.
4. Vérifier que l’URL publique renvoie exactement ce fichier et que son installation silencieuse fonctionne. Le manifeste utilise le type Inno, les paramètres silencieux standard, le périmètre utilisateur et l’identifiant stable de désinstallation.
5. Vérifier les paquets et demandes existants, puis soumettre uniquement ces trois fichiers dans une PR de [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). Respecter le [guide officiel](https://github.com/microsoft/winget-pkgs/blob/master/doc/Authoring.md). La signature personnelle du CLA peut être demandée par Microsoft.
6. Contrôler les résultats de validation et, après fusion/indexation, `winget show --id Aleqsd.CodexTracker --exact --source winget`.

Ne jamais réutiliser une version en remplaçant son binaire : le SHA-256 du catalogue doit correspondre au téléchargement. Ne pas annoncer une installation par identifiant disponible avant l’indexation. Le test d’un manifeste local nécessite l’option Windows `LocalManifestFiles` ou Windows Sandbox ; le script de génération ne change aucun de ces réglages.
