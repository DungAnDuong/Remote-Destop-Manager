# 03 – Thiết kế Database SQLite (Bước 5)

**File:** `RDM_Database.db`, tại `%LOCALAPPDATA%\RDM\` (ACL của user). Có thể đổi thư mục bằng biến môi trường `RDM_DATA_DIR`.

**Cấu hình kết nối:** WAL, `foreign_keys=ON`, `busy_timeout=5000`. Mỗi thao tác mở một connection ngắn (có pool) và không giữ transaction qua lời gọi mạng hay UI, nên không khóa Database kéo dài.

## 1. Sơ đồ quan hệ

```mermaid
erDiagram
  Groups ||--o{ Clients : "chứa"
  Groups ||--o{ Groups : "cha"
  Clients ||--o{ ClientTags : ""
  Tags ||--o{ ClientTags : ""
  Users ||--o{ Favorites : ""
  Clients ||--o{ Favorites : ""
  Clients ||--o{ ConnectionHistory : "tham chiếu mềm qua RemoteID"
```

## 2. Các bảng

Cột đánh dấu 🔒 là bản mã AES-256-GCM.

| Bảng | Cột chính | Ghi chú |
|---|---|---|
| **Clients** | ID, DeviceID (duy nhất), RemoteID (duy nhất), DeviceName, Description, GroupID, EncryptedCredential 🔒, IPAddress, MacAddress, OperatingSystem, Architecture, LastOnline, CreatedDate, IsFavorite, IsEnabled | RemoteID là khóa nghiệp vụ |
| **Groups** | ID, Name (duy nhất, không phân biệt hoa thường), Description, ParentID, CreatedDate | Xóa nhóm thì thiết bị về "không nhóm" |
| **ConnectionHistory** | ID, RemoteID, DeviceName, ControllerUser, StartTime, EndTime, Duration, ConnectionType, ConnectionStatus | Giữ DeviceName để lịch sử còn nguyên khi thiết bị bị xóa |
| **Users** | ID, UsernameHash, EncryptedUsername 🔒, PasswordHash 🔒, EncryptedFullName 🔒, EncryptedEmail 🔒, Role, IsEnabled, FailedAttempts, LockedUntil, MustChangePassword, CreatedDate, LastLogin | `UsernameHash` là HMAC-SHA256 để tra cứu mà không lộ tên |
| **Settings** | Key, Value, IsEncrypted, UpdatedDate | Giá trị nhạy cảm dùng `IsEncrypted=1`, Value 🔒 |
| **Favorites** | UserID, ClientID, CreatedDate | Yêu thích theo từng người dùng |
| **AuditLogs** | ID, Timestamp, EventType, Severity, Username, RemoteID, DeviceName, Details, Success | Không bao giờ chứa secret |
| **SecurityKeys** | ID, Purpose, Version, Status, WrappedKey, ProtectionScheme, CreatedDate, RotatedDate, RevokedDate, Fingerprint | `WrappedKey` được bảo vệ bằng DPAPI. `Fingerprint` là SHA-256 rút gọn của khóa |
| **Tags, ClientTags** | ID, Name / ClientID, TagID | Đáp ứng "Gắn nhãn" (Phần 6) |
| **SchemaVersion** | Version, AppliedDate, Description | Lịch sử migration |

**Về `Clients.IsFavorite` và bảng `Favorites`:** đặc tả yêu cầu cả hai. `Favorites` là nguồn chính (theo từng người dùng). `IsFavorite` được cập nhật trong cùng transaction và có nghĩa "có ít nhất một người dùng đánh dấu".

## 3. Index

`Clients(GroupID)`, `Clients(DeviceName)`, `Clients(LastOnline)`, `ConnectionHistory(StartTime)`, `ConnectionHistory(RemoteID)`, `AuditLogs(Timestamp)`, `AuditLogs(EventType)`, `SecurityKeys(Purpose, Status)`. RemoteID, DeviceID, UsernameHash có index duy nhất.

## 4. Migration

- `PRAGMA user_version` cùng bảng `SchemaVersion`. Migration là danh sách theo phiên bản, mỗi bước chạy trong một transaction.
- File DB có phiên bản **mới hơn** ứng dụng: từ chối mở, không ghi đè.
- Trước khi nâng phiên bản DB đã có dữ liệu: tự sao lưu `RDM_Database.pre-v{N}.bak`.

## 5. Mã hóa: quyết định

**Chọn mã hóa từng trường**, không mã hóa toàn bộ file.

| Phương án | .NET 4.8, x86/x64 | Vấn đề |
|---|---|---|
| SEE | Có | Giấy phép trả phí |
| SQLCipher qua Microsoft.Data.Sqlite | Có thể | Đổi driver, thêm native, chưa có kiểm chứng trên Windows 7 |
| **Mã hóa từng trường** | **Có** | Metadata (tên máy, IP) vẫn đọc được nếu file bị đánh cắp |

**Rủi ro còn lại, cần chủ dự án chấp nhận:** kẻ trộm file DB thấy được tên thiết bị, IP, Remote ID, lịch sử kết nối và tên người dùng xuất hiện trong `AuditLogs` / `ConnectionHistory`, nhưng không thấy mật khẩu, credential, token, họ tên, email hay khóa. Nếu cần che cả metadata, nên dùng BitLocker cho ổ đĩa.

## 6. Backup/Restore

- Dùng SQLite Online Backup API, nên có bản nhất quán ngay cả khi đang ghi.
- File `.rdmbak` = header + AES-256-GCM(DB + khóa dữ liệu), khóa sinh từ **mật khẩu backup** qua PBKDF2. Khóa DPAPI không dùng được trên máy khác nên khóa dữ liệu được bọc lại bằng mật khẩu backup.
- Restore: xác thực GCM (sai mật khẩu hoặc file bị sửa đều bị từ chối) → kiểm tra tính toàn vẹn SQLite → sao lưu DB hiện tại → thay thế → bọc lại khóa bằng DPAPI của máy mới.
