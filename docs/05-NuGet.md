# 05 – Thư viện NuGet (Bước 7)

Tiêu chí: tương thích .NET Framework 4.8, chạy được cả x86 và x64. Ưu tiên thư viện managed để giảm rủi ro nạp DLL native.

| Package | Dùng ở | Mục đích | Native? | Ghi chú tương thích |
|---|---|---|---|---|
| `BouncyCastle.Cryptography` 2.5.1 | Shared | AES-256-GCM, PBKDF2; từ Giai đoạn 4: X25519, Ed25519, HKDF | Không | .NET 4.8 không có `AesGcm`; netstandard2.0 không có `Rfc2898DeriveBytes` với SHA-256 |
| `System.Security.Cryptography.ProtectedData` | Shared | DPAPI | Không | Chỉ dùng trên Windows |
| `System.Data.SQLite.Core` 1.0.119 | Data | SQLite | **Có**: `x86\` và `x64\SQLite.Interop.dll` | Có đủ cả hai kiến trúc. Nhúng SQLite 3.46.1, chưa có bản vá CVE-2025-6965, xem `07-BaoCaoKiemThu.md` |
| `NLog` | Shared, Data, Controller | Logging | Không | Hỗ trợ .NET Framework |
| `ClosedXML` | Data | Import/Export Excel | Không | Kéo theo OpenXML SDK |
| `Microsoft.Data.Sqlite` 10.0.14 | Server | Registry thiết bị của Relay, tách khỏi Database của Controller | Có (kèm theo package) | Chỉ dùng trên Server, không ảnh hưởng Windows cũ |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | Tests | Kiểm thử | Không | Chạy bằng `vstest.console` với `/Platform:x86` hoặc `x64` |

**Không dùng ORM:** Repository viết bằng ADO.NET thuần với tham số hóa; ít phụ thuộc, dễ kiểm soát trên Windows cũ.

**Biểu đồ:** tự vẽ bằng GDI+, không thêm thư viện.

**Cần đánh giá ở các giai đoạn sau:** Media Foundation qua P/Invoke cho H.264 (không thêm NuGet nếu được); Inno Setup hoặc WiX cho bộ cài (máy này đã có WiX 5.0.2).

Phiên bản chính xác được ghi trong từng `.csproj`, và kết quả kiểm tra tương thích thực tế nằm trong `07-BaoCaoKiemThu.md`. Tính tương thích với Windows 7 và Server 2012 mới dựa trên mục tiêu framework của package, **chưa được chạy thử** trên các hệ điều hành đó.
