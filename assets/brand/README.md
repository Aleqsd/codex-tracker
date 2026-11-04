# Logo et illustrations

`owl.svg` est le logo vectoriel : le hibou 05 redessiné en formes géométriques régulières (ellipses, oreilles en courbes, traits à bouts ronds), un seul chemin de lignes et de courbes de Bézier. Il remplace le PNG 05 pour l’icône, le site, l’installateur et l’application.

Six hiboux, chacun en trois versions : encre blanche sur fond transparent, encre sombre sur fond transparent (`_dark_ink`) et blanc sur fond sombre (`_dark_background`). `codex_tracker_icons_planche.png` les présente ensemble ; `codex_tracker_principal_app.ico` est l’icône livrée avec la planche.

| Illustration | Usage |
| --- | --- |
| 05 Hibou mascotte (principal) | Logo (`owl.svg`) : icône Windows, site, installateur, README, barre de titre, page Application, bandeau des nouveautés |
| 01 Hibou joyeux au terminal | Réglages → Général → Claude Code ; README, installation |
| 02 Hibou grands yeux | Accueil sans compte ; README, présentation |
| 03 Hibou endormi | Liste Resets vide ; pied des README |
| 04 Hibou à lunettes | Réglages → Assistants ; README, pour aller plus loin |
| 06 Hibou dans une fenêtre de terminal | Carte du premier lancement ; README, confidentialité |

`python scripts/brand.py` produit à partir de `owl.svg` et des autres versions blanches `src/CodexTracker.App/Assets/App.ico`, `site/icon.png`, `installer/Assets/*.bmp`, le pinceau `MascotMain` d’App.xaml, les silhouettes de `src/CodexTracker.App/Assets/Mascot` et les images des README dans `docs/brand` (une par thème GitHub).
