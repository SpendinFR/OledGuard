# OledGuard — moteur simple de stabilité

OledGuard assombrit les grandes zones lumineuses qui restent réellement statiques. Il ne cherche plus à détecter une fenêtre active et n'ajoute aucun halo, chemin de souris ou effet artificiel.

## Principe

- L'écran est découpé en grandes zones de 64 × 64 pixels par défaut.
- Chaque zone est comparée à des références courte, moyenne et longue.
- Une zone doit rester stable sur les trois temporalités et dépasser le délai configuré avant de pouvoir être assombrie.
- Les zones voisines sont nettoyées avec une règle de majorité bidirectionnelle.
- Un petit îlot sombre est supprimé ; un petit trou clair entouré de zones sombres est comblé.
- Les composantes trop petites sont ignorées.
- La luminosité est évaluée sur toute la région : une région déjà très sombre n'est pas masquée inutilement.
- Une activité réelle fait réapparaître la zone rapidement.

## Réglages conseillés

- zones : 64 px ;
- références : 2 s, 15 s et 60 s ;
- délai statique : 120 s ;
- fondu : 20 s ;
- assombrissement maximal : 85 % ;
- filtre : majorité 6/9, deux passes ;
- région minimale : quatre cellules ;
- trou clair maximal : trois cellules.

## Commandes

- `Ctrl + Alt + O` : activer ou désactiver ;
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes ;
- clic droit sur l'icône : paramètres et fermeture.

## Mémoire

Pour un écran 4K avec des cellules de 64 px et quatre échantillons par côté, la capture d'analyse mesure environ 240 × 136 pixels. Le moteur conserve quatre petits tampons réutilisés, sans historique vidéo 4K. L'overlay Windows reste la principale allocation graphique.


## Correction 3.1

- Chaque sous-échantillon conserve son propre âge de stabilité. Un changement au centre d’une fenêtre ne remet donc plus à zéro les bandes identiques autour.
- Une région statique connectée partage une seule opacité pendant le fondu, ce qui supprime les bandes internes.
- Le réglage 128 px avec 8 sous-zones donne environ 16 px de précision réelle sans capture 4K.
