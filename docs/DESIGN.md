# Design de Codex Tracker

## Principes

Interface française, compacte et calme : fonds neutres légèrement froids, surfaces en cartes arrondies et un seul accent indigo. Afficher l’information essentielle ; placer les détails dans le survol, une section repliable ou une vue dédiée. Les compteurs de réserves sont du texte, pas de faux boutons.

`ThemeManager` est la source des couleurs. Utiliser les ressources dynamiques (`BackgroundBrush`, `PanelBrush`, `RaisedBrush`, `TextBrush`, `MutedBrush`, `SubtleBrush`, `LineBrush`, `ButtonBrush`, `SegmentBrush`…). Fond sombre #101114, cartes #17181C, texte #ECEDF0 ; thème clair #F4F5F7, cartes #FFFFFF, texte #16171A. L’accent (`AccentBrush`, `AccentSoftBrush`, `PrimaryButtonBrush`) porte les jauges de quota, la sélection, le focus clavier et l’action principale ; aucune autre couleur décorative. Vert, orange et rouge (et leurs variantes `…SoftBrush` pour les encarts) signalent un état, jamais une décoration. Garder les valeurs exactes dans la palette existante.

## Composants

- Titres 21–23 px, sections 13 px en Medium, texte 12–13 px, légendes 11 px. Roboto Regular (400) pour le texte courant, Medium (500) pour les titres intermédiaires et Bold (700) pour les emphases fortes. Les trois fichiers statiques sont embarqués, avec leur provenance dans `Assets/Fonts/SOURCES.md` et leur licence dans `docs/licenses` ; aucune installation de police.
- Toutes les fenêtres, y compris l’aperçu de la barre d’état et les dialogues, appliquent explicitement le style `Window` d’App.xaml : ressource partagée `AppFontFamily` (Roboto), métriques Ideal, lissage Auto et hinting Fixed. Le positionnement naturel des glyphes évite les contours en crans du mode Display/Grayscale aux petites tailles. Les fonds opaques autorisent ClearType ; WPF respecte les réglages du système et gère les éléments qui introduisent de la transparence. L’alignement de la mise en page sur les pixels reste actif ; aucun réglage global de Windows n’est modifié. Le menu natif de l’icône utilise Roboto Regular chargée en mémoire, uniquement dans le processus du tracker.
- Espacement : 7–8 px entre éléments liés, 16–24 px entre groupes. Réutiliser les marges de la vue concernée. Cartes (`Card`, `Ui.Panel`) : rayon 12 px, bordure `LineBrush` ; boutons et champs : rayon 8 px.
- Navigation principale : contrôle segmenté centré dans la barre de titre (Comptes, Resets, Réglages). Les bandeaux de mise à jour et de récupération s’affichent au-dessus du pied de fenêtre.
- Comptes actifs : une tuile par outil avec anneau `QuotaRing` (quota hebdomadaire), puis 5 h, reset et réserves en lignes libellé/valeur. Liste des comptes : une carte par fournisseur, barre de quota courte sous le pourcentage, trait d’accent sur le compte actif.
- Boutons standards, `PrimaryButton` pour l’action principale, `QuietButton` pour une action secondaire, `LinkButton` pour un lien, `IconButton`/`Ui.IconButton` pour une icône seule. Le focus clavier est un anneau d’accent (`FocusRing`), invisible au clic souris.
- Préférences marche/arrêt : `CheckBox` au style `Switch` (libellé à gauche, interrupteur à droite). Les choix multiples restent des cases à cocher.
- Dropdowns : style existant avec icône, libellé et chevron ; ne pas utiliser l’objet de données comme texte.
- Icônes vectorielles provenant des ressources existantes, avec de la marge autour du tracé.
- Le pourcentage dans l’icône de notification utilise Roboto Medium embarquée avec le rendu WPF Display et un lissage en niveaux de gris, directement à la taille physique de la barre des tâches (`SM_CXSMICON` au DPI de `Shell_TrayWnd`). La taille de police est choisie d’après l’encre visible ; les chiffres sont centrés sur des pixels entiers, puis leurs pixels et leur transparence sont copiés sans redimensionnement dans l’icône native. La barre de quota garde une ligne nette, et la clé du cache inclut la taille. Windows détermine la taille de l’emplacement.
- Initiales sur fond coloré en l’absence de photo ; fond neutre du thème derrière une image transparente.
- Réglages dans le troisième onglet de la fenêtre, à côté de Comptes et Resets. Sections à gauche (pastille d’accent sur la section active), formulaire à droite en cartes titrées dont les lignes sont séparées par un filet. Défilement indépendant des deux colonnes et labels lisibles en petite fenêtre. Conserver la section et les saisies lorsque l’utilisateur passe à un autre onglet.
- Pied de fenêtre : point d’état (vert après un relevé, orange pendant la collecte, rouge en échec) et état de collecte à gauche, version réellement exécutée à droite en texte discret ; une pastille « Démo » signale les données fictives. Réserver sa largeur pour qu’elle reste lisible en fenêtre compacte ; ne pas afficher la version distante à cet endroit.
- Confirmation MCP : action, destinataire, effet et expiration ; jamais afficher de clé.

## Animations

Navigation : une pastille glisse vers l’onglet, la vue Liste/Semaine ou la section des réglages choisie (320 ms, léger rebond). Les pages montent de 8 à 10 px en fondu (200–300 ms) ; tuiles, comptes, échéances et blocs des réglages entrent en cascade (38 ms d’écart, dix éléments au plus). À l’affichage, les anneaux de quota se remplissent et les barres poussent depuis la gauche ; le pourcentage écrit reste toujours la valeur mesurée. Les interrupteurs font glisser leur curseur et révèlent l’accent en fondu, les coches rebondissent, les chevrons tournent avec un léger dépassement. Boutons principaux et boutons d’icône grossissent un peu au survol et se compriment à l’appui (80 ms) ; l’icône d’actualisation fait un tour. Pendant une collecte, un halo pulse autour du point d’état. Fenêtres, dialogues, aperçu de la barre d’état, bandeaux et détails repliables apparaissent en montant légèrement. Les animations restent décoratives : aucune ne retarde la navigation, ne bloque une saisie ni n’affiche une valeur de quota intermédiaire.

`UiMotion` respecte la désactivation des animations Windows et le contraste élevé. Les rafraîchissements automatiques ne relancent ni cascades ni transitions de navigation. Les éléments masqués ou retirés arrêtent leur animation ; les aperçus `--preview` et les captures `--screenshot`, `--peek-screenshot`, `--details-screenshot` et `--settings-screenshot` montrent directement l’état final pour rester reproductibles. Les couleurs restent liées aux ressources dynamiques du thème.

## Aperçus reproductibles

`scripts/dev.ps1 -Action Preview` lance les vraies vues en mode démo avec une horloge de présentation fixe. `-View` choisit Comptes, Resets ou une section des réglages ; `-Theme`, `-Size` et `-Dpi` contrôlent le rendu.

`-Matrix` produit 64 captures : Comptes/Resets/Semaine/Assistants × clair/sombre × normal/compact × 96/120/144/192 DPI. Le cas 120 DPI couvre le rendu à 125 %. Le rendu dépend encore des polices et du fuseau Windows ; comparer les captures sur le même environnement. Les captures sont des rendus WPF à ces résolutions, complétés par les contrôles d’interaction ; elles ne remplacent pas un déplacement manuel entre écrans physiques.

Vérifier le débordement, les textes coupés, le focus clavier et la distinction des états. Les dates inconnues restent explicitement inconnues.

Un correctif local installé doit utiliser une préversion du **prochain** numéro de patch (par exemple `0.9.8-polices.1` après la stable `0.9.7`). Un suffixe ajouté au numéro stable déjà publié (`0.9.7-local`) le rend plus ancien selon SemVer : le moteur de mise à jour peut alors réinstaller la stable et effacer le correctif. Vérifier le numéro visible, puis le hash du binaire après la recherche automatique et un redémarrage normal.

## Resets : agenda et semaine

Les annonces publiques vérifiées réutilisent l’encart d’estimation. **Voir** ouvre Resets, avec date, fuseau, comptes concernés et liens vers la confirmation et la portée. La mention « annoncé comme terminé » distingue la déclaration publique d’un quota mesuré. `--demo --demo-global-resets` produit des aperçus fictifs ; la suite WPF couvre clair/sombre, normal/compact et 96/144/192 DPI.

`GlobalResetCard` garde les sources et la durée de validité visibles ; les raisons par compte et par fenêtre sont dans une section repliable qui reste ouverte après collecte. Les comptes sans estimation ont une explication neutre ; « relevé plus récent » ne devient jamais « reset confirmé ». Réglages sépare la vérification des annonces de celle des quotas, avec un bouton dédié et les heures du dernier succès et du prochain essai.

Les comptes inactifs dont une échéance connue vient de passer ont un encart sobre et une annotation `≈100 % — estimé`. Le bouton **Voir** fait défiler la liste jusqu’au compte concerné, y compris en petite fenêtre. Le survol donne date, dernier pourcentage mesuré et hypothèse d’absence d’utilisation ailleurs. `GoodBrush` accompagne le texte sans remplacer l’indication d’incertitude. L’aperçu `--demo --demo-resets` utilise un compte fictif dans cet état.

La page Resets affiche d’abord une liste chronologique des prochaines échéances : icône officielle du fournisseur, compte, type, délai et heure locale. Une ligne s’ouvre pour retrouver date exacte, fuseau et relevé. Les réserves Codex, dates à confirmer, dates inconnues et sources d’un reset général sont repliées par défaut ; leur état reste conservé après collecte. Le bouton **Voir** d’une annonce ouvre directement ses sources. Les filtres de compte et de type sont des listes déroulantes ; le menu **…** contient l’export Google Agenda.

Le choix Liste / Semaine utilise `ResetKindFilter`. La semaine présente sept jours lisibles avec leur nombre d’échéances, puis la même liste. Un clic sur un jour filtre les lignes ; un second clic réaffiche toute la semaine. Aujourd’hui porte un trait neutre. La sélection reste conservée après collecte. Les comptes longs sont tronqués avec un survol complet. L’aperçu `-View Semaine` entre dans la matrice de démonstration.

## Tour animé du README

Après les aperçus normaux à 96 DPI, `python scripts/tour.py` (Pillow) assemble six écrans fictifs : Comptes, Semaine, Resets, Rappels, Général en clair et Assistants. Chaque écran reste 1,8 seconde, avec une transition de 120 ms, des légendes courtes et une progression discrète. Le GIF utilise une palette commune et une boucle de 11,52 secondes. Ne jamais utiliser des captures des comptes réels.

## Plusieurs outils et reset déclaré

Les comptes actifs de Codex et Claude Code partagent le composant existant, avec les icônes officielles des applications. La liste forme deux cartes avec icône, nom et nombre de comptes en en-tête ; l’actif vient en premier dans chaque section. Les identités personnelle et d’entreprise Claude portent leur nom d’organisation. Le tableau de bord défile entièrement en fenêtre compacte. Les réserves ne sont pas proposées pour Claude Code. `--demo-claude` produit exclusivement des comptes fictifs, dont une même adresse chez les deux fournisseurs et deux organisations Claude sur cette adresse.

Les icônes officielles sont conservées telles quelles sous `Assets/Providers`, avec leur provenance dans `SOURCES.md`. Le thème choisit la variante Codex claire/sombre fournie par OpenAI. L’icône Claude garde ses couleurs officielles ; aucune couleur de marque n’est ajoutée à la palette de l’application. Les transitions de navigation, les survols et le focus clavier existants sont conservés.

**Reset Codex…** ouvre un formulaire avec date, heure locale, portée et annulation. Une heure invalide, future ou ambiguë lors du changement d’heure est refusée. La déclaration donne **100 % · déclaré**, avec son heure et le quota réellement mesuré au survol. Elle garde un encart daté tant qu’un compte est concerné. `--demo-manual-reset` permet de vérifier ce rendu sans toucher aux comptes réels.
