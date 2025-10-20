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

        Vector3 steer =
            weightSeparation * sep +
            weightAlignment * ali +
            weightCohesion * coh +
            weightBounds * boundsForce +
            weightObstacleAvoid * avoid;

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
        boidCount = IntSlider("Boid Count", boidCount, 1, 2000);

        // Speeds
        minSpeed = Slider("Min Speed", minSpeed, 0.1f, maxSpeed);
        maxSpeed = Slider("Max Speed", maxSpeed, minSpeed, 15f);
        maxSteerForce = Slider("Max Steer", maxSteerForce, 0.1f, 20f);

        GUILayout.Space(6);

        // Neighborhood
        neighborRadius = Slider("Neighbor Radius", neighborRadius, 0.1f, 10f);
        separationRadius = Slider("Separation Radius", separationRadius, 0.05f, neighborRadius);

        GUILayout.Space(6);

        // Weights
        weightSeparation = Slider("W Separation", weightSeparation, 0f, 10f);
        weightAlignment = Slider("W Alignment", weightAlignment, 0f, 10f);
        weightCohesion = Slider("W Cohesion", weightCohesion, 0f, 10f);
        weightBounds = Slider("W Bounds", weightBounds, 0f, 10f);
        weightObstacleAvoid = Slider("W Obstacle", weightObstacleAvoid, 0f, 10f);

        GUILayout.Space(6);

        // Avoidance
        avoidDistance = Slider("Avoid Distance", avoidDistance, 0.1f, 10f);
        avoidProbeAngle = Slider("Avoid Angle", avoidProbeAngle, 0f, 85f);

        // Debug
        drawDebug = Toggle("Draw Debug", drawDebug);

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
