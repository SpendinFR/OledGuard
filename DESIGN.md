# OledGuard V1 — architecture

## Règle principale

Le noir est l’état par défaut. Deux sources seulement peuvent retirer localement le masque :

1. une activité significative située dans la fenêtre au premier plan ;
2. la zone de découverte autour de la souris.

Les changements derrière la fenêtre au premier plan ne révèlent rien.

## Fenêtre au premier plan

`GetForegroundWindow` fournit le handle actif. `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)` fournit ses limites visibles, avec `GetWindowRect` en secours.

À chaque changement de fenêtre ou déplacement important :

- l’ancien historique d’activité est supprimé ;
- la nouvelle fenêtre est révélée quelques secondes ;
- l’analyse locale reprend ensuite.

## Détection d’activité

La capture GDI est directement réduite. Chaque cellule contient 3 × 3 échantillons par défaut. Deux images réduites sont comparées :

- les faibles changements demandent confirmation ;
- les cellules modifiées sont regroupées en composantes connexes ;
- une composante trop petite est ignorée ;
- une composante acceptée active son rectangle englobant avec une petite marge.

## Rendu

Deux champs de distance de Chebyshev sont calculés :

- activité de contenu, coupée aux limites de la fenêtre au premier plan ;
- activité de la souris, autorisée partout.

La distance de Chebyshev produit des contours carrés. L’opacité est quantifiée en plusieurs niveaux : transparent, gris sombres, noir. La texture est agrandie en nearest-neighbour, donc sans flou spatial. Le fondu reste temporel et fluide.

## Balayage de repos

À intervalle configurable, une bande noire légèrement diagonale traverse la zone visible de la fenêtre au premier plan. Elle n’éclaircit jamais l’image et n’emploie aucune couleur. Le passage est calculé sur la petite grille et ne requiert aucune texture supplémentaire pleine résolution.

## Budget mémoire

Pour un écran 4K, cellule 24 px et 3 échantillons :

- grille : environ 160 × 90 cellules ;
- capture réduite : environ 480 × 270 × 4 octets ;
- image précédente : même taille ;
- carte alpha : environ 160 × 90 × 4 octets ;
- tableaux de travail : nettement moins de 1 Mo.

La dépense dominante est la surface transparente gérée par WPF/DWM, généralement de l’ordre d’une à deux surfaces 4K. Objectif : moins de 100 Mo par écran 4K.
