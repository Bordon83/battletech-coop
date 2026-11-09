using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BattletechCoop.Support;

static class Program
{
    static int Main()
    {
        var tests = new KeyValuePair<string, Action>[] {
            Test("every prototype support has a positive price and only intended categories", Catalog),
            Test("invalid configuration and malformed targets are rejected", InvalidModel),
            Test("cost multipliers round up and never yield free support", Pricing),
            Test("overflow cannot create a cheap quote or mutate state", Overflow),
            Test("untrusted company input cannot alter immutable snapshots", ImmutableState),
            Test("shared preparation reserves money and leaves stock untouched", Preparation),
            Test("PC preparation query exposes prices, limits and shared selection without writes", PreparationView),
            Test("career progress, contacts and equipment independently gate support", Unlocks),
            Test("career preparation and use capacities grow with progress", Progression),
            Test("mission and per-card limits apply to the entire shared deck", Limits),
            Test("stock and money shortages reject the whole preparation", Shortages),
            Test("unknown IDs, duplicate entries and malformed commands fail atomically", Malformed),
            Test("predeployment targets are required and host validated", Predeployment),
            Test("unsupported game integration fails closed without payment", Unsupported),
            Test("a guest cannot instantiate authority or manage mission lifecycle", Authority),
            Test("stale deck edits do not overwrite another player's selection", StaleEdits),
            Test("retrying preparation is idempotent even after later state changes", RetryPreparation),
            Test("request IDs cannot be reused for different commands", RequestConflict),
            Test("launch revalidates money, stock, career and mission rules", RevalidateLaunch),
            Test("usage requires the deployed mission and a prepared support", Lifecycle),
            Test("accepted usage charges once, consumes shared stock and creates an order", PaidUse),
            Test("different peers share limits and cannot spend each other's receipts", SharedArsenal),
            Test("invalid use targets and missing capabilities do not spend anything", InvalidUse),
            Test("preplanned targets cannot be changed during a mission", TargetLocked),
            Test("usage rechecks current career, rules, funds and stock", RevalidateUse),
            Test("close releases unused reservation and preserves paid order history", Close),
            Test("cancel and subsequent missions cannot reopen old allowances", Cancel),
            Test("failed storage commit does not leak any side effects", FailedCommit),
            Test("simultaneous host and guest requests spend only once", ConcurrentUse),
            Test("restart with the same store retains deduplication and frozen prices", Restart),
            Test("bounded request history rejects further writes safely", HistoryLimit)
        };
        var failed = 0;
        foreach (var test in tests)
        {
            try { test.Value(); Console.WriteLine("PASS " + test.Key); }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + test.Key + ": " + e); }
        }
        Console.WriteLine((tests.Length - failed) + "/" + tests.Length + " passed");
        return failed == 0 ? 0 : 1;
    }

    static KeyValuePair<string, Action> Test(string name, Action body) { return new KeyValuePair<string, Action>(name, body); }
    static void Check(bool condition, string message = "assertion failed") { if (!condition) throw new Exception(message); }
    static void Equal<T>(T expected, T actual) { Check(Equals(expected, actual), "Expected " + expected + ", got " + actual); }
    static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static SupportTarget Area(int x = 10) { return new SupportTarget(TargetShape.Area, x, 20); }
    static Selection[] Pick(int uses = 1) { return new[] { new Selection("artillery.thumper", uses, Area()) }; }
    static CareerProfile Career(int contracts = 40, int rank = 5, bool equipment = true)
    {
        return new CareerProfile(contracts, new Dictionary<string, int> {
            { "artillery", rank }, { "engineering", rank }, { "aerospace", rank } },
            equipment ? new[] { "artillery.link", "minelayer", "air.control" } : new string[0]);
    }
    sealed class Environment : IHostEnvironment
    {
        public bool IsHost { get; set; } = true;
        public CareerProfile Career { get; set; } = Program.Career();
        public MissionRules Rules = new MissionRules("m1", 6, 6, (SupportKind[])Enum.GetValues(typeof(SupportKind)));
        public bool GuestPresent = true;
        public bool IsParticipant(string peer) { return peer == "host" || (GuestPresent && peer == "guest"); }
        public bool CanManageMission(string peer) { return peer == "host"; }
        public MissionRules GetMission(string id) { return Rules != null && Rules.MissionId == id ? Rules : null; }
    }
    sealed class Targets : ITargetPolicy
    {
        public bool AllowPreparation = true, AllowUse = true;
        public bool Validate(MissionRules mission, SupportDefinition definition, SupportTarget target, TargetCheck check)
        { return check == TargetCheck.Preparation ? AllowPreparation : AllowUse; }
    }
    sealed class Fixture
    {
        public readonly Environment Env = new Environment();
        public readonly Targets Targets = new Targets();
        public readonly MemorySupportStore Store;
        public readonly SupportCatalog Catalog;
        public HostSupportService Service;
        public CompanySupportState State { get { return Store.Read(); } }
        public Fixture(long money = 200000, SupportCatalog catalog = null)
        {
            Catalog = catalog ?? SupportCatalog.CreatePrototype();
            Store = new MemorySupportStore(new CompanySupportState(money,
                Catalog.Definitions.Keys.ToDictionary(id => id, id => 10)));
            Service = new HostSupportService(Store, Env, Catalog, targets: Targets);
        }
        public void Ready(int uses = 1)
        {
            Equal(Failure.None, Service.Prepare("guest", "prepare", 0, "m1", Pick(uses)).Failure);
            Equal(Failure.None, Service.Launch("host", "launch", State.Revision, "m1").Failure);
        }
        public SupportResult Use(string peer = "guest", string request = "use", long? revision = null, SupportTarget target = null)
        { return Service.RequestUse(peer, request, revision ?? State.Revision, "m1", "artillery.thumper", target ?? Area()); }
        public void Economy(long money, int stock = 10)
        {
            var current = State;
            Check(Store.TryCommit(current.Revision, current.WithEconomy(money,
                current.Stock.ToDictionary(p => p.Key, p => stock))));
        }
    }
    static void Unchanged(Fixture f, Failure failure, Func<SupportResult> action)
    {
        var before = f.State;
        Equal(failure, action().Failure);
        Check(ReferenceEquals(before, f.State), "Rejected request changed state");
    }

    static void Catalog()
    {
        var c = SupportCatalog.CreatePrototype(); Equal(8, c.Definitions.Count);
        Check(c.Definitions.Values.All(d => d.BaseCBills > 0));
        Equal(4, c.Definitions.Values.Select(d => d.Kind).Distinct().Count());
    }
    static void InvalidModel()
    {
        Throws<ArgumentException>(() => new Selection("bad\nID", 1));
        Throws<ArgumentOutOfRangeException>(() => new Selection("x", 0));
        Throws<ArgumentOutOfRangeException>(() => new CostPolicy(0));
        Throws<ArgumentOutOfRangeException>(() => new CompanySupportState(-1, new Dictionary<string, int>()));
        Throws<ArgumentOutOfRangeException>(() => Career(-1));
        Throws<ArgumentOutOfRangeException>(() => Career(rank: 6));
        Throws<ArgumentException>(() => new SupportTarget(TargetShape.Line, 1, 1, 1, 1));
        Throws<ArgumentException>(() => new SupportTarget(TargetShape.None, 1));
        Throws<ArgumentException>(() => new SupportTarget(TargetShape.Area, 0, 0, 3));
        Throws<ArgumentException>(() => new SupportCatalog(new[] { Definition("x", 1), Definition("x", 2) }));
    }
    static SupportDefinition Definition(string id, long cost)
    { return new SupportDefinition(id, id, SupportKind.Artillery, TargetShape.Area, cost, 0, null, 0, null, 1, 10); }
    static void Pricing()
    {
        Equal(1L, new CostPolicy(1).UnitCost(Definition("x", 1)));
        Equal(2L, new CostPolicy(15000).UnitCost(Definition("x", 1)));
        Equal(15000L, new CostPolicy().UnitCost(Definition("x", 15000)));
        Equal(1667L, new CostPolicy(3333).UnitCost(Definition("x", 5000)));
    }
    static void Overflow()
    {
        var f = new Fixture(long.MaxValue, new SupportCatalog(new[] { Definition("x", long.MaxValue) }));
        Unchanged(f, Failure.InvalidRequest, () => f.Service.Prepare("host", "a", 0, "m1", new[] { new Selection("x", 1) }));
        var policy = new CostPolicy(1);
        var huge = Definition("x", long.MaxValue);
        Check(policy.UnitCost(huge) > 0);
        var sumCatalog = new SupportCatalog(new[] { Definition("x", long.MaxValue / 2) });
        f = new Fixture(long.MaxValue, sumCatalog);
        f.Service = new HostSupportService(f.Store, f.Env, sumCatalog, new CostPolicy(1), f.Targets);
        Unchanged(f, Failure.InvalidRequest, () => f.Service.Prepare("host", "a", 0, "m1",
            new[] { new Selection("x", int.MaxValue) }));
    }
    static void ImmutableState()
    {
        var stock = new Dictionary<string, int> { { "x", 2 } };
        var state = new CompanySupportState(20, stock); stock["x"] = 999;
        Equal(2, state.Stock["x"]);
        Throws<NotSupportedException>(() => ((IDictionary<string, int>)state.Stock)["x"] = 5);
        var f = new Fixture(); var selections = Pick();
        f.Service.Prepare("host", "p", 0, "m1", selections); selections[0] = new Selection("air.cover", 2);
        Equal("artillery.thumper", f.State.Mission.Lines[0].Selection.SupportId);
    }
    static void Preparation()
    {
        var f = new Fixture(10000);
        Equal(Failure.None, f.Service.Prepare("guest", "p", 0, "m1", Pick(2)).Failure);
        Equal(10000L, f.State.CBills); Equal(0L, f.State.AvailableCBills);
        Equal(10000L, f.State.Mission.ReservedCBills); Equal(10, f.State.Stock["artillery.thumper"]);
        Equal(Failure.None, f.Service.Prepare("host", "p2", 1, "m1", Pick()).Failure);
        Equal(5000L, f.State.AvailableCBills);
    }
    static void PreparationView()
    {
        var f = new Fixture(10000); f.Env.Career = Career(0);
        var state = f.State;
        Equal(Failure.Unauthorized, f.Service.GetPreparation("outsider", "m1").Failure);
        Equal(Failure.WrongMission, f.Service.GetPreparation("host", "unknown").Failure);
        var view = ((IMissionPreparation)f.Service).GetPreparation("guest", "m1").View;
        Equal(2, view.MaxUses); Equal(2, view.PreparationSlots); Equal(0L, view.Revision);
        var thumper = view.Options.Single(o => o.Definition.Id == "artillery.thumper");
        Equal(5000L, thumper.UnitCBills); Equal(2, thumper.MaxUsesIfSelectedAlone);
        Equal(Failure.Unavailable, view.Options.Single(o => o.Definition.Id == "air.bomb").Availability);
        Check(ReferenceEquals(state, f.State));
        f.Service.Prepare("guest", "p", 0, "m1", Pick());
        view = f.Service.GetPreparation("host", "m1").View;
        Equal(5000L, view.ReservedCBills); Equal(5000L, view.AvailableCBills);
        Equal("artillery.thumper", view.CurrentPlan.Lines[0].Selection.SupportId);
        f.Service.Launch("host", "l", 1, "m1");
        Equal(Failure.WrongPhase, f.Service.GetPreparation("guest", "m1").Failure);
    }
    static void Unlocks()
    {
        var f = new Fixture(); var selection = new[] { new Selection("artillery.sniper", 1, Area()) };
        f.Env.Career = Career(0);
        Unchanged(f, Failure.Unavailable, () => f.Service.Prepare("host", "p", 0, "m1", selection));
        f.Env.Career = Career(rank: 1);
        Unchanged(f, Failure.Unavailable, () => f.Service.Prepare("host", "p", 0, "m1", selection));
        f.Env.Career = Career(equipment: false);
        Unchanged(f, Failure.Unavailable, () => f.Service.Prepare("host", "p", 0, "m1", selection));
        f.Env.Career = Career();
        Equal(Failure.None, f.Service.Prepare("host", "p", 0, "m1", selection).Failure);
    }
    static void Progression()
    {
        Equal(2, Career(0).PreparationSlots); Equal(3, Career(5).PreparationSlots);
        Equal(2, Career(5).MissionUses); Equal(3, Career(10).MissionUses);
        Equal(6, Career(int.MaxValue).PreparationSlots); Equal(6, Career(int.MaxValue).MissionUses);
        var f = new Fixture(); f.Env.Career = Career(0);
        var deck = new[] { new Selection("artillery.thumper", 2, Area()), new Selection("mines.light", 1, Area()) };
        Unchanged(f, Failure.LimitExceeded, () => f.Service.Prepare("guest", "p", 0, "m1", deck));
        f.Env.Career = Career(10);
        Equal(Failure.None, f.Service.Prepare("guest", "p", 0, "m1", deck).Failure);
    }
    static void Limits()
    {
        var f = new Fixture();
        Unchanged(f, Failure.LimitExceeded, () => f.Service.Prepare("host", "p", 0, "m1", Pick(3)));
        f.Env.Rules = new MissionRules("m1", 6, 1, new[] { SupportKind.Artillery });
        Unchanged(f, Failure.LimitExceeded, () => f.Service.Prepare("host", "p", 0, "m1", Pick(2)));
        f.Env.Rules = new MissionRules("m1", 0, 6, new[] { SupportKind.Artillery });
        Unchanged(f, Failure.LimitExceeded, () => f.Service.Prepare("host", "p", 0, "m1", Pick()));
        f.Env.Rules = new MissionRules("m1", 6, 6, new[] { SupportKind.AirStrike });
        Unchanged(f, Failure.Unavailable, () => f.Service.Prepare("host", "p", 0, "m1", Pick()));
    }
    static void Shortages()
    {
        var f = new Fixture(4999);
        Unchanged(f, Failure.InsufficientFunds, () => f.Service.Prepare("guest", "p", 0, "m1", Pick()));
        f.Economy(100000, 0);
        Unchanged(f, Failure.InsufficientStock, () => f.Service.Prepare("guest", "p", f.State.Revision, "m1", Pick()));
    }
    static void Malformed()
    {
        var f = new Fixture();
        Unchanged(f, Failure.InvalidRequest, () => f.Service.Prepare("guest", "p", 0, "m1", null));
        Unchanged(f, Failure.InvalidRequest, () => f.Service.Prepare("guest", "p", 0, "m1", new Selection[] { null }));
        Unchanged(f, Failure.InvalidRequest, () => f.Service.Prepare("guest", "p", 0, "m1", Pick().Concat(Pick()).ToArray()));
        Unchanged(f, Failure.InvalidRequest, () => f.Service.Prepare("guest", "p", 0, "m1", Enumerable.Repeat(Pick()[0], 65).ToArray()));
        Unchanged(f, Failure.InvalidRequest, () => f.Service.Prepare("guest", "bad\nid", 0, "m1", Pick()));
        Unchanged(f, Failure.Unavailable, () => f.Service.Prepare("guest", "p", 0, "m1", new[] { new Selection("unknown", 1) }));
        Unchanged(f, Failure.WrongMission, () => f.Service.Prepare("guest", "p", 0, "unknown", Pick()));
    }
    static void Predeployment()
    {
        var f = new Fixture();
        Unchanged(f, Failure.InvalidTarget, () => f.Service.Prepare("guest", "p", 0, "m1", new[] { new Selection("mines.light", 1) }));
        f.Targets.AllowPreparation = false;
        Unchanged(f, Failure.InvalidTarget, () => f.Service.Prepare("guest", "p", 0, "m1", Pick()));
        f.Targets.AllowPreparation = true;
        Equal(Failure.None, f.Service.Prepare("guest", "p", 0, "m1", Pick()).Failure);
        f.Targets.AllowPreparation = false;
        Unchanged(f, Failure.InvalidTarget, () => f.Service.Launch("host", "l", 1, "m1"));
    }
    static void Unsupported()
    {
        var f = new Fixture(); f.Service = new HostSupportService(f.Store, f.Env, f.Catalog);
        Unchanged(f, Failure.InvalidTarget, () => f.Service.Prepare("guest", "p", 0, "m1", Pick()));
        Equal(Failure.None, f.Service.Prepare("guest", "p", 0, "m1", new[] { new Selection("air.cover", 1) }).Failure);
        Equal(Failure.None, f.Service.Launch("host", "l", 1, "m1").Failure);
        Unchanged(f, Failure.InvalidTarget, () => f.Service.RequestUse("guest", "u", 2, "m1", "air.cover", new SupportTarget(TargetShape.None)));
    }
    static void Authority()
    {
        var f = new Fixture(); f.Env.IsHost = false;
        Unchanged(f, Failure.NotHost, () => f.Service.Prepare("host", "p", 0, "m1", Pick()));
        f.Env.IsHost = true;
        Unchanged(f, Failure.Unauthorized, () => f.Service.Prepare("outsider", "p", 0, "m1", Pick()));
        f.Ready();
        Unchanged(f, Failure.Unauthorized, () => f.Service.Close("guest", "c", 2, "m1"));
        Unchanged(f, Failure.Unauthorized, () => f.Service.Launch("guest", "l", 2, "m1"));
        f.Env.GuestPresent = false;
        Unchanged(f, Failure.Unauthorized, () => f.Use());
    }
    static void StaleEdits()
    {
        var f = new Fixture(); f.Service.Prepare("guest", "p", 0, "m1", Pick());
        Unchanged(f, Failure.StaleRevision, () => f.Service.Prepare("host", "p2", 0, "m1", Pick(2)));
    }
    static void RetryPreparation()
    {
        var f = new Fixture(); f.Ready(); var before = f.State;
        var retry = f.Service.Prepare("guest", "prepare", 0, "m1", Pick());
        Check(retry.Accepted && retry.Replayed); Equal(1L, retry.Revision);
        Check(ReferenceEquals(before, f.State));
    }
    static void RequestConflict()
    {
        var f = new Fixture(); f.Service.Prepare("guest", "p", 0, "m1", Pick());
        Unchanged(f, Failure.RequestConflict, () => f.Service.Prepare("guest", "p", 1, "m1", Pick(2)));
        Unchanged(f, Failure.RequestConflict, () => f.Use(request: "p"));
    }
    static void RevalidateLaunch()
    {
        var f = new Fixture(); f.Service.Prepare("host", "p", 0, "m1", Pick());
        f.Economy(1);
        Unchanged(f, Failure.InsufficientFunds, () => f.Service.Launch("host", "l", f.State.Revision, "m1"));
        f.Economy(200000, 0);
        Unchanged(f, Failure.InsufficientStock, () => f.Service.Launch("host", "l", f.State.Revision, "m1"));
        f.Economy(200000); f.Env.Career = Career(rank: 0);
        Unchanged(f, Failure.Unavailable, () => f.Service.Launch("host", "l", f.State.Revision, "m1"));
        f.Env.Career = Career(); f.Env.Rules = new MissionRules("m1", 0, 0, new[] { SupportKind.Artillery });
        Unchanged(f, Failure.LimitExceeded, () => f.Service.Launch("host", "l", f.State.Revision, "m1"));
    }
    static void Lifecycle()
    {
        var f = new Fixture();
        Unchanged(f, Failure.WrongMission, () => f.Use());
        f.Service.Prepare("host", "p", 0, "m1", Pick());
        Unchanged(f, Failure.WrongPhase, () => f.Use());
        f.Service.Launch("host", "l", 1, "m1");
        Unchanged(f, Failure.WrongPhase, () => f.Service.Prepare("guest", "p2", 2, "m1", Pick(2)));
        Unchanged(f, Failure.Unavailable, () => f.Service.RequestUse("guest", "u", 2, "m1", "air.cover", new SupportTarget(TargetShape.None)));
    }
    static void PaidUse()
    {
        var f = new Fixture(10000); f.Ready(2); var result = f.Use();
        Check(result.Accepted && result.OrderId != null); Equal(5000L, f.State.CBills);
        Equal(0L, f.State.AvailableCBills); Equal(9, f.State.Stock["artillery.thumper"]);
        Equal(1, f.State.Mission.Used); Equal(1, f.State.Orders.Count);
        Equal(5000L, f.State.Orders[0].ChargedCBills);
        var before = f.State; var retry = f.Use(revision: 2);
        Check(retry.Replayed); Equal(result.OrderId, retry.OrderId); Check(ReferenceEquals(before, f.State));
    }
    static void SharedArsenal()
    {
        var f = new Fixture(); f.Ready(2);
        Equal(Failure.None, f.Use("guest", "same").Failure);
        Unchanged(f, Failure.StaleRevision, () => f.Use("host", "same", 2));
        Equal(Failure.None, f.Use("host", "same").Failure);
        Check(f.State.Orders[0].OrderId != f.State.Orders[1].OrderId);
        Unchanged(f, Failure.LimitExceeded, () => f.Use("guest", "third"));
        Equal(8, f.State.Stock["artillery.thumper"]);
    }
    static void InvalidUse()
    {
        var f = new Fixture(); f.Ready();
        Unchanged(f, Failure.InvalidTarget, () => f.Use(target: new SupportTarget(TargetShape.Point, 1, 1)));
        Unchanged(f, Failure.InvalidTarget, () => f.Service.RequestUse("host", "u", 2, "m1", "artillery.thumper", null));
        f.Targets.AllowUse = false;
        Unchanged(f, Failure.InvalidTarget, () => f.Use());
    }
    static void TargetLocked()
    {
        var f = new Fixture(); f.Ready();
        Unchanged(f, Failure.InvalidTarget, () => f.Use(target: Area(999)));
        // The preparation phase has passed; use validation alone checks current combat legality.
        f.Targets.AllowPreparation = false;
        Equal(Failure.None, f.Use().Failure);
    }
    static void RevalidateUse()
    {
        var f = new Fixture(); f.Ready(2); f.Economy(5000);
        Unchanged(f, Failure.InsufficientFunds, () => f.Use());
        f.Economy(200000, 1);
        Unchanged(f, Failure.InsufficientStock, () => f.Use());
        f.Economy(200000); f.Env.Career = Career(rank: 0);
        Unchanged(f, Failure.Unavailable, () => f.Use());
        f.Env.Career = Career(); f.Env.Rules = new MissionRules("m1", 6, 0, new[] { SupportKind.Artillery });
        Unchanged(f, Failure.LimitExceeded, () => f.Use());
    }
    static void Close()
    {
        var f = new Fixture(10000); f.Ready(2); f.Use();
        Equal(Failure.None, f.Service.Close("host", "c", f.State.Revision, "m1").Failure);
        Equal(5000L, f.State.CBills); Equal(5000L, f.State.AvailableCBills);
        Equal(9, f.State.Stock["artillery.thumper"]); Equal(1, f.State.Orders.Count);
        Unchanged(f, Failure.WrongPhase, () => f.Use("host", "late"));
        Check(f.Use(revision: 2).Replayed);
    }
    static void Cancel()
    {
        var f = new Fixture(); f.Service.Prepare("guest", "p", 0, "m1", Pick());
        f.Env.Rules = new MissionRules("m2", 6, 6, new[] { SupportKind.Artillery });
        Unchanged(f, Failure.WrongMission, () => f.Service.Prepare("guest", "p2", 1, "m2", Pick()));
        Equal(Failure.None, f.Service.Close("host", "c", 1, "m1").Failure);
        Equal(Failure.None, f.Service.Prepare("guest", "p2", 2, "m2", Pick()).Failure);
        f.Env.Rules = new MissionRules("m1", 6, 6, new[] { SupportKind.Artillery });
        Unchanged(f, Failure.WrongMission, () => f.Service.Prepare("guest", "p3", 3, "m1", Pick()));
        Equal(200000L, f.State.CBills);
    }
    sealed class RejectingStore : IHostSupportStore
    {
        private readonly IHostSupportStore inner;
        public RejectingStore(IHostSupportStore inner) { this.inner = inner; }
        public CompanySupportState Read() { return inner.Read(); }
        public bool TryCommit(long revision, CompanySupportState next) { return false; }
    }
    static void FailedCommit()
    {
        var f = new Fixture(); f.Ready();
        f.Service = new HostSupportService(new RejectingStore(f.Store), f.Env, f.Catalog, targets: f.Targets);
        Unchanged(f, Failure.StaleRevision, () => f.Use());
    }
    sealed class RacingStore : IHostSupportStore
    {
        private readonly IHostSupportStore inner;
        private readonly Barrier barrier = new Barrier(2);
        public RacingStore(IHostSupportStore inner) { this.inner = inner; }
        public CompanySupportState Read() { return inner.Read(); }
        public bool TryCommit(long revision, CompanySupportState next)
        {
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new Exception("Race setup timed out");
            return inner.TryCommit(revision, next);
        }
    }
    static void ConcurrentUse()
    {
        var f = new Fixture(5000); f.Ready();
        f.Service = new HostSupportService(new RacingStore(f.Store), f.Env, f.Catalog, targets: f.Targets);
        var a = Task.Run(() => f.Use("host", "u1", 2));
        var b = Task.Run(() => f.Use("guest", "u2", 2));
        Task.WaitAll(a, b);
        Equal(1, new[] { a.Result, b.Result }.Count(r => r.Accepted));
        Equal(1, new[] { a.Result, b.Result }.Count(r => r.Failure == Failure.StaleRevision));
        Equal(0L, f.State.CBills); Equal(9, f.State.Stock["artillery.thumper"]); Equal(1, f.State.Orders.Count);
    }
    static void Restart()
    {
        var f = new Fixture(10000); f.Ready(2); var first = f.Use();
        f.Service = new HostSupportService(f.Store, f.Env, f.Catalog, new CostPolicy(20000), f.Targets);
        var replay = f.Use(revision: 2); Check(replay.Replayed); Equal(first.OrderId, replay.OrderId);
        Equal(Failure.None, f.Use(request: "u2").Failure);
        Equal(0L, f.State.CBills); Equal(5000L, f.State.Orders[1].ChargedCBills);
    }
    static void HistoryLimit()
    {
        var f = new Fixture();
        for (var i = 0; i < HostSupportService.MaxAcceptedRequests - 1; i++)
            Check(f.Service.Prepare("host", "p" + i, f.State.Revision, "m1", new Selection[0]).Accepted);
        Unchanged(f, Failure.HistoryFull, () => f.Service.Prepare("host", "extra", f.State.Revision, "m1", new Selection[0]));
        Equal(Failure.None, f.Service.Close("host", "close", f.State.Revision, "m1").Failure);
        Equal(0L, f.State.Mission.ReservedCBills);
        Unchanged(f, Failure.HistoryFull, () => f.Service.Prepare("host", "extra", f.State.Revision, "m1", new Selection[0]));
        Check(f.Service.Prepare("host", "p0", 0, "m1", new Selection[0]).Replayed);
    }
}
