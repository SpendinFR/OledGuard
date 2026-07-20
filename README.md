# OledGuard 3.2 — exposition cumulative

OledGuard assombrit progressivement les zones lumineuses qui restent réellement statiques sur un écran OLED.

## Changement principal

La version 3.2 ne considère plus qu'un mouvement remet le risque à zéro.

- chaque sous-zone conserve une dette d'exposition lumineuse ;
- la dette augmente selon la durée, la stabilité et la luminance ;
- une interruption courte révèle immédiatement l'image mais ne supprime presque pas la dette ;
- avec le réglage par défaut, une seconde de mouvement retire seulement 0,2 seconde d'exposition équivalente ;
- la dette est sauvegardée dans `%LOCALAPPDATA%\OledGuard\exposure` et survit aux redémarrages ;
- le masque tient compte de la lumière restante après assombrissement afin de ne pas compter la zone comme si elle était encore à pleine luminosité.

## Valeurs par défaut

- stabilité avant accumulation : 30 secondes ;
- début de l'assombrissement : 8 minutes équivalentes à blanc maximal ;
- protection maximale : 25 minutes équivalentes à blanc maximal ;
- attente après mouvement avant retour du masque : 12 secondes ;
- assombrissement maximal : 35 % ;
- fondu vers sombre : 12 secondes ;
- réapparition : 1,2 seconde ;
- sauvegarde de la dette : toutes les 5 minutes.

Les couleurs sombres cumulent très peu de dette. Les blancs et interfaces claires cumulent nettement plus vite grâce à une pondération non linéaire de la luminance.

## Détection

- références courte, moyenne et longue ;
- âge indépendant pour chaque sous-zone ;
- nettoyage bidirectionnel des petits îlots et trous ;
- opacité uniforme dans chaque région connectée pour éviter les bandes internes ;
- aucune capture vidéo 4K conservée.

## Commandes existantes

- `Ctrl + Alt + O` : activer ou désactiver ;
- `Ctrl + Alt + R` : révéler tout pendant 10 secondes ;
- clic droit sur l'icône : paramètres et fermeture.
