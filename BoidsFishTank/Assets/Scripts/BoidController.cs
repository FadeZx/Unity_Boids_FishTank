#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;  // new Input System
#endif

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[DefaultExecutionOrder(-50)]
public class BoidController : MonoBehaviour
{
    [Header("References")]
    public BoxCollider simulationArea;     // IsTrigger = true
    public BoidAgent boidPrefab;

    [Header("Counts")]
    public int boidCount = 100;

    [Header("Speeds")]
    public float minSpeed = 1.5f;
    public float maxSpeed = 4.0f;
    public float maxSteerForce = 6.0f;

    [Header("Neighborhood")]
    public float neighborRadius = 2.0f;
    public float separationRadius = 0.6f;

    [Header("Weights")]
    public float weightSeparation = 1.5f;
    public float weightAlignment = 1.0f;
    public float weightCohesion = 1.0f;
    public float weightBounds = 2.0f;
    public float weightObstacleAvoid = 3.0f;

    [Header("Obstacle Avoidance")]
    public LayerMask obstacleMask;       // set to "Obstacle"
    public float avoidDistance = 1.5f;   // ray length
    public float avoidProbeAngle = 25f;  // degrees (side feelers)

    [Header("Predator Avoidance")]
    public OrcaController predatorController;  // assign your OrcaController to make prey flee orcas
    public float predatorAvoidRadius = 2.5f;   // how far prey start reacting to orcas
    public float weightPredatorAvoid = 2.0f;   // base strength of predator avoidance
    [Tooltip("Extra boost multiplier applied to predator avoidance when orca is very close.")]
    public float predatorAvoidBoost = 2.0f;
    [Tooltip("Distance within which the predator avoidance gets fully boosted.")]
    public float predatorPanicRadius = 1.2f;

    [Header("Vertical Steering")]
    [Tooltip("Scale for up/down steering vs left/right. < 1 makes vertical turning harder.")]
    public float verticalSteerDamping = 0.4f;

    [Header("Debug")]
    public bool drawDebug = false;

    [HideInInspector] public List<BoidAgent> agents = new List<BoidAgent>();

    // --- UI / persistence ---
    [Serializable]
    public class BoidSettings
    {
        public int boidCount;
        public float minSpeed, maxSpeed, maxSteerForce;
        public float neighborRadius, separationRadius;
        public float weightSeparation, weightAlignment, weightCohesion, weightBounds, weightObstacleAvoid;
        public float avoidDistance, avoidProbeAngle;
        public float predatorAvoidRadius, weightPredatorAvoid;
        public float predatorAvoidBoost, predatorPanicRadius;
        public float verticalSteerDamping;
        public bool drawDebug;
    }

    const string kPrefsKey = "Boids_Settings_JSON";
    string JsonPath => Path.Combine(Application.persistentDataPath, "boids_settings.json");

    bool showUI = true;        // F1 toggle
    Vector2 scroll;            // UI scroll
    int lastSpawnCount;        // detect boidCount change for respawn

    void Start()
    {
        if (!simulationArea)
        {
            Debug.LogError("Assign a BoxCollider as simulationArea (Is Trigger).");
            enabled = false;
            return;
        }

        // Try auto-load from file (if exists), then PlayerPrefs
        TryLoadFromFile();
        if (agents.Count == 0) Spawn();
    }

    void Update()
    {
        // Toggle UI
        if (KeyDown_F1()) showUI = !showUI;

        // Hotkeys
        if (CtrlS_Down()) SaveToFile();
        if (CtrlL_Down()) LoadFromFile();
        if (KeyDown_R()) Respawn();

        // Respawn when count changed
        if (boidCount != lastSpawnCount) Respawn();
    }


    // ----------------- Spawning -----------------
    void Spawn()
    {
        ClearAgents();

        if (!boidPrefab)
        {
            Debug.LogWarning("BoidController: Missing boidPrefab.");
            return;
        }

        var area = simulationArea.bounds;
        for (int i = 0; i < boidCount; i++)
        {
            Vector3 p = new Vector3(
                UnityEngine.Random.Range(area.min.x, area.max.x),
                UnityEngine.Random.Range(area.min.y, area.max.y),
                UnityEngine.Random.Range(area.min.z, area.max.z)
            );

            var a = Instantiate(boidPrefab, p, Quaternion.identity, transform);
            a.controller = this;
            a.Velocity = UnityEngine.Random.insideUnitSphere.normalized * UnityEngine.Random.Range(minSpeed, maxSpeed);
            agents.Add(a);
        }
        lastSpawnCount = boidCount;
    }

    void Respawn()
    {
        Spawn();
        // Reset kill count when starting over
        if (predatorController != null)
            predatorController.ResetKillCount();
    }

    void ClearAgents()
    {
        // delete children
        for (int i = transform.childCount - 1; i >= 0; i--)
        {
            var child = transform.GetChild(i);
            Destroy(child.gameObject);
        }
        agents.Clear();
    }

    // Remove a single prey agent (called by predators upon contact)
    public void RemoveAgent(BoidAgent agent)
    {
        if (agent == null) return;
        agents.Remove(agent);
        Destroy(agent.gameObject);
        // Decrease desired count to reflect kills, without forcing respawn
        if (boidCount > 0) boidCount--;
        // Keep lastSpawnCount in sync so Update() doesn't auto-respawn
        lastSpawnCount = boidCount;
    }

    // ----------------- Steering -----------------
    public Vector3 ComputeSteering(BoidAgent self, float dt, out (Vector3 sep, Vector3 ali, Vector3 coh, Vector3 bounds, Vector3 avoid) forces)
    {
        Vector3 pos = self.Position;
        Vector3 vel = self.Velocity;

        Vector3 sep = Vector3.zero;
        Vector3 ali = Vector3.zero;
        Vector3 coh = Vector3.zero;
        int neighborCount = 0;

        float nRad2 = neighborRadius * neighborRadius;
        float sRad2 = separationRadius * separationRadius;

        foreach (var other in agents)
        {
            if (other == self) continue;
            Vector3 to = other.Position - pos;
            float d2 = to.sqrMagnitude;
            if (d2 > nRad2) continue;
            neighborCount++;

            Vector3 dir = to.normalized;
            if (d2 < sRad2)
                sep -= dir / Mathf.Max(0.001f, Mathf.Sqrt(d2));
            ali += other.Velocity;
            coh += other.Position;
        }

        if (neighborCount > 0)
        {
            ali = (ali / neighborCount).normalized * maxSpeed - vel;
            coh = ((coh / neighborCount) - pos);
        }

        if (sep.sqrMagnitude > 0.0001f) sep = sep.normalized * maxSpeed - vel;

        Vector3 boundsForce = BoundsSteer(pos, vel);
        Vector3 avoid = ObstacleAvoid(pos, vel);

        // Predator avoidance (flee orcas) with boost when close
        Vector3 predatorAvoid = Vector3.zero;
        if (predatorController != null && predatorController.pod != null && predatorController.pod.Count > 0)
        {
            float r2 = predatorAvoidRadius * predatorAvoidRadius;
            foreach (var o in predatorController.pod)
            {
                Vector3 to = o.transform.position - pos;
                float d2 = to.sqrMagnitude;
                if (d2 < r2 && d2 > 0.0001f)
                {
                    float boost = 1f;
                    if (d2 < predatorPanicRadius * predatorPanicRadius)
                        boost = predatorAvoidBoost;
                    predatorAvoid -= boost * to.normalized / Mathf.Sqrt(d2);
                }
            }
            if (predatorAvoid.sqrMagnitude > 0.0001f)
                predatorAvoid = predatorAvoid.normalized * maxSpeed - vel;
        }

        // Combine forces
        Vector3 steer =
            weightSeparation * sep +
            weightAlignment * ali +
            weightCohesion * coh +
            weightBounds * boundsForce +
            weightObstacleAvoid * avoid +
            weightPredatorAvoid * predatorAvoid;

        // Make vertical steering harder: scale Y component before clamping
        steer.y *= verticalSteerDamping;

        // Clamp final steering
        if (steer.sqrMagnitude > maxSteerForce * maxSteerForce)
            steer = steer.normalized * maxSteerForce;

        forces = (sep, ali, coh, boundsForce, avoid);
        return steer;
    }

    Vector3 BoundsSteer(Vector3 pos, Vector3 vel)
    {
        var b = simulationArea.bounds;
        Vector3 steer = Vector3.zero;
        float pad = 0.5f; // padding from walls
        Vector3 target = pos;

        if (pos.x < b.min.x + pad) target.x = b.min.x + pad;
        else if (pos.x > b.max.x - pad) target.x = b.max.x - pad;

        if (pos.y < b.min.y + pad) target.y = b.min.y + pad;
        else if (pos.y > b.max.y - pad) target.y = b.max.y - pad;

        if (pos.z < b.min.z + pad) target.z = b.min.z + pad;
        else if (pos.z > b.max.z - pad) target.z = b.max.z - pad;

        if (target != pos)
        {
            Vector3 desired = (target - pos).normalized * maxSpeed;
            steer = desired - vel;
        }
        return steer;
    }

    Vector3 ObstacleAvoid(Vector3 pos, Vector3 vel)
    {
        if (vel.sqrMagnitude < 0.0001f) return Vector3.zero;
        Vector3 fwd = vel.normalized;
        Vector3 steer = Vector3.zero;

        // Forward ray
        if (Physics.Raycast(pos, fwd, out RaycastHit hit, avoidDistance, obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 away = Vector3.Reflect(fwd, hit.normal);
            steer += away;
        }

        // Side feelers
        Quaternion leftQ = Quaternion.AngleAxis(-avoidProbeAngle, Vector3.up);
        Quaternion rightQ = Quaternion.AngleAxis(avoidProbeAngle, Vector3.up);
        Vector3 left = leftQ * fwd;
        Vector3 right = rightQ * fwd;

        if (Physics.Raycast(pos, left, avoidDistance * 0.8f, obstacleMask, QueryTriggerInteraction.Ignore))
            steer += -Vector3.Cross(Vector3.up, left).normalized;

        if (Physics.Raycast(pos, right, avoidDistance * 0.8f, obstacleMask, QueryTriggerInteraction.Ignore))
            steer += Vector3.Cross(Vector3.up, right).normalized;

        return steer;
    }

    // ----------------- Runtime UI -----------------
    void OnGUI()
    {
        if (!showUI) return;

        const int w = 320;
        Rect r = new Rect(12, 12, w, Screen.height - 24);
        GUILayout.BeginArea(r, GUI.skin.box);
        GUILayout.Label("<b>Boids Runtime Settings</b>", new GUIStyle(GUI.skin.label) { richText = true });

        scroll = GUILayout.BeginScrollView(scroll);

        // Counts
        boidCount = IntSliderT("Boid Count", "Number of prey agents simulated (decreases on kills).", boidCount, 1, 2000);
        GUILayout.Label($"Current Count: <b>{agents.Count}</b>", new GUIStyle(GUI.skin.label) { richText = true });

        // Speeds
        minSpeed = SliderT("Min Speed", "Minimum cruising speed (prevents stalling).", minSpeed, 0.1f, maxSpeed);
        maxSpeed = SliderT("Max Speed", "Top speed for desired velocities.", maxSpeed, minSpeed, 15f);
        maxSteerForce = SliderT("Max Steer", "Upper limit on steering force to avoid jitter.", maxSteerForce, 0.1f, 20f);

        GUILayout.Space(6);

        // Neighborhood
        neighborRadius = SliderT("Neighbor Radius", "How far neighbors influence alignment/cohesion.", neighborRadius, 0.1f, 10f);
        separationRadius = SliderT("Separation Radius", "Distance where strong separation pushes away.", separationRadius, 0.05f, neighborRadius);

        GUILayout.Space(6);

        // Weights
        weightSeparation = SliderT("W Separation", "Weight of separation (spread apart).", weightSeparation, 0f, 10f);
        weightAlignment = SliderT("W Alignment", "Weight of alignment (match headings).", weightAlignment, 0f, 10f);
        weightCohesion = SliderT("W Cohesion", "Weight of cohesion (stay together).", weightCohesion, 0f, 10f);
        weightBounds = SliderT("W Bounds", "Weight of staying inside the tank volume.", weightBounds, 0f, 10f);
        weightObstacleAvoid = SliderT("W Obstacle", "Weight of steering away from obstacles.", weightObstacleAvoid, 0f, 10f);

        GUILayout.Space(6);

        // Avoidance
        avoidDistance = SliderT("Avoid Distance", "Forward probe length for obstacle detection.", avoidDistance, 0.1f, 10f);
        avoidProbeAngle = SliderT("Avoid Angle", "Side feeler spread angle for obstacle sensing.", avoidProbeAngle, 0f, 85f);

        GUILayout.Space(6);

        // Predator
        GUILayout.Label("<b>Predator</b>", new GUIStyle(GUI.skin.label) { richText = true });
        predatorAvoidRadius = SliderT("Predator Radius", "Distance within which prey react to orcas.", predatorAvoidRadius, 0.5f, 10f);
        weightPredatorAvoid = SliderT("W Predator", "Strength of fleeing response from orcas.", weightPredatorAvoid, 0f, 10f);
        predatorPanicRadius = SliderT("Panic Radius", "Within this distance, boost predator avoidance.", predatorPanicRadius, 0.2f, 5f);
        predatorAvoidBoost = SliderT("Panic Boost", "Multiplier for predator avoidance inside Panic Radius.", predatorAvoidBoost, 1f, 5f);

        GUILayout.Space(6);
        GUILayout.Label("<b>Vertical Steering</b>", new GUIStyle(GUI.skin.label) { richText = true });
        verticalSteerDamping = SliderT("Vertical Damping", "Scale for up/down steering vs left/right (<1 = harder to steer vertically).", verticalSteerDamping, 0.1f, 1f);

        // Debug
        drawDebug = ToggleT("Draw Debug", "Render debug gizmos/lines.", drawDebug);

        GUILayout.Space(10);
        GUILayout.Label($"Save Path:\n<size=10>{Application.persistentDataPath}</size>", new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true });

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save → File")) SaveToFile();
        if (GUILayout.Button("Load ← File")) LoadFromFile();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Save → PlayerPrefs")) SaveToPrefs();
        if (GUILayout.Button("Load ← PlayerPrefs")) LoadFromPrefs();
        GUILayout.EndHorizontal();

        if (GUILayout.Button("Respawn Now")) Respawn();

        GUILayout.EndScrollView();

        // Hover tooltip (shows current control description)
        if (!string.IsNullOrEmpty(GUI.tooltip))
        {
            var tipStyle = new GUIStyle(GUI.skin.box) { wordWrap = true, fontSize = 11 };
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(GUI.tooltip, tipStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndVertical();
        }

        GUILayout.Label("Hotkeys: F1 toggle • Ctrl+S save • Ctrl+L load • R respawn", new GUIStyle(GUI.skin.label) { fontSize = 10, wordWrap = true });
        GUILayout.EndArea();
    }

    // Simple IMGUI helpers
    float Slider(string label, float v, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130));
        v = GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString("0.00"), GUILayout.Width(48));
        GUILayout.EndHorizontal();
        return v;
    }
    int IntSlider(string label, int v, int min, int max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130));
        v = (int)GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString(), GUILayout.Width(48));
        GUILayout.EndHorizontal();
        return Mathf.Clamp(v, min, max);
    }
    bool Toggle(string label, bool t)
    {
        GUILayout.BeginHorizontal();
        t = GUILayout.Toggle(t, "", GUILayout.Width(18));
        GUILayout.Label(label);
        GUILayout.EndHorizontal();
        return t;
    }

    // Tooltip-aware helpers
    float SliderT(string label, string tooltip, float v, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(130));
        v = GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString("0.00"), GUILayout.Width(48));
        GUILayout.EndHorizontal();
        return v;
    }
    int IntSliderT(string label, string tooltip, int v, int min, int max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(new GUIContent(label, tooltip), GUILayout.Width(130));
        v = (int)GUILayout.HorizontalSlider(v, min, max);
        GUILayout.Label(v.ToString(), GUILayout.Width(48));
        GUILayout.EndHorizontal();
        return Mathf.Clamp(v, min, max);
    }
    bool ToggleT(string label, string tooltip, bool t)
    {
        GUILayout.BeginHorizontal();
        t = GUILayout.Toggle(t, new GUIContent("", tooltip), GUILayout.Width(18));
        GUILayout.Label(new GUIContent(label, tooltip));
        GUILayout.EndHorizontal();
        return t;
    }

    // ----------------- Persistence -----------------
    BoidSettings Collect()
    {
        return new BoidSettings
        {
            boidCount = boidCount,
            minSpeed = minSpeed,
            maxSpeed = maxSpeed,
            maxSteerForce = maxSteerForce,
            neighborRadius = neighborRadius,
            separationRadius = separationRadius,
            weightSeparation = weightSeparation,
            weightAlignment = weightAlignment,
            weightCohesion = weightCohesion,
            weightBounds = weightBounds,
            weightObstacleAvoid = weightObstacleAvoid,
            avoidDistance = avoidDistance,
            avoidProbeAngle = avoidProbeAngle,
            predatorAvoidRadius = predatorAvoidRadius,
            weightPredatorAvoid = weightPredatorAvoid,
            predatorAvoidBoost = predatorAvoidBoost,
            predatorPanicRadius = predatorPanicRadius,
            verticalSteerDamping = verticalSteerDamping,
            drawDebug = drawDebug
        };
    }

    void Apply(BoidSettings s, bool respawnIfNeeded = true)
    {
        if (s == null) return;
        bool needRespawn = s.boidCount != boidCount;

        boidCount = s.boidCount;
        minSpeed = s.minSpeed; maxSpeed = s.maxSpeed; maxSteerForce = s.maxSteerForce;
        neighborRadius = s.neighborRadius; separationRadius = s.separationRadius;
        weightSeparation = s.weightSeparation; weightAlignment = s.weightAlignment; weightCohesion = s.weightCohesion; weightBounds = s.weightBounds; weightObstacleAvoid = s.weightObstacleAvoid;
        avoidDistance = s.avoidDistance; avoidProbeAngle = s.avoidProbeAngle;
        predatorAvoidRadius = s.predatorAvoidRadius; weightPredatorAvoid = s.weightPredatorAvoid;
        predatorAvoidBoost = s.predatorAvoidBoost; predatorPanicRadius = s.predatorPanicRadius;
        verticalSteerDamping = s.verticalSteerDamping;
        drawDebug = s.drawDebug;

        if (respawnIfNeeded && needRespawn) Respawn();
    }

    void SaveToFile()
    {
        try
        {
            var json = JsonUtility.ToJson(Collect(), true);
            File.WriteAllText(JsonPath, json);
#if UNITY_EDITOR
            Debug.Log($"[Boids] Saved settings to file:\n{JsonPath}\n{json}");
#endif
        }
        catch (Exception e)
        {
            Debug.LogError($"[Boids] SaveToFile error: {e.Message}");
        }
    }

    void LoadFromFile()
    {
        try
        {
            if (!File.Exists(JsonPath))
            {
                Debug.LogWarning($"[Boids] No settings file at: {JsonPath}");
                return;
            }
            var json = File.ReadAllText(JsonPath);
            var s = JsonUtility.FromJson<BoidSettings>(json);
            Apply(s, true);
#if UNITY_EDITOR
            Debug.Log($"[Boids] Loaded settings from file:\n{JsonPath}\n{json}");
#endif
        }
        catch (Exception e)
        {
            Debug.LogError($"[Boids] LoadFromFile error: {e.Message}");
        }
    }

    void SaveToPrefs()
    {
        var json = JsonUtility.ToJson(Collect(), false);
        PlayerPrefs.SetString(kPrefsKey, json);
        PlayerPrefs.Save();
#if UNITY_EDITOR
        Debug.Log($"[Boids] Saved settings to PlayerPrefs: {json}");
#endif
    }

    void LoadFromPrefs()
    {
        if (!PlayerPrefs.HasKey(kPrefsKey))
        {
            Debug.LogWarning("[Boids] No PlayerPrefs settings found.");
            return;
        }
        var json = PlayerPrefs.GetString(kPrefsKey);
        var s = JsonUtility.FromJson<BoidSettings>(json);
        Apply(s, true);
#if UNITY_EDITOR
        Debug.Log($"[Boids] Loaded settings from PlayerPrefs: {json}");
#endif
    }

    bool TryLoadFromFile()
    {
        if (File.Exists(JsonPath))
        {
            LoadFromFile();
            return true;
        }
        if (PlayerPrefs.HasKey(kPrefsKey))
        {
            LoadFromPrefs();
            return true;
        }
        return false;
    }

    // ------------- Gizmo (optional) -------------
    void OnDrawGizmosSelected()
    {
        if (simulationArea)
        {
            Gizmos.color = new Color(0, 0.8f, 1f, 0.15f);
            Gizmos.DrawCube(simulationArea.bounds.center, simulationArea.bounds.size);
        }
    }


    // --- Input helpers (works with both systems) ---
    bool KeyDown_F1()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame;
#else
    return Input.GetKeyDown(KeyCode.F1);
#endif
    }

    bool KeyDown_R()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#else
    return Input.GetKeyDown(KeyCode.R);
#endif
    }

    bool CtrlHeld()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        if (k == null) return false;
        return k.leftCtrlKey.isPressed || k.rightCtrlKey.isPressed;
#else
    return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
#endif
    }

    bool CtrlS_Down()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        return k != null && CtrlHeld() && k.sKey.wasPressedThisFrame;
#else
    return CtrlHeld() && Input.GetKeyDown(KeyCode.S);
#endif
    }

    bool CtrlL_Down()
    {
#if ENABLE_INPUT_SYSTEM
        var k = Keyboard.current;
        return k != null && CtrlHeld() && k.lKey.wasPressedThisFrame;
#else
    return CtrlHeld() && Input.GetKeyDown(KeyCode.L);
#endif
    }
}
