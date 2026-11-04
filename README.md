<p align="center"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/brand/owl-dark.svg"><img src="docs/brand/owl-light.svg" alt="Codex Tracker : le hibou" width="128" height="128"></picture></p>
<h1 align="center">Codex Tracker</h1>
<p align="center"><strong>Sachez toujours combien il vous reste de Codex et de Claude Code.</strong><br>Votre quota, l’heure de la prochaine recharge et un rappel au bon moment, directement dans la barre des tâches Windows.</p>
<p align="center"><a href="https://github.com/Aleqsd/codex-tracker/releases/download/v1.0.2/CodexTracker-1.0.2-Setup.exe"><strong>⬇ Télécharger pour Windows</strong></a> · <a href="https://codex-tracker.zqsdev.com/fr/">Site</a> · <a href="README.en.md">English</a></p>
<p align="center"><sub>Gratuit · Windows 11 x64 · Sans compte ni serveur · 6 langues · Version 1.0.2</sub></p>

<p align="center"><img src="docs/hero.png" alt="Codex Tracker : tableau de bord sombre, semaine des recharges en clair et aperçu de la barre des tâches, avec des comptes fictifs" width="880"></p>

## En deux mots

<picture><source media="(prefers-color-scheme: dark)" srcset="docs/brand/watching-dark.png"><img src="docs/brand/watching-light.png" alt="" width="84" align="right"></picture>

Codex et Claude Code limitent votre usage : un quota sur **5 heures** et un autre sur **la semaine**. Une fois épuisé, il faut attendre qu’il se recharge, et rien ne vous dit clairement où vous en êtes.

Codex Tracker répond à trois questions, sans rien configurer :

- **Combien il me reste ?** Le pourcentage s’affiche dans l’icône de la barre des tâches. Survolez-la pour le détail.
- **Quand ça se recharge ?** L’heure exacte de chaque recharge, pour chaque compte, en liste ou sur la semaine.
- **Lequel de mes comptes utiliser ?** Tous vos comptes Codex et Claude Code côte à côte, personnels comme professionnels.

## Ce que vous obtenez

<table>
<tr>
<td width="50%" valign="top"><img src="docs/dashboard.png" alt="Tableau de bord avec comptes fictifs"><br><strong>Vos comptes d’un coup d’œil.</strong> Un anneau par compte actif, une barre pour chacun des autres et la date de leur dernier relevé.</td>
<td width="50%" valign="top"><img src="docs/resets-week.png" alt="Semaine des recharges avec comptes fictifs"><br><strong>Le calendrier des recharges.</strong> Les prochaines recharges jour par jour, et les crédits de reset Codex en réserve avec leur date d’expiration.</td>
</tr>
<tr>
<td width="50%" valign="top"><img src="docs/reminders.png" alt="Réglages des rappels avec comptes fictifs"><br><strong>Prévenu au bon moment.</strong> Activez la cloche d’un reset pour être prévenu à l’heure choisie. Retrouvez aussi les rappels avant une recharge, sous 20, 10 ou 5 % de quota, avant d’être à court ou quand un autre compte peut s’être rechargé, et leur copie gratuite sur votre téléphone avec ntfy, même en mode Ne pas déranger. Une pastille discrète apparaît au-dessus de l’horloge quand un compte est presque vide ou qu’une réserve va expirer : « Ouvrir » affiche le suivi, « Masquer » la retire jusqu’à la prochaine alerte. Les notifications Windows proposent aussi « Rappeler » plus tard.</td>
<td width="50%" valign="top"><img src="docs/settings.png" alt="Réglages généraux, démonstration"><br><strong>Discret et à votre goût.</strong> Interface en français, anglais, espagnol, allemand, portugais ou japonais, thème clair ou sombre comme Windows, noms et photos pour vos comptes, synchronisation Google Agenda et alertes Gmail pour les resets en réserve.</td>
</tr>
</table>

<p align="center"><img src="docs/tour.gif" alt="Tour rapide : comptes, semaine, recharges, rappels, thème clair et assistants" width="760"><br><sub>Toutes les images utilisent des comptes fictifs.</sub></p>

## Installer en trois étapes

<picture><source media="(prefers-color-scheme: dark)" srcset="docs/brand/terminal-dark.png"><img src="docs/brand/terminal-light.png" alt="" width="84" align="right"></picture>

1. **Téléchargez et lancez** l’installateur ci-dessus. Pas de droits administrateur, rien d’autre à installer.
2. **Utilisez Codex ou Claude Code comme d’habitude.** Le compte connecté apparaît tout seul. Vos autres comptes s’ajoutent quand vous les ouvrez.
3. **Gardez l’icône visible** : glissez-la depuis la flèche ^ de la barre des tâches. Fermer la fenêtre la range simplement à côté de l’horloge ; le suivi continue.

Ou en une ligne dans PowerShell : `irm https://codex-tracker.zqsdev.com/install.ps1 | iex` (empreinte SHA-256 vérifiée), ou demandez à Codex : https://codex-tracker.zqsdev.com/install/

Les mises à jour se téléchargent seules ; un bouton **Mettre à jour et relancer** apparaît quand une version est prête.

## Vos données restent chez vous

<picture><source media="(prefers-color-scheme: dark)" srcset="docs/brand/window-dark.png"><img src="docs/brand/window-light.png" alt="" width="84" align="right"></picture>

- **Rien à créer, rien à partager.** Pas d’inscription, pas de serveur : tout est enregistré sur votre PC.
- **Aucun mot de passe demandé.** Le tracker lit les quotas que Codex et Claude Code connaissent déjà. Il ne lit pas vos conversations et ne change jamais de compte à votre place.
- **Peu de connexions, et choisies.** Il recherche les mises à jour sur GitHub et, si vous le laissez faire, les annonces publiques de reset général. Les deux se désactivent dans les réglages. Google Agenda/Gmail, ntfy, Twilio et SendGrid ne se connectent que si vous les activez.

Le détail est dans la [page confidentialité](docs/PRIVACY.md).

<details>
<summary><strong>Questions fréquentes</strong></summary>

**Pourquoi seul le compte ouvert est-il mis à jour ?**
Le tracker lit le compte actif dans Codex et dans Claude. Les autres gardent leur dernier relevé, toujours daté. Quand l’heure de recharge d’un compte inactif est passée, il l’affiche comme « probablement rechargé », à confirmer en ouvrant ce compte.

**Faut-il laisser l’application ouverte ?**
Pour les rappels, oui : elle reste discrète dans la barre des tâches et peut démarrer avec Windows. Le PC doit être allumé.

**Et Claude Code dans le terminal ?**
L’application Claude est détectée automatiquement. Pour le terminal, copiez le réglage proposé dans **Réglages → Général → Claude Code**. [Sources et limites](docs/CLAUDE-CODE.md).

**OpenAI a rechargé tous les quotas d’un coup ?**
Cliquez sur l’icône d’horloge à côté de l’en-tête Codex et indiquez l’heure. Les comptes concernés affichent « 100 % · déclaré » jusqu’au prochain relevé réel. Les annonces publiques reconnues sont aussi signalées automatiquement ([fonctionnement](docs/GLOBAL-RESETS.md)).

**C’est payant ?**
Non. Les SMS, appels et e-mails SendGrid, facultatifs, suivent les tarifs de vos comptes Twilio ou SendGrid ; les e-mails Gmail et le téléphone via ntfy sont gratuits.

</details>

## Pour aller plus loin

<picture><source media="(prefers-color-scheme: dark)" srcset="docs/brand/reading-dark.png"><img src="docs/brand/reading-light.png" alt="" width="84" align="right"></picture>

- **Sans installation** : le ZIP portable est dans les [versions publiées](https://github.com/Aleqsd/codex-tracker/releases). Son petit fichier `.sha256` sert aux mises à jour automatiques.
- **Assistants de code** : un [serveur MCP local](docs/MCP.md), désactivé par défaut, permet à votre assistant de consulter vos quotas, de régler les rappels, canaux et Google Agenda et la plupart des réglages ; les mêmes commandes existent en ligne de commande (`CodexTracker.exe --cli help`).
- [Guide d’utilisation complet](docs/UTILISATION.md) · [Signaler un problème](https://github.com/Aleqsd/codex-tracker/issues). **Réglages → Application → Préparer un diagnostic** produit un résumé sans compte ni secret à joindre à votre signalement.

Les exécutables ne sont pas signés : Windows peut afficher un avertissement SmartScreen au premier lancement.

<details>
<summary>Développer ou contribuer</summary>

.NET 10 / WPF. Consultez [AGENTS.md](AGENTS.md), l’[architecture](docs/ARCHITECTURE.md) et le [design](docs/DESIGN.md).

```powershell
./scripts/dev.ps1 -Action Check
./scripts/dev.ps1 -Action Demo
```

Logo et illustrations : [assets/brand](assets/brand/README.md), régénérés par `python scripts/brand.py`. Les aperçus utilisent exclusivement des données fictives. Avant une version, `scripts/release.ps1` enchaîne compilation, tests et paquets.

</details>

<p align="center"><picture><source media="(prefers-color-scheme: dark)" srcset="docs/brand/sleeping-dark.png"><img src="docs/brand/sleeping-light.png" alt="" width="64"></picture></p>

---

<sub>Projet indépendant, non affilié à OpenAI ni à Anthropic. Codex et Claude sont des marques de leurs propriétaires respectifs. Licence [MIT](LICENSE).</sub>
