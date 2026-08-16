namespace RobotCompetitionBooth.Web.Models;

public sealed record StoredCollaboratorIdentity(
    string? Id,
    string? Name,
    string? Color);

public sealed record CollaboratorIdentity(
    string Id,
    string Name,
    string Color);

public sealed record RobotCollaborator(
    string Id,
    string Name,
    string Color,
    string? SelectedBlockId,
    string? SelectedBlockDescription);

public sealed record CollaborationJoinResult(
    CollaboratorIdentity CurrentUser,
    IReadOnlyList<RobotCollaborator> Collaborators,
    string WorkspaceJson,
    string WorkspaceName,
    long Revision,
    bool JoinedExistingSession);

public sealed record WorkspaceCollaborationUpdate(
    string DeviceId,
    Guid SourceConnectionId,
    string WorkspaceJson,
    string WorkspaceName,
    bool WorkspaceNameChanged,
    long Revision);

public sealed record PresenceCollaborationUpdate(
    string DeviceId,
    IReadOnlyList<RobotCollaborator> Collaborators);
