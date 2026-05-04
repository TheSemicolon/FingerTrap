namespace FingerTrap.Sidecar.Ipc;

public sealed record PtySpawnRequest(
    string SessionId,
    string? Shell,
    string? Cwd,
    int Cols,
    int Rows,
    IReadOnlyDictionary<string, string>? Env);

public sealed record PtySpawnResult(int Pid);

public sealed record PtyWriteRequest(string SessionId, string DataBase64);

public sealed record PtyResizeRequest(string SessionId, int Cols, int Rows);

public sealed record PtyOutputNotification(string SessionId, string DataBase64);

public sealed record PtyExitNotification(string SessionId, int ExitCode);
