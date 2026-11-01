# Reste à valider avant 1.0

État au 01/11/2026. La 1.0 reste **non signée** et distribuée par les Releases GitHub ; l’édition Microsoft Store est signée par le Store. Il n’y a pas de CI distante : chaque version passe par `scripts/release.ps1 -Publish` puis `-UpdateThisPc` (voir [AGENTS.md](../AGENTS.md)). WinGet : la [PR microsoft/winget-pkgs#438574](https://github.com/microsoft/winget-pkgs/pull/438574) est mise à jour par `scripts/winget.ps1`.

## Essais physiques

- [ ] Profil vierge : `scripts/sandbox-test.ps1 -Version <x.y.z>` (Windows Sandbox à activer une fois dans les fonctionnalités facultatives) enchaîne mise à jour depuis la version publique précédente avec données conservées, installation neuve, raccourci, ligne PowerShell publiée et désinstallation. Compléter à la main par la [recette](PUBLISHED-UPDATE-TEST.md) du bouton de mise à jour et du démarrage. Les dernières preuves en profil vierge précèdent la 0.9.13.
- [ ] Réception réelle des notifications actionnables (Ouvrir le suivi, Rappeler) et effet de « Ne pas déranger », pastille à l’écran et copie ntfy comprises.
- [ ] Deux écrans physiques à DPI différents, sortie de veille, redémarrage d’Explorer et démarrage avec Windows.
- [ ] Utilisateur non élevé : lancement, accès MCP, premier lancement sans Codex, changement réel de compte dans Codex puis fermeture et réouverture du tracker.
- [ ] Passage manuel complet de l’interface en anglais, puis un survol en espagnol, allemand, portugais et japonais (textes tronqués, polices). Les traductions sont vérifiées par `LocalizationTests` (complètes, paramètres, formats) ; leur relecture par un locuteur reste souhaitable.

Pour chaque essai, noter la version exacte, la date, Windows et l’affichage, les étapes, le résultat attendu et le résultat observé, sans adresse, quota réel ni dossier privé.

## Tests WPF

Les vérifications de focus clavier passent par `Foreground.Focus` / `Foreground.HasFocus` : quand Windows garde le premier plan sur une autre application, le focus logique de la fenêtre fait foi, et seules les interactions qui exigent vraiment le premier plan sont signalées « SKIP ». La suite ne doit plus échouer parce que l’utilisateur travaille pendant qu’elle tourne.

## Diffusion et validations externes

- [ ] Validation Google des portées sensibles ([GOOGLE-VERIFICATION.md](GOOGLE-VERIFICATION.md)) : vidéo `scripts/record-google-demo.ps1`, puis soumission.
- [ ] Première soumission Microsoft Store ([STORE.md](STORE.md)) : compte Partner Center, identité, captures `scripts/store-screenshots.py`.

## Candidat 1.0

- [ ] `scripts/bump.ps1 -Version 1.0.0`, puis `scripts/release.ps1 -Publish -UpdateThisPc` sur le commit exact de la 1.0.
- [ ] Vérifier Setup, ZIP, SHA-256, notes courtes et mise à niveau depuis la dernière version publique, données conservées.
- [ ] Consigner les essais ci-dessus et les limites des intégrations facultatives (assistants tiers, Google Agenda, Twilio et SendGrid testés avec des API simulées, jamais par un envoi payant automatique) avant de publier la stable.
