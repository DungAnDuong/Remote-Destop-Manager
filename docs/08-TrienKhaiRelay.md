# 08 – Triển khai Relay Server (phần Registry và Online/Offline)

Viết cho quản trị viên hệ thống. Relay đăng ký thiết bị, báo Online/Offline và chuyển tiếp phiên giữa hai máy không thấy nhau trực tiếp. Relay chỉ thấy bản mã, không đọc được phiên. Hướng dẫn đầy đủ gửi khách hàng ở `RELAY-DEPLOYMENT.md`.

## 1. Yêu cầu

- Windows Server hoặc Windows x64 có .NET 10 ASP.NET Core Runtime. Chạy trên Linux chưa được kiểm thử.
- Mở cổng TCP cho thiết bị và Controller, mặc định 7443.

## 2. Chứng thư TLS

Relay không chạy nếu thiếu chứng thư. Đặt mật khẩu PFX trong biến môi trường `RDM_CERT_PASSWORD`, không ghi vào file cấu hình.

**Có tên miền và chứng thư từ CA:** dùng file PFX đó; Client và Controller để trống khóa ghim. Khuyến nghị ECDSA P-256 nếu có máy Windows 7.

**Mạng nội bộ:** sinh chứng thư tự ký. Hostname phải đúng tên mà Client và Controller dùng để kết nối.

```powershell
$env:RDM_CERT_PASSWORD = '<mật khẩu>'
.\RDM.Server.exe --generate-cert C:\RDM\relay.pfx relay.congty.local 825
```

Lệnh in ra **khóa ghim** 64 ký tự. Nhập khóa này vào Settings của mọi Client và Controller. Khi đổi chứng thư bằng khóa mới, phải cập nhật lại khóa ghim ở mọi nơi.

## 3. Token

Sinh chuỗi ngẫu nhiên dài cho token Controller và mã đăng ký thiết bị, rồi băm. Lệnh đọc từ stdin để token không nằm trong lịch sử dòng lệnh.

```powershell
.\RDM.Server.exe --hash-token   # nhập token, nhấn Enter, nhận giá trị băm
```

Ghi giá trị băm vào `appsettings.json`; bản rõ chỉ nhập ở Controller hoặc Client.

| Khóa cấu hình | Ý nghĩa |
|---|---|
| `Relay:CertificatePath` | Đường dẫn file PFX |
| `Relay:ControllerTokenSha256` | Để trống thì mọi Controller bị từ chối |
| `Relay:EnrollmentTokenSha256` | Để trống thì **ai cũng đăng ký được thiết bị mới và dùng băng thông Relay**. **Phải đặt khi mở ra Internet.** Thiết bị đã đăng ký xin được phiên tới mọi Remote ID, sau đó vẫn phải có mật khẩu hoặc chủ máy chấp nhận |
| `Relay:MaxAuthFailures`, `Relay:AuthBlockMinutes` | Chặn IP sau số lần xác thực sai, mặc định 20 lần, 15 phút. Nhiều máy chung một IP NAT sẽ cùng bị chặn |
| `Relay:MaxConnections` | Giới hạn kết nối đồng thời |
| `Relay:MaxRelaySessions` | Giới hạn phiên chuyển tiếp đồng thời, mặc định 1000. Là giới hạn cấu hình, chưa phải năng lực đã kiểm chứng |
| `Relay:TicketSeconds`, `Relay:RelayIdleSeconds`, `Relay:SessionRequestsPerMinute` | Hạn vé 30 giây; đóng phiên sau 120 giây không có dữ liệu; 30 yêu cầu mỗi phút cho mỗi thiết bị |

## 4. Cấu hình Client và Controller

- **Mỗi máy RDM:** nút Settings ở cửa sổ chính → Relay Server, Port, Khóa ghim, Mã đăng ký.
- **Máy quản trị, thêm:** Quản lý thiết bị → Settings → Host, Port, Token Controller, Khóa ghim. Tùy chọn tự động thêm thiết bị mới.

## 5. Kiểm tra

`GET http://127.0.0.1:7080/health` trả về số thiết bị Online và số Controller; endpoint này chỉ lắng nghe trên loopback. Không mở nó ra Internet.

## 6. Sao lưu

Sao lưu file `%ProgramData%\RDM\Relay\rdm-relay.db` (đổi được bằng `Relay:DatabasePath`). Gỡ cài đặt không xóa file này. Mất file này thì registry bị trống: Remote ID không còn gắn với khóa, và bất kỳ ai cũng có thể đăng ký trước chủ cũ nếu không đặt mã đăng ký.

## 7. Trực tiếp và chuyển tiếp

RDM thử trực tiếp trước qua địa chỉ Relay cung cấp, mỗi địa chỉ chờ 2 giây, cổng mặc định 7444 (đổi được trong Settings). Bộ cài RDM tự mở tường lửa trên mạng Domain và Private. Không được thì hai máy cùng kết nối ra Relay bằng vé dùng một lần. Không có TCP hole punching nên hai máy sau hai router thường đi qua Relay. Nhập IP ở cửa sổ chính thì luôn đi trực tiếp, dạng `địa chỉ:cổng` nếu cổng khác mặc định. Địa chỉ do Relay cung cấp không được tin: danh tính máy được xác minh bằng khóa ghim.

## 8. Chưa có

TCP hole punching, thu hồi thiết bị từ giao diện, Server Manager, Firewall Policy, Audit Log bền vững của Relay (hiện chỉ ghi ra console).

Chạy Relay như dịch vụ Windows đã có mã (`UseWindowsService`) nhưng **chưa kiểm thử**; các bước ở `RELAY-DEPLOYMENT.md`.
