# OledGuard 0.5 — masque rectangulaire d’activité

## Objectif visuel

L’écran doit être noir loin de toute activité. Une zone utilisée reste nette à l’intérieur d’un rectangle cohérent, puis rejoint le noir par un dégradé court aux coins légèrement adoucis.

## Contenu

Les cellules modifiées sont regroupées par composantes proches. Chaque composante produit un rectangle rempli. Les rectangles encore actifs et proches sont fusionnés pour éviter les trous et les fragments.

Les très petites composantes utilisent un canal « micro » avec un dégradé minimal. Un curseur ou un point qui clignote ne doit donc pas ouvrir un grand halo.

## Souris

Un mouvement continu constitue un trait logique. Son rectangle englobant est agrandi par une petite marge. À chaque mouvement, toutes les cellules du rectangle reçoivent la même échéance. Le rectangle entier commence donc son fondu au même instant.

Après une courte pause, le prochain mouvement crée un nouveau rectangle au lieu d’agrandir indéfiniment l’ancien.

## Forme et fondu

Trois champs de distance sont calculés : contenu, micro-changements et souris. Le coût diagonal proche du coût horizontal crée des contours de type squircle, plus carrés que le champ euclidien de la version 0.4.

Le fondu temporel interpole chaque alpha proportionnellement vers le noir. La forme conserve ainsi son dégradé pendant sa disparition, au lieu de se refermer progressivement depuis les bords.

## Coût

Pour un écran 4K et une grille de 32 px, le moteur travaille sur environ 8 160 cellules et une carte de rendu d’environ 32 640 valeurs. Les trois champs de distance restent de quelques dizaines de kilo-octets chacun. Aucune image 4K historique n’est conservée.
