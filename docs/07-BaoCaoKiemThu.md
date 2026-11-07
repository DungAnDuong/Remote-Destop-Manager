# 07 – Báo cáo kiểm thử

**Ngày chạy:** 05/11/2026. **Phạm vi:** Giai đoạn 1 đến 5, phiên bản 0.6.0. **Tái hiện:** `.\build\verify.ps1 -OutDir <thư mục>` (mất vài phút, có cửa sổ hiện trên màn hình; script trả exit 1 nếu có cấu hình lỗi).

Quy ước theo Phần 18: chỉ ghi **Đạt** cho phần đã thực sự chạy. Phần chưa chạy ghi **Chưa kiểm thử**, không suy đoán.

## 1. Build và kiểm thử tự động

Môi trường: Windows 10 Pro 22H2 x64 (build 19045), Visual Studio 2022 17.14.

| Cấu hình | Build | Lỗi | Test đạt | RDM.exe | SQLite native |
|---|---|---|---|---|---|
| Debug x86 | Thành công | 0 | 251/251 | x86 | x86 |
| Debug x64 | Thành công | 0 | 251/251 | x64 | x64 |
| Release x86 | Thành công | 0 | 251/251 | x86 | x86 |
| Release x64 | Thành công | 0 | 251/251 | x64 | x64 |

**Không nằm trong con số trên:** `RealDesktopTests` (chụp màn hình và chuột, bàn phím thật). Nhóm này chưa từng chạy xong, xem mục 3b. Các test phiên trong bộ 241 dùng màn hình và input **giả lập**.

**Cảnh báo còn lại (1):** `NETSDK1233`, VS 2022 không hỗ trợ chính thức .NET 10 cho RDM.Server. Chủ dự án đã chấp nhận.

**Lỗ hổng thư viện (`dotnet list package --vulnerable`):** không còn gói bị báo. `build-installer.ps1` tự dừng nếu phát hiện. **Máy quét không thấy hết, xem "Rủi ro bảo mật còn mở" ở mục 3c.**

**Đính chính:** báo cáo các lần trước ghi "không có cảnh báo lỗ hổng NuGet". Từ khi thêm `Microsoft.Data.Sqlite` vào Server, điều đó không còn đúng: script build chỉ lọc lỗi nên đã che mất cảnh báo `NU1903` về CVE-2025-6965. Đã vá trên Server; test `Server_RunsPatchedSqlite` chặn SQLite cũ hơn 3.50.2 quay lại.

Test Relay chạy `RDM.Server.exe` thật qua TCP và TLS thật trên loopback, kể cả khi tiến trình test là x86.

## 2. Ma trận hệ điều hành

| Hệ điều hành | x86 | x64 |
|---|---|---|
| Windows 10 (build 19045) | **Đạt một phần**: bản x86 chạy qua WOW64 trên Windows x64, chưa chạy trên Windows 10 x86 thật | **Đạt** |
| Windows 7 SP1 | Chưa kiểm thử | Chưa kiểm thử |
| Windows 8.1 | Chưa kiểm thử | Chưa kiểm thử |
| Windows 11 | Không áp dụng | Chưa kiểm thử |
| Server 2012, 2012 R2, 2016, 2019, 2022, 2025 | Chưa kiểm thử (WOW64) | Chưa kiểm thử |

**Rủi ro lớn nhất còn chưa kiểm chứng:** TLS 1.2 từ Windows 7 tới Relay. Toàn bộ kiểm thử TLS mới chạy Windows 10 ↔ Windows 10 trên loopback.

## 3. Chạy ứng dụng thật

**`build\ui-smoke.ps1`** (Controller bản Release): tạo tài khoản, đăng nhập, mở 12 trang, đổi theme.

| Bản | Kết quả | Tạo tài khoản + đăng nhập | RAM |
|---|---|---|---|
| x86 | Đạt | 5,3 giây | 57 MB |
| x64 | Đạt | 4,3 giây | 67 MB |

**`build\e2e-smoke.ps1`** (Relay Server và hai `RDM.exe` thật, thao tác qua giao diện, cả x86 và x64):

| Bước | Kết quả |
|---|---|
| Server khởi động với chứng thư sinh bằng `--generate-cert` | Đạt |
| Client sinh Remote ID, cấu hình qua hộp thoại Settings, lên Online qua TLS | Đạt |
| Controller tạo tài khoản, cấu hình Relay, thêm thiết bị qua hộp thoại Add Client | Đạt |
| Controller hiện Online, Dashboard đếm đúng Online 1 / Offline 0 | Đạt |
| Chủ máy tắt Remote Access, Controller hiện "Online (tắt truy cập)" | Đạt |
| Kết thúc Client, Controller hiện Offline | Đạt |
| Tắt Relay, Controller hiện "Chưa rõ" và vẫn phản hồi | Đạt |

Script này kiểm tra bằng thao tác và tiến trình; **trạng thái hiển thị được đối chiếu bằng cách xem ảnh chụp**: toàn bộ các bước ở bản x64 Debug, và các bước Client Online, Offline ở bản x86 Release. Script tự nó không đọc được nội dung ô trong bảng.

| Kết nối nhanh không đăng nhập; hộp thoại fingerprint; nhập sai bị từ chối | Đạt |
| Kết nối từ danh sách: khóa đã ghim nên không hỏi lại fingerprint | Đạt |
| Đăng xuất phần quản lý quay về cửa sổ chính, chương trình không thoát | Đạt |
| Chủ máy chấp nhận trên Client, mở cửa sổ Remote, phiên E2EE | Đạt |
| Thanh báo "Đang bị điều khiển" hiện trên máy đích | Đạt |
| Chủ máy bấm "Ngắt ngay": phiên đóng, Controller báo lý do và không tự kết nối lại | Đạt |
| Active Sessions và Connection History ghi đúng | Đạt (đối chiếu ảnh ở x64) |

**Chưa chạy thao tác thật trên giao diện:** hộp chọn file khi Import/Export và Backup/Restore; đổi mật khẩu Client qua hộp thoại; Edit Client; DPI khác 100%; kéo cửa sổ qua màn hình khác DPI; Full Screen; đổi Monitor và chất lượng từ toolbar. Logic phía sau đa số đã có test ở tầng Service.

## 3c. Bộ cài đặt (phiên bản 0.6.0)

Tạo bằng `.\build\build-installer.ps1`, trong `artifacts\installers`. Kiểm tra bằng `.\build\verify-installer.ps1`: bung từng MSI ra thư mục tạm, **không cài vào máy**.

| Bộ cài | Kích thước | Kiểm tra trên file đã bung |
|---|---|---|
| `RDM_Setup_x64.exe` | 7,8 MB | Đủ file, đúng kiến trúc, đúng SQLite native, có icon, mở được cửa sổ |
| `RDM_Setup_x86.exe` | 7,6 MB | Như trên |
| `RDM_Server_Setup.exe` | 44,2 MB | Đủ file, tự chứa runtime, sinh được chứng thư, trả lời `/health` |

**Điều kiện cài của bộ cài Server đã đối chứng trên Windows 10:** điều kiện ngược `VersionNT < v10.0` bị chặn đúng, không ghi gì vào máy.

**Giới hạn của `/layout`:** chế độ này không đánh giá điều kiện, nên exit 0 không chứng minh gì về điều kiện cài.

**Chủ dự án đã cài bản trước đó:** `C:\Program Files\RDM\RDM.exe` đang chạy trên máy phát triển, nên cài đặt thực tế có vẻ đã thành công. Đây là quan sát, không phải kiểm thử; nâng cấp lên 0.6.0 chưa được thử.

**Chưa kiểm thử (cần quyền Administrator và làm thay đổi hệ thống):** cài xong trọn vẹn do người kiểm thử đối chiếu từng mục, giao diện bộ cài, ghi vào Program Files, shortcut, luật tường lửa, tự gỡ bản 0.4.0, nâng cấp, gỡ cài đặt, cài trên máy sạch hay Windows 7.

**Hành vi đã thiết kế:**
- Bộ cài kiểm tra .NET Framework 4.8 và phiên bản Windows. **Không kèm** .NET 4.8: thiếu thì báo và dừng.
- **Bộ cài tự mở tường lửa cho RDM.exe trên mạng Domain và Private**, không mở trên mạng Public. Đây là thay đổi bảo mật trên mọi máy cài.
- **Bộ cài tự gỡ bản Controller, Client 0.4.0** nếu có. Remote ID và dữ liệu nằm ở `%LOCALAPPDATA%\RDM` nên được giữ lại.
- **Windows Service không được cài** vì chưa kiểm thử. Quản trị viên tự chạy `RDM.exe --install-service` nếu cần.
- **Relay Server chỉ cài file**, không tự chạy như dịch vụ Windows.
- Gỡ cài đặt không đụng dữ liệu người dùng (`%LOCALAPPDATA%\RDM`, `%ProgramData%\RDM\Relay`) và `appsettings.json` của Server được giữ lại.

**Chưa có chữ ký số.** Windows SmartScreen sẽ cảnh báo, và antivirus có thể chặn vì đây là phần mềm điều khiển từ xa. Không nên phân phối rộng khi chưa ký.

**Rủi ro bảo mật còn mở: SQLite của Controller.** `System.Data.SQLite.Core` 1.0.119 là bản mới nhất trên NuGet và nhúng SQLite **3.46.1**, thấp hơn 3.50.2 là bản vá CVE-2025-6965. Máy quét NuGet không báo gói này. Lỗi cần kẻ tấn công chạy được SQL tùy ý; Controller chỉ dùng câu lệnh cố định có tham số, nên đánh giá rủi ro thực tế thấp. Muốn hết hẳn thì phải đổi driver SQLite. Tạm giữ và theo dõi bản mới theo khuyến nghị; chủ dự án không có ý kiến khác (05/11/2026). Client không dùng SQLite.

**Lỗi chưa rõ nguyên nhân:** kịch bản e2e bản x86 từng dừng giữa chừng một lần ở phiên bản 0.5.1, sau bước kết nối nhanh. Thông báo lỗi bị mất do bộ lọc output, và `verify.ps1` lúc đó không phát hiện. Lần đó phiên Windows có thể có người đang dùng. Chạy lại 3 lần đều đạt, chưa tái hiện được. `verify.ps1` giờ bắt buộc mọi kịch bản phải báo hoàn tất và thất bại nếu không.

## 3d. Chuyển tiếp qua Relay (Giai đoạn 5)

**Mọi kết quả dưới đây đều chạy trên một máy, qua loopback.** Chúng chứng minh giao thức và mã chạy đúng, **không** chứng minh chạy được qua Internet.

| Hạng mục | Kết quả | Cách kiểm |
|---|---|---|
| Phiên đầy đủ qua Relay: hình, chữ, khung thay đổi | **Đạt** | `RDM.Server.exe` thật, máy đích không lắng nghe cổng nào, màn hình và input giả lập |
| Từ giao diện thật: trực tiếp không tới được thì tự chuyển sang Relay | **Đạt** (x86, x64) | `e2e-smoke`: hai `RDM.exe`, địa chỉ công bố không tới được, thanh trạng thái hiện `via Relay`; đối chiếu ảnh Connection History ở x64: phiên trước `Direct`, phiên sau `Relay` |
| Mật khẩu, khóa ghim, công tắc khẩn cấp qua Relay | **Đạt** | Sai mật khẩu bị từ chối; từ chối khóa thì không hỏi chủ máy; chủ máy ngắt thì không tự kết nối lại |
| 5 phiên đồng thời không lẫn dữ liệu | **Đạt** | Mỗi phiên gửi chữ riêng, kiểm từng máy đích nhận đúng |
| Vé: dùng lại, bịa, hết hạn | **Đạt** | Đều bị từ chối |
| Trả lời đúng trạng thái: không tồn tại, Offline, tắt truy cập, tự kết nối chính mình | **Đạt** | Mã tương ứng, không phát vé |
| Giới hạn: yêu cầu mỗi phút, số phiên đồng thời | **Đạt** | Cấu hình thấp rồi vượt ngưỡng |
| Một đầu rời đi, bên nhận không đọc | **Đạt** | Relay đóng phiên. Test bên nhận không đọc đã được đối chứng: bỏ bản sửa thì test thất bại |
| Log Relay không chứa nội dung phiên, tên người dùng | **Đạt** | Quét output của Server |
| Thông lượng Relay trên loopback | **~76–78 MB/s** | Khối 256 KB, **chưa tính E2EE**, cả bốn cấu hình. Chỉ là giới hạn trên của phần chuyển tiếp |

**Chưa kiểm thử:**
- **Qua Internet, NAT, tường lửa, router thật, hai máy khác nhau.**
- Mất mạng giữa phiên qua Relay; đổi mạng khi đang kết nối.
- FPS và độ trễ qua Relay trên mạng thật. **Băng thông thực tế của Relay chưa đo.**
- Relay chịu tải: mới có 5 phiên đồng thời. `MaxRelaySessions` 1000 là giới hạn cấu hình, chưa phải năng lực đã kiểm chứng.
- **Relay chạy như dịch vụ Windows:** có mã và có hướng dẫn, chưa từng chạy.
- Trực tiếp qua địa chỉ thật trong LAN khi có Relay: chỉ thử với địa chỉ không tới được.

**Không có TCP hole punching.** Phần 19 có "NAT Traversal": hiện chỉ đạt theo nghĩa kết nối ra ngoài qua Relay, không có xuyên NAT trực tiếp. Muốn tiết kiệm băng thông Relay thì cần giai đoạn riêng, có thể cần máy chủ phối hợp và vẫn không chắc thành công với NAT đối xứng.

## 3e. Relay đóng gói sẵn (phiên bản 0.6.1)

Bản Release mang sẵn `erp247.ddns.net:31408`, khóa ghim và mã đăng ký; bản Debug không có. Cổng 443 trên máy chủ đang do Caddy phục vụ website khác nên Relay dùng cổng riêng.

**Đã kiểm tra:**
- 251/251 trên 4 cấu hình; e2e và ui-smoke đạt trên x86 và x64.
- Áp dụng một lần, giữ Relay của chủ máy, xóa rồi không tự điền lại: có test.
- Chuỗi Relay có trong `RDM.Client.dll` bản Release, không có trong bản Debug. Script e2e đặt `RDM_NO_RELAY_DEFAULTS=1` để không chạm Relay thật.
- Relay nạp chứng thư thật và nghe trên loopback, nhật ký báo đúng khóa ghim, hạn 2036-11-02.

**Chưa kiểm tra:**
- `build\deploy-relay.ps1` chưa từng chạy, vì cần quyền Administrator: cài dịch vụ, chạy bằng LocalService, mở tường lửa.
- Chuyển cổng router, thiết bị thật đăng ký và phiên qua `erp247.ddns.net:31408`. Thiết bị thật chưa bao giờ đăng ký với Relay này.
- Mã đóng gói áp dụng trên bản cài thật; test chỉ gọi trực tiếp hàm.

**Vấn đề còn mở:**
- Một lần nghiệm thu có `e2e-smoke x64` kết thúc không in dòng nào, trong vòng 4 giây sau ui-smoke. Không có sự cố trong nhật ký Windows, chưa rõ nguyên nhân. Chạy lại ngay sau đó 3/3 đạt. Kịch bản dừng trước cả bước khởi động Relay, nhưng không loại trừ được hoàn toàn lỗi môi trường liên quan tới phần mềm trên máy. Sau đó Kaspersky báo `PDM:Trojan.Win32.Generic` trên `build\e2e-smoke.ps1` và file này biến mất khỏi đĩa: có thể cùng nguyên nhân, chưa chứng minh. Chưa rõ RDM.exe có bị phát hiện tương tự trên máy khác hay không.
- Máy này có Kaspersky. PowerShell dò cổng thấy chứng thư RSA tự ký khác, cùng tên, thumbprint đổi mỗi lần: nghi là tính năng quét kết nối mã hóa, chưa chứng minh. RDM từ chối mọi chứng thư sai khóa ghim, nên máy có phần mềm chặn TLS kiểu này sẽ không kết nối được Relay cho tới khi loại trừ RDM.exe.
- Mã đăng ký nằm trong bộ cài nên ai có bộ cài cũng trích xuất được; thu hồi thì phải cài lại mọi máy.

**Chế độ Managed (0.6.3):** bản Release có cấu hình đóng gói không hiện tên miền, cổng, khóa ghim, mã đăng ký hay chữ Relay/Server trên giao diện; đường truyền hiện là `Internet`. Mỗi lần khởi động đều áp lại cấu hình đóng gói, thay cho quy tắc "chỉ một lần" của 0.6.1. Kết quả: 251/251 trên Debug x86, Debug x64 và Release x86; Release x64 250/251. Test hỏng là `LanDiscoveryTests.FindsRightId_ReturnsSessionPort` (hết thời gian chờ 3 giây); chạy lại riêng nhóm này 8/8 đạt. Truy vấn chỉ gửi một gói UDP, không gửi lại, nên mất gói là không tìm thấy; nguyên nhân mất gói chưa xác định. **Chưa kiểm tra giao diện Managed bằng mắt hay bằng script**, vì `ui-smoke` và `e2e-smoke` không được chạy do cảnh báo Kaspersky. File log vẫn có chữ "Relay" nhưng không ghi tên miền. Tên miền vẫn tìm được bằng công cụ xem kết nối mạng hoặc đọc file DLL.

**Giao diện khay và Remember password (0.6.5):** máy bị điều khiển bỏ thanh đỏ và bong bóng, chỉ còn chỉ báo lặng trên biểu tượng khay của cửa sổ chính (chú thích "Being controlled by …" và lệnh Disconnect) — vẫn công khai, không phải cảnh báo bật lên. Đóng cửa sổ chỉ thu nhỏ xuống khay; thoát hẳn qua menu Exit. Hộp đăng nhập có ô "Remember password" (lưu bằng DPAPI theo tài khoản Windows). Bản Managed bỏ ô cổng trong Settings. Chưa kiểm tra giao diện bằng mắt hay script.

**Giai đoạn 6 - File Transfer và Chat (0.7.0):** chat và truyền file đi trên cùng kênh E2EE của phiên, không mở cổng mới. File nhận về lưu vào `Documents\RDM Received Files`, tên file bị cắt chỉ còn phần tên (chặn vượt thư mục), không ghi đè, không chạy. Phía nhận tự nhận vì phiên đã xác thực; hai bên đều thấy trong bảng chat. Máy điều khiển: menu khay có "Send File..." và "Chat" (bảng chat gắn bên phải cửa sổ). Máy bị điều khiển: cửa sổ chat tự mở khi có tin hoặc file. Kiểm thử engine (`SessionTransferTests`) 5/5: chat tới nơi; file 300 KB nguyên vẹn; tên vượt thư mục bị cắt về đúng thư mục nhận; không ghi đè (đổi thành "(1)"); khúc dữ liệu quá lớn bị từ chối. **Chưa kiểm tra giao diện chat/file bằng mắt hay giữa hai máy thật.**

**Trình quản lý file, chat đẹp hơn, nhiều màn hình (0.8.0):**
- Truyền file kiểu AnyDesk: cửa sổ File Manager hai khung (máy điều khiển trái, máy ở xa phải), duyệt thư mục/ổ đĩa của máy ở xa, Upload/Download. Giao thức duyệt thư mục (FsList/FsListReply/FsGet) và truyền file có thư mục đích đi trên cùng kênh E2EE. Theo yêu cầu, máy bị điều khiển **không hiển thị gì** khi truyền file (chỉ chat mới mở cửa sổ). File vẫn lưu an toàn: tên bị cắt chống vượt thư mục, không ghi đè, không tự chạy; thư mục đích là đường dẫn tuyệt đối do người dùng duyệt tới (toàn quyền hệ thống như AnyDesk, giới hạn theo quyền của tài khoản đang đăng nhập).
- Chat vẽ lại kiểu bong bóng, có thanh tiêu đề và nút đóng.
- Nhiều màn hình: thanh nút nổi ở đáy cửa sổ Remote (1, 2, ..., All) và phím tắt Ctrl+1..9 chọn màn hình, Ctrl+0 xem tất cả. Mục "All Monitors" chụp toàn vùng ảo, thêm tự động ở máy đích khi có từ 2 màn hình.
- Kiểm thử engine 8/8 (thêm: duyệt thư mục trả đúng mục, upload lưu đúng thư mục đích, download về đúng thư mục local).
- **Chưa kiểm tra giao diện các phần này bằng mắt hay giữa hai máy thật.** Bảo mật truyền file: hiện tự nhận, chưa có bước hỏi đồng ý từng file.

**Thoát toàn màn hình (0.7.0):** ở chế độ full không còn thanh công cụ, nên thêm phím F11 (chặn tại máy điều khiển, không gửi sang máy đích) và nút nổi "Exit Full Screen" ở mép trên; menu khay vẫn có mục Full Screen.

**Lỗi cạnh tranh ở công tắc khẩn cấp (sửa 0.6.5):** khi chủ máy ngắt, máy chủ gửi khung "owner ended" rồi đóng socket ngay; ở bản Release, socket bị đóng (kèm RST) trước khi khung tới nơi, nên Controller nhận nhầm là mất kết nối (byUser=false) và có thể tự kết nối lại. Chặn kết nối mới (OwnerBlock) vẫn chặn được, nhưng tín hiệu "có chủ ý" không đáng tin. `OwnerDisconnectAll_EndsSession_AndReleasesInput` hỏng 100% ở Release (cả x86 và x64), đạt ở Debug. Sửa: sau khi gửi khung, chờ ngắn để Controller đọc xong và tự đóng trước (đóng nhẹ nhàng). Sau khi sửa: 10/10 ở cả Release x86 và x64.

**Lỗi phát hiện và sửa trong đợt này:**
- `--hash-token` nhận BOM khi PowerShell 5.1 chuyển qua đường ống, làm sai mã băm và Relay sẽ từ chối mọi thiết bị mới. Đã xác nhận bằng so sánh SHA-256 và sửa ở cả hai phía.
- Controller có thể hiện Online sai nếu tin Offline gửi trước ảnh chụp cũ. Phát hiện khi đọc mã, đã sửa thứ tự gửi; không có test tái hiện đúng thời điểm này.
- `Controller_SnapshotIncludesOfflineRegisteredDevices` và e2e dựa vào thời gian chờ cố định: đổi sang chờ theo điều kiện.

## 3b. Màn hình và input thật: phần quan trọng nhất còn thiếu

| Hạng mục | Trạng thái |
|---|---|
| Chụp màn hình thật, qua E2EE, hiển thị trên Controller | **Đạt một phần:** thấy được một lần trong lần chạy `e2e-smoke` bản x86 Release, trên một màn hình 1920x1200. Không có phép đo |
| **Chuột thật qua SendInput** | **Chưa kiểm thử** |
| **Bàn phím thật, tổ hợp phím, tiếng Việt, Unikey** | **Chưa kiểm thử** |
| **Low-level hook, Alt+Tab, phím Windows** | **Chưa kiểm thử** |
| **FPS trên màn hình đang thay đổi** (mục tiêu 15–30) | **Chưa đo.** Lần duy nhất thấy được là màn hình gần như đứng yên |
| Nhiều màn hình | **Chưa kiểm thử** (chỉ có test với màn hình giả) |
| Khi phiên Windows bị ngắt khỏi màn hình | **Đạt:** không lỗi, Controller báo rõ tình trạng |

Lý do: trong phần lớn thời gian kiểm thử, phiên Windows bị ngắt khỏi màn hình (Session 1, console ở Session 4), nên BitBlt bị từ chối. Test input thật không được chạy tự động vì nếu người dùng đang dùng máy, chữ thử nghiệm có thể bị gõ vào ứng dụng khác.

**Cách hoàn tất:** khi đang ngồi ở máy, không có việc gì đang dở, chạy `.\build\real-desktop-test.ps1` rồi `.\build\real-desktop-test.ps1 -Platform x86`. Chuột sẽ di chuyển khoảng 10 giây. Test in ra số FPS đo được.

**Windows Service (`--install-service`): chưa từng chạy.** Cần quyền Administrator và là thay đổi hệ thống, nên không tự cài lên máy phát triển. Gồm cả Ctrl+Alt+Del qua `SendSAS` và khởi chạy Agent trong phiên người dùng. Nên thử trên máy ảo.

**Không phải hai máy khác nhau:** mọi tiến trình chạy trên cùng một máy qua loopback. Tiêu chí Phần 19 "hai máy tính Windows khác nhau" và NAT, tường lửa thật vẫn chưa kiểm thử.

## 4. Hiệu năng đo được

| Chỉ số | x86 | x64 |
|---|---|---|
| PBKDF2 600.000 vòng, một lần băm | ~2,0–2,3 giây | ~1,7–1,8 giây |
| Import 5.000 thiết bị | ~0,75 giây | ~0,9 giây |
| Đọc 5.000 thiết bị | ~65 ms | ~75 ms |
| Tìm kiếm trong 5.000 thiết bị | 5–7 ms | 7–8 ms |

| Thông lượng kênh E2EE (khối 256 KB, loopback) | ~12 MB/s | ~16 MB/s |
| Thông lượng Relay, chưa tính E2EE (khối 256 KB, loopback) | ~77 MB/s | ~76 MB/s |

Thông lượng này đo riêng kênh mã hóa bằng AES-GCM của BouncyCastle (mã managed). Đủ cho ảnh JPEG thông thường, nhưng có thể là nút thắt ở chế độ "Không mất" (PNG) hoặc màn hình độ phân giải cao. Cần đo thực tế (mục 3b) trước khi quyết định có đổi sang AES của CNG hay không.

Phần chuyển tiếp nhanh hơn kênh E2EE nhiều lần, nên trên một phiên, E2EE mới là nút thắt, không phải Relay.

**Chưa đo:** FPS thực tế; băng thông trên mạng thật; Relay chịu tải nhiều kết nối. `MaxConnections` mặc định 10.000 là giới hạn cấu hình, chưa phải năng lực đã kiểm chứng.

## 5. Kiểm thử bảo mật (Phần 8.9)

| Hạng mục | Kết quả | Nội dung đã kiểm |
|---|---|---|
| SQL Injection | **Đạt** | Controller: ô tìm kiếm, trường lưu trữ. Relay: Remote ID độc hại |
| Brute Force | **Đạt** | Controller khóa sau 5 lần sai. Client khóa 15 phút, giữ nguyên khi khởi động lại. Relay chặn IP, kể cả token đúng |
| Unauthorized Access | **Đạt** | Gọi thẳng Service bằng vai trò thấp; Controller sai token; thiết bị thiếu mã đăng ký |
| Database Theft | **Đạt** | File DB, WAL và file trạng thái Client không có secret dạng rõ. Máy khác không mở được khóa (**giả lập**) |
| MITM (Controller/Client ↔ Relay) | **Đạt** | Chứng thư không tin cậy, sai khóa ghim đều bị từ chối |
| MITM trên kênh E2EE | **Đạt** | Proxy tráo khóa tạm bị chữ ký phát hiện. Kẻ giả mạo có khóa hợp lệ bị khóa ghim chặn, **trước khi** bằng chứng mật khẩu được gửi |
| Relay không đọc được nội dung | **Đạt** | Quét toàn bộ dữ liệu qua proxy: không có tên người dùng, mật khẩu, chữ gõ, ảnh JPEG dạng rõ |
| Certificate Validation | **Đạt một phần** | Tin cậy, khóa ghim, sai hostname dù đúng ghim. **Chưa kiểm:** chứng thư hết hạn, thu hồi, chuỗi CA thật |
| Replay Attack | **Đạt** | Phát lại chữ ký đăng ký; bằng chứng mật khẩu gắn transcript; phát lại gói lệnh trong phiên làm phiên bị đóng và lệnh không chạy hai lần |
| Sửa, xóa, chèn gói trong phiên (bổ sung) | **Đạt** | Lật bit, xóa một gói, chèn khung không mã hóa: phiên bị đóng, lệnh không thực thi |
| Đổi khóa phiên (bổ sung) | **Đạt** | Đổi khóa trong suốt khi đang truyền |
| Công tắc khẩn cấp (bổ sung) | **Đạt** | Chủ máy ngắt thì Controller không tự kết nối lại; Unattended đúng mật khẩu cũng bị chặn 60 giây |
| Ghim khóa Controller (bổ sung) | **Đạt** | Operator không bỏ ghim được; khóa đổi bị Audit mức Critical. **Chưa kiểm thử qua giao diện:** cảnh báo khi khóa đã ghim thay đổi |
| Chiếm Remote ID (bổ sung) | **Đạt** | Khóa khác bị từ chối, cả sau khi Relay khởi động lại |
| TLS cũ (bổ sung) | **Đạt** | Server từ chối TLS 1.1 |
| Khung tin độc hại (bổ sung) | **Đạt** | Khung khổng lồ trước xác thực bị đóng; 3.000 mẫu rác chỉ gây lỗi giao thức |
| Log không chứa bí mật | **Đạt** | Log Relay không có token, kể cả token sai |
| File Tampering (bản mã) | **Đạt** | Lật từng bit, chép bản mã sang dòng khác, file trạng thái Client bị sửa |
| Key Rotation | **Đạt** | Đọc lại đúng, khóa thu hồi bị từ chối, rollback khi lỗi |
| Backup/Restore Security | **Đạt** | Sai mật khẩu, lật bit, file rác; phục hồi sang máy khác (**giả lập**) |
| CSV/Formula Injection (bổ sung) | **Đạt** | Ô công thức được vô hiệu hóa khi Export |
| Test vector chuẩn (bổ sung) | **Đạt** | AES-256-GCM, PBKDF2-HMAC-SHA256, Ed25519 (RFC 8032) |
| Session Hijacking | **Đạt một phần** | Mọi gói đều cần khóa phiên riêng; vé dùng lại, bịa, hết hạn bị từ chối. Chưa có kịch bản kẻ tấn công lấy được vé còn hạn rồi vào trước máy thật (theo thiết kế chỉ gây lỗi kết nối, xem `04-Security.md`) |
| Từ chối dịch vụ qua Relay (bổ sung) | **Đạt một phần** | Giới hạn yêu cầu, số phiên, vé hết hạn, bên nhận không đọc. **Chưa kiểm:** thiết bị đã đăng ký dùng hết băng thông, nhiều thiết bị cùng dò Remote ID |
| Token Theft | **Chưa kiểm thử** | Thiết bị không dùng token. Token Controller chưa có cơ chế thu hồi |
| Path Traversal | **Chưa kiểm thử** | Thuộc Giai đoạn 6, chưa có mã |

Đây là kiểm thử do chính nhóm phát triển viết, chưa phải đánh giá bảo mật độc lập.

## 6. Tiến độ theo Phần 18

| Mục | Trạng thái | Mục | Trạng thái |
|---|---|---|---|
| Cài đặt, gỡ cài đặt | Chưa có mã | Khởi động | Đạt (Windows 10) |
| Đăng ký Remote ID | Đạt (loopback) | SQLite | Đạt (Windows 10) |
| Mã hóa dữ liệu | Đạt (lưu trữ) | Quản lý mật khẩu | Đạt |
| Online/Offline | Đạt (loopback) | Authentication | Đạt (Relay, cục bộ) |
| TLS | Đạt (tới Relay, loopback) | E2EE | Đạt (loopback, kể cả qua proxy tấn công) |
| Kết nối LAN, Internet | **Chưa kiểm thử trên mạng thật.** Relay đạt trên loopback (mục 3d) | Screen | Đạt một phần (mục 3b) |
| Mouse, Keyboard | **Chưa kiểm thử với input thật** (mục 3b) | | |
| File Transfer, Chat | Chưa có mã | Multi-Monitor | Chưa có mã |
| Multi-Session | Nhiều phiên qua một Relay: đạt trên loopback. Nhiều Controller vào một máy: chưa hỗ trợ | | |
| Reconnect | Đạt (tới Relay); phiên: chưa thử mất mạng thật | Backup/Restore, Audit Log | Đạt |

## 7. Lỗi kiểm thử đã phát hiện và sửa

| Lỗi | Phát hiện nhờ | Ghi chú |
|---|---|---|
| Nút tắt Remote Access của Client bị cắt khỏi khung | Xem ảnh chụp | Script vẫn bấm được nên test logic không thấy. Đã thêm test kiểm tra mọi nút nằm trọn trong khung |
| Build solution x86 làm RDM.Server không chạy được | Nghiệm thu x86 | `RDM.Shared` bị khóa x86; đã đổi sang IL trung tính |
| Bản x86 báo sai edition Windows | Xem ảnh chụp | Đọc nhầm `WOW6432Node`; đã thêm test |
| `verify.ps1` trả exit 0 khi có test lỗi | Đọc kết quả | Đã sửa |
| **Chủ máy bấm Ngắt ngay nhưng Controller tự kết nối lại;** với Unattended thì công tắc khẩn cấp vô tác dụng | Rà soát thiết kế | Đã sửa ở hai đầu, có test |
| **Tràn số nguyên khi kiểm tra biên vùng ảnh:** máy đích độc hại có thể vượt kiểm tra | Test với giá trị biên | Đã sửa |
| Hộp thoại fingerprint chạy trên luồng nền, sẽ lỗi ở lần kết nối đầu | Rà soát mã | Đã sửa, kịch bản ba tiến trình đi qua đường này |
| Kẻ không có mật khẩu có thể spam hộp thoại xin phép lên màn hình chủ máy | Rà soát thiết kế | Đã thêm thời gian chờ 10 giây, có test |
| Active Sessions vẫn liệt kê phiên đã bị chủ máy ngắt | Xem ảnh chụp | Đã sửa |
| **Bộ cài Client chỉ chứa file exe, không chạy được,** dù build báo thành công | `verify-installer.ps1` | Đã sửa; build giờ dừng nếu WiX báo thiếu file |
| SQLite của Relay có CVE-2025-6965 | Cảnh báo `NU1903` | Đã vá lên 3.53.3 |
| Tiếng Việt trong console Relay bị hỏng | Đọc log | Đã sửa |
| Test Relay thỉnh thoảng lỗi do tranh cổng | Đọc log Server | Đã sửa bộ test; Server không có lỗi |
| **Bộ cài Server từ chối cài trên mọi hệ điều hành:** điều kiện đòi `VersionNT >= 1000`, nhưng Windows Installer báo 603 ngay trên Windows 10 | Chủ dự án chạy cài thật | Đã chuyển điều kiện sang Burn, nơi đọc đúng phiên bản; đối chứng bằng điều kiện ngược; `verify-installer` chặn tái diễn |
| Chữ tiếng Việt không dấu trên giao diện ("Xoay") mà scanner chữ có dấu không thấy | Xem ảnh chụp | Đã sửa, test chặn thêm từ không dấu đặc trưng |
| Test quét Database có thể đọc thiếu khi SQLite gộp WAL giữa chừng | Lỗi ngẫu nhiên ở Release x64 | Đã sửa: quét lại từ đầu, không bỏ qua |
| **Script kiểm thử lưu ảnh màn hình thật của người chạy** khi phiên có màn hình | Xem ảnh chụp | Đã xóa ảnh; mặc định không lưu nữa |
| Thiết bị vừa thêm trống IP/OS | Xem ảnh chụp | Đã sửa, có test ở tầng Service |
| **Treo khi dọn dẹp ngay sau khi xin vé:** kết quả chạy tiếp trên luồng nhận dữ liệu, nếu bên gọi chặn chờ dừng kết nối thì hai bên chờ nhau | Test treo hơn 10 phút, đặt mốc từng bước | Đã sửa; hộp thoại xin phép chủ máy và fingerprint có cùng kiểu mã nên cũng được sửa |
| **Bên nhận cố tình không đọc giữ chỗ phiên Relay mãi** vì ghi không có thời hạn | Rà soát mã | Đã sửa, test đã đối chứng |
| Một đầu chết đúng lúc nhận `RelayReady` làm đầu kia chờ mãi, giữ kết nối | Rà soát mã | Đã sửa. **Chưa có test tái hiện đúng thời điểm này** |
| Tìm máy LAN báo lỗi socket chưa bind khi mọi lần gửi đều lỗi | Lỗi ngẫu nhiên ở Debug x64 | Đã sửa, có test. Chưa xác định vì sao lần gửi trong lần chạy đó lỗi |
| Không kết nối được máy đã đổi cổng: nhập IP luôn dùng 7444 | Viết kịch bản e2e | Đã thêm dạng `địa chỉ:cổng`, có test |

## 8. Giới hạn đã biết

**Một chương trình kiểu TeamViewer (chủ dự án chọn, thay cho tách riêng ở Phần 3, 14, 15):**
- **Mọi máy cài đều có đủ code quản trị.** Khả năng đọc dữ liệu vẫn được chặn: phần quản lý phải đăng nhập, Database riêng theo tài khoản Windows.
- **Không có RDM login cho kết nối nhanh:** ai dùng được tài khoản Windows đó đều kết nối được ra máy khác, nhưng vẫn phải biết mật khẩu máy đích hoặc được chủ máy chấp nhận. Không đọc được mật khẩu đã lưu. Audit ghi `windows:<tên>`.
- **Mỗi máy mở cổng TCP 7444 và UDP 7445 khi RDM chạy.** Tắt Remote Access thì đóng cả hai.
- **Tìm máy qua UDP broadcast chưa thử trên mạng thật**, chỉ thử trên loopback. Không qua được router; khi đó dùng Relay hoặc nhập IP.

**Giai đoạn 5:**
- **Không có TCP hole punching** (mục 3d). Hai máy sau hai router đi qua Relay, nên băng thông Relay là chi phí thật.
- **Thử trực tiếp mất tối đa 2 giây cho mỗi địa chỉ** trước khi chuyển sang Relay, nên kết nối qua Relay chậm hơn vài giây.
- **Relay biết ai kết nối tới ai, lúc nào, bao nhiêu dữ liệu**, và ghi Remote ID vào log. Không biết nội dung.
- **Relay để trống mã đăng ký thì ai cũng dùng được băng thông của nó.** Chưa có hạn mức băng thông theo thiết bị. Tài liệu triển khai yêu cầu đặt mã khi mở ra Internet.
- **Máy đích phải có cấu hình Relay trên cả hai đầu** để chuyển tiếp. Bên điều khiển chưa cấu hình Relay thì chỉ có đường trực tiếp.
- **Chưa có IPv6** cho đường chuyển tiếp ở mức đã kiểm thử; chỉ thử IPv4 loopback.

**Giai đoạn 4:**
- **Kết nối trực tiếp chưa thử giữa hai máy thật.** Bộ cài tự mở cổng trên mạng Domain và Private.
- **Kênh P2P dùng E2EE, không bọc thêm TLS.** Lệch câu chữ Phần 8.1 ("TLS 1.2 trở lên" cho cả P2P). Lý do: TLS tự ký không có gốc tin cậy nên không thêm bảo đảm; E2EE đã mã hóa và xác thực hai đầu bằng khóa ghim. Giữ nguyên theo khuyến nghị; chủ dự án không có ý kiến khác (05/11/2026).
- **Windows Service chưa từng chạy.** Khi không có Service: đóng cửa sổ Client hoặc đăng xuất Windows là Offline; không có Ctrl+Alt+Del.
- **Không điều khiển được Secure Desktop** (UAC, màn hình đăng nhập, màn hình khóa), kể cả khi có Service: Agent chạy bằng token người dùng. Controller báo rõ thay vì màn hình đen. Không tạo Agent quyền SYSTEM vì rủi ro cao. Chưa làm, theo khuyến nghị; chủ dự án không có ý kiến khác (05/11/2026).
- **Ứng dụng chạy quyền Administrator trên máy đích không nhận được input** từ Agent quyền thường (UIPI).
- **Ctrl+Alt+Del qua Special Keys chỉ có khi có Service.**
- **Mỗi Client một phiên.** Nhiều Controller cùng lúc chưa hỗ trợ.
- **Lần kết nối đầu dựa vào việc người dùng đối chiếu fingerprint.** Bỏ qua bước đó (tắt StrictPinning rồi bấm bừa) thì Relay độc hại có thể chen vào và dò mật khẩu offline. Mật khẩu yếu làm rủi ro này tăng.
- **Giải nén ảnh dùng GDI+ với dữ liệu từ máy đích.** Chỉ sau khi đã xác thực khóa ghim, nhưng máy đích bị chiếm có thể khai thác lỗi của GDI+ trên Controller.
- **DPI:** app khai báo System DPI Aware; chưa kiểm thử màn hình có DPI khác nhau.
- **Reconnect:** chưa thử với mất mạng thật; chỉ có test Client ngắt có chủ ý.
- **H.264:** chưa có, dùng JPEG và PNG.

**Giai đoạn 3:**
- **Relay ghi log ra console**, chưa có Audit Log bền vững, thu hồi thiết bị hay Server Manager. Token Controller chưa có cơ chế thu hồi ngoài việc đổi cấu hình.
- **Chặn Brute Force theo IP:** nhiều máy chung một IP NAT sẽ cùng bị chặn nếu một máy thử sai nhiều lần.
- **Thông tin thiết bị (tên, IP, OS) do thiết bị tự khai**, chỉ để hiển thị. Tên do quản trị viên đặt không bị ghi đè.

**Giai đoạn 1–2:**
- **Xoay khóa chỉ làm thủ công.** "Thay đổi khóa định kỳ theo chính sách" (Phần 8.4) chưa tự động.
- **Metadata không mã hóa:** tên máy, IP, Remote ID, lịch sử, tên người dùng trong `AuditLogs` và `ConnectionHistory` vẫn đọc được nếu file DB bị đánh cắp.
- **Thu hồi khóa không vô hiệu hóa backup cũ**, vì backup mang theo bản sao khóa của nó. Hộp thoại có cảnh báo.
- **Xóa khóa khỏi bộ nhớ** chỉ ở mức "khi có thể" do GC của .NET có thể đã sao chép mảng.
- **Thanh tiêu đề** vẫn sáng khi ở Dark Mode.
- **Bản x86 có kèm cả `x64\SQLite.Interop.dll`** (và ngược lại). Vô hại khi chạy; bộ cài ở Giai đoạn 7 sẽ loại bỏ.
- **Chưa có kiểm thử trên máy sạch**, nên chưa biết `SQLite.Interop.dll` có cần cài thêm VC++ runtime không.
- **Test UI từng treo** khi dùng `Application.DoEvents` lồng nhau để chuyển trang (kẹt trong `DestroyWindow`). Chạy bằng message loop thật thì không treo. Nguyên nhân gốc chưa được xác định tuyệt đối; cần để ý nếu Giai đoạn 4 dùng vòng lặp message lồng nhau.
