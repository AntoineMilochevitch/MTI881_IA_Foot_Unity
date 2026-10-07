using System;
using System.Collections.Generic;

namespace IAFoot.Learning
{
    /// <summary>
    /// Modèle quelconque : un vecteur d'observations en entrée, un vecteur de sortie.
    /// Réseau de neurones, modèle linéaire, arbre de décision, ensemble... tout ce qui sait faire ça convient.
    /// </summary>
    public interface IPolicyModel
    {
        int OutputSize { get; }

        /// <summary>Lève une exception si le modèle ne peut pas consommer des observations de cette taille.</summary>
        void CheckInputSize(int inputSize);

        void Evaluate(float[] input, float[] output);

        string Describe();
    }

    /// <summary>Donne accès aux tenseurs du message pendant la construction d'un modèle.</summary>
    public sealed class ModelBuildContext
    {
        readonly Dictionary<string, Tensor> tensors;

        public ModelBuildContext(Dictionary<string, Tensor> tensors)
        {
            this.tensors = tensors ?? new Dictionary<string, Tensor>();
        }

        /// <summary>
        /// Résout une référence : soit le nom d'un tenseur du message (cas normal pour les poids),
        /// soit des nombres écrits directement dans le JSON (pratique pour les petits vecteurs).
        /// </summary>
        public Tensor Resolve(object reference, string what)
        {
            switch (reference)
            {
                case string name:
                    return tensors.TryGetValue(name, out Tensor tensor)
                        ? tensor
                        : throw new FormatException($"{what} : tenseur '{name}' absent du message");
                case double number:
                    return new Tensor(new[] { (float)number }, 1);
                case List<object> list:
                {
                    var values = new List<float>();
                    var shape = new List<int>();
                    Flatten(list, 0, values, shape, what);
                    return new Tensor(values.ToArray(), shape.ToArray());
                }
                default:
                    throw new FormatException($"{what} : nom de tenseur ou tableau de nombres attendu");
            }
        }

        public Tensor Require(Dictionary<string, object> spec, string key, string what) =>
            Resolve(spec.Require(key, what), $"{what}.{key}");

        public Tensor Optional(Dictionary<string, object> spec, string key, string what) =>
            spec.Has(key) ? Resolve(spec[key], $"{what}.{key}") : null;

        static void Flatten(List<object> list, int depth, List<float> values, List<int> shape, string what)
        {
            if (shape.Count == depth)
                shape.Add(list.Count);
            else if (shape[depth] != list.Count)
                throw new FormatException($"{what} : tableau non rectangulaire");

            foreach (object item in list)
            {
                if (item is List<object> nested)
                    Flatten(nested, depth + 1, values, shape, what);
                else if (item is double number)
                    values.Add((float)number);
                else
                    throw new FormatException($"{what} : nombre attendu");
            }
        }
    }

    /// <summary>
    /// Registre des types de modèles. Pour ajouter un nouveau type (ex : "knn", "gru"...), écrire une classe
    /// qui implémente <see cref="IPolicyModel"/> puis appeler <see cref="Register"/> : aucun autre code à modifier.
    /// </summary>
    public static class PolicyModelFactory
    {
        public delegate IPolicyModel Builder(Dictionary<string, object> spec, ModelBuildContext context);

        static readonly Dictionary<string, Builder> Builders = new Dictionary<string, Builder>
        {
            ["sequential"] = SequentialModel.Build,
            ["mlp"] = SequentialModel.Build,
            ["linear"] = SequentialModel.BuildLinear,
            ["decision_tree"] = DecisionTreeModel.Build,
            ["ensemble"] = EnsembleModel.Build,
            ["constant"] = ConstantModel.Build,
        };

        public static void Register(string type, Builder builder) => Builders[type] = builder;

        public static IEnumerable<string> KnownTypes => Builders.Keys;

        public static IPolicyModel Build(object specObject, ModelBuildContext context)
        {
            Dictionary<string, object> spec = specObject.AsObject("model");
            string type = spec.GetString("type") ?? throw new FormatException("model : champ 'type' manquant");
            if (!Builders.TryGetValue(type, out Builder builder))
                throw new NotSupportedException($"Type de modèle inconnu : '{type}' (connus : {string.Join(", ", Builders.Keys)})");

            IPolicyModel model = builder(spec, context);

            // Normalisation des entrées, disponible pour n'importe quel type de modèle.
            if (spec.Has("input_norm"))
            {
                Dictionary<string, object> norm = spec["input_norm"].AsObject("input_norm");
                model = new NormalizedModel(model,
                    context.Require(norm, "mean", "input_norm").AsFloats(),
                    context.Require(norm, "std", "input_norm").AsFloats(),
                    norm.GetFloat("clip", 10f));
            }
            return model;
        }
    }

    // ---------------------------------------------------------------------- Réseau séquentiel

    /// <summary>Couche d'un <see cref="SequentialModel"/>.</summary>
    public interface ILayer
    {
        /// <summary>Taille de sortie pour cette taille d'entrée (lève une exception si incompatible).</summary>
        int GetOutputSize(int inputSize);

        void Forward(float[] input, float[] output);

        string Describe();
    }

    /// <summary>Registre des types de couches (même principe que <see cref="PolicyModelFactory"/>).</summary>
    public static class LayerFactory
    {
        public delegate ILayer Builder(Dictionary<string, object> spec, ModelBuildContext context);

        static readonly Dictionary<string, Builder> Builders = new Dictionary<string, Builder>
        {
            ["dense"] = DenseLayer.Build,
            ["linear"] = DenseLayer.Build,
            ["activation"] = ActivationLayer.Build,
            ["layer_norm"] = LayerNormLayer.Build,
        };

        public static void Register(string type, Builder builder) => Builders[type] = builder;

        public static ILayer Build(object specObject, ModelBuildContext context)
        {
            Dictionary<string, object> spec = specObject.AsObject("layer");
            string type = spec.GetString("type", "dense");
            if (!Builders.TryGetValue(type, out Builder builder))
                throw new NotSupportedException($"Type de couche inconnu : '{type}' (connus : {string.Join(", ", Builders.Keys)})");
            return builder(spec, context);
        }
    }

    public delegate void Activation(float[] values);

    public static class Activations
    {
        public delegate Activation Builder(Dictionary<string, object> spec);

        static readonly Dictionary<string, Builder> Builders = new Dictionary<string, Builder>
        {
            ["identity"] = _ => null,
            ["linear"] = _ => null,
            ["none"] = _ => null,
            ["relu"] = _ => v => Map(v, x => x > 0f ? x : 0f),
            ["tanh"] = _ => v => Map(v, x => MathF.Tanh(x)),
            ["sigmoid"] = _ => v => Map(v, Sigmoid),
            ["softplus"] = _ => v => Map(v, x => x > 20f ? x : MathF.Log(1f + MathF.Exp(x))),
            ["silu"] = _ => v => Map(v, x => x * Sigmoid(x)),
            ["swish"] = _ => v => Map(v, x => x * Sigmoid(x)),
            ["gelu"] = _ => v => Map(v, x => 0.5f * x * (1f + Erf(x * 0.70710678f))),
            ["softmax"] = _ => Softmax,
            ["leaky_relu"] = spec =>
            {
                float slope = spec.GetFloat("negative_slope", 0.01f);
                return v => Map(v, x => x > 0f ? x : slope * x);
            },
            ["elu"] = spec =>
            {
                float alpha = spec.GetFloat("alpha", 1f);
                return v => Map(v, x => x > 0f ? x : alpha * (MathF.Exp(x) - 1f));
            },
        };

        public static void Register(string name, Builder builder) => Builders[name] = builder;

        /// <summary>Retourne null pour l'identité.</summary>
        public static Activation Get(string name, Dictionary<string, object> spec)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            if (!Builders.TryGetValue(name, out Builder builder))
                throw new NotSupportedException($"Activation inconnue : '{name}' (connues : {string.Join(", ", Builders.Keys)})");
            return builder(spec);
        }

        public static float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));

        public static void Softmax(float[] values)
        {
            float max = float.NegativeInfinity;
            foreach (float v in values)
                max = MathF.Max(max, v);

            float sum = 0f;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = MathF.Exp(values[i] - max);
                sum += values[i];
            }
            for (int i = 0; i < values.Length; i++)
                values[i] /= sum;
        }

        static void Map(float[] values, Func<float, float> f)
        {
            for (int i = 0; i < values.Length; i++)
                values[i] = f(values[i]);
        }

        // Approximation d'Abramowitz et Stegun (7.1.26), erreur maximale ≈ 1.5e-7.
        static float Erf(float x)
        {
            float sign = x < 0f ? -1f : 1f;
            x = MathF.Abs(x);
            float t = 1f / (1f + 0.3275911f * x);
            float poly = ((((1.061405429f * t - 1.453152027f) * t + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t;
            return sign * (1f - poly * MathF.Exp(-x * x));
        }
    }

    /// <summary>y = activation(W·x + b), avec W de forme [sorties, entrées] (convention de torch.nn.Linear).</summary>
    public sealed class DenseLayer : ILayer
    {
        readonly float[] weight;
        readonly float[] bias;
        readonly int inputSize;
        readonly int outputSize;
        readonly Activation activation;
        readonly string activationName;

        public DenseLayer(Tensor weight, Tensor bias, Activation activation, string activationName)
        {
            if (weight.Shape.Length != 2)
                throw new FormatException("dense : 'weight' doit être de forme [sorties, entrées]");
            outputSize = weight.Shape[0];
            inputSize = weight.Shape[1];
            this.weight = weight.AsFloats();
            this.bias = bias?.AsFloats();
            if (this.bias != null && this.bias.Length != outputSize)
                throw new FormatException($"dense : 'bias' a {this.bias.Length} éléments au lieu de {outputSize}");
            this.activation = activation;
            this.activationName = activationName;
        }

        public int InputSize => inputSize;

        public static ILayer Build(Dictionary<string, object> spec, ModelBuildContext context)
        {
            string activationName = spec.GetString("activation");
            return new DenseLayer(
                context.Require(spec, "weight", "dense"),
                context.Optional(spec, "bias", "dense"),
                Activations.Get(activationName, spec),
                activationName);
        }

        public int GetOutputSize(int size) =>
            size == inputSize ? outputSize : throw new FormatException($"dense : attend {inputSize} entrées, en reçoit {size}");

        public void Forward(float[] input, float[] output)
        {
            int w = 0;
            for (int o = 0; o < outputSize; o++)
            {
                float sum = bias != null ? bias[o] : 0f;
                for (int i = 0; i < inputSize; i++)
                    sum += weight[w++] * input[i];
                output[o] = sum;
            }
            activation?.Invoke(output);
        }

        public string Describe() =>
            string.IsNullOrEmpty(activationName) ? $"dense({inputSize}→{outputSize})" : $"dense({inputSize}→{outputSize}, {activationName})";
    }

    public sealed class ActivationLayer : ILayer
    {
        readonly Activation activation;
        readonly string name;

        public ActivationLayer(string name, Activation activation)
        {
            this.name = name;
            this.activation = activation;
        }

        public static ILayer Build(Dictionary<string, object> spec, ModelBuildContext context)
        {
            string name = spec.GetString("function") ?? spec.GetString("activation")
                ?? throw new FormatException("activation : champ 'function' manquant");
            return new ActivationLayer(name, Activations.Get(name, spec));
        }

        public int GetOutputSize(int size) => size;

        public void Forward(float[] input, float[] output)
        {
            Array.Copy(input, output, input.Length);
            activation?.Invoke(output);
        }

        public string Describe() => name;
    }

    /// <summary>Normalisation de couche (torch.nn.LayerNorm sur la dernière dimension).</summary>
    public sealed class LayerNormLayer : ILayer
    {
        readonly float[] weight;
        readonly float[] bias;
        readonly float epsilon;
        readonly int size;

        public LayerNormLayer(float[] weight, float[] bias, float epsilon)
        {
            this.weight = weight;
            this.bias = bias;
            this.epsilon = epsilon;
            size = weight.Length;
            if (bias != null && bias.Length != size)
                throw new FormatException("layer_norm : 'weight' et 'bias' de tailles différentes");
        }

        public static ILayer Build(Dictionary<string, object> spec, ModelBuildContext context) =>
            new LayerNormLayer(
                context.Require(spec, "weight", "layer_norm").AsFloats(),
                context.Optional(spec, "bias", "layer_norm")?.AsFloats(),
                spec.GetFloat("eps", 1e-5f));

        public int GetOutputSize(int inputSize) =>
            inputSize == size ? size : throw new FormatException($"layer_norm : attend {size} entrées, en reçoit {inputSize}");

        public void Forward(float[] input, float[] output)
        {
            float mean = 0f;
            for (int i = 0; i < size; i++)
                mean += input[i];
            mean /= size;

            float variance = 0f;
            for (int i = 0; i < size; i++)
                variance += (input[i] - mean) * (input[i] - mean);
            float inverseStd = 1f / MathF.Sqrt(variance / size + epsilon);

            for (int i = 0; i < size; i++)
                output[i] = (input[i] - mean) * inverseStd * weight[i] + (bias != null ? bias[i] : 0f);
        }

        public string Describe() => $"layer_norm({size})";
    }

    /// <summary>Suite de couches appliquées l'une après l'autre : couvre les MLP et les modèles linéaires.</summary>
    public sealed class SequentialModel : IPolicyModel
    {
        readonly ILayer[] layers;
        readonly float[][] buffers;
        readonly int inputSize;

        public SequentialModel(IList<ILayer> layers, int inputSize)
        {
            this.layers = new ILayer[layers.Count];
            buffers = new float[layers.Count][];
            this.inputSize = inputSize;

            int size = inputSize;
            for (int i = 0; i < layers.Count; i++)
            {
                this.layers[i] = layers[i];
                try
                {
                    size = layers[i].GetOutputSize(size);
                }
                catch (FormatException e)
                {
                    throw new FormatException($"couche {i} : {e.Message}");
                }
                buffers[i] = new float[size];
            }
            OutputSize = size;
        }

        public int OutputSize { get; }

        public static IPolicyModel Build(Dictionary<string, object> spec, ModelBuildContext context)
        {
            var layers = new List<ILayer>();
            foreach (object layerSpec in spec.Require("layers", "sequential").AsList("layers"))
                layers.Add(LayerFactory.Build(layerSpec, context));

            int inputSize = spec.GetInt("input_size", -1);
            if (inputSize < 0 && layers.Count > 0 && layers[0] is DenseLayer dense)
                inputSize = dense.InputSize;
            if (inputSize < 0)
                throw new FormatException("sequential : 'input_size' requis quand la première couche n'est pas 'dense'");
            return new SequentialModel(layers, inputSize);
        }

        /// <summary>Raccourci {"type":"linear","weight":..,"bias":..} : une seule couche dense.</summary>
        public static IPolicyModel BuildLinear(Dictionary<string, object> spec, ModelBuildContext context)
        {
            var dense = (DenseLayer)DenseLayer.Build(spec, context);
            return new SequentialModel(new ILayer[] { dense }, dense.InputSize);
        }

        public void CheckInputSize(int size)
        {
            if (size != inputSize)
                throw new FormatException($"le modèle attend {inputSize} observations, l'agent en fournit {size}");
        }

        public void Evaluate(float[] input, float[] output)
        {
            float[] current = input;
            for (int i = 0; i < layers.Length; i++)
            {
                layers[i].Forward(current, buffers[i]);
                current = buffers[i];
            }
            Array.Copy(current, output, OutputSize);
        }

        public string Describe()
        {
            var parts = new string[layers.Length];
            for (int i = 0; i < layers.Length; i++)
                parts[i] = layers[i].Describe();
            return $"sequential[{string.Join(" → ", parts)}]";
        }
    }

    // ---------------------------------------------------------------------- Arbre de décision

    /// <summary>
    /// Arbre de décision binaire au format scikit-learn : à chaque nœud interne, on va à gauche si
    /// x[feature] &lt;= threshold, sinon à droite. Une feuille (left = -1) renvoie sa ligne de 'value'.
    /// </summary>
    public sealed class DecisionTreeModel : IPolicyModel
    {
        readonly int[] feature;
        readonly float[] threshold;
        readonly int[] left;
        readonly int[] right;
        readonly float[] value;
        readonly int minInputSize;
        readonly int depthLimit;

        public DecisionTreeModel(int[] feature, float[] threshold, int[] left, int[] right, Tensor value)
        {
            int nodes = feature.Length;
            if (nodes == 0 || threshold.Length != nodes || left.Length != nodes || right.Length != nodes)
                throw new FormatException("decision_tree : 'feature', 'threshold', 'left' et 'right' doivent avoir la même taille (≥ 1)");
            if (value.Shape.Length != 2 || value.Shape[0] != nodes)
                throw new FormatException("decision_tree : 'value' doit être de forme [noeuds, sorties]");

            for (int i = 0; i < nodes; i++)
            {
                if (left[i] < 0)
                    continue;
                if (left[i] >= nodes || right[i] < 0 || right[i] >= nodes || feature[i] < 0)
                    throw new FormatException($"decision_tree : nœud {i} invalide");
                minInputSize = Math.Max(minInputSize, feature[i] + 1);
            }

            this.feature = feature;
            this.threshold = threshold;
            this.left = left;
            this.right = right;
            this.value = value.AsFloats();
            OutputSize = value.Shape[1];
            depthLimit = nodes;
        }

        public int OutputSize { get; }

        public static IPolicyModel Build(Dictionary<string, object> spec, ModelBuildContext context) =>
            new DecisionTreeModel(
                context.Require(spec, "feature", "decision_tree").AsInts(),
                context.Require(spec, "threshold", "decision_tree").AsFloats(),
                context.Require(spec, "left", "decision_tree").AsInts(),
                context.Require(spec, "right", "decision_tree").AsInts(),
                context.Require(spec, "value", "decision_tree"));

        public void CheckInputSize(int inputSize)
        {
            if (inputSize < minInputSize)
                throw new FormatException($"l'arbre utilise l'observation n°{minInputSize - 1}, l'agent n'en fournit que {inputSize}");
        }

        public void Evaluate(float[] input, float[] output)
        {
            int node = 0;
            for (int guard = 0; left[node] >= 0 && guard < depthLimit; guard++)
                node = input[feature[node]] <= threshold[node] ? left[node] : right[node];
            Array.Copy(value, node * OutputSize, output, 0, OutputSize);
        }

        public string Describe() => $"decision_tree({feature.Length} nœuds → {OutputSize})";
    }

    // ---------------------------------------------------------------------- Ensemble

    /// <summary>
    /// Combine plusieurs modèles de n'importe quel type : moyenne (forêt aléatoire, bagging)
    /// ou somme pondérée + biais (gradient boosting).
    /// </summary>
    public sealed class EnsembleModel : IPolicyModel
    {
        readonly IPolicyModel[] models;
        readonly float[] weights;
        readonly float[] bias;
        readonly float[] scratch;

        public EnsembleModel(IPolicyModel[] models, float[] weights, float[] bias)
        {
            if (models.Length == 0)
                throw new FormatException("ensemble : au moins un modèle requis");
            OutputSize = models[0].OutputSize;
            foreach (IPolicyModel model in models)
            {
                if (model.OutputSize != OutputSize)
                    throw new FormatException("ensemble : tous les modèles doivent avoir la même taille de sortie");
            }
            if (bias != null && bias.Length != OutputSize)
                throw new FormatException("ensemble : 'bias' de mauvaise taille");

            this.models = models;
            this.weights = weights;
            this.bias = bias;
            scratch = new float[OutputSize];
        }

        public int OutputSize { get; }

        public static IPolicyModel Build(Dictionary<string, object> spec, ModelBuildContext context)
        {
            List<object> specs = spec.Require("models", "ensemble").AsList("models");
            var models = new IPolicyModel[specs.Count];
            for (int i = 0; i < models.Length; i++)
                models[i] = PolicyModelFactory.Build(specs[i], context);

            var weights = new float[models.Length];
            float[] given = context.Optional(spec, "weights", "ensemble")?.AsFloats();
            if (given != null && given.Length != models.Length)
                throw new FormatException("ensemble : 'weights' doit avoir un élément par modèle");

            string aggregate = spec.GetString("aggregate", "mean");
            if (aggregate != "mean" && aggregate != "sum")
                throw new FormatException("ensemble : 'aggregate' doit valoir 'mean' ou 'sum'");
            for (int i = 0; i < weights.Length; i++)
                weights[i] = (given != null ? given[i] : 1f) / (aggregate == "mean" ? models.Length : 1f);

            return new EnsembleModel(models, weights, context.Optional(spec, "bias", "ensemble")?.AsFloats());
        }

        public void CheckInputSize(int inputSize)
        {
            foreach (IPolicyModel model in models)
                model.CheckInputSize(inputSize);
        }

        public void Evaluate(float[] input, float[] output)
        {
            for (int i = 0; i < OutputSize; i++)
                output[i] = bias != null ? bias[i] : 0f;

            for (int m = 0; m < models.Length; m++)
            {
                models[m].Evaluate(input, scratch);
                for (int i = 0; i < OutputSize; i++)
                    output[i] += weights[m] * scratch[i];
            }
        }

        public string Describe() => $"ensemble({models.Length} × {models[0].Describe()})";
    }

    // ---------------------------------------------------------------------- Divers

    /// <summary>Renvoie toujours la même sortie (tests, politique par défaut).</summary>
    public sealed class ConstantModel : IPolicyModel
    {
        readonly float[] value;

        public ConstantModel(float[] value) => this.value = value;

        public int OutputSize => value.Length;

        public static IPolicyModel Build(Dictionary<string, object> spec, ModelBuildContext context) =>
            new ConstantModel(context.Require(spec, "value", "constant").AsFloats());

        public void CheckInputSize(int inputSize) { }

        public void Evaluate(float[] input, float[] output) => Array.Copy(value, output, value.Length);

        public string Describe() => $"constant({value.Length})";
    }

    /// <summary>Applique (x - mean) / std aux observations avant de les passer au modèle.</summary>
    public sealed class NormalizedModel : IPolicyModel
    {
        readonly IPolicyModel inner;
        readonly float[] mean;
        readonly float[] std;
        readonly float clip;
        readonly float[] scratch;

        public NormalizedModel(IPolicyModel inner, float[] mean, float[] std, float clip)
        {
            if (mean.Length != std.Length)
                throw new FormatException("input_norm : 'mean' et 'std' de tailles différentes");
            this.inner = inner;
            this.mean = mean;
            this.std = std;
            this.clip = clip;
            scratch = new float[mean.Length];
        }

        public int OutputSize => inner.OutputSize;

        public void CheckInputSize(int inputSize)
        {
            if (inputSize != mean.Length)
                throw new FormatException($"input_norm prévu pour {mean.Length} observations, l'agent en fournit {inputSize}");
            inner.CheckInputSize(inputSize);
        }

        public void Evaluate(float[] input, float[] output)
        {
            for (int i = 0; i < scratch.Length; i++)
            {
                float normalized = (input[i] - mean[i]) / MathF.Max(std[i], 1e-6f);
                scratch[i] = MathF.Max(-clip, MathF.Min(clip, normalized));
            }
            inner.Evaluate(scratch, output);
        }

        public string Describe() => $"normalize → {inner.Describe()}";
    }
}
