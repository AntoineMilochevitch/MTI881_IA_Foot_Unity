# MTI881 – IA Foot (Unity)

Petit jeu de football en 3D, conçu pour entraîner des agents par apprentissage par renforcement avec **Unity ML-Agents**.

- **Unity** 6000.3.10f1 (URP, Input System)
- **Modèles 3D** : Lightning Poly – *Football Essentials 3D*

## Lancer le jeu
1. Ouvrir le projet avec Unity 6000.3.10f1.
2. Ouvrir la scène `Assets/Lightning Poly/Football Essentials 3D/Demo Scene/Demo Scene.unity`.
3. Play : vous jouez les **Bleus** contre un bot (les **Rouges**).

Pour monter une nouvelle scène : menu **IA Foot > Créer une nouvelle scène de match**,
ou **IA Foot > Configurer le match dans la scène ouverte** dans une scène qui contient déjà les prefabs du pack.

## Contrôles
| Action | Clavier | Manette |
|---|---|---|
| Déplacement | ZQSD (WASD) | Stick gauche |
| Tir (maintenir = plus fort) | Espace | A / Gâchette droite |
| Tacle | Maj gauche | B |
| Saut | E | Y |

## Fonctionnalités
- **Joueurs** : déplacement dans toutes les directions avec rotation interpolée, tir chargé
  (intensité de 0 à 1 selon le temps d'appui), tacle qui étourdit l'adversaire, saut, animations procédurales.
- **Ballon** : traînée de l'air, effet Magnus, résistance au roulement, anti-blocage contre les murs.
- **Buts** : validés quand le ballon a entièrement franchi la ligne, puis animation de la cage.
- **Match** : score et chrono affichés à l'écran, coup d'envoi avec le ballon au centre et les joueurs de chaque côté.
  Règles modulaires : score limite, temps limite avec but en or, ballon sorti.
- **Pensé pour l'IA** : chaque joueur est piloté par un « cerveau » interchangeable (humain, bot scripté ou agent ML).
  L'arène est duplicable pour l'entraînement parallèle, gère le 2v2 et fournit un repère d'équipe symétrique pour le self-play.

Documentation du code et intégration ML-Agents : [`Assets/IAFoot/README.md`](Assets/IAFoot/README.md).
