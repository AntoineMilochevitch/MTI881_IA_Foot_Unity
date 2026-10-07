using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace IAFoot.Learning
{
    /// <summary>
    /// Connexion TCP vers le back-end Python, gérée dans des threads d'arrière-plan pour ne jamais bloquer le jeu.
    /// Se reconnecte toute seule si le serveur n'est pas encore lancé ou redémarre.
    /// Les messages reçus et les journaux sont déposés dans des files à vider depuis le thread principal.
    /// </summary>
    public sealed class TrainingClient : IDisposable
    {
        public readonly ConcurrentQueue<Message> Incoming = new ConcurrentQueue<Message>();
        public readonly ConcurrentQueue<string> Logs = new ConcurrentQueue<string>();

        readonly string host;
        readonly int port;
        readonly int retryMilliseconds;
        readonly BlockingCollection<byte[]> outgoing = new BlockingCollection<byte[]>(new ConcurrentQueue<byte[]>());
        readonly object gate = new object();

        Thread thread;
        TcpClient current;
        volatile bool connected;
        volatile bool stopping;
        int connectionCount;
        bool failureLogged;

        public TrainingClient(string host, int port, float retrySeconds = 2f)
        {
            this.host = host;
            this.port = port;
            retryMilliseconds = Math.Max(100, (int)(retrySeconds * 1000f));
        }

        public bool IsConnected => connected;

        /// <summary>Augmente de 1 à chaque (re)connexion : permet de savoir qu'il faut renvoyer le "hello".</summary>
        public int ConnectionCount => Volatile.Read(ref connectionCount);

        public void Start()
        {
            if (thread != null)
                return;
            thread = new Thread(Run) { IsBackground = true, Name = "IAFoot.TrainingClient" };
            thread.Start();
        }

        /// <summary>Met un message en file d'envoi. Retourne faux (message ignoré) si la connexion est coupée.</summary>
        public bool Send(Message message) => Send(message, out _);

        /// <param name="frameBytes">Taille de la trame envoyée, en octets (0 si le message est ignoré).</param>
        public bool Send(Message message, out int frameBytes)
        {
            frameBytes = 0;
            if (!connected)
                return false;
            byte[] frame = message.Encode();
            frameBytes = frame.Length;
            outgoing.Add(frame);
            return true;
        }

        public void Dispose()
        {
            // Laisse une courte chance aux derniers messages (ex : dernier batch) de partir.
            for (int i = 0; i < 20 && connected && outgoing.Count > 0; i++)
                Thread.Sleep(25);

            stopping = true;
            lock (gate)
                current?.Close();
            thread?.Join(1000);
            thread = null;
        }

        void Run()
        {
            while (!stopping)
            {
                TcpClient client = null;
                Thread writer = null;
                try
                {
                    client = new TcpClient { NoDelay = true };
                    lock (gate)
                        current = client;
                    client.Connect(host, port);

                    while (outgoing.TryTake(out _)) { } // restes d'une connexion précédente
                    NetworkStream stream = client.GetStream();
                    Interlocked.Increment(ref connectionCount);
                    connected = true;
                    failureLogged = false;
                    Logs.Enqueue($"Connecté au back-end Python ({host}:{port}).");

                    writer = new Thread(() => WriteLoop(stream, client)) { IsBackground = true, Name = "IAFoot.TrainingClient.Write" };
                    writer.Start();

                    while (!stopping)
                        Incoming.Enqueue(Message.Read(stream));
                }
                catch (Exception e)
                {
                    if (stopping)
                        break;
                    if (connected)
                        Logs.Enqueue($"Connexion au back-end Python perdue : {e.Message}");
                    else if (!failureLogged)
                    {
                        failureLogged = true;
                        Logs.Enqueue($"Back-end Python injoignable sur {host}:{port} (lancez `python train.py`). " +
                                     $"Nouvelle tentative toutes les {retryMilliseconds / 1000f:0.#} s.");
                    }
                }
                finally
                {
                    connected = false;
                    try { client?.Close(); } catch { /* déjà fermé */ }
                    lock (gate)
                        current = null;
                    writer?.Join(1000);
                }

                for (int waited = 0; waited < retryMilliseconds && !stopping; waited += 50)
                    Thread.Sleep(50);
            }
        }

        void WriteLoop(NetworkStream stream, TcpClient client)
        {
            try
            {
                while (!stopping && connected)
                {
                    if (outgoing.TryTake(out byte[] frame, 100))
                        stream.Write(frame, 0, frame.Length);
                }
            }
            catch (Exception)
            {
                // La boucle de lecture détectera la coupure et relancera la connexion.
                try { client.Close(); } catch { /* déjà fermé */ }
            }
        }
    }
}
