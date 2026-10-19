using System;
using UnityEngine;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

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
    [NonSerialized] Vector3[] treePositions, subtreeMin, subtreeMax;
    [NonSerialized] NativeArray<SearchNode> nativeNodes;
    [NonSerialized] NativeArray<int> nativeNearest;
    [NonSerialized] NativeArray<float> nativeDistances;
    [NonSerialized] NativeArray<float3> nativePoints;
    [NonSerialized] int[] batchNearest, batchMap;
    [NonSerialized] float[] batchDistances;

    struct SearchNode
    {
        public float3 position, min, max;
        public int index, left, right, axis;
    }

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.High)]
    struct NearestJob : IJob
    {
        [ReadOnly] public NativeArray<SearchNode> nodes;
        public NativeArray<int> nearest;
        public NativeArray<float> distances;
        [ReadOnly] public NativeArray<float3> points;
        public int root, count;

        public void Execute()
        {
            for (int query = 0; query < count; query++) ExecutePoint(query);
        }
        void ExecutePoint(int query)
        {
            float3 point = points[query];
            int baseIndex = query * 4;
            for (int i = 0; i < 4; i++) { distances[baseIndex + i] = float.PositiveInfinity; nearest[baseIndex + i] = 0; }
            FixedList512Bytes<int> stack = default;
            stack.Add(root);
            while (stack.Length > 0)
            {
                int entry = stack[stack.Length - 1]; stack.RemoveAt(stack.Length - 1);
                SearchNode node = nodes[entry];
                float3 gap = math.max(node.min - point, math.max(0, point - node.max));
                if (math.dot(gap, gap) > distances[baseIndex + 3]) continue;
                float3 offset = node.position - point;
                float d = math.dot(offset, offset);
                if (d < distances[baseIndex + 3])
                {
                    int slot = 3;
                    while (slot > 0 && d < distances[baseIndex + slot - 1])
                    { distances[baseIndex + slot] = distances[baseIndex + slot - 1]; nearest[baseIndex + slot] = nearest[baseIndex + slot - 1]; slot--; }
                    distances[baseIndex + slot] = d; nearest[baseIndex + slot] = node.index;
                }
                float delta = point[node.axis] - node.position[node.axis];
                int near = delta < 0 ? node.left : node.right;
                int far = delta < 0 ? node.right : node.left;
                if (far >= 0 && delta * delta <= distances[baseIndex + 3]) stack.Add(far);
                if (near >= 0) stack.Add(near);
            }
        }
    }
    // Four neighbours, reused on Unity's main thread without per-particle allocations.
    readonly int[] nearest = new int[4];
    readonly float[] distances = new float[4];
    float sampledTime = float.NaN;
    int frameOffset, nextOffset;
    float timeBlend;
    static readonly Unity.Profiling.ProfilerMarker SampleMarker = new("Simulation.RecordedAirflow");

    public void Initialize()
    {
        if (tree != null) return;
        tree = new int[positions.Length];
        treePositions = new Vector3[tree.Length];
        subtreeMin = new Vector3[tree.Length];
        subtreeMax = new Vector3[tree.Length];
        for (int i = 0; i < tree.Length; i++) tree[i] = i;
        Build(0, tree.Length, 0);
        nativeNodes = new NativeArray<SearchNode>(tree.Length, Allocator.Persistent);
        nativeNearest = new NativeArray<int>(4, Allocator.Persistent);
        nativeDistances = new NativeArray<float>(4, Allocator.Persistent);
        nativePoints = new NativeArray<float3>(1, Allocator.Persistent);
        BuildNativeNodes(0, tree.Length, 0);
    }

    void BuildNativeNodes(int start, int count, int depth)
    {
        if (count == 0) return;
        int half = count / 2, mid = start + half, rightCount = count - half - 1;
        Vector3 p = treePositions[mid], min = subtreeMin[mid], max = subtreeMax[mid];
        nativeNodes[mid] = new SearchNode {
            position = new float3(p.x, p.y, p.z), min = new float3(min.x, min.y, min.z), max = new float3(max.x, max.y, max.z),
            index = tree[mid], axis = depth % 3, left = half > 0 ? start + half / 2 : -1,
            right = rightCount > 0 ? mid + 1 + rightCount / 2 : -1
        };
        BuildNativeNodes(start, half, depth + 1);
        BuildNativeNodes(mid + 1, rightCount, depth + 1);
    }

    void OnDisable()
    {
        if (nativeNodes.IsCreated) nativeNodes.Dispose();
        if (nativeNearest.IsCreated) nativeNearest.Dispose();
        if (nativeDistances.IsCreated) nativeDistances.Dispose();
        if (nativePoints.IsCreated) nativePoints.Dispose();
        tree = null;
    }
    void Build(int start, int count, int depth)
    {
        if (count == 0) return;
        int axis = depth % 3;
        if (count > 1)
            Array.Sort(tree, start, count, System.Collections.Generic.Comparer<int>.Create(
                (a,b) => positions[a][axis].CompareTo(positions[b][axis])));
        int half = count / 2;
        Build(start, half, depth + 1);
        Build(start + half + 1, count - half - 1, depth + 1);
        int mid = start + half;
        Vector3 min = positions[tree[mid]], max = min;
        treePositions[mid] = min;
        if (half > 0)
        {
            int left = start + half / 2;
            min = Vector3.Min(min, subtreeMin[left]); max = Vector3.Max(max, subtreeMax[left]);
        }
        int rightCount = count - half - 1;
        if (rightCount > 0)
        {
            int right = mid + 1 + rightCount / 2;
            min = Vector3.Min(min, subtreeMin[right]); max = Vector3.Max(max, subtreeMax[right]);
        }
        subtreeMin[mid] = min; subtreeMax[mid] = max;
    }
    void Search(Vector3 p, int start, int count, int depth)
    {
        if (count == 0) return;
        int half = count / 2, mid = start + half, index = tree[mid];
        // Tight subtree bounds prune empty space that splitting planes cannot,
        // especially around flat walls/floors. Neighbours and weights stay exact.
        Vector3 min = subtreeMin[mid], max = subtreeMax[mid];
        float dx = Mathf.Max(min.x - p.x, Mathf.Max(0, p.x - max.x));
        float dy = Mathf.Max(min.y - p.y, Mathf.Max(0, p.y - max.y));
        float dz = Mathf.Max(min.z - p.z, Mathf.Max(0, p.z - max.z));
        if (dx * dx + dy * dy + dz * dz > distances[3]) return;
        Vector3 node = treePositions[mid];
        float d = (node - p).sqrMagnitude;
        if (d < distances[3])
        {
            int slot = 3;
            while (slot > 0 && d < distances[slot - 1])
            { distances[slot] = distances[slot - 1]; nearest[slot] = nearest[slot - 1]; slot--; }
            distances[slot] = d; nearest[slot] = index;
        }
        float delta = p[depth % 3] - node[depth % 3];
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
        using var profile = SampleMarker.Auto();
        if (!bounds.Contains(p)) return Vector3.zero;
        Initialize();
        for (int i = 0; i < 4; i++) { distances[i] = float.PositiveInfinity; nearest[i] = 0; }
        if (BurstCompiler.IsEnabled && tree.Length > 0)
        {
            nativePoints[0] = new float3(p.x, p.y, p.z);
            new NearestJob { nodes = nativeNodes, nearest = nativeNearest, distances = nativeDistances,
                points = nativePoints, count = 1, root = tree.Length / 2 }.Run();
            for (int i = 0; i < 4; i++) { nearest[i] = nativeNearest[i]; distances[i] = nativeDistances[i]; }
        }
        else Search(p, 0, tree.Length, 0);
        if (distances[0] > supportRadius * supportRadius) return Vector3.zero;
        UpdateSampleTime(time);
        return Blend(nearest, distances, 0);
    }

    void UpdateSampleTime(float time)
    {
        if (sampledTime != time)
        {
            sampledTime = time;
            int frame = Array.BinarySearch(times, time);
            if (frame < 0) frame = Math.Max(0, ~frame - 1);
            frame = Math.Min(frame, times.Length - 1);
            int next = Math.Min(frame + 1, times.Length - 1);
            timeBlend = next == frame ? 0 : Mathf.InverseLerp(times[frame], times[next], time);
            frameOffset = frame * positions.Length;
            nextOffset = next * positions.Length;
        }
    }

    Vector3 Blend(int[] indices, float[] squaredDistances, int offset)
    {
        Vector3 result = Vector3.zero;
        float weights = 0;
        for (int i = 0; i < 4; i++)
        {
            float weight = 1f / Mathf.Max(squaredDistances[offset + i], 1e-10f);
            int node = indices[offset + i];
            result += weight * Vector3.Lerp(velocities[frameOffset + node],
                velocities[nextOffset + node], timeBlend);
            weights += weight;
        }
        return result / weights;
    }

    // One native call per emitter, rather than per particle. Buffers are shared on
    // the main thread and reused; each particle retains its own exact neighbours.
    public void SampleBatch(Vector3[] points, Vector3[] results, int count, float time, float supportRadius)
    {
        if (!BurstCompiler.IsEnabled)
        {
            for (int i = 0; i < count; i++) results[i] = Sample(points[i], time, supportRadius);
            return;
        }
        using var profile = SampleMarker.Auto();
        Initialize();
        if (count > nativePoints.Length)
        {
            int capacity = Mathf.Max(count, nativePoints.Length * 2);
            nativePoints.Dispose(); nativeNearest.Dispose(); nativeDistances.Dispose();
            nativePoints = new NativeArray<float3>(capacity, Allocator.Persistent);
            nativeNearest = new NativeArray<int>(capacity * 4, Allocator.Persistent);
            nativeDistances = new NativeArray<float>(capacity * 4, Allocator.Persistent);
        }
        if (batchMap == null || batchMap.Length < count)
        {
            int capacity = nativePoints.Length;
            batchMap = new int[capacity]; batchNearest = new int[capacity * 4]; batchDistances = new float[capacity * 4];
        }
        int supported = 0;
        for (int i = 0; i < count; i++)
        {
            results[i] = Vector3.zero;
            if (!bounds.Contains(points[i]) || tree.Length == 0) continue;
            Vector3 p = points[i];
            batchMap[supported] = i;
            nativePoints[supported++] = new float3(p.x, p.y, p.z);
        }
        if (supported == 0) return;
        new NearestJob { nodes = nativeNodes, nearest = nativeNearest, distances = nativeDistances,
            points = nativePoints, count = supported, root = tree.Length / 2 }.Run();
        NativeArray<int>.Copy(nativeNearest, 0, batchNearest, 0, supported * 4);
        NativeArray<float>.Copy(nativeDistances, 0, batchDistances, 0, supported * 4);
        UpdateSampleTime(time);
        for (int query = 0; query < supported; query++)
            if (batchDistances[query * 4] <= supportRadius * supportRadius)
                results[batchMap[query]] = Blend(batchNearest, batchDistances, query * 4);
    }
}
