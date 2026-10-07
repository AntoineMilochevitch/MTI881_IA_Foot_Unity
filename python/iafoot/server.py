"""Serveur TCP auquel Unity se connecte.

Unity envoie "hello" (description des observations / actions) puis des "batch" de transitions.
Python peut à tout moment envoyer "set_model" (nouveau modèle) ou "set_config" (réglages).
Plusieurs instances d'Unity peuvent se connecter en même temps (entraînement parallèle).
"""

from __future__ import annotations

import socket
import sys
import threading
import time
import traceback
from dataclasses import dataclass
from typing import Any, Callable

import numpy as np

from . import protocol
from .models import DecoderExport, ModelExport


@dataclass
class Batch:
    """Un lot de transitions envoyé par Unity pour un comportement.

    Les transitions sont dans l'ordre chronologique, tous agents mélangés. `trajectories()` les regroupe.
    `terminated` : l'épisode s'est vraiment fini (but) -> pas de valeur future à estimer.
    `truncated`  : l'épisode a été coupé (limite de pas, remise en jeu) -> bootstrapper avec `next_obs`.
    """

    behavior: str
    batch_id: int
    obs: np.ndarray            # [N, obs_size] float32
    actions: np.ndarray        # [N, action_size] float32
    rewards: np.ndarray        # [N] float32
    next_obs: np.ndarray | None  # [N, obs_size] float32 (None si désactivé dans Unity)
    terminated: np.ndarray     # [N] bool
    truncated: np.ndarray      # [N] bool
    log_probs: np.ndarray      # [N] float32 : log-probabilité de l'action au moment où elle a été choisie
    action_index: np.ndarray   # [N] int32 : indice dans la table d'actions (décodeur 'discrete'), sinon -1
    agent_id: np.ndarray       # [N] int32
    episode_id: np.ndarray     # [N] int32
    step: np.ndarray           # [N] int32 : numéro de la décision dans l'épisode
    model_version: np.ndarray  # [N] int32 : version du modèle qui a choisi l'action (0 = aléatoire)
    episodes: list[dict[str, Any]]  # épisodes terminés depuis le batch précédent
    header: dict[str, Any]

    def __len__(self) -> int:
        return int(self.rewards.shape[0])

    @property
    def nbytes(self) -> int:
        """Taille en mémoire des tableaux du batch, en octets."""
        arrays = (self.obs, self.actions, self.rewards, self.next_obs, self.terminated, self.truncated, self.log_probs,
                  self.action_index, self.agent_id, self.episode_id, self.step, self.model_version)
        return sum(int(array.nbytes) for array in arrays if array is not None)

    @classmethod
    def from_message(cls, header: dict[str, Any], tensors: dict[str, np.ndarray]) -> Batch:
        return cls(
            behavior=header.get("behavior", ""),
            batch_id=int(header.get("batch_id", -1)),
            obs=tensors["obs"],
            actions=tensors["actions"],
            rewards=tensors["rewards"],
            next_obs=tensors.get("next_obs"),
            terminated=tensors["terminated"].astype(bool),
            truncated=tensors["truncated"].astype(bool),
            log_probs=tensors["log_probs"],
            action_index=tensors["action_index"],
            agent_id=tensors["agent_id"],
            episode_id=tensors["episode_id"],
            step=tensors["step"],
            model_version=tensors["model_version"],
            episodes=list(header.get("episodes", [])),
            header=header,
        )

    def trajectories(self) -> dict[tuple[int, int], np.ndarray]:
        """Indices des transitions de chaque (agent_id, episode_id), triés par numéro de pas."""
        groups: dict[tuple[int, int], list[int]] = {}
        for i, key in enumerate(zip(self.agent_id.tolist(), self.episode_id.tolist())):
            groups.setdefault(key, []).append(i)
        return {key: np.asarray(sorted(indices, key=lambda i: self.step[i])) for key, indices in groups.items()}


class UnityConnection:
    """Une instance d'Unity connectée."""

    def __init__(self, sock: socket.socket, address: tuple[str, int], debug: bool = True):
        self.address = address
        self.hello: dict[str, Any] | None = None
        self.debug = debug
        self.bytes_received = 0
        self.bytes_sent = 0
        self._sock = sock
        self._send_lock = threading.Lock()

    def __repr__(self) -> str:
        session = self.hello.get("session") if self.hello else "?"
        return f"<Unity {self.address[0]}:{self.address[1]} session={session}>"

    @property
    def behaviors(self) -> list[dict[str, Any]]:
        return list(self.hello.get("behaviors", [])) if self.hello else []

    def send(self, header: dict[str, Any], tensors: dict[str, Any] | None = None) -> None:
        frame = protocol.encode(header, tensors)
        with self._send_lock:
            self._sock.sendall(frame)
            self.bytes_sent += len(frame)
        if self.debug:
            _log("->", f"{_describe(header, tensors or {})} | {protocol.format_size(len(frame))} envoyés à Unity")

    def send_model(self, model: ModelExport, *, version: int, behavior: str = "*",
                   decoder: DecoderExport | None = None) -> None:
        """Envoie un modèle : Unity le reconstruit et l'installe aussitôt sur les agents du comportement.

        behavior="*" vise tous les comportements. Unity répond par un message "model_ack".
        """
        header: dict[str, Any] = {"type": "set_model", "behavior": behavior, "version": int(version), "model": model.spec}
        tensors = dict(model.tensors)
        if decoder is not None:
            header["decoder"] = decoder.spec
            tensors.update(decoder.tensors)
        if model.check is not None:
            header["check"] = {"input": "check.input", "output": "check.output", "tolerance": 1e-3}
            tensors["check.input"], tensors["check.output"] = model.check
        self.send(header, tensors)

    def set_config(self, *, time_scale: float | None = None, batch_size: int | None = None, flush: bool = False) -> None:
        """Change la vitesse de simulation, le seuil d'envoi des batchs, ou force l'envoi du batch en cours."""
        header: dict[str, Any] = {"type": "set_config"}
        if time_scale is not None:
            header["time_scale"] = float(time_scale)
        if batch_size is not None:
            header["batch_size"] = int(batch_size)
        if flush:
            header["flush"] = True
        self.send(header)

    def close(self) -> None:
        try:
            self._sock.close()
        except OSError:
            pass


Handler = Callable[..., None]


def _log(direction: str, text: str) -> None:
    """Ligne de debug horodatée : '<-' = reçu d'Unity, '->' = envoyé à Unity, '..' = traitement local."""
    print(f"[{time.strftime('%H:%M:%S')}] {direction} {text}", flush=True)


def _describe(header: dict[str, Any], tensors: dict[str, Any]) -> str:
    """Résumé en une ligne du contenu d'un message."""
    kind = header.get("type")
    if kind == "hello":
        behaviors = header.get("behaviors", [])
        agents = sum(len(b.get("agents", [])) for b in behaviors)
        return f"hello : {len(behaviors)} comportement(s), {agents} agent(s)"
    if kind == "batch":
        return (f"batch n°{header.get('batch_id')} ('{header.get('behavior')}') : {header.get('count')} transitions, "
                f"{len(header.get('episodes', []))} épisode(s) terminé(s), {len(tensors)} tableaux")
    if kind == "set_model":
        parameters = sum(int(np.asarray(t).size) for t in tensors.values())
        return (f"set_model v{header.get('version')} pour '{header.get('behavior')}' : modèle "
                f"'{header.get('model', {}).get('type')}', {parameters} valeurs")
    if kind == "model_ack":
        return f"model_ack v{header.get('version')} : {'accepté' if header.get('ok') else 'REFUSÉ'}"
    if kind == "set_config":
        settings = {k: v for k, v in header.items() if k not in ("type", "tensors")}
        return f"set_config : {settings}"
    return f"message '{kind}'"


class TrainingServer:
    """Accepte les connexions d'Unity et distribue les messages à des fonctions de rappel.

    on_connect(connection)             après réception du "hello" (connection.hello / connection.behaviors)
    on_batch(connection, batch)        à chaque lot de transitions
    on_model_ack(connection, header)   réponse d'Unity à un send_model (header["ok"], header["error"])
    on_disconnect(connection)

    Les rappels sont exécutés un par un (verrou global), même avec plusieurs instances d'Unity.
    """

    def __init__(self, host: str = "127.0.0.1", port: int = 5005, debug: bool = True):
        self.host = host
        self.port = port
        #: Affiche une ligne par message reçu / envoyé, avec sa taille.
        self.debug = debug
        self.connections: list[UnityConnection] = []
        self.on_connect: Handler | None = None
        self.on_batch: Handler | None = None
        self.on_model_ack: Handler | None = None
        self.on_disconnect: Handler | None = None
        self._stop = threading.Event()
        self._callback_lock = threading.Lock()

    def stop(self) -> None:
        self._stop.set()

    def serve_forever(self) -> None:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind((self.host, self.port))
            listener.listen()
            # Délai court : sous Windows, accept() bloquant empêcherait Ctrl+C de fonctionner.
            listener.settimeout(0.5)
            print(f"[serveur] En écoute sur {self.host}:{self.port} - lancez Play dans Unity (Ctrl+C pour arrêter).", flush=True)

            try:
                while not self._stop.is_set():
                    try:
                        sock, address = listener.accept()
                    except socket.timeout:
                        continue
                    sock.settimeout(None)
                    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                    connection = UnityConnection(sock, address, self.debug)
                    if self.debug:
                        _log("..", f"connexion entrante depuis {address[0]}:{address[1]}, attente du hello")
                    threading.Thread(target=self._serve, args=(connection,), daemon=True).start()
            except KeyboardInterrupt:
                print("\n[serveur] Arrêt demandé.", flush=True)
            finally:
                self._stop.set()
                for connection in list(self.connections):
                    connection.close()

    def _call(self, handler: Handler | None, *args: Any) -> None:
        if handler is None:
            return
        with self._callback_lock:
            try:
                handler(*args)
            except Exception:  # noqa: BLE001 - une erreur dans le code d'entraînement ne doit pas couper Unity
                name = getattr(handler, "__name__", repr(handler))
                print(f"\n[serveur] ERREUR dans votre fonction '{name}' (la connexion avec Unity est conservée) :", flush=True)
                traceback.print_exc()
                sys.stderr.flush()

    def _serve(self, connection: UnityConnection) -> None:
        self.connections.append(connection)
        try:
            while not self._stop.is_set():
                header, tensors, nbytes = protocol.read_frame(connection._sock)
                connection.bytes_received += nbytes
                kind = header.get("type")
                if self.debug:
                    _log("<-", f"{_describe(header, tensors)} | {protocol.format_size(nbytes)} reçus d'Unity")
                if kind == "hello":
                    if header.get("protocol") != protocol.PROTOCOL_VERSION:
                        print(f"[serveur] Attention : Unity parle le protocole v{header.get('protocol')}, "
                              f"ce serveur le v{protocol.PROTOCOL_VERSION}.", flush=True)
                    connection.hello = header
                    self._call(self.on_connect, connection)
                elif kind == "batch":
                    self._call(self.on_batch, connection, Batch.from_message(header, tensors))
                elif kind == "model_ack":
                    self._call(self.on_model_ack, connection, header)
                else:
                    print(f"[serveur] Message de type inconnu ignoré : {kind!r}", flush=True)
        except (ConnectionError, OSError) as error:
            if not self._stop.is_set():
                print(f"[serveur] {connection} déconnecté ({error}).", flush=True)
        except protocol.ProtocolError as error:
            print(f"[serveur] {connection} : trame invalide, connexion fermée ({error}).", flush=True)
        finally:
            connection.close()
            if connection in self.connections:
                self.connections.remove(connection)
            self._call(self.on_disconnect, connection)
