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

Le journal des rappels conserve 30 jours et les clés nécessaires aux échéances futures. Les reçus d’actions MCP conservent uniquement UUID, empreinte, statut, date et résultat sans arguments. Ils ne sont pas purgés automatiquement, afin qu’un ancien UUID ne déclenche pas un second test ; au-delà de 10 000 reçus, les nouvelles mutations sont refusées. Une demande en attente ne conserve son contenu qu’en mémoire et expire après cinq minutes.

Les assistants peuvent consulter les quotas mais ne peuvent ni changer de compte Codex, ni exécuter des commandes arbitraires, ni éditer des fichiers. La modification du code se fait dans le dépôt avec les outils habituels de l’assistant.

## Agenda semaine (0.8)

`ResetCalendar` groupe les instants connus par date dans un fuseau explicite, du lundi au dimanche. Les heures répétées restent deux instants distincts. `PriorityReserve` exige un compteur serveur positif et une expiration future connue ; l’entrée conserve le relevé et ses erreurs. `ResetsView` partage ses filtres entre Agenda et Semaine et préserve la navigation à chaque collecte. Ces projections ne modifient ni les rappels ni les échéances.

## Mises à jour préparées (0.8.2)

`AutomaticUpdater`, possédé par la fenêtre principale, recherche après 15 secondes puis toutes les six heures. Il respecte le cache HTTP et les limites GitHub ; un téléchargement interrompu est retenté après 15 minutes. Aucun ordonnanceur dans les fenêtres de réglages ou les ponts MCP. Le mode démo et les exécutables de développement ne démarrent pas ce moteur.

`UpdateService.PrepareAsync` sérialise les téléchargements, contrôle source, taille, SHA-256 et archive, puis publie atomiquement `updates/prepared-update.json`. Le chemin préparé est construit à partir d’un UUID validé. Le hash de l’exécutable est revérifié après redémarrage et avant installation. Une ancienne préparation valide reste utilisable si le téléchargement d’une version plus récente échoue.

Au démarrage normal, une préparation peut être installée avant l’ouverture des collecteurs. La tentative est enregistrée avant lancement du helper ; échec, arrêt ou restauration ne provoquent pas de boucle. Le démarrage à la demande d’un MCP et le contrôle de santé n’installent pas automatiquement. Le bouton de la fenêtre passe par le même helper, ferme proprement les ponts MCP, puis rouvre le tracker. Le helper conserve les vérifications, le délai de libération et la restauration existants.

Pour prévisualiser le bandeau avec des données fictives : ajouter `--demo-update` à une commande `--demo --preview ...`. Cette démonstration ne peut pas installer une mise à jour.

### Canaux de mise à jour (0.9)

Les versions stables sont sélectionnées par défaut. `IncludePrereleaseUpdates` active les préversions déclarées par GitHub ou par le suffixe SemVer. Les caches HTTP et les paquets prêts sont séparés ; les réponses d’un ancien canal ne remplacent pas celles du canal choisi. Les anciens paquets dont le statut bêta n’était pas mémorisé sont ignorés et doivent être retéléchargés. Le helper actualise la version affichée par Windows après contrôle du démarrage, seulement pour l’installation enregistrée correspondante.

### Récupération et diagnostic (0.9)

`RecoverableJsonFile` maintient une génération `.bak` validée pour les préférences, profils et relevés, et préserve le contenu endommagé avant remplacement. Il ne doit jamais restaurer un ancien journal d’envoi, reçu MCP ou identifiant. Une récupération des préférences coupe les rappels, alertes et MCP ; `RecoveryPending` garde l’avertissement visible jusqu’à un acquittement explicite. L’acquittement ne réactive rien.

`CodexCompatibilityDiagnostic` expose uniquement des enums et dates. Le rapport de support est une projection explicite, sans sérialisation des comptes, préférences complètes, chemins, exceptions ou secrets. `CODEX_HOME` est résolu au démarrage ; le modifier nécessite de relancer le tracker.
