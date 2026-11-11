using System;
using System.Collections.Generic;
using UnityEngine;

namespace IAFoot
{
    public enum RoundSpawnMode
    {
        Free = 0,
        AroundBall = 1,
    }

    /// <summary>Tire un placement complet avant de déplacer le moindre objet.</summary>
    [Serializable]
    public sealed class RoundSpawnRandomizer
    {
        [Tooltip("Nouvelles positions à chaque manche. Désactiver pour utiliser les points d'apparition fixes.")]
        public bool enabled = true;
        [Tooltip("Centre X/Z de la zone, dans le repère local de l'arène.")]
        public Vector2 areaCenter = Vector2.zero;
        [Tooltip("Dimensions X/Z de la zone. La taille des objets est déduite pour les garder à l'intérieur.")]
        public Vector2 areaSize = new Vector2(2.6f, 1.4f);
        [Tooltip("Distance horizontale minimale entre les centres des joueurs et du ballon, en unités monde.")]
        [Min(0f)] public float minimumDistance = 0.4f;
        [Tooltip("Marge entre les empreintes des objets, en unités monde.")]
        [Min(0f)] public float clearance = 0.05f;
        [Tooltip("0 : nouvelle séquence à chaque lancement. Une valeur positive reproduit la séquence des placements.")]
        [Min(0)] public int seed;

        [Header("Placement autour du ballon")]
        [Tooltip("Free : positions indépendantes. Around Ball : distances liées au but défendu et bleu à gauche du rouge.")]
        public RoundSpawnMode mode = RoundSpawnMode.Free;
        [Tooltip("Intervalle joueur-ballon près du but défendu, en unités monde. Minimum Distance reste prioritaire.")]
        public Vector2 nearGoalDistance = new Vector2(0.4f, 0.55f);
        [Tooltip("Intervalle joueur-ballon loin du but défendu, en unités monde.")]
        public Vector2 farGoalDistance = new Vector2(0.55f, 0.8f);
        [Tooltip("Angle autour du ballon en degrés : 0 vers son camp, +/-180 vers le camp adverse. [-180,180] autorise tout le cercle.")]
        public Vector2 angleDegrees = new Vector2(-180f, 180f);

        [NonSerialized] System.Random random;
        [NonSerialized] int currentSeed;

        sealed class Body
        {
            public Transform root;
            public Collider[] colliders;
            public PlayerController player;
            public float radius;
        }

        /// <summary>
        /// Appelé après le placement fixe, avant RoundStarted et PlayStarted.
        /// En cas d'échec, tous les objets restent aux points fixes : aucun placement partiel.
        /// </summary>
        public bool TryPlace(MatchManager match)
        {
            if (!enabled || !Valid(areaCenter.x) || !Valid(areaCenter.y)
                || !Valid(areaSize.x) || !Valid(areaSize.y) || areaSize.x <= 0f || areaSize.y <= 0f
                || !Valid(minimumDistance) || minimumDistance < 0f
                || !Valid(clearance) || clearance < 0f)
                return false;
            if (mode != RoundSpawnMode.Free && mode != RoundSpawnMode.AroundBall)
                return false;
            if (mode == RoundSpawnMode.AroundBall &&
                (!match.ball || !match.ball.isActiveAndEnabled || !ValidInterval(nearGoalDistance)
                 || !ValidInterval(farGoalDistance) || nearGoalDistance.x <= 0f
                 || farGoalDistance.x < nearGoalDistance.x || farGoalDistance.y < nearGoalDistance.y
                 || !ValidInterval(angleDegrees) || angleDegrees.y - angleDegrees.x > 360f))
                return false;

            Transform arena = match.transform;
            Vector3 up = arena.up;
            float scaleX = arena.TransformVector(Vector3.right).magnitude;
            float scaleZ = arena.TransformVector(Vector3.forward).magnitude;
            if (scaleX < 1e-5f || scaleZ < 1e-5f)
                return false;

            Physics.SyncTransforms();
            var bodies = new List<Body>();
            var movingColliders = new HashSet<Collider>();
            if (match.ball && match.ball.isActiveAndEnabled)
                AddBody(match.ball.transform, null, up, bodies, movingColliders);
            foreach (PlayerController player in match.AllPlayers)
                if (player && player.isActiveAndEnabled)
                    AddBody(player.transform, player, up, bodies, movingColliders);

            if (random == null || currentSeed != seed)
            {
                currentSeed = seed;
                // RNG indépendant des tirs du bot et de l'échantillonnage des politiques.
                random = new System.Random(seed == 0 ? Guid.NewGuid().GetHashCode() : seed);
            }

            var positions = new Vector3[bodies.Count];
            // Un terrain trop petit ou encombré ne doit jamais bloquer la simulation.
            for (int layoutAttempt = 0; layoutAttempt < 8; layoutAttempt++)
            {
                bool complete = true;
                for (int i = 0; i < bodies.Count; i++)
                {
                    // Le ballon (index 0) garde le même tirage sur toute la zone.
                    // Tous les joueurs sont ensuite proposés ensemble : aucun côté
                    // n'est placé en premier au détriment de l'autre.
                    if (mode == RoundSpawnMode.AroundBall && i == 1)
                    {
                        complete = TryPlayersAroundBall(match, positions, bodies, movingColliders, scaleX, scaleZ);
                        break;
                    }
                    Body body = bodies[i];
                    float halfX = areaSize.x * 0.5f - (body.radius + clearance) / scaleX;
                    float halfZ = areaSize.y * 0.5f - (body.radius + clearance) / scaleZ;
                    if (halfX <= 0f || halfZ <= 0f || body.colliders.Length == 0)
                        return false;

                    float height = arena.InverseTransformPoint(body.root.position).y;
                    bool found = false;
                    for (int attempt = 0; attempt < 128; attempt++)
                    {
                        Vector3 candidate = arena.TransformPoint(new Vector3(
                            areaCenter.x + SignedSample() * halfX,
                            height,
                            areaCenter.y + SignedSample() * halfZ));
                        if (!Separated(candidate, i, positions, bodies, up)
                            || !HasGround(candidate, up, movingColliders)
                            || !ClearOfObstacles(body, candidate, movingColliders))
                            continue;
                        positions[i] = candidate;
                        found = true;
                        break;
                    }
                    if (!found)
                    {
                        complete = false;
                        break;
                    }
                }
                if (!complete)
                    continue;

                for (int i = 0; i < bodies.Count; i++)
                {
                    Body body = bodies[i];
                    if (body.player)
                        body.player.ResetState(positions[i], match.GetAttackRotation(body.player.Team));
                    else
                        match.ball.ResetBall(positions[i]);
                }
                Physics.SyncTransforms();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Interpole les deux bornes selon la distance horizontale ballon-but défendu,
        /// divisée par la distance entre les buts (bornée à 1). C'est l'intervalle
        /// qui se rapproche du ballon, pas une égalité imposée entre deux tirages.
        /// Les vrais buts de CE match sont utilisés, même sur une arène déplacée.
        /// Sans GoalZone (scène minimale), les deux extrémités de la zone servent de référence.
        /// </summary>
        public Vector2 DistanceRange(MatchManager match, TeamId team, Vector3 ballPosition)
        {
            Vector3 own = GoalPosition(match, team);
            Vector3 other = GoalPosition(match, team.Opponent());
            float length = Vector3.ProjectOnPlane(other - own, match.transform.up).magnitude;
            float distance = Vector3.ProjectOnPlane(ballPosition - own, match.transform.up).magnitude;
            float fraction = Mathf.Clamp01(distance / Mathf.Max(length, 1e-5f));
            return Vector2.Lerp(nearGoalDistance, farGoalDistance, fraction);
        }

        Vector3 GoalPosition(MatchManager match, TeamId team)
        {
            GoalZone goal = match.GetGoalDefendedBy(team);
            return goal ? goal.Center : match.transform.TransformPoint(new Vector3(
                areaCenter.x - team.AttackSign() * areaSize.x * 0.5f, 0f, areaCenter.y));
        }

        bool TryPlayersAroundBall(MatchManager match, Vector3[] positions, List<Body> bodies,
            HashSet<Collider> movingColliders, float scaleX, float scaleZ)
        {
            Transform arena = match.transform;
            Vector3 ballLocal = arena.InverseTransformPoint(positions[0]);
            var ranges = new Vector2[bodies.Count];
            for (int i = 1; i < bodies.Count; i++)
            {
                ranges[i] = DistanceRange(match, bodies[i].player.Team, positions[0]);
                // Une limite physique n'est jamais contournée par une distance tirée trop petite.
                ranges[i].x = Mathf.Max(ranges[i].x, minimumDistance,
                    bodies[0].radius + bodies[i].radius + clearance);
                if (ranges[i].x > ranges[i].y || bodies[i].colliders.Length == 0)
                    return false;
            }

            // Nombre d'essais borné : des réglages impossibles rendent la main au
            // MatchManager, qui conserve le placement fixe et affiche un avertissement.
            for (int attempt = 0; attempt < 256; attempt++)
            {
                bool valid = true;
                float rightmostBlue = float.NegativeInfinity;
                float leftmostRed = float.PositiveInfinity;
                for (int i = 1; i < bodies.Count; i++)
                {
                    Body body = bodies[i];
                    float radius = Sample(ranges[i]);
                    float angle = Sample(angleDegrees) * Mathf.Deg2Rad;
                    // Rotation de 180 degrés pour Red : mêmes règles dans les deux camps.
                    float side = -body.player.Team.AttackSign();
                    Vector3 local = new Vector3(
                        ballLocal.x + side * radius * Mathf.Cos(angle) / scaleX,
                        arena.InverseTransformPoint(body.root.position).y,
                        ballLocal.z + side * radius * Mathf.Sin(angle) / scaleZ);
                    positions[i] = arena.TransformPoint(local);
                    float halfX = areaSize.x * 0.5f - (body.radius + clearance) / scaleX;
                    float halfZ = areaSize.y * 0.5f - (body.radius + clearance) / scaleZ;
                    if (halfX <= 0f || halfZ <= 0f
                        || Mathf.Abs(local.x - areaCenter.x) > halfX
                        || Mathf.Abs(local.z - areaCenter.y) > halfZ
                        || !Separated(positions[i], i, positions, bodies, arena.up))
                    {
                        valid = false;
                        break;
                    }
                    if (body.player.Team == TeamId.Blue)
                        rightmostBlue = Mathf.Max(rightmostBlue, local.x);
                    else
                        leftmostRed = Mathf.Min(leftmostRed, local.x);
                }
                // Le ballon peut être à gauche, entre les joueurs, ou à droite.
                // On rejette une proposition, on ne la rabat jamais sur une bordure.
                if (!valid || rightmostBlue >= leftmostRed)
                    continue;
                for (int i = 1; i < bodies.Count; i++)
                    if (!HasGround(positions[i], arena.up, movingColliders)
                        || !ClearOfObstacles(bodies[i], positions[i], movingColliders))
                    {
                        valid = false;
                        break;
                    }
                if (valid)
                    return true;
            }
            return false;
        }

        float Sample(Vector2 range) => Mathf.Lerp(range.x, range.y, (float)random.NextDouble());
        static bool ValidInterval(Vector2 range) => Valid(range.x) && Valid(range.y) && range.x <= range.y;

        float SignedSample() => (float)(random.NextDouble() * 2.0 - 1.0);
        static bool Valid(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static void AddBody(Transform root, PlayerController player, Vector3 up,
            List<Body> bodies, HashSet<Collider> movingColliders)
        {
            var shapes = new List<Collider>();
            float radius = 0f;
            foreach (Collider shape in root.GetComponentsInChildren<Collider>())
            {
                if (!shape.enabled || shape.isTrigger)
                    continue;
                shapes.Add(shape);
                movingColliders.Add(shape);
                Bounds bounds = shape.bounds;
                // Cercle englobant l'empreinte physique, y compris les colliders décentrés.
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = bounds.center + Vector3.Scale(bounds.extents, new Vector3(
                        (corner & 1) == 0 ? -1f : 1f,
                        (corner & 2) == 0 ? -1f : 1f,
                        (corner & 4) == 0 ? -1f : 1f));
                    radius = Mathf.Max(radius, Vector3.ProjectOnPlane(point - root.position, up).magnitude);
                }
            }
            bodies.Add(new Body { root = root, player = player, colliders = shapes.ToArray(), radius = radius });
        }

        bool Separated(Vector3 candidate, int index, Vector3[] positions, List<Body> bodies, Vector3 up)
        {
            for (int j = 0; j < index; j++)
            {
                float distance = Mathf.Max(minimumDistance, bodies[index].radius + bodies[j].radius + clearance);
                if (Vector3.ProjectOnPlane(candidate - positions[j], up).sqrMagnitude < distance * distance)
                    return false;
            }
            return true;
        }

        static bool HasGround(Vector3 position, Vector3 up, HashSet<Collider> movingColliders)
        {
            foreach (RaycastHit hit in Physics.RaycastAll(position + up * 0.05f, -up, 0.5f,
                ~0, QueryTriggerInteraction.Ignore))
            {
                if (!movingColliders.Contains(hit.collider) && Vector3.Dot(hit.normal, up) > 0.7f)
                    return true;
            }
            return false;
        }

        static bool ClearOfObstacles(Body body, Vector3 position, HashSet<Collider> movingColliders)
        {
            Vector3 offset = position - body.root.position;
            foreach (Collider shape in body.colliders)
            {
                Bounds bounds = shape.bounds;
                foreach (Collider other in Physics.OverlapSphere(bounds.center + offset,
                    bounds.extents.magnitude + 0.01f, ~0, QueryTriggerInteraction.Collide))
                {
                    if (movingColliders.Contains(other))
                        continue;
                    // Les zones de but sont des triggers, mais ne sont pas des points de départ valides.
                    if (other.isTrigger && !other.GetComponentInParent<GoalZone>())
                        continue;
                    if (Physics.ComputePenetration(shape, shape.transform.position + offset, shape.transform.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out float depth)
                        && depth > 1e-5f)
                        return false;
                }
            }
            return true;
        }

        public void DrawGizmo(Transform arena)
        {
            if (!enabled)
                return;
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = arena.localToWorldMatrix;
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(new Vector3(areaCenter.x, 0.08f, areaCenter.y),
                new Vector3(areaSize.x, 0f, areaSize.y));
            Gizmos.matrix = previous;
        }
    }
}
