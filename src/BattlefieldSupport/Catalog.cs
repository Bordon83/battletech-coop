using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace BattletechCoop.Support
{
    public sealed class SupportCatalog
    {
        public IReadOnlyDictionary<string, SupportDefinition> Definitions { get; }
        public SupportCatalog(IEnumerable<SupportDefinition> definitions)
        {
            if (definitions == null) throw new ArgumentNullException("definitions");
            var copy = new Dictionary<string, SupportDefinition>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (definition == null) throw new ArgumentException("Null definition");
                copy.Add(definition.Id, definition);
            }
            Definitions = new ReadOnlyDictionary<string, SupportDefinition>(copy);
        }

        // Original labels; all prices, unlocks and limits below are provisional mod balancing.
        // No card text, artwork, damage tables or claims of complete rules fidelity.
        public static SupportCatalog CreatePrototype()
        {
            return new SupportCatalog(new[] {
                new SupportDefinition("artillery.thumper", "Thumper artillery", SupportKind.Artillery,
                    TargetShape.Area, 5000, 0, "artillery", 1, null, 1, 2, true),
                new SupportDefinition("artillery.sniper", "Sniper artillery", SupportKind.Artillery,
                    TargetShape.Area, 15000, 5, "artillery", 2, "artillery.link", 2, 2, true),
                new SupportDefinition("artillery.longtom", "Long Tom artillery", SupportKind.Artillery,
                    TargetShape.Area, 35000, 15, "artillery", 3, "artillery.link", 3, 1, true),
                new SupportDefinition("mines.light", "Light minefield", SupportKind.Minefield,
                    TargetShape.Area, 3000, 0, "engineering", 1, null, 1, 2, true),
                new SupportDefinition("mines.heavy", "Heavy minefield", SupportKind.Minefield,
                    TargetShape.Area, 10000, 5, "engineering", 2, "minelayer", 2, 1, true),
                new SupportDefinition("air.strafe", "Strafing run", SupportKind.AirStrike,
                    TargetShape.Line, 20000, 5, "aerospace", 2, null, 2, 2),
                new SupportDefinition("air.bomb", "Bombing run", SupportKind.AirStrike,
                    TargetShape.Area, 25000, 10, "aerospace", 3, "air.control", 3, 1),
                new SupportDefinition("air.cover", "Defensive air cover", SupportKind.AirCover,
                    TargetShape.None, 12000, 5, "aerospace", 2, null, 2, 2)
            });
        }
    }

    public sealed class CostPolicy
    {
        public int BasisPoints { get; }
        public CostPolicy(int basisPoints = 10000)
        {
            if (basisPoints < 1 || basisPoints > 100000) throw new ArgumentOutOfRangeException("basisPoints");
            BasisPoints = basisPoints;
        }
        public long UnitCost(SupportDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException("definition");
            // Round up without floating point. Even a discounted use always costs at least one C-Bill.
            var scaled = checked(definition.BaseCBills * BasisPoints);
            return scaled / 10000 + (scaled % 10000 == 0 ? 0 : 1);
        }
    }
}
