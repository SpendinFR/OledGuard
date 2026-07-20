# Conception du MVP OledGuard

## Choix de 30 secondes

Le marquage OLED est un phénomène d’exposition cumulée sur des durées longues. Passer de 30 secondes à 5 secondes ne change pas radicalement la protection sur plusieurs heures, mais multiplie les assombrissements pendant la lecture. Le délai par défaut est donc 30 secondes ; 5 secondes sert surtout au test et aux usages très agressifs.

## Machine d’état d’une zone

Chaque zone possède :

- l’instant du dernier changement détecté ;
- une échéance de révélation temporaire ;
- une opacité actuelle ;
- une opacité cible ;
- une luminance approximative.

Transitions :

1. **Changement détecté** : opacité mise immédiatement à zéro, compteur statique remis à zéro, voisinage révélé.
2. **Stabilité inférieure au délai** : zone visible.
3. **Stabilité supérieure au délai** : cible noire, fondu en 900 ms.
4. **Souris proche** : cible visible, fondu en 140 ms.
5. **Souris partie** : maintien de 850 ms, puis retour au noir si la zone reste statique.
6. **Révéler tout** : toutes les cibles deviennent visibles pendant 10 secondes.

## Cas Codex

Une zone qui change toutes les quelques secondes ne devient pas noire : chaque changement repart pour 30 secondes. Si Codex s’arrête plus de 30 secondes, la zone devient noire. Au prochain changement, elle redevient visible au plus tard lors de la prochaine capture, soit environ 0,5 seconde lorsqu’un masque est présent.

## Empreinte mémoire

L’analyse ne conserve pas de captures 4K. Pour un écran 4K avec cellules de 64 px et 4 échantillons par cellule :

- grille : environ 60 × 34 zones ;
- image d’analyse : environ 240 × 136 pixels ;
- tampon courant : environ 128 Kio ;
- tampon précédent : environ 128 Kio ;
- états et masques : quelques dizaines de Kio.

La principale allocation graphique est la fenêtre transparente WPF composée par Windows. Sa taille réelle en VRAM dépend de WPF, DWM, du pilote et de la résolution.

## Améliorations prévues

1. Remplacer la capture GDI par Desktop Duplication afin d’exploiter les rectangles modifiés fournis par DXGI.
2. Utiliser DirectComposition/Direct2D pour contrôler plus précisément l’empreinte GPU.
3. Ajouter un compteur CPU/GPU interne et un mode diagnostic.
4. Ajouter une interpolation spatiale pour des contours plus doux sans blur plein écran.
5. Ajouter des profils d’exclusion pour les jeux, vidéos et applications de création graphique.
6. Tester le HDR, les écrans à fréquence élevée et les topologies multi-écrans mixtes.
7. Signer l’exécutable et créer un installateur.
