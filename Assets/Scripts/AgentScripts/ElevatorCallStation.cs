using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;



/// <summary>
/// Placed on the waiting-node in front of the elevator on each floor.
/// Agents navigate here, register as waiting, and board when the elevator arrives.
/// </summary>
public class ElevatorCallStation : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Tooltip("Which floor index this station belongs to (matches ElevatorController.floorYPositions).")]
    public int floorIndex = 0;

    [Tooltip("Reference to the shared ElevatorController in the scene.")]
    public ElevatorController elevatorController;

    [Tooltip("Maximum agents allowed to queue at this station. Agents beyond this take the stairs instead.")]
    public int maxWaitingAgents = 8;
    [Min(0.6f)] public float queueSpacing = 1.1f;
    [Min(5f)] public float maximumWaitSeconds = 45f;
    public int WaitingCount => _waitingAgents.Count;
    public int ReservedCount => _reservations.Count;
    // ── State ─────────────────────────────────────────────────────────────────

    // Agents currently waiting for the elevator on this floor
    private readonly List<NavigationAgent> _waitingAgents = new();
    private readonly Dictionary<NavigationAgent, Reservation> _reservations = new();
    private readonly List<NavigationAgent> _expired = new();
    private float _nextCleanup;
    private struct Reservation
    {
        public Vector3 position;
        public float expires;
    }

    // Reserve before walking here, so a whole class cannot claim the same
    // last seat or all steer at the landing's exact centre.
    public bool TryReserveAgent(NavigationAgent agent, int destinationFloor, out Vector3 position)
    {
        position = transform.position;
        if (agent == null || destinationFloor == floorIndex || !IsElevatorViable() ||
            !elevatorController.IsValidFloor(destinationFloor)) return false;
        if (_reservations.TryGetValue(agent, out var existing)) { position = existing.position; return true; }
        if (!NavMesh.SamplePosition(transform.position, out var center, 1f, NavMesh.AllAreas)) return false;
        float spacing = Mathf.Max(queueSpacing, agent.GetComponent<NavMeshAgent>().radius * 2f + 0.25f);
        for (int slot = 0; slot < maxWaitingAgents * 4; slot++)
        {
            int row = slot / 3 + 1;
            int column = slot % 3 - 1;
            Vector3 candidate = center.position + transform.forward * (row * spacing) + transform.right * (column * spacing);
            if (!NavMesh.SamplePosition(candidate, out var hit, 0.3f, NavMesh.AllAreas) ||
                Mathf.Abs(hit.position.y - center.position.y) > 0.3f ||
                NavMesh.Raycast(center.position, hit.position, out _, NavMesh.AllAreas)) continue;
            bool occupied = false;
            foreach (var reservation in _reservations.Values)
                if ((reservation.position - hit.position).sqrMagnitude < spacing * spacing * 0.9f) { occupied = true; break; }
            if (occupied) continue;
            var path = new NavMeshPath();
            var nav = agent.GetComponent<NavMeshAgent>();
            if (!nav.CalculatePath(hit.position, path) || path.status != NavMeshPathStatus.PathComplete) continue;
            position = hit.position;
            _reservations.Add(agent, new Reservation { position = position, expires = Time.time + maximumWaitSeconds });
            return true;
        }
        return false;
    }

    public void RemoveAgent(NavigationAgent agent)
    {
        _reservations.Remove(agent);
        _waitingAgents.Remove(agent);
    }

    private void Update()
    {
        if (Time.time < _nextCleanup) return;
        _nextCleanup = Time.time + 0.5f;
        _expired.Clear();
        foreach (var entry in _reservations)
            if (entry.Key == null || !entry.Key.HasElevatorReservation(this) || Time.time >= entry.Value.expires ||
                elevatorController == null || !elevatorController.isActiveAndEnabled)
                _expired.Add(entry.Key);
        foreach (var agent in _expired)
        {
            RemoveAgent(agent);
            if (agent != null) agent.CancelElevatorWait(this);
        }
    }

    private void OnDisable()
    {
        _expired.Clear();
        _expired.AddRange(_reservations.Keys);
        foreach (var agent in _expired)
        {
            RemoveAgent(agent);
            if (agent != null) agent.CancelElevatorWait(this);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Hard check used both before an agent starts walking here AND on arrival.
    /// Returns true only when the queue genuinely has room right now.
    /// </summary>
    public bool IsElevatorViable()
    {
        if (elevatorController == null)
        {
            Debug.LogError($"[{name}] ElevatorCallStation has no ElevatorController assigned! " +
                            "This station will never be usable.", this);
            return false;
        }
        return isActiveAndEnabled && elevatorController.isActiveAndEnabled && elevatorController.IsValidFloor(floorIndex) &&
            !elevatorController.IsFull && _reservations.Count < maxWaitingAgents;
    }

    /// <summary>
    /// Called by NavigationAgent on arrival. Returns true if the agent was
    /// accepted into the queue, false if it should reroute to stairs instead.
    /// </summary>
    public bool TryRegisterWaitingAgent(NavigationAgent agent, int destinationFloor)
    {
        if (agent == null || elevatorController == null || destinationFloor == floorIndex ||
            !elevatorController.IsValidFloor(destinationFloor)) return false;
        if (_waitingAgents.Contains(agent)) return true;
        // Capacity was claimed at departure. A full car can return for a
        // reserved passenger instead of rejecting them on arrival.
        if (!_reservations.ContainsKey(agent)) return false;

        _waitingAgents.Add(agent);
        if (elevatorController.CanBoardAt(floorIndex)) OnElevatorArrived();
        else elevatorController.RequestFloor(floorIndex);
        return true;
    }

    /// <summary>
    /// Called by ElevatorController when the cage arrives and doors are open.
    /// Drains the waiting queue up to the elevator's remaining capacity.
    /// </summary>
    public void OnElevatorArrived()
    {
        if (elevatorController == null || !elevatorController.CanBoardAt(floorIndex)) return;
        while (_waitingAgents.Count > 0 && !elevatorController.IsFull)
        {
            NavigationAgent agent = _waitingAgents[0];
            _waitingAgents.RemoveAt(0);
            if (agent == null || !agent.HasElevatorReservation(this))
            {
                _reservations.Remove(agent);
                continue;
            }
            _reservations.Remove(agent);
            agent.BoardElevator(elevatorController);
        }
    }
}
