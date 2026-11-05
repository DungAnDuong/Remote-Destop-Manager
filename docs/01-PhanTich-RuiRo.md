# 01 – Phân tích yêu cầu và rủi ro tương thích (Bước 1–2)

## 1. Tóm tắt phạm vi

Ba thành phần độc lập: **Controller** (.NET Framework 4.8, WinForms, SQLite), **Client** (.NET Framework 4.8, Service + Agent trong phiên người dùng), **Relay Server** (ASP.NET Core). Ưu tiên cao nhất là bảo mật (Phần 8). Bảy giai đoạn triển khai tuần tự.

Sản phẩm này điều khiển máy tính từ xa nên ràng buộc về **sự đồng ý của chủ máy** là yêu cầu chức năng, không phải tùy chọn: Client luôn hiển thị thông báo khi có phiên, luôn có nút ngắt khẩn cấp, và Unattended Access chỉ bật khi chủ máy chủ động cấu hình. Không xây dựng chế độ chạy ẩn.

## 2. Các điểm trong đặc tả cần điều chỉnh

Các điểm dưới đây **mâu thuẫn với thực tế nền tảng**. Cần chủ dự án xác nhận cách xử lý đề xuất.

| # | Đặc tả | Thực tế | Đề xuất |
|---|---|---|---|
| A1 | Windows 8 (RTM) x86/x64: "Có" | .NET Framework 4.8 **không hỗ trợ** Windows 8 RTM (chỉ 8.1 trở lên) | Bỏ Windows 8 RTM; yêu cầu nâng lên 8.1 (miễn phí). Bộ cài từ chối và nêu lý do |
| A2 | Windows Server 2012 → 2025 x86: "Có" | Từ Server 2008 R2 trở đi Windows Server **chỉ có bản x64** | Hiểu là: bản x86 *chạy qua WOW64* trên Server x64. Khuyến nghị dùng bản x64. Server Core có thể thiếu WOW64 |
| A3 | Windows 10 x86 | Có, nhưng Windows 11 không có x86 | Đúng như bảng; giữ nguyên |
| A4 | TLS 1.3 "khi nền tảng hỗ trợ" | Schannel chỉ có TLS 1.3 từ Windows 11 / Server 2022 | Chọn động theo hệ điều hành. Không truyền cờ `Tls13` trên OS cũ |
| A5 | AES-256-GCM trên .NET Framework 4.8 | .NET Framework 4.8 **không có** lớp `AesGcm` | Dùng BouncyCastle.Cryptography (managed, chạy được cả x86/x64, không có file native) |
| A6 | Mã hóa toàn bộ Database | `System.Data.SQLite` bản miễn phí không còn hỗ trợ mã hóa; SEE phải trả phí; SQLCipher đòi đổi driver | Chọn **mã hóa từng trường** theo đúng nhánh thay thế đặc tả cho phép (Phần 7). Xem `03-Database.md` |
| A7 | "Kiểm thử thực tế" trên 10 hệ điều hành | Cần máy ảo/máy thật; môi trường hiện tại chỉ có Windows 10 x64 | Báo cáo chỉ ghi Pass cho phần đã chạy thật. Các OS khác ghi **"Chưa kiểm thử"** |
| A8 | Hiệu lực hỗ trợ | Windows 7/8.1/Server 2012 đã hết hỗ trợ từ Microsoft | Vẫn hỗ trợ theo đặc tả nhưng hiện cảnh báo trong Security Center |

**A9. Ngôn ngữ:** chủ dự án yêu cầu toàn bộ giao diện và thông báo của phần mềm bằng tiếng Anh. Chú thích trong code và tài liệu nội bộ giữ tiếng Việt theo Phần 16. Tài liệu đi kèm bộ cài là `docs/RELAY-DEPLOYMENT.md` bằng tiếng Anh; `docs/08-TrienKhaiRelay.md` chỉ dùng nội bộ.

## 3. Ma trận rủi ro tương thích

Mức: **C**ao / **T**rung bình / **Th**ấp. Các mục ghi "cần xác minh" chưa được kiểm chứng trên máy thật.

| Rủi ro | Mức | Ảnh hưởng | Biện pháp |
|---|---|---|---|
| **TLS 1.2 trên Windows 7 / Server 2012**: bộ mã ECDHE-RSA-GCM không có sẵn trên Windows 7 | C | Không kết nối được Relay | Relay dùng chứng thư **ECDSA P-256** (ECDHE-ECDSA-AES-GCM có trên Windows 7). Trên OS cũ, bản cập nhật bắt buộc được bộ cài kiểm tra. Nếu không đạt mức tối thiểu thì **từ chối kết nối Internet**, không hạ cấp (cần xác minh trên VM Windows 7) |
| **Session 0 Isolation**: Service không chụp/điều khiển được desktop | C | Không có hình, không có input | Service chỉ giám sát; dùng `WTSQueryUserToken` + `CreateProcessAsUser` để khởi chạy Agent trong phiên console |
| **Secure Desktop / UAC**: BitBlt và SendInput không tới được desktop Winlogon | C | Mất hình khi UAC hiện | Agent chạy quyền SYSTEM trên desktop `Winlogon` khi Unattended; ở chế độ thường thì báo rõ "UAC đang hiện, chờ người dùng tại máy" |
| **UIPI**: Agent quyền thường không gửi được input tới cửa sổ elevated | T | Mất điều khiển trên ứng dụng admin | Như trên; báo giới hạn trên Toolbar |
| **Ctrl+Alt+Del**: không thể giả lập bằng SendInput | T | Không gửi được SAS | `SendSAS` (sas.dll, Win7+) từ Service; cần chính sách `SoftwareSASGeneration`. Bộ cài xin phép cấu hình |
| **SQLite native x86/x64**: nạp sai `SQLite.Interop.dll` gây `BadImageFormatException` | C | App không khởi động | Build riêng từng kiến trúc; test chạy riêng x86 và x64; bộ cài kiểm tra file |
| **Thiếu VC++ runtime** trên Windows cũ cho `SQLite.Interop.dll` | T | Lỗi nạp DLL trên máy sạch | Cần xác minh trên VM sạch; đưa vào checklist Giai đoạn 7 |
| **Font icon**: `Segoe MDL2 Assets` không có trên Windows 7 | T | Icon thành ô vuông | Dùng `Segoe UI Symbol` (Windows 7 trở lên) |
| **DPI**: Per-Monitor V2 không có trên Windows 7/8.1 | Th | Mờ khi kéo qua màn hình khác DPI | Khai báo System DPI Aware và tự scale theo DPI lúc khởi động |
| **BitBlt và cửa sổ layered / phần cứng tăng tốc** | T | Thiếu hình overlay, video đen | Bật `CAPTUREBLT`; ghi nhận giới hạn |
| **Phiên RDP bị ngắt**: desktop không render | T | BitBlt ra màn hình đen | Phát hiện qua `WTSGetActiveConsoleSessionId` và báo trạng thái |
| **Hiệu năng BitBlt + JPEG** ở 4K | T | Không đạt 15 FPS | So sánh khối (tile diff), chỉ gửi vùng đổi, giảm chất lượng theo băng thông |
| **H.264** qua Media Foundation: không có trên Server nếu thiếu Desktop Experience, cũng thiếu trên bản N | T | Không encode được | JPEG/PNG là nền; H.264 chỉ là tùy chọn khi phát hiện được |
| **Bàn phím tiếng Việt**: Unikey/IME gửi ký tự qua Backspace + Unicode | T | Sai dấu | Ký tự thường gửi bằng `KEYEVENTF_UNICODE`; phím chức năng gửi bằng scan code |
| **Alt+Tab, phím Windows** bị OS của Controller giữ | T | Không tới được máy xa | Low-level keyboard hook khi cửa sổ Remote đang focus |
| **NAT đối xứng**: P2P thất bại | C | Không kết nối trực tiếp | Luôn có Relay dự phòng. E2EE áp dụng cho mọi đường truyền |
| **Chữ ký số**: file không ký dễ bị SmartScreen/AV chặn; phần mềm điều khiển từ xa thường bị AV gắn cờ | C | Bộ cài bị chặn | Cần chứng thư code signing (EV nếu có). Windows 7 cần KB4474419/KB4490628 để xác thực SHA-2 |
| **.NET 4.8 trên Windows 7**: cần KB4019990 | Th | Cài đặt lỗi | Bộ cài kiểm tra |
| **DPAPI**: khóa gắn với user/máy, mất profile là mất khóa | C | Không giải mã được credential | Backup có mật khẩu chứa khóa đã bọc lại (xem `04-Security.md`) |

## 4. Thứ tự giải quyết

1. Xây lõi có thể kiểm thử trước (crypto, database), rồi mới tới UI.
2. Đặt kiểm thử x86 và x64 vào script build ngay từ đầu.
3. Giao thức E2EE được thiết kế ngay từ đây dù tới Giai đoạn 4 mới lập trình, để Relay không phải sửa về sau.
