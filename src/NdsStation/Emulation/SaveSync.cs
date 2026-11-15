using System.Security.Cryptography;
using NdsStation.Services;

namespace NdsStation.Emulation;

public enum SyncState { Synced, Pending, Uploading, Failed }

/// <summary>
/// Keeps the server save in step with the running game. Every write is backed up locally
/// right away, then uploaded after a short quiet period. Uploads only happen when the
/// save differs from what the server already has.
/// </summary>
public sealed class SaveSync : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly ApiClient _api;
    private readonly string _gameId;
    private readonly SemaphoreSlim _uploadLock = new(1, 1);
    private CancellationTokenSource? _debounce;
    private byte[]? _serverHash;
    // Bumped on every change; compared with the generation last uploaded, so a write that
    // lands while an upload is in flight is never mistaken for already synced.
    private int _generation;
    private int _uploadedGeneration;

    public SaveSync(ApiClient api, string gameId, byte[]? serverSave)
    {
        _api = api;
        _gameId = gameId;
        _serverHash = serverSave is null ? null : SHA256.HashData(serverSave);
    }

    /// <summary>
    /// Flags the save as changed without a write from the game, e.g. after loading a
    /// save state or when booting from a local backup the server does not have.
    /// </summary>
    public void MarkDirty() => Interlocked.Increment(ref _generation);

    public SyncState State { get; private set; } = SyncState.Synced;

    public string? LastError { get; private set; }

    /// <summary>Raised on a thread-pool thread.</summary>
    public event Action<SyncState>? StateChanged;

    /// <summary>
    /// Call when the save counter of the core changes. Games write their save in many
    /// small chunks, so both the local backup and the upload wait for writes to settle.
    /// </summary>
    public void OnSaveWritten()
    {
        Interlocked.Increment(ref _generation);
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var token = _debounce.Token;
        SetState(SyncState.Pending);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BackupDelay, token);
                var save = NdsCore.GetSave();
                if (save is null) return;
                bool synced = MatchesServer(save);
                LocalStore.WriteSaveBackup(_gameId, save, pendingUpload: !synced);
                if (synced)
                {
                    SetState(SyncState.Synced);
                    return;
                }

                await Task.Delay(Debounce - BackupDelay, token);
                await FlushAsync(token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });
    }

    private static readonly TimeSpan BackupDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Uploads the current save if the server does not have it yet.</summary>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        await _uploadLock.WaitAsync(ct);
        try
        {
            // A game that never wrote its save has nothing new for the server.
            int generation = Volatile.Read(ref _generation);
            var save = generation != _uploadedGeneration ? NdsCore.GetSave() : null;
            if (save is null || MatchesServer(save))
            {
                if (save is not null) LocalStore.MarkSaveUploaded(_gameId);
                SetState(SyncState.Synced);
                return true;
            }

            LocalStore.WriteSaveBackup(_gameId, save, pendingUpload: true);
            SetState(SyncState.Uploading);
            try
            {
                await _api.UploadSaveAsync(_gameId, save, ct);
            }
            catch (ApiException ex)
            {
                LastError = ex.Message;
                SetState(SyncState.Failed);
                return false;
            }

            _serverHash = SHA256.HashData(save);
            _uploadedGeneration = generation;
            LocalStore.MarkSaveUploaded(_gameId);
            LastError = null;
            SetState(SyncState.Synced);
            return true;
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    public void CancelPending() => _debounce?.Cancel();

    private bool MatchesServer(byte[] save) =>
        _serverHash is not null && SHA256.HashData(save).AsSpan().SequenceEqual(_serverHash);

    private void SetState(SyncState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _uploadLock.Dispose();
    }
}
