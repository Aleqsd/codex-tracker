# Architecture et contribution

## Flux principal

Codex → collecteur passif → `TrackerState` → fenêtre et icône. Les données connues restent disponibles lorsque Codex est fermé ou qu’un relevé échoue.

Le processus WPF possède les préférences, les identifiants DPAPI et le moteur de rappels. `ApplicationCommands` porte la validation et la révision partagées ; une modification des préférences ou des connecteurs change cette révision. Les formulaires ouverts refusent d’écraser une configuration modifiée ailleurs.

Assistant → MCP stdio → canal Windows privé → commandes du processus WPF. Le processus `--mcp` ne crée pas de fenêtre et n’écrit pas les données de l’application. Aucun serveur HTTP. Le SDK officiel gère la négociation du protocole MCP ; notre canal interne est versionné indépendamment.

Le pont se connecte dès son démarrage, même si aucun outil n’est utilisé. La fermeture du tracker ferme les canaux et les processus MCP ; le client assistant doit alors se reconnecter. Le remplacement de l’exécutable attend au maximum dix secondes sa libération et abandonne sans remplacement si un verrou subsiste.

## Ajouter un réglage

1. Définir sa valeur par défaut et sa normalisation dans les préférences. Ne jamais modifier les valeurs existantes au chargement sans migration explicite.
2. Ajouter sa validation à `ApplicationCommands` et utiliser celle-ci depuis le contrôle WPF.
3. S’il est pilotable, étendre le DTO MCP explicite : ne pas exposer l’objet de préférences complet, qui contient aussi des chemins privés et des états internes.
4. Tester l’ancienne configuration, la valeur invalide et une modification concurrente.

## Ajouter une vue

Utiliser `ThemeManager`, les styles d’App.xaml et les petits composants d’Ui.cs. Brancher une page explicite dans la navigation et le mode aperçu. Le mode démo utilise des adresses réservées aux exemples ; aucun chargement des profils réels. `PreviewClock` fige seulement la présentation, jamais la planification réelle.

## Ajouter un canal

Étendre les types métier, la configuration chiffrée et l’adaptateur de notification. Tous les envois passent par `ReminderDispatcher` : journal écrit avant l’envoi, une seule file, résultats incertains sans répétition automatique. Simuler HTTP, puis couvrir refus, expiration, limites et reprise. Ne jamais placer de secret dans un DTO de lecture MCP.

## Journaux et limites

`GlobalResetReader` lit un index public uniquement pour ses URL puis vérifie les originaux avec X oEmbed. `GlobalResetMonitor` est facultatif dans `TrackerServiceOptions`, absent des tests et démos sauf adaptateur simulé. Il vérifie toutes les 15 min, recule à 30 min après un échec et conserve uniquement des preuves publiques en mémoire. `TrackerState.GlobalResetFeed` projette cet état sans modifier le magasin de profils. Voir [les restrictions de reconnaissance](GLOBAL-RESETS.md).

`GlobalResetAnnouncement.Applies` exige une offre/fenêtre connue, un relevé antérieur à l’annonce initiale et un compte inactif. Les occurrences `global/{post}/{compte}/{type}`, Windows uniquement, réutilisent le journal et le dispatcher existants. Leur durée maximale de 24 h reste inférieure à la conservation des reçus de 30 jours. Le bouton de l’encart ouvre Resets ; le quota réel de l’icône ne change pas.

`StatusFor` fournit les mêmes règles à l’estimation et aux explications de l’interface. `IGlobalResetReader` permet de simuler une réponse tardive. Le moniteur sérialise les contrôles, annule par génération lors d’une désactivation ou veille et expose ses échéances ; l’événement de préférences le synchronise immédiatement avant de remettre à jour le WPF. Un contrôle manuel respecte un délai minimal et le recul après HTTP 429. `ReminderDispatcher` diffère les alertes publiques pendant une panne de source, sans perdre leur reçu ni déclencher un nouvel envoi externe.

`ExpectedReset` projette les échéances passées des comptes inactifs sans modifier les snapshots, historiques, prévisions ou conseils. Les occurrences locales `expected/…`, délai zéro et canal Windows uniquement, passent par `ReminderDispatcher` et son journal avant envoi. `ResetNotifications` les active ; le rattrapage est limité à 24 heures et à une fenêtre réelle. Les échéances futures utilisent toujours `ReminderPlanner`. Le journal conservé 30 jours couvre toute la durée de validité d’une estimation, empêchant son renvoi après purge.

Le journal des rappels conserve 30 jours et les clés nécessaires aux échéances futures. Les reçus d’actions MCP conservent uniquement UUID, empreinte, statut, date et résultat sans arguments. Ils ne sont pas purgés automatiquement, afin qu’un ancien UUID ne déclenche pas un second test ; au-delà de 10 000 reçus, les nouvelles mutations sont refusées. Une demande en attente ne conserve son contenu qu’en mémoire et expire après cinq minutes.

Les assistants peuvent consulter les quotas mais ne peuvent ni changer de compte Codex, ni exécuter des commandes arbitraires, ni éditer des fichiers. La modification du code se fait dans le dépôt avec les outils habituels de l’assistant.

## Agenda semaine (0.8)

`ResetCalendar` groupe les instants connus par date dans un fuseau explicite, du lundi au dimanche. Les heures répétées restent deux instants distincts. `PriorityReserve` exige un compteur serveur positif et une expiration future connue ; l’entrée conserve le relevé et ses erreurs. `ResetsView` partage ses filtres entre Agenda et Semaine et préserve la navigation à chaque collecte. Ces projections ne modifient ni les rappels ni les échéances.

## Mises à jour préparées (0.8.2)

`AutomaticUpdater`, possédé par la fenêtre principale, recherche après 15 secondes puis toutes les 15 minutes après un contrôle réussi. Il respecte le cache HTTP et les limites GitHub ; un téléchargement interrompu est retenté après 15 minutes. Aucun ordonnanceur dans les fenêtres de réglages ou les ponts MCP. Le mode démo et les exécutables de développement ne démarrent pas ce moteur.

`UpdateService.CheckChanged` signale chaque contrôle réseau terminé, y compris une réponse HTTP 304 ou un échec. `ReadLatestCheck` expose le résultat du canal actuel ; les résultats d’un ancien canal sont ignorés. Les réglages affichent la date du dernier contrôle, l’état du téléchargement et la prochaine échéance de l’ordonnanceur. La limite de cinq minutes des contrôles manuels reste distincte de la recherche automatique.

`UpdateService.PrepareAsync` sérialise les téléchargements, contrôle source, taille, SHA-256 et archive, puis publie atomiquement `updates/prepared-update.json`. Le chemin préparé est construit à partir d’un UUID validé. Le hash de l’exécutable est revérifié après redémarrage et avant installation. Une ancienne préparation valide reste utilisable si le téléchargement d’une version plus récente échoue.

Au démarrage normal, une préparation peut être installée avant l’ouverture des collecteurs. La tentative est enregistrée avant lancement du helper ; échec, arrêt ou restauration ne provoquent pas de boucle. Le démarrage à la demande d’un MCP et le contrôle de santé n’installent pas automatiquement. Le bouton de la fenêtre passe par le même helper, ferme proprement les ponts MCP, puis rouvre le tracker. Le helper conserve les vérifications, le délai de libération et la restauration existants.

Pour prévisualiser le bandeau avec des données fictives : ajouter `--demo-update` à une commande `--demo --preview ...`. Cette démonstration ne peut pas installer une mise à jour.

### Canaux de mise à jour (0.9)

Les versions stables sont sélectionnées par défaut. `IncludePrereleaseUpdates` active les préversions déclarées par GitHub ou par le suffixe SemVer. Les caches HTTP et les paquets prêts sont séparés ; les réponses d’un ancien canal ne remplacent pas celles du canal choisi. Les anciens paquets dont le statut bêta n’était pas mémorisé sont ignorés et doivent être retéléchargés. Le helper actualise la version affichée par Windows après contrôle du démarrage, seulement pour l’installation enregistrée correspondante.

### Récupération et diagnostic (0.9)

`RecoverableJsonFile` maintient une génération `.bak` validée pour les préférences, profils et relevés, et préserve le contenu endommagé avant remplacement. Il ne doit jamais restaurer un ancien journal d’envoi, reçu MCP ou identifiant. Une récupération des préférences coupe les rappels, alertes et MCP ; `RecoveryPending` garde l’avertissement visible jusqu’à un acquittement explicite. L’acquittement ne réactive rien.

`CodexCompatibilityDiagnostic` expose uniquement des enums et dates. Le rapport de support est une projection explicite, sans sérialisation des comptes, préférences complètes, chemins, exceptions ou secrets. `CODEX_HOME` est résolu au démarrage ; le modifier nécessite de relancer le tracker.

### Stockage et environnement Windows (0.9.2)

Avant toute préférence ou collecte réelle, `EntryPoint` vérifie le chemin physique d’un fichier temporaire. Un processus lancé depuis un environnement MSIX peut hériter de sa redirection même sans identité de package. `DesktopEnvironment` relance alors le tracker avec le contexte du bureau, sans élévation. Le nouveau processus revérifie le chemin et refuse une seconde redirection : jamais de boucle ou de profil vide de remplacement. Le mode démo reste isolé ; le pont MCP conserve ses flux standard et n’ouvre aucun stockage.

Sous le verrou exclusif du magasin normal, `ProfileStore.Recovery` rapproche une seule fois les anciens profils du cache Codex par adresse, choisit les relevés selon `FetchedAt`, réattribue les historiques et sauvegarde les fichiers d’origine. Les sources restent intactes. Le journal de migration empêche de réimporter un compte supprimé volontairement. Un journal endommagé bloque la fusion ; ne jamais effacer ce journal pour contourner une erreur. Voir [STORAGE-RECOVERY.md](STORAGE-RECOVERY.md).

## Comptes Claude Code et resets déclarés

`AccountProfile.Provider` vaut Codex pour les anciens profils ; un profil Claude Code portant la même adresse reste distinct. `ProviderAccountId` associe UUID de compte et UUID d’organisation : les organisations personnelle et professionnelle sur une même adresse gardent des profils et historiques séparés. `TrackerService` partage la persistance et la télémétrie mais conserve une génération, une annulation et une collecte indépendantes par outil. Une modification Claude n’entraîne pas de requête Codex. Les deux comptes actifs apparaissent dans le tableau de bord ; l’icône privilégie Codex lorsqu’ils sont tous deux connectés.

Claude Code est détecté dans `.claude.json`, ou le fichier équivalent sous `CLAUDE_CONFIG_DIR`. `.claude/.credentials.json` fournit des métadonnées complémentaires lorsqu’il contient une connexion terminal ; Desktop n’en a pas besoin. Seules les métadonnées d’identité, d’organisation et d’abonnement sont retenues. Aucune requête utilisant le jeton Claude, connexion, rotation de jeton ou bascule de compte n’est effectuée.

`ClaudeDesktopUsageReader` lit passivement `plan-usage-history.json` sous le dossier Roaming de Claude, classique ou du package Microsoft Store. Seule la version 2 avec organisation explicite est acceptée. `t` est une observation en millisecondes Unix ; `u.fh` et `u.sd` sont des pourcentages utilisés. Le dernier échantillon de l’organisation sélectionnée est lu, sans redater les valeurs. `WeeklyResetInference` repère la dernière remise à zéro hebdomadaire (baisse d’au moins 10 points vers 30 % utilisés ou moins, entre deux relevés espacés de 36 h au plus) dans l’historique du tracker et le cache Desktop, puis projette la suivante par semaines entières. Une date passée fournie par le terminal (échantillon sans `WeeklyResetEstimated`) est un point d’ancrage exact : elle prime si la remise à zéro observée la contient à une semaine près, sinon la preuve la plus récente l’emporte. `QuotaWindow.EstimatedResetFrom` porte la fourchette, nulle pour une projection exacte ; l’interface affiche « ≈ » et « estimé ». Une date du terminal pour la période en cours est conservée face à un relevé Desktop plus récent et prime toujours sur l’estimation. Un quota plein mesuré réarme les seuils comme pour une fenêtre sans date. La version 1 sans organisation et les formats futurs restent inconnus. Les moniteurs surveillent ce fichier sans ouvrir la base de sessions, la configuration Desktop ou son cache de jetons. Le relevé le plus récent entre cache Desktop et collecteur terminal est prioritaire.

La cadence périodique est avancée par la collecte commune, y compris avec Claude seul. Les observations natives immédiates ne reportent pas la prochaine collecte Codex. Sans date de reset, un quota mesuré à 100 % réarme les alertes de seuil, sans notification de reset ni échéance inventée. Une correction partielle garde la déduplication ; les observations anciennes sont ignorées. Ce réarmement reste conservé après redémarrage et répare aussi les anciens masques associés à une observation complète.

`--claude-session-start` associe la session native à son compte. `--claude-statusline` reçoit le JSON documenté via stdin, extrait uniquement `rate_limits.five_hour` et `seven_day`, puis dépose un relevé atomique dans `claude-observations`. Une session d’un ancien compte est ignorée après un changement d’identité. Les callbacks sont sérialisés ; ils ne possèdent ni préférences ni magasin de profils et n’ouvrent aucune fenêtre. La configuration proposée doit être ajoutée par l’utilisateur ; les réglages natifs existants ne sont jamais modifiés automatiquement.

`ManualCodexReset` est une déclaration datée dans les préférences, enregistrée par `ApplicationCommands` avec contrôle de révision. `QuotaPresentation` affiche 100 % pour les comptes Codex sans relevé postérieur, y compris le compte actif. Cette projection ne modifie ni snapshots, ni télémétrie, ni réserves. Elle expire par fenêtre (5 h / 7 jours), se retire devant un relevé plus récent et peut être annulée. Les anciennes prochaines échéances ne sont plus planifiées ni exportées pendant la déclaration ; aucune nouvelle échéance serveur n’est inventée.
