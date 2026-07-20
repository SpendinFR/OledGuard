# Architecture hybride OledGuard 2.1

## Deux résolutions séparées

La détection utilise une grille de 32 px afin de regrouper les changements en blocs cohérents. Le rendu utilise une grille de 16 px afin de retrouver le chemin souris pixelisé et progressif de la version originale.

## Contenu

Les cellules modifiées sont regroupées par composantes. Les composantes trop petites sont ignorées. Une composante acceptée est agrandie jusqu'à une taille minimale, reçoit une marge, puis peut fusionner avec un bloc actif proche.

## Souris

Le calcul est celui du prototype 0.1 : à chaque position du pointeur, toutes les petites cellules situées dans un rayon circulaire reçoivent leur propre date d'expiration. En déplacement, les anciennes positions expirent avant les nouvelles et forment un chemin dynamique.

## Fondu

Chaque cellule possède une opacité actuelle et une cible. La transition utilise l'incrément linéaire du moteur original. La cible statique est configurable et vaut 0,88 par défaut au lieu de 1,0.
