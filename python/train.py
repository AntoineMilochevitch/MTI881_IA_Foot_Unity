"""Back-end d'entraînement d'IA Foot - pour l'instant en mode debug.

Ce script :
  1. attend la connexion d'Unity et affiche la description des observations / actions ;
  2. affiche le contenu de chaque batch de parties reçu (aucun entraînement n'est fait pour le moment) ;
  3. envoie un modèle de démonstration (poids aléatoires) à Unity, qui l'installe sur les agents.

L'entraînement PyTorch viendra se brancher dans `DebugTrainer.on_batch`.

Exemples :
    python train.py                         # MLP aléatoire envoyé à la connexion
    python train.py --model tree            # arbre de décision + actions discrètes
    python train.py --model none            # n'envoie rien : les agents restent en exploration aléatoire
    python train.py --resend-every 5        # nouveau modèle tous les 5 batchs (test du remplacement à chaud)
    python train.py --time-scale 10         # accélère la simulation Unity
"""

from __future__ import annotations

import argparse
import sys
from collections import Counter
from typing import Any

import numpy as np

from iafoot import Batch, DecoderExport, ModelExport, TrainingServer, UnityConnection, models, protocol


def build_demo_model(kind: str, obs_size: int, actions: list[dict[str, Any]], rng: np.random.Generator,
                     deterministic: bool) -> tuple[ModelExport, DecoderExport] | None:
    """Fabrique un modèle à poids aléatoires du type demandé, pour tester la chaîne Python -> Unity."""
    if kind == "none":
        return None

    action_size = len(actions)
    # Boutons (tir, tacle, saut) : logit initial de -2, soit ~12 % de chances d'appuyer à chaque décision.
    output_bias = np.array([-2.0 if a["type"] == "binary" else 0.0 for a in actions], dtype=np.float32)

    if kind == "mlp":
        hidden = 64
        try:
            import torch
            from torch import nn
        except ImportError:
            # Sans PyTorch : même architecture, décrite directement en numpy.
            sizes = [obs_size, hidden, hidden, action_size]
            weights = [rng.normal(0, 1 / np.sqrt(n_in), size=(n_out, n_in)) for n_in, n_out in zip(sizes, sizes[1:])]
            weights[-1] *= 0.01  # sorties proches de 0 : comportement d'abord dominé par l'exploration
            biases = [np.zeros(n_out) for n_out in sizes[1:-1]] + [output_bias]
            model = models.mlp(weights, biases, ["tanh", "tanh", None])
            model = model.with_check(rng.normal(size=obs_size))
        else:
            network = nn.Sequential(
                nn.Linear(obs_size, hidden), nn.Tanh(),
                nn.Linear(hidden, hidden), nn.Tanh(),
                nn.Linear(hidden, action_size),
            )
            with torch.no_grad():
                network[-1].weight.mul_(0.01)
                network[-1].bias.copy_(torch.from_numpy(output_bias))
            model = models.from_torch(network)  # joint un exemple calculé par PyTorch, vérifié par Unity
        return model, models.direct_decoder(log_std=-0.7, deterministic=deterministic)

    if kind == "linear":
        model = models.linear(rng.normal(0, 0.05, size=(action_size, obs_size)), output_bias)
        return model.with_check(rng.normal(size=obs_size)), models.direct_decoder(deterministic=deterministic)

    if kind == "tree":
        # Arbre binaire complet de profondeur 3 : 7 nœuds internes puis 8 feuilles, un score par action discrète.
        table = models.foot_action_table()
        internal, leaves = 7, 8
        nodes = internal + leaves
        feature = np.concatenate([rng.integers(0, obs_size, size=internal), np.zeros(leaves, dtype=int)])
        threshold = np.concatenate([rng.normal(0, 0.3, size=internal), np.zeros(leaves)])
        left = np.array([2 * i + 1 if i < internal else -1 for i in range(nodes)])
        right = np.array([2 * i + 2 if i < internal else -1 for i in range(nodes)])
        value = rng.normal(size=(nodes, len(table)))
        model = models.decision_tree(feature, threshold, left, right, value).with_check(rng.normal(size=obs_size))
        return model, models.discrete_decoder(table, mode="argmax" if deterministic else "softmax")

    raise ValueError(f"Type de modèle de démonstration inconnu : {kind}")


class DebugTrainer:
    """Affiche ce qu'Unity envoie. C'est ici que l'entraînement PyTorch prendra place."""

    def __init__(self, args: argparse.Namespace):
        self.args = args
        self.rng = np.random.default_rng(args.seed)
        self.version = 0
        self.batches_received = 0
        self.transitions_received = 0

    # ------------------------------------------------------------------ Connexion

    def on_connect(self, connection: UnityConnection) -> None:
        hello = connection.hello or {}
        print(f"\n=== Unity connecté : {connection} ===")
        print(f"  Unity {hello.get('unity_version')} | pas de physique = {hello.get('fixed_delta_time')} s | "
              f"time_scale = {hello.get('time_scale')} | seuil de batch = {hello.get('batch_size')} transitions")

        for behavior in connection.behaviors:
            actions = ", ".join(f"{a['name']} ({a['type']})" for a in behavior["actions"])
            agents = ", ".join(f"#{a['id']} {a['name']} [{a['team']}]" for a in behavior["agents"])
            print(f"  Comportement '{behavior['name']}' :")
            print(f"    observations : {behavior['obs_size']} valeurs")
            if self.args.verbose:
                for start in range(0, len(behavior["obs_names"]), 6):
                    print("       " + ", ".join(behavior["obs_names"][start:start + 6]))
            print(f"    actions      : {behavior['action_size']} valeurs -> {actions}")
            print(f"    décision     : tous les {behavior['decision_period']} pas de physique")
            print(f"    agents       : {agents or 'aucun'}")
            print(f"    modèle actuel: v{behavior['model_version']} (0 = exploration aléatoire)")

        if self.args.time_scale is not None or self.args.batch_size is not None:
            connection.set_config(time_scale=self.args.time_scale, batch_size=self.args.batch_size)
            print(f"  -> set_config envoyé (time_scale={self.args.time_scale}, batch_size={self.args.batch_size})")

        self.send_models(connection)
        sys.stdout.flush()

    def on_disconnect(self, connection: UnityConnection) -> None:
        print(f"=== Unity déconnecté : {connection} | total reçu : {self.batches_received} batch(s), "
              f"{self.transitions_received} transitions | {protocol.format_size(connection.bytes_received)} reçus, "
              f"{protocol.format_size(connection.bytes_sent)} envoyés ===", flush=True)

    # ------------------------------------------------------------------ Python -> Unity

    def send_models(self, connection: UnityConnection) -> None:
        for behavior in connection.behaviors:
            demo = build_demo_model(self.args.model, behavior["obs_size"], behavior["actions"],
                                    self.rng, self.args.deterministic)
            if demo is None:
                continue
            model, decoder = demo
            self.version += 1
            connection.send_model(model, version=self.version, behavior=behavior["name"], decoder=decoder)
            parameters = sum(int(t.size) for t in model.tensors.values())
            print(f"  [python] Modèle de démonstration v{self.version} envoyé à '{behavior['name']}' : type "
                  f"'{model.spec['type']}', {parameters} paramètres (poids aléatoires), décodeur '{decoder.spec['type']}'. "
                  "En attente de la confirmation d'Unity...")

    def on_model_ack(self, connection: UnityConnection, header: dict[str, Any]) -> None:
        if header.get("ok"):
            print(f"  <- Unity a installé le modèle v{header.get('version')} sur '{header.get('behavior')}' : "
                  f"{header.get('description')}", flush=True)
        else:
            print(f"  <- Unity a REFUSÉ le modèle v{header.get('version')} : {header.get('error')}", flush=True)

    # ------------------------------------------------------------------ Unity -> Python

    def on_batch(self, connection: UnityConnection, batch: Batch) -> None:
        self.batches_received += 1
        self.transitions_received += len(batch)
        self.print_batch(batch)

        # TODO entraînement : c'est ici qu'on mettra à jour le réseau PyTorch avec `batch`,
        # puis qu'on renverra les nouveaux poids avec connection.send_model(models.from_torch(reseau), ...).
        print(f"  [python] Batch décodé. Aucun entraînement pour l'instant : les données sont seulement affichées. "
              f"Total depuis le lancement : {self.batches_received} batch(s), {self.transitions_received} transitions.")

        if self.args.resend_every and self.batches_received % self.args.resend_every == 0:
            print(f"  [python] {self.batches_received} batchs reçus : envoi d'un nouveau modèle à Unity (--resend-every).")
            self.send_models(connection)
        else:
            print("  [python] Rien n'est renvoyé à Unity pour ce batch. En attente du prochain...")
        sys.stdout.flush()

    def print_batch(self, batch: Batch) -> None:
        n = len(batch)
        print(f"\n--- Batch n°{batch.batch_id} | comportement '{batch.behavior}' | {n} transitions ---")
        if n == 0:
            return

        print(f"  Taille du batch : {n} transitions x ({batch.obs.shape[1]} observations + {batch.actions.shape[1]} actions) "
              f"= {protocol.format_size(batch.nbytes)} de données")

        print("  Tableaux reçus :")
        for name in ("obs", "actions", "rewards", "next_obs", "terminated", "truncated", "log_probs",
                     "action_index", "agent_id", "episode_id", "step", "model_version"):
            array = getattr(batch, name)
            if array is not None:
                print(f"    {name:<14} forme {str(array.shape):<12} {array.dtype}")

        trajectories = batch.trajectories()
        lengths = [len(indices) for indices in trajectories.values()]
        print(f"  Trajectoires : {len(trajectories)} morceaux d'épisodes, {len(set(batch.agent_id.tolist()))} agent(s), "
              f"longueur min/moy/max = {min(lengths)}/{np.mean(lengths):.1f}/{max(lengths)}")
        print(f"  Fins d'épisode : {int(batch.terminated.sum())} terminées (but), {int(batch.truncated.sum())} tronquées")
        print(f"  Récompenses : somme = {batch.rewards.sum():+.3f}, moyenne = {batch.rewards.mean():+.4f}, "
              f"min = {batch.rewards.min():+.3f}, max = {batch.rewards.max():+.3f}")
        print(f"  Observations : moyenne = {batch.obs.mean():+.3f}, écart-type = {batch.obs.std():.3f}, "
              f"min = {batch.obs.min():+.3f}, max = {batch.obs.max():+.3f}, "
              f"valeurs non finies = {int((~np.isfinite(batch.obs)).sum())}")
        print(f"  Actions (moyenne par dimension) : {np.array2string(batch.actions.mean(axis=0), precision=2, suppress_small=True)}")
        versions = Counter(batch.model_version.tolist())
        print("  Version du modèle : " + ", ".join(f"v{v} x{count}" for v, count in sorted(versions.items())))

        if batch.episodes:
            outcomes = Counter(episode["outcome"] for episode in batch.episodes)
            returns = [episode["return"] for episode in batch.episodes]
            print(f"  Épisodes terminés : {len(batch.episodes)} | retour moyen = {np.mean(returns):+.3f} | "
                  + ", ".join(f"{outcome} x{count}" for outcome, count in outcomes.items()))

        for i in range(min(self.args.show, n)):
            flags = "TERMINÉ" if batch.terminated[i] else "TRONQUÉ" if batch.truncated[i] else ""
            print(f"  [{i}] agent {batch.agent_id[i]} | épisode {batch.episode_id[i]} | pas {batch.step[i]} | "
                  f"récompense {batch.rewards[i]:+.4f} | log_prob {batch.log_probs[i]:+.3f} {flags}")
            print(f"      obs     = {np.array2string(batch.obs[i], precision=2, suppress_small=True, max_line_width=140)}")
            print(f"      action  = {np.array2string(batch.actions[i], precision=2, suppress_small=True)}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Back-end d'entraînement IA Foot (mode debug).")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=5005)
    parser.add_argument("--model", choices=["mlp", "linear", "tree", "none"], default="mlp",
                        help="modèle de démonstration (poids aléatoires) envoyé à Unity à la connexion")
    parser.add_argument("--resend-every", type=int, default=0, metavar="N",
                        help="renvoie un nouveau modèle tous les N batchs (0 = jamais)")
    parser.add_argument("--deterministic", action="store_true", help="désactive l'exploration côté Unity")
    parser.add_argument("--time-scale", type=float, default=None, help="vitesse de simulation à imposer à Unity")
    parser.add_argument("--batch-size", type=int, default=None, help="seuil d'envoi des batchs à imposer à Unity")
    parser.add_argument("--show", type=int, default=2, metavar="K", help="nombre de transitions détaillées par batch")
    parser.add_argument("--verbose", action="store_true", help="affiche aussi le nom de chaque observation")
    parser.add_argument("--quiet", action="store_true", help="masque les lignes de debug réseau (une par message reçu / envoyé)")
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()

    # Console Windows : évite un plantage sur les caractères accentués.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    np.set_printoptions(linewidth=140)

    trainer = DebugTrainer(args)
    server = TrainingServer(args.host, args.port, debug=not args.quiet)
    server.on_connect = trainer.on_connect
    server.on_batch = trainer.on_batch
    server.on_model_ack = trainer.on_model_ack
    server.on_disconnect = trainer.on_disconnect
    server.serve_forever()


if __name__ == "__main__":
    main()
