using System.Security.Cryptography;
using RobotCompetitionBooth.Web.Models;

namespace RobotCompetitionBooth.Web.Services;

public sealed class RobotCollaborationService
{
    private const int MaximumDisplayNameLength = 40;
    private const double MaximumCursorCoordinateMagnitude = 1_000_000;

    private static readonly string[] Adjectives =
    [
        "Brave", "Bright", "Calm", "Clever", "Curious", "Daring", "Eager", "Gentle",
        "Happy", "Helpful", "Jolly", "Kind", "Lively", "Lucky", "Merry", "Mighty",
        "Nimble", "Patient", "Playful", "Quick", "Quiet", "Shiny", "Swift", "Witty"
    ];

    private static readonly string[] Animals =
    [
        "Badger", "Bear", "Beaver", "Bison", "Cat", "Cheetah", "Dolphin", "Falcon",
        "Fox", "Gecko", "Hare", "Hedgehog", "Koala", "Lemur", "Lion", "Lynx",
        "Otter", "Owl", "Panda", "Penguin", "Rabbit", "Raccoon", "Tiger", "Wolf"
    ];

    private static readonly string[] CollaboratorColors =
    [
        "#C62828", "#AD1457", "#6A1B9A", "#4527A0", "#283593", "#1565C0",
        "#0277BD", "#00838F", "#00695C", "#2E7D32", "#558B2F", "#9E6C00",
        "#EF6C00", "#D84315", "#5D4037", "#455A64"
    ];

    private readonly object stateLock = new();
    private readonly Dictionary<string, CollaborationSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> connectionDevices = [];

    public event EventHandler<WorkspaceCollaborationUpdate>? WorkspaceChanged;

    public event EventHandler<PresenceCollaborationUpdate>? PresenceChanged;

    public event EventHandler<CursorCollaborationUpdate>? CursorChanged;

    public CollaborationJoinResult Join(
        Guid connectionId,
        string deviceId,
        StoredCollaboratorIdentity? storedIdentity,
        string initialWorkspaceJson,
        string initialWorkspaceName)
    {
        DeviceProgramStore.ValidateDeviceId(deviceId);
        DeviceProgramStore.ValidateWorkspacePayload(initialWorkspaceJson);
        var normalizedWorkspaceName = DeviceProgramStore.NormalizeWorkspaceName(initialWorkspaceName);

        CollaborationJoinResult result;
        PresenceCollaborationUpdate presenceUpdate;
        lock (stateLock)
        {
            CollaborationSession session;
            bool joinedExistingSession;
            if (sessions.TryGetValue(deviceId, out var existingSession))
            {
                session = existingSession;
                joinedExistingSession = true;
            }
            else
            {
                session = new(initialWorkspaceJson, normalizedWorkspaceName);
                sessions.Add(deviceId, session);
                joinedExistingSession = false;
            }

            var identity = ResolveIdentity(session, storedIdentity);
            session.Connections[connectionId] = new(identity);
            connectionDevices[connectionId] = deviceId;

            var collaborators = CreateCollaboratorSnapshot(session);
            result = new(
                identity,
                collaborators,
                CreateCursorSnapshot(session),
                session.WorkspaceJson,
                session.WorkspaceName,
                session.Revision,
                joinedExistingSession);
            presenceUpdate = new(deviceId, collaborators);
        }

        PresenceChanged?.Invoke(this, presenceUpdate);
        return result;
    }

    public long UpdateWorkspace(
        Guid connectionId,
        string workspaceJson,
        string? workspaceName = null)
    {
        DeviceProgramStore.ValidateWorkspacePayload(workspaceJson);
        var normalizedWorkspaceName = workspaceName is null
            ? null
            : DeviceProgramStore.NormalizeWorkspaceName(workspaceName);

        WorkspaceCollaborationUpdate update;
        lock (stateLock)
        {
            var (deviceId, session, _) = GetConnection(connectionId);
            session.WorkspaceJson = workspaceJson;
            if (normalizedWorkspaceName is not null)
            {
                session.WorkspaceName = normalizedWorkspaceName;
            }

            session.Revision++;
            update = new(
                deviceId,
                connectionId,
                session.WorkspaceJson,
                session.WorkspaceName,
                normalizedWorkspaceName is not null,
                session.Revision);
        }

        WorkspaceChanged?.Invoke(this, update);
        return update.Revision;
    }

    public void UpdateSelection(
        Guid connectionId,
        string? blockId,
        string? blockDescription)
    {
        if (blockId is { Length: > 128 })
        {
            throw new ArgumentException("The selected block ID is too long.", nameof(blockId));
        }

        var normalizedDescription = string.IsNullOrWhiteSpace(blockDescription)
            ? null
            : blockDescription.Trim();
        if (normalizedDescription is { Length: > 100 })
        {
            normalizedDescription = normalizedDescription[..100];
        }

        PresenceCollaborationUpdate update;
        lock (stateLock)
        {
            var (deviceId, session, participant) = GetConnection(connectionId);
            participant.SelectedBlockId = string.IsNullOrWhiteSpace(blockId) ? null : blockId;
            participant.SelectedBlockDescription = normalizedDescription;
            participant.SelectionUpdatedAt = DateTimeOffset.UtcNow;
            update = new(deviceId, CreateCollaboratorSnapshot(session));
        }

        PresenceChanged?.Invoke(this, update);
    }

    public void UpdateCursor(
        Guid connectionId,
        double? workspaceX,
        double? workspaceY)
    {
        if (workspaceX.HasValue != workspaceY.HasValue)
        {
            throw new ArgumentException("Both cursor coordinates must be supplied together.");
        }

        if (workspaceX is { } x &&
            (!double.IsFinite(x) || Math.Abs(x) > MaximumCursorCoordinateMagnitude))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspaceX),
                "The cursor X coordinate is outside the supported workspace range.");
        }

        if (workspaceY is { } y &&
            (!double.IsFinite(y) || Math.Abs(y) > MaximumCursorCoordinateMagnitude))
        {
            throw new ArgumentOutOfRangeException(
                nameof(workspaceY),
                "The cursor Y coordinate is outside the supported workspace range.");
        }

        CursorCollaborationUpdate update;
        lock (stateLock)
        {
            var (deviceId, session, participant) = GetConnection(connectionId);
            participant.CursorWorkspaceX = workspaceX;
            participant.CursorWorkspaceY = workspaceY;
            participant.CursorUpdatedAt = DateTimeOffset.UtcNow;
            update = new(
                deviceId,
                participant.Identity.Id,
                CreateCursorSnapshot(session, participant.Identity.Id),
                false);
        }

        CursorChanged?.Invoke(this, update);
    }

    public CollaboratorIdentity Rename(Guid connectionId, string requestedName)
    {
        var normalizedName = NormalizeDisplayName(requestedName);
        CollaboratorIdentity identity;
        PresenceCollaborationUpdate update;
        CursorCollaborationUpdate? cursorUpdate;
        lock (stateLock)
        {
            var (deviceId, session, participant) = GetConnection(connectionId);
            var identityId = participant.Identity.Id;
            identity = participant.Identity with { Name = normalizedName };
            foreach (var connection in session.Connections.Values.Where(
                         connection => connection.Identity.Id == identityId))
            {
                connection.Identity = identity;
            }

            update = new(deviceId, CreateCollaboratorSnapshot(session));
            var cursor = CreateCursorSnapshot(session, identityId);
            cursorUpdate = cursor is null
                ? null
                : new(deviceId, identityId, cursor, false);
        }

        PresenceChanged?.Invoke(this, update);
        if (cursorUpdate is not null)
        {
            CursorChanged?.Invoke(this, cursorUpdate);
        }

        return identity;
    }

    public void Leave(Guid connectionId)
    {
        PresenceCollaborationUpdate? update = null;
        CursorCollaborationUpdate? cursorUpdate = null;
        lock (stateLock)
        {
            if (!connectionDevices.Remove(connectionId, out var deviceId) ||
                !sessions.TryGetValue(deviceId, out var session))
            {
                return;
            }

            if (!session.Connections.Remove(connectionId, out var participant))
            {
                return;
            }

            if (session.Connections.Count == 0)
            {
                sessions.Remove(deviceId);
            }
            else
            {
                update = new(deviceId, CreateCollaboratorSnapshot(session));
            }

            var collaboratorStillConnected = session.Connections.Values.Any(connection =>
                connection.Identity.Id == participant.Identity.Id);
            cursorUpdate = new(
                deviceId,
                participant.Identity.Id,
                CreateCursorSnapshot(session, participant.Identity.Id),
                !collaboratorStillConnected);
        }

        if (update is not null)
        {
            PresenceChanged?.Invoke(this, update);
        }

        if (cursorUpdate is not null)
        {
            CursorChanged?.Invoke(this, cursorUpdate);
        }
    }

    private (string DeviceId, CollaborationSession Session, Participant Participant) GetConnection(
        Guid connectionId)
    {
        if (!connectionDevices.TryGetValue(connectionId, out var deviceId) ||
            !sessions.TryGetValue(deviceId, out var session) ||
            !session.Connections.TryGetValue(connectionId, out var participant))
        {
            throw new InvalidOperationException("The collaboration connection is no longer active.");
        }

        return (deviceId, session, participant);
    }

    private static CollaboratorIdentity ResolveIdentity(
        CollaborationSession session,
        StoredCollaboratorIdentity? storedIdentity)
    {
        var parsedIdentityId = Guid.TryParse(storedIdentity?.Id, out var identityId)
            ? identityId.ToString("N")
            : Guid.NewGuid().ToString("N");
        var activeIdentity = session.Connections.Values
            .Select(connection => connection.Identity)
            .FirstOrDefault(identity => identity.Id == parsedIdentityId);
        if (activeIdentity is not null)
        {
            return activeIdentity;
        }

        string displayName;
        try
        {
            displayName = NormalizeDisplayName(storedIdentity?.Name ?? string.Empty);
        }
        catch (ArgumentException)
        {
            displayName = CreateGeneratedName(session);
        }

        var color = IsValidColor(storedIdentity?.Color)
            ? storedIdentity!.Color!.ToUpperInvariant()
            : CollaboratorColors[RandomNumberGenerator.GetInt32(CollaboratorColors.Length)];

        return new(parsedIdentityId, displayName, color);
    }

    private static string CreateGeneratedName(CollaborationSession session)
    {
        var activeNames = session.Connections.Values
            .Select(connection => connection.Identity.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var candidate = $"{Adjectives[RandomNumberGenerator.GetInt32(Adjectives.Length)]} " +
                Animals[RandomNumberGenerator.GetInt32(Animals.Length)];
            if (!activeNames.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"Helpful Otter {RandomNumberGenerator.GetInt32(100, 1000)}";
    }

    private static string NormalizeDisplayName(string requestedName)
    {
        var normalizedName = requestedName?.Trim() ?? string.Empty;
        if (normalizedName.Length is < 1 or > MaximumDisplayNameLength ||
            normalizedName.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The display name must be between 1 and {MaximumDisplayNameLength} characters.",
                nameof(requestedName));
        }

        return normalizedName;
    }

    private static bool IsValidColor(string? color) =>
        color is { Length: 7 } &&
        color[0] == '#' &&
        color.AsSpan(1).ToArray().All(char.IsAsciiHexDigit);

    private static IReadOnlyList<RobotCollaborator> CreateCollaboratorSnapshot(
        CollaborationSession session) => session.Connections.Values
        .GroupBy(connection => connection.Identity.Id, StringComparer.Ordinal)
        .Select(group =>
        {
            var identity = group.First().Identity;
            var selectedConnection = group
                .Where(connection => connection.SelectedBlockId is not null)
                .OrderByDescending(connection => connection.SelectionUpdatedAt)
                .FirstOrDefault();
            return new RobotCollaborator(
                identity.Id,
                identity.Name,
                identity.Color,
                selectedConnection?.SelectedBlockId,
                selectedConnection?.SelectedBlockDescription);
        })
        .OrderBy(collaborator => collaborator.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(collaborator => collaborator.Id, StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<RobotCollaboratorCursor> CreateCursorSnapshot(
        CollaborationSession session) => session.Connections.Values
        .Select(connection => connection.Identity.Id)
        .Distinct(StringComparer.Ordinal)
        .Select(identityId => CreateCursorSnapshot(session, identityId))
        .OfType<RobotCollaboratorCursor>()
        .OrderBy(cursor => cursor.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(cursor => cursor.CollaboratorId, StringComparer.Ordinal)
        .ToArray();

    private static RobotCollaboratorCursor? CreateCursorSnapshot(
        CollaborationSession session,
        string identityId)
    {
        var cursorConnection = session.Connections.Values
            .Where(connection =>
                connection.Identity.Id == identityId &&
                connection.CursorWorkspaceX.HasValue &&
                connection.CursorWorkspaceY.HasValue)
            .OrderByDescending(connection => connection.CursorUpdatedAt)
            .FirstOrDefault();
        if (cursorConnection is null)
        {
            return null;
        }

        return new(
            cursorConnection.Identity.Id,
            cursorConnection.Identity.Name,
            cursorConnection.Identity.Color,
            cursorConnection.CursorWorkspaceX!.Value,
            cursorConnection.CursorWorkspaceY!.Value);
    }

    private sealed class CollaborationSession(string workspaceJson, string workspaceName)
    {
        public Dictionary<Guid, Participant> Connections { get; } = [];

        public string WorkspaceJson { get; set; } = workspaceJson;

        public string WorkspaceName { get; set; } = workspaceName;

        public long Revision { get; set; }
    }

    private sealed class Participant(CollaboratorIdentity identity)
    {
        public CollaboratorIdentity Identity { get; set; } = identity;

        public string? SelectedBlockId { get; set; }

        public string? SelectedBlockDescription { get; set; }

        public DateTimeOffset SelectionUpdatedAt { get; set; }

        public double? CursorWorkspaceX { get; set; }

        public double? CursorWorkspaceY { get; set; }

        public DateTimeOffset CursorUpdatedAt { get; set; }
    }
}
