# Annonces de resets généraux

Le tracker peut signaler qu’un compte inactif est **probablement à 100 %** après une annonce publique de remise à zéro. Cela ne confirme jamais le quota réel d’un compte : il faut l’ouvrir dans Codex pour recevoir un nouveau relevé.

- Vérification au démarrage puis toutes les **15 minutes** ; nouvel essai après 30 minutes en cas d’échec.
- Notification Windows regroupée, dédupliquée dans le journal local. Elle suit **Prévenir après un reset**. Aucun nouveau SMS, appel ou email pour ces annonces.
- Estimation hebdomadaire limitée à **24 heures** après le message d’achèvement, et à 5 heures pour la fenêtre courte. Une nouvelle observation ou l’activation du compte dans Codex la retire immédiatement.
- Les comptes Free ne sont pas concernés par une annonce réservée aux abonnements payants. Offre inconnue, fenêtre manquante, observation vieille de plus de 30 jours ou abonnement connu comme terminé : aucune estimation.
- Les observations antérieures à l’annonce initiale sont seules admissibles. Un relevé reçu pendant le déploiement reste prioritaire.

**Réglages → Rappels → Annonces de resets généraux** affiche l’état et la date de vérification. L’option peut être désactivée indépendamment des rappels habituels. Les sources sont consultables au clavier et à la souris dans l’onglet **Resets**.

## Sources et prudence

L’[index public communautaire de shixi-11](https://shixilin.com/ai/codex-claude-resets/) sert uniquement à découvrir des URL. Ses classifications, traductions et textes ne décident jamais d’un reset. Aucun code de ce projet n’est intégré.

Le tracker relit le texte original via **publish.x.com/oembed**, vérifie l’URL et l’auteur (`thsottiaux`, `OpenAI` ou `OpenAIDevs`) et dérive la date de l’identifiant du post. Les requêtes HTTPS sont bornées, sans redirection, cookie, authentification ni donnée de compte. Les services reçoivent les métadonnées ordinaires d’une connexion, notamment l’adresse IP.

Seules quelques formulations explicites en anglais, nommant Codex, l’achèvement et les utilisateurs concernés, sont reconnues automatiquement. Une promesse, un crédit en réserve, une restriction ou une portée ambiguë ne suffisent pas. Une correction explicite ultérieure présente dans l’index suspend l’estimation. Ce mécanisme n’est pas un flux officiel exhaustif : omission, suppression, changement de formulation ou panne d’une source peuvent empêcher la détection.

X oEmbed ne prouve pas le lien entre deux messages. Une confirmation vague ne récupère donc jamais automatiquement la portée d’un autre post proche. La version 0.9.5 contient une seule association relue ensemble : l’[annonce pour les utilisateurs payants du 26 septembre 2026](https://x.com/thsottiaux/status/2103637477760311522) et sa [confirmation d’achèvement](https://x.com/thsottiaux/status/2103911959544610829). Les deux originaux doivent rester vérifiables. Une future paire ambiguë nécessitera une nouvelle revue et une mise à jour ; les annonces autonomes reconnues sont détectées sans mise à jour.

## Données conservées

Les quotas, échéances, crédits, historique, prévisions et pourcentage de l’icône restent les mesures de Codex. Les estimations constituent une présentation séparée. Dans Resets, les anciennes dates sont explicitement à reconfirmer ; aucune nouvelle échéance n’est inventée. Pendant l’estimation, les rappels de ces anciennes échéances de quota sont suspendus, sans modifier les règles ; les réserves restent indépendantes.

Les preuves publiques restent en mémoire. Au redémarrage, elles doivent être revérifiées ; les notifications déjà envoyées restent dédupliquées par le journal persistant. En cas de panne après une vérification réussie, les preuves précédentes restent datées et expirent normalement. Désactiver la détection retire immédiatement ses estimations et annule les notifications encore différées.

La démo `--demo --demo-global-resets` utilise uniquement des comptes, liens et annonces fictifs, sans réseau.
