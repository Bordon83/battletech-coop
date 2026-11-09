using System;
using System.Collections.Generic;
using System.Linq;

namespace BattletechCoop.Support
{
    // Data contract for a future PC preparation panel. No game widget types leak into the core.
    public interface IMissionPreparation
    {
        PreparationResult GetPreparation(string peerId, string missionId);
        SupportResult Prepare(string peerId, string requestId, long revision, string missionId,
            IReadOnlyList<Selection> selections);
        SupportResult Launch(string peerId, string requestId, long revision, string missionId);
    }

    public sealed class PreparationOption
    {
        public SupportDefinition Definition { get; }
        public long UnitCBills { get; }
        public int CompanyStock { get; }
        public Failure Availability { get; }
        // Individual ceiling, not a promise that every option fits together in one deck.
        // Target validation is deferred until the player actually selects a map position.
        public int MaxUsesIfSelectedAlone { get; }
        internal PreparationOption(SupportDefinition definition, long price, int stock, Failure availability, int maxUses)
        { Definition = definition; UnitCBills = price; CompanyStock = stock; Availability = availability; MaxUsesIfSelectedAlone = maxUses; }
    }

    public sealed class PreparationView
    {
        public string MissionId { get; }
        public long Revision { get; }
        public long CompanyCBills { get; }
        public long ReservedCBills { get; }
        public long AvailableCBills { get; }
        public int PreparationSlots { get; }
        public int MaxUses { get; }
        public MissionPlan CurrentPlan { get; }
        public IReadOnlyList<PreparationOption> Options { get; }
        internal PreparationView(MissionRules rules, CareerProfile career, CompanySupportState state,
            IEnumerable<PreparationOption> options)
        {
            MissionId = rules.MissionId; Revision = state.Revision; CompanyCBills = state.CBills;
            AvailableCBills = state.AvailableCBills;
            ReservedCBills = state.Mission == null ? 0 : state.Mission.ReservedCBills;
            PreparationSlots = Math.Min(rules.PreparationSlots, career.PreparationSlots);
            MaxUses = Math.Min(rules.MaxUses, career.MissionUses);
            CurrentPlan = state.Mission != null && state.Mission.Phase == MissionPhase.Preparing ? state.Mission : null;
            Options = Array.AsReadOnly(options.ToArray());
        }
    }

    public sealed class PreparationResult
    {
        public Failure Failure { get; }
        public PreparationView View { get; }
        internal PreparationResult(Failure failure, PreparationView view = null) { Failure = failure; View = view; }
    }
}
