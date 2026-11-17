using System;
using System.Collections.Generic;
using System.IO;
using IAFoot.Learning;
using UnityEditor;
using UnityEngine;

namespace IAFoot.Editor
{
    /// <summary>Régression log(1-p) sur les boutons rares, sans lancer une scène.</summary>
    public static class DecoderParityChecks
    {
        sealed class RareOffRandom : System.Random
        {
            public override double NextDouble() => 0.999999;
        }

        public static void RunBatch()
        {
            try
            {
                var context = new ModelBuildContext(new Dictionary<string, Tensor>());
                var actionSpec = new ActionSpec(new ActionDim("jump", true));
                var spec = new Dictionary<string, object> { ["type"] = "direct_logits" };
                IActionDecoder decoder = ActionDecoderFactory.Build(spec, context, actionSpec);
                var action = new float[1];
                decoder.Decode(new[] { 11.35186672f }, new RareOffRandom(), action, out float lp, out _);
                if (action[0] != 0f || Math.Abs(lp - (-11.35187847)) > 2e-6)
                    throw new Exception($"Bouton rare incorrect : a={action[0]}, logp={lp}");
                if (DirectDecoder.BinaryLogProbability(100f, false) != -100f
                    || DirectDecoder.BinaryLogProbability(-100f, true) != -100f)
                    throw new Exception("Les événements rares doivent conserver leur log-probabilité sans plancher.");

                // Les anciens clients gardent leur convention direct.
                spec["type"] = "direct";
                decoder = ActionDecoderFactory.Build(spec, context, actionSpec);
                decoder.Decode(new[] { 11.35186672f }, new RareOffRandom(), action, out lp, out _);
                float p = Activations.Sigmoid(11.35186672f);
                if (lp != MathF.Log(MathF.Max(1f - p, 1e-8f)))
                    throw new Exception("Régression du décodeur direct historique.");

                // Facultatif : rejouer les observations/actions d'un parity_failure.pt,
                // exportées en messages IAFT avec expected_cpu et expected_cuda.
                string directory = Environment.GetEnvironmentVariable("IAFOOT_PARITY_FIXTURES");
                if (!string.IsNullOrEmpty(directory))
                    foreach (string path in Directory.GetFiles(directory, "*.bytes"))
                        CheckFixture(path);
                Debug.Log("DECODER_PARITY_CHECKS PASS");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogException(error);
                EditorApplication.Exit(1);
            }
        }

        static void CheckFixture(string path)
        {
            Message message = Message.FromBytes(File.ReadAllBytes(path));
            var actions = new ActionSpec(new ActionDim("move_x", false), new ActionDim("move_z", false),
                new ActionDim("shoot", true), new ActionDim("power", false), new ActionDim("curve", false),
                new ActionDim("tackle", true), new ActionDim("jump", true));
            int size = message.Tensors["obs"].Shape[1];
            int count = message.Tensors["obs"].Shape[0];
            Policy policy = Policy.FromMessage(message, actions, size);
            float[] obs = message.Tensors["obs"].AsFloats();
            float[] samples = message.Tensors["actions"].AsFloats();
            float[] logStd = message.Tensors["test_log_std"].AsFloats();
            float[] expectedCpu = message.Tensors["expected_cpu"].AsFloats();
            float[] expectedCuda = message.Tensors["expected_cuda"].AsFloats();
            var input = new float[size];
            double maxError = 0;
            for (int row = 0; row < count; row++)
            {
                Array.Copy(obs, row * size, input, 0, size);
                float[] raw = policy.EvaluateModel(input);
                double lp = 0;
                for (int col = 0; col < 7; col++)
                {
                    float a = samples[row * 7 + col];
                    if (actions.Dims[col].IsBinary)
                        lp += DirectDecoder.BinaryLogProbability(raw[col], a == 1f);
                    else
                    {
                        double noise = (a - (double)raw[col]) / Math.Exp(logStd[col]);
                        lp += -0.5 * noise * noise - logStd[col] - 0.9189385332046727;
                    }
                }
                maxError = Math.Max(maxError, Math.Abs(lp - expectedCpu[row]));
                maxError = Math.Max(maxError, Math.Abs(lp - expectedCuda[row]));
            }
            Debug.Log($"PARITY_REPLAY {Path.GetFileName(path)}: {count} transitions, max CPU/CUDA error={maxError:G9}");
            if (maxError > 0.0001)
                throw new Exception($"Écart après correction trop élevé : {maxError}");
        }
    }
}
