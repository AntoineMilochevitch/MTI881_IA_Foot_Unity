using System.Collections.Generic;
using UnityEngine;

namespace IAFoot
{
    /// <summary>
    /// Animations procédurales du modèle (squash &amp; stretch, inclinaisons) et marqueur d'équipe au sol.
    /// Seul le visuel bouge : la physique reste sur la racine du joueur.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    public class PlayerVisuals : MonoBehaviour
    {
        [Tooltip("Transform qui porte les meshes. Si vide, un enfant \"Visual\" est créé au lancement et les enfants existants y sont déplacés.")]
        [SerializeField] Transform visualRoot;
        [SerializeField, Min(0f)] float smoothing = 18f;

        [Header("Charge du tir")]
        [SerializeField] float chargeSquash = 0.2f;
        [SerializeField] float chargeLeanBack = 12f;
        [Tooltip("Tremblement quand la charge est au maximum (degrés).")]
        [SerializeField] float fullChargeShake = 4f;

        [Header("Frappe")]
        [SerializeField, Min(0.01f)] float kickDuration = 0.28f;
        [SerializeField] float kickStretch = 0.25f;
        [SerializeField] float kickLeanForward = 25f;

        [Header("Tacle")]
        [Tooltip("Inclinaison pendant la glissade (négatif = vers l'arrière, pieds devant).")]
        [SerializeField] float tackleLean = -55f;
        [SerializeField] float tackleRoll = 10f;
        [SerializeField] float tackleDrop = 0.04f;

        [Header("Saut")]
        [Tooltip("Étirement vertical du modèle quand il est en l'air.")]
        [SerializeField] float airStretch = 0.12f;

        [Header("Étourdi")]
        [SerializeField] float stunWobbleAngle = 18f;
        [SerializeField] float stunWobbleFrequency = 16f;

        [Header("Marqueur d'équipe")]
        [SerializeField] bool showTeamMarker = true;
        [SerializeField, Min(0f)] float markerRadius = 0.13f;
        [Tooltip("Agrandissement du marqueur à charge maximale (jauge de tir).")]
        [SerializeField] float markerChargeGrowth = 0.6f;

        static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        static readonly int ColorId = Shader.PropertyToID("_Color");

        PlayerController player;
        Vector3 basePosition;
        Vector3 baseScale;
        Quaternion baseRotation;
        Vector3 currentEuler;
        Vector3 currentScale = Vector3.one;
        float currentDrop;
        float kickTimer = -1f;
        float kickPower;

        Transform marker;
        Renderer markerRenderer;
        MaterialPropertyBlock markerBlock;
        Color appliedMarkerColor = Color.clear;

        void Awake()
        {
            player = GetComponent<PlayerController>();
            if (!visualRoot)
                visualRoot = CreateVisualRoot();

            basePosition = visualRoot.localPosition;
            baseScale = visualRoot.localScale;
            baseRotation = visualRoot.localRotation;

            if (showTeamMarker)
                CreateMarker();
        }

        void OnEnable() => player.Kicked += OnKicked;

        void OnDisable() => player.Kicked -= OnKicked;

        void OnKicked(PlayerController kicker, float power, bool hitBall)
        {
            kickTimer = 0f;
            kickPower = power;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            float time = Time.time;
            Vector3 euler = Vector3.zero;
            Vector3 scale = Vector3.one;
            float drop = 0f;

            if (player.IsCharging)
            {
                // Le joueur se ramasse et se penche en arrière : on "arme" la frappe.
                float c = player.ChargeAmount;
                scale = new Vector3(1f + chargeSquash * c * 0.5f, 1f - chargeSquash * c, 1f + chargeSquash * c * 0.5f);
                euler.x -= chargeLeanBack * c;
                if (c >= 1f)
                    euler.y += Mathf.Sin(time * 60f) * fullChargeShake;
            }

            if (kickTimer >= 0f)
            {
                // Détente : étirement vertical et bascule vers l'avant.
                float k = Mathf.Sin(kickTimer / kickDuration * Mathf.PI);
                float strength = 0.5f + 0.5f * kickPower;
                euler.x += kickLeanForward * k * strength;
                scale.y *= 1f + kickStretch * k * strength;
                float thin = 1f - kickStretch * 0.5f * k * strength;
                scale.x *= thin;
                scale.z *= thin;
                kickTimer += dt;
                if (kickTimer >= kickDuration)
                    kickTimer = -1f;
            }

            if (player.IsTackling)
            {
                euler.x += tackleLean;
                euler.z += tackleRoll;
                drop += tackleDrop;
            }

            if (!player.IsGrounded)
            {
                scale.y *= 1f + airStretch;
                scale.x *= 1f - airStretch * 0.5f;
                scale.z *= 1f - airStretch * 0.5f;
            }

            if (player.IsStunned)
            {
                euler.z += Mathf.Sin(time * stunWobbleFrequency) * stunWobbleAngle;
                euler.y += Mathf.Cos(time * stunWobbleFrequency * 0.7f) * stunWobbleAngle * 0.5f;
            }

            float t = 1f - Mathf.Exp(-smoothing * dt);
            currentEuler = Vector3.Lerp(currentEuler, euler, t);
            currentScale = Vector3.Lerp(currentScale, scale, t);
            currentDrop = Mathf.Lerp(currentDrop, drop, t);

            visualRoot.localRotation = baseRotation * Quaternion.Euler(currentEuler);
            visualRoot.localScale = Vector3.Scale(baseScale, currentScale);
            visualRoot.localPosition = basePosition + Vector3.down * currentDrop;

            UpdateMarker();
        }

        Transform CreateVisualRoot()
        {
            var children = new List<Transform>();
            foreach (Transform child in transform)
                children.Add(child);

            Transform root = new GameObject("Visual").transform;
            root.SetParent(transform, false);
            foreach (Transform child in children)
                child.SetParent(root, true);
            return root;
        }

        void CreateMarker()
        {
            GameObject disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "TeamMarker";
            DestroyImmediate(disc.GetComponent<Collider>());
            marker = disc.transform;
            marker.SetParent(transform, false);
            marker.localPosition = Vector3.up * 0.003f;

            markerRenderer = disc.GetComponent<Renderer>();
            markerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            markerRenderer.receiveShadows = false;
            markerBlock = new MaterialPropertyBlock();
        }

        void UpdateMarker()
        {
            if (!marker)
                return;

            float charge = player.ChargeAmount;
            float diameter = markerRadius * 2f * (1f + markerChargeGrowth * charge);
            marker.localScale = new Vector3(diameter, 0.002f, diameter);

            Color color = Color.Lerp(player.TeamColor, Color.white, charge * 0.6f);
            if (color == appliedMarkerColor)
                return;

            appliedMarkerColor = color;
            markerRenderer.GetPropertyBlock(markerBlock);
            markerBlock.SetColor(BaseColorId, color);
            markerBlock.SetColor(ColorId, color);
            markerRenderer.SetPropertyBlock(markerBlock);
        }
    }
}
