using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace BattletechCoop.Support
{
    public enum SupportKind { Artillery, Minefield, AirStrike, AirCover }
    public enum TargetShape { None, Point, Line, Area }
    public enum MissionPhase { Preparing, Deployed, Closed }
    public enum TargetCheck { Preparation, Use }
    public enum Failure
    {
        None, NotHost, Unauthorized, InvalidRequest, StaleRevision, RequestConflict,
        HistoryFull, WrongMission, WrongPhase, Unavailable, LimitExceeded,
        InsufficientStock, InsufficientFunds, InvalidTarget, Unsupported
    }

    internal static class Guard
    {
        public static string Id(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 96 ||
                value.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid identifier");
            return value;
        }
        public static IReadOnlyDictionary<string, int> Counts(IDictionary<string, int> source)
        {
            if (source == null) throw new ArgumentNullException("source");
            var copy = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in source)
            {
                Id(pair.Key);
                if (pair.Value < 0) throw new ArgumentOutOfRangeException("source");
                copy.Add(pair.Key, pair.Value);
            }
            return new ReadOnlyDictionary<string, int>(copy);
        }
    }

    // Coordinates are opaque integer map units. Only the verified game adapter can interpret them.
    public sealed class SupportTarget : IEquatable<SupportTarget>
    {
        public TargetShape Shape { get; }
        public int X { get; }
        public int Z { get; }
        public int EndX { get; }
        public int EndZ { get; }
        public SupportTarget(TargetShape shape, int x = 0, int z = 0, int endX = 0, int endZ = 0)
        {
            if (!Enum.IsDefined(typeof(TargetShape), shape)) throw new ArgumentOutOfRangeException("shape");
            if (shape == TargetShape.Line && x == endX && z == endZ)
                throw new ArgumentException("A line needs two distinct points");
            if (shape != TargetShape.Line && (endX != 0 || endZ != 0))
                throw new ArgumentException("Only lines have an endpoint");
            if (shape == TargetShape.None && (x != 0 || z != 0))
                throw new ArgumentException("Untargeted support has no position");
            Shape = shape; X = x; Z = z; EndX = endX; EndZ = endZ;
        }
        public bool Equals(SupportTarget other)
        {
            return other != null && Shape == other.Shape && X == other.X && Z == other.Z &&
                EndX == other.EndX && EndZ == other.EndZ;
        }
        public override bool Equals(object obj) { return Equals(obj as SupportTarget); }
        public override int GetHashCode() { return (int)Shape ^ X ^ Z ^ EndX ^ EndZ; }
    }

    public sealed class SupportDefinition
    {
        public string Id { get; }
        public string DisplayName { get; }
        public SupportKind Kind { get; }
        public TargetShape TargetShape { get; }
        public long BaseCBills { get; }
        public int MinimumContracts { get; }
        public string Contact { get; }
        public int ContactRank { get; }
        public string Equipment { get; }
        public int SlotsPerUse { get; }
        public int MaxUsesPerMission { get; }
        public bool RequiresPredeploymentTarget { get; }
        public SupportDefinition(string id, string displayName, SupportKind kind, TargetShape targetShape,
            long baseCBills, int minimumContracts, string contact, int contactRank,
            string equipment, int slotsPerUse, int maxUsesPerMission, bool requiresPredeploymentTarget = false)
        {
            Id = Guard.Id(id);
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Missing display name");
            if (!Enum.IsDefined(typeof(SupportKind), kind) || !Enum.IsDefined(typeof(TargetShape), targetShape))
                throw new ArgumentException("Unknown support or target kind");
            if (baseCBills <= 0 || minimumContracts < 0 || contactRank < 0 || contactRank > 5 ||
                slotsPerUse <= 0 || maxUsesPerMission <= 0) throw new ArgumentOutOfRangeException("definition");
            if (contactRank > 0 && contact == null) throw new ArgumentException("Rank requires a contact");
            if (requiresPredeploymentTarget && targetShape == TargetShape.None)
                throw new ArgumentException("Predeployment support needs a target shape");
            DisplayName = displayName; Kind = kind; TargetShape = targetShape; BaseCBills = baseCBills;
            MinimumContracts = minimumContracts; Contact = contact == null ? null : Guard.Id(contact);
            ContactRank = contactRank; Equipment = equipment == null ? null : Guard.Id(equipment);
            SlotsPerUse = slotsPerUse; MaxUsesPerMission = maxUsesPerMission;
            RequiresPredeploymentTarget = requiresPredeploymentTarget;
        }
    }

    public sealed class CareerProfile
    {
        public int CompletedContracts { get; }
        public IReadOnlyDictionary<string, int> Contacts { get; }
        public IReadOnlyCollection<string> Equipment { get; }
        // Mod balancing, not tabletop values or assumed game progression APIs.
        public int PreparationSlots { get { return 2 + Math.Min(4, CompletedContracts / 5); } }
        public int MissionUses { get { return 2 + Math.Min(4, CompletedContracts / 10); } }
        public CareerProfile(int completedContracts, IDictionary<string, int> contacts, IEnumerable<string> equipment)
        {
            if (completedContracts < 0) throw new ArgumentOutOfRangeException("completedContracts");
            CompletedContracts = completedContracts; Contacts = Guard.Counts(contacts);
            if (Contacts.Values.Any(v => v > 5)) throw new ArgumentOutOfRangeException("contacts");
            if (equipment == null) throw new ArgumentNullException("equipment");
            Equipment = Array.AsReadOnly(equipment.Select(Guard.Id).Distinct(StringComparer.Ordinal).ToArray());
        }
        public bool Allows(SupportDefinition d)
        {
            int rank;
            return CompletedContracts >= d.MinimumContracts &&
                (d.Contact == null || (Contacts.TryGetValue(d.Contact, out rank) && rank >= d.ContactRank)) &&
                (d.Equipment == null || Equipment.Contains(d.Equipment));
        }
    }

    public sealed class MissionRules
    {
        public string MissionId { get; }
        public int PreparationSlots { get; }
        public int MaxUses { get; }
        public IReadOnlyCollection<SupportKind> AllowedKinds { get; }
        public MissionRules(string missionId, int preparationSlots, int maxUses, IEnumerable<SupportKind> allowedKinds)
        {
            MissionId = Guard.Id(missionId);
            if (preparationSlots < 0 || maxUses < 0) throw new ArgumentOutOfRangeException("limits");
            if (allowedKinds == null) throw new ArgumentNullException("allowedKinds");
            var kinds = allowedKinds.Distinct().ToArray();
            if (kinds.Any(k => !Enum.IsDefined(typeof(SupportKind), k))) throw new ArgumentException("Unknown kind");
            PreparationSlots = preparationSlots; MaxUses = maxUses;
            AllowedKinds = Array.AsReadOnly(kinds);
        }
    }

    public sealed class Selection
    {
        public string SupportId { get; }
        public int Uses { get; }
        public SupportTarget PredeploymentTarget { get; }
        public Selection(string supportId, int uses, SupportTarget predeploymentTarget = null)
        {
            SupportId = Guard.Id(supportId);
            if (uses <= 0) throw new ArgumentOutOfRangeException("uses");
            Uses = uses; PredeploymentTarget = predeploymentTarget;
        }
    }

    public sealed class PreparedLine
    {
        public Selection Selection { get; }
        public long UnitCBills { get; }
        public int Used { get; }
        public int Remaining { get { return Selection.Uses - Used; } }
        internal PreparedLine(Selection selection, long unitCBills, int used = 0)
        { Selection = selection; UnitCBills = unitCBills; Used = used; }
    }

    public sealed class MissionPlan
    {
        public string MissionId { get; }
        public MissionPhase Phase { get; }
        public IReadOnlyList<PreparedLine> Lines { get; }
        public long ReservedCBills { get; }
        public int Used { get { return Lines.Sum(l => l.Used); } }
        internal MissionPlan(string missionId, MissionPhase phase, IEnumerable<PreparedLine> lines)
        {
            MissionId = missionId; Phase = phase; Lines = Array.AsReadOnly(lines.ToArray());
            ReservedCBills = phase == MissionPhase.Closed ? 0 :
                Lines.Aggregate(0L, (total, line) => checked(total + checked(line.UnitCBills * line.Remaining)));
        }
    }
}
