using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BattletechCoop.Support
{
    public sealed class AcceptedRequest
    {
        public string PeerId { get; }
        public string RequestId { get; }
        public string Fingerprint { get; }
        public long Revision { get; }
        public string OrderId { get; }
        internal AcceptedRequest(string peerId, string requestId, string fingerprint, long revision, string orderId)
        { PeerId = peerId; RequestId = requestId; Fingerprint = fingerprint; Revision = revision; OrderId = orderId; }
    }

    // Durable outbox entry. The future effect adapter must deduplicate OrderId across save/reload.
    public sealed class SupportOrder
    {
        public string OrderId { get; }
        public string MissionId { get; }
        public string SupportId { get; }
        public string PeerId { get; }
        public SupportTarget Target { get; }
        public long ChargedCBills { get; }
        internal SupportOrder(string id, string mission, string support, string peer, SupportTarget target, long cost)
        { OrderId = id; MissionId = mission; SupportId = support; PeerId = peer; Target = target; ChargedCBills = cost; }
    }

    public sealed class CompanySupportState
    {
        public const int SchemaVersion = 1;
        public long Revision { get; }
        public long CBills { get; }
        public long AvailableCBills { get { return CBills - (Mission == null ? 0 : Mission.ReservedCBills); } }
        public IReadOnlyDictionary<string, int> Stock { get; }
        public MissionPlan Mission { get; }
        public IReadOnlyList<string> ClosedMissions { get; }
        public IReadOnlyDictionary<string, AcceptedRequest> Requests { get; }
        public IReadOnlyList<SupportOrder> Orders { get; }

        public CompanySupportState(long cbills, IDictionary<string, int> stock)
            : this(0, cbills, stock, null, new string[0], new Dictionary<string, AcceptedRequest>(), new SupportOrder[0]) { }

        internal CompanySupportState(long revision, long cbills, IDictionary<string, int> stock, MissionPlan mission,
            IEnumerable<string> closed, IDictionary<string, AcceptedRequest> requests, IEnumerable<SupportOrder> orders)
        {
            if (cbills < 0 || revision < 0) throw new ArgumentOutOfRangeException("state");
            Revision = revision; CBills = cbills; Stock = Guard.Counts(stock); Mission = mission;
            ClosedMissions = Array.AsReadOnly(closed.ToArray());
            Requests = new ReadOnlyDictionary<string, AcceptedRequest>(new Dictionary<string, AcceptedRequest>(requests));
            Orders = Array.AsReadOnly(orders.ToArray());
        }

        // Trusted store adapters can refresh economy in the same transaction as Career changes.
        // A lower balance may invalidate an existing reservation; launch/use will reject it.
        public CompanySupportState WithEconomy(long cbills, IDictionary<string, int> stock)
        {
            return new CompanySupportState(checked(Revision + 1), cbills, stock, Mission,
                ClosedMissions, Requests.ToDictionary(p => p.Key, p => p.Value), Orders);
        }
    }

    // Headless/prototype store only. This is not a game-save persistence implementation.
    public sealed class MemorySupportStore : IHostSupportStore
    {
        private readonly object gate = new object();
        private CompanySupportState state;
        public MemorySupportStore(CompanySupportState initial)
        { state = initial ?? throw new ArgumentNullException("initial"); }
        public CompanySupportState Read() { lock (gate) return state; }
        public bool TryCommit(long expectedRevision, CompanySupportState next)
        {
            if (next == null) throw new ArgumentNullException("next");
            lock (gate)
            {
                if (state.Revision != expectedRevision) return false;
                if (next.Revision != checked(expectedRevision + 1)) throw new ArgumentException("Invalid revision");
                state = next; return true;
            }
        }
    }

    public sealed class SupportResult
    {
        public Failure Failure { get; }
        public bool Accepted { get { return Failure == Failure.None; } }
        public bool Replayed { get; }
        public long Revision { get; }
        public string OrderId { get; }
        internal SupportResult(Failure failure, long revision, string orderId = null, bool replayed = false)
        { Failure = failure; Revision = revision; OrderId = orderId; Replayed = replayed; }
    }
}
