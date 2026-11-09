using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BattletechCoop.Support
{
    // Host-only application service. No Unity, Harmony, transport, save or game API references.
    public sealed class HostSupportService : IMissionPreparation
    {
        public const int MaxSelections = 64;
        public const int MaxAcceptedRequests = 4096;
        private readonly IHostSupportStore store;
        private readonly IHostEnvironment environment;
        private readonly SupportCatalog catalog;
        private readonly CostPolicy costs;
        private readonly ITargetPolicy targets;
        private enum Operation { Prepare, Launch, Use, Close }

        public HostSupportService(IHostSupportStore store, IHostEnvironment environment,
            SupportCatalog catalog, CostPolicy costs = null, ITargetPolicy targets = null)
        {
            this.store = store ?? throw new ArgumentNullException("store");
            this.environment = environment ?? throw new ArgumentNullException("environment");
            this.catalog = catalog ?? throw new ArgumentNullException("catalog");
            this.costs = costs ?? new CostPolicy();
            this.targets = targets ?? new UnsupportedTargets();
        }

        public PreparationResult GetPreparation(string peerId, string missionId)
        {
            if (!environment.IsHost) return new PreparationResult(Failure.NotHost);
            if (peerId == null || !environment.IsParticipant(peerId)) return new PreparationResult(Failure.Unauthorized);
            try { Guard.Id(peerId); Guard.Id(missionId); }
            catch (ArgumentException) { return new PreparationResult(Failure.InvalidRequest); }
            var state = store.Read();
            var rules = environment.GetMission(missionId);
            if (rules == null || rules.MissionId != missionId || state.ClosedMissions.Contains(missionId))
                return new PreparationResult(Failure.WrongMission);
            if (state.Mission != null && state.Mission.Phase != MissionPhase.Closed)
            {
                if (state.Mission.MissionId != missionId) return new PreparationResult(Failure.WrongMission);
                if (state.Mission.Phase != MissionPhase.Preparing) return new PreparationResult(Failure.WrongPhase);
            }
            var career = environment.Career;
            if (career == null) return new PreparationResult(Failure.Unavailable);
            var options = new List<PreparationOption>();
            foreach (var definition in catalog.Definitions.Values.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                int stock;
                if (!state.Stock.TryGetValue(definition.Id, out stock)) stock = 0;
                long price;
                try { price = costs.UnitCost(definition); }
                catch (OverflowException)
                {
                    options.Add(new PreparationOption(definition, 0, stock, Failure.InvalidRequest, 0));
                    continue;
                }
                var maxUses = Math.Min(definition.MaxUsesPerMission, Math.Min(stock,
                    Math.Min(Math.Min(rules.MaxUses, career.MissionUses),
                        Math.Min(rules.PreparationSlots, career.PreparationSlots) / definition.SlotsPerUse)));
                maxUses = (int)Math.Min(maxUses, state.CBills / price);
                var availability = !career.Allows(definition) || !rules.AllowedKinds.Contains(definition.Kind)
                    ? Failure.Unavailable : stock == 0 ? Failure.InsufficientStock
                    : state.CBills < price ? Failure.InsufficientFunds : maxUses == 0 ? Failure.LimitExceeded : Failure.None;
                options.Add(new PreparationOption(definition, price, stock, availability,
                    availability == Failure.None ? maxUses : 0));
            }
            return new PreparationResult(Failure.None, new PreparationView(rules, career, state, options));
        }

        // Replaces the shared preparation atomically. Concurrent edits must use the latest revision.
        public SupportResult Prepare(string peerId, string requestId, long revision, string missionId,
            IReadOnlyList<Selection> selections)
        { return Execute(peerId, requestId, revision, missionId, Operation.Prepare, selections, null, null); }

        public SupportResult Launch(string peerId, string requestId, long revision, string missionId)
        { return Execute(peerId, requestId, revision, missionId, Operation.Launch, null, null, null); }

        // Accepts a paid accounting order into the outbox. It does NOT execute a game effect.
        public SupportResult RequestUse(string peerId, string requestId, long revision, string missionId,
            string supportId, SupportTarget target)
        { return Execute(peerId, requestId, revision, missionId, Operation.Use, null, supportId, target); }

        // Cancellation before launch and end/abort after launch both release unused reservations.
        // The future adapter must settle accepted effect orders before closing the mission.
        public SupportResult Close(string peerId, string requestId, long revision, string missionId)
        { return Execute(peerId, requestId, revision, missionId, Operation.Close, null, null, null); }

        private SupportResult Execute(string peer, string request, long expected, string missionId,
            Operation operation, IReadOnlyList<Selection> input, string supportId, SupportTarget target)
        {
            var state = store.Read();
            if (!environment.IsHost) return Reject(Failure.NotHost, state);
            if (peer == null || !environment.IsParticipant(peer)) return Reject(Failure.Unauthorized, state);
            if ((operation == Operation.Launch || operation == Operation.Close) && !environment.CanManageMission(peer))
                return Reject(Failure.Unauthorized, state);
            try
            {
                Guard.Id(peer); Guard.Id(request); Guard.Id(missionId);
                if (expected < 0) return Reject(Failure.InvalidRequest, state);
                Selection[] selections = new Selection[0];
                if (operation == Operation.Prepare)
                {
                    if (input == null || input.Count > MaxSelections) return Reject(Failure.InvalidRequest, state);
                    selections = input.ToArray();
                    if (selections.Any(s => s == null) ||
                        selections.Select(s => s.SupportId).Distinct(StringComparer.Ordinal).Count() != selections.Length)
                        return Reject(Failure.InvalidRequest, state);
                    selections = selections.OrderBy(s => s.SupportId, StringComparer.Ordinal).ToArray();
                }
                if (operation == Operation.Use) Guard.Id(supportId);
                var fingerprint = Hash(w => {
                    w.Write((int)operation); w.Write(missionId); w.Write(supportId ?? ""); WriteTarget(w, target);
                    w.Write(selections.Length);
                    foreach (var s in selections) { w.Write(s.SupportId); w.Write(s.Uses); WriteTarget(w, s.PredeploymentTarget); }
                });
                var key = peer + "\n" + request;
                AcceptedRequest prior;
                if (state.Requests.TryGetValue(key, out prior))
                    return prior.Fingerprint == fingerprint
                        ? new SupportResult(Failure.None, prior.Revision, prior.OrderId, true)
                        : Reject(Failure.RequestConflict, state);
                if (expected != state.Revision) return Reject(Failure.StaleRevision, state);
                // Keep the final receipt slot for cancellation/mission end, so a full history
                // cannot trap an active reservation. Compaction is a future durable-store feature.
                if (state.Requests.Count >= MaxAcceptedRequests ||
                    (state.Requests.Count == MaxAcceptedRequests - 1 && operation != Operation.Close))
                    return Reject(Failure.HistoryFull, state);

                var plan = state.Mission;
                var stock = state.Stock.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
                var balance = state.CBills;
                var closed = state.ClosedMissions.ToList();
                var orders = state.Orders.ToList();
                string orderId = null;
                if (operation == Operation.Close)
                {
                    if (plan == null || plan.MissionId != missionId) return Reject(Failure.WrongMission, state);
                    if (plan.Phase == MissionPhase.Closed) return Reject(Failure.WrongPhase, state);
                    plan = new MissionPlan(missionId, MissionPhase.Closed, plan.Lines);
                    closed.Add(missionId);
                }
                else
                {
                    var rules = environment.GetMission(missionId);
                    if (rules == null || rules.MissionId != missionId) return Reject(Failure.WrongMission, state);
                    var career = environment.Career;
                    if (career == null) return Reject(Failure.Unavailable, state);
                    if (operation == Operation.Prepare)
                    {
                        if (closed.Contains(missionId)) return Reject(Failure.WrongMission, state);
                        if (plan != null && plan.Phase != MissionPhase.Closed && plan.MissionId != missionId)
                            return Reject(Failure.WrongMission, state);
                        if (plan != null && plan.MissionId == missionId && plan.Phase != MissionPhase.Preparing)
                            return Reject(Failure.WrongPhase, state);
                        var lines = new List<PreparedLine>();
                        foreach (var selection in selections)
                        {
                            SupportDefinition definition;
                            if (!catalog.Definitions.TryGetValue(selection.SupportId, out definition))
                                return Reject(Failure.Unavailable, state);
                            lines.Add(new PreparedLine(selection, costs.UnitCost(definition)));
                        }
                        plan = new MissionPlan(missionId, MissionPhase.Preparing, lines);
                    }
                    else
                    {
                        if (plan == null || plan.MissionId != missionId) return Reject(Failure.WrongMission, state);
                        var requiredPhase = operation == Operation.Launch ? MissionPhase.Preparing : MissionPhase.Deployed;
                        if (plan.Phase != requiredPhase) return Reject(Failure.WrongPhase, state);
                    }
                    var error = ValidatePlan(plan, rules, career, state, operation != Operation.Use);
                    if (error != Failure.None) return Reject(error, state);
                    if (operation == Operation.Launch)
                        plan = new MissionPlan(missionId, MissionPhase.Deployed, plan.Lines);
                    else if (operation == Operation.Use)
                    {
                        var line = plan.Lines.FirstOrDefault(l => l.Selection.SupportId == supportId);
                        if (line == null) return Reject(Failure.Unavailable, state);
                        if (line.Remaining == 0) return Reject(Failure.LimitExceeded, state);
                        var definition = catalog.Definitions[supportId];
                        if (target == null || target.Shape != definition.TargetShape ||
                            (line.Selection.PredeploymentTarget != null && !line.Selection.PredeploymentTarget.Equals(target)))
                            return Reject(Failure.InvalidTarget, state);
                        if (!targets.Validate(rules, definition, target, TargetCheck.Use))
                            return Reject(Failure.InvalidTarget, state);
                        balance = checked(balance - line.UnitCBills);
                        stock[supportId]--;
                        var nextLines = plan.Lines.Select(l => ReferenceEquals(l, line)
                            ? new PreparedLine(l.Selection, l.UnitCBills, l.Used + 1) : l);
                        plan = new MissionPlan(missionId, MissionPhase.Deployed, nextLines);
                        orderId = "support-" + Hash(w => { w.Write(missionId); w.Write(peer); w.Write(request); });
                        orders.Add(new SupportOrder(orderId, missionId, supportId, peer, target, line.UnitCBills));
                    }
                }
                var nextRevision = checked(state.Revision + 1);
                var requests = state.Requests.ToDictionary(p => p.Key, p => p.Value);
                requests.Add(key, new AcceptedRequest(peer, request, fingerprint, nextRevision, orderId));
                var next = new CompanySupportState(nextRevision, balance, stock, plan, closed, requests, orders);
                if (!store.TryCommit(state.Revision, next)) return Reject(Failure.StaleRevision, store.Read());
                return new SupportResult(Failure.None, nextRevision, orderId);
            }
            catch (ArgumentException) { return Reject(Failure.InvalidRequest, state); }
            catch (OverflowException) { return Reject(Failure.InvalidRequest, state); }
        }

        private Failure ValidatePlan(MissionPlan plan, MissionRules rules, CareerProfile career, CompanySupportState state,
            bool validatePreparationTargets)
        {
            long uses = 0, slots = 0;
            foreach (var line in plan.Lines)
            {
                SupportDefinition definition;
                if (!catalog.Definitions.TryGetValue(line.Selection.SupportId, out definition) ||
                    !career.Allows(definition) || !rules.AllowedKinds.Contains(definition.Kind)) return Failure.Unavailable;
                if (line.Selection.Uses > definition.MaxUsesPerMission) return Failure.LimitExceeded;
                uses = checked(uses + line.Selection.Uses);
                slots = checked(slots + checked((long)line.Selection.Uses * definition.SlotsPerUse));
                int stock;
                if (!state.Stock.TryGetValue(definition.Id, out stock)) stock = 0;
                if (stock < line.Remaining) return Failure.InsufficientStock;
                var target = line.Selection.PredeploymentTarget;
                if (target == null && definition.RequiresPredeploymentTarget) return Failure.InvalidTarget;
                if (target != null && (target.Shape != definition.TargetShape ||
                    (validatePreparationTargets && !targets.Validate(rules, definition, target, TargetCheck.Preparation))))
                    return Failure.InvalidTarget;
            }
            if (uses > Math.Min(rules.MaxUses, career.MissionUses) ||
                slots > Math.Min(rules.PreparationSlots, career.PreparationSlots)) return Failure.LimitExceeded;
            if (plan.ReservedCBills > state.CBills) return Failure.InsufficientFunds;
            return Failure.None;
        }

        private static SupportResult Reject(Failure failure, CompanySupportState state)
        { return new SupportResult(failure, state.Revision); }

        private static void WriteTarget(BinaryWriter writer, SupportTarget target)
        {
            writer.Write(target != null);
            if (target == null) return;
            writer.Write((int)target.Shape); writer.Write(target.X); writer.Write(target.Z);
            writer.Write(target.EndX); writer.Write(target.EndZ);
        }
        private static string Hash(Action<BinaryWriter> write)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            using (var sha = SHA256.Create())
            {
                write(writer); writer.Flush();
                return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
            }
        }
    }
}
