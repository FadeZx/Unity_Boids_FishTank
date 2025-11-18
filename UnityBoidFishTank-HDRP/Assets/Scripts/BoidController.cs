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

    [Tooltip("Tank area collider - boids will avoid spawning inside this area but use it for movement boundaries")]
    public BoxCollider simulationArea;     // IsTrigger = true
    public BoidAgent boidPrefab;


    [Header("Counts")]
    public int boidCount = 100;

    [Header("Spawning")]
    [Tooltip("Distance from tank center for spawning ring (gizmo shows exact radius you set)")]
    public float spawnRadius = 5.0f;
    [Tooltip("Center point for spawning ring (if not set, uses tank center when tank area exists)")]
    public Transform spawnCenter;
    [Tooltip("Maximum attempts to find valid spawn position outside tank")]
    public int maxSpawnAttempts = 50;

    [Header("Speeds")]
    public float minSpeed = 1.5f;
    public float maxSpeed = 4.0f;
    [Tooltip("Hard cap on boid speed (0 = uncapped). Applies after any panic boost.")]
    public float maxSpeedCap = 0f;
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
    [Tooltip("Extra speed multiplier when in panic range of an orca.")]
    public float predatorPanicSpeedMultiplier = 1.25f;

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
        public float minSpeed, maxSpeed, maxSpeedCap, maxSteerForce;
        public float neighborRadius, separationRadius;
        public float weightSeparation, weightAlignment, weightCohesion, weightBounds, weightObstacleAvoid;
        public float avoidDistance, avoidProbeAngle;
        public float predatorAvoidRadius, weightPredatorAvoid;
        public float predatorAvoidBoost, predatorPanicRadius, predatorPanicSpeedMultiplier;
        public float verticalSteerDamping;
        public bool drawDebug;
        public float spawnRadius;
        public int maxSpawnAttempts;
    }

    const string kPrefsKey = "Boids_Settings_JSON";
    string JsonPath => Path.Combine(Application.persistentDataPath, "boids_settings.json");

    bool showUI = true;        // always draw handle; F1 collapses/expands panel
    bool showPanel = true;     // collapse/expand similar to audio UI
    float panelAnim = 1f;      // 0 collapsed -> 1 expanded
    float panelAnimVel = 0f;
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
        // Toggle panel collapse/expand with F1
        if (KeyDown_F1()) showPanel = !showPanel;

        // Hotkeys
        if (CtrlS_Down()) SaveToFile();
        if (CtrlL_Down()) LoadFromFile();
        if (KeyDown_R()) Respawn();

        // Respawn when count changed
        if (boidCount != lastSpawnCount) Respawn();

#if UNITY_EDITOR
        // Force gizmo updates in editor when spawn center moves
        if (spawnCenter != null)
        {
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
#endif
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

        Vector3 centerPoint = spawnCenter ? spawnCenter.position : simulationArea.bounds.center;
        
        // Debug logging to see what's happening
        if (spawnCenter != null)
        {
            Debug.Log($"BoidController: Using spawn center '{spawnCenter.name}' at position {spawnCenter.position} with radius {spawnRadius}");
        }
        else
        {
            Debug.Log($"BoidController: No spawn center set, using simulation area center {simulationArea.bounds.center} with radius {spawnRadius}");
        }
        var area = simulationArea.bounds;
        
        for (int i = 0; i < boidCount; i++)
        {
            Vector3 p = GetValidSpawnPosition(centerPoint, area);

            var a = Instantiate(boidPrefab, p, Quaternion.identity, transform);
            a.controller = this;
            a.Velocity = UnityEngine.Random.insideUnitSphere.normalized * UnityEngine.Random.Range(minSpeed, maxSpeed);
            agents.Add(a);
        }
        lastSpawnCount = boidCount;
    }

    Vector3 GetValidSpawnPosition(Vector3 centerPoint, Bounds area)
    {
        // Always use the centerPoint passed in (which is already spawn center or fallback)
        Vector3 spawnCenterPos = centerPoint;
        Bounds bounds = simulationArea != null ? simulationArea.bounds : area;

        for (int attempt = 0; attempt < maxSpawnAttempts; attempt++)
        {
            // Uniformly pick a point inside the requested spawn radius
            Vector3 candidatePos = spawnCenterPos + UnityEngine.Random.insideUnitSphere * spawnRadius;

            // Keep within the simulation bounds if we have them
            candidatePos.x = Mathf.Clamp(candidatePos.x, bounds.min.x, bounds.max.x);
            candidatePos.y = Mathf.Clamp(candidatePos.y, bounds.min.y, bounds.max.y);
            candidatePos.z = Mathf.Clamp(candidatePos.z, bounds.min.z, bounds.max.z);

            return candidatePos;
        }

        // Fallback: use center if something goes wrong
        return spawnCenterPos;
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
        float speedLimit = maxSpeed;
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

        Vector3 boundsForce = BoundsSteer(pos, vel);
        Vector3 avoid = ObstacleAvoid(pos, vel);

        // Predator avoidance (flee orcas) with boost when close
        Vector3 predatorAvoid = Vector3.zero;
        bool predatorPanic = false;
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
                    {
                        boost = predatorAvoidBoost;
                        predatorPanic = true;
                    }
                    predatorAvoid -= boost * to.normalized / Mathf.Sqrt(d2);
                }
            }
        }

        if (predatorPanic)
            speedLimit = maxSpeed * predatorPanicSpeedMultiplier;
        speedLimit = GetCappedSpeed(speedLimit);
        self.RuntimeMaxSpeed = speedLimit;

        if (predatorAvoid.sqrMagnitude > 0.0001f)
            predatorAvoid = predatorAvoid.normalized * speedLimit - vel;

        if (neighborCount > 0)
        {
            ali = (ali / neighborCount).normalized * speedLimit - vel;
            coh = ((coh / neighborCount) - pos);
        }

        if (sep.sqrMagnitude > 0.0001f) sep = sep.normalized * speedLimit - vel;

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
        const float w = 320f;
        const float handleH = 22f;
        const float margin = 12f;

        float x = margin;
        float y = margin;

        // Animate panel height
        float targetAnim = showPanel ? 1f : 0f;
        panelAnim = Mathf.SmoothDamp(panelAnim, targetAnim, ref panelAnimVel, 0.15f, Mathf.Infinity, Time.deltaTime);

        float collapsedH = handleH + 4f;
        float expandedH = Screen.height - margin * 2f;
        float h = Mathf.Lerp(collapsedH, expandedH, Mathf.Clamp01(panelAnim));
        Rect r = new Rect(x, y, w, h);
        GUILayout.BeginArea(r, GUI.skin.box);

        // Handle button
        if (GUILayout.Button(showPanel ? "Boids(F1) ▲" : "Boids(F1) ▼", GUILayout.Height(handleH)))
        {
            showPanel = !showPanel;
        }

        if (!showPanel)
        {
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label("<b>Boids Runtime Settings</b>", new GUIStyle(GUI.skin.label) { richText = true });

        scroll = GUILayout.BeginScrollView(scroll);

        // Counts
        boidCount = IntSliderT("Boid Count", "Number of prey agents simulated (decreases on kills).", boidCount, 1, 2000);
        GUILayout.Label($"Current Count: <b>{agents.Count}</b>", new GUIStyle(GUI.skin.label) { richText = true });

        GUILayout.Space(6);
        GUILayout.Label("<b>Spawning</b>", new GUIStyle(GUI.skin.label) { richText = true });
        spawnRadius = SliderT("Spawn Radius", "Distance from tank center for spawning ring (gizmo shows exact radius).", spawnRadius, 0.5f, 20f);
        maxSpawnAttempts = IntSliderT("Max Spawn Attempts", "Maximum attempts to find valid spawn position outside tank.", maxSpawnAttempts, 10, 200);

        // Speeds
        minSpeed = SliderT("Min Speed", "Minimum cruising speed (prevents stalling).", minSpeed, 0.1f, maxSpeed);
        maxSpeed = SliderT("Max Speed", "Top speed for desired velocities.", maxSpeed, minSpeed, 15f);
        maxSpeedCap = SliderT("Max Speed Cap", "Absolute cap after boosts (0 = uncapped).", maxSpeedCap, 0f, 25f);
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
        predatorPanicSpeedMultiplier = SliderT("Panic Speed x", "Speed multiplier applied while panicking near an orca.", predatorPanicSpeedMultiplier, 1f, 3f);

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
        maxSpeedCap = maxSpeedCap,
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
        predatorPanicSpeedMultiplier = predatorPanicSpeedMultiplier,
        verticalSteerDamping = verticalSteerDamping,
        drawDebug = drawDebug,
        spawnRadius = spawnRadius,
        maxSpawnAttempts = maxSpawnAttempts
    };
    }

    void Apply(BoidSettings s, bool respawnIfNeeded = true)
    {
        if (s == null) return;
        bool needRespawn = s.boidCount != boidCount;

        boidCount = s.boidCount;
        minSpeed = s.minSpeed; maxSpeed = s.maxSpeed; maxSpeedCap = s.maxSpeedCap; maxSteerForce = s.maxSteerForce;
        neighborRadius = s.neighborRadius; separationRadius = s.separationRadius;
        weightSeparation = s.weightSeparation; weightAlignment = s.weightAlignment; weightCohesion = s.weightCohesion; weightBounds = s.weightBounds; weightObstacleAvoid = s.weightObstacleAvoid;
        avoidDistance = s.avoidDistance; avoidProbeAngle = s.avoidProbeAngle;
        predatorAvoidRadius = s.predatorAvoidRadius; weightPredatorAvoid = s.weightPredatorAvoid;
        predatorAvoidBoost = s.predatorAvoidBoost; predatorPanicRadius = s.predatorPanicRadius; predatorPanicSpeedMultiplier = s.predatorPanicSpeedMultiplier;
        verticalSteerDamping = s.verticalSteerDamping;
        drawDebug = s.drawDebug;
        spawnRadius = s.spawnRadius;
        maxSpawnAttempts = s.maxSpawnAttempts;

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

    public float GetCappedSpeed(float desiredMax)
    {
        if (maxSpeedCap > 0f)
            return Mathf.Min(desiredMax, maxSpeedCap);
        return desiredMax;
    }

    // ------------- Gizmos -------------
    void OnDrawGizmosSelected()
    {
        // Draw simulation area (blue) - only when selected for clarity
        if (simulationArea)
        {
            Gizmos.color = new Color(0, 0.8f, 1f, 0.15f);
            Gizmos.DrawCube(simulationArea.bounds.center, simulationArea.bounds.size);
            Gizmos.color = new Color(0, 0.8f, 1f, 0.5f);
            Gizmos.DrawWireCube(simulationArea.bounds.center, simulationArea.bounds.size);
        }
        
        // Draw tank area (red - avoid spawning here) - only when selected
        if (simulationArea)
        {
            Gizmos.color = new Color(1f, 0f, 0f, 0.15f);
            Gizmos.DrawCube(simulationArea.bounds.center, simulationArea.bounds.size);
            
            Gizmos.color = new Color(1f, 0f, 0f, 0.7f);
            Gizmos.DrawWireCube(simulationArea.bounds.center, simulationArea.bounds.size);
        }
        
        // Draw detailed spawn area info when selected
        Vector3 centerPoint;
        if (simulationArea)
        {
            // Always use spawn center if set, otherwise use simulation area center (not tank center)
            centerPoint = spawnCenter ? spawnCenter.position : simulationArea.bounds.center;
            float tankMaxExtent = Mathf.Max(simulationArea.bounds.size.x, simulationArea.bounds.size.y, simulationArea.bounds.size.z) * 0.5f;
            float minDistanceFromTank = tankMaxExtent + 1.0f;
            
            // Draw minimum safe distance (yellow) if different from spawn radius
            if (spawnRadius < minDistanceFromTank)
            {
                Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
                Gizmos.DrawWireSphere(centerPoint, minDistanceFromTank);
            }
        }
    }

    void OnDrawGizmos()
    {
        // Always show spawn area (not just when debug is enabled) for better visibility
        Vector3 centerPoint;
        if (simulationArea)
        {
            // Use spawn center if set, otherwise use tank center
            centerPoint = spawnCenter ? spawnCenter.position : simulationArea.bounds.center;
        }
        else
        {
            centerPoint = spawnCenter ? spawnCenter.position : (simulationArea ? simulationArea.bounds.center : transform.position);
        }
        
        // Show spawn radius with subtle transparency
        Gizmos.color = new Color(0f, 1f, 0f, 0.08f);
        Gizmos.DrawSphere(centerPoint, spawnRadius);
        
        Gizmos.color = new Color(0f, 1f, 0f, 0.3f);
        Gizmos.DrawWireSphere(centerPoint, spawnRadius);
        
        // Show spawn center point
        Gizmos.color = new Color(0f, 1f, 0f, 0.6f);
        Gizmos.DrawWireCube(centerPoint, Vector3.one * 0.3f);
    }


    // --- Validation for real-time gizmo updates ---
#if UNITY_EDITOR
    void OnValidate()
    {
        // This forces gizmos to update when inspector values change
        if (Application.isPlaying)
        {
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        }
    }
#endif

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
