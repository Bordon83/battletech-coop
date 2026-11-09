namespace BattletechCoop.Support
{
    // Implement on the host. Peer IDs come from authenticated transport metadata, never a message body.
    public interface IHostEnvironment
    {
        bool IsHost { get; }
        bool IsParticipant(string peerId);
        bool CanManageMission(string peerId);
        CareerProfile Career { get; }
        MissionRules GetMission(string missionId);
    }

    // Must validate phase/turn, legal map position, targeting, capabilities and preplanned deployment.
    // Returning true authorizes an accounting order, not an assumed BattleTech invocation.
    public interface ITargetPolicy
    {
        bool Validate(MissionRules mission, SupportDefinition definition, SupportTarget target, TargetCheck check);
    }

    public sealed class UnsupportedTargets : ITargetPolicy
    {
        public bool Validate(MissionRules mission, SupportDefinition definition, SupportTarget target, TargetCheck check)
        { return false; }
    }

    // Read returns an immutable snapshot. Compare-and-swap atomically persists ALL state, including
    // C-Bills, stock, request receipts and effect orders. False means nothing was written.
    // A live adapter must coordinate with every other Career money writer; separate wallet calls
    // before/after TryCommit do not satisfy this contract. Failures must leave no partial writes.
    public interface IHostSupportStore
    {
        CompanySupportState Read();
        bool TryCommit(long expectedRevision, CompanySupportState next);
    }
}
