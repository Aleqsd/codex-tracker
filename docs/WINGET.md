# Distribution WinGet

Identifiant : `Aleqsd.CodexTracker`. La version 0.9.2 est soumise dans la [demande Microsoft #438574](https://github.com/microsoft/winget-pkgs/pull/438574), pour inclure le correctif de stockage des comptes dès la première publication. Le CLA et tous les contrôles techniques de cette version ont réussi, notamment l’analyse et l’installation du Setup. La revue de politique reste manuelle, confirmée par Microsoft le 23 septembre 2026. La disponibilité dans le catalogue public dépend encore de cette revue et de la fusion du manifeste ; construire ou valider le manifeste ne publie pas le paquet.

Après intégration dans le catalogue :

```powershell
winget install --id Aleqsd.CodexTracker --exact --source winget
winget upgrade --id Aleqsd.CodexTracker --exact --source winget
```

En attendant, utiliser l’[installateur officiel du projet](https://github.com/Aleqsd/codex-tracker/releases). Il installe pour l’utilisateur courant, sans droits administrateur. La mise à jour conserve `%LOCALAPPDATA%\CodexTracker`, les comptes, réglages et secrets chiffrés.

## Publier une version

1. Exécuter `scripts/dev.ps1 -Action Check`, pousser le commit et attendre sa CI Windows verte.
2. Produire et tester le ZIP autonome et l’installateur avec `scripts/publish.ps1` et `scripts/build-installer.ps1`. Publier une Release dont les fichiers sont immuables.
3. Exécuter `scripts/winget.ps1` : la version stable vient de `Directory.Build.props` ; `-Version 0.9.2` permet de cibler explicitement une version déjà publiée. Le script télécharge le Setup public, exige une empreinte identique au fichier local, produit les trois manifestes sous `artifacts/winget/manifests/a/Aleqsd/CodexTracker/<version>`, puis appelle `winget validate`. Un échec réseau ou une différence de contenu arrête la génération avant toute modification des manifestes.
4. Vérifier que l’URL publique renvoie exactement ce fichier et que son installation silencieuse fonctionne. Le manifeste utilise le type Inno, les paramètres silencieux standard, le périmètre utilisateur et l’identifiant stable de désinstallation.
5. Vérifier les paquets et demandes existants, puis soumettre uniquement ces trois fichiers dans une PR de [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). Respecter le [guide officiel](https://github.com/microsoft/winget-pkgs/blob/master/doc/Authoring.md). La signature personnelle du CLA peut être demandée par Microsoft.
6. Contrôler les résultats de validation et, après fusion/indexation, `winget show --id Aleqsd.CodexTracker --exact --source winget`.

Ne jamais réutiliser une version en remplaçant son binaire : le SHA-256 du catalogue doit correspondre au téléchargement. Ne pas annoncer une installation par identifiant disponible avant l’indexation. Le test d’un manifeste local nécessite l’option Windows `LocalManifestFiles` ou Windows Sandbox ; le script de génération ne change aucun de ces réglages.

Après signature, utiliser `-InstallerPath artifacts/release-candidate/CodexTracker-<version>-Setup.exe` pour prendre le candidat exact déjà publié, sans le reconstruire. Le checksum local du Setup est vérifié s’il existe, mais n’est pas requis : le téléchargement public est toujours comparé. Les 14 contrôles de `scripts/test-winget.ps1` simulent le téléchargement et le validateur, sans installer de paquet ni modifier Windows ; la validation réelle du manifeste reste distincte.
