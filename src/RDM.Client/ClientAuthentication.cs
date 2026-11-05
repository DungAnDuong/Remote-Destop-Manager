using System;
using RDM.Shared.Common;
using RDM.Shared.Security;

namespace RDM.Client;

public sealed class ClientValidationException : Exception
{
    public ClientValidationException(string message) : base(message) { }
}

public enum AccessDecision { DeniedDisabled, DeniedLocked, DeniedBadPassword, RequireOwnerApproval, Allowed }

/// <summary>
/// Quản lý mật khẩu truy cập và quyết định có cho phép kết nối. Đây là mật khẩu RDM, không phải mật khẩu Windows.
/// Quy tắc: chủ máy tắt Remote Access thì luôn từ chối. Unattended chỉ có khi chủ máy chủ động bật và đã đặt mật khẩu.
/// Mọi trường hợp khác đều phải được chủ máy chấp nhận trên màn hình.
/// </summary>
public sealed class ClientAuthentication
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly ClientStateStore _store;
    private readonly ClientState _state;
    private readonly PasswordHasher _hasher;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new object();

    public ClientAuthentication(ClientStateStore store, ClientState state, int iterations = PasswordHasher.DefaultIterations, Func<DateTime> utcNow = null)
    {
        _store = store; _state = state; _hasher = new PasswordHasher(iterations); _now = utcNow ?? (() => DateTime.UtcNow);
    }

    public bool HasPassword => _state.AccessPasswordHash != null;
    public bool IsLocked => _state.LockedUntilUtc.HasValue && _state.LockedUntilUtc.Value > _now();

    /// <summary>Đặt mật khẩu mới. Nếu đã có mật khẩu thì phải nhập đúng mật khẩu hiện tại.</summary>
    public void SetPassword(string currentPassword, string newPassword)
    {
        lock (_gate)
        {
            if (HasPassword && !_hasher.Verify(currentPassword, _state.AccessPasswordHash, out _))
                throw new ClientValidationException("The current password is incorrect.");
            var problem = PasswordPolicy.Validate(newPassword);
            if (problem != null) throw new ClientValidationException(problem);
            _state.AccessPasswordHash = _hasher.Hash(newPassword);
            _state.FailedAttempts = 0;
            _state.LockedUntilUtc = null;
            _store.Save(_state);
        }
    }

    /// <summary>Xóa mật khẩu; Unattended cũng tắt theo vì không còn gì bảo vệ.</summary>
    public void ClearPassword(string currentPassword)
    {
        lock (_gate)
        {
            if (HasPassword && !_hasher.Verify(currentPassword, _state.AccessPasswordHash, out _))
                throw new ClientValidationException("The current password is incorrect.");
            _state.AccessPasswordHash = null;
            _state.UnattendedEnabled = false;
            _store.Save(_state);
        }
    }

    public void SetUnattended(bool enabled)
    {
        lock (_gate)
        {
            if (enabled && !HasPassword) throw new ClientValidationException("Set an access password before enabling Unattended Access.");
            _state.UnattendedEnabled = enabled;
            _store.Save(_state);
        }
    }

    public void SetAccessEnabled(bool enabled)
    {
        lock (_gate)
        {
            _state.AccessEnabled = enabled;
            _store.Save(_state);
        }
    }

    /// <summary>Tham số gửi cho bên kết nối để tính bằng chứng; null nếu chưa có mật khẩu.</summary>
    public bool TryGetProofParameters(out byte[] salt, out int iterations)
    {
        salt = null; iterations = 0;
        var parts = _state.AccessPasswordHash?.Split('$');
        if (parts == null || parts.Length != 4) return false;
        iterations = int.Parse(parts[1]);
        salt = Convert.FromBase64String(parts[2]);
        return true;
    }

    /// <summary>
    /// Quyết định cho một yêu cầu kết nối. Bằng chứng gắn với transcript phiên nên không phát lại được (xem docs/04-Security.md).
    /// Giai đoạn 4 sẽ gọi hàm này sau khi bắt tay E2EE.
    /// </summary>
    public AccessDecision Evaluate(byte[] transcript, byte[] proof)
    {
        lock (_gate)
        {
            if (!_state.AccessEnabled) return AccessDecision.DeniedDisabled;
            if (IsLocked) return AccessDecision.DeniedLocked;
            if (!HasPassword) return AccessDecision.RequireOwnerApproval;

            if (!VerifyProof(transcript, proof))
            {
                var attempts = _state.FailedAttempts + 1;
                var lockNow = attempts >= MaxFailedAttempts;
                _state.FailedAttempts = lockNow ? 0 : attempts;
                _state.LockedUntilUtc = lockNow ? _now() + LockoutDuration : (DateTime?)null;
                _store.Save(_state);
                return lockNow ? AccessDecision.DeniedLocked : AccessDecision.DeniedBadPassword;
            }

            if (_state.FailedAttempts != 0) { _state.FailedAttempts = 0; _store.Save(_state); }
            return _state.UnattendedEnabled ? AccessDecision.Allowed : AccessDecision.RequireOwnerApproval;
        }
    }

    private bool VerifyProof(byte[] transcript, byte[] proof)
    {
        if (proof == null || transcript == null) return false;
        var parts = _state.AccessPasswordHash.Split('$');
        var key = Convert.FromBase64String(parts[3]);
        try { return PasswordHasher.FixedTimeEquals(AccessProof.ComputeWithKey(key, transcript), proof); }
        finally { Array.Clear(key, 0, key.Length); }
    }
}
