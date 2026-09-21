<h1 align="center">Codex Tracker</h1>
<p align="center">Vos quotas Codex, vos resets et vos rappels — dans la barre des tâches Windows.</p>
<p align="center"><a href="https://github.com/Aleqsd/codex-tracker/releases/download/v0.8.0/CodexTracker-0.8.0-Setup.exe"><strong>⬇ Télécharger pour Windows</strong></a> · <a href="https://github.com/Aleqsd/codex-tracker/releases">Toutes les versions</a></p>
<p align="center">Windows 11 · x64 · Gratuit · Données locales — nécessite Codex installé</p>

![Tour rapide de Codex Tracker : comptes, semaine, agenda, rappels, thèmes et assistants](docs/tour.gif)
<p align="center"><sub>Démonstration avec des comptes fictifs. <a href="docs/resets-week.png">Aperçu statique</a></sub></p>

## Commencer en une minute

1. **Installez** le fichier `Setup.exe` ci-dessus. Aucun runtime ni droit administrateur à ajouter.
2. **Ouvrez Codex.** Le tracker détecte le compte connecté. Vos autres comptes apparaissent quand vous les utilisez dans Codex.
3. **Gardez l’icône près de l’horloge.** Survolez-la pour un aperçu, cliquez pour ouvrir le tableau de bord.

Le bouton **—** réduit la fenêtre dans la barre des tâches ; **✕** la masque près de l’horloge. Le tracker continue à fonctionner.

## L’essentiel, en un coup d’œil

- **📊 Comptes** — quota hebdomadaire dans l’icône, offre, réserves et dernier relevé pour chaque compte.
- **📅 Resets** — agenda ou grille de la semaine, filtres par compte/type et réserve qui expire en premier.
- **🔔 Rappels** — notifications Windows avant un reset ou l’expiration d’une réserve, aux délais de votre choix.
- **🎨 À votre goût** — thème clair/sombre, noms et avatars personnalisés. Import des échéances dans Google Agenda.

## Bon à savoir

**Seul le compte actif dans Codex est actualisé**, toutes les deux minutes par défaut. Les autres conservent leur dernier relevé daté. Une date ou une valeur absente reste inconnue.

**Les rappels nécessitent un PC éveillé et le tracker ouvert.** Le démarrage avec Windows est facultatif. Aucun service distant n’est nécessaire pour les notifications Windows.

**Les options avancées restent facultatives.** Vous pouvez connecter vos propres comptes Twilio/SendGrid pour SMS, appels et emails, ou activer le [MCP local](docs/MCP.md) pour votre assistant. Ces options sont désactivées par défaut ; les frais éventuels dépendent de vos prestataires.

## Installer autrement ou aller plus loin

- **Sans installation :** prenez le ZIP portable dans les [Releases](https://github.com/Aleqsd/codex-tracker/releases). Le petit fichier `.sha256` sert aux mises à jour automatiques ; vous pouvez l’ignorer.
- **Mises à jour :** ouvrez **Réglages → Application**. Comptes et réglages sont conservés.
- **WinGet :** [distribution en cours de soumission](docs/WINGET.md), identifiant prévu `Aleqsd.CodexTracker`.
- [Guide d’utilisation](docs/UTILISATION.md) · [Confidentialité](docs/PRIVACY.md) · [Signaler un problème](https://github.com/Aleqsd/codex-tracker/issues)

<details>
<summary>Développer ou contribuer</summary>

.NET 10 / WPF. Consultez [AGENTS.md](AGENTS.md), l’[architecture](docs/ARCHITECTURE.md) et le [design](docs/DESIGN.md).

```powershell
./scripts/dev.ps1 -Action Check
./scripts/dev.ps1 -Action Demo
```

Les aperçus utilisent exclusivement des données fictives. Voir la [validation](docs/VALIDATION.md).

</details>

---

Projet indépendant, non affilié à OpenAI. Licence [MIT](LICENSE).
