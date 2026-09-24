# IA Foot : gameplay

## Mise en place
- **Option A (décor du pack)** : dupliquer `Lightning Poly/.../Demo Scene`, l'ouvrir, puis menu **IA Foot > Configurer le match dans la scène ouverte**.
- **Option B (scène minimale)** : menu **IA Foot > Créer une nouvelle scène de match** (terrain, buts, murs invisibles, caméra). Elle est sauvegardée dans `Assets/IAFoot/Scenes/`.

Ensuite, vérifier la taille des zones de but (boîtes colorées dans la vue Scene, objet `GoalTrigger`) et lancer Play.

## Contrôles
| Action | Joueur 1 | Joueur 2 | Manette |
|---|---|---|---|
| Déplacement | ZQSD (WASD) | Flèches | Stick gauche |
| Tir (maintenir = plus fort) | Espace | Ctrl droit / Pavé 0 | A / Gâchette droite |
| Tacle | Maj gauche | Maj droit / Pavé 1 | B |
| Saut | E | Pavé 2 / Entrée du pavé | Y |

## Architecture
```
Arena (MatchManager)          score, manches, règles, événements ; pas de singleton, donc duplicable
├── Ball (SoccerBall)         traînée de l'air, effet Magnus, roulement
├── Goal_Blue / Goal_Red
│   └── GoalTrigger (GoalZone)  but validé quand le ballon est entièrement dedans, puis animation
├── Blue_Player_0 (PlayerController + PlayerVisuals + HumanPlayerInput)
├── Red_Player_0  (PlayerController + PlayerVisuals + SimpleBotInput)
├── SpawnPoints / BallSpawn
└── MatchUI (ScoreboardUI)
```

- **PlayerController** ne lit aucune entrée. Un "cerveau" le pilote via :
  `SetMoveInput(Vector3)`, `StartCharge()` / `ReleaseCharge()`, `Shoot(power01, curve)`, `Tackle()`.
- **Cerveaux** : `HumanPlayerInput`, `SimpleBotInput` et, plus tard, votre Agent ML-Agents. Un seul cerveau par joueur.
- **2v2** : ajouter des personnages dans `MatchManager > teams > players` ; les points `*_1` sont déjà créés.
- **Règles** : assets `MatchRule` dans `Assets/IAFoot/Settings` (score limite, temps limite + but en or,
  ballon sorti). Pour une nouvelle règle : hériter de `MatchRule`, créer l'asset, l'ajouter à `rules`.

## Pour ML-Agents (à venir)
- Installer `com.unity.ml-agents`, puis créer un `Agent` sur le joueur qui appelle l'API du `PlayerController`
  dans `OnActionReceived` (et retirer `HumanPlayerInput` / `SimpleBotInput`).
- Récompenses : `MatchManager.GoalScored`, `PlayerController.Kicked` / `TackleHit`, `SoccerBall.LastTouchedBy`.
- Épisodes : `MatchManager.RoundStarted` / `MatchEnded`, ou appeler `RestartRound()`.
- Observations symétriques : `MatchManager.ToTeamFrame(...)` / `DirectionToTeamFrame(...)`. Dans ce repère,
  +X pointe toujours vers le but adverse, donc un même modèle peut jouer des deux côtés (self-play).
- Pour entraîner : `goalCelebrationDuration = 0`, `kickoffFreezeDuration = 0`, désactiver `MatchUI`,
  puis dupliquer l'objet `Arena` en l'éloignant des autres.
