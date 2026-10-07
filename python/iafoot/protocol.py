"""Format des messages échangés entre Unity et Python (identique dans les deux sens).

Trame :
    b"IAFT" | uint32 taille de l'en-tête | uint32 taille des données | en-tête JSON UTF-8 | données binaires

L'en-tête est un objet JSON avec un champ "type" et un champ "tensors" qui décrit chaque tableau
du bloc binaire : {"nom": {"dtype": "f32" | "i32", "shape": [...], "offset": ..., "nbytes": ...}}.
Tout est en little-endian.
"""

from __future__ import annotations

import json
import socket
import struct
from typing import Any

import numpy as np

MAGIC = b"IAFT"
PROTOCOL_VERSION = 1
_PREFIX = struct.Struct("<4sII")
_DTYPES = {"f32": np.dtype("<f4"), "i32": np.dtype("<i4")}
_MAX_SECTION_BYTES = 1 << 30


class ProtocolError(Exception):
    """Trame illisible ou incohérente."""


def _as_wire_array(name: str, value: Any) -> tuple[str, np.ndarray]:
    array = np.asarray(value)
    if array.dtype.kind == "f":
        return "f32", np.ascontiguousarray(array, dtype="<f4")
    if array.dtype.kind in "iub":
        return "i32", np.ascontiguousarray(array, dtype="<i4")
    raise ProtocolError(f"Tenseur '{name}' : type {array.dtype} non supporté (flottants ou entiers attendus)")


def encode(header: dict[str, Any], tensors: dict[str, Any] | None = None) -> bytes:
    """Construit une trame complète à partir d'un en-tête et de tableaux numpy."""
    header = dict(header)
    descriptors: dict[str, Any] = {}
    chunks: list[bytes] = []
    offset = 0
    for name, value in (tensors or {}).items():
        dtype, array = _as_wire_array(name, value)
        data = array.tobytes()
        descriptors[name] = {"dtype": dtype, "shape": list(array.shape), "offset": offset, "nbytes": len(data)}
        chunks.append(data)
        offset += len(data)
    header["tensors"] = descriptors

    # allow_nan=False : NaN / Infinity ne sont pas du JSON valide, autant échouer ici que côté Unity.
    header_bytes = json.dumps(header, allow_nan=False, separators=(",", ":")).encode("utf-8")
    return _PREFIX.pack(MAGIC, len(header_bytes), offset) + header_bytes + b"".join(chunks)


def decode(header_bytes: bytes, payload: bytes) -> tuple[dict[str, Any], dict[str, np.ndarray]]:
    header = json.loads(header_bytes.decode("utf-8"))
    tensors: dict[str, np.ndarray] = {}
    for name, descriptor in header.get("tensors", {}).items():
        dtype = _DTYPES.get(descriptor.get("dtype", "f32"))
        if dtype is None:
            raise ProtocolError(f"Tenseur '{name}' : dtype '{descriptor.get('dtype')}' inconnu")
        shape = tuple(int(dim) for dim in descriptor["shape"])
        count = int(np.prod(shape, dtype=np.int64)) if shape else 1
        start = int(descriptor["offset"])
        end = start + count * dtype.itemsize
        if start < 0 or end > len(payload):
            raise ProtocolError(f"Tenseur '{name}' hors du bloc de données")
        # .copy() : le tableau devient modifiable et indépendant du tampon réseau.
        tensors[name] = np.frombuffer(payload, dtype=dtype, count=count, offset=start).reshape(shape).copy()
    return header, tensors


def _read_exactly(sock: socket.socket, count: int) -> bytes:
    buffer = bytearray()
    while len(buffer) < count:
        chunk = sock.recv(min(count - len(buffer), 1 << 20))
        if not chunk:
            raise ConnectionError("Connexion fermée par Unity")
        buffer += chunk
    return bytes(buffer)


def read_message(sock: socket.socket) -> tuple[dict[str, Any], dict[str, np.ndarray]]:
    """Lit un message complet (bloquant). Lève ConnectionError si Unity ferme la connexion."""
    header, tensors, _ = read_frame(sock)
    return header, tensors


def read_frame(sock: socket.socket) -> tuple[dict[str, Any], dict[str, np.ndarray], int]:
    """Comme `read_message`, mais renvoie aussi la taille totale de la trame en octets."""
    magic, header_length, payload_length = _PREFIX.unpack(_read_exactly(sock, _PREFIX.size))
    if magic != MAGIC:
        raise ProtocolError("Signature 'IAFT' absente : ce n'est pas un client IA Foot")
    if header_length > _MAX_SECTION_BYTES or payload_length > _MAX_SECTION_BYTES:
        raise ProtocolError("Taille de trame démesurée")
    header_bytes = _read_exactly(sock, header_length)
    payload = _read_exactly(sock, payload_length)
    header, tensors = decode(header_bytes, payload)
    return header, tensors, _PREFIX.size + header_length + payload_length


def format_size(nbytes: int) -> str:
    """Taille lisible : octets, Ko ou Mo."""
    if nbytes < 1024:
        return f"{nbytes} o"
    if nbytes < 1024 * 1024:
        return f"{nbytes / 1024:.1f} Ko"
    return f"{nbytes / (1024 * 1024):.2f} Mo"
