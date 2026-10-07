"""Description des modèles envoyés à Unity.

Un modèle est décrit par un objet JSON ("spec") qui référence des tableaux de poids par leur nom.
Unity reconstruit le modèle à partir de cette description : il n'y a donc aucune dépendance à PyTorch
côté jeu, et le format n'est pas limité aux réseaux de neurones.

Types de modèles compris par Unity (registre extensible, voir PolicyModels.cs) :
    sequential / mlp   suite de couches : dense, activation, layer_norm
    linear             une seule couche dense
    decision_tree      arbre binaire au format scikit-learn
    ensemble           moyenne ou somme pondérée de modèles quelconques (forêts, boosting...)
    constant           sortie fixe

N'importe quel modèle peut recevoir une normalisation d'entrée avec `with_input_norm`.

Décodeurs d'actions (comment la sortie du modèle devient une action) :
    direct     une sortie par dimension d'action : moyenne gaussienne (continu) ou logit (bouton)
    discrete   un score par ligne d'une table d'actions : argmax, epsilon-greedy ou softmax
"""

from __future__ import annotations

import itertools
import math
from dataclasses import dataclass, field
from typing import Any, Sequence

import numpy as np

_counter = itertools.count()


def _unique(prefix: str) -> str:
    """Nom de tenseur unique : plusieurs modèles peuvent cohabiter dans un même message."""
    return f"{prefix}{next(_counter)}"


@dataclass
class ModelExport:
    """Un modèle prêt à être envoyé : sa description JSON et ses tableaux de poids."""

    spec: dict[str, Any]
    tensors: dict[str, np.ndarray] = field(default_factory=dict)
    #: Exemple (entrée, sortie attendue). Unity refuse le modèle si son propre calcul donne autre chose.
    check: tuple[np.ndarray, np.ndarray] | None = None

    def predict(self, observation: Sequence[float]) -> np.ndarray:
        """Évalue le modèle en numpy, avec exactement la sémantique implémentée côté Unity."""
        return _predict(self.spec, self.tensors, np.asarray(observation, dtype=np.float32))

    def with_input_norm(self, mean: Sequence[float], std: Sequence[float], clip: float = 10.0) -> ModelExport:
        """Ajoute (x - mean) / std avant le modèle (statistiques des observations calculées côté Python)."""
        prefix = _unique("norm")
        spec = dict(self.spec)
        spec["input_norm"] = {"mean": f"{prefix}.mean", "std": f"{prefix}.std", "clip": float(clip)}
        tensors = dict(self.tensors)
        tensors[f"{prefix}.mean"] = np.asarray(mean, dtype=np.float32)
        tensors[f"{prefix}.std"] = np.asarray(std, dtype=np.float32)
        return ModelExport(spec, tensors, None)

    def with_check(self, observation: Sequence[float], expected: Sequence[float] | None = None) -> ModelExport:
        """Joint un exemple de vérification. Sans `expected`, la sortie de référence est calculée en numpy."""
        observation = np.asarray(observation, dtype=np.float32)
        output = self.predict(observation) if expected is None else np.asarray(expected, dtype=np.float32)
        return ModelExport(self.spec, self.tensors, (observation, output))


@dataclass
class DecoderExport:
    spec: dict[str, Any]
    tensors: dict[str, np.ndarray] = field(default_factory=dict)


# --------------------------------------------------------------------------- Réseaux et modèles linéaires


def mlp(weights: Sequence[np.ndarray], biases: Sequence[np.ndarray | None], activations: Sequence[str | None]) -> ModelExport:
    """Perceptron multicouche à partir de tableaux numpy.

    `weights[i]` est de forme [sorties, entrées] (convention de torch.nn.Linear) et `activations[i]`
    est le nom de l'activation appliquée après la couche i (None pour aucune).
    """
    if not (len(weights) == len(biases) == len(activations)):
        raise ValueError("weights, biases et activations doivent avoir la même longueur")

    prefix = _unique("mlp")
    layers: list[dict[str, Any]] = []
    tensors: dict[str, np.ndarray] = {}
    for i, (weight, bias, activation) in enumerate(zip(weights, biases, activations)):
        layer: dict[str, Any] = {"type": "dense", "weight": f"{prefix}.{i}.weight"}
        tensors[layer["weight"]] = np.asarray(weight, dtype=np.float32)
        if bias is not None:
            layer["bias"] = f"{prefix}.{i}.bias"
            tensors[layer["bias"]] = np.asarray(bias, dtype=np.float32)
        if activation:
            layer["activation"] = activation
        layers.append(layer)
    return ModelExport({"type": "sequential", "layers": layers}, tensors)


def linear(weight: np.ndarray, bias: np.ndarray | None = None) -> ModelExport:
    """Modèle linéaire y = W·x + b, avec W de forme [sorties, entrées]."""
    prefix = _unique("linear")
    spec: dict[str, Any] = {"type": "linear", "weight": f"{prefix}.weight"}
    tensors = {spec["weight"]: np.asarray(weight, dtype=np.float32)}
    if bias is not None:
        spec["bias"] = f"{prefix}.bias"
        tensors[spec["bias"]] = np.asarray(bias, dtype=np.float32)
    return ModelExport(spec, tensors)


def from_torch(module: Any, check: bool = True) -> ModelExport:
    """Convertit un réseau PyTorch séquentiel (nn.Sequential, éventuellement imbriqués).

    Couches gérées : Linear, LayerNorm (1D), ReLU, Tanh, Sigmoid, LeakyReLU, ELU, GELU, SiLU, Softplus,
    Softmax ; Identity, Dropout et Flatten sont ignorées (sans effet à l'inférence).
    Avec `check=True`, une entrée aléatoire et la sortie calculée par PyTorch sont jointes au modèle :
    Unity vérifie alors que sa propre implémentation donne le même résultat.
    """
    import torch
    from torch import nn

    prefix = _unique("torch")
    layers: list[dict[str, Any]] = []
    tensors: dict[str, np.ndarray] = {}

    def array(parameter: Any) -> np.ndarray:
        return parameter.detach().cpu().numpy().astype(np.float32)

    def visit(layer: Any) -> None:
        index = len(layers)
        if isinstance(layer, nn.Sequential):
            for child in layer:
                visit(child)
        elif isinstance(layer, nn.Linear):
            spec: dict[str, Any] = {"type": "dense", "weight": f"{prefix}.{index}.weight"}
            tensors[spec["weight"]] = array(layer.weight)
            if layer.bias is not None:
                spec["bias"] = f"{prefix}.{index}.bias"
                tensors[spec["bias"]] = array(layer.bias)
            layers.append(spec)
        elif isinstance(layer, nn.LayerNorm):
            if len(layer.normalized_shape) != 1 or not layer.elementwise_affine:
                raise NotImplementedError("LayerNorm : seule la version 1D avec poids est gérée")
            spec = {"type": "layer_norm", "weight": f"{prefix}.{index}.weight", "eps": float(layer.eps)}
            tensors[spec["weight"]] = array(layer.weight)
            if layer.bias is not None:
                spec["bias"] = f"{prefix}.{index}.bias"
                tensors[spec["bias"]] = array(layer.bias)
            layers.append(spec)
        elif isinstance(layer, (nn.Identity, nn.Dropout, nn.Flatten)):
            pass
        elif isinstance(layer, nn.LeakyReLU):
            layers.append({"type": "activation", "function": "leaky_relu", "negative_slope": float(layer.negative_slope)})
        elif isinstance(layer, nn.ELU):
            layers.append({"type": "activation", "function": "elu", "alpha": float(layer.alpha)})
        elif isinstance(layer, nn.GELU):
            if layer.approximate != "none":
                raise NotImplementedError("GELU : seule la version exacte (approximate='none') est gérée")
            layers.append({"type": "activation", "function": "gelu"})
        elif isinstance(layer, nn.Softplus):
            if layer.beta != 1:
                raise NotImplementedError("Softplus : seul beta=1 est géré")
            layers.append({"type": "activation", "function": "softplus"})
        elif type(layer) in _torch_activations(nn):
            layers.append({"type": "activation", "function": _torch_activations(nn)[type(layer)]})
        else:
            raise NotImplementedError(
                f"Couche PyTorch non gérée : {type(layer).__name__}. Ajoutez un type de couche des deux côtés "
                "(LayerFactory.Register en C#, from_torch en Python) ou exportez un modèle 'sequential' à la main."
            )

    visit(module)
    first_dense = next((layer for layer in layers if layer["type"] == "dense"), None)
    if first_dense is None:
        raise ValueError("Le réseau ne contient aucune couche Linear")
    input_size = int(tensors[first_dense["weight"]].shape[1])
    export = ModelExport({"type": "sequential", "input_size": input_size, "layers": layers}, tensors)

    if check:
        was_training = module.training
        module.eval()
        example = torch.randn(input_size)
        with torch.no_grad():
            expected = module(example)
        module.train(was_training)
        export.check = (example.numpy().astype(np.float32), expected.numpy().astype(np.float32))
    return export


def _torch_activations(nn: Any) -> dict[type, str]:
    return {nn.ReLU: "relu", nn.Tanh: "tanh", nn.Sigmoid: "sigmoid", nn.SiLU: "silu", nn.Softmax: "softmax"}


# --------------------------------------------------------------------------- Arbres et ensembles


def decision_tree(feature: Sequence[int], threshold: Sequence[float], left: Sequence[int], right: Sequence[int],
                  value: np.ndarray) -> ModelExport:
    """Arbre de décision binaire.

    Au nœud i : on va vers `left[i]` si x[feature[i]] <= threshold[i], sinon vers `right[i]`.
    Une feuille a left[i] == -1 et renvoie la ligne `value[i]` (forme [noeuds, sorties]).
    """
    prefix = _unique("tree")
    tensors = {
        f"{prefix}.feature": np.asarray(feature, dtype=np.int32),
        f"{prefix}.threshold": np.asarray(threshold, dtype=np.float32),
        f"{prefix}.left": np.asarray(left, dtype=np.int32),
        f"{prefix}.right": np.asarray(right, dtype=np.int32),
        f"{prefix}.value": np.asarray(value, dtype=np.float32),
    }
    if tensors[f"{prefix}.value"].ndim != 2:
        raise ValueError("value doit être de forme [noeuds, sorties]")
    spec = {"type": "decision_tree", **{key.split(".")[1]: key for key in tensors}}
    return ModelExport(spec, tensors)


def from_sklearn_tree(estimator: Any) -> ModelExport:
    """Convertit un DecisionTreeRegressor / DecisionTreeClassifier de scikit-learn déjà entraîné.

    Régression : la sortie est la valeur prédite (une colonne par sortie).
    Classification : la sortie est la probabilité de chaque classe (à utiliser avec un décodeur 'discrete').
    """
    tree = estimator.tree_
    value = np.asarray(tree.value, dtype=np.float64)  # [noeuds, sorties, classes]
    if hasattr(estimator, "classes_"):
        if value.shape[1] != 1:
            raise NotImplementedError("Classifieur multi-sorties non géré")
        value = value[:, 0, :]
        value = value / np.maximum(value.sum(axis=1, keepdims=True), 1e-12)
    else:
        value = value[:, :, 0]

    feature = np.where(tree.children_left < 0, 0, tree.feature)
    return decision_tree(feature, tree.threshold, tree.children_left, tree.children_right, value)


def ensemble(models: Sequence[ModelExport], aggregate: str = "mean", weights: Sequence[float] | None = None,
             bias: Sequence[float] | None = None) -> ModelExport:
    """Combine des modèles quelconques : 'mean' (forêt aléatoire, bagging) ou 'sum' (+ poids et biais : boosting)."""
    if aggregate not in ("mean", "sum"):
        raise ValueError("aggregate doit valoir 'mean' ou 'sum'")
    spec: dict[str, Any] = {"type": "ensemble", "aggregate": aggregate, "models": [model.spec for model in models]}
    tensors: dict[str, np.ndarray] = {}
    for model in models:
        tensors.update(model.tensors)
    prefix = _unique("ensemble")
    if weights is not None:
        spec["weights"] = f"{prefix}.weights"
        tensors[spec["weights"]] = np.asarray(weights, dtype=np.float32)
    if bias is not None:
        spec["bias"] = f"{prefix}.bias"
        tensors[spec["bias"]] = np.asarray(bias, dtype=np.float32)
    return ModelExport(spec, tensors)


def from_sklearn_forest(estimator: Any) -> ModelExport:
    """Convertit un RandomForestRegressor / RandomForestClassifier de scikit-learn."""
    return ensemble([from_sklearn_tree(tree) for tree in estimator.estimators_], aggregate="mean")


def constant(value: Sequence[float]) -> ModelExport:
    """Modèle qui renvoie toujours la même sortie (utile pour tester la chaîne)."""
    return ModelExport({"type": "constant", "value": [float(v) for v in value]})


# --------------------------------------------------------------------------- Décodeurs d'actions


def direct_decoder(log_std: float | Sequence[float] = -0.7, deterministic: bool = False) -> DecoderExport:
    """Une sortie du modèle par dimension d'action.

    Dimension continue : action ~ Normale(sortie, exp(log_std)). Dimension bouton : sortie = logit de
    la probabilité d'appuyer. `deterministic=True` supprime l'exploration (évaluation).
    """
    values = [float(log_std)] if np.isscalar(log_std) else [float(v) for v in log_std]
    return DecoderExport({"type": "direct", "log_std": values, "deterministic": bool(deterministic)})


def discrete_decoder(actions: np.ndarray, mode: str = "argmax", epsilon: float = 0.1,
                     temperature: float = 1.0) -> DecoderExport:
    """Le modèle sort un score par ligne de `actions` (forme [N, taille d'action]).

    mode : 'argmax', 'epsilon_greedy' (Q-learning) ou 'softmax' (tirage selon softmax(scores / temperature)).
    """
    if mode not in ("argmax", "epsilon_greedy", "softmax"):
        raise ValueError("mode doit valoir 'argmax', 'epsilon_greedy' ou 'softmax'")
    name = _unique("actions")
    spec = {"type": "discrete", "actions": name, "mode": mode, "epsilon": float(epsilon), "temperature": float(temperature)}
    return DecoderExport(spec, {name: np.asarray(actions, dtype=np.float32)})


def foot_action_table() -> np.ndarray:
    """Petite table d'actions discrètes pour le jeu de foot.

    Colonnes : move_x, move_z, shoot, shoot_power, shoot_curve, tackle, jump (voir FootSpaces.cs).
    Lignes : immobile, 8 directions, 8 directions + tir fort, tir faible, tacle vers l'avant, saut.
    """
    rows = [[0, 0, 0, 0, 0, 0, 0]]
    directions = [(math.cos(k * math.pi / 4), math.sin(k * math.pi / 4)) for k in range(8)]
    rows += [[x, z, 0, 0, 0, 0, 0] for x, z in directions]
    rows += [[x, z, 1, 1, 0, 0, 0] for x, z in directions]
    rows += [[1, 0, 1, -0.5, 0, 0, 0], [1, 0, 0, 0, 0, 1, 0], [0, 0, 0, 0, 0, 0, 1]]
    return np.asarray(rows, dtype=np.float32)


# --------------------------------------------------------------------------- Évaluation de référence (numpy)

_erf = np.vectorize(math.erf, otypes=[np.float64])


def _softmax(x: np.ndarray) -> np.ndarray:
    e = np.exp(x - x.max())
    return e / e.sum()


def _activate(name: str | None, x: np.ndarray, spec: dict[str, Any]) -> np.ndarray:
    if not name or name in ("identity", "linear", "none"):
        return x
    if name == "relu":
        return np.maximum(x, 0)
    if name == "tanh":
        return np.tanh(x)
    if name == "sigmoid":
        return 1 / (1 + np.exp(-x))
    if name == "softplus":
        return np.where(x > 20, x, np.log1p(np.exp(np.minimum(x, 20))))
    if name in ("silu", "swish"):
        return x / (1 + np.exp(-x))
    if name == "gelu":
        return 0.5 * x * (1 + _erf(x / math.sqrt(2)))
    if name == "softmax":
        return _softmax(x)
    if name == "leaky_relu":
        return np.where(x > 0, x, spec.get("negative_slope", 0.01) * x)
    if name == "elu":
        return np.where(x > 0, x, spec.get("alpha", 1.0) * (np.exp(np.minimum(x, 0)) - 1))
    raise NotImplementedError(f"Activation inconnue : {name}")


def _resolve(reference: Any, tensors: dict[str, np.ndarray]) -> np.ndarray:
    return tensors[reference] if isinstance(reference, str) else np.asarray(reference, dtype=np.float32)


def _predict(spec: dict[str, Any], tensors: dict[str, np.ndarray], x: np.ndarray) -> np.ndarray:
    x = x.astype(np.float64)
    if "input_norm" in spec:
        norm = spec["input_norm"]
        std = np.maximum(_resolve(norm["std"], tensors), 1e-6)
        x = np.clip((x - _resolve(norm["mean"], tensors)) / std, -norm.get("clip", 10.0), norm.get("clip", 10.0))

    kind = spec["type"]
    if kind == "linear":
        y = _resolve(spec["weight"], tensors).astype(np.float64) @ x
        if "bias" in spec:
            y = y + _resolve(spec["bias"], tensors)
    elif kind in ("sequential", "mlp"):
        y = x
        for layer in spec["layers"]:
            layer_type = layer.get("type", "dense")
            if layer_type in ("dense", "linear"):
                y = _resolve(layer["weight"], tensors).astype(np.float64) @ y
                if "bias" in layer:
                    y = y + _resolve(layer["bias"], tensors)
                y = _activate(layer.get("activation"), y, layer)
            elif layer_type == "activation":
                y = _activate(layer.get("function") or layer.get("activation"), y, layer)
            elif layer_type == "layer_norm":
                y = (y - y.mean()) / np.sqrt(y.var() + layer.get("eps", 1e-5)) * _resolve(layer["weight"], tensors)
                if "bias" in layer:
                    y = y + _resolve(layer["bias"], tensors)
            else:
                raise NotImplementedError(f"Type de couche inconnu : {layer_type}")
    elif kind == "decision_tree":
        feature, threshold = _resolve(spec["feature"], tensors), _resolve(spec["threshold"], tensors)
        left, right = _resolve(spec["left"], tensors), _resolve(spec["right"], tensors)
        node = 0
        while left[node] >= 0:
            # Comparaison en float32, comme côté Unity.
            node = int(left[node] if np.float32(x[int(feature[node])]) <= np.float32(threshold[node]) else right[node])
        y = _resolve(spec["value"], tensors)[node]
    elif kind == "ensemble":
        outputs = [_predict(model, tensors, x.astype(np.float32)) for model in spec["models"]]
        weights = _resolve(spec["weights"], tensors) if "weights" in spec else np.ones(len(outputs))
        if spec.get("aggregate", "mean") == "mean":
            weights = weights / len(outputs)
        y = sum(w * out for w, out in zip(weights, outputs))
        if "bias" in spec:
            y = y + _resolve(spec["bias"], tensors)
    elif kind == "constant":
        y = _resolve(spec["value"], tensors)
    else:
        raise NotImplementedError(f"Type de modèle inconnu : {kind}")
    return np.asarray(y, dtype=np.float32)
