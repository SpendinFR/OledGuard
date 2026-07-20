# OledGuard 0.4 — architecture du masque

## Objectif visuel

Le masque doit être noir loin de toute activité, entièrement transparent sur la zone utile, puis varier continûment entre les deux. Les cases de détection ne doivent jamais être visibles directement.

## Pipeline

1. Capture réduite du bureau avec GDI.
2. Comparaison par cellules pour détecter les changements.
3. Chaque changement prolonge l’activité locale de 30 secondes.
4. La souris prolonge également l’activité autour de sa position.
5. Fermeture morphologique légère pour combler les petits trous.
6. Champ de distance de type chamfer jusqu’à la cellule active la plus proche.
7. Conversion de la distance en opacité avec une courbe smoothstep :
   - centre transparent ;
   - large transition ;
   - extérieur noir profond.
8. Interpolation bilinéaire dans une carte alpha 2× plus fine.
9. Fondu temporel rapide vers visible et lent vers noir.

## Coût

Le champ de distance utilise deux passages linéaires. Pour un écran 4K et une cellule de 32 px, il travaille sur environ 8 160 cellules. La carte rendue contient environ 32 640 valeurs alpha. Aucun frame 4K n’est conservé.
