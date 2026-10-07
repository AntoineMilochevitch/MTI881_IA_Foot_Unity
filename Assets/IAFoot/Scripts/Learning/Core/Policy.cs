using System;
using System.Collections.Generic;

namespace IAFoot.Learning
{
    public readonly struct ActionDim
    {
        public readonly string Name;
        /// <summary>Vrai : bouton (0 ou 1). Faux : valeur continue, utile dans [-1, 1].</summary>
        public readonly bool IsBinary;

        public ActionDim(string name, bool isBinary)
        {
            Name = name;
            IsBinary = isBinary;
        }
    }

    /// <summary>Description du vecteur d'actions d'un agent.</summary>
    public sealed class ActionSpec
    {
        public readonly ActionDim[] Dims;

        public ActionSpec(params ActionDim[] dims) => Dims = dims;

        public int Count => Dims.Length;
    }

    /// <summary>
    /// Transforme la sortie brute d'un modèle en vecteur d'actions (avec ou sans exploration).
    /// Séparé du modèle pour qu'un même type de modèle serve à plusieurs algorithmes :
    /// "direct" pour les méthodes à gradient de politique, "discrete" pour le Q-learning ou les classifieurs.
    /// </summary>
    public interface IActionDecoder
    {
        /// <summary>Taille de sortie que le modèle doit avoir.</summary>
        int ModelOutputSize { get; }

        /// <param name="logProbability">Log-probabilité de l'action choisie (0 en mode déterministe).</param>
        /// <param name="index">Indice de l'action discrète choisie, ou -1.</param>
        void Decode(float[] modelOutput, Random random, float[] action, out float logProbability, out int index);

        string Describe();
    }

    public static class ActionDecoderFactory
    {
        public delegate IActionDecoder Builder(Dictionary<string, object> spec, ModelBuildContext context, ActionSpec actions);

        static readonly Dictionary<string, Builder> Builders = new Dictionary<string, Builder>
        {
            ["direct"] = DirectDecoder.Build,
            ["discrete"] = DiscreteDecoder.Build,
        };

        public static void Register(string type, Builder builder) => Builders[type] = builder;

        /// <summary>Sans description de décodeur : décodeur "direct" avec ses réglages par défaut.</summary>
        public static IActionDecoder Build(object specObject, ModelBuildContext context, ActionSpec actions)
        {
            Dictionary<string, object> spec = specObject != null
                ? specObject.AsObject("decoder")
                : new Dictionary<string, object>();
            string type = spec.GetString("type", "direct");
            if (!Builders.TryGetValue(type, out Builder builder))
                throw new NotSupportedException($"Type de décodeur inconnu : '{type}' (connus : {string.Join(", ", Builders.Keys)})");
            return builder(spec, context, actions);
        }
    }

    /// <summary>
    /// Le modèle sort une valeur par dimension d'action.
    /// Dimension continue : moyenne d'une gaussienne d'écart-type exp(log_std).
    /// Dimension binaire : logit d'une loi de Bernoulli (probabilité d'appuyer = sigmoïde).
    /// </summary>
    public sealed class DirectDecoder : IActionDecoder
    {
        const float LogSqrtTwoPi = 0.9189385f;

        readonly ActionSpec actions;
        readonly float[] logStd;
        readonly bool deterministic;

        public DirectDecoder(ActionSpec actions, float[] logStd, bool deterministic)
        {
            this.actions = actions;
            this.deterministic = deterministic;
            this.logStd = new float[actions.Count];
            if (logStd.Length != 1 && logStd.Length != actions.Count)
                throw new FormatException($"decoder direct : 'log_std' doit avoir 1 ou {actions.Count} éléments");
            for (int i = 0; i < actions.Count; i++)
                this.logStd[i] = logStd.Length == 1 ? logStd[0] : logStd[i];
        }

        public int ModelOutputSize => actions.Count;

        public static IActionDecoder Build(Dictionary<string, object> spec, ModelBuildContext context, ActionSpec actions)
        {
            float[] logStd = context.Optional(spec, "log_std", "decoder")?.AsFloats() ?? new[] { -0.7f };
            return new DirectDecoder(actions, logStd, spec.GetBool("deterministic"));
        }

        public void Decode(float[] modelOutput, Random random, float[] action, out float logProbability, out int index)
        {
            index = -1;
            logProbability = 0f;
            for (int i = 0; i < actions.Count; i++)
            {
                float raw = modelOutput[i];
                if (actions.Dims[i].IsBinary)
                {
                    float p = Activations.Sigmoid(raw);
                    if (deterministic)
                    {
                        action[i] = raw > 0f ? 1f : 0f;
                        continue;
                    }
                    bool pressed = random.NextDouble() < p;
                    action[i] = pressed ? 1f : 0f;
                    logProbability += MathF.Log(MathF.Max(pressed ? p : 1f - p, 1e-8f));
                }
                else
                {
                    if (deterministic)
                    {
                        action[i] = raw;
                        continue;
                    }
                    float noise = NextGaussian(random);
                    action[i] = raw + MathF.Exp(logStd[i]) * noise;
                    logProbability += -0.5f * noise * noise - logStd[i] - LogSqrtTwoPi;
                }
            }
        }

        public string Describe() => deterministic ? "direct (déterministe)" : "direct (gaussienne + Bernoulli)";

        static float NextGaussian(Random random)
        {
            // Box-Muller
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }

    /// <summary>
    /// Le modèle sort un score par action d'une table d'actions prédéfinies (une ligne = un vecteur d'actions complet).
    /// Convient au Q-learning (scores = Q-valeurs), aux classifieurs et aux arbres.
    /// Modes : "argmax", "epsilon_greedy" ou "softmax" (tirage selon softmax(scores / temperature)).
    /// </summary>
    public sealed class DiscreteDecoder : IActionDecoder
    {
        readonly float[] table;
        readonly int actionSize;
        readonly int actionCount;
        readonly string mode;
        readonly float epsilon;
        readonly float temperature;
        readonly float[] probabilities;

        public DiscreteDecoder(Tensor table, int actionSize, string mode, float epsilon, float temperature)
        {
            if (table.Shape.Length != 2 || table.Shape[1] != actionSize || table.Shape[0] == 0)
                throw new FormatException($"decoder discrete : 'actions' doit être de forme [N, {actionSize}]");
            if (mode != "argmax" && mode != "epsilon_greedy" && mode != "softmax")
                throw new FormatException("decoder discrete : 'mode' doit valoir 'argmax', 'epsilon_greedy' ou 'softmax'");

            this.table = table.AsFloats();
            this.actionSize = actionSize;
            actionCount = table.Shape[0];
            this.mode = mode;
            this.epsilon = Math.Min(1f, Math.Max(0f, epsilon));
            this.temperature = Math.Max(1e-4f, temperature);
            probabilities = new float[actionCount];
        }

        public int ModelOutputSize => actionCount;

        public static IActionDecoder Build(Dictionary<string, object> spec, ModelBuildContext context, ActionSpec actions) =>
            new DiscreteDecoder(
                context.Require(spec, "actions", "decoder"),
                actions.Count,
                spec.GetString("mode", "argmax"),
                spec.GetFloat("epsilon", 0.1f),
                spec.GetFloat("temperature", 1f));

        public void Decode(float[] modelOutput, Random random, float[] action, out float logProbability, out int index)
        {
            int best = 0;
            for (int i = 1; i < actionCount; i++)
            {
                if (modelOutput[i] > modelOutput[best])
                    best = i;
            }

            switch (mode)
            {
                case "softmax":
                {
                    for (int i = 0; i < actionCount; i++)
                        probabilities[i] = modelOutput[i] / temperature;
                    Activations.Softmax(probabilities);

                    double draw = random.NextDouble();
                    index = actionCount - 1;
                    double cumulative = 0.0;
                    for (int i = 0; i < actionCount; i++)
                    {
                        cumulative += probabilities[i];
                        if (draw < cumulative)
                        {
                            index = i;
                            break;
                        }
                    }
                    logProbability = MathF.Log(MathF.Max(probabilities[index], 1e-8f));
                    break;
                }
                case "epsilon_greedy":
                {
                    index = random.NextDouble() < epsilon ? random.Next(actionCount) : best;
                    float probability = epsilon / actionCount + (index == best ? 1f - epsilon : 0f);
                    logProbability = MathF.Log(MathF.Max(probability, 1e-8f));
                    break;
                }
                default:
                    index = best;
                    logProbability = 0f;
                    break;
            }

            Array.Copy(table, index * actionSize, action, 0, actionSize);
        }

        public string Describe() => $"discrete ({actionCount} actions, {mode})";
    }

    /// <summary>Un modèle + un décodeur + un numéro de version : ce qui pilote réellement un agent.</summary>
    public sealed class Policy
    {
        public readonly int Version;
        public readonly IPolicyModel Model;
        public readonly IActionDecoder Decoder;

        readonly float[] modelOutput;

        public Policy(int version, IPolicyModel model, IActionDecoder decoder)
        {
            if (model.OutputSize != decoder.ModelOutputSize)
                throw new FormatException($"le modèle sort {model.OutputSize} valeurs alors que le décodeur en attend {decoder.ModelOutputSize}");
            Version = version;
            Model = model;
            Decoder = decoder;
            modelOutput = new float[model.OutputSize];
        }

        /// <summary>Politique par défaut tant qu'aucun modèle n'a été reçu : exploration aléatoire (version 0).</summary>
        public static Policy CreateRandom(ActionSpec actions)
        {
            // Boutons : logit de -2, soit ≈ 12 % de chances d'appuyer à chaque décision.
            var output = new float[actions.Count];
            for (int i = 0; i < output.Length; i++)
                output[i] = actions.Dims[i].IsBinary ? -2f : 0f;
            return new Policy(0, new ConstantModel(output), new DirectDecoder(actions, new[] { 0f }, false));
        }

        /// <summary>Construit une politique à partir d'un message "set_model".</summary>
        public static Policy FromMessage(Message message, ActionSpec actions, int observationSize)
        {
            var context = new ModelBuildContext(message.Tensors);
            IPolicyModel model = PolicyModelFactory.Build(message.Header.Require("model", "set_model"), context);
            model.CheckInputSize(observationSize);

            message.Header.TryGetValue("decoder", out object decoderSpec);
            IActionDecoder decoder = ActionDecoderFactory.Build(decoderSpec, context, actions);
            return new Policy(message.Header.GetInt("version", 1), model, decoder);
        }

        /// <summary>
        /// Vérifie que le modèle tourne et, si Python a joint un exemple {"check": {"input", "output"}},
        /// que la sortie calculée en C# correspond bien à celle calculée en Python. Lève une exception sinon.
        /// </summary>
        public void Verify(Message message, int observationSize)
        {
            foreach (float value in EvaluateModel(new float[observationSize]))
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new FormatException("le modèle produit des valeurs non finies (NaN / infini)");
            }

            if (!message.Header.Has("check"))
                return;

            Dictionary<string, object> check = message.Header["check"].AsObject("check");
            var context = new ModelBuildContext(message.Tensors);
            float[] input = context.Require(check, "input", "check").AsFloats();
            float[] expected = context.Require(check, "output", "check").AsFloats();
            if (input.Length != observationSize || expected.Length != Model.OutputSize)
                throw new FormatException("l'exemple de vérification 'check' n'a pas les bonnes tailles");

            float tolerance = check.GetFloat("tolerance", 1e-3f);
            float[] actual = EvaluateModel(input);
            for (int i = 0; i < expected.Length; i++)
            {
                if (MathF.Abs(actual[i] - expected[i]) > tolerance * MathF.Max(1f, MathF.Abs(expected[i])))
                    throw new FormatException($"sortie différente de celle calculée par Python (sortie {i} : {actual[i]} au lieu de {expected[i]})");
            }
        }

        /// <summary>Sortie brute du modèle (tableau interne, réutilisé à chaque appel).</summary>
        public float[] EvaluateModel(float[] observation)
        {
            Model.Evaluate(observation, modelOutput);
            return modelOutput;
        }

        public void Act(float[] observation, Random random, float[] action, out float logProbability, out int index)
        {
            Model.Evaluate(observation, modelOutput);
            Decoder.Decode(modelOutput, random, action, out logProbability, out index);
        }

        public string Describe() => $"v{Version} : {Model.Describe()} + {Decoder.Describe()}";
    }
}
