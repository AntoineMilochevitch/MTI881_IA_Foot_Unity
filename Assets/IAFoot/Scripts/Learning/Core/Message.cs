using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IAFoot.Learning
{
    /// <summary>Tableau numérique à N dimensions (float32 ou int32), stocké à plat ligne par ligne.</summary>
    public sealed class Tensor
    {
        public readonly int[] Shape;
        public readonly float[] Floats;
        public readonly int[] Ints;

        public Tensor(float[] data, params int[] shape)
        {
            Floats = data;
            Shape = CheckShape(shape, data.Length);
        }

        public Tensor(int[] data, params int[] shape)
        {
            Ints = data;
            Shape = CheckShape(shape, data.Length);
        }

        public bool IsFloat => Floats != null;
        public int Count => IsFloat ? Floats.Length : Ints.Length;
        public string DType => IsFloat ? "f32" : "i32";

        public float[] AsFloats()
        {
            if (IsFloat)
                return Floats;
            var result = new float[Ints.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = Ints[i];
            return result;
        }

        public int[] AsInts()
        {
            if (!IsFloat)
                return Ints;
            var result = new int[Floats.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = (int)Math.Round(Floats[i]);
            return result;
        }

        static int[] CheckShape(int[] shape, int count)
        {
            if (shape == null || shape.Length == 0)
                return new[] { count };

            long total = 1;
            foreach (int dim in shape)
                total *= dim;
            if (total != count)
                throw new ArgumentException($"Forme [{string.Join(",", shape)}] incompatible avec {count} éléments");
            return shape;
        }
    }

    /// <summary>
    /// Message échangé entre Unity et Python, dans les deux sens.
    /// Trame : "IAFT" | uint32 taille de l'en-tête | uint32 taille des données | en-tête JSON UTF-8 | données binaires.
    /// L'en-tête contient un champ "type" et un champ "tensors" qui décrit chaque tenseur du bloc binaire
    /// (dtype, shape, offset, nbytes). Tout est en little-endian.
    /// </summary>
    public sealed class Message
    {
        const int MaxSectionBytes = 1 << 30;
        static readonly byte[] Magic = { (byte)'I', (byte)'A', (byte)'F', (byte)'T' };

        public readonly Dictionary<string, object> Header;
        public readonly Dictionary<string, Tensor> Tensors = new Dictionary<string, Tensor>();

        public Message(string type)
        {
            Header = new Dictionary<string, object> { ["type"] = type };
        }

        Message(Dictionary<string, object> header)
        {
            Header = header;
        }

        public string Type => Header.GetString("type", string.Empty);

        public byte[] Encode()
        {
            var header = new Dictionary<string, object>(Header);
            var descriptors = new Dictionary<string, object>();
            int payloadLength = 0;
            foreach (KeyValuePair<string, Tensor> pair in Tensors)
            {
                int nbytes = pair.Value.Count * 4;
                descriptors[pair.Key] = new Dictionary<string, object>
                {
                    ["dtype"] = pair.Value.DType,
                    ["shape"] = pair.Value.Shape,
                    ["offset"] = payloadLength,
                    ["nbytes"] = nbytes,
                };
                payloadLength += nbytes;
            }
            header["tensors"] = descriptors;

            byte[] headerBytes = Encoding.UTF8.GetBytes(Json.Serialize(header));
            var frame = new byte[12 + headerBytes.Length + payloadLength];
            Buffer.BlockCopy(Magic, 0, frame, 0, 4);
            WriteUInt32(frame, 4, (uint)headerBytes.Length);
            WriteUInt32(frame, 8, (uint)payloadLength);
            Buffer.BlockCopy(headerBytes, 0, frame, 12, headerBytes.Length);

            int offset = 12 + headerBytes.Length;
            foreach (Tensor tensor in Tensors.Values)
            {
                int nbytes = tensor.Count * 4;
                Buffer.BlockCopy(tensor.IsFloat ? (Array)tensor.Floats : tensor.Ints, 0, frame, offset, nbytes);
                offset += nbytes;
            }
            return frame;
        }

        /// <summary>Lit un message complet sur un flux (bloquant). Lève EndOfStreamException si le flux se ferme.</summary>
        public static Message Read(Stream stream)
        {
            var prefix = new byte[12];
            ReadExactly(stream, prefix, 12);
            for (int i = 0; i < 4; i++)
            {
                if (prefix[i] != Magic[i])
                    throw new InvalidDataException("Trame invalide : signature 'IAFT' absente");
            }

            uint headerLength = ReadUInt32(prefix, 4);
            uint payloadLength = ReadUInt32(prefix, 8);
            if (headerLength > MaxSectionBytes || payloadLength > MaxSectionBytes)
                throw new InvalidDataException("Trame invalide : taille démesurée");

            var headerBytes = new byte[headerLength];
            ReadExactly(stream, headerBytes, (int)headerLength);
            var payload = new byte[payloadLength];
            ReadExactly(stream, payload, (int)payloadLength);
            return Decode(headerBytes, payload);
        }

        /// <summary>Décode une trame complète (ex : modèle sauvegardé dans un fichier .bytes).</summary>
        public static Message FromBytes(byte[] frame)
        {
            using (var stream = new MemoryStream(frame, false))
                return Read(stream);
        }

        static Message Decode(byte[] headerBytes, byte[] payload)
        {
            Dictionary<string, object> header = Json.Parse(Encoding.UTF8.GetString(headerBytes)).AsObject("en-tête");
            var message = new Message(header);
            if (!header.Has("tensors"))
                return message;

            foreach (KeyValuePair<string, object> pair in header["tensors"].AsObject("tensors"))
            {
                Dictionary<string, object> descriptor = pair.Value.AsObject($"tensors.{pair.Key}");
                List<object> shapeList = descriptor.Require("shape", pair.Key).AsList("shape");
                var shape = new int[shapeList.Count];
                long count = 1;
                for (int i = 0; i < shape.Length; i++)
                {
                    shape[i] = (int)Math.Round((double)shapeList[i]);
                    count *= shape[i];
                }

                int offset = descriptor.GetInt("offset");
                if (offset < 0 || count < 0 || offset + count * 4 > payload.Length)
                    throw new InvalidDataException($"Tenseur '{pair.Key}' hors du bloc de données");

                string dtype = descriptor.GetString("dtype", "f32");
                if (dtype == "f32")
                {
                    var data = new float[count];
                    Buffer.BlockCopy(payload, offset, data, 0, (int)count * 4);
                    message.Tensors[pair.Key] = new Tensor(data, shape);
                }
                else if (dtype == "i32")
                {
                    var data = new int[count];
                    Buffer.BlockCopy(payload, offset, data, 0, (int)count * 4);
                    message.Tensors[pair.Key] = new Tensor(data, shape);
                }
                else
                {
                    throw new InvalidDataException($"Tenseur '{pair.Key}' : dtype '{dtype}' non supporté (f32 ou i32)");
                }
            }
            return message;
        }

        static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                    throw new EndOfStreamException("Connexion fermée par le serveur");
                read += n;
            }
        }

        static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        static uint ReadUInt32(byte[] buffer, int offset) =>
            (uint)(buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16 | buffer[offset + 3] << 24);
    }
}
