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

public sealed record RobotCollaboratorCursor(
    string CollaboratorId,
    string Name,
    string Color,
    double WorkspaceX,
    double WorkspaceY);

public sealed record CollaborationJoinResult(
    CollaboratorIdentity CurrentUser,
    IReadOnlyList<RobotCollaborator> Collaborators,
    IReadOnlyList<RobotCollaboratorCursor> Cursors,
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

public sealed record CursorCollaborationUpdate(
    string DeviceId,
    string CollaboratorId,
    RobotCollaboratorCursor? Cursor,
    bool CollaboratorLeft);
