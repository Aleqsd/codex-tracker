# Reste à valider avant 1.0

État au 01/11/2026. La 1.0 reste **non signée** et distribuée par les Releases GitHub ; l’édition Microsoft Store est signée par le Store. Il n’y a pas de CI distante : chaque version passe par `scripts/release.ps1 -Publish` puis `-UpdateThisPc` (voir [AGENTS.md](../AGENTS.md)). WinGet : la [PR microsoft/winget-pkgs#438574](https://github.com/microsoft/winget-pkgs/pull/438574) est mise à jour par `scripts/winget.ps1`.

## Essais physiques

- [ ] Cycle de l’installateur (`scripts/test-installer.ps1`) et mise à niveau publique ([recette](PUBLISHED-UPDATE-TEST.md), bouton et démarrage) dans Windows Sandbox ou une VM jetable. Les dernières preuves en profil vierge précèdent la 0.9.13 ; la désinstallation qui retire l’enregistrement des notifications n’a jamais été testée.
- [ ] Réception réelle des notifications actionnables (Ouvrir le suivi, Rappeler) et effet de « Ne pas déranger », pastille à l’écran et copie ntfy comprises.
- [ ] Deux écrans physiques à DPI différents, sortie de veille, redémarrage d’Explorer et démarrage avec Windows.
- [ ] Utilisateur non élevé : lancement, accès MCP, premier lancement sans Codex, changement réel de compte dans Codex puis fermeture et réouverture du tracker.
- [ ] Passage manuel complet de l’interface en anglais.

Pour chaque essai, noter la version exacte, la date, Windows et l’affichage, les étapes, le résultat attendu et le résultat observé, sans adresse, quota réel ni dossier privé.

## Candidat 1.0

- [ ] `scripts/bump.ps1 -Version 1.0.0`, puis `scripts/release.ps1 -Publish -UpdateThisPc` sur le commit exact de la 1.0.
- [ ] Vérifier Setup, ZIP, SHA-256, notes courtes et mise à niveau depuis la dernière version publique, données conservées.
- [ ] Consigner les essais ci-dessus et les limites des intégrations facultatives (assistants tiers, Google Agenda, Twilio et SendGrid testés avec des API simulées, jamais par un envoi payant automatique) avant de publier la stable.
