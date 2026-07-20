# OledGuard 0.6 — grille carrée nette

Le rendu utilise des rectangles WPF nets, sans interpolation bitmap ni flou spatial. La grille d’analyse et la grille de rendu sont identiques.

La capture est réduite à quelques centaines de pixels de largeur. Chaque cellule conserve sa durée de stabilité, son expiration de révélation et son alpha courant.

Les changements de contenu rendent immédiatement visible la cellule concernée. Un passage morphologique minimal remplit uniquement les trous entourés de plusieurs cellules modifiées. Un point isolé reste donc limité à sa propre cellule.

Les cellules touchées pendant un trajet de souris sont regroupées dans une session de mouvement. À la fin du trajet, elles reçoivent toutes la même expiration afin de revenir au noir simultanément.
