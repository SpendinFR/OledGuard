# Conception OledGuard 1.2

## Décision

La version 1.2 supprime le moteur de fenêtre active et revient à la carte alpha fine de la version 0.3, validée visuellement comme la plus naturelle.

## Carte de protection

- grille logique de 16 px ;
- capture de 3 × 3 échantillons par cellule ;
- fondu temporel vers le noir ;
- interpolation linéaire de la petite carte alpha ;
- léger feathering 3 × 3 uniquement au rendu ;
- chemin souris circulaire composé de cellules superposées.

## Filtrage des taches

Un changement doit toucher au moins deux échantillons dans une cellule. Un caret, un point clignotant ou une variation d’un seul échantillon ne réveille donc plus la zone. Les changements faibles doivent toujours être confirmés sur deux captures.

## Mémoire

Aucun tampon 4K n’est conservé. Sur un écran 4K, la capture d’analyse reste proche de 720 × 405 BGRA et la carte alpha proche de 240 × 135.
