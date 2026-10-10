# Suivre Claude Code

Codex Tracker détecte le profil sélectionné dans Claude Code sur Windows, conserve ses derniers relevés et distingue ses comptes de ceux de Codex. L’identité vient de `%USERPROFILE%/.claude.json` ; `%USERPROFILE%/.claude/.credentials.json` apporte les métadonnées d’abonnement lorsqu’une session terminal y est présente. Si `CLAUDE_CONFIG_DIR` est défini, ces fichiers sont lus dans ce dossier. Un dossier invalide ne provoque pas de retour silencieux vers un autre compte.

Les comptes personnels et d’entreprise sur une même adresse restent distincts grâce à leurs UUID de compte et d’organisation. Le nom d’organisation apparaît dans les vues. Un compte n’est ajouté qu’après observation de son identité native ; la présence d’une organisation dans un vieux cache ne suffit pas à l’attribuer à votre adresse.

## Application Claude : automatique

L’onglet Code de l’application Claude met à jour les mêmes métadonnées d’identité que le terminal. Le tracker lit automatiquement le fichier natif `plan-usage-history.json` sous `%APPDATA%/Claude`, ou sous `LocalCache/Roaming/Claude` du package Microsoft Store. Aucun hook ni réglage n’est nécessaire pour cette source.

Le cache fournit les pourcentages utilisés sur 5 heures et sur la semaine, identifiés par organisation. Leur date d’observation reste inchangée, même après **Actualiser**. Seule sa version 2 est acceptée : l’ancien format sans organisation, un champ absent ou un futur format restent inconnus. Le cache ne contient pas les échéances des resets. Claude renouvelle le quota de la semaine au même moment chaque semaine : dès qu’une remise à zéro est observée (baisse nette de l’utilisation de la semaine entre deux relevés espacés de 36 h au plus), le tracker en déduit le prochain reset hebdomadaire, affiché **≈ … · estimé** avec sa fourchette au survol. Il faut donc qu’une remise à zéro ait eu lieu pendant le suivi ; avant cela, l’échéance reste inconnue. Une date fournie par le terminal remplace toujours l’estimation, et reste conservée tant que sa période court, même si l’application Claude publie un relevé plus récent. Le quota de 5 heures, dont la fenêtre démarre à l’usage, n’est pas estimé. Ses dernières valeurs peuvent être anciennes : le tracker ne force ni l’ouverture de l’application ni une requête Anthropic.

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
