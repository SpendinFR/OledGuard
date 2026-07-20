# OledGuard 2.1 — moteur hybride réglable

Cette version fusionne les deux comportements confirmés par les exécutables de référence :

- **souris de la 0.1.0** : chemin circulaire composé de petits pixels, dont chaque position expire séparément et produit un dégradé temporel ;
- **contenu de la 0.5.0** : les changements proches sont regroupés puis réveillent de gros blocs rectangulaires stables.

Les zones statiques ne deviennent plus obligatoirement noires. Elles atteignent une opacité maximale réglable, **88 % par défaut**, avec le même fondu linéaire cellule par cellule que le moteur original.

## Réglages

La fenêtre de paramètres expose notamment :

- taille des cellules de détection et des pixels visuels ;
- assombrissement maximal, délai et vitesse de fondu ;
- nombre de niveaux d'opacité ;
- taille minimale, marge, fusion et dégradé des blocs réveillés ;
- rayon et durée du chemin souris original ;
- seuils de détection faibles et forts ;
- fréquences d'analyse et nombre d'échantillons.

## Commandes

- `Ctrl + Alt + O` : activer ou désactiver ;
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes ;
- clic droit sur l'icône : paramètres et fermeture.

## Mémoire

À 4K avec les valeurs par défaut :

- détection : environ 120 × 68 cellules ;
- rendu : environ 240 × 135 cellules ;
- aucune capture 4K conservée ;
- quelques petits tableaux et un tampon d'analyse réutilisé.

La surface de composition WPF dépend de Windows et du pilote, mais le moteur ne crée aucune texture d'historique ou vidéo.
