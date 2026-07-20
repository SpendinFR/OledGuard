# OledGuard 0.7.0 — masque carré net

## Objectif visuel

Conserver le comportement utile de la version 0.4, mais remplacer le grand halo arrondi et flou par de petites zones carrées lisibles : centre transparent, quelques niveaux sombres nets, puis noir profond.

## Pipeline

1. Capture réduite du bureau avec GDI.
2. Comparaison de 4 × 4 échantillons par cellule de 24 px.
3. Rejet des changements qui affectent trop peu d’échantillons.
4. Regroupement des cellules modifiées en composantes voisines.
5. Suppression des composantes plus petites que le seuil configuré, par défaut deux cellules.
6. Chaque activité valide reste visible 30 secondes.
7. Champ de distance de type Chebyshev : les contours grandissent en carrés, pas en cercles.
8. Conversion de la distance en quatre niveaux d’opacité spatiale.
9. Agrandissement du masque avec nearest-neighbour, sans flou spatial.
10. Fondu temporel rapide vers visible et progressif vers noir.

## Filtre des petits clignotements

Un pixel ou indicateur isolé ne doit pas maintenir une grande zone visible. Deux protections sont combinées :

- un nombre minimal d’échantillons modifiés dans une cellule ;
- un nombre minimal de cellules voisines dans une composante d’activité.

La souris reste prioritaire : elle révèle les petits contrôles lorsque l’utilisateur souhaite réellement interagir avec eux.

## Coût

Pour un écran 4K et une cellule de 24 px, le moteur travaille sur environ 14 400 cellules et une carte alpha de même taille. Les tableaux sont réutilisés et représentent seulement quelques centaines de kilo-octets.
