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
