using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// Loads a velocity field for the airflow particle advection system.
/// Supports two sources — set exactly ONE in the Inspector:
///
///   (A) velocityCSVFileName  — actual PINO output (x,y,z,u,v,w per node)
///   (B) caseConfigFileName   — PINO case config JSON (mceinry_*.json)
///                              Auto-populates grid bounds and builds a
///                              synthetic field from ceiling diffuser positions.
///                              Use this until real PINO inference output exists.
///
/// CSV coordinate system:
///   PINO outputs FEniCS local coords (X=right, Y=forward/floor, Z=up).
///   Set remapFenicsToUnity = true (default) so the loader converts to
///   Unity convention (X=right, Y=up, Z=forward) on the way in.
///   If your generate_airflow_fields.py already remaps, set it false.
///
/// JSON coordinate system:
///   Remap is always applied — the JSON uses FEniCS local coords throughout.
///
/// Requires: com.unity.nuget.newtonsoft-json
///   Window → Package Manager → Add package by name → com.unity.nuget.newtonsoft-json
///
/// SETUP:
///   1. Drop on any persistent GameObject (e.g. GameManager).
///   2. Set caseConfigFileName = "mceinry_two_rooms_hallway_draft.json" for now.
///   3. Drop the JSON into Assets/StreamingAssets/.
///   4. Other scripts call VelocityFieldLoader.Instance.SampleVelocity(worldPos).
///   5. When PINO CSV is ready, clear caseConfigFileName, set velocityCSVFileName.
/// </summary>
public class VelocityFieldLoader : MonoBehaviour
{
    // -----------------------------------------------------------------------
    // Inspector fields
    // -----------------------------------------------------------------------

    [Header("Source — set exactly one")]
    [Tooltip("PINO output CSV (x,y,z,u,v,w). Leave blank to use case config JSON instead.")]
    public string velocityCSVFileName = "";

    [Tooltip("PINO case config JSON (mceinry_*.json). Auto-populates bounds and builds " +
             "a synthetic field. Leave blank once real PINO CSV is available.")]
    public string caseConfigFileName = "";

    [Header("Grid bounds — auto-populated from JSON; set manually for CSV")]
    public float xMin = 0f;
    public float yMin = 0f;
    public float zMin = 0f;
    public float xMax = 8.95f;
    public float yMax = 4.308f;  // FEniCS length_z (vertical) → Unity Y
    public float zMax = 6.45f;   // FEniCS length_y (floor depth) → Unity Z

    [Header("Grid resolution — auto-populated from JSON")]
    public float cellSize = 0.75f;

    [Header("CSV coordinate remap")]
    [Tooltip("True if the CSV uses FEniCS local coords (X right, Y floor-forward, Z up). " +
             "Applies FEniCS→Unity remap on load. False if CSV is already in Unity coords.")]
    public bool remapFenicsToUnity = true;

    [Header("Synthetic field tuning (JSON / case config mode only)")]
    [Tooltip("Inlet jet speed in m/s. Leave at 0 to auto-compute from ACH and diffuser area.")]
    public float inletJetSpeedOverride = 0f;

    [Tooltip("Gaussian sigma for XZ spread of ceiling inlet jets (meters). " +
             "0.5 m is roughly one diffuser width.")]
    public float jetSigma = 0.5f;

    [Header("Debug")]
    public bool logOnLoad = true;

    // -----------------------------------------------------------------------
    // Singleton
    // -----------------------------------------------------------------------
    public static VelocityFieldLoader Instance { get; private set; }

    // -----------------------------------------------------------------------
    // Public state — readable by AirflowParticleDriver etc.
    // -----------------------------------------------------------------------
    public bool IsLoaded    { get; private set; } = false;

    /// <summary>
    /// True when the active field was built analytically from diffuser positions
    /// rather than loaded from real PINO inference output.
    /// AirflowParticleDriver should log a visible warning when this is true.
    /// </summary>
    public bool IsSynthetic { get; private set; } = false;

    // -----------------------------------------------------------------------
    // Internal
    // -----------------------------------------------------------------------
    private Vector3[,,] _field;  // [ix, iy, iz] in Unity coords
    private int _nx, _ny, _nz;

    // -----------------------------------------------------------------------
    // Unity lifecycle
    // -----------------------------------------------------------------------
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        bool hasJson = !string.IsNullOrWhiteSpace(caseConfigFileName);
        bool hasCsv  = !string.IsNullOrWhiteSpace(velocityCSVFileName);

        if (hasJson && hasCsv)
        {
            Debug.LogWarning("[VelocityFieldLoader] Both caseConfigFileName and velocityCSVFileName " +
                             "are set — using JSON case config. Clear it once real PINO CSV is ready.");
            hasJson = true; hasCsv = false;
        }

        if (hasJson)      StartCoroutine(LoadCaseConfigCoroutine(caseConfigFileName));
        else if (hasCsv)  StartCoroutine(LoadCSVCoroutine(velocityCSVFileName));
        else              Debug.LogError("[VelocityFieldLoader] No file specified. " +
                                        "Set caseConfigFileName or velocityCSVFileName in the Inspector.");
    }

    // -----------------------------------------------------------------------
    // Public: reload at runtime (e.g. scenario switching)
    // -----------------------------------------------------------------------
    public void LoadField(string fileName)
    {
        IsLoaded = false;
        IsSynthetic = false;
        if (fileName.EndsWith(".json"))
            StartCoroutine(LoadCaseConfigCoroutine(fileName));
        else
            StartCoroutine(LoadCSVCoroutine(fileName));
    }

    // -----------------------------------------------------------------------
    // Public: sample the velocity at a Unity world-space position.
    // Returns Vector3.zero if not loaded or position is outside grid bounds.
    // -----------------------------------------------------------------------
    public Vector3 SampleVelocity(Vector3 worldPos)
    {
        if (!IsLoaded) return Vector3.zero;

        int ix = Mathf.Clamp(Mathf.FloorToInt((worldPos.x - xMin) / cellSize), 0, _nx - 1);
        int iy = Mathf.Clamp(Mathf.FloorToInt((worldPos.y - yMin) / cellSize), 0, _ny - 1);
        int iz = Mathf.Clamp(Mathf.FloorToInt((worldPos.z - zMin) / cellSize), 0, _nz - 1);

        return _field[ix, iy, iz];
    }

    // -----------------------------------------------------------------------
    // Public: bounds check (use to validate scene-to-field alignment)
    // -----------------------------------------------------------------------
    public bool IsInBounds(Vector3 worldPos)
    {
        return worldPos.x >= xMin && worldPos.x <= xMax &&
               worldPos.y >= yMin && worldPos.y <= yMax &&
               worldPos.z >= zMin && worldPos.z <= zMax;
    }

    // -----------------------------------------------------------------------
    // Public: human-readable bounds summary
    // -----------------------------------------------------------------------
    public string GetBoundsInfo()
    {
        string tag = IsSynthetic ? " [SYNTHETIC — awaiting PINO CSV]" : "";
        return $"Field bounds  x:[{xMin},{xMax}]  y:[{yMin},{yMax}]  z:[{zMin},{zMax}]" +
               $"  cellSize:{cellSize}{tag}";
    }

    // -----------------------------------------------------------------------
    // JSON case config loader
    // Reads geometry and hvac.diffusers; builds synthetic velocity field.
    //
    // FEniCS local coords  →  Unity coords
    //   length_x (right)   →  xMax  (Unity X)
    //   length_y (floor)   →  zMax  (Unity Z)   ← swapped
    //   length_z (up)      →  yMax  (Unity Y)   ← swapped
    //
    // Per diffuser center: FEniCS (fx, fy, fz) → Unity (fx, fz, fy)
    //
    // Synthetic field model:
    //   Each ceiling inlet → downward Gaussian jet at its XZ position.
    //   Each ceiling outlet → inverse-square sink pulling toward it.
    //   Sum of contributions at every grid cell gives the placeholder field.
    // -----------------------------------------------------------------------
    private IEnumerator LoadCaseConfigCoroutine(string fileName)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"[VelocityFieldLoader] Case config not found: {path}");
            yield break;
        }

        JObject root = JObject.Parse(File.ReadAllText(path));

        // --- Geometry → grid bounds ------------------------------------------
        JObject geom     = (JObject)root["geometry"];
        float fenicsLx   = geom["length_x"].Value<float>();  // → Unity X
        float fenicsLy   = geom["length_y"].Value<float>();  // → Unity Z
        float fenicsLz   = geom["length_z"].Value<float>();  // → Unity Y
        cellSize         = geom["mesh_size"].Value<float>();

        xMin = 0f; xMax = fenicsLx;
        yMin = 0f; yMax = fenicsLz;   // FEniCS Z (vertical) → Unity Y
        zMin = 0f; zMax = fenicsLy;   // FEniCS Y (floor depth) → Unity Z

        AllocateGrid();

        // --- HVAC: diffuser positions and flow rate --------------------------
        float ach        = root["hvac"]["ach"].Value<float>();
        float volume     = fenicsLx * fenicsLy * fenicsLz;
        float totalFlow  = ach * volume / 3600f;  // m³/s

        var inlets  = new List<(Vector3 pos, float area)>();
        var outlets = new List<Vector3>();

        foreach (JObject d in root["hvac"]["diffusers"])
        {
            string role = d["role"].Value<string>();
            if (role != "inlet" && role != "outlet") continue;

            JArray c  = (JArray)d["center"];
            float fx  = c[0].Value<float>();
            float fy  = c[1].Value<float>();
            float fz  = c[2].Value<float>();

            // FEniCS (fx, fy, fz) → Unity (fx, fz, fy)
            Vector3 unityPos = new Vector3(fx, fz, fy);

            if (role == "inlet")
            {
                JArray sz   = (JArray)d["size"];
                float area  = sz[0].Value<float>() * sz[1].Value<float>();
                inlets.Add((unityPos, area));
            }
            else
            {
                outlets.Add(unityPos);
            }
        }

        // Compute per-inlet jet speed from ACH unless overridden
        float avgArea     = 0f;
        foreach (var (_, area) in inlets) avgArea += area;
        avgArea /= Mathf.Max(1, inlets.Count);

        float jetSpeed    = inletJetSpeedOverride > 0f
            ? inletJetSpeedOverride
            : totalFlow / Mathf.Max(1, inlets.Count) / avgArea;

        float sinkStr     = totalFlow / Mathf.Max(1, outlets.Count);  // m³/s per outlet
        float sigma2      = jetSigma * jetSigma;

        if (logOnLoad)
            Debug.Log($"[VelocityFieldLoader] Case config parsed: " +
                      $"domain {fenicsLx:F2}×{fenicsLy:F2}×{fenicsLz:F2} m  " +
                      $"cellSize {cellSize} m  grid {_nx}×{_ny}×{_nz}  " +
                      $"{inlets.Count} inlets / {outlets.Count} outlets  " +
                      $"jet speed {jetSpeed:F2} m/s (ACH={ach})");

        // --- Build synthetic velocity field ----------------------------------
        for (int ix = 0; ix < _nx; ix++)
        for (int iy = 0; iy < _ny; iy++)
        for (int iz = 0; iz < _nz; iz++)
        {
            float wx = xMin + ix * cellSize;
            float wy = yMin + iy * cellSize;
            float wz = zMin + iz * cellSize;
            Vector3 cellPos = new Vector3(wx, wy, wz);

            Vector3 vel = Vector3.zero;

            // Inlet jets: downward Gaussian plume, attenuated by XZ distance
            foreach (var (inletPos, _) in inlets)
            {
                float dx  = wx - inletPos.x;
                float dz  = wz - inletPos.z;
                float w   = Mathf.Exp(-(dx * dx + dz * dz) / sigma2);
                vel      += new Vector3(0f, -jetSpeed * w, 0f);
            }

            // Outlet sinks: inverse-square pull toward each outlet center
            foreach (Vector3 outletPos in outlets)
            {
                Vector3 toOutlet = outletPos - cellPos;
                float dist       = toOutlet.magnitude + 0.01f;  // clamp to avoid /0
                vel             += toOutlet.normalized * (sinkStr / (dist * dist));
            }

            _field[ix, iy, iz] = vel;
        }

        IsSynthetic = true;
        IsLoaded    = true;

        if (logOnLoad)
            Debug.Log($"[VelocityFieldLoader] Synthetic field ready. {GetBoundsInfo()}");

        yield return null;
    }

    // -----------------------------------------------------------------------
    // CSV loader — for actual PINO inference output (x,y,z,u,v,w per node).
    //
    // If remapFenicsToUnity is true (default), input coords are treated as
    // FEniCS local (X right, Y floor-forward, Z up) and converted to Unity
    // (X right, Y up, Z forward) on the way in:
    //   position: (x, y, z)_fenics → (x, z, y)_unity
    //   velocity: (u, v, w)_fenics → (u, w, v)_unity
    //
    // If your Python pipeline already remaps, set remapFenicsToUnity = false.
    // -----------------------------------------------------------------------
    private IEnumerator LoadCSVCoroutine(string fileName)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        if (!File.Exists(path))
        {
            Debug.LogError($"[VelocityFieldLoader] CSV not found: {path}");
            yield break;
        }

        AllocateGrid();

        if (logOnLoad)
            Debug.Log($"[VelocityFieldLoader] Loading {fileName}  " +
                      $"grid: {_nx}×{_ny}×{_nz} = {_nx * _ny * _nz} cells  " +
                      $"remapFenics={remapFenicsToUnity}");

        int rowsRead    = 0;
        int rowsSkipped = 0;

        using (var reader = new StreamReader(path))
        {
            reader.ReadLine();  // skip header

            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                string[] parts = line.Split(',');
                if (parts.Length < 6) { rowsSkipped++; continue; }

                // Use InvariantCulture to avoid locale-dependent decimal separators
                if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
                    !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float u) ||
                    !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ||
                    !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
                {
                    rowsSkipped++;
                    continue;
                }

                // FEniCS (x, y, z) → Unity (x, z, y) if remap is enabled
                float px = x;
                float py = remapFenicsToUnity ? z : y;
                float pz = remapFenicsToUnity ? y : z;

                float ux = u;
                float uy = remapFenicsToUnity ? w : v;
                float uz = remapFenicsToUnity ? v : w;

                int ix = Mathf.Clamp(Mathf.RoundToInt((px - xMin) / cellSize), 0, _nx - 1);
                int iy = Mathf.Clamp(Mathf.RoundToInt((py - yMin) / cellSize), 0, _ny - 1);
                int iz = Mathf.Clamp(Mathf.RoundToInt((pz - zMin) / cellSize), 0, _nz - 1);

                _field[ix, iy, iz] = new Vector3(ux, uy, uz);
                rowsRead++;
            }
        }

        IsSynthetic = false;
        IsLoaded    = true;

        if (logOnLoad)
            Debug.Log($"[VelocityFieldLoader] Done. {rowsRead} rows loaded, " +
                      $"{rowsSkipped} skipped. {GetBoundsInfo()}");

        yield return null;
    }

    // -----------------------------------------------------------------------
    // Internal: allocate the grid array from current bounds + cellSize
    // -----------------------------------------------------------------------
    private void AllocateGrid()
    {
        _nx = Mathf.RoundToInt((xMax - xMin) / cellSize) + 1;
        _ny = Mathf.RoundToInt((yMax - yMin) / cellSize) + 1;
        _nz = Mathf.RoundToInt((zMax - zMin) / cellSize) + 1;
        _field = new Vector3[_nx, _ny, _nz];
    }
}