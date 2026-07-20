# Conception du moteur cumulatif 3.2

## Dette d'exposition

Chaque cellule conserve une valeur en secondes équivalentes à pleine luminance. Pour une capture stable :

`gain = durée × poids_luminance × lumière_restante`

Le poids de luminance est nul sous le seuil configuré, puis augmente avec une courbe de puissance 1,6. Le blanc fixe cumule donc beaucoup plus vite qu'un gris sombre.

La lumière restante vaut approximativement `1 - opacité`. Une cellule déjà assombrie continue d'accumuler, mais moins vite puisqu'elle émet réellement moins de lumière.

## Mouvement et interruption courte

Un changement soutenu révèle la zone, mais ne remet pas la dette à zéro :

`perte = durée × taux_de_décroissance`

Le taux par défaut est 0,20 pendant un vrai mouvement. Une interruption d'une seconde ne retire donc que 0,2 seconde de dette. Une zone incertaine décroît encore plus lentement, à 0,03 par défaut.

## Transformation de la dette en opacité

- avant `ExposureStartMinutes`, la cible est nulle ;
- entre le seuil de début et le seuil maximal, une interpolation smoothstep évite une apparition brutale ;
- après `ExposureFullMinutes`, la cible atteint `MaximumMaskOpacity`.

Le mouvement supprime temporairement la région du masque. Après `ReapplyDelaySeconds`, la cible calculée depuis la dette redevient active sans recommencer tout le compteur.

## Persistance

Chaque écran et chaque géométrie de grille possèdent un fichier binaire séparé dans `%LOCALAPPDATA%\OledGuard\exposure`. L'identité est hachée en SHA-256. Les écritures utilisent un fichier temporaire puis un remplacement atomique.

## Limites assumées

Le moteur mesure l'image finale capturée par Windows. Il ne connaît pas directement les sous-pixels physiques, les algorithmes internes du téléviseur ou la luminance HDR absolue. Il vise une réduction pratique de l'exposition différentielle, pas une mesure de vieillissement certifiée par le fabricant.
