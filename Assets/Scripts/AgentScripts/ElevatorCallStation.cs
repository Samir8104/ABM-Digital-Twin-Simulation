using System.Collections.Generic;
using UnityEngine;



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
    public int WaitingCount => _waitingAgents.Count;
    // ── State ─────────────────────────────────────────────────────────────────

    // Agents currently waiting for the elevator on this floor
    private readonly Queue<NavigationAgent> _waitingAgents = new();

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
        return !elevatorController.IsFull && _waitingAgents.Count < maxWaitingAgents;
    }

    /// <summary>
    /// Called by NavigationAgent on arrival. Returns true if the agent was
    /// accepted into the queue, false if it should reroute to stairs instead.
    /// </summary>
    public bool TryRegisterWaitingAgent(NavigationAgent agent, int destinationFloor)
    {
        // Second gate — queue may have filled while the agent was walking here
        if (!IsElevatorViable())
            return false;

        _waitingAgents.Enqueue(agent);
        elevatorController.RequestFloor(floorIndex);
        elevatorController.RequestFloor(destinationFloor);
        return true;
    }

    /// <summary>
    /// Called by ElevatorController when the cage arrives and doors are open.
    /// Drains the waiting queue up to the elevator's remaining capacity.
    /// </summary>
    public void OnElevatorArrived()
    {
        while (_waitingAgents.Count > 0 && !elevatorController.IsFull)
        {
            NavigationAgent agent = _waitingAgents.Dequeue();
            agent.BoardElevator(elevatorController);
        }
    }
}