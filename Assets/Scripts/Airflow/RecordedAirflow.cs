using System;
using UnityEngine;

// Imported SI samples. Solver (x,y,z-up) is converted to Unity (x,y-up,z).
[PreferBinarySerialization]
public class RecordedAirflow : ScriptableObject
{
    public Vector3[] positions;
    public Vector3[] velocities; // time-major
    public float[] times;
    public Bounds bounds;
    public float maxSpeed;
    [NonSerialized] int[] tree;
    // Four neighbours, reused on Unity's main thread without per-particle allocations.
    readonly int[] nearest = new int[4];
    readonly float[] distances = new float[4];

    public void Initialize()
    {
        if (tree != null) return;
        tree = new int[positions.Length];
        for (int i = 0; i < tree.Length; i++) tree[i] = i;
        Build(0, tree.Length, 0);
    }
    void Build(int start, int count, int depth)
    {
        if (count < 2) return;
        int axis = depth % 3;
        Array.Sort(tree, start, count, System.Collections.Generic.Comparer<int>.Create(
            (a,b) => positions[a][axis].CompareTo(positions[b][axis])));
        int half = count / 2;
        Build(start, half, depth + 1);
        Build(start + half + 1, count - half - 1, depth + 1);
    }
    void Search(Vector3 p, int start, int count, int depth)
    {
        if (count == 0) return;
        int half = count / 2, mid = start + half, index = tree[mid];
        float d = (positions[index] - p).sqrMagnitude;
        if (d < distances[3])
        {
            int slot = 3;
            while (slot > 0 && d < distances[slot - 1])
            { distances[slot] = distances[slot - 1]; nearest[slot] = nearest[slot - 1]; slot--; }
            distances[slot] = d; nearest[slot] = index;
        }
        float delta = p[depth % 3] - positions[index][depth % 3];
        int right = count - half - 1;
        if (delta < 0) Search(p, start, half, depth + 1);
        else Search(p, mid + 1, right, depth + 1);
        if (delta * delta <= distances[3])
        {
            if (delta < 0) Search(p, mid + 1, right, depth + 1);
            else Search(p, start, half, depth + 1);
        }
    }
    public Vector3 Sample(Vector3 p, float time, float supportRadius)
    {
        if (!bounds.Contains(p)) return Vector3.zero;
        Initialize();
        for (int i = 0; i < 4; i++) { distances[i] = float.PositiveInfinity; nearest[i] = 0; }
        Search(p, 0, tree.Length, 0);
        if (distances[0] > supportRadius * supportRadius) return Vector3.zero;
        int frame = Array.BinarySearch(times, time);
        if (frame < 0) frame = Math.Max(0, ~frame - 1);
        frame = Math.Min(frame, times.Length - 1);
        int next = Math.Min(frame + 1, times.Length - 1);
        float blend = next == frame ? 0 : Mathf.InverseLerp(times[frame], times[next], time);
        Vector3 result = Vector3.zero;
        float weights = 0;
        for (int i = 0; i < 4; i++)
        {
            float weight = 1f / Mathf.Max(distances[i], 1e-10f);
            int node = nearest[i];
            result += weight * Vector3.Lerp(velocities[frame * positions.Length + node],
                velocities[next * positions.Length + node], blend);
            weights += weight;
        }
        return result / weights;
    }
}
