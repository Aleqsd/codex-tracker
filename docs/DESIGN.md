# Design de Codex Tracker

## Principes

Interface française, sobre, neutre et compacte. Afficher l’information essentielle ; placer les détails dans le survol, une section repliable ou une vue dédiée. Les compteurs de réserves sont du texte, pas de faux boutons.

`ThemeManager` est la source des couleurs. Utiliser les ressources dynamiques `BackgroundBrush`, `PanelBrush`, `TextBrush`, `MutedBrush`, `LineBrush`, `ButtonBrush`. Les fonds sombres sont proches de #181818, le texte de #ECECE8 ; le thème clair part de #FAFAF8. Garder les valeurs exactes dans la palette existante. Orange et rouge signalent un état, jamais une décoration.

## Composants

- Titres 21–23 px, sections 13 px semi-gras, texte 12–13 px, légendes 11 px.
- Espacement : 7–8 px entre éléments liés, 16–24 px entre groupes. Réutiliser les marges de la vue concernée.
- Boutons standards, `PrimaryButton` pour l’action principale, `QuietButton` pour une action secondaire.
- Dropdowns : style existant avec icône, libellé et chevron ; ne pas utiliser l’objet de données comme texte.
- Icônes vectorielles provenant des ressources existantes, avec de la marge autour du tracé.
- Initiales sur fond coloré en l’absence de photo ; fond neutre du thème derrière une image transparente.
- Réglages dans le troisième onglet de la fenêtre, à côté de Comptes et Resets. Sections à gauche, formulaire à droite. Défilement indépendant des deux colonnes et labels lisibles en petite fenêtre. Conserver la section et les saisies lorsque l’utilisateur passe à un autre onglet.
- Pied de fenêtre : état de collecte à gauche, version réellement exécutée à droite en texte discret. Réserver sa largeur pour qu’elle reste lisible en fenêtre compacte ; ne pas afficher la version distante à cet endroit.
- Confirmation MCP : action, destinataire, effet et expiration ; jamais afficher de clé.

## Aperçus reproductibles

`scripts/dev.ps1 -Action Preview` lance les vraies vues en mode démo avec une horloge de présentation fixe. `-View` choisit Comptes, Resets ou une section des réglages ; `-Theme`, `-Size` et `-Dpi` contrôlent le rendu.

`-Matrix` produit 48 captures : Comptes/Resets/Semaine/Assistants × clair/sombre × normal/compact × 96/144/192 DPI. Le rendu dépend encore des polices et du fuseau Windows ; comparer les captures sur le même environnement. Les captures sont des rendus WPF à ces résolutions, complétés par les contrôles d’interaction ; elles ne remplacent pas un déplacement manuel entre écrans physiques.

Vérifier le débordement, les textes coupés, le focus clavier et la distinction des états. Les dates inconnues restent explicitement inconnues.

## Resets : agenda et semaine

Les annonces publiques vérifiées réutilisent l’encart d’estimation. **Voir** ouvre Resets, avec date, fuseau, comptes concernés et liens vers la confirmation et la portée. La mention « annoncé comme terminé » distingue la déclaration publique d’un quota mesuré. `--demo --demo-global-resets` produit des aperçus fictifs ; la suite WPF couvre clair/sombre, normal/compact et 96/144/192 DPI.

`GlobalResetCard` garde les sources et la durée de validité visibles ; les raisons par compte et par fenêtre sont dans une section repliable qui reste ouverte après collecte. Les comptes sans estimation ont une explication neutre ; « relevé plus récent » ne devient jamais « reset confirmé ». Réglages sépare la vérification des annonces de celle des quotas, avec un bouton dédié et les heures du dernier succès et du prochain essai.

Les comptes inactifs dont une échéance connue vient de passer ont un encart sobre et une annotation `≈100 % — estimé`. Le bouton **Voir** fait défiler la liste jusqu’au compte concerné, y compris en petite fenêtre. Le survol donne date, dernier pourcentage mesuré et hypothèse d’absence d’utilisation ailleurs. `GoodBrush` accompagne le texte sans remplacer l’indication d’incertitude. L’aperçu `--demo --demo-resets` utilise un compte fictif dans cet état.

Le choix Agenda / Semaine utilise `ResetKindFilter` dans un groupe radio distinct des types de resets. La semaine garde sept colonnes, marque aujourd’hui par un trait neutre et utilise les mêmes icônes de type. Les comptes longs sont tronqués avec un survol complet. La priorité est un encart informatif sans bordure de bouton ; seules les commandes de navigation sont cliquables. L’aperçu `-View Semaine` entre dans la matrice de démonstration.

## Tour animé du README

Après les aperçus normaux à 96 DPI, `python scripts/tour.py` (Pillow) assemble six écrans fictifs : Comptes, Semaine, Resets, Rappels, Général en clair et Assistants. Chaque écran reste 1,8 seconde, avec une transition de 120 ms, des légendes courtes et une progression discrète. Le GIF utilise une palette commune et une boucle de 11,52 secondes. Ne jamais utiliser des captures des comptes réels.
