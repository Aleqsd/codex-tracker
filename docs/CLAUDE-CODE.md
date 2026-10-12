# Suivre Claude Code

Codex Tracker détecte le profil sélectionné dans Claude Code sur Windows, conserve ses derniers relevés et distingue ses comptes de ceux de Codex. L’identité vient de `%USERPROFILE%/.claude.json` ; `%USERPROFILE%/.claude/.credentials.json` apporte les métadonnées d’abonnement lorsqu’une session terminal y est présente. Si `CLAUDE_CONFIG_DIR` est défini, ces fichiers sont lus dans ce dossier. Un dossier invalide ne provoque pas de retour silencieux vers un autre compte.

Les comptes personnels et d’entreprise sur une même adresse restent distincts grâce à leurs UUID de compte et d’organisation. Le nom d’organisation apparaît dans les vues. Un compte n’est ajouté qu’après observation de son identité native ; la présence d’une organisation dans un vieux cache ne suffit pas à l’attribuer à votre adresse.

## Application Claude : automatique

L’onglet Code de l’application Claude met à jour les mêmes métadonnées d’identité que le terminal. Le tracker lit automatiquement le fichier natif `plan-usage-history.json` sous `%APPDATA%/Claude`, ou sous `LocalCache/Roaming/Claude` du package Microsoft Store. Aucun hook ni réglage n’est nécessaire pour cette source.

Le tracker lit aussi les réponses de quotas déjà enregistrées dans le cache HTTP local de Claude Desktop, sous `Cache/Cache_Data`. Ces réponses donnent les **échéances exactes des 5 heures et de la semaine**, sans rouvrir le terminal ni ajouter de réglage. Seules les réponses de l’organisation sélectionnée sont retenues. Aucun cookie, jeton ou appel réseau n’est utilisé ; les fichiers natifs restent inchangés. Les conversations ne sont pas décodées. Le format Chromium blockfile version 3 et les réponses compressées courantes sont pris en charge ; une entrée incomplète, un format inconnu ou une réponse absente laisse cette source indisponible.

L’historique version 2 fournit les pourcentages, mais pas les échéances. Le relevé le plus récent est retenu avec sa date d’observation originale, même après **Actualiser** ; une simple revalidation HTTP ne le redatera pas. Si l’historique apporte des pourcentages plus récents, les échéances exactes du cache HTTP sont conservées tant que leur période court, même si Desktop purge son cache ou si le tracker redémarre. Une échéance passée des 5 heures ne sert jamais à inventer la suivante : cette fenêtre redémarre à l’usage. Les valeurs peuvent donc rester anciennes ou sans échéance lorsque Desktop n’a pas reçu de réponse récente ; le tracker ne force ni son ouverture ni une requête Anthropic.

Si Desktop n’a pas fourni d’échéance des 5 heures encore valable, un second collecteur lit les **métadonnées des erreurs natives de quota du Code intégré**. Desktop associe chaque session CLI à un UUID de compte et d’organisation dans `claude-code-sessions/<compte>/<organisation>/local_*.json`. Seules ces sessions peuvent fournir une date : dans les dernières lignes de leurs fichiers locaux, le collecteur extrait `apiErrorParams.rate_limit_info.resetsAt` pour une erreur HTTP 429 de type `five_hour`. Il ne décode pas le texte des messages et ignore l’état représentatif `quotaLimits`, qui peut concerner une autre fenêtre. Ce format est vérifié pour Claude Code **2.1.293** et **2.1.295**, qui l’écrivent avec le même code ; les versions 2.1 suivantes sont acceptées, tandis qu’une version 2.2 ou ultérieure reste inconnue jusqu’à vérification.

Ce repli est automatique au démarrage et lors des actualisations périodiques, sans ouvrir de terminal, modifier les réglages natifs ou envoyer une requête. Il apporte uniquement la date exacte : les pourcentages et leur heure d’observation ne sont pas redatés. Une date fournie avant un relevé plus récent ayant moins de 100 % de consommation est ignorée ; une erreur plus récente annonçant une fenêtre terminée invalide une ancienne échéance. Les lectures sont bornées à 16 sessions récentes par racine, 512 dossiers de projet, 512 Kio de fin de fichier et 64 Kio par enregistrement ; les liens et les écritures incomplètes restent indisponibles. Si aucune erreur de quota récente n’a été enregistrée, ce repli ne peut pas créer une date manquante.

Quand aucune date exacte n’est disponible, l’estimation hebdomadaire existante reste en secours : une baisse nette de l’utilisation entre deux relevés espacés de 36 h au plus permet de projeter la semaine suivante, affichée **≈ … · estimé** avec sa fourchette au survol. Une date exacte fournie par Desktop ou le terminal remplace l’estimation. Les dates terminales encore valables restent conservées après l’expiration de sa session. Une date hebdomadaire passée peut ancrer les semaines suivantes ; aucune projection de ce type n’est faite pour les 5 heures. **Réglages → Général → Claude Code** indique séparément le réglage et la date du dernier relevé du terminal.

La lecture du cache ne dépend pas d’un jeton dans le fichier du terminal : Desktop transmet sa propre session au Code intégré. Si vous avez plusieurs profils, ouvrez chacun dans l’onglet Code pour que son identité soit observée, puis revenez au profil voulu. Le tracker conserve les précédents comptes et actualise uniquement celui sélectionné.

## Terminal Claude Code : recevoir les quotas et échéances

Claude Code transmet les limites de 5 heures et de la semaine à sa [barre de statut](https://code.claude.com/docs/en/statusline). Les champs apparaissent après une réponse dans une session avec un abonnement compatible (Pro / Max). Ils peuvent être absents indépendamment ; une absence reste inconnue.

1. Dans le tracker, ouvrez **Réglages → Général → Claude Code** et cliquez sur **Copier le réglage Claude Code**.
2. Ouvrez les réglages utilisateur de Claude Code, `%USERPROFILE%/.claude/settings.json`, ou `settings.json` sous `CLAUDE_CONFIG_DIR`.
3. Fusionnez l’entrée `hooks.SessionStart` avec vos hooks existants et ajoutez l’entrée `statusLine`. Conservez vos autres réglages. Si vous utilisez déjà une barre de statut, gardez votre script et ajoutez-lui l’appel au collecteur avec le même JSON d’entrée ; ne remplacez pas votre configuration sans en garder une copie.
4. Ouvrez une nouvelle session Claude Code et utilisez-la normalement. Les relevés arrivent automatiquement dans le tracker, même s’il démarre après Claude Code.

Le hook `SessionStart` associe la session à son compte avant la réception des quotas : une session restée ouverte sur un ancien compte ne peut pas actualiser le nouveau compte. Une session déjà associée à un autre compte conserve son association : après un changement de compte, ouvrez une nouvelle conversation plutôt que de reprendre cette ancienne session pour transmettre les quotas. La configuration utilise le chemin de l’exécutable courant ; recopiez-la si vous déplacez une version portable.

La barre de statut proposée affiche les quotas restants. Les commandes PowerShell encodées permettent de transmettre le JSON sur Windows avec des chemins contenant des espaces, sans lancer de fenêtre du tracker.

## Limites

- Seul le compte actif du dossier Claude configuré est actualisé. Les autres gardent leurs relevés datés. Les profils WSL et les autres dossiers de configuration ne sont pas parcourus automatiquement.
- Les relevés arrivent à l’usage de Claude Code. **Actualiser** relit la dernière observation locale ; il ne force pas une requête Anthropic et ne change pas son heure.
- Les clés API et les connexions Bedrock / Vertex / Foundry n’offrent pas ces quotas d’abonnement. Une session expirée demande de rouvrir Claude Code ; le tracker ne renouvelle pas ses jetons.
- Les réserves Codex, les annonces de resets généraux Codex et les déclarations manuelles de reset Codex ne s’appliquent pas à Claude Code.
- La configuration native n’est jamais modifiée automatiquement. Pour arrêter la réception, retirez uniquement les commandes du tracker des réglages Claude Code.

Les comptes, resets, rappels et historiques utilisent les mêmes composants que Codex. Le MCP expose le fournisseur et les deux comptes actifs ; il conserve les champs existants pour ses clients. Aucune conversation ni donnée de projet issue de stdin n’est conservée. Voir [confidentialité](PRIVACY.md), [authentification native](https://code.claude.com/docs/en/authentication) et [hooks Claude Code](https://code.claude.com/docs/en/hooks).
