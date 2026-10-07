# Back-end Python d'IA Foot

Remplace ML-Agents par une chaîne maison : Unity enregistre les parties et les envoie par batchs à Python,
Python renvoie des modèles qu'Unity installe à chaud sur les agents.

Pour l'instant `train.py` **affiche** les données reçues et envoie un modèle à poids aléatoires :
l'entraînement PyTorch reste à écrire dans `DebugTrainer.on_batch`.

## Lancer
```
cd python
pip install numpy            # torch est optionnel pour l'instant
python train.py              # puis Play dans Unity
```
Côté Unity, une seule fois : menu **IA Foot > Entraînement > Préparer la scène**.
L'ordre de lancement n'a pas d'importance, Unity se reconnecte tout seul.

| Option | Effet |
|---|---|
| `--model mlp\|linear\|tree\|none` | modèle de démonstration envoyé à la connexion (`none` : les agents restent en exploration aléatoire) |
| `--resend-every N` | renvoie un nouveau modèle tous les N batchs (teste le remplacement à chaud) |
| `--deterministic` | désactive l'exploration côté Unity |
| `--time-scale 10` | accélère la simulation |
| `--batch-size 2048` | change le seuil d'envoi des batchs |
| `--show K`, `--verbose` | détail de K transitions par batch, noms des observations |
| `--quiet` | masque les lignes de debug réseau |

Chaque message reçu (`<-`) ou envoyé (`->`) donne une ligne horodatée avec son contenu et sa taille.

## Brancher son propre réseau et sa propre méthode d'entraînement
Tout se passe dans la classe `DebugTrainer` de `train.py`. Le dossier `iafoot/` (réseau, protocole, export)
n'a pas à être modifié. Trois endroits à changer :

| Où dans `train.py` | Aujourd'hui | À mettre à la place |
|---|---|---|
| En haut du fichier | imports numpy | ajouter `import torch` et `from torch import nn` |
| `DebugTrainer.__init__` | compteurs de debug, `self.version = 0` | vos hyperparamètres, ex : `self.gamma = 0.99` (gardez `self.version`) |
| `DebugTrainer.on_connect` | appelle `send_models`, qui envoie un modèle aléatoire fabriqué par `build_demo_model` | créer **vos** réseaux et votre optimiseur, puis envoyer le réseau de politique une première fois |
| `DebugTrainer.on_batch`, au commentaire `TODO entraînement` | affiche le batch | **votre** mise à jour, puis renvoyer les nouveaux poids |

`build_demo_model` et `send_models` ne servent qu'à la démonstration : supprimez-les une fois votre réseau en place.
Lancez avec `--model none` tant qu'ils existent, pour qu'ils n'écrasent pas votre modèle.

### 1. Créer le réseau dans `on_connect`
Les tailles ne sont connues qu'à la connexion d'Unity, dans `connection.behaviors`. Ne les écrivez pas en dur :
elles changent dès que quelqu'un modifie les observations côté Unity.

```python
def on_connect(self, connection):
    behavior = connection.behaviors[0]          # un seul comportement ("Foot") par défaut
    self.behavior = behavior["name"]
    obs_size, action_size = behavior["obs_size"], behavior["action_size"]
    # Quelles sorties sont des boutons (tir, tacle, saut) et lesquelles sont continues (déplacement, puissance).
    self.is_button = torch.tensor([a["type"] == "binary" for a in behavior["actions"]])

    self.actor = nn.Sequential(nn.Linear(obs_size, 64), nn.Tanh(), nn.Linear(64, 64), nn.Tanh(),
                               nn.Linear(64, action_size))
    self.critic = nn.Sequential(nn.Linear(obs_size, 64), nn.Tanh(), nn.Linear(64, 1))
    self.log_std = nn.Parameter(torch.full((action_size,), -0.5))
    self.optimizer = torch.optim.Adam([*self.actor.parameters(), *self.critic.parameters(), self.log_std], lr=3e-4)
    self.send_actor(connection)

def send_actor(self, connection):
    self.version += 1                           # une nouvelle version à chaque envoi
    connection.send_model(models.from_torch(self.actor), version=self.version, behavior=self.behavior,
                          decoder=models.direct_decoder(log_std=self.log_std.tolist()))
```

- **Seul le réseau qui choisit les actions part vers Unity.** Critique, réseau cible, buffer de rejeu, optimiseur :
  tout le reste vit uniquement en Python et peut avoir n'importe quelle forme.
- **Le réseau envoyé doit être un `nn.Sequential`** fait des couches listées dans la section *Modèles*. Pour une
  autre architecture, voir *Ajouter un type de modèle*.
- **Sa sortie fait `action_size` valeurs** avec le décodeur `direct` : la moyenne pour chaque action continue,
  le logit pour chaque bouton. N'ajoutez pas de `Tanh` ou de `Sigmoid` final, Unity borne lui-même les actions.

### 2. Entraîner dans `on_batch`
`batch` contient des tableaux numpy (voir *Contenu d'un batch*). Exemple minimal de gradient de politique :

```python
def log_prob(self, obs, actions):
    """Log-probabilité des actions sous le réseau actuel, calculée exactement comme dans Unity."""
    output = self.actor(obs)
    moves, buttons = ~self.is_button, self.is_button
    gaussian = torch.distributions.Normal(output[:, moves], self.log_std[moves].exp())
    bernoulli = torch.distributions.Bernoulli(logits=output[:, buttons])
    return gaussian.log_prob(actions[:, moves]).sum(1) + bernoulli.log_prob(actions[:, buttons]).sum(1)

def on_batch(self, connection, batch):
    # Méthode on-policy : ne garder que les transitions jouées avec le modèle actuel.
    fresh = batch.model_version == self.version
    if not fresh.any():
        return                                   # sinon la perte vaut nan et le réseau est détruit

    with torch.no_grad():
        values = self.critic(torch.from_numpy(batch.obs)).squeeze(1).numpy()
        next_values = self.critic(torch.from_numpy(batch.next_obs)).squeeze(1).numpy()

    # Retours actualisés, trajectoire par trajectoire (le batch mélange les agents).
    returns = np.zeros(len(batch), dtype=np.float32)
    for indices in batch.trajectories().values():          # indices d'un (agent, épisode), triés par pas
        last = indices[-1]
        running = 0.0 if batch.terminated[last] else next_values[last]
        for i in indices[::-1]:
            running = batch.rewards[i] + self.gamma * running
            returns[i] = running

    obs, actions = torch.from_numpy(batch.obs[fresh]), torch.from_numpy(batch.actions[fresh])
    advantages = torch.from_numpy((returns - values)[fresh])
    loss = -(self.log_prob(obs, actions) * advantages).mean()   # + la perte du critique, l'entropie, etc.
    self.optimizer.zero_grad()
    loss.backward()
    self.optimizer.step()

    self.send_actor(connection)                  # Unity installe les nouveaux poids sur les agents
```

### Les pièges
- **Le batch n'est pas une trajectoire.** Les transitions de tous les agents sont mélangées dans l'ordre du temps.
  Tout calcul qui suit le temps (retours, GAE) doit passer par `batch.trajectories()`.
- **`terminated` et `truncated` ne se traitent pas pareil.** Sur un but (`terminated`), la valeur future est 0.
  Sur une coupure (`truncated`), ou en fin de batch quand l'épisode continue, il faut l'estimer avec
  `critic(next_obs)`. Les confondre apprend à l'agent que la limite de temps est une défaite.
- **Un batch peut mélanger plusieurs versions du modèle.** Unity continue à jouer avec l'ancien modèle tant que le
  nouveau n'est pas arrivé. Filtrez avec `batch.model_version` (on-policy) ou utilisez `batch.log_probs`,
  la probabilité au moment du tirage (ratio de PPO).
- **Les actions continues enregistrées ne sont pas bornées.** `batch.actions` contient la valeur tirée
  (ex : 1.84) alors qu'Unity l'a ramenée à [-1, 1] pour jouer. C'est voulu : c'est cette valeur brute qu'il faut
  pour recalculer une log-probabilité.
- **`on_batch` bloque la réception** tant qu'il n'a pas fini. Le jeu continue de tourner et les batchs suivants
  attendent dans la file réseau : rien n'est perdu, mais ils auront été joués avec un modèle de plus en plus vieux.
- **Vérifiez la réponse d'Unity** dans `on_model_ack` : `header["ok"]` est faux si le modèle est refusé (sortie
  différente de PyTorch, mauvaise taille, valeurs `nan`), et l'ancien modèle reste alors en place.
- **Une erreur dans votre code** est affichée en entier dans la console sans couper la connexion.

### Autres approches
- **Actions discrètes (DQN, etc.)** : le réseau sort un score par ligne d'une table d'actions. Envoyez-le avec
  `decoder=models.discrete_decoder(models.foot_action_table(), mode="epsilon_greedy", epsilon=0.1)` ;
  l'action choisie est dans `batch.action_index`. La table est libre : une ligne = un vecteur d'actions complet.
- **Méthode off-policy** : ajoutez chaque batch à votre propre buffer de rejeu dans `on_batch`, puis tirez-y vos
  mini-batchs. Chaque transition est complète (`obs`, `actions`, `rewards`, `next_obs`, `terminated`).
- **Évaluer sans exploration** : `models.direct_decoder(deterministic=True)`.
- **Deux équipes entraînées séparément** : donnez un `Behavior Name` différent aux agents de chaque équipe dans
  Unity. `connection.behaviors` contient alors deux entrées et `batch.behavior` dit d'où vient chaque batch.

### Ce qui se règle côté Unity, pas ici
| Pour changer... | Aller dans... |
|---|---|
| les récompenses | composant `FootAgent` de chaque joueur (section Récompenses), ou `FootAgent.cs` pour en ajouter |
| ce que l'agent observe | `FootObservation` dans `Assets/IAFoot/Scripts/Learning/FootSpaces.cs` |
| les actions possibles | `FootActions` dans le même fichier |
| la taille des batchs, la vitesse du jeu | `TrainingBridge`, ou `connection.set_config(batch_size=..., time_scale=...)` |
| la durée maximale d'un épisode, la fréquence de décision | composant `FootAgent` |

## Déplacer le back-end
Ce dossier ne dépend pas du projet Unity : les deux ne se parlent que par le réseau. Il peut vivre dans un
autre dossier, un autre dépôt ou une autre machine.

- **Autre dossier / autre dépôt** : copier `train.py` et le dossier `iafoot/` ensemble. Rien à changer côté Unity.
- **Depuis un autre script** : `from iafoot import TrainingServer, models` (le dossier `iafoot/` doit être à côté
  du script, ou son parent dans `PYTHONPATH`).
- **Autre machine** : lancer `python train.py --host 0.0.0.0`, mettre l'adresse IP de cette machine dans le champ
  `Host` du `TrainingBridge` dans Unity, et autoriser le port 5005 dans le pare-feu. La connexion n'est ni
  chiffrée ni authentifiée : à réserver à un réseau de confiance.
- **Autre port** : `--port` côté Python et champ `Port` du `TrainingBridge`.

## Fichiers
| Fichier | Rôle |
|---|---|
| `train.py` | point d'entrée : affichage de debug, envoi du modèle de démonstration |
| `iafoot/server.py` | serveur TCP, classes `TrainingServer`, `UnityConnection`, `Batch` |
| `iafoot/models.py` | description et export des modèles (`from_torch`, `from_sklearn_tree`, ...) |
| `iafoot/protocol.py` | encodage / décodage des messages |

## Protocole
Unity est le client, Python le serveur (port 5005 par défaut). Même format dans les deux sens :

```
"IAFT" | uint32 taille en-tête | uint32 taille données | en-tête JSON UTF-8 | données binaires
```
L'en-tête contient `"type"` et `"tensors"`, qui décrit chaque tableau du bloc binaire
(`dtype` f32 ou i32, `shape`, `offset`, `nbytes`), en little-endian.

| Message | Sens | Contenu |
|---|---|---|
| `hello` | Unity -> Python | comportements : taille et noms des observations, actions, agents, période de décision |
| `batch` | Unity -> Python | transitions (voir ci-dessous) et résumés des épisodes terminés |
| `set_model` | Python -> Unity | `behavior`, `version`, `model`, `decoder`, `check` optionnel |
| `model_ack` | Unity -> Python | `ok`, plus `description` ou `error` |
| `set_config` | Python -> Unity | `time_scale`, `batch_size`, `flush` |

### Contenu d'un batch
Un batch part dès que le nombre de transitions accumulées atteint le seuil (`Batch Size` du `TrainingBridge`).
Les transitions sont dans l'ordre chronologique, tous agents mélangés ; `Batch.trajectories()` les regroupe
par (agent, épisode).

| Tableau | Forme | Sens |
|---|---|---|
| `obs`, `next_obs` | [N, obs_size] | observation au moment de la décision, et à la décision suivante |
| `actions` | [N, action_size] | action appliquée |
| `rewards` | [N] | récompense cumulée entre les deux décisions |
| `terminated` | [N] | l'épisode s'est fini sur un but : pas de valeur future |
| `truncated` | [N] | l'épisode a été coupé (limite de pas, remise en jeu) : bootstrapper avec `next_obs` |
| `log_probs` | [N] | log-probabilité de l'action quand elle a été tirée |
| `action_index` | [N] | indice dans la table d'actions (décodeur `discrete`), sinon -1 |
| `agent_id`, `episode_id`, `step` | [N] | pour reconstituer les trajectoires |
| `model_version` | [N] | version du modèle qui a choisi l'action (0 = exploration aléatoire) |

Un épisode = une manche (du coup d'envoi au but). Un batch peut donc contenir des épisodes entiers et des
morceaux d'épisodes en cours.

## Modèles
Un modèle est une description JSON qui référence des tableaux de poids par leur nom. Unity le reconstruit
sans dépendre de PyTorch, et le format n'est pas limité aux réseaux de neurones.

| Type | Description | Export |
|---|---|---|
| `sequential` / `mlp` | couches `dense`, `activation`, `layer_norm` | `models.from_torch(reseau)`, `models.mlp(...)` |
| `linear` | y = W·x + b | `models.linear(W, b)` |
| `decision_tree` | arbre binaire, format scikit-learn | `models.from_sklearn_tree(arbre)`, `models.decision_tree(...)` |
| `ensemble` | moyenne ou somme pondérée de modèles quelconques | `models.from_sklearn_forest(foret)`, `models.ensemble(...)` |
| `constant` | sortie fixe | `models.constant(valeurs)` |

Activations : relu, tanh, sigmoid, leaky_relu, elu, gelu, silu, softplus, softmax.
Tout modèle accepte une normalisation d'entrée : `modele.with_input_norm(mean, std)`.

Le **décodeur** dit comment la sortie du modèle devient une action :
- `models.direct_decoder(log_std, deterministic)` : une sortie par dimension d'action. Continue : moyenne
  d'une gaussienne. Bouton : logit de la probabilité d'appuyer. Convient à PPO, SAC, etc.
- `models.discrete_decoder(table, mode)` : un score par ligne d'une table d'actions, choisi par `argmax`,
  `epsilon_greedy` ou `softmax`. Convient au Q-learning, aux classifieurs et aux arbres.

```python
reseau = torch.nn.Sequential(torch.nn.Linear(45, 64), torch.nn.Tanh(), torch.nn.Linear(64, 7))
connection.send_model(models.from_torch(reseau), version=3, behavior="Foot",
                      decoder=models.direct_decoder(log_std=-0.7))
```

`from_torch` joint un exemple (entrée, sortie calculée par PyTorch). Unity recalcule la sortie avec sa propre
implémentation et **refuse le modèle si elle diffère** : une erreur d'export se voit tout de suite au lieu de
donner un agent qui joue mal sans raison apparente.

### Ajouter un type de modèle
1. C# : une classe qui implémente `IPolicyModel`, puis `PolicyModelFactory.Register("mon_type", Build)`.
   Pour une couche de réseau : `ILayer` et `LayerFactory.Register`. Pour une activation : `Activations.Register`.
2. Python : une fonction qui renvoie un `ModelExport(spec, tensors)` avec `spec["type"] == "mon_type"`.

Les modèles récurrents (GRU, LSTM) ne sont pas gérés : `IPolicyModel` n'a pas d'état par agent.
