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
- Sections de réglages à gauche, formulaire à droite. Défilement vertical et labels lisibles en petite fenêtre.
- Confirmation MCP : action, destinataire, effet et expiration ; jamais afficher de clé.

## Aperçus reproductibles

`scripts/dev.ps1 -Action Preview` lance les vraies vues en mode démo avec une horloge de présentation fixe. `-View` choisit Comptes, Resets ou une section des réglages ; `-Theme`, `-Size` et `-Dpi` contrôlent le rendu.

`-Matrix` produit 36 captures : Comptes/Resets/Assistants × clair/sombre × normal/compact × 96/144/192 DPI. Le rendu dépend encore des polices et du fuseau Windows ; comparer les captures sur le même environnement. Les captures sont des rendus WPF à ces résolutions, complétés par les contrôles d’interaction ; elles ne remplacent pas un déplacement manuel entre écrans physiques.

Vérifier le débordement, les textes coupés, le focus clavier et la distinction des états. Les dates inconnues restent explicitement inconnues.
