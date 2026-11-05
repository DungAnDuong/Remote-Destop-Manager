# Remote Desktop Manager (RDM)

Phần mềm điều khiển máy tính từ xa cho quản trị IT doanh nghiệp. Đặc tả đầy đủ: [promp.txt](promp.txt).

## Trạng thái

**Chưa phải sản phẩm hoàn chỉnh.** Giai đoạn 1, 2, 3 đã xong. Giai đoạn 4 đã có mã: phiên E2EE, xem màn hình, điều khiển. Giai đoạn 5 đã có mã: kết nối trực tiếp trước, không được thì chuyển tiếp qua Relay. **Chuột và bàn phím thật chưa được kiểm thử, kết nối qua Internet và NAT thật cũng vậy**: mọi kiểm thử Relay đều chạy trên loopback, xem [báo cáo](docs/07-BaoCaoKiemThu.md). Chưa có TCP hole punching. Đã có bộ cài nhưng chưa ký số.

| Giai đoạn | Nội dung | Trạng thái |
|---|---|---|
| 1 | Nền tảng, giao diện Controller, quản lý Client và nhóm, Import/Export | Xong |
| 2 | SQLite, mã hóa, DPAPI, PBKDF2, Key Management, Audit Log, Backup/Restore | Xong |
| 3 | RDM Client, Remote ID, đăng ký, Heartbeat, Online/Offline, xác thực truy cập | Xong |
| 4 | Phiên kết nối trực tiếp, E2EE, Screen Capture, chuột và bàn phím, Windows Service | Đã có mã; chờ kiểm thử input thật và Service |
| 5 | Relay dữ liệu, NAT Traversal, Server Manager | Chưa bắt đầu (`RDM.Server` đã có Device Registry và Heartbeat) |
| 6 | File Transfer, Chat, Multi-Session | Chưa bắt đầu |
| 7 | Kiểm thử đa nền tảng, bộ cài | Chưa bắt đầu |

Kết quả kiểm thử thực tế: [docs/07-BaoCaoKiemThu.md](docs/07-BaoCaoKiemThu.md).

## Yêu cầu để build

- Visual Studio 2022 có workload .NET desktop (targeting pack .NET Framework 4.8).
- .NET SDK x64 có .NET 10 (cho RDM.Server).

## Build và kiểm thử

```powershell
.\build\build.ps1 -Configuration Release -Test   # Build và test cả x86, x64
.\build\verify.ps1 -OutDir C:\temp\rdm-verify     # Nghiệm thu: 4 cấu hình và chạy exe thật
```

Nếu mở bằng Visual Studio, chọn cấu hình `x86` hoặc `x64`; solution không có Any CPU.

**Lưu ý:** script dùng đường dẫn tường minh tới dotnet x64, vì trên máy hiện tại dotnet x86 đứng trước trong PATH và không chạy được SDK.

## Bộ cài đặt

```powershell
powershell -ExecutionPolicy Bypass -File .\build\build-installer.ps1    # Tạo 3 bộ cài trong artifacts\installers
powershell -ExecutionPolicy Bypass -File .\build\verify-installer.ps1   # Kiểm tra mà không cài vào máy
```

Bộ cài chưa có chữ ký số và **chưa từng được chạy cài thật**. Icon được tạo từ `icon.png` bằng `build\make-icon.ps1`. Chi tiết ở [mục 3c của báo cáo](docs/07-BaoCaoKiemThu.md).

## Kiểm thử trên màn hình thật

Chạy khi đang ngồi ở máy, không có việc đang dở. Chuột sẽ di chuyển khoảng 10 giây.

```powershell
.\build\real-desktop-test.ps1 -Platform x64
.\build\real-desktop-test.ps1 -Platform x86
```

## Chạy

```powershell
.\src\RDM.App\bin\x64\Release\RDM.exe
```

Cửa sổ chính mở ngay, không cần đăng nhập để kết nối nhanh. Phần **Quản lý thiết bị** lần đầu sẽ yêu cầu tạo tài khoản Administrator; không có mật khẩu mặc định. Dữ liệu nằm ở `%LOCALAPPDATA%\RDM\`. Đặt `RDM_DATA_DIR` và `RDM_CLIENT_DATA_DIR` để dùng thư mục khác khi thử nghiệm.

Để có trạng thái Online/Offline, cần chạy Relay Server và cấu hình Client, Controller: xem [docs/08-TrienKhaiRelay.md](docs/08-TrienKhaiRelay.md).

## Cấu trúc

| Thư mục | Nội dung |
|---|---|
| `src/RDM.Shared` | Model, crypto, `KeyManager` |
| `src/RDM.Data` | SQLite, Repository, Migration, Backup, dịch vụ nghiệp vụ |
| `src/RDM.App` | Chương trình `RDM.exe`, cửa sổ chính kiểu TeamViewer |
| `src/RDM.UI` | Theme, control dùng chung, icon |
| `src/RDM.Controller` | Vai trò điều khiển: phần quản lý, phiên Remote Desktop |
| `src/RDM.Client` | Vai trò được điều khiển: chụp màn hình, input, Service |
| `src/RDM.Server` | Relay Server: Device Registry, Heartbeat, Online/Offline |
| `tests/RDM.Tests` | Unit, Database, Security, UI Tests |
| `docs` | Thiết kế (01–06), báo cáo kiểm thử (07), triển khai Relay (08) |
| `build` | Script build, kiểm thử, nghiệm thu |

## Thư viện

Xem [docs/05-NuGet.md](docs/05-NuGet.md).
